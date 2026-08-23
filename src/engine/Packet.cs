// etodpx - ham paket ayristirma / kurma yardimcilari
// Hicbir fonksiyon allocation yapmaz; hepsi pointer uzerinde calisir.
using System;
using System.Runtime.CompilerServices;

namespace Etodpx.Engine
{
    /// <summary>Bir paketin cozumlenmis gorunumu (offset'ler paket basina gore).</summary>
    internal unsafe struct PacketView
    {
        public byte* Base;
        public int Length;

        public bool IsV6;
        public int IpHeaderLen;
        public byte Protocol;

        public int L4Offset;      // TCP/UDP basligi
        public int L4HeaderLen;
        public int PayloadOffset;
        public int PayloadLength;

        public byte* Payload { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return Base + PayloadOffset; } }
    }

    internal static unsafe class Packet
    {
        public const byte ProtoTcp = 6;
        public const byte ProtoUdp = 17;

        // ---- endian yardimcilari -------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Read16(byte* p) { return (ushort)((p[0] << 8) | p[1]); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Read32(byte* p)
        {
            return ((uint)p[0] << 24) | ((uint)p[1] << 16) | ((uint)p[2] << 8) | p[3];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write16(byte* p, ushort v) { p[0] = (byte)(v >> 8); p[1] = (byte)v; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write32(byte* p, uint v)
        {
            p[0] = (byte)(v >> 24); p[1] = (byte)(v >> 16); p[2] = (byte)(v >> 8); p[3] = (byte)v;
        }

        // ---- IP / TCP / UDP ayristirma ------------------------------------------

        /// <summary>Paketi cozumler. Bozuk/desteklenmeyen paketlerde false doner.</summary>
        public static bool Parse(byte* pkt, int len, out PacketView v)
        {
            v = default(PacketView);
            if (len < 20) return false;

            v.Base = pkt;
            v.Length = len;

            int version = pkt[0] >> 4;
            if (version == 4)
            {
                v.IsV6 = false;
                v.IpHeaderLen = (pkt[0] & 0x0F) * 4;
                if (v.IpHeaderLen < 20 || v.IpHeaderLen > len) return false;
                v.Protocol = pkt[9];
            }
            else if (version == 6)
            {
                if (len < 40) return false;
                v.IsV6 = true;
                v.IpHeaderLen = 40;
                v.Protocol = pkt[6];
                // Uzanti basliklari: desync icin gerekli degil, boyle paketleri es geciyoruz.
                if (v.Protocol != ProtoTcp && v.Protocol != ProtoUdp) return false;
            }
            else return false;

            // IP basliginda beyan edilen uzunluk tampondaki uzunluktan kisa olabilir
            // (surucu dolgusu, katman farklari). Yuk uzunlugunu beyana gore kirpmazsak
            // dolgu baytlarini veri sanip yanlis yerden boleriz.
            int declared = v.IsV6 ? 40 + Read16(pkt + 4) : Read16(pkt + 2);
            if (declared >= v.IpHeaderLen && declared < len) len = declared;
            v.Length = len;

            v.L4Offset = v.IpHeaderLen;

            if (v.Protocol == ProtoTcp)
            {
                if (v.L4Offset + 20 > len) return false;
                v.L4HeaderLen = ((pkt[v.L4Offset + 12] >> 4) & 0x0F) * 4;
                if (v.L4HeaderLen < 20) return false;
            }
            else if (v.Protocol == ProtoUdp)
            {
                if (v.L4Offset + 8 > len) return false;
                v.L4HeaderLen = 8;
            }
            else return false;

            v.PayloadOffset = v.L4Offset + v.L4HeaderLen;
            if (v.PayloadOffset > len) return false;
            v.PayloadLength = len - v.PayloadOffset;
            return true;
        }

        // ---- TCP alan erisimleri -------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint TcpSeq(byte* pkt, int l4) { return Read32(pkt + l4 + 4); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTcpSeq(byte* pkt, int l4, uint seq) { Write32(pkt + l4 + 4, seq); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte TcpFlags(byte* pkt, int l4) { return pkt[l4 + 13]; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTcpFlags(byte* pkt, int l4, byte f) { pkt[l4 + 13] = f; }

        public const byte TcpFin = 0x01, TcpSyn = 0x02, TcpRst = 0x04,
                          TcpPsh = 0x08, TcpAck = 0x10, TcpUrg = 0x20;

        // ---- IP uzunluk / TTL ----------------------------------------------------

        /// <summary>IPv4 total-length ya da IPv6 payload-length alanini gunceller.</summary>
        public static void SetIpLength(byte* pkt, bool isV6, int totalLen, int ipHdrLen)
        {
            if (isV6) Write16(pkt + 4, (ushort)(totalLen - ipHdrLen));
            else Write16(pkt + 2, (ushort)totalLen);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTtl(byte* pkt, bool isV6, byte ttl) { pkt[isV6 ? 7 : 8] = ttl; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetTtl(byte* pkt, bool isV6) { return pkt[isV6 ? 7 : 8]; }

        /// <summary>IPv4 kimlik alanini degistirir (sahte paketlerin ayrilmasi icin).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetIpId(byte* pkt, bool isV6, ushort id) { if (!isV6) Write16(pkt + 4, id); }

        // ---- kopyalama -----------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Copy(byte* dst, byte* src, int n)
        {
            Buffer.MemoryCopy(src, dst, n, n);
        }

        /// <summary>Buyuk/kucuk harf duyarsiz ASCII karsilastirma (payload icinde arama icin).</summary>
        public static int IndexOfCI(byte* hay, int hayLen, byte[] needleLower, int startAt)
        {
            int nl = needleLower.Length;
            if (nl == 0 || hayLen < nl) return -1;
            int last = hayLen - nl;
            for (int i = startAt; i <= last; i++)
            {
                int j = 0;
                while (j < nl)
                {
                    byte c = hay[i + j];
                    if (c >= (byte)'A' && c <= (byte)'Z') c += 32;
                    if (c != needleLower[j]) break;
                    j++;
                }
                if (j == nl) return i;
            }
            return -1;
        }
    }
}
