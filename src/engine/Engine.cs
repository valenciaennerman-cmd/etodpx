// dpetoos - motor: filtre kurulumu, tutamaklar, is parcaciklari
//
// Tasarimin ozu: cekirdek filtresini olabildigince DAR tutmak.
// Yalnizca yeni bir baglantinin ILK paketi (TLS ClientHello / HTTP istek satiri /
// QUIC Initial) kullanici alanina gelir. Ping (ICMP), oyun trafigi, indirme,
// yukleme ve genel olarak akislarin geri kalani cekirdegi hic terk etmez.
// Bu yuzden gecikme ve hiz kaybi olculebilir duzeyde degildir.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Dpetoos.Engine
{
    internal sealed unsafe class DpiEngine : IDisposable
    {
        private const int ErrorFileNotFound = 2;
        private const int ErrorAccessDenied = 5;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorNoData = 232;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorInvalidImageHash = 577;
        private const int ErrorServiceDoesNotExist = 1060;
        private const int ErrorDriverBlocked = 1275;

        private readonly Config _cfg;
        private readonly Stats _stats = new Stats();

        private IntPtr _tcpHandle = WinDivert.InvalidHandle;
        private IntPtr _quicHandle = WinDivert.InvalidHandle;
        private readonly List<Thread> _threads = new List<Thread>();
        private volatile bool _running;
        private int _generation;
        private int _faultRaised;
        // Baslatma arka planda yapiliyor; kullanici o sirada pencereyi kaparsa
        // Stop() ile Start() ic ice girebilir. Kilit bunu tekile indirir.
        private readonly object _lifecycle = new object();

        public Stats Stats { get { return _stats; } }
        public bool IsRunning { get { return _running; } }
        public string ActiveTcpFilter { get; private set; }
        public string ActiveQuicFilter { get; private set; }
        public string QuicWarning { get; private set; }

        /// <summary>Beklenmedik bir hata olustugunda tetiklenir (arayuze mesaj tasir).</summary>
        public event Action<string> Fault;

        public DpiEngine(Config cfg) { _cfg = cfg; }

        // ============================================================ yasam dongusu

        public void Start()
        {
            lock (_lifecycle) { StartLocked(); }
        }

        private void StartLocked()
        {
            if (_running) return;

            _stats.Reset();
            ActiveTcpFilter = null;
            ActiveQuicFilter = null;
            QuicWarning = null;
            Interlocked.Exchange(ref _faultRaised, 0);

            int generation = unchecked(Volatile.Read(ref _generation) + 1);
            Volatile.Write(ref _generation, generation);

            string filter = PickFilter(BuildTcpFilterCandidates(), DivertLayer.Network);
            if (filter == null)
                throw new EngineException("TCP filtresi derlenemedi. WinDivert sürümü çok eski olabilir (2.2+ gerekir).");
            ActiveTcpFilter = filter;

            _tcpHandle = Open(filter);

            bool quicOn = _cfg.QuicMode != "off" && _cfg.QuicMode != "none";
            if (quicOn)
            {
                string qf = PickFilter(BuildQuicFilterCandidates(), DivertLayer.Network);
                if (qf != null)
                {
                    ActiveQuicFilter = qf;
                    try { _quicHandle = Open(qf); }
                    catch (EngineException ex)
                    {
                        _quicHandle = WinDivert.InvalidHandle;
                        QuicWarning = "QUIC başlatılamadı: " + ex.Message;
                    }
                }
                else QuicWarning = "QUIC filtresi sürücü tarafından kabul edilmedi.";
            }

            _running = true;
            _stats.MarkStart();

            int tcpThreads = _cfg.Threads > 0
                ? _cfg.Threads
                : Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

            for (int i = 0; i < tcpThreads; i++) SpawnWorker(_tcpHandle, false, i, generation);
            if (_quicHandle != WinDivert.InvalidHandle) SpawnWorker(_quicHandle, true, 100, generation);
        }

        public void Stop()
        {
            lock (_lifecycle) { StopLocked(); }
        }

        private void StopLocked()
        {
            if (!_running && _tcpHandle == WinDivert.InvalidHandle &&
                _quicHandle == WinDivert.InvalidHandle) return;
            _running = false;

            if (_tcpHandle != WinDivert.InvalidHandle) WinDivert.WinDivertShutdown(_tcpHandle, WinDivert.ShutdownBoth);
            if (_quicHandle != WinDivert.InvalidHandle) WinDivert.WinDivertShutdown(_quicHandle, WinDivert.ShutdownBoth);

            // Ortak son tarih: is parcacigi basina beklemek, kapanisi cok is
            // parcacigiyla uzatirdi. Shutdown cagrisi zaten hepsini uyandirir.
            var deadline = Environment.TickCount + 1500;
            bool allExited = true;
            foreach (var t in _threads)
            {
                int left = deadline - Environment.TickCount;
                if (left < 0) left = 0;
                try { if (!t.Join(left)) allExited = false; }
                catch { allExited = false; }
            }

            var pending = _threads.ToArray();
            _threads.Clear();

            IntPtr tcp = _tcpHandle, quic = _quicHandle;
            _tcpHandle = WinDivert.InvalidHandle;
            _quicHandle = WinDivert.InvalidHandle;

            if (allExited)
            {
                CloseHandles(tcp, quic);
                return;
            }

            // Bir is parcacigi hala WinDivertRecv icinde bekliyor. Handle'i simdi
            // kapatmak, kapatilmis bir tutamak uzerinde bekleyen cagri birakir -
            // tanimsiz davranis. Kapatmayi arka plana aliyoruz: arayuz beklemez,
            // tutamak ise ancak herkes cikinca kapanir.
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (var t in pending)
                {
                    // Shutdown normalde Recv'i hemen uyandirir. Yine de bir surucu
                    // gecikirse tutamagi calisan thread'in altindan kapatmayiz.
                    try { t.Join(); }
                    catch { }
                }
                CloseHandles(tcp, quic);
            });
        }

        private static void CloseHandles(IntPtr tcp, IntPtr quic)
        {
            if (tcp != WinDivert.InvalidHandle) WinDivert.WinDivertClose(tcp);
            if (quic != WinDivert.InvalidHandle) WinDivert.WinDivertClose(quic);
        }

        public void Dispose() { Stop(); }

        // ================================================================ tutamak

        private IntPtr Open(string filter)
        {
            IntPtr h;
            try
            {
                h = WinDivert.WinDivertOpen(filter, DivertLayer.Network, _cfg.DivertPriority, (ulong)DivertFlags.None);
            }
            catch (DllNotFoundException)
            {
                throw new EngineException(
                    "WinDivert.dll bulunamadı.\n\nbin klasörüne WinDivert.dll ve WinDivert64.sys " +
                    "dosyalarını koyun (kurulum: tools\\setup-windivert.cmd).");
            }
            catch (BadImageFormatException)
            {
                throw new EngineException("WinDivert.dll mimarisi uyumsuz. 64 bit sürüm gerekli.");
            }

            if (h == WinDivert.InvalidHandle)
            {
                int err = Marshal.GetLastWin32Error();
                throw new EngineException(DescribeOpenError(err));
            }

            if (!WinDivert.WinDivertSetParam(h, DivertParam.QueueLength, (ulong)_cfg.QueueLength) ||
                !WinDivert.WinDivertSetParam(h, DivertParam.QueueTime, (ulong)_cfg.QueueTime) ||
                !WinDivert.WinDivertSetParam(h, DivertParam.QueueSize, (ulong)_cfg.QueueSize))
            {
                int err = Marshal.GetLastWin32Error();
                WinDivert.WinDivertClose(h);
                throw new EngineException("WinDivert kuyruk ayarları uygulanamadı (hata " + err + ").");
            }
            return h;
        }

        private static string DescribeOpenError(int err)
        {
            switch (err)
            {
                case ErrorFileNotFound:
                    return "WinDivert sürücü dosyası (WinDivert64.sys) bulunamadı.\n" +
                           "DLL ile .sys dosyası aynı klasörde olmalı.";
                case ErrorAccessDenied:
                    return "Yönetici izni gerekiyor. dpetoos yönetici olarak çalıştırılmalı.";
                case ErrorInvalidParameter:
                    return "Filtre ifadesi sürücü tarafından reddedildi (WinDivert 2.2+ gerekir).";
                case ErrorInvalidImageHash:
                    return "WinDivert sürücüsünün imzası Windows tarafından kabul edilmedi.\n" +
                           "Resmi WinDivert 2.2.2 paketini kullanın.";
                case ErrorServiceDoesNotExist:
                    return "WinDivert servisi kurulamadı. Bir güvenlik yazılımı engelliyor olabilir.";
                case ErrorDriverBlocked:
                    return "Sürücü Windows tarafından engellendi (Bellek Bütünlüğü / Çekirdek Yalıtımı açık olabilir).";
                case 1058:
                    return "WinDivert servisi devre dışı bırakılmış.";
                default:
                    return "WinDivert açılamadı (hata kodu " + err + ").";
            }
        }

        // ============================================================ is parcaciklari

        private void SpawnWorker(IntPtr handle, bool quic, int seed, int generation)
        {
            var t = new Thread(() => WorkerLoop(handle, quic, seed, generation));
            t.IsBackground = true;
            t.Name = quic ? "dpetoos-quic" : "dpetoos-tcp-" + seed;
            try { t.Priority = ThreadPriority.Highest; } catch { }
            _threads.Add(t);
            try { t.Start(); }
            catch { _threads.Remove(t); throw; }
        }

        private void WorkerLoop(IntPtr handle, bool quic, int seed, int generation)
        {
            DesyncWorker w = null;
            string terminalFault = null;
            try
            {
                w = new DesyncWorker(_cfg, _stats, handle, seed);
                int consecutiveErrors = 0;

                while (_running && generation == Volatile.Read(ref _generation))
                {
                    uint recvLen = 0;
                    DivertAddress addr = default(DivertAddress);

                    if (!WinDivert.WinDivertRecv(handle, w.RecvBuffer, (uint)w.RecvCapacity, &recvLen, &addr))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (!_running || err == ErrorNoData) break;
                        if (err == ErrorInsufficientBuffer)
                        {
                            // Paket tampona sigmadi ve kuyruktan dustu. Normalde
                            // erisilmez (tampon 64 KB) ama sessizce yutmuyoruz.
                            _stats.BumpSendError();
                            continue;
                        }
                        if (++consecutiveErrors > 64)
                        {
                            terminalFault = "Paket alımı sürekli başarısız (hata " + err + "). Koruma durduruldu.";
                            break;
                        }
                        Thread.Sleep(1);
                        continue;
                    }
                    consecutiveErrors = 0;
                    if (recvLen == 0) continue;

                    bool handled;
                    try
                    {
                        handled = quic
                            ? w.HandleQuic((int)recvLen, ref addr)
                            : w.HandleTcp((int)recvLen, ref addr);
                    }
                    catch
                    {
                        handled = false;   // ne olursa olsun paketi yutma
                    }

                    if (!handled)
                    {
                        uint sent;
                        if (!WinDivert.WinDivertSend(handle, w.RecvBuffer, recvLen, &sent, &addr))
                            _stats.BumpSendError();       // dokunmadigimiz paket bile kaybolmamali
                        _stats.BumpPassthrough();
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running && generation == Volatile.Read(ref _generation))
                    terminalFault = "Motor hatası: " + ex.Message;
            }
            finally
            {
                if (w != null) w.Dispose();
                if (terminalFault != null) FailGeneration(generation, terminalFault);
            }
        }

        private void FailGeneration(int generation, string message)
        {
            if (!_running || generation != Volatile.Read(ref _generation)) return;
            if (Interlocked.CompareExchange(ref _faultRaised, 1, 0) != 0) return;

            RaiseFault(message);
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (_lifecycle)
                {
                    if (_running && generation == Volatile.Read(ref _generation)) StopLocked();
                }
            });
        }

        private void RaiseFault(string msg)
        {
            LogFault(msg);
            var h = Fault;
            if (h != null) { try { h(msg); } catch { } }
        }

        /// <summary>
        /// Arizayi diske yazar. Motor kendi kendine durdugunda pencere kapaliysa
        /// ya da tepsideyse geriye hicbir iz kalmiyordu; sorunu bildiren kullanici
        /// da "birden calismaz oldu" demekten oteye gidemiyordu.
        /// </summary>
        private static void LogFault(string msg)
        {
            try
            {
                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "dpetoos-fault.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // ================================================================= filtreler

        private string PrivateExclusion()
        {
            if (!_cfg.SkipPrivateRanges) return null;
            // Yerel ag, loopback ve link-local hedefleri hic ellemiyoruz:
            // modem arayuzu, NAS, yerel gelistirme sunuculari etkilenmesin.
            string v4 = "((ip.DstAddr < 10.0.0.0 or ip.DstAddr > 10.255.255.255) and "
                      + "(ip.DstAddr < 172.16.0.0 or ip.DstAddr > 172.31.255.255) and "
                      + "(ip.DstAddr < 192.168.0.0 or ip.DstAddr > 192.168.255.255) and "
                      + "(ip.DstAddr < 127.0.0.0 or ip.DstAddr > 127.255.255.255) and "
                      + "(ip.DstAddr < 169.254.0.0 or ip.DstAddr > 169.254.255.255))";

            // IPv6 tarafi: fe80::/10 (link-local) ve fc00::/7 (unique local).
            // Bu ifade surucu tarafindan reddedilirse aday merdiveni yalniz-IPv4
            // surumune duser, davranis bozulmaz.
            string v6 = "((ipv6.DstAddr < fe80:: or ipv6.DstAddr > febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff) and "
                      + "(ipv6.DstAddr < fc00:: or ipv6.DstAddr > fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff))";

            return "((ipv6 and " + v6 + ") or (ip and " + v4 + "))";
        }

        private static string Join(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(" and ");
                sb.Append(p);
            }
            return sb.ToString();
        }

        private string TlsClause()
        {
            // 0x16 = handshake kaydi, 0x03 = TLS surum ailesi, payload[5] = 0x01 -> ClientHello
            // Payload[2] <= 0x04 : kayit surumunun alt baytinin makul araligi
            // Payload[3] <= 0x40 : TLS kaydi en fazla 16 KB olabilir
            // Ikisi de ucuz ve rastgele veriyle yanlis eslesmeyi 256 kat azaltir.
            // Tam yapisal dogrulama (kayit/el sikisma uzunlugu tutarliligi)
            // kullanici tarafinda IsClientHello() icinde yapilir.
            return "(tcp.DstPort == 443 and tcp.Payload[0] == 0x16 and tcp.Payload[1] == 0x03 "
                 + "and tcp.Payload[2] <= 0x04 and tcp.Payload[3] <= 0x40 and tcp.Payload[5] == 0x01)";
        }

        private string HttpClause()
        {
            // Yalnizca istek satiriyla baslayan paketler: GET/POST/HEAD/PUT/OPTIONS/PATCH/DELETE/CONNECT
            return "(tcp.DstPort == 80 and ("
                 + "tcp.Payload32[0] == 0x47455420 or "
                 + "tcp.Payload32[0] == 0x504f5354 or "
                 + "tcp.Payload32[0] == 0x48454144 or "
                 + "tcp.Payload32[0] == 0x50555420 or "
                 + "tcp.Payload32[0] == 0x4f505449 or "
                 + "tcp.Payload32[0] == 0x50415443 or "
                 + "tcp.Payload32[0] == 0x44454c45 or "
                 + "tcp.Payload32[0] == 0x434f4e4e))";
        }

        private string PortsClause()
        {
            var l = new List<string>();
            if (TlsActive()) l.Add("tcp.DstPort == 443");
            if (HttpActive()) l.Add("tcp.DstPort == 80");
            if (l.Count == 0) return "false";
            return "(" + string.Join(" or ", l.ToArray()) + ")";
        }

        private string PayloadClause()
        {
            var l = new List<string>();
            if (TlsActive()) l.Add(TlsClause());
            if (HttpActive()) l.Add(HttpClause());
            if (l.Count == 0) return "false";
            return "(" + string.Join(" or ", l.ToArray()) + ")";
        }

        private static bool ModeActive(string mode)
        {
            return mode != "off" && mode != "none" && !string.IsNullOrEmpty(mode);
        }

        private bool TlsActive() { return _cfg.EnableTls && ModeActive(_cfg.TlsMode); }
        private bool HttpActive() { return _cfg.EnableHttp && ModeActive(_cfg.HttpMode); }

        /// <summary>
        /// En dardan en genise dogru aday filtreler. Ilk derlenebilen kullanilir.
        /// Genis olana dusersek de dogruluk bozulmaz; sadece kullanici alanina biraz
        /// daha fazla paket gelir (yine de yalnizca 80/443 giden veri paketleri).
        /// </summary>
        /// <summary>IPv6 sozdizimi reddedilirse kullanilacak yalniz-IPv4 surumu.</summary>
        private string PrivateExclusionV4Only()
        {
            if (!_cfg.SkipPrivateRanges) return null;
            return "(ipv6 or ("
                 + "(ip.DstAddr < 10.0.0.0 or ip.DstAddr > 10.255.255.255) and "
                 + "(ip.DstAddr < 172.16.0.0 or ip.DstAddr > 172.31.255.255) and "
                 + "(ip.DstAddr < 192.168.0.0 or ip.DstAddr > 192.168.255.255) and "
                 + "(ip.DstAddr < 127.0.0.0 or ip.DstAddr > 127.255.255.255) and "
                 + "(ip.DstAddr < 169.254.0.0 or ip.DstAddr > 169.254.255.255)))";
        }

        internal string[] BuildTcpFilterCandidates()
        {
            // !tcp.Syn : TCP Fast Open ile gelen yuklu SYN paketleri hic gelmesin.
            string baseExpr = "outbound and !impostor and !loopback and tcp and !tcp.Syn and tcp.PayloadLength > 0";
            string priv = PrivateExclusion();
            return new[]
            {
                Join(baseExpr, priv, PayloadClause()),
                Join(baseExpr, PrivateExclusionV4Only(), PayloadClause()),
                Join(baseExpr, PayloadClause()),
                Join(baseExpr, priv, PortsClause()),
                Join(baseExpr, PortsClause())
            };
        }

        internal string[] BuildQuicFilterCandidates()
        {
            // QUIC Initial: uzun basik (0xc0..0xcf) ve dolgu nedeniyle daima buyuk paket.
            // Kisa basikli veri paketleri (0x40..0x7f) bu filtreye hic takilmaz.
            string baseExpr = "outbound and !impostor and !loopback and udp and udp.DstPort == 443 and udp.PayloadLength >= 1200";
            // Yalnizca ilk bayta bakmak YETMEZ: 0xc0-0xcf araligi rastgele bir
            // baytin %6'sina denk gelir. UDP/443 uzerinden gecen QUIC olmayan
            // trafik (ornegin Steam Datagram Relay - CS2 buradan gecer) yanlislikla
            // yakalanir ve oyun paketine sahte paket enjekte edilir -> ping sicramasi.
            // Bu yuzden QUIC SURUM alani da dogrulanir: v1 (0x00000001) veya v2.
            string head = "((udp.Payload[0] >= 0xc0 and udp.Payload[0] <= 0xcf and "
                        + "udp.Payload32[1] == 0x00000001) or "
                        + "(udp.Payload[0] >= 0xd0 and udp.Payload[0] <= 0xdf and "
                        + "udp.Payload32[1] == 0x6b3343cf))";
            string priv = PrivateExclusion();
            return new[]
            {
                Join(baseExpr, priv, head),
                Join(baseExpr, PrivateExclusionV4Only(), head),
                Join(baseExpr, head),
                baseExpr
            };
        }

        private static string PickFilter(string[] candidates, DivertLayer layer)
        {
            foreach (var f in candidates)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (FilterCompiles(f, layer)) return f;
            }
            return null;
        }

        internal static bool FilterCompiles(string filter, DivertLayer layer)
        {
            try
            {
                byte* err = null;
                uint pos = 0;
                return WinDivert.WinDivertHelperCompileFilter(filter, layer, null, 0, &err, &pos);
            }
            catch (DllNotFoundException) { return true; }   // DLL yoksa Open zaten anlamli hata verecek
            catch (EntryPointNotFoundException) { return true; }
            catch { return false; }
        }
    }

    internal sealed class EngineException : Exception
    {
        public EngineException(string m) : base(m) { }
    }
}
