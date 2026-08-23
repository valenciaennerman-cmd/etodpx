// dpetoos - otomatik strateji bulucu
//
// DPI kutulari operatore, sehre, hatta abonelik tipine gore farkli davranir.
// "Dogru" ayar yoktur; sadece SENIN hattinda calisan ayar vardir.
// Bu arac stratejileri tek tek deneyip her biriyle gercek TLS el sikismasi
// yapar (sertifika dogrulamasi ACIK - engel sayfasi basari sayilmaz) ve
// kazanani dpetoos.conf dosyasina yazar.
//
// Yonetici olarak calistirilmali. Calisan dpetoos varsa once durdurulur.
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
using Dpetoos;
using Dpetoos.Engine;

namespace Dpetoos.Tune
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
        private const int TriesPerTarget = 3;
        private static bool _auto;
        private static bool _dpetoosWasRunning;
        private static readonly StringBuilder _log = new StringBuilder();

        private static void W(string s) { Console.Write(s); _log.Append(s); }
        private static void WL(string s) { Console.WriteLine(s); _log.AppendLine(s); }
        private static void WL() { Console.WriteLine(); _log.AppendLine(); }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "dpetoos - strateji bulucu";

            var rest = new List<string>();
            foreach (var a in args)
            {
                if (a == "--auto" || a == "-a") _auto = true;
                else rest.Add(a);
            }
            if (rest.Count == 0)
                rest.AddRange(new[] { "discord.com", "cdn.discordapp.com", "x.com" });

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
                WL("  Ornek: dpetoos-tune.exe discord.com");
                Pause();
                return 1;
            }
            _targets = cleaned.ToArray();

            WL();
            WL("  dpetoos strateji bulucu");
            WL("  =====================");
            WL("  hedefler : " + string.Join(", ", _targets));
            WL();

            if (!StopRunningDpetoos()) return Finish(1);

            if (!Preflight()) { Pause(); return Finish(3); }

            var strategies = BuildStrategies();

            WL("  " + strategies.Count + " strateji denenecek.");
            WL("  Once dpetoos KAPALI iken olcum aliniyor...");
            WL();

            int baseline = RunTests();
            int total = _targets.Length * TriesPerTarget;
            WL("  temel durum (koruma yok) : " + baseline + "/" + total);
            WL();

            if (baseline == total)
            {
                WL("  Hedefler korumasiz da aciliyor - burada asilacak bir engel yok.");
                WL("  Farkli bir hedef verip tekrar dene:");
                WL("    dpetoos-tune.exe engellisite.com");
                WL();
                Pause();
                return Finish(0);
            }

            var results = new List<KeyValuePair<Strategy, int>>();
            Strategy best = null;
            int bestScore = baseline;

            foreach (var s in strategies)
            {
                W(("  " + s.Name).PadRight(42));
                int score = TryStrategy(s);
                results.Add(new KeyValuePair<Strategy, int>(s, score));

                string bar = new string('#', score) + new string('.', total - score);
                WL(bar + "  " + score + "/" + total);

                if (score > bestScore) { bestScore = score; best = s; }
                FlushLog();                              // ilerleme disaridan izlenebilsin
            }

            WL();
            WL("  --- siralama (esitlikte en guvenli once) ---");
            // Esit puanlilarda deneme sirasi belirleyicidir: liste sahte paketi
            // sunucuya ULASTIRAMAYACAK yontemlerden baslar, TTL'ler en dusukten
            // artar. Secim de bunu izler (ilk zirveye ulasan kazanir), o yuzden
            // gosterim ayni sirayi kullanmali - yoksa kazanan yanlis gorunur.
            results.Sort(delegate (KeyValuePair<Strategy, int> a, KeyValuePair<Strategy, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                if (c != 0) return c;
                return strategies.IndexOf(a.Key).CompareTo(strategies.IndexOf(b.Key));
            });
            foreach (var kv in results)
                WL("   " + kv.Value + "/" + total + "  " + kv.Key.Name);
            WL();

            if (best == null)
            {
                WL("  Hicbir strateji temel durumu gecemedi.");
                WL("  Bu hedef muhtemelen IP seviyesinde engelli; DPI atlatma yetmez.");
                WL();
                Pause();
                return Finish(2);
            }

            WL("  KAZANAN : " + best.Name + "  (" + bestScore + "/" + total + ")");
            WL();
            string ans = null;
            if (!_auto)
            {
                W("  dpetoos.conf dosyasina yazilsin mi? [E/h] ");
                ans = Console.ReadLine();
            }
            if (ans != null && ans.Trim().ToLowerInvariant().StartsWith("h"))
            {
                WL();
                WL("  Yazilmadi. Elle eklemek istersen:");
                WL();
                WL(best.ConfLines());
                Pause();
                return Finish(0);
            }

            WriteConf(best);
            WL();
            WL("  Yazildi. dpetoos'u yeniden baslat.");
            WL();
            Pause();
            return Finish(0);
        }

        /// <summary>
        /// "https://www.site.com/a/b?x=1" -> "www.site.com"
        /// Sema, kullanici bilgisi, port, yol ve parca atilir.
        /// </summary>
        private static string ToHostName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string t = raw.Trim().Trim('"');

            int scheme = t.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) t = t.Substring(scheme + 3);

            int at = t.IndexOf('@');                  // kullanici:parola@
            if (at >= 0) t = t.Substring(at + 1);

            int cut = t.IndexOfAny(new[] { '/', '?', '#', (char)92 });   // 92 = ters boluk
            if (cut >= 0) t = t.Substring(0, cut);

            if (t.StartsWith("[", StringComparison.Ordinal))   // IPv6 hazir bulunsun
            {
                int close = t.IndexOf(']');
                if (close > 0) return t.Substring(1, close - 1);
            }

            // Port ancak TEK bir iki nokta varsa kirpilir; birden fazlaysa
            // koseli parantezsiz bir IPv6 adresidir ve kirpmak onu bozar.
            int first = t.IndexOf(':');
            if (first > 0 && first == t.LastIndexOf(':')) t = t.Substring(0, first);

            return t.Trim().TrimEnd('.').ToLowerInvariant();
        }

        // ---------------------------------------------------------- on teshis

        private enum Diag { Open, NoDns, DnsPoisoned, IpBlocked, SniBlocked, Unreachable }

        /// <summary>
        /// Taramadan once engelin CINSINI belirler. dpetoos yalnizca SNI engelini
        /// asabilir; DNS ya da IP engelinde 31 strateji denemenin anlami yok.
        /// </summary>
        private static bool Preflight()
        {
            WL("  --- on teshis ---");
            bool anySni = false;
            _dnsPoisoned = false;

            foreach (string host in _targets)
            {
                Diag d = Diagnose(host);
                if (d == Diag.DnsPoisoned) _dnsPoisoned = true;
                string msg;
                switch (d)
                {
                    case Diag.NoDns:
                        msg = "alan adi cozulemiyor - DNS'te boyle bir kayit yok";
                        break;
                    case Diag.DnsPoisoned:
                        msg = "DNS ZEHIRLI - once DNS'i duzelt, dpetoos bunu asamaz";
                        break;
                    case Diag.IpBlocked:
                        msg = "IP seviyesinde engel - TCP baglantisi bile kurulmuyor, dpetoos asamaz";
                        break;
                    case Diag.Open:
                        msg = "zaten aciliyor - burada asilacak engel yok";
                        break;
                    case Diag.SniBlocked:
                        msg = "SNI engeli - dpetoos bunu asabilir";
                        anySni = true;
                        break;
                    default:
                        msg = "sunucuya ulasiliyor ama TLS kurulamiyor (sebep belirsiz)";
                        anySni = true;   // yine de denemeye deger
                        break;
                }
                WL(("  " + host).PadRight(32) + msg);
            }
            WL();

            if (_dnsPoisoned)
            {
                WL("  ISS'inin DNS sunucusu bu alan adi icin sahte bir adres donduruyor.");
                WL("  dpetoos paketleri parcalar; DNS seni bastan yanlis sunucuya");
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
                return false;
            }

            if (!anySni)
            {
                WL("  Taranacak bir SNI engeli bulunamadi, strateji taramasi atlaniyor.");
                WL("  Gercek ve engelli bir site adiyla tekrar dene, ornegin:");
                WL("    dpetoos-tune.exe discord.com");
                WL();
                return false;
            }
            return true;
        }

        private static bool _dnsPoisoned;

        private static Diag Diagnose(string host)
        {
            System.Net.IPAddress[] ips;
            try
            {
                ips = System.Net.Dns.GetHostAddresses(host);
                if (ips.Length == 0) return Diag.NoDns;
            }
            catch { return Diag.NoDns; }

            // Sistem DNS'i ile bagimsiz bir cozumleyiciyi karsilastir. Ortak tek bir
            // adres bile yoksa sistem DNS'i sahte cevap donduruyor demektir - bu
            if (IsDnsPoisoned(host, ips)) return Diag.DnsPoisoned;

            if (!CanConnect(host)) return Diag.IpBlocked;
            if (Handshake(host)) return Diag.Open;

            // Ayni sunucuya zararsiz bir SNI ile ulasilabiliyorsa yol acik demektir;
            // engelin tek dayanagi alan adi metnidir.
            if (HandshakeWithSni(ips[0].ToString(), "example.com")) return Diag.SniBlocked;
            return Diag.Unreachable;
        }

        /// <summary>
        /// Sistem DNS'i sahte cevap mi donduruyor?
        ///
        /// Tek bir referansla karsilastirmak YANILTICI olur: CDN'ler her
        /// cozumleyiciye havuzdan farkli adresler dondurebilir ve bu masum fark
        /// "zehirli" gibi gorunur. Bu yuzden IKI bagimsiz referans sorulur ve
        /// yalnizca su durumda bayrak kaldirilir:
        ///   - iki referans birbiriyle ortak adres veriyor (yani adres kararli), VE
        ///   - sistem DNS'inin verdigi adreslerin hicbiri o kumede degil.
        /// Referanslar birbirini tutmuyorsa alan adi zaten degisken demektir ve
        /// hicbir sey iddia edilmez.
        /// </summary>
        private static bool IsDnsPoisoned(string host, System.Net.IPAddress[] systemIps)
        {
            var r1 = ResolveVia("1.1.1.1", host);
            var r2 = ResolveVia("8.8.8.8", host);
            if (r1.Count == 0 || r2.Count == 0) return false;

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

        /// <summary>
        /// Belirtilen DNS sunucusuna DOGRUDAN sorar (sistem cozumleyicisini
        /// atlar). Amac, sistem DNS'inin verdigi cevabi bagimsiz bir kaynakla
        /// karsilastirip sahte cevabi yakalamak.
        /// </summary>
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

        /// <summary>DNS adini atlar; sikistirma isaretcisi (0xC0) iki bayttir.</summary>
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

        /// <summary>Sertifika dogrulamasi KAPALI - burada amac el sikismanin
        /// tamamlanip tamamlanmadigi, kimin cevap verdigi degil.</summary>
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

        // ------------------------------------------------------------ stratejiler

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

            // TTL varyantlari. Paketleri yeniden birlestiren DPI kutularina karsi
            // en etkili yontem budur: sahte paket gecerli (dogru seq + dogru
            // checksum) oldugu icin DPI onu kabul eder, ama TTL yuzunden sunucuya
            // varmadan oler. Deger hatta gore degisir, o yuzden hepsini deniyoruz.
            foreach (int t in new[] { 1, 2, 3, 4, 5, 6, 7 })
            {
                l.Add(new Strategy { Name = "ttl " + t + " + host disorder", Ttl = true, TtlValue = t, SplitPos = "host" });
                l.Add(new Strategy { Name = "ttl " + t + " + midsld disorder", Ttl = true, TtlValue = t, SplitPos = "midsld" });
                // bolmeden, sadece sahte paket (zapret'in duz "fake" kipi)
                l.Add(new Strategy { Name = "ttl " + t + " (bolmesiz)", Ttl = true, TtlValue = t, Tls = "fake" });
            }

            return l;
        }

        private static int TryStrategy(Strategy s)
        {
            var cfg = new Config();
            cfg.QuicMode = "drop";        // olcum sirasinda QUIC yolu devre disi kalsin
            cfg.Threads = 2;
            s.ApplyTo(cfg);

            var eng = new DpiEngine(cfg);
            try
            {
                eng.Start();
                Thread.Sleep(250);        // surucu otursun
                return RunTests();
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

        // ------------------------------------------------------------------ test

        private static int RunTests()
        {
            int ok = 0;
            foreach (string host in _targets)
                for (int i = 0; i < TriesPerTarget; i++)
                    if (Handshake(host)) ok++;
            return ok;
        }

        /// <summary>
        /// Gercek TLS el sikismasi. Sertifika ADI ve ZINCIRI dogrulanir; boylece
        /// engel sayfasi ya da araya giren bir sunucu basari olarak sayilmaz.
        /// </summary>
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

                // Zaman asimi sart: DPI baglantiyi sessizce yutarsa el sikisma
                // TCP yeniden gonderim zaman asimini (20+ sn) beklerdi.
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

        // ---------------------------------------------------------------- yardimci

        private static bool StopRunningDpetoos()
        {
            var procs = Process.GetProcessesByName("dpetoos");
            if (procs.Length == 0) return true;

            string dir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string expected = Path.GetFullPath(Path.Combine(dir, "dpetoos.exe"));
            var matching = new List<Process>();
            foreach (var p in procs)
            {
                try
                {
                    string actual = Path.GetFullPath(p.MainModule.FileName);
                    if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) matching.Add(p);
                    else p.Dispose();
                }
                catch { p.Dispose(); }
            }
            if (matching.Count == 0) return true;

            _dpetoosWasRunning = true;

            WL("  Calisan dpetoos bulundu - olcum icin durduruluyor.");
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
                    WL("  dpetoos durdurulamadi: " + ex.Message);
                    WL("  Once dpetoos'u elle kapat, sonra tekrar dene.");
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
            RestartDpetoosIfNeeded();
            return code;
        }

        private static void RestartDpetoosIfNeeded()
        {
            if (!_dpetoosWasRunning) return;
            _dpetoosWasRunning = false;
            try
            {
                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string exe = Path.Combine(dir, "dpetoos.exe");
                if (!File.Exists(exe))
                {
                    WL("  UYARI: dpetoos.exe bulunamadigi icin yeniden baslatilamadi.");
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });
                WL("  dpetoos yeniden baslatildi.");
            }
            catch (Exception ex)
            {
                WL("  UYARI: dpetoos yeniden baslatilamadi: " + ex.Message);
            }
        }

        private static void WriteConf(Strategy best)
        {
            string dir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(dir, "dpetoos.conf");

            var keep = new List<string>();
            var drop = new[] { "tls", "http", "split_pos", "fake_badsum",
                               "fake_wrongseq", "fake_ttl", "fake_ttl_value" };

            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    string t = line.TrimStart();
                    int eq = t.IndexOf('=');
                    if (eq > 0 && t.Length > 0 && t[0] != '#' && t[0] != ';')
                    {
                        string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                        if (Array.IndexOf(drop, k) >= 0) continue;   // eskisini at
                    }
                    keep.Add(line);
                }
            }

            keep.Add("");
            keep.Add("# --- dpetoos-tune tarafindan bulundu: " + best.Name);
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

        /// <summary>Sonuclari diske yaz - arac yonetici olarak ayri bir pencerede
        /// calistigi icin ciktiyi baska turlu okumak zor.</summary>
        private static void FlushLog()
        {
            try
            {
                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                File.WriteAllText(Path.Combine(dir, "dpetoos-tune.log"),
                                  _log.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
