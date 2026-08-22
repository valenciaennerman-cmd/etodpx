// dpetoos - TLS ClientHello (SNI) ve HTTP Host konumlandirma
// Amac: payload icinde alan adinin TAM konumunu bulmak, boylece TCP'yi
// tam alan adinin ortasindan bolebilelim. DPI icin en zor durum budur.
using System;

namespace Dpetoos.Engine
{
    internal struct HostLocation
    {
        public int Offset;   // payload icinde alan adinin ilk baytinin konumu
        public int Length;   // alan adi uzunlugu
        public bool Found;
    }

    internal static unsafe class Hostname
    {
        // ---------------------------------------------------------------- TLS SNI
        /// <summary>
        /// TLS ClientHello icindeki SNI alan adini bulur.
        /// Tum uzunluk alanlari sinir kontrolunden gecer; bozuk paket -> Found=false.
        /// </summary>
        public static HostLocation FindSni(byte* p, int len)
        {
            HostLocation r = default(HostLocation);
            if (len < 44) return r;
            if (p[0] != 0x16) return r;            // handshake record
            if (p[5] != 0x01) return r;            // ClientHello

            // ClientHello govdesi record basligindan (5) sonra baslar.
            int hsLen = (p[6] << 16) | (p[7] << 8) | p[8];
            int end = 9 + hsLen;
            if (end > len) end = len;              // parcalanmis olabilir, elimizdekiyle devam

            int i = 9 + 2 + 32;                    // client_version(2) + random(32)
            if (i + 1 > end) return r;

            int sessionIdLen = p[i]; i += 1 + sessionIdLen;
            if (i + 2 > end) return r;

            int cipherLen = (p[i] << 8) | p[i + 1]; i += 2 + cipherLen;
            if (i + 1 > end) return r;

            int compLen = p[i]; i += 1 + compLen;
            if (i + 2 > end) return r;

            int extTotal = (p[i] << 8) | p[i + 1]; i += 2;
            int extEnd = i + extTotal;
            if (extEnd > end) extEnd = end;

            while (i + 4 <= extEnd)
            {
                int extType = (p[i] << 8) | p[i + 1];
                int extLen = (p[i + 2] << 8) | p[i + 3];
                int extData = i + 4;
                if (extData + extLen > extEnd) return r;

                if (extType == 0x0000)             // server_name
                {
                    if (extLen < 5) return r;
                    // list_len(2) name_type(1) name_len(2) name...
                    int nameType = p[extData + 2];
                    int nameLen = (p[extData + 3] << 8) | p[extData + 4];
                    int nameOff = extData + 5;
                    if (nameType != 0) return r;
                    if (nameLen <= 0 || nameOff + nameLen > extEnd) return r;
                    r.Offset = nameOff;
                    r.Length = nameLen;
                    r.Found = true;
                    return r;
                }
                i = extData + extLen;
            }
            return r;
        }

        // --------------------------------------------------------------- HTTP Host
        private static readonly byte[] NeedleHost = { (byte)'h', (byte)'o', (byte)'s', (byte)'t', (byte)':' };

        /// <summary>HTTP istegindeki Host basliginin degerini bulur.</summary>
        public static HostLocation FindHttpHost(byte* p, int len)
        {
            HostLocation r = default(HostLocation);
            int i = 0;
            while (true)
            {
                int at = Packet.IndexOfCI(p, len, NeedleHost, i);
                if (at < 0) return r;

                // Satir basinda olmali (ilk satir istek satiridir, oradaki "host:" sayilmaz)
                bool lineStart = at >= 2 && p[at - 1] == (byte)'\n';
                if (!lineStart) { i = at + 1; continue; }

                int v = at + NeedleHost.Length;
                while (v < len && (p[v] == (byte)' ' || p[v] == (byte)'\t')) v++;
                int e = v;
                while (e < len && p[e] != (byte)'\r' && p[e] != (byte)'\n') e++;
                // port varsa alan adindan ayir
                int colon = -1;
                for (int k = v; k < e; k++) if (p[k] == (byte)':') { colon = k; break; }
                if (colon >= 0) e = colon;

                if (e <= v) return r;
                r.Offset = v;
                r.Length = e - v;
                r.Found = true;
                return r;
            }
        }

        /// <summary>
        /// HTTP baslik adlari buyuk/kucuk harf duyarsizdir. Gercek Host: basligini
        /// hOSt: yaparak basit, harfe duyarli DPI imzalarini bozar.
        ///
        /// DIKKAT: bu, YAKALANAN paketin tamponunu yerinde degistirir. Paketi
        /// kendimiz gondermezsek WorkerLoop onu checksum'i YENIDEN HESAPLAMADAN
        /// enjekte eder; degismis yuk ile eski checksum bozuk paket demektir ve
        /// sunucu onu atar. Bu yuzden degisiklik geri alinabilir olmali.
        /// </summary>
        /// <returns>Degistirilen ofset, degisiklik yapilmadiysa -1.</returns>
        public static int RewriteHttpHostCase(byte* p, int len, out uint saved)
        {
            saved = 0;
            int i = 0;
            while (true)
            {
                int at = Packet.IndexOfCI(p, len, NeedleHost, i);
                if (at < 0) return -1;
                if (at >= 2 && p[at - 1] == (byte)'\n')
                {
                    saved = Packet.Read32(p + at);
                    p[at] = (byte)'h';
                    p[at + 1] = (byte)'O';
                    p[at + 2] = (byte)'S';
                    p[at + 3] = (byte)'t';
                    return at;
                }
                i = at + 1;
            }
        }

        /// <summary>RewriteHttpHostCase degisikligini geri alir.</summary>
        public static void RestoreHttpHostCase(byte* p, int at, uint saved)
        {
            if (at >= 0) Packet.Write32(p + at, saved);
        }

        // ------------------------------------------------------- bolme konumu secimi

        /// <summary>
        /// Alan adinin ikinci seviye etiketinin (SLD) ortasini dondurur.
        /// "www.youtube.com" -> "youtube" ortasi. DPI'in imza eslemesini en cok
        /// bozan konum burasidir.
        /// </summary>
        public static int MidSld(byte* p, int hostOff, int hostLen)
        {
            if (hostLen <= 0) return hostOff;

            int lastDot = -1, prevDot = -1;
            for (int k = hostLen - 1; k >= 0; k--)
            {
                if (p[hostOff + k] == (byte)'.')
                {
                    if (lastDot < 0) lastDot = k;
                    else { prevDot = k; break; }
                }
            }

            int sldStart, sldEnd;
            if (lastDot < 0) { sldStart = 0; sldEnd = hostLen; }        // nokta yok
            else if (prevDot < 0) { sldStart = 0; sldEnd = lastDot; }   // "site.com"
            else { sldStart = prevDot + 1; sldEnd = lastDot; }          // "www.site.com"

            int sldLen = sldEnd - sldStart;
            if (sldLen <= 1) return hostOff + hostLen / 2;
            return hostOff + sldStart + sldLen / 2;
        }
    }
}
