// etodpx - sahte (decoy) yuk uretimi
// Sahte paketler DPI'in gordugu ilk paket olur; gercek sunucuya asla ulasmaz
// (bozuk checksum) ya da yolda oler (dusuk TTL). Icerik zararsiz bir alan adi
// tasir, boylece yanlislikla ulasirsa bile kimseyi rahatsiz etmez.
using System;
using System.Collections.Generic;
using System.Text;

namespace Etodpx.Engine
{
    internal static class Fakes
    {
        private static readonly Random Rng = new Random();

        private static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            lock (Rng) Rng.NextBytes(b);
            return b;
        }

        /// <summary>Gecerli gorunumlu, zararsiz SNI tasiyan bir TLS ClientHello uretir.</summary>
        public static byte[] BuildTlsClientHello(string sni)
        {
            if (string.IsNullOrEmpty(sni)) sni = "www.iana.org";
            byte[] host = Encoding.ASCII.GetBytes(sni);

            var ext = new List<byte>(256);

            // server_name (0x0000)
            AddExt(ext, 0x0000, Concat(
                Be16(host.Length + 3),
                new byte[] { 0x00 },
                Be16(host.Length),
                host));

            // ec_point_formats (0x000b)
            AddExt(ext, 0x000b, new byte[] { 0x01, 0x00 });

            // supported_groups (0x000a): x25519, secp256r1, secp384r1
            AddExt(ext, 0x000a, new byte[] { 0x00, 0x06, 0x00, 0x1d, 0x00, 0x17, 0x00, 0x18 });

            // session_ticket (0x0023)
            AddExt(ext, 0x0023, new byte[0]);

            // signature_algorithms (0x000d)
            AddExt(ext, 0x000d, new byte[] {
                0x00, 0x0a, 0x04, 0x03, 0x08, 0x04, 0x04, 0x01, 0x05, 0x03, 0x02, 0x01 });

            // supported_versions (0x002b): TLS 1.3, 1.2
            AddExt(ext, 0x002b, new byte[] { 0x04, 0x03, 0x04, 0x03, 0x03 });

            // key_share (0x0033): x25519, 32 bayt rastgele
            var kex = RandomBytes(32);
            AddExt(ext, 0x0033, Concat(
                Be16(36), Be16(0x001d), Be16(32), kex));

            var body = new List<byte>(512);
            body.AddRange(new byte[] { 0x03, 0x03 });          // client_version TLS 1.2
            body.AddRange(RandomBytes(32));                     // random
            body.Add(32); body.AddRange(RandomBytes(32));       // legacy session id
            body.AddRange(new byte[] {                          // cipher suites
                0x00, 0x08, 0x13, 0x01, 0x13, 0x02, 0xc0, 0x2f, 0x00, 0x9c });
            body.AddRange(new byte[] { 0x01, 0x00 });           // compression: null
            body.AddRange(Be16(ext.Count));
            body.AddRange(ext);

            var hs = new List<byte>(body.Count + 4);
            hs.Add(0x01);                                       // ClientHello
            hs.AddRange(Be24(body.Count));
            hs.AddRange(body);

            var rec = new List<byte>(hs.Count + 5);
            rec.AddRange(new byte[] { 0x16, 0x03, 0x01 });      // handshake, TLS 1.0 record
            rec.AddRange(Be16(hs.Count));
            rec.AddRange(hs);

            return rec.ToArray();
        }

        /// <summary>Zararsiz gorunumlu bir HTTP GET istegi uretir.</summary>
        public static byte[] BuildHttpRequest(string host)
        {
            if (string.IsNullOrEmpty(host)) host = "www.iana.org";
            string s =
                "GET / HTTP/1.1\r\n" +
                "Host: " + host + "\r\n" +
                "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n" +
                "Accept: */*\r\n" +
                "Accept-Encoding: gzip, deflate\r\n" +
                "Connection: keep-alive\r\n\r\n";
            return Encoding.ASCII.GetBytes(s);
        }

        /// <summary>
        /// QUIC Initial gorunumlu sahte paket. DCID rastgele oldugu icin sunucu
        /// tarafinda hicbir durum olusturmaz, sessizce dusulur.
        /// </summary>
        public static byte[] BuildQuicInitial(int totalLen)
        {
            if (totalLen < 1200) totalLen = 1200;
            if (totalLen > 1400) totalLen = 1400;

            var b = new List<byte>(totalLen);
            b.Add(0xc3);                                   // long header, Initial, pn_len=4
            b.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x01 });  // QUIC v1
            b.Add(8); b.AddRange(RandomBytes(8));          // DCID
            b.Add(8); b.AddRange(RandomBytes(8));          // SCID
            b.Add(0x00);                                   // token length = 0

            int remaining = totalLen - b.Count - 2;        // uzunluk alani 2 bayt varint
            if (remaining < 4) remaining = 4;
            b.AddRange(new byte[] { (byte)(0x40 | ((remaining >> 8) & 0x3f)), (byte)(remaining & 0xff) });
            b.AddRange(RandomBytes(remaining));            // pn + sifreli govde taklidi

            return b.ToArray();
        }

        // ---- kucuk yardimcilar --------------------------------------------------
        private static void AddExt(List<byte> dst, int type, byte[] data)
        {
            dst.AddRange(Be16(type));
            dst.AddRange(Be16(data.Length));
            dst.AddRange(data);
        }

        private static byte[] Be16(int v) { return new byte[] { (byte)(v >> 8), (byte)v }; }
        private static byte[] Be24(int v) { return new byte[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v }; }

        private static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }
    }
}
