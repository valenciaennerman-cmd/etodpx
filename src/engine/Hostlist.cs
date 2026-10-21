// Alan adi listesi: yalnizca listedeki adlara dokunulur.
//
// Liste yokken etodpx 443'e giden her ClientHello'yu boluyordu. Engelli
// olmayan bir sunucu icin bunun faydasi yok ama zarari var: DPI kutusu tuhaf
// gordugu paketi dusurunce Windows yeniden gonderiyor ve baglanti TCP zaman
// asimi merdivenini (1+2+4+8+16 sn) tirmaniyor. Oyun icinde 30 saniyelik
// donma, Discord'da yayin acilmamasi buradan geliyordu.
//
// Istege bagli alan adi bazli kural (per-host strategy):
//   ornek.com                -> genel etodpx.conf stratejisi
//   ornek.com fake+disorder  -> yalniz bu ad icin ayri kip / Secenekler
//   ornek.com disorder split_pos=midsld
//   ornek.com fake+disorder ttl=5
//   ornek.com fake+disorder badsum
//   ornek.com fake+disorder wrongseq
//
// Sicak yolda tahsis yok: dosya acilista okunup tek bir unmanaged bloga
// yaziliyor, eslesme pointer uzerinde yapiliyor.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Etodpx.Engine
{
    // Bir alan adina ozel kural bayraklari (1 bayt).
    internal static class HostFlags
    {
        public const byte None       = 0;
        public const byte HasOverride = 0x01;
        public const byte Fake       = 0x02;
        public const byte Split      = 0x04;
        public const byte Disorder   = 0x08;
        public const byte BadSum     = 0x10;
        public const byte WrongSeq   = 0x20;
        public const byte Ttl        = 0x40;
        public const byte SplitMidSld = 0x80;    // 0 = varsayilan/host, 1 = midsld (SplitPos==0 iken)
    }

    internal struct HostRule
    {
        public bool Matched;
        public byte Flags;
        public byte TtlValue;
        public byte FixedSplitPos;               // >0 ise sabit konum

        public bool HasOverride { get { return (Flags & HostFlags.HasOverride) != 0; } }
    }

    internal sealed unsafe class Hostlist : IDisposable
    {
        // Blok yerlesimi: [uzunluk:1][bayrak:1][ttl:1][splitPos:1][baytlar...] ... [0]
        private readonly IntPtr _mem;
        private readonly byte* _p;

        public int Count { get; private set; }
        public string SourcePath { get; private set; }

        private Hostlist(byte[] blob, int count, string path)
        {
            Count = count;
            SourcePath = path;
            _mem = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, _mem, blob.Length);
            _p = (byte*)_mem;
        }

        /// Dosya yoksa null doner - o zaman butun adlara dokunulur (eski davranis).
        public static Hostlist Load(string path, List<string> warnings)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            var seen = new List<string>();
            var blob = new List<byte>(256);

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                    // Ilk kelimeden sonrasi istege bagli alan adina ozel strateji.
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    // "*.site.com" ve ".site.com" ayni sey: son ek eslesmesi zaten var.
                    string host = parts[0].TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
                    if (host.Length == 0 || host.Length > 253) continue;
                    if (seen.Contains(host)) continue;

                    byte[] b = Encoding.ASCII.GetBytes(host);
                    if (b.Length == 0 || b.Length > 255) continue;

                    byte flags = HostFlags.None;
                    byte ttl = 0;
                    byte fixedPos = 0;
                    if (parts.Length > 1)
                        ParseRuleTokens(parts, 1, out flags, out ttl, out fixedPos);

                    seen.Add(host);
                    blob.Add((byte)b.Length);
                    blob.Add(flags);
                    blob.Add(ttl);
                    blob.Add(fixedPos);
                    blob.AddRange(b);
                }
            }
            catch (Exception ex)
            {
                if (warnings != null) warnings.Add("Alan adı listesi okunamadı: " + ex.Message);
                return null;
            }

            if (seen.Count == 0)
            {
                if (warnings != null)
                    warnings.Add("Alan adı listesi boş: " + path + " (bütün adlara dokunulacak)");
                return null;
            }

            blob.Add(0);
            return new Hostlist(blob.ToArray(), seen.Count, path);
        }

        private static void ParseRuleTokens(string[] parts, int start,
                                            out byte flags, out byte ttl, out byte fixedPos)
        {
            flags = HostFlags.None;
            ttl = 0;
            fixedPos = 0;
            bool modeSeen = false;

            for (int i = start; i < parts.Length; i++)
            {
                string t = parts[i].Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                if (t[0] == '#' || t[0] == ';') break;

                if (t == "fake+disorder" || t == "fake+split" || t == "fake" ||
                    t == "disorder" || t == "split" || t == "off" || t == "none")
                {
                    modeSeen = true;
                    flags |= HostFlags.HasOverride;
                    if (t.Contains("fake")) flags |= HostFlags.Fake;
                    if (t.Contains("disorder")) flags |= (byte)(HostFlags.Disorder | HostFlags.Split);
                    else if (t.Contains("split")) flags |= HostFlags.Split;
                }
                else if (t == "badsum" || t == "fake_badsum=true" || t == "fake_badsum=1")
                {
                    flags |= (byte)(HostFlags.HasOverride | HostFlags.BadSum);
                }
                else if (t == "wrongseq" || t == "fake_wrongseq=true" || t == "fake_wrongseq=1")
                {
                    flags |= (byte)(HostFlags.HasOverride | HostFlags.WrongSeq);
                }
                else if (t.StartsWith("ttl=", StringComparison.Ordinal) ||
                         t.StartsWith("fake_ttl=", StringComparison.Ordinal) ||
                         t.StartsWith("fake_ttl_value=", StringComparison.Ordinal))
                {
                    int eq = t.IndexOf('=');
                    int v;
                    if (int.TryParse(t.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) &&
                        v >= 1 && v <= 64)
                    {
                        flags |= (byte)(HostFlags.HasOverride | HostFlags.Ttl);
                        ttl = (byte)v;
                    }
                }
                else if (t == "midsld" || t == "split_pos=midsld")
                {
                    flags |= (byte)(HostFlags.HasOverride | HostFlags.SplitMidSld);
                }
                else if (t == "host" || t == "split_pos=host")
                {
                    flags |= HostFlags.HasOverride;
                    flags &= unchecked((byte)~HostFlags.SplitMidSld);
                    fixedPos = 0;
                }
                else if (t.StartsWith("pos=", StringComparison.Ordinal) ||
                         t.StartsWith("split_pos=", StringComparison.Ordinal))
                {
                    int eq = t.IndexOf('=');
                    int v;
                    if (int.TryParse(t.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) &&
                        v >= 1 && v <= 255)
                    {
                        flags |= HostFlags.HasOverride;
                        fixedPos = (byte)v;
                    }
                }
            }

            // Kullanici yalnizca "ttl=5" ya da "badsum" yazip kip yazmadiysa
            // varsayilan olarak fake+disorder kabul edilir.
            if ((flags & HostFlags.HasOverride) != 0 && !modeSeen)
            {
                bool anyFakeOpt = (flags & (HostFlags.BadSum | HostFlags.WrongSeq | HostFlags.Ttl)) != 0;
                if (anyFakeOpt)
                    flags |= (byte)(HostFlags.Fake | HostFlags.Split | HostFlags.Disorder);
                else
                    flags |= (byte)(HostFlags.Split | HostFlags.Disorder);
            }
        }

        /// <summary>
        /// Verilen alan adi listede mi. "site.com" girdisi "a.site.com" ile de
        /// eslesir; "kotusite.com" ile eslesmez - son ek nokta sinirinda olmali.
        /// </summary>
        public bool Match(byte* host, int len)
        {
            HostRule rule;
            return MatchRule(host, len, out rule);
        }

        public bool MatchRule(byte* host, int len, out HostRule rule)
        {
            rule = default(HostRule);
            if (len <= 0 || _p == null) return false;

            byte* e = _p;
            while (*e != 0)
            {
                int n = e[0];
                byte flags = e[1];
                byte ttl = e[2];
                byte splitPos = e[3];
                byte* name = e + 4;
                if (n <= len && SuffixEquals(host, len, name, n))
                {
                    // Tam esitlik ya da nokta sinirinda son ek.
                    if (n == len || host[len - n - 1] == (byte)'.')
                    {
                        rule.Matched = true;
                        rule.Flags = flags;
                        rule.TtlValue = ttl;
                        rule.FixedSplitPos = splitPos;
                        return true;
                    }
                }
                e = name + n;
            }
            return false;
        }

        // Yeni bir alan adini dosyaya guvenle ekler (zaten kapsaniyorsa tekrar eklemez).
        public static bool AddHostToFile(string path, string rawHost, out string cleanHost, out string error)
        {
            error = null;
            cleanHost = NormalizeHostInput(rawHost);
            if (string.IsNullOrEmpty(cleanHost) || Uri.CheckHostName(cleanHost) != UriHostNameType.Dns)
            {
                error = "Geçerli bir alan adı girin (örnek: site.com).";
                return false;
            }

            try
            {
                if (File.Exists(path))
                {
                    using (Hostlist existing = Load(path, null))
                    {
                        if (existing != null)
                        {
                            byte[] b = Encoding.ASCII.GetBytes(cleanHost);
                            fixed (byte* p = b)
                            {
                                if (existing.Match(p, b.Length)) return false; // zaten kapsaniyor
                            }
                        }
                    }
                }

                string line = Environment.NewLine + cleanHost + Environment.NewLine;
                File.AppendAllText(path, line, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = "hosts.txt güncellenemedi: " + ex.Message;
                return false;
            }
        }

        internal static string NormalizeHostInput(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string t = raw.Trim().Trim('"');
            int scheme = t.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) t = t.Substring(scheme + 3);
            int cut = t.IndexOfAny(new[] { '/', '?', '#', '\\', ' ', '\t' });
            if (cut >= 0) t = t.Substring(0, cut);
            int colon = t.IndexOf(':');
            if (colon > 0 && colon == t.LastIndexOf(':')) t = t.Substring(0, colon);
            t = t.TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
            if (t.StartsWith("www.", StringComparison.Ordinal) && t.Length > 4 && t.IndexOf('.', 4) > 4)
                t = t.Substring(4);
            return t;
        }

        private static bool SuffixEquals(byte* host, int hostLen, byte* name, int nameLen)
        {
            byte* h = host + (hostLen - nameLen);
            for (int i = 0; i < nameLen; i++)
            {
                byte c = h[i];
                if (c >= (byte)'A' && c <= (byte)'Z') c += 32;
                if (c != name[i]) return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (_mem != IntPtr.Zero) Marshal.FreeHGlobal(_mem);
        }
    }
}
