// Parcali ClientHello: devam segmentlerinin kopyalari.
//
// Post-quantum anahtar degisimi (X25519MLKEM768) acik Chromium tabanli
// istemcilerde - Discord masaustu dahil - ClientHello ~1.8 KB ve tek TCP
// segmentine sigmiyor. Chromium uzanti sirasini her baglantida karistirdigi
// icin SNI baglantilarin kabaca %40'inda ikinci segmente dusuyor.
//
// Kablolu kartlarin cogunda LSO (Large Send Offload) acik: yigin ClientHello'nun
// tamamini tek buyuk paket olarak iniyor, segmentlere karti boluyor. WinDivert
// paketi karttan once gordugu icin SNI hep elimizde. Wi-Fi kartlarinda LSO yok;
// veriyi yigin kendisi boluyor ve cekirdek filtresine yalnizca ilk segment
// takiliyor. SNI ikinci segmentteyse alan adi listesi eslesmiyor, pakete hic
// dokunulmuyor ve DPI baglantiyi kesiyor. "Kabloda sorunsuz, Wi-Fi'de arada bir
// acilmiyor" tablosu buradan geliyordu.
//
// Ayri bir SNIFF tutamagi 443'e giden kisa PSH segmentlerinin KOPYASINI buraya
// yaziyor; paketin kendisi bekletilmiyor. Is parcacigi ilk segmentte SNI
// bulamazsa devamini burada arayip alan adini birlesik mesajdan okuyor.
//
// Sicak yolda tahsis yok: tablo acilista tek unmanaged blok olarak aliniyor.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Etodpx.Engine
{
    internal sealed unsafe class ContinuationTable : IDisposable
    {
        // Cekirdek filtresi de bu siniri kullaniyor: toplu yuklemenin tam boy
        // segmentleri hic kopyalanmiyor.
        public const int MaxPayload = 1400;
        private const int Slots = 32;
        // Kaybolan ClientHello yeniden gonderildiginde devam kopyasi hala
        // bulunabilsin; eslesme 4'lu + sira numarasiyla oldugu icin karismaz.
        private const int MaxAgeMs = 5000;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Entry
        {
            public int Stamp;            // 0 = bos
            public int Length;
            public uint Seq;
            public ushort SrcPort;
            public ushort DstPort;
            public byte IsV6;
            public fixed byte Src[16];
            public fixed byte Dst[16];
            public fixed byte Data[MaxPayload];
        }

        private readonly object _gate = new object();
        private readonly IntPtr _mem;
        private readonly Entry* _entries;
        private int _next;

        public ContinuationTable()
        {
            _mem = Marshal.AllocHGlobal(sizeof(Entry) * Slots);
            _entries = (Entry*)_mem;
            for (int i = 0; i < Slots; i++) _entries[i].Stamp = 0;
        }

        // SNIFF tutamagindan okunan paketin kopyasini saklar.
        public void Record(byte* pkt, int len)
        {
            PacketView v;
            if (!Packet.Parse(pkt, len, out v) || v.Protocol != Packet.ProtoTcp) return;
            int n = v.PayloadLength;
            if (n <= 0 || n > MaxPayload) return;

            lock (_gate)
            {
                Entry* e = _entries + _next;
                _next = (_next + 1) % Slots;

                e->Stamp = Environment.TickCount | 1;
                e->Length = n;
                e->Seq = Packet.TcpSeq(pkt, v.L4Offset);
                e->SrcPort = Packet.Read16(pkt + v.L4Offset);
                e->DstPort = Packet.Read16(pkt + v.L4Offset + 2);
                e->IsV6 = (byte)(v.IsV6 ? 1 : 0);

                for (int i = 0; i < 16; i++) { e->Src[i] = 0; e->Dst[i] = 0; }
                if (v.IsV6)
                {
                    Packet.Copy(e->Src, pkt + 8, 16);
                    Packet.Copy(e->Dst, pkt + 24, 16);
                }
                else
                {
                    Packet.Copy(e->Src, pkt + 12, 4);
                    Packet.Copy(e->Dst, pkt + 16, 4);
                }
                Packet.Copy(e->Data, v.Payload, n);

                Monitor.PulseAll(_gate);
            }
        }

        // pkt'teki segmentin hemen ardindan gelen segmentin yukunu dst'ye kopyalar.
        // Doner: kopyalanan bayt; bulunamazsa 0.
        //
        // Kopya genelde ilk segment okunmadan ONCE burada: yigin iki segmenti arka
        // arkaya yolluyor, ilki bizim kuyrugumuzda bekliyor, ikincisi dogrudan hatta
        // cikip kopyasi sniff tutamagina dusuyor. Bekleme yalnizca is parcaciklari
        // ters sirada uyanirsa devreye giriyor.
        public int CopyNext(byte* pkt, ref PacketView v, byte* dst, int dstCap, int waitMs)
        {
            if (dstCap <= 0) return 0;
            uint want = unchecked(Packet.TcpSeq(pkt, v.L4Offset) + (uint)v.PayloadLength);
            int deadline = unchecked(Environment.TickCount + waitMs);

            lock (_gate)
            {
                while (true)
                {
                    int now = Environment.TickCount;
                    for (int i = 0; i < Slots; i++)
                    {
                        Entry* e = _entries + i;
                        if (e->Stamp == 0 || e->Seq != want) continue;
                        if (unchecked(now - e->Stamp) > MaxAgeMs) continue;
                        if (!SameFlow(e, pkt, ref v)) continue;

                        int n = e->Length < dstCap ? e->Length : dstCap;
                        Packet.Copy(dst, e->Data, n);
                        return n;
                    }

                    int left = unchecked(deadline - Environment.TickCount);
                    if (left <= 0) return 0;
                    Monitor.Wait(_gate, left);
                }
            }
        }

        private static bool SameFlow(Entry* e, byte* pkt, ref PacketView v)
        {
            if (e->IsV6 != (v.IsV6 ? 1 : 0)) return false;
            if (e->SrcPort != Packet.Read16(pkt + v.L4Offset)) return false;
            if (e->DstPort != Packet.Read16(pkt + v.L4Offset + 2)) return false;

            int n = v.IsV6 ? 16 : 4;
            byte* src = pkt + (v.IsV6 ? 8 : 12);
            byte* dst = pkt + (v.IsV6 ? 24 : 16);
            for (int i = 0; i < n; i++)
                if (e->Src[i] != src[i] || e->Dst[i] != dst[i]) return false;
            return true;
        }

        public void Dispose()
        {
            if (_mem != IntPtr.Zero) Marshal.FreeHGlobal(_mem);
        }
    }
}
