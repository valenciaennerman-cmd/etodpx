// Otomatik strateji bulucu.
//
// DPI kutulari operatore, sehre, hatta abonelik tipine gore farkli davraniyor;
// evrensel bir "dogru ayar" yok. Bu arac stratejileri tek tek deneyip her
// biriyle gercek TLS el sikismasi yapiyor (sertifika dogrulamasi acik, yani
// engel sayfasi basari sayilmiyor) ve kazanani etodpx.conf'a yaziyor.
//
// Yonetici gerekiyor. Calisan etodpx varsa once durduruluyor.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Etodpx;
using Etodpx.Engine;

namespace Etodpx.Tune
{
    internal sealed class Strategy
    {
        public string Name;
        public string Tls = "fake+disorder";
        public string SplitPos = "midsld";
        public bool BadSum;
        public bool WrongSeq;
        public bool Ttl;
        public int TtlValue = 5;

        public void ApplyTo(Config c)
        {
            c.TlsMode = Tls;
            c.HttpMode = Tls;
            c.SplitPos = SplitPos;
            c.FakeBadSum = BadSum;
            c.FakeWrongSeq = WrongSeq;
            c.FakeTtl = Ttl;
            c.FakeTtlValue = TtlValue;
        }

        public string ConfLines()
        {
            var sb = new StringBuilder();
            sb.AppendLine("tls  = " + Tls);
            sb.AppendLine("http = " + Tls);
            sb.AppendLine("split_pos = " + SplitPos);
            sb.AppendLine("fake_badsum = " + (BadSum ? "true" : "false"));
            sb.AppendLine("fake_wrongseq = " + (WrongSeq ? "true" : "false"));
            sb.AppendLine("fake_ttl = " + (Ttl ? "true" : "false"));
            sb.AppendLine("fake_ttl_value = " + TtlValue);
            return sb.ToString();
        }
    }

    internal static class Tuner
    {
        private static string[] _targets;
        // Dogrulama derinligi: kazanan ayar bu kadar denemeyi gecmek zorunda.
        private const int TriesPerTarget = 3;
        // Tarama derinligi. Sureyi belirleyen sey calismayan stratejiler: basarisiz
        // bir el sikismasi zaman asimina kadar (~4 sn) suruyor. Tarama tek denemeyle
        // genis geciyor, yalnizca kazanan aday tam derinlikte dogrulaniyor.
        private const int ScanTries = 1;
        private static bool _auto;
        private static bool _manageApp = true;
        private static bool _etodpxWasRunning;
        private static string _resultCode;
        private static readonly StringBuilder _log = new StringBuilder();

        private static void W(string s) { Console.Write(s); _log.Append(s); }
        private static void WL(string s) { Console.WriteLine(s); _log.AppendLine(s); }
        private static void WL() { Console.WriteLine(); _log.AppendLine(); }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "etodpx - strateji bulucu";

            var rest = new List<string>();
            foreach (var a in args)
            {
                if (a == "--auto" || a == "-a") _auto = true;
                else if (a == "--no-manage-app") _manageApp = false;
                else rest.Add(a);
            }
            if (rest.Count == 0)
                rest.AddRange(new[] { "discord.com", "cdn.discordapp.com", "x.com", "roblox.com" });

            // Kullanici tam adres yapistirabilir; alan adini ayikla.
            var cleaned = new List<string>();
            foreach (var t in rest)
            {
                string hostOnly = ToHostName(t);
                if (hostOnly.Length == 0) continue;
                if (hostOnly != t) WL("  not: \"" + t + "\" -> " + hostOnly);
                if (!cleaned.Contains(hostOnly)) cleaned.Add(hostOnly);
            }
            if (cleaned.Count == 0)
            {
                WL("  Gecerli bir alan adi verilmedi.");
                WL("  Ornek: etodpx-tune.exe discord.com");
                SetResult("INVALID_TARGET");
                Pause();
                return 1;
            }
            _targets = cleaned.ToArray();

            WL();
            WL("  etodpx strateji bulucu");
            WL("  =====================");
            WL("  hedefler : " + string.Join(", ", _targets));
            WL();

            if (_manageApp)
            {
                if (!StopRunningEtodpx())
                {
                    SetResult("APP_BUSY");
                    return Finish(1);
                }
            }
            else ReportOtherCopies();

            if (!Preflight())
            {
                if (_resultCode == null) SetResult("PREFLIGHT_FAILED");
                Pause();
                return Finish(3);
            }

            var strategies = BuildStrategies();

            WL("  " + strategies.Count + " strateji denenecek.");
            WL("  Once etodpx KAPALI iken olcum aliniyor...");
            WL();

            bool ignoredAbort;
            int total = _targets.Length * ScanTries;
            int confirmTotal = _targets.Length * TriesPerTarget;
            int baseline = RunTests(ScanTries, -1, out ignoredAbort);
            WL("  temel durum (koruma yok) : " + baseline + "/" + total);
            WL();

            if (baseline == total)
            {
                WL("  Hedefler korumasiz da aciliyor - burada asilacak bir engel yok.");
                WL("  Farkli bir hedef verip tekrar dene:");
                WL("    etodpx-tune.exe engellisite.com");
                WL();
                SetResult("ALREADY_OPEN");
                Pause();
                return Finish(0);
            }

            var results = new List<KeyValuePair<Strategy, int>>();
            var cutShort = new List<Strategy>();
            var rejected = new List<Strategy>();
            Strategy best = null;
            int bestScore = baseline;
            bool confirmed = false;

            foreach (var s in strategies)
            {
                W(("  " + s.Name).PadRight(42));
                bool stopped;
                int score = TryStrategy(s, ScanTries, bestScore, out stopped);
                if (stopped) cutShort.Add(s);
                results.Add(new KeyValuePair<Strategy, int>(s, score));

                string bar = new string('#', score) + new string('.', total - score);
                WL(bar + "  " + score + "/" + total +
                   (stopped ? "  (erken kesildi)" : string.Empty));
                FlushLog();                              // ilerleme disaridan izlenebilsin

                if (score > bestScore) { bestScore = score; best = s; }

                // Liste en guvenliden agresife sirali; bir strateji tum hedefleri
                // acinca gerisini taramak sadece bekletir. Ama once dogrula.
                if (score != total) continue;

                if (Confirm(s, confirmTotal) == confirmTotal)
                {
                    confirmed = true;
                    WL("  Tam uyum dogrulandi; kalan denemeler atlandi.");
                    break;
                }

                // Taramadaki tam puan sansliymis; en iyiyi kalanlardan yeniden sec.
                WL("  Dogrulama gecmedi; taramaya devam ediliyor.");
                rejected.Add(s);
                best = null;
                bestScore = baseline;
                foreach (var kv in results)
                {
                    if (rejected.Contains(kv.Key)) continue;
                    if (kv.Value > bestScore) { bestScore = kv.Value; best = kv.Key; }
                }
            }

            // Liste sonuna kadar tarandiysa kazanan henuz tam derinlikte olculmedi.
            // Yarim yamalak calisan bir ayari yazmamak icin burada da dogruluyoruz.
            if (best != null && !confirmed && Confirm(best, confirmTotal) * 2 <= confirmTotal)
            {
                WL("  Kazanan aday dogrulamayi gecemedi.");
                best = null;
            }

            WL();
            WL("  --- siralama (esitlikte en guvenli once) ---");
            // Esitlikte deneme sirasi belirleyici: liste sahte paketi sunucuya
            // ulastiramayacak yontemlerden basliyor, TTL'ler en dusukten artiyor.
            // Secim de bunu izliyor, gosterim de ayni sirayi kullanmali.
            results.Sort(delegate (KeyValuePair<Strategy, int> a, KeyValuePair<Strategy, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                if (c != 0) return c;
                return strategies.IndexOf(a.Key).CompareTo(strategies.IndexOf(b.Key));
            });
            // Erken kesilenin puani bir alt sinir; gercek degeri olculmedi.
            foreach (var kv in results)
                WL("   " + (cutShort.Contains(kv.Key) ? "<" : " ") + kv.Value + "/" + total +
                   "  " + kv.Key.Name + (rejected.Contains(kv.Key) ? "  (dogrulama gecmedi)" : string.Empty));
            if (cutShort.Count > 0)
                WL("   ( < : kazanani gecemeyecegi anlasilinca kesildi )");
            WL();

            if (best == null)
            {
                if (rejected.Count > 0)
                {
                    // Taramada acti, tekrarinda acmadi. Bunu IP engeli diye
                    // raporlamak yanlis olur.
                    WL("  Bazi stratejiler taramada acti ama tekrar olcumde tutmadi.");
                    WL("  Baglanti kararsiz olabilir; araci bir kez daha calistir.");
                }
                else
                {
                    WL("  Hicbir strateji temel durumu gecemedi.");
                    WL("  Bu hedef muhtemelen IP seviyesinde engelli; DPI atlatma yetmez.");
                }
                WL();
                SetResult("NO_STRATEGY");
                Pause();
                return Finish(2);
            }

            WL("  KAZANAN : " + best.Name + "  (" + bestScore + "/" + total + ")");
            WL();
            string ans = null;
            if (!_auto)
            {
                W("  etodpx.conf dosyasina yazilsin mi? [E/h] ");
                ans = Console.ReadLine();
            }
            if (ans != null && ans.Trim().ToLowerInvariant().StartsWith("h"))
            {
                WL();
                WL("  Yazilmadi. Elle eklemek istersen:");
                WL();
                WL(best.ConfLines());
                SetResult("NOT_WRITTEN");
                Pause();
                return Finish(0);
            }

            WriteConf(best);
            WL();
            WL("  Yazildi. etodpx'i yeniden baslat.");
            WL();
            SetResult("CONFIG_WRITTEN");
            Pause();
            return Finish(0);
        }

        // "https://www.site.com/a/b?x=1" -> "www.site.com"

        internal static string ToHostName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string t = raw.Trim().Trim('"');

            int scheme = t.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) t = t.Substring(scheme + 3);

            int cut = t.IndexOfAny(new[] { '/', '?', '#', (char)92 });   // 92 = ters boluk
            int at = t.IndexOf('@');                  // kullanici:parola@
            // @ yalnizca authority bolumundeyse kullanici bilgisi; /channels/@me
            // gibi bir yol parcasi alan adini "me"ye cevirmemeli.
            if (at >= 0 && (cut < 0 || at < cut))
            {
                t = t.Substring(at + 1);
                cut = t.IndexOfAny(new[] { '/', '?', '#', (char)92 });
            }

            if (cut >= 0) t = t.Substring(0, cut);

            if (t.StartsWith("[", StringComparison.Ordinal))   // IPv6 hazir bulunsun
            {
                int close = t.IndexOf(']');
                if (close > 0) return t.Substring(1, close - 1);
            }

            // Port ancak tek iki nokta varsa kirpilir; birden fazlaysa koseli
            // parantezsiz bir IPv6 adresidir ve kirpmak onu bozar.
            int first = t.IndexOf(':');
            if (first > 0 && first == t.LastIndexOf(':')) t = t.Substring(0, first);

            return t.Trim().TrimEnd('.').ToLowerInvariant();
        }

        // --- on teshis

        private enum Diag { Open, NoDns, DnsPoisoned, IpBlocked, SniBlocked, Unreachable }

        // Engelin cinsini belirler. etodpx yalnizca SNI engelini asabiliyor; DNS
        // ya da IP engelinde butun stratejileri denemenin anlami yok.
        private static bool Preflight()
        {
            WL("  --- on teshis ---");
            bool anySni = false;
            bool anyOpen = false;
            bool anyNoDns = false;
            bool anyIpBlocked = false;
            var scanTargets = new List<string>();
            _dnsPoisoned = false;
            _dnsCompareFailed = false;
            _dnsResolutionFailed = false;

            foreach (string host in _targets)
            {
                Diag d = Diagnose(host);
                if (d == Diag.DnsPoisoned) _dnsPoisoned = true;
                if (d == Diag.NoDns) anyNoDns = true;
                if (d == Diag.IpBlocked) anyIpBlocked = true;
                if (d == Diag.Open) anyOpen = true;
                string msg;
                switch (d)
                {
                    case Diag.NoDns:
                        msg = "alan adi cozulemiyor - DNS'te boyle bir kayit yok";
                        break;
                    case Diag.DnsPoisoned:
                        msg = "DNS ZEHIRLI - once DNS'i duzelt, etodpx bunu asamaz";
                        break;
                    case Diag.IpBlocked:
                        // DNS karsilastirmasi yapilamadiysa bu teshis guvenilmez:
                        // sistem DNS'i sahte adres vermis olabilir ve TCP o yuzden
                        // kurulmuyordur. Gizlersek kullanici bosuna VPN arar.
                        msg = _dnsCompareFailed
                            ? "TCP baglantisi kurulmuyor - AMA DNS dogrulanamadi (asagiya bak)"
                            : "IP seviyesinde engel - TCP baglantisi bile kurulmuyor, etodpx asamaz";
                        break;
                    case Diag.Open:
                        msg = "zaten aciliyor - burada asilacak engel yok";
                        break;
                    case Diag.SniBlocked:
                        msg = "SNI engeli - etodpx bunu asabilir";
                        anySni = true;
                        scanTargets.Add(host);
                        break;
                    default:
                        msg = "sunucuya ulasiliyor ama TLS kurulamiyor (sebep belirsiz)";
                        anySni = true;   // yine de denemeye deger
                        scanTargets.Add(host);
                        break;
                }
                WL(("  " + host).PadRight(32) + msg);

                // Teshisin dayanagini kullanici gorsun.
                try
                {
                    var ips = System.Net.Dns.GetHostAddresses(host);
                    var v4 = new List<string>();
                    foreach (var a in ips)
                        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            v4.Add(a.ToString());
                    if (v4.Count > 0)
                        WL("".PadRight(34) + "cozumlenen adres: " + string.Join(", ", v4.ToArray()));
                }
                catch { }
            }
            WL();

            if (_dnsCompareFailed)
            {
                WL("  UYARI: 1.1.1.1 ve 8.8.8.8'e ne dogrudan ne de DoH ile ulasilabildi.");
                WL("  Agin dis DNS sunucularini engelliyor olabilir. Bu durumda sistem");
                WL("  DNS'inin dogru cevap verip vermedigini DOGRULAYAMIYORUZ.");
                WL();
                WL("  Yukaridaki 'cozumlenen adres' satirina bak:");
                WL("    195.175.254.2  -> engel sayfasi, DNS zehirli");
                WL("    0.0.0.0        -> DNS bilerek bosa dusurmus");
                WL("    162.159.x.x    -> gercek adres, DNS temiz");
                WL();
                WL("  DNS zehirliyse duz DNS degistirmek ise yaramaz (UDP 53 kapali).");
                WL("  DNS over HTTPS gerekir:");
                WL("    Ayarlar > Ag ve Internet > Wi-Fi > Donanim ozellikleri >");
                WL("    DNS sunucusu atamasi > Duzenle > El ile > IPv4 acik >");
                WL("    Tercih edilen: 1.1.1.1  ve altindaki 'DNS over HTTPS' = Acik");
                WL();
            }

            if (_dnsPoisoned)
            {
                WL("  ISS'inin DNS sunucusu bu alan adi icin sahte bir adres donduruyor.");
                WL("  etodpx paketleri parcalar; DNS seni bastan yanlis sunucuya");
                WL("  yollarsa yapabilecegi bir sey yoktur. Once DNS'i degistir:");
                WL();
                WL("    Ayarlar > Ag ve Internet > (Ethernet veya Wi-Fi) > DNS sunucusu atamasi");
                WL("    Duzenle > El ile > IPv4 acik > Tercih edilen: 1.1.1.1  Diger: 1.0.0.1");
                WL();
                WL("  Ya da yonetici komut isteminde:");
                WL("    netsh interface ip set dns name=\"Ethernet\" static 1.1.1.1");
                WL("    netsh interface ip add dns name=\"Ethernet\" 1.0.0.1 index=2");
                WL();
                WL("  Birden fazla ag baglantin varsa DOGRU olanina yazdigindan emin ol.");
                WL("  Sonra bu araci tekrar calistir.");
                WL();
                SetResult("DNS_POISONED");
                return false;
            }

            if (!anySni)
            {
                WL("  Taranacak bir SNI engeli bulunamadi, strateji taramasi atlaniyor.");
                WL("  Gercek ve engelli bir site adiyla tekrar dene, ornegin:");
                WL("    etodpx-tune.exe discord.com");
                WL();
                if (_dnsResolutionFailed || anyNoDns) SetResult("DNS_UNAVAILABLE");
                else if (_dnsCompareFailed) SetResult("DNS_UNVERIFIED");
                else if (anyOpen) SetResult("ALREADY_OPEN");
                else if (anyIpBlocked) SetResult("IP_BLOCKED");
                else SetResult("NO_SNI");
                return false;
            }

            // Tarama yalnizca DPI ile asilabilecek hedefler uzerinden yapiliyor.
            // Zaten acilan hedef her stratejide basarili olup puani sisiriyor;
            // IP seviyesinde engelli olan ise hicbirinde acilmadigi icin tam puan
            // asla yakalanmiyor, erken cikis devreye girmiyor ve butun liste
            // bosuna taraniyor.
            if (scanTargets.Count != _targets.Length)
            {
                WL("  Taranacak hedefler : " + string.Join(", ", scanTargets.ToArray()));
                WL("  (digerleri DPI ile asilabilir cinsten degil, olcume katilmiyor)");
                WL();
                _targets = scanTargets.ToArray();
            }
            return true;
        }

        private static bool _dnsPoisoned;
        private static bool _dnsResolutionFailed;

        private static Diag Diagnose(string host)
        {
            System.Net.IPAddress[] ips;
            try
            {
                ips = System.Net.Dns.GetHostAddresses(host);
                if (ips.Length == 0) return Diag.NoDns;
            }
            catch
            {
                // Sistem DNS'i tamamen olu olabilir. Bagimsiz bir DoH cevabi kaydin
                // gercekten var oldugunu gosterir; buna "yazim hatasi" demek yanlis.
                var independent = ResolveViaDoh(host, "https://1.1.1.1/dns-query");
                if (independent.Count > 0) _dnsResolutionFailed = true;
                else _dnsCompareFailed = true;
                return Diag.NoDns;
            }

            foreach (var ip in ips)
                if (IsKnownPoisonAddress(ip)) return Diag.DnsPoisoned;

            // Olcutun tamami IsDnsPoisoned icinde.
            if (IsDnsPoisoned(host, ips)) return Diag.DnsPoisoned;

            if (!CanConnect(host)) return Diag.IpBlocked;
            if (Handshake(host)) return Diag.Open;

            // Ayni sunucuya zararsiz bir SNI ile ulasiliyorsa engelin tek dayanagi
            // alan adi metnidir.
            if (HandshakeWithSni(ips[0].ToString(), "example.com")) return Diag.SniBlocked;
            return Diag.Unreachable;
        }

        // Referans cozumleyicilere hic ulasilamadi mi. Bazi aglar UDP 53'u kendi
        // DNS'i disinda kapatiyor; o zaman "zehirli degil" demek yanlis olur.
        private static bool _dnsCompareFailed;

        internal static bool IsKnownPoisonAddress(System.Net.IPAddress ip)
        {
            string value = ip.ToString();
            return value == "195.175.254.2" ||
                   value.Equals("2a01:358:4014:a00::3", StringComparison.OrdinalIgnoreCase) ||
                   value == "0.0.0.0" || value == "::";
        }

        // Tek referansla karsilastirmak yaniltici: CDN'ler her cozumleyiciye
        // havuzdan farkli adres dondurebiliyor ve bu masum fark zehirli gibi
        // gorunuyor. Iki bagimsiz referans soruluyor, bayrak yalnizca ikisi
        // birbiriyle ortak adres veriyorsa VE sistemin verdigi adreslerin hicbiri
        // o kumede degilse kalkiyor. Referanslar tutmuyorsa bir sey iddia etmiyoruz.
        private static bool IsDnsPoisoned(string host, System.Net.IPAddress[] systemIps)
        {
            var r1 = ResolveVia("1.1.1.1", host);
            var r2 = ResolveVia("8.8.8.8", host);

            // UDP 53 kapaliysa DoH'a dus. DoH de engellenebilir, o yuzden
            // basarisizlik "DNS temiz" demek degil.
            if (r1.Count == 0) r1 = ResolveViaDoh(host, "https://1.1.1.1/dns-query");
            if (r2.Count == 0) r2 = ResolveViaDoh(host, "https://dns.google/resolve");

            if (r1.Count == 0 || r2.Count == 0)
            {
                _dnsCompareFailed = true;
                return false;
            }

            bool referencesAgree = false;
            foreach (var a in r1)
                if (r2.Contains(a)) { referencesAgree = true; break; }
            if (!referencesAgree) return false;          // adres zaten degisken

            var sys = new List<string>();
            foreach (var a in systemIps)
                if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    sys.Add(a.ToString());
            if (sys.Count == 0) return false;

            foreach (var a in sys)
                if (r1.Contains(a) || r2.Contains(a)) return false;   // ortak adres var

            return true;
        }

        // Cloudflare ve Google ayni JSON bicimini donduruyor:
        // {"Answer":[{"data":"1.2.3.4"}]}
        private static List<string> ResolveViaDoh(string host, string endpoint)
        {
            var result = new List<string>();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string url = endpoint + "?name=" + Uri.EscapeDataString(host) + "&type=A";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Accept = "application/dns-json";
                req.UserAgent = "etodpx";
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    string body = sr.ReadToEnd();
                    // Tam JSON ayristiricisi gereksiz; "data":"<ipv4>" ariyoruz.
                    const string key = "\"data\":\"";
                    int i = 0;
                    while ((i = body.IndexOf(key, i, StringComparison.Ordinal)) >= 0)
                    {
                        i += key.Length;
                        int end = body.IndexOf('"', i);
                        if (end < 0) break;
                        string val = body.Substring(i, end - i);
                        System.Net.IPAddress ip;
                        if (System.Net.IPAddress.TryParse(val, out ip) &&
                            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            result.Add(val);
                        i = end;
                    }
                }
            }
            catch { /* DoH da kapaliysa karsilastirma yapilamaz */ }
            return result;
        }

        // Sistem cozumleyicisini atlayip sunucuya dogrudan sorar.
        private static List<string> ResolveVia(string server, string host)
        {
            var result = new List<string>();
            UdpClient u = null;
            try
            {
                byte[] q = BuildDnsQuery(host);
                u = new UdpClient();
                u.Client.ReceiveTimeout = 2500;
                u.Send(q, q.Length, server, 53);
                var ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] r = u.Receive(ref ep);
                ParseDnsAnswers(r, result);
            }
            catch { /* karsilastirma yapilamadi; teshis diger adimlarla surer */ }
            finally { if (u != null) { try { u.Close(); } catch { } } }
            return result;
        }

        private static byte[] BuildDnsQuery(string host)
        {
            var b = new List<byte>(64);
            b.Add(0x7A); b.Add(0x5C);                 // islem kimligi
            b.Add(0x01); b.Add(0x00);                 // standart sorgu, ozyineleme istenir
            b.AddRange(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 });   // 1 soru
            foreach (var label in host.Split('.'))
            {
                if (label.Length == 0 || label.Length > 63) continue;
                b.Add((byte)label.Length);
                b.AddRange(Encoding.ASCII.GetBytes(label));
            }
            b.Add(0);
            b.AddRange(new byte[] { 0, 1, 0, 1 });    // tip A, sinif IN
            return b.ToArray();
        }

        private static void ParseDnsAnswers(byte[] r, List<string> into)
        {
            if (r.Length < 12) return;
            int qd = (r[4] << 8) | r[5];
            int an = (r[6] << 8) | r[7];
            int i = 12;

            for (int k = 0; k < qd && i < r.Length; k++)
            {
                i = SkipName(r, i);
                i += 4;                                // tip + sinif
            }

            for (int k = 0; k < an && i + 10 <= r.Length; k++)
            {
                i = SkipName(r, i);
                if (i + 10 > r.Length) return;
                int type = (r[i] << 8) | r[i + 1];
                int rdlen = (r[i + 8] << 8) | r[i + 9];
                i += 10;
                if (i + rdlen > r.Length) return;
                if (type == 1 && rdlen == 4)
                    into.Add(r[i] + "." + r[i + 1] + "." + r[i + 2] + "." + r[i + 3]);
                i += rdlen;
            }
        }

        // Sikistirma isaretcisi (0xC0) iki bayt.
        private static int SkipName(byte[] r, int i)
        {
            while (i < r.Length)
            {
                int len = r[i];
                if (len == 0) return i + 1;
                if ((len & 0xC0) == 0xC0) return i + 2;
                i += len + 1;
            }
            return i;
        }

        private static bool CanConnect(string host)
        {
            var c = new TcpClient();
            try
            {
                var ar = c.BeginConnect(host, 443, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(4000)) return false;
                c.EndConnect(ar);
                return true;
            }
            catch { return false; }
            finally { try { c.Close(); } catch { } }
        }

        // Sertifika dogrulamasi kapali: burada soru el sikismanin tamamlanip
        // tamamlanmadigi, kimin cevap verdigi degil.
        private static bool HandshakeWithSni(string ip, string sni)
        {
            TcpClient c = null;
            SslStream ssl = null;
            try
            {
                c = new TcpClient();
                var ar = c.BeginConnect(ip, 443, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(4000)) return false;
                c.EndConnect(ar);
                var ns = c.GetStream();
                ns.ReadTimeout = 3500; ns.WriteTimeout = 3500;
                ssl = new SslStream(ns, false, delegate { return true; });
                ssl.AuthenticateAsClient(sni);
                return ssl.IsAuthenticated;
            }
            catch { return false; }
            finally
            {
                if (ssl != null) { try { ssl.Dispose(); } catch { } }
                if (c != null) { try { c.Close(); } catch { } }
            }
        }

        // --- stratejiler

        private static List<Strategy> BuildStrategies()
        {
            var l = new List<Strategy>();

            // Sunucuya asla ulasmayan sahte paketler (guvenli olanlar) once.
            l.Add(new Strategy { Name = "badsum + midsld disorder", BadSum = true });
            l.Add(new Strategy { Name = "wrongseq + midsld disorder", WrongSeq = true });
            l.Add(new Strategy { Name = "badsum + wrongseq + disorder", BadSum = true, WrongSeq = true });
            l.Add(new Strategy { Name = "badsum + midsld split", BadSum = true, Tls = "fake+split" });
            l.Add(new Strategy { Name = "wrongseq + host disorder", WrongSeq = true, SplitPos = "host" });
            l.Add(new Strategy { Name = "badsum + pos 1 disorder", BadSum = true, SplitPos = "1" });
            l.Add(new Strategy { Name = "badsum + pos 2 disorder", BadSum = true, SplitPos = "2" });

            // Sahte paket olmadan sadece bolme.
            l.Add(new Strategy { Name = "sadece disorder (midsld)", Tls = "disorder" });
            l.Add(new Strategy { Name = "sadece split (midsld)", Tls = "split" });
            l.Add(new Strategy { Name = "sadece disorder (pos 1)", Tls = "disorder", SplitPos = "1" });

            // TTL varyantlari: paketleri yeniden birlestiren DPI kutularina karsi
            // en etkili yontem. Sahte paket gecerli oldugu icin DPI kabul ediyor,
            // TTL yuzunden sunucuya varmadan oluyor. Deger hatta gore degistigi
            // icin hepsini deniyoruz.
            foreach (int t in new[] { 1, 2, 3, 4, 5, 6, 7 })
            {
                l.Add(new Strategy { Name = "ttl " + t + " + host disorder", Ttl = true, TtlValue = t, SplitPos = "host" });
                l.Add(new Strategy { Name = "ttl " + t + " + midsld disorder", Ttl = true, TtlValue = t, SplitPos = "midsld" });
                // bolmeden, sadece sahte paket (zapret'in duz "fake" kipi)
                l.Add(new Strategy { Name = "ttl " + t + " (bolmesiz)", Ttl = true, TtlValue = t, Tls = "fake" });
            }

            return l;
        }

        // Kalan denemelerin hepsi gecse bile bestScore asilamiyorsa bu strateji
        // kazanamaz. Kazanmak icin en iyiyi gecmek gerek, esitlik yetmez - o yuzden
        // karsilastirma "<=". Ayri metot: buradaki bir off-by-one calisan bir
        // stratejiyi sessizce eler ve kullaniciya "engel asilamiyor" dedirtir.
        internal static bool CannotWin(int ok, int remaining, int bestScore)
        {
            return ok + remaining <= bestScore;
        }

        // Tarama hedef basina tek deneme yaptigi icin sansli tek bir el sikismasi
        // calismayan bir stratejiyi kazanan gosterebiliyor.
        private static int Confirm(Strategy s, int confirmTotal)
        {
            W("  dogrulaniyor".PadRight(42));
            bool ignored;
            int score = TryStrategy(s, TriesPerTarget, -1, out ignored);
            WL(score + "/" + confirmTotal);
            FlushLog();
            return score;
        }

        private static int TryStrategy(Strategy s, int tries, int giveUpAtOrBelow, out bool aborted)
        {
            aborted = false;
            var cfg = new Config();
            cfg.QuicMode = "drop";        // olcum sirasinda QUIC yolu devre disi kalsin
            cfg.Threads = 2;
            s.ApplyTo(cfg);

            var eng = new DpiEngine(cfg);
            try
            {
                eng.Start();
                Thread.Sleep(250);        // surucu otursun
                return RunTests(tries, giveUpAtOrBelow, out aborted);
            }
            catch (Exception ex)
            {
                W("(motor hatasi: " + ex.Message + ") ");
                return 0;
            }
            finally
            {
                try { eng.Stop(); } catch { }
                Thread.Sleep(400);        // DPI durum tablosu sogusun
            }
        }

        // --- test

        // giveUpAtOrBelow: o ana kadarki en iyi puan. Asilamayacagi anlasilinca
        // devam etmek sonucu degistirmiyor, yalnizca bekletiyor. -1 verilirse hic
        // vazgecilmez; temel olcum ve dogrulama icin sart.
        private static int RunTests(int tries, int giveUpAtOrBelow, out bool aborted)
        {
            aborted = false;
            int ok = 0;
            int remaining = _targets.Length * tries;
            foreach (string host in _targets)
                for (int i = 0; i < tries; i++)
                {
                    if (Handshake(host)) ok++;
                    remaining--;
                    if (CannotWin(ok, remaining, giveUpAtOrBelow)) { aborted = true; return ok; }
                }
            return ok;
        }

        // Sertifika adi ve zinciri dogrulaniyor: engel sayfasi ya da araya giren
        // bir sunucu basari sayilmasin.
        private static bool Handshake(string host)
        {
            TcpClient c = null;
            SslStream ssl = null;
            try
            {
                c = new TcpClient();
                var ar = c.BeginConnect(host, 443, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(4000)) return false;
                c.EndConnect(ar);

                // DPI baglantiyi sessizce yutarsa el sikisma TCP yeniden gonderim
                // zaman asimini (20+ sn) beklerdi.
                var ns = c.GetStream();
                ns.ReadTimeout = 3500;
                ns.WriteTimeout = 3500;
                c.ReceiveTimeout = 3500;
                c.SendTimeout = 3500;

                ssl = new SslStream(ns, false, ValidateStrict);
                ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                return ssl.IsAuthenticated;
            }
            catch { return false; }
            finally
            {
                if (ssl != null) { try { ssl.Dispose(); } catch { } }
                if (c != null) { try { c.Close(); } catch { } }
            }
        }

        private static bool ValidateStrict(object sender, X509Certificate cert,
                                           X509Chain chain, SslPolicyErrors errors)
        {
            return errors == SslPolicyErrors.None;
        }

        // --- yardimci

        // Arayuz uygulamayi kendisi yonetiyorsa ayarlayici ana kopyayi kapatmaz.
        // Baska klasorden acik bir surum varsa makine-okunur bir kayit birakir.
        private static void ReportOtherCopies()
        {
            string dir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string expected = Path.GetFullPath(Path.Combine(dir, "etodpx.exe"));

            foreach (var p in Process.GetProcessesByName("etodpx"))
            {
                try
                {
                    string actual = Path.GetFullPath(p.MainModule.FileName);
                    if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) continue;
                    ReportOtherCopy(actual);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        private static void ReportOtherCopy(string path)
        {
            WL("  DIKKAT: baska klasorden calisan etodpx: " + path);
            WL("ETODPX_OTHER_COPY_B64=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));
        }

        private static bool StopRunningEtodpx()
        {
            var procs = Process.GetProcessesByName("etodpx");
            if (procs.Length == 0) return true;

            string dir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string expected = Path.GetFullPath(Path.Combine(dir, "etodpx.exe"));
            var matching = new List<Process>();
            var elsewhere = new List<string>();
            foreach (var p in procs)
            {
                try
                {
                    string actual = Path.GetFullPath(p.MainModule.FileName);
                    if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) matching.Add(p);
                    else { elsewhere.Add(actual); p.Dispose(); }
                }
                catch { p.Dispose(); }
            }

            // Ayari buraya yaziyoruz ama kullanici oteki kopyaya bakiyor olabilir.
            // Sessiz kalirsak "kazanan bulundu, hicbir sey degismedi" durumu olusur.
            if (elsewhere.Count > 0)
            {
                WL();
                WL("  DIKKAT: baska bir klasorden calisan etodpx var:");
                foreach (var path in elsewhere) WL("    " + path);
                WL();
                WL("  Ayari BU klasore yazacagim:");
                WL("    " + Path.Combine(dir, "etodpx.conf"));
                WL();
                WL("  Yukaridaki kopyayi kullaniyorsan ayar ona islemez. Fazla");
                WL("  kopyalari silip tek klasorden calis, yoksa hangisinin");
                WL("  ayarlandigi surekli karisir.");
                WL();
                foreach (var path in elsewhere)
                    WL("ETODPX_OTHER_COPY_B64=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));
            }

            if (matching.Count == 0) return true;

            _etodpxWasRunning = true;

            WL("  Calisan etodpx bulundu - olcum icin durduruluyor.");
            foreach (var p in matching)
            {
                try
                {
                    bool exited = p.CloseMainWindow() && p.WaitForExit(3000);
                    if (!exited)
                    {
                        p.Kill();
                        exited = p.WaitForExit(4000);
                    }
                    if (!exited) throw new InvalidOperationException("Süreç zamanında kapanmadı.");
                }
                catch (Exception ex)
                {
                    WL("  etodpx durdurulamadi: " + ex.Message);
                    WL("  Once etodpx'i elle kapat, sonra tekrar dene.");
                    Pause();
                    return false;
                }
                finally { p.Dispose(); }
            }
            Thread.Sleep(600);
            WL();
            return true;
        }

        private static int Finish(int code)
        {
            if (_resultCode == null) SetResult(code == 0 ? "OK" : "FAILED");
            if (_manageApp) RestartEtodpxIfNeeded();
            return code;
        }

        private static void SetResult(string code)
        {
            if (_resultCode != null) return;
            _resultCode = code;
            WL("ETODPX_RESULT=" + code);
        }

        private static void RestartEtodpxIfNeeded()
        {
            if (!_etodpxWasRunning) return;
            _etodpxWasRunning = false;
            try
            {
                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string exe = Path.Combine(dir, "etodpx.exe");
                if (!File.Exists(exe))
                {
                    WL("  UYARI: etodpx.exe bulunamadigi icin yeniden baslatilamadi.");
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });
                WL("  etodpx yeniden baslatildi.");
            }
            catch (Exception ex)
            {
                WL("  UYARI: etodpx yeniden baslatilamadi: " + ex.Message);
            }
        }

        private const string TuneHeader = "# --- etodpx-tune tarafindan bulundu: ";

        private static void WriteConf(Strategy best)
        {
            string dir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(dir, "etodpx.conf");

            var keep = new List<string>();
            var drop = new[] { "tls", "http", "split_pos", "fake_badsum",
                               "fake_wrongseq", "fake_ttl", "fake_ttl_value" };

            if (File.Exists(path))
            {
                // Onceki turun basligi da gitmeli. Ayar satirlari degisiyordu ama
                // yorumlar kaliyor, dosya her turda bir baslik daha topluyordu.
                bool afterHeader = false;
                foreach (var line in File.ReadAllLines(path))
                {
                    string t = line.TrimStart();
                    if (t.StartsWith(TuneHeader, StringComparison.Ordinal)) { afterHeader = true; continue; }
                    if (afterHeader)
                    {
                        afterHeader = false;
                        if (t.StartsWith("# --- ", StringComparison.Ordinal)) continue;   // tarih satiri
                    }

                    int eq = t.IndexOf('=');
                    if (eq > 0 && t.Length > 0 && t[0] != '#' && t[0] != ';')
                    {
                        string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                        if (Array.IndexOf(drop, k) >= 0) continue;   // eskisini at
                    }
                    keep.Add(line);
                }

                // Ayirici bos satir da gitmeli, yoksa dosya her turda bir satir uzar.
                while (keep.Count > 0 && keep[keep.Count - 1].Trim().Length == 0)
                    keep.RemoveAt(keep.Count - 1);
            }

            keep.Add("");
            keep.Add(TuneHeader + best.Name);
            keep.Add("# --- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            foreach (var l in best.ConfLines().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                keep.Add(l);

            File.WriteAllLines(path, keep.ToArray(), new UTF8Encoding(false));
            WL("  -> " + path);
        }

        private static void Pause()
        {
            FlushLog();
            if (_auto) return;
            Console.Write("  Kapatmak icin Enter...");
            Console.ReadLine();
        }

        // Arac ayri bir yonetici penceresinde calistigi icin ciktiyi baska
        // turlu okumak zor.
        private static void FlushLog()
        {
            try
            {
                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                File.WriteAllText(Path.Combine(dir, "etodpx-tune.log"),
                                  _log.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
