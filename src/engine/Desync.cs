// Desync stratejileri. Her ornek tek bir is parcacigina ait.
// Tamponlar acilista unmanaged bellekten alinir: paket yolunda tahsis yok.
using System;
using System.Runtime.InteropServices;

namespace Etodpx.Engine
{
    internal sealed unsafe class DesyncWorker : IDisposable
    {
        private const int BufSize = 0x10000 + 64;
        // 1200 = QUIC Initial alt siniri, 1280 = IPv6 asgari MTU. Arasi.
        private const int QuicFakeSize = 1250;
        // Devam segmentinin kopyasi genelde zaten hazir; bu yalnizca ust sinir.
        // Asilirsa paket eskisi gibi dokunulmadan gider.
        internal const int ContinuationWaitMs = 30;

        private readonly Config _cfg;
        private readonly Stats _stats;
        private readonly IntPtr _handle;
        private volatile Hostlist _hosts;      // null = butun adlara dokun
        private readonly ContinuationTable _continuations;   // null = parcali ClientHello birlestirilmez

        private readonly IntPtr _recvMem, _workMem, _fakeTlsMem, _fakeHttpMem, _quicMem, _asmMem;
        private readonly byte* _recv;
        private readonly byte* _work;
        private readonly byte* _asm;           // birlestirilmis ClientHello
        private readonly byte* _fakeTlsP;
        private readonly byte* _fakeHttpP;
        private readonly byte* _quicP;
        private readonly int _fakeTlsLen, _fakeHttpLen, _quicLen;

        private ushort _ipId;
        private uint _rngState;

        private readonly bool _tlsFake, _tlsSplit, _tlsDisorder;
        private readonly bool _httpFake, _httpSplit, _httpDisorder;
        private readonly int _fixedSplitPos;      // >0 ise SplitPos sayisal verilmis
        private readonly bool _splitAtHostStart;

        // hosts = null: liste kullanilmaz, butun adlara dokunulur. Oz test
        // eslesme disi davranisi bu sekilde siniyor.
        // continuations = null: SNI'si ikinci segmente tasan ClientHello'ya
        // eskisi gibi yalnizca ilk segmente bakilarak karar verilir.
        public DesyncWorker(Config cfg, Stats stats, IntPtr handle, int seed, Hostlist hosts = null,
                            ContinuationTable continuations = null)
        {
            _cfg = cfg;
            _stats = stats;
            _handle = handle;
            _hosts = hosts;
            _continuations = continuations;

            _recvMem = Marshal.AllocHGlobal(BufSize);
            _workMem = Marshal.AllocHGlobal(BufSize);
            _recv = (byte*)_recvMem;
            _work = (byte*)_workMem;
            if (continuations != null)
            {
                _asmMem = Marshal.AllocHGlobal(BufSize);
                _asm = (byte*)_asmMem;
            }

            byte[] tls = Fakes.BuildTlsClientHello(cfg.FakeSni);
            byte[] http = Fakes.BuildHttpRequest(cfg.FakeSni);
            _fakeTlsLen = tls.Length;
            _fakeHttpLen = http.Length;
            _fakeTlsMem = Marshal.AllocHGlobal(_fakeTlsLen);
            _fakeHttpMem = Marshal.AllocHGlobal(_fakeHttpLen);
            Marshal.Copy(tls, 0, _fakeTlsMem, _fakeTlsLen);
            Marshal.Copy(http, 0, _fakeHttpMem, _fakeHttpLen);
            _fakeTlsP = (byte*)_fakeTlsMem;
            _fakeHttpP = (byte*)_fakeHttpMem;

            byte[] quic = Fakes.BuildQuicInitial(QuicFakeSize);
            _quicLen = quic.Length;
            _quicMem = Marshal.AllocHGlobal(QuicFakeSize);
            Marshal.Copy(quic, 0, _quicMem, _quicLen);
            _quicP = (byte*)_quicMem;

            ParseMode(cfg.TlsMode, out _tlsFake, out _tlsSplit, out _tlsDisorder);
            ParseMode(cfg.HttpMode, out _httpFake, out _httpSplit, out _httpDisorder);

            _splitAtHostStart = cfg.SplitPos == "host";
            int fixedPos;
            _fixedSplitPos = int.TryParse(cfg.SplitPos, out fixedPos) && fixedPos > 0 ? fixedPos : 0;

            _ipId = (ushort)(Environment.TickCount + seed * 4099);
            _rngState = (uint)(Environment.TickCount ^ (seed * 0x9E3779B9));
            if (_rngState == 0) _rngState = 0x1234567;
        }

        // Canli hostlist yenilemesi: yeni liste hazir oldugunda atomik olarak degistirilir.
        public void SetHostlist(Hostlist hosts)
        {
            _hosts = hosts;
        }

        private static void ParseMode(string m, out bool fake, out bool split, out bool disorder)
        {
            m = (m ?? string.Empty).ToLowerInvariant();
            if (m == "off" || m == "none" || m.Length == 0)
            {
                fake = false; split = false; disorder = false;
                return;
            }
            fake = m.Contains("fake");
            disorder = m.Contains("disorder");
            split = disorder || m.Contains("split");
        }

        public byte* RecvBuffer { get { return _recv; } }
        public int RecvCapacity { get { return BufSize; } }

        // "16 03 .. .. .. 01" deseni tek basina yetmiyor: sifreli akis ortasindaki
        // rastgele veri 16 milyonda bir uyuyor ve buyuk bir indirmede bu birkac kez
        // oluyor. Uzunluk alanlarinin tutarliligi da araniyor.
        private static bool IsClientHello(byte* p, int len)
        {
            if (len < 9) return false;
            if (p[0] != 0x16 || p[1] != 0x03) return false;   // handshake kaydi
            if (p[2] > 0x04) return false;                    // kayit surumu makul mu
            if (p[5] != 0x01) return false;                   // ClientHello

            int recLen = (p[3] << 8) | p[4];
            if (recLen < 4 || recLen > 0x4000) return false;   // TLS kaydi en fazla 16 KB

            int hsLen = (p[6] << 16) | (p[7] << 8) | p[8];
            // Govde kaydin icine tam oturmali. Paket segmentlere bolunmus olsa da gecerli.
            return hsLen == recLen - 4;
        }

        // Akis ortasindaki govde verisini istekle karistirmamak icin.
        private static bool LooksLikeHttpRequest(byte* p, int len)
        {
            if (len < 16) return false;
            uint w = Packet.Read32(p);
            switch (w)
            {
                case 0x47455420:   // "GET "
                case 0x504f5354:   // "POST"
                case 0x48454144:   // "HEAD"
                case 0x50555420:   // "PUT "
                case 0x4f505449:   // "OPTI"
                case 0x50415443:   // "PATC"
                case 0x44454c45:   // "DELE"
                case 0x434f4e4e:   // "CONN"
                case 0x54524143:   // "TRAC"
                    break;
                default:
                    return false;
            }

            // Govde parcasi da "POST" ile baslayabilir; istek satiri " HTTP/1.x"
            // ile biter, onu ariyoruz.
            int scan = len < 2048 ? len : 2048;
            for (int i = 1; i + 1 < scan; i++)
            {
                if (p[i] != 0x0D || p[i + 1] != 0x0A) continue;   // CRLF
                if (i < 9) return false;                          // satir cok kisa
                byte* t = p + i - 9;
                return t[0] == (byte)' ' && t[1] == (byte)'H' && t[2] == (byte)'T'
                    && t[3] == (byte)'T' && t[4] == (byte)'P' && t[5] == (byte)'/'
                    && t[6] == (byte)'1' && t[7] == (byte)'.'
                    && (t[8] == (byte)'0' || t[8] == (byte)'1');
            }
            return false;      // istek satiri bulunamadi
        }

        // xorshift32 - sahte paketlerin tekil gorunmesi icin yeterli, kriptografik degil
        private uint NextRand()
        {
            uint x = _rngState;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            _rngState = x;
            return x;
        }

        // --- TCP

        // true  -> paketi biz yeniden urettik, orijinali gonderme.
        // false -> dokunmadik, oldugu gibi geri enjekte et.
        public bool HandleTcp(int len, ref DivertAddress addr)
        {
            PacketView v;
            if (!Packet.Parse(_recv, len, out v)) return false;
            if (v.Protocol != Packet.ProtoTcp || v.PayloadLength <= 0) return false;
            if (v.IsV6 && !_cfg.EnableIPv6) return false;

            // TCP Fast Open'da SYN yuk tasir; onu bolmek iki ayri SYN uretir ve
            // baglantiyi bozar. SYN/RST tasiyan paketlere hic dokunmuyoruz.
            byte f0 = Packet.TcpFlags(_recv, v.L4Offset);
            if ((f0 & (Packet.TcpSyn | Packet.TcpRst)) != 0) return false;

            byte* pl = v.Payload;
            int plLen = v.PayloadLength;

            bool isTls = IsClientHello(pl, plLen);
            bool isHttp = false;

            bool wantFake, wantSplit, wantDisorder;
            byte* fakePayload;
            int fakeLen;

            if (isTls)
            {
                if (!_cfg.EnableTls) return false;
                wantFake = _tlsFake; wantSplit = _tlsSplit; wantDisorder = _tlsDisorder;
                fakePayload = _fakeTlsP; fakeLen = _fakeTlsLen;
            }
            else if (LooksLikeHttpRequest(pl, plLen))
            {
                if (!_cfg.EnableHttp) return false;
                isHttp = true;
                wantFake = _httpFake; wantSplit = _httpSplit; wantDisorder = _httpDisorder;
                fakePayload = _fakeHttpP; fakeLen = _fakeHttpLen;
            }
            else
            {
                // Akis ortasindaki veri; bolmenin faydasi yok, riski var. Cekirdek
                // filtresi bunlari zaten eliyor - burasi genis moda dusulunce devrede.
                return false;
            }

            Hostlist hosts = _hosts;
            if (!wantFake && !wantSplit && hosts == null) return false;

            // Alan adi bu segmentte yoksa ClientHello sonraki segmente tasmistir
            // (Wi-Fi gibi LSO'suz kartlarda olagan). Karar birlesik mesaj uzerinden
            // veriliyor; gonderilen veri yine yalnizca bu segment.
            byte* msg = pl;
            int msgLen = plLen;
            bool needHost = hosts != null || (wantSplit && _fixedSplitPos == 0);
            if (isTls && needHost && _continuations != null && NeedsContinuation(pl, plLen))
            {
                int more = _continuations.CopyNext(_recv, ref v, _asm + plLen, BufSize - plLen,
                                                   ContinuationWaitMs);
                if (more > 0)
                {
                    Packet.Copy(_asm, pl, plLen);
                    msg = _asm;
                    msgLen = plLen + more;
                }
            }

            bool splitAtHostStart = _splitAtHostStart;
            int fixedSplitPos = _fixedSplitPos;
            bool fakeBadSum = _cfg.FakeBadSum;
            bool fakeWrongSeq = _cfg.FakeWrongSeq;
            bool fakeTtl = _cfg.FakeTtl;
            int fakeTtlValue = _cfg.EffectiveFakeTtl(v.IsV6);

            // Liste varsa listede olmayan hicbir ada dokunmuyoruz. Engelli
            // olmayan bir sunucuya giden paketi bolmenin faydasi yok; DPI onu
            // tuhaf bulup dusurunce baglanti TCP zaman asimi merdivenini
            // tirmaniyor ve uygulama saniyelerce donuyor.
            if (hosts != null)
            {
                HostRule rule;
                if (!MatchHostlist(hosts, msg, msgLen, isTls, out rule)) return false;

                if (rule.HasOverride)
                {
                    wantFake = (rule.Flags & HostFlags.Fake) != 0;
                    wantSplit = (rule.Flags & HostFlags.Split) != 0;
                    wantDisorder = (rule.Flags & HostFlags.Disorder) != 0;

                    bool anyFakeOverride = (rule.Flags & (HostFlags.BadSum | HostFlags.WrongSeq | HostFlags.Ttl)) != 0;
                    if (anyFakeOverride)
                    {
                        fakeBadSum = (rule.Flags & HostFlags.BadSum) != 0;
                        fakeWrongSeq = (rule.Flags & HostFlags.WrongSeq) != 0;
                        fakeTtl = (rule.Flags & HostFlags.Ttl) != 0;
                        if (fakeTtl && rule.TtlValue > 0) fakeTtlValue = rule.TtlValue;
                    }

                    if (rule.FixedSplitPos > 0)
                    {
                        fixedSplitPos = rule.FixedSplitPos;
                        splitAtHostStart = false;
                    }
                    else
                    {
                        fixedSplitPos = 0;
                        splitAtHostStart = (rule.Flags & HostFlags.SplitMidSld) == 0;
                    }
                }
            }

            if (!wantFake && !wantSplit) return false;

            // bolme konumunu sec
            int splitPos = -1;
            if (wantSplit)
            {
                bool hostFound;
                splitPos = ChooseSplitPos(msg, msgLen, isTls, splitAtHostStart, fixedSplitPos, out hostFound);
                if (hostFound) _stats.BumpHost();
                splitPos = FitSplitToSegment(splitPos, plLen, isTls, fixedSplitPos);
            }

            // Gonderecek bir sey yoksa tampona dokunmadan cik: WorkerLoop paketi
            // checksum'i yeniden hesaplamadan enjekte ediyor.
            if (splitPos <= 0 && !wantFake) return false;

            // Yuku degistirmek artik guvenli, ama gonderim tutmazsa geri almaliyiz.
            int hostCaseAt = -1;
            uint hostCaseSaved = 0;
            if (isHttp && _cfg.HttpHostCase)
                hostCaseAt = Hostname.RewriteHttpHostCase(pl, plLen, out hostCaseSaved);

            int hdrLen = v.PayloadOffset;                       // IP + TCP baslik
            uint seq = Packet.TcpSeq(_recv, v.L4Offset);
            byte flags = Packet.TcpFlags(_recv, v.L4Offset);

            // 1) sahte paket(ler): DPI bunlari gorur, sunucu gormez
            if (wantFake)
            {
                bool anyFakeSent = false;
                for (int r = 0; r < _cfg.FakeCount; r++)
                {
                    // Bozuk checksum: DPI genelde dogrulamaz, sunucunun TCP yigini atar.
                    if (fakeBadSum)
                        anyFakeSent |= SendTcp(ref v, hdrLen, fakePayload, fakeLen, seq, flags, ref addr, true, 0);

                    // Pencere disi seq: checksum dogrulayan DPI icin. Sunucu eski veri sayip atar.
                    if (fakeWrongSeq)
                        anyFakeSent |= SendTcp(ref v, hdrLen, fakePayload, fakeLen,
                                unchecked(seq - (uint)_cfg.FakeWrongSeqDelta), flags, ref addr, false, 0);

                    // Dusuk TTL: DPI'i gecer, sunucuya varmadan oler. Deger fazla
                    // gelirse sahte paket sunucuya ulasip baglantiyi bozar.
                    if (fakeTtl)
                        anyFakeSent |= SendTcp(ref v, hdrLen, fakePayload, fakeLen, seq, flags, ref addr, false, (byte)fakeTtlValue);
                }
                if (anyFakeSent) _stats.BumpFake();
            }

            // 2) gercek veri
            bool realSent;
            if (splitPos > 0)
            {
                byte flagsFirst = (byte)(flags & ~Packet.TcpPsh & ~Packet.TcpFin);
                int len1 = splitPos;
                int len2 = plLen - splitPos;

                if (wantDisorder)
                {
                    // ikinci parca once gider: DPI'in yeniden birlestirmesini bozar,
                    // sunucunun TCP yigini ise sorunsuz sirayla toparlar
                    bool sent2 = SendTcp(ref v, hdrLen, pl + splitPos, len2, seq + (uint)splitPos, flags, ref addr, false, 0);
                    bool sent1 = SendTcp(ref v, hdrLen, pl, len1, seq, flagsFirst, ref addr, false, 0);
                    realSent = sent1 & sent2;
                }
                else
                {
                    bool sent1 = SendTcp(ref v, hdrLen, pl, len1, seq, flagsFirst, ref addr, false, 0);
                    bool sent2 = SendTcp(ref v, hdrLen, pl + splitPos, len2, seq + (uint)splitPos, flags, ref addr, false, 0);
                    realSent = sent1 & sent2;
                }
                if (realSent) _stats.BumpSplit();
            }
            else
            {
                realSent = SendTcp(ref v, hdrLen, pl, plLen, seq, flags, ref addr, false, 0);
            }

            // Segmentlerden biri bile gitmediyse orijinali yutmuyoruz. Ama once
            // yuku eski haline dondur: eski checksum ile degismis yuk sunucuda duser.
            if (!realSent)
            {
                Hostname.RestoreHttpHostCase(pl, hostCaseAt, hostCaseSaved);
                return false;
            }

            _stats.BumpConnection();
            return true;
        }

        // TLS kaydi elimizdeki yukten uzunsa ClientHello sonraki segmente tasiyor.
        // SNI bu kisimda bulunamadiysa devami olmadan karar verilemez.
        internal static bool NeedsContinuation(byte* pl, int plLen)
        {
            if (plLen < 9) return false;
            int recLen = (pl[3] << 8) | pl[4];
            if (recLen + 5 <= plLen) return false;
            return !Hostname.FindSni(pl, plLen).Found;
        }

        // Yukteki alan adi listede mi. Ad okunamiyorsa dokunmuyoruz: ne
        // oldugunu bilmedigimiz bir baglantiyi bozma riskini almaya degmez.
        private static bool MatchHostlist(Hostlist hosts, byte* pl, int plLen, bool isTls, out HostRule rule)
        {
            rule = default(HostRule);
            HostLocation h = isTls ? Hostname.FindSni(pl, plLen) : Hostname.FindHttpHost(pl, plLen);
            if (!h.Found) return false;
            return hosts.MatchRule(pl + h.Offset, h.Length, out rule);
        }

        // -1 donerse bolme yapilmaz.
        //
        // split_pos sayi verildiginde mutlak konumdur, alan adi aranmaz. Once alan
        // adina bakip bulununca sayiyi yok saymak "pos 2" ayarini sessizce midsld'e
        // cevirir; ayarlayicinin pos stratejileri de midsld'in kopyasi olur.
        internal static int ChooseSplitPos(byte* pl, int plLen, bool isTls,
                                           bool splitAtHostStart, int fixedSplitPos, out bool hostFound)
        {
            hostFound = false;
            int pos;

            if (fixedSplitPos > 0)
            {
                pos = fixedSplitPos;
            }
            else
            {
                HostLocation h = isTls ? Hostname.FindSni(pl, plLen) : Hostname.FindHttpHost(pl, plLen);
                if (h.Found)
                {
                    hostFound = true;
                    pos = splitAtHostStart ? h.Offset + 1 : Hostname.MidSld(pl, h.Offset, h.Length);
                }
                else pos = isTls ? 3 : 2;
            }

            return pos <= 0 || pos >= plLen ? -1 : pos;
        }

        // Konum birlesik mesaja gore hesaplaniyor ama bolme yalnizca elimizdeki
        // segmentin icinde yapilabilir; tasarsa ikinci parcanin uzunlugu eksiye
        // duser. Alan adi sonraki segmentteyse bulunamamis gibi varsayilan konuma
        // dusuluyor; sabit konum segmente sigmiyorsa eskisi gibi bolme yok.
        internal static int FitSplitToSegment(int pos, int segLen, bool isTls, int fixedSplitPos)
        {
            if (pos < segLen) return pos;
            if (fixedSplitPos > 0) return -1;
            int fallback = isTls ? 3 : 2;
            return fallback < segLen ? fallback : -1;
        }

        // --- QUIC

        // Giden QUIC Initial paketi. true -> orijinali gonderme.
        public bool HandleQuic(int len, ref DivertAddress addr)
        {
            string mode = _cfg.QuicMode;
            if (mode == "off" || mode == "none") return false;

            PacketView v;
            if (!Packet.Parse(_recv, len, out v)) return false;
            if (v.Protocol != Packet.ProtoUdp || v.PayloadLength <= 0) return false;
            if (v.IsV6 && !_cfg.EnableIPv6) return false;

            // Yalnizca Initial. Kisa baslikli veri paketlerine dokunmak hizi bozar.
            if (v.PayloadLength < 1200) return false;

            byte* q = v.Payload;
            // Surum alani (bayt 1-4). Bu olmadan UDP/443'ten gecen QUIC disi trafik
            // - Steam Datagram Relay gibi - QUIC sanilip araya sahte paket giriyor.
            uint version = Packet.Read32(q + 1);
            bool v1Initial = version == 0x00000001 && q[0] >= 0xc0 && q[0] <= 0xcf;
            bool v2Initial = version == 0x6b3343cf && q[0] >= 0xd0 && q[0] <= 0xdf;
            if (!v1Initial && !v2Initial) return false;

            // Her iki baglanti kimligi uzunlugu da QUIC'te en fazla 20 olabilir.
            int dcidLen = q[5];
            if (dcidLen > 20) return false;
            int scidLenOffset = 6 + dcidLen;
            if (scidLenOffset >= v.PayloadLength || q[scidLenOffset] > 20) return false;

            if (mode == "drop")
            {
                // Initial dusurulur: tarayici aninda TCP+TLS yoluna gecer, orayi zaten aciyoruz
                _stats.BumpQuic();
                _stats.BumpConnection();
                return true;
            }

            // "fake": once cozulemeyecek bir Initial (dusuk TTL), sonra gercegi
            PrepareQuicFake(version);
            RefreshQuicIds();
            int quicTtl = _cfg.EffectiveFakeTtl(v.IsV6);
            bool fakeSent = SendUdp(ref v, v.PayloadOffset, _quicP, _quicLen, ref addr, (byte)quicTtl);
            bool realSent = SendUdp(ref v, v.PayloadOffset, v.Payload, v.PayloadLength, ref addr, 0);

            if (!realSent) return false;       // WorkerLoop orijinal datagrami yollar

            if (fakeSent) _stats.BumpFake();
            _stats.BumpQuic();
            _stats.BumpConnection();
            return true;
        }

        // [0]tip [1-4]surum [5]dcid_len [6-13]DCID [14]scid_len [15-22]SCID
        // Uzunluk baytlarina dokunmuyoruz, yoksa paket bicimsiz kalir.
        private void RefreshQuicIds()
        {
            Packet.Write32(_quicP + 6, NextRand());
            Packet.Write32(_quicP + 10, NextRand());
            Packet.Write32(_quicP + 15, NextRand());
            Packet.Write32(_quicP + 19, NextRand());
        }

        private void PrepareQuicFake(uint version)
        {
            if (version == 0x6b3343cf)
            {
                _quicP[0] = 0xd3;             // QUIC v2 Initial, pn_len=4
                Packet.Write32(_quicP + 1, 0x6b3343cf);
            }
            else
            {
                _quicP[0] = 0xc3;             // QUIC v1 Initial, pn_len=4
                Packet.Write32(_quicP + 1, 0x00000001);
            }
        }

        // --- paket kurma

        // Orijinalin IP+TCP basligini kopyalayip yeni yuk ile paket kurar ve yollar.
        // badSum=true  -> TCP checksum kasitli bozulur: DPI paketi isler, sunucu atar.
        // ttl>0      -> TTL dusurulur: paket DPI'i gecer, sunucuya varmadan oler.
        private bool SendTcp(ref PacketView v, int hdrLen, byte* payload, int payloadLen,
                             uint seq, byte flags, ref DivertAddress addr, bool badSum, byte ttl)
        {
            int total = hdrLen + payloadLen;
            if (total > BufSize || payloadLen < 0)
            {
                // Tampon 64 KB, IP paketi en fazla 65535 - buraya dusmemeli. Yine de
                // sessizce vazgecmiyoruz: cagiran "gonderdim" sanip orijinali atlar.
                _stats.BumpSendError();
                return false;
            }

            Packet.Copy(_work, v.Base, hdrLen);
            if (payloadLen > 0) Packet.Copy(_work + hdrLen, payload, payloadLen);

            Packet.SetIpLength(_work, v.IsV6, total, v.IpHeaderLen);
            Packet.SetTcpSeq(_work, v.L4Offset, seq);
            Packet.SetTcpFlags(_work, v.L4Offset, flags);

            if (ttl > 0) Packet.SetTtl(_work, v.IsV6, ttl);
            if (!v.IsV6) Packet.SetIpId(_work, false, unchecked(_ipId++));

            return Emit(_work, total, v.L4Offset + 16, ref addr, badSum);
        }

        private bool SendUdp(ref PacketView v, int hdrLen, byte* payload, int payloadLen,
                             ref DivertAddress addr, byte ttl)
        {
            int total = hdrLen + payloadLen;
            if (total > BufSize || payloadLen < 0)
            {
                _stats.BumpSendError();
                return false;
            }

            Packet.Copy(_work, v.Base, hdrLen);
            if (payloadLen > 0) Packet.Copy(_work + hdrLen, payload, payloadLen);

            Packet.SetIpLength(_work, v.IsV6, total, v.IpHeaderLen);
            Packet.Write16(_work + v.L4Offset + 4, (ushort)(8 + payloadLen));   // UDP uzunlugu

            if (ttl > 0) Packet.SetTtl(_work, v.IsV6, ttl);
            if (!v.IsV6) Packet.SetIpId(_work, false, unchecked(_ipId++));

            return Emit(_work, total, 0, ref addr, false);
        }

        private bool Emit(byte* buf, int total, int checksumOffset, ref DivertAddress src, bool badSum)
        {
            DivertAddress a = src;                  // her gonderim kendi kopyasiyla calisir

            if (!WinDivert.WinDivertHelperCalcChecksums(buf, (uint)total, &a, (ulong)ChecksumFlags.All))
            {
                _stats.BumpSendError();
                return false;
            }

            if (badSum && checksumOffset > 0)
            {
                // Dogru checksum hesaplandi; simdi kesinlikle yanlis olacak sekilde ters cevir.
                byte* ck = buf + checksumOffset;
                ushort good = Packet.Read16(ck);
                Packet.Write16(ck, (ushort)(good ^ 0xFFFF));
                // "checksum gecerli" isaretliyoruz ki Windows yigini yeniden hesaplamasin.
                a.TcpChecksum = true;
                a.IpChecksum = true;
            }

            uint sent;
            if (!WinDivert.WinDivertSend(_handle, buf, (uint)total, &sent, &a) || sent != (uint)total)
            {
                _stats.BumpSendError();
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_recvMem);
            Marshal.FreeHGlobal(_workMem);
            Marshal.FreeHGlobal(_fakeTlsMem);
            Marshal.FreeHGlobal(_fakeHttpMem);
            Marshal.FreeHGlobal(_quicMem);
            if (_asmMem != IntPtr.Zero) Marshal.FreeHGlobal(_asmMem);
        }
    }
}
