// etodpx.conf ayristirmasi. Arayuzde ayar yok; bu dosya guc kullanicilari icin.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Etodpx
{
    internal sealed class Config
    {
        // --- desync ---
        public string TlsMode = "disorder";       // fake+disorder | fake+split | fake | disorder | split | off
        public string HttpMode = "disorder";
        public string QuicMode = "fake";          // fake | drop | off

        public string SplitPos = "host";          // host | midsld | 1..N (sabit ofset)
        public bool FakeBadSum = false;           // sahte paketi bozuk TCP checksum ile gonder
        public bool FakeTtl = false;              // sahte TCP paketi; hatta gore ayarlanmadan acilmaz
        public int FakeTtlValue = 4;              // hatta gore degisir - etodpx-tune ile bul
        public int FakeTtlValueV6 = 0;            // 0 = FakeTtlValue ile ayni; >0 ise IPv6 icin ayri hop siniri
        public bool FakeWrongSeq = false;         // sahte paketi pencere disi seq ile gonder
        public int FakeWrongSeqDelta = 0x10000;   // gercek seq bu kadar geriden
        public int FakeCount = 1;                 // her tur icin sahte paket sayisi
        public string FakeSni = "www.iana.org";   // sahte ClientHello icindeki zararsiz alan adi
        public bool HttpHostCase = false;         // "Host:" -> "hOSt:" (RFC uyumlu ama varsayilan kapali)

        // --- kapsam ---
        // Alan adi listesi. Dosya varsa YALNIZCA icindeki adlara dokunulur;
        // yoksa butun adlara. Bos birakilirsa liste hic kullanilmaz.
        public string HostlistPath = "hosts.txt";
        public bool EnableHttp = true;
        public bool EnableTls = true;
        public bool EnableIPv6 = true;
        public bool SkipPrivateRanges = true;

        // --- basarim ---
        public int Threads = 0;                   // 0 = otomatik
        public int QueueLength = 16384;           // maks (paket dusmesini engeller)
        public int QueueTime = 2000;              // ms
        public int QueueSize = 33554432;          // 32 MB, maks
        // Paket is parcaciklari zaten Highest onceliginde ve neredeyse hep
        // WinDivertRecv icinde bloke bekliyor. Surecin TAMAMINI yukseltmek
        // arayuzu de yukseltir ve tam ekran bir oyunla islemci yarisina sokar.
        // Varsayilan kapali; gerekirse etodpx.conf ile acilir.
        public bool HighPriority = false;
        public short DivertPriority = 0;

        // --- arayuz ---
        public bool AutoStart = true;             // uygulama acilinca korumayi baslat
        public bool MinimizeToTray = true;

        public string SourcePath = null;
        public List<string> Warnings = new List<string>();

        // IPv6 icin ozel deger verilmisse onu, yoksa ortak TTL degerini doner.
        public int EffectiveFakeTtl(bool isV6)
        {
            return isV6 && FakeTtlValueV6 > 0 ? FakeTtlValueV6 : FakeTtlValue;
        }

        public static Config Load(string path)
        {
            var c = new Config();
            c.SourcePath = path;
            if (!File.Exists(path)) return c;

            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    c.Apply(k, v);
                }
            }
            catch (Exception ex)
            {
                c.Warnings.Add("Ayar dosyası okunamadı: " + ex.Message);
            }
            return c;
        }

        // Ayarlayicinin diske yazdigi degerleri calisan uygulamaya tasir. Motor bu
        // sirada kapali; yeniden baslayinca ayni nesnedeki yeni degerleri kullanir.
        public void CopyFrom(Config other)
        {
            if (other == null) throw new ArgumentNullException("other");

            TlsMode = other.TlsMode;
            HttpMode = other.HttpMode;
            QuicMode = other.QuicMode;
            SplitPos = other.SplitPos;
            FakeBadSum = other.FakeBadSum;
            FakeTtl = other.FakeTtl;
            FakeTtlValue = other.FakeTtlValue;
            FakeTtlValueV6 = other.FakeTtlValueV6;
            FakeWrongSeq = other.FakeWrongSeq;
            FakeWrongSeqDelta = other.FakeWrongSeqDelta;
            FakeCount = other.FakeCount;
            FakeSni = other.FakeSni;
            HttpHostCase = other.HttpHostCase;

            HostlistPath = other.HostlistPath;
            EnableHttp = other.EnableHttp;
            EnableTls = other.EnableTls;
            EnableIPv6 = other.EnableIPv6;
            SkipPrivateRanges = other.SkipPrivateRanges;

            Threads = other.Threads;
            QueueLength = other.QueueLength;
            QueueTime = other.QueueTime;
            QueueSize = other.QueueSize;
            HighPriority = other.HighPriority;
            DivertPriority = other.DivertPriority;

            AutoStart = other.AutoStart;
            MinimizeToTray = other.MinimizeToTray;
            SourcePath = other.SourcePath;
            Warnings = new List<string>(other.Warnings);
        }

        private void Apply(string k, string v)
        {
            switch (k)
            {
                case "tls": case "tls_mode": TlsMode = Choice(k, v, TlsMode,
                    "fake+disorder", "fake+split", "fake", "disorder", "split", "off", "none"); break;
                case "http": case "http_mode": HttpMode = Choice(k, v, HttpMode,
                    "fake+disorder", "fake+split", "fake", "disorder", "split", "off", "none"); break;
                case "quic": case "quic_mode": QuicMode = Choice(k, v, QuicMode,
                    "fake", "drop", "off", "none"); break;
                case "split_pos": SplitPos = Split(v, SplitPos); break;
                case "fake_badsum": FakeBadSum = Bool(k, v, FakeBadSum); break;
                case "fake_ttl": FakeTtl = Bool(k, v, FakeTtl); break;
                case "fake_ttl_value": FakeTtlValue = Int(k, v, FakeTtlValue, 1, 64); break;
                case "fake_ttl_value_v6": FakeTtlValueV6 = Int(k, v, FakeTtlValueV6, 0, 64); break;
                case "fake_wrongseq": FakeWrongSeq = Bool(k, v, FakeWrongSeq); break;
                case "fake_wrongseq_delta": FakeWrongSeqDelta = Int(k, v, FakeWrongSeqDelta, 1024, 0x1000000); break;
                case "fake_count": FakeCount = Int(k, v, FakeCount, 1, 8); break;
                case "fake_sni": FakeSni = Host(v, FakeSni); break;
                case "http_host_case": HttpHostCase = Bool(k, v, HttpHostCase); break;
                case "hostlist": HostlistPath = v; break;
                case "enable_http": EnableHttp = Bool(k, v, EnableHttp); break;
                case "enable_tls": EnableTls = Bool(k, v, EnableTls); break;
                case "enable_ipv6": EnableIPv6 = Bool(k, v, EnableIPv6); break;
                case "skip_private": SkipPrivateRanges = Bool(k, v, SkipPrivateRanges); break;
                case "threads": Threads = Int(k, v, Threads, 0, 16); break;
                case "queue_length": QueueLength = Int(k, v, QueueLength, 32, 16384); break;
                case "queue_time": QueueTime = Int(k, v, QueueTime, 100, 16000); break;
                case "queue_size": QueueSize = Int(k, v, QueueSize, 65535, 33554432); break;
                case "high_priority": HighPriority = Bool(k, v, HighPriority); break;
                case "divert_priority": DivertPriority = (short)Int(k, v, DivertPriority, -30000, 30000); break;
                case "auto_start": AutoStart = Bool(k, v, AutoStart); break;
                case "minimize_to_tray": MinimizeToTray = Bool(k, v, MinimizeToTray); break;
                default: Warnings.Add("Bilinmeyen ayar yok sayıldı: " + k); break;
            }
        }

        private string Choice(string key, string v, string d, params string[] allowed)
        {
            string value = v.ToLowerInvariant();
            foreach (string a in allowed) if (value == a) return value;
            Warnings.Add("Geçersiz " + key + " değeri: " + v + " (varsayılan: " + d + ")");
            return d;
        }

        private string Split(string v, string d)
        {
            string value = v.ToLowerInvariant();
            if (value == "host" || value == "midsld") return value;
            int n;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0)
                return value;
            Warnings.Add("Geçersiz split_pos değeri: " + v + " (varsayılan: " + d + ")");
            return d;
        }

        private string Host(string v, string d)
        {
            if (v.Length <= 253 && Uri.CheckHostName(v) == UriHostNameType.Dns) return v;
            Warnings.Add("Geçersiz fake_sni değeri: " + v + " (varsayılan: " + d + ")");
            return d;
        }

        private bool Bool(string key, string v, bool d)
        {
            v = v.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on" || v == "evet") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off" || v == "hayir") return false;
            Warnings.Add("Geçersiz " + key + " değeri: " + v + " (varsayılan: " + d + ")");
            return d;
        }

        private int Int(string key, string v, int d, int lo, int hi)
        {
            int r;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
            {
                Warnings.Add("Geçersiz " + key + " değeri: " + v + " (varsayılan: " + d + ")");
                return d;
            }
            if (r < lo || r > hi)
            {
                int original = r;
                if (r < lo) r = lo;
                if (r > hi) r = hi;
                Warnings.Add(key + " sınır dışıydı: " + original + " (uygulanan: " + r + ")");
            }
            return r;
        }

    }
}
