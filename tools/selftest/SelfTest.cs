// Oz test: motorun arayuz olmadan dogrulanabilir parcalari.
// Yonetici gerekmez, yalnizca WinDivert.dll yaninda olmali (filtre derleyicisi).
//
// Derleme: tools\selftest\build-selftest.cmd
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Etodpx;
using Etodpx.Engine;
using Etodpx.Tune;

namespace Etodpx.Test
{
    internal static unsafe class SelfTest
    {
        private static int _pass, _fail;

        private static void Check(bool ok, string what, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("  [ok]   " + what); }
            else
            {
                _fail++;
                Console.WriteLine("  [HATA] " + what);
                if (!string.IsNullOrEmpty(detail)) Console.WriteLine("         " + detail);
            }
        }

        private static void Check(bool ok, string what) { Check(ok, what, null); }

        private static void RunSection(string name, Action test, string filter)
        {
            if (!string.IsNullOrEmpty(filter) &&
                name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                return;
            test();
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string filter = (args != null && args.Length > 0) ? args[0] : null;
            Console.WriteLine();
            Console.WriteLine("etodpx oz test" + (filter == null ? string.Empty : " (filtre: " + filter + ")"));
            Console.WriteLine("==============");

            RunSection("Interop", TestInterop, filter);
            RunSection("Config", TestConfig, filter);
            RunSection("ConfigCopy", TestConfigCopy, filter);
            RunSection("Hostlist", TestHostlist, filter);
            RunSection("HostlistRules", TestHostlistRules, filter);
            RunSection("RepairTargets", TestRepairTargets, filter);
            RunSection("DnsRestore", TestDnsRestore, filter);
            RunSection("TunerDiagnostics", TestTunerDiagnostics, filter);
            RunSection("StartupTask", TestStartupTask, filter);
            RunSection("Filters", TestFilters, filter);
            RunSection("FakeClientHello", TestFakeClientHello, filter);
            RunSection("SniParser", TestSniParser, filter);
            RunSection("MidSld", TestMidSld, filter);
            RunSection("SplitPosModes", TestSplitPosModes, filter);
            RunSection("HttpHost", TestHttpHost, filter);
            RunSection("PacketParse", TestPacketParse, filter);
            RunSection("QuicFake", TestQuicFake, filter);
            RunSection("DesyncGuards", TestDesyncGuards, filter);
            RunSection("ContinuationTable", TestContinuationTable, filter);
            RunSection("SegmentedClientHello", TestSegmentedClientHello, filter);
            RunSection("IPv6", TestIPv6, filter);
            RunSection("QuicFalsePositives", TestQuicFalsePositives, filter);
            RunSection("TcpFalsePositives", TestTcpFalsePositives, filter);
            RunSection("HostCaseSafety", TestHostCaseSafety, filter);

            Console.WriteLine();
            Console.WriteLine("gecen: " + _pass + "   kalan: " + _fail);
            Console.WriteLine();
            return _fail == 0 ? 0 : 1;
        }

        // --- ayar kopyalama
        // Ayarlayici yeni degerleri diske yazip CopyFrom ile calisan uygulamaya
        // tasiyor. O elle yazilmis listeye eklenmeyen her yeni alan sessizce eski
        // degerinde kalir: dosyada dogru ayar gorunur, motor baskasini kullanir.
        private static void TestConfigCopy()
        {
            Console.WriteLine();
            Console.WriteLine("- ayar kopyalama");

            var src = new Config();
            var dst = new Config();
            var missed = new System.Collections.Generic.List<string>();

            var fields = typeof(Config).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            // Kaynagi her alanda varsayilandan farkli bir degere cek.
            foreach (var f in fields)
            {
                if (f.FieldType == typeof(bool)) f.SetValue(src, !(bool)f.GetValue(dst));
                else if (f.FieldType == typeof(int)) f.SetValue(src, (int)f.GetValue(dst) + 7);
                else if (f.FieldType == typeof(short)) f.SetValue(src, (short)((short)f.GetValue(dst) + 7));
                else if (f.FieldType == typeof(string)) f.SetValue(src, "kopya-testi");
            }
            src.Warnings.Add("uyari");

            dst.CopyFrom(src);

            foreach (var f in fields)
            {
                object a = f.GetValue(src), b = f.GetValue(dst);
                if (f.FieldType == typeof(System.Collections.Generic.List<string>))
                {
                    var la = (System.Collections.Generic.List<string>)a;
                    var lb = (System.Collections.Generic.List<string>)b;
                    if (la.Count != lb.Count) missed.Add(f.Name);
                    continue;
                }
                if (!Equals(a, b)) missed.Add(f.Name);
            }

            Check(fields.Length > 20, "Config alanlari yansima ile bulundu",
                  "bulunan " + fields.Length);
            Check(missed.Count == 0, "CopyFrom butun ayar alanlarini tasiyor",
                  "tasinmayan: " + string.Join(", ", missed.ToArray()));
        }

        // --- alan adi listesi
        // Liste yokken etodpx 443'e giden HER ClientHello'yu boluyordu. Engelli
        // olmayan sunucuya giden baglantiyi bolmenin faydasi yok ama DPI onu
        // dusurunce uygulama TCP zaman asimi kadar donuyor. Eslesme kurallari
        // bu yuzden dar: son ek yalnizca nokta sinirinda sayilir.
        private static bool Hits(Hostlist list, string host)
        {
            byte[] b = Encoding.ASCII.GetBytes(host);
            fixed (byte* p = b) return list.Match(p, b.Length);
        }

        private static void TestHostlist()
        {
            Console.WriteLine();
            Console.WriteLine("- alan adi listesi");

            string path = Path.Combine(Path.GetTempPath(), "etodpx-hostlist-test.txt");
            File.WriteAllLines(path, new[]
            {
                "# yorum satiri", "", "discord.com", "*.example.org", "TRAILING.NET."
            });

            Hostlist list = Hostlist.Load(path, null);
            Check(list != null && list.Count == 3,
                  "yorum ve bos satirlar atlanarak okundu",
                  list == null ? "null" : "sayi " + list.Count);
            if (list == null) return;

            Check(Hits(list, "discord.com"), "tam eslesme");
            Check(Hits(list, "cdn.discord.com"), "alt alan adi eslesiyor");
            Check(Hits(list, "DISCORD.COM"), "buyuk harf duyarsiz");
            Check(Hits(list, "a.b.example.org"), "bastaki *. yok sayiliyor");
            Check(Hits(list, "trailing.net"), "sondaki nokta kirpiliyor");

            Check(!Hits(list, "notdiscord.com"),
                  "nokta sinirinda olmayan son ek ESLESMIYOR");
            Check(!Hits(list, "discord.com.baskasite.net"),
                  "adin ortasindaki eslesme sayilmiyor");
            Check(!Hits(list, "google.com"), "listede olmayan ad eslesmiyor");
            Check(!Hits(list, "com"), "kisa parca eslesmiyor");

            list.Dispose();

            Check(Hostlist.Load(Path.Combine(Path.GetTempPath(), "etodpx-yok-boyle.txt"), null) == null,
                  "dosya yoksa null (butun adlara dokunulur)");

            string empty = Path.Combine(Path.GetTempPath(), "etodpx-hostlist-bos.txt");
            File.WriteAllLines(empty, new[] { "# yalniz yorum" });
            var warn = new System.Collections.Generic.List<string>();
            Check(Hostlist.Load(empty, warn) == null && warn.Count == 1,
                  "bos liste uyari uretip null donuyor");

            try { File.Delete(path); File.Delete(empty); } catch { }
        }

        // --- alan adi basina ozel strateji ve canli ekleme
        private static void TestHostlistRules()
        {
            Console.WriteLine();
            Console.WriteLine("- alan adi basina strateji ve ekleme");

            string path = Path.Combine(Path.GetTempPath(), "etodpx-hostrules-test.txt");
            File.WriteAllLines(path, new[]
            {
                "discord.com",
                "x.com fake+disorder ttl=6 midsld",
                "old.site.org split badsum pos=2"
            });

            Hostlist list = Hostlist.Load(path, null);
            Check(list != null && list.Count == 3, "ozel kuralli liste okundu");
            if (list != null)
            {
                HostRule r1;
                byte[] b1 = Encoding.ASCII.GetBytes("cdn.x.com");
                bool m1;
                fixed (byte* p = b1) m1 = list.MatchRule(p, b1.Length, out r1);
                Check(m1 && r1.HasOverride, "x.com alt alan adinda ozel strateji yakalandi");
                Check((r1.Flags & HostFlags.Fake) != 0 &&
                      (r1.Flags & HostFlags.Disorder) != 0 &&
                      (r1.Flags & HostFlags.Ttl) != 0 &&
                      (r1.Flags & HostFlags.SplitMidSld) != 0 &&
                      r1.TtlValue == 6,
                      "x.com ozel bayraklari (fake+disorder, ttl=6, midsld) dogru",
                      "ttl=" + r1.TtlValue + ", flags=" + (int)r1.Flags);

                HostRule r2;
                byte[] b2 = Encoding.ASCII.GetBytes("old.site.org");
                bool m2;
                fixed (byte* p = b2) m2 = list.MatchRule(p, b2.Length, out r2);
                Check(m2 && r2.HasOverride && r2.FixedSplitPos == 2 &&
                      (r2.Flags & HostFlags.BadSum) != 0,
                      "sabit bolme konumu ve badsum bayragi okundu");

                HostRule rDefault;
                byte[] bDef = Encoding.ASCII.GetBytes("discord.com");
                bool mDef;
                fixed (byte* p = bDef) mDef = list.MatchRule(p, bDef.Length, out rDefault);
                Check(mDef && !rDefault.HasOverride,
                      "sade alan adinda genel ayar korunuyor");

                list.Dispose();
            }

            string normalized;
            string err;
            bool added = Hostlist.AddHostToFile(path, "https://www.NewSite.com/test?a=1", out normalized, out err);
            Check(added && err == null && normalized == "newsite.com",
                  "AddHostToFile tam URL'den alan adini ayiklayip ekliyor",
                  err ?? normalized);

            bool dupSub = Hostlist.AddHostToFile(path, "sub.newsite.com", out normalized, out err);
            Check(!dupSub && err == null, "ust alan adi zaten listedeyse alt alan adi tekrar yazilmiyor");
            bool otherSite = Hostlist.AddHostToFile(path, "another.org", out normalized, out err);
            Check(otherSite && err == null, "farkli alan adi eklenebiliyor");

            // Canli liste degistirme (SetHostlist)
            var cfg = new Config();
            var st = new Stats();
            using (var w = new DesyncWorker(cfg, st, IntPtr.Zero, 0))
            using (var hNew = Hostlist.Load(path, null))
            {
                w.SetHostlist(hNew);
                byte[] unrecognized = BuildTcpPacket(Fakes.BuildTlsClientHello("google.com"), Packet.TcpPsh);
                Check(!Feed(w, unrecognized) && st.SendErrors == 0,
                      "SetHostlist sonrasi listede olmayan alan adi es geciliyor");
            }

            try { File.Delete(path); } catch { }
        }

        // --- Windows baslangic gorevi
        private static void TestStartupTask()
        {
            Console.WriteLine();
            Console.WriteLine("- Windows baslangic gorevi");

            string createArgs = StartupTask.BuildCreateArguments(@"C:\Program Files\etodpx\etodpx.exe");
            Check(createArgs.Contains("/Create") &&
                  createArgs.Contains("/SC ONLOGON") &&
                  createArgs.Contains("/RL HIGHEST") &&
                  createArgs.Contains("etodpx.exe"),
                  "schtasks olusturma komutu /SC ONLOGON ve /RL HIGHEST iceriyor", createArgs);

            string delArgs = StartupTask.BuildDeleteArguments();
            Check(delArgs.Contains("/Delete") && delArgs.Contains("/F"),
                  "schtasks silme komutu dogru", delArgs);

            string queryArgs = StartupTask.BuildQueryArguments();
            Check(queryArgs.Contains("/Query") && queryArgs.Contains(StartupTask.TaskName),
                  "schtasks sorgu komutu dogru", queryArgs);
        }

        // --- onarim hedefleri
        // "Tek tikla duzelt" bir alan adi icin strateji bulup onu hostlist'e
        // koymazsa hicbir sey degismez: motor o ada dokunmaz. Roblox tam olarak
        // boyle atlanmisti - ayarlayicinin varsayilan hedefinde vardi, dagitilan
        // hosts.txt'de yoktu. Iki listenin ayrismasini burada yakaliyoruz.
        private static void TestRepairTargets()
        {
            Console.WriteLine();
            Console.WriteLine("- onarim hedefleri");

            string[] targets = NetworkRepair.RepairTargets;
            Check(targets != null && targets.Length >= 2, "en az iki hedef olculuyor",
                  targets == null ? "null" : "sayi " + targets.Length);
            if (targets == null) return;

            Check(Array.IndexOf(targets, "discord.com") >= 0, "Discord hedefler arasinda");
            Check(Array.IndexOf(targets, "roblox.com") >= 0, "Roblox hedefler arasinda");

            string[] required =
            {
                "discord.com", "discordapp.com", "cdn.discordapp.com",
                "updates.discord.com", "gateway.discord.gg", "discord.gg", "status.discord.com",
                "roblox.com", "www.roblox.com", "apis.roblox.com", "auth.roblox.com",
                "assetgame.roblox.com", "gamejoin.roblox.com", "clientsettings.roblox.com"
            };
            var missing = new System.Collections.Generic.List<string>();
            foreach (string t in required)
                if (Array.IndexOf(targets, t) < 0) missing.Add(t);
            Check(missing.Count == 0, "uygulamanin kullandigi tum uclar olculuyor",
                  missing.Count == 0 ? null : "eksik: " + string.Join(",", missing.ToArray()));

            bool allDns = true;
            foreach (string t in targets)
                if (Uri.CheckHostName(t) != UriHostNameType.Dns) allDns = false;
            Check(allDns, "hedeflerin hepsi gecerli alan adi");

            // Oz test bin\ icinden calisiyor; depo kopyasi bir ust dizinde.
            // Yayin ZIP'inde ust dizin yok, o zaman bu kontrol atlanir.
            string repoList = Path.Combine("..", "hosts.txt");
            if (!File.Exists(repoList))
            {
                Console.WriteLine("  [atla] depo hosts.txt yok, kapsam kontrolu yapilmadi");
                return;
            }

            Hostlist shipped = Hostlist.Load(repoList, null);
            Check(shipped != null, "dagitilan hosts.txt okunabiliyor");
            if (shipped == null) return;

            missing.Clear();
            foreach (string t in targets)
                if (!Hits(shipped, t)) missing.Add(t);
            Check(missing.Count == 0, "hosts.txt butun onarim hedeflerini kapsiyor",
                  missing.Count == 0 ? null : "eksik: " + string.Join(",", missing.ToArray()));

            // Olctuk: rbxcdn.com bu hatta engelli degil. Engelli olmayan bir CDN
            // listeye girerse Roblox'ta avatarlar ve resimler yuklenmez.
            Check(!Hits(shipped, "t0.rbxcdn.com"),
                  "engelsiz Roblox CDN'i listeye alinmamis");

            shipped.Dispose();
        }

        // --- DNS geri alma
        // Geri alma yalnizca elle yazilmis sunuculari geri koymali. Tek ailesi elle
        // ayarli bir makinede eski kod digerini bizim Cloudflare adresimizde
        // birakiyordu - "geri alinabilir" sozu boyle bozuluyor.
        private static void TestDnsRestore()
        {
            Console.WriteLine();
            Console.WriteLine("- DNS geri alma");

            Check(NetworkRepair.SplitServers("1.1.1.1,1.0.0.1").Length == 2,
                  "kayit defteri metni virgulden ayriliyor");
            Check(NetworkRepair.SplitServers("1.1.1.1 1.0.0.1").Length == 2,
                  "kayit defteri metni bosluktan da ayriliyor");
            Check(NetworkRepair.SplitServers("").Length == 0,
                  "bos NameServer = DHCP");

            var bothStatic = new DnsBackup
            {
                Static4 = new[] { "8.8.8.8" },
                Static6 = new[] { "2001:4860:4860::8888" }
            };
            Check(NetworkRepair.RestoreServers(bothStatic).Length == 2,
                  "iki aile de elle ayarliysa ikisi de geri konuyor");

            var v4Only = new DnsBackup { Static4 = new[] { "8.8.8.8" }, Static6 = new string[0] };
            string[] r = NetworkRepair.RestoreServers(v4Only);
            Check(r.Length == 1 && r[0] == "8.8.8.8",
                  "yalniz IPv4 elle ayarliysa IPv6 DHCP'ye birakiliyor",
                  "gelen " + string.Join(",", r));

            var v6Only = new DnsBackup { Static4 = new string[0], Static6 = new[] { "2001:db8::1" } };
            r = NetworkRepair.RestoreServers(v6Only);
            Check(r.Length == 1 && r[0] == "2001:db8::1",
                  "yalniz IPv6 elle ayarliysa IPv4 DHCP'ye birakiliyor",
                  "gelen " + string.Join(",", r));

            var allDhcp = new DnsBackup { Static4 = new string[0], Static6 = new string[0] };
            Check(NetworkRepair.RestoreServers(allDhcp).Length == 0,
                  "iki aile de DHCP ise hicbir sey elle yazilmiyor");

            // Eski surumden kalan yedek dosyalarinda aile ayrimi yok.
            var legacyStatic = new DnsBackup { WasDhcp = false, Servers = new[] { "9.9.9.9" } };
            Check(NetworkRepair.RestoreServers(legacyStatic).Length == 1,
                  "eski bicimli yedek hala geri yuklenebiliyor");
            var legacyDhcp = new DnsBackup { WasDhcp = true, Servers = new string[0] };
            Check(NetworkRepair.RestoreServers(legacyDhcp).Length == 0,
                  "eski bicimli DHCP yedegi DHCP'ye donuyor");

            // Sifreleme ayari da geri alinmali. netsh ile yazilan kayit makine
            // genelinde ve sunucuya bagli; birakilirsa DNS'i zaten 1.1.1.1 olan
            // kullanici geri alma sonrasi bile "DoH zorunlu" durumunda kaliyordu.
            var doh = new DnsBackup
            {
                DohBefore = new[]
                {
                    "1.1.1.1|0|0|https://cloudflare-dns.com/dns-query",
                    "1.0.0.1|-",
                    "2606:4700:4700::1111|1|1|https://cloudflare-dns.com/dns-query"
                }
            };
            string[] cmds = NetworkRepair.DohRestoreCommands(doh);
            Check(cmds.Length == 3, "her sunucu icin bir geri alma komutu",
                  "gelen " + cmds.Length);
            Check(cmds[0].Contains("Set-DnsClientDohServerAddress") &&
                  cmds[0].Contains("'1.1.1.1'") &&
                  cmds[0].Contains("-AutoUpgrade $false") &&
                  cmds[0].Contains("-AllowFallbackToUdp $false"),
                  "onceden kayitli sunucu eski degerleriyle yaziliyor", cmds[0]);
            Check(cmds[1].Contains("Remove-DnsClientDohServerAddress") && cmds[1].Contains("'1.0.0.1'"),
                  "bizden once olmayan kayit siliniyor", cmds[1]);
            Check(cmds[2].Contains("-AutoUpgrade $true") && cmds[2].Contains("-AllowFallbackToUdp $true"),
                  "acik olan ayar acik geri konuyor", cmds[2]);
            // netsh'in "set encryption"i yalnizca statik listede zaten olan bir
            // kaydi duzenliyor; olmayanda sozdizimi hatasi veriyor.
            foreach (string c in cmds)
                Check(!c.Contains("netsh") && !c.Contains("dnsclient"),
                      "geri alma netsh yerine cmdlet kullaniyor", c);

            Check(NetworkRepair.DohRestoreCommands(new DnsBackup()).Length == 0,
                  "eski bicimli yedekte sifreleme ayarina dokunulmuyor");
            Check(NetworkRepair.SplitLines("") == null,
                  "bos DoH kaydi bilgi yok demek (null)");

            // Kurulum betigi de netsh'ten cikmali: netsh sozdizimi hatasinda
            // bile 0 donuyor, yani "kaydedildi" kontrolu hicbir zaman tutmadi.
            Check(NetworkRepair.DohProviders.Length == 3,
                  "Cloudflare, Google ve Quad9 sirayla hazir");
            Check(NetworkRepair.DohProviders[0].Name == "Cloudflare" &&
                  NetworkRepair.DohProviders[1].Name == "Google" &&
                  NetworkRepair.DohProviders[2].Name == "Quad9",
                  "sifreli DNS saglayici sirasi kararli");

            string dohScript = NetworkRepair.BuildDohScript(
                "'1.1.1.1','1.0.0.1'", 12,
                "https://cloudflare-dns.com/dns-query",
                new[] { "discord.com", "roblox.com" });
            Check(!dohScript.Contains("netsh"), "sifreli DNS kurulumu netsh kullanmiyor");
            Check(dohScript.Contains("Add-DnsClientDohServerAddress") &&
                  dohScript.Contains("Set-DnsClientDohServerAddress"),
                  "kayit yoksa ekleniyor, varsa guncelleniyor");
            Check(dohScript.Contains("-AllowFallbackToUdp $false") &&
                  !dohScript.Contains("-AllowFallbackToUdp $true"),
                  "DoH duz ve zehirlenebilir UDP'ye dusmuyor");
            Check(dohScript.Contains("$v.AllowFallbackToUdp"),
                  "sifreleme ve UDP yasagi geri okunup dogrulaniyor");
            Check(dohScript.Contains("Resolve-DnsName -Name $h") &&
                  dohScript.Contains("'discord.com','roblox.com'") &&
                  !dohScript.Contains("www.microsoft.com"),
                  "genel site yerine gercek hedeflerin hepsi dogrulaniyor");
            // Zehirlenme alan adi basina. Tek ada bakan bir dogrulama Discord
            // acildi diye "DNS duzeldi" deyip Roblox'u bozuk birakiyordu.
            Check(dohScript.Contains("foreach($h in @(") &&
                  dohScript.Contains("if(-not $ok){throw ('Yeni DNS ayariyla cozumlenemedi: '+$h)}"),
                  "hedeflerden biri cozulemezse DNS degisikligi basarili sayilmiyor");
            Check(dohScript.Contains("195.175.254.2") && dohScript.Contains("0.0.0.0"),
                  "bilinen DNS zehir adresleri basari sayilmiyor");
            Check(dohScript.Contains("https://cloudflare-dns.com/dns-query"),
                  "saglayiciya ait DoH sablonu kullaniliyor");
            Check(dohScript.Contains("-InterfaceIndex 12"), "sunucular dogru arayuze yaziliyor");

            // Gecis halindeki bir agda ilk sorgu zaman asimina ugrayip ikincisi
            // gecebilir; dogrulama ve islem zaman asimlari dar olmamali.
            Check(dohScript.Contains("$deadline=(Get-Date).AddSeconds(" + NetworkRepair.DohValidationSeconds + ")") &&
                  dohScript.Contains("Start-Sleep -Milliseconds 800") &&
                  dohScript.Contains("$hasDoh="),
                  "DoH dogrulamasi gecici hatalara karsi yeniden deneme penceresi ve Win10 uyumu kullaniyor");
            Check(NetworkRepair.DohValidationSeconds >= 30 &&
                  NetworkRepair.CommandTimeoutMs >= 45000 &&
                  NetworkRepair.DohCommandTimeoutMs > NetworkRepair.DohValidationSeconds * 1000,
                  "PowerShell zaman asimi dogrulama penceresinden genis",
                  "pencere=" + NetworkRepair.DohValidationSeconds + "s, ps=" + NetworkRepair.DohCommandTimeoutMs + "ms");

            var managed = new DnsBackup { ManagedServers = new[] { "1.1.1.1", "1.0.0.1" } };
            Check(NetworkRepair.CanReuseBackup(managed, new[] { "1.0.0.1", "1.1.1.1" }),
                  "etodpx'in yonettigi DNS'te asil geri alma noktasi korunuyor");
            Check(!NetworkRepair.CanReuseBackup(managed, new[] { "8.8.8.8", "8.8.4.4" }),
                  "kullanicinin sonradan degistirdigi DNS eski yedekle ezilmiyor");
            var legacyManaged = new DnsBackup();
            Check(NetworkRepair.CanReuseBackup(legacyManaged, new[] { "1.1.1.1", "1.0.0.1" }),
                  "v2.3 Cloudflare yedegi geriye uyumlu");
            Check(!NetworkRepair.CanReuseBackup(legacyManaged, new[] { "8.8.8.8", "8.8.4.4" }),
                  "eski yedek elle secilen Google DNS'i etodpx ayari sanmiyor");
            Check(!NetworkRepair.CanReuseBackup(legacyManaged, new[] { "9.9.9.9", "149.112.112.112" }),
                  "eski yedek elle secilen Quad9 DNS'i etodpx ayari sanmiyor");
            Check(!NetworkRepair.CanReuseBackup(legacyManaged, new[] { "192.0.2.53" }),
                  "ilgisiz eski yedek yeniden kullanilmiyor");

            // Captive portal / dis DoH engelli ag tespiti: yalnizca hala bizim
            // yonettigimiz ve aktif rotayi tasiyan adaptorde DNS tamamen durduysa
            // eski ayara donulmeli; kullanici ayari degistirdiyse ya da hat kopuksa
            // dokunulmamali.
            string[] mServers = { "1.1.1.1", "1.0.0.1" };
            Check(NetworkRepair.ClassifyProbe(new[] { "1.1.1.1", "1.0.0.1", "fe80::1" }, mServers, "DHCP_OK") ==
                  NetworkRepair.ManagedDnsState.BlockedHere,
                  "ag degisiminde kilitlenen yonettigimiz DoH yakalaniyor");
            Check(NetworkRepair.ClassifyProbe(mServers, mServers, "SYSTEM_OK") ==
                  NetworkRepair.ManagedDnsState.Healthy,
                  "calisan yonettigimiz DoH koruma altinda kaliyor");
            Check(NetworkRepair.ClassifyProbe(new[] { "8.8.8.8", "8.8.4.4" }, mServers, "DHCP_OK") ==
                  NetworkRepair.ManagedDnsState.NotManaged,
                  "kullanicinin sonradan degistirdigi DNS'e dokunulmuyor");
            Check(NetworkRepair.ClassifyProbe(mServers, mServers, "DOWN") ==
                  NetworkRepair.ManagedDnsState.NetworkDown,
                  "aktif rotada olmayan ya da kopuk adaptorde DNS geri alinmiyor");
            string probeScript = NetworkRepair.BuildManagedProbeScript(12);
            Check(probeScript.Contains("-InterfaceIndex 12") &&
                  probeScript.Contains("DhcpNameServer") &&
                  probeScript.Contains("ETODPX_PROBE=DHCP_OK"),
                  "ag degisimi yoklamasi yalnizca yonettigimiz sunuculari ve DHCP DNS'ini kontrol ediyor");

            string[] mergedDoh = NetworkRepair.MergeDohEntries(
                new[] { "1.1.1.1|0|0|https://old.example/dns-query" },
                new[] { "1.1.1.1|1|0|https://cloudflare-dns.com/dns-query", "8.8.8.8|-" });
            Check(mergedDoh.Length == 2,
                  "v2.3 yedegine yeni saglayici kayitlari ekleniyor");
            Check(mergedDoh[0].Contains("old.example") && mergedDoh[1] == "8.8.8.8|-",
                  "eski geri alma degeri birlestirmede ezilmiyor");
        }

        private static void TestTunerDiagnostics()
        {
            Console.WriteLine();
            Console.WriteLine("- otomatik tani siniflandirmasi");

            Check(Tuner.ToHostName("https://discord.com/channels/@me") == "discord.com",
                "tam adresten hedef alani ayriliyor");
            Check(Tuner.ToHostName("discord.com:443") == "discord.com",
                "port hedef alan adindan ayriliyor");
            Check(Tuner.ToHostName("https://kullanici:parola@discord.com/login") == "discord.com",
                "gercek kullanici bilgisi alan adindan ayriliyor");
            Check(Tuner.ToHostName("[2606:4700::1]:443") == "2606:4700::1",
                "koseli parantezli IPv6 + port dogru ayriliyor");
            Check(Tuner.ToHostName("2606:4700::1") == "2606:4700::1",
                "ciplak IPv6 port sanilip kesilmiyor");

            Check(Tuner.IsKnownPoisonAddress(System.Net.IPAddress.Parse("195.175.254.2")),
                "bilinen IPv4 engel sayfasi yakalaniyor");
            Check(Tuner.IsKnownPoisonAddress(System.Net.IPAddress.Parse("2a01:358:4014:a00::3")),
                "bilinen IPv6 engel sayfasi yakalaniyor");
            Check(!Tuner.IsKnownPoisonAddress(System.Net.IPAddress.Parse("1.1.1.1")),
                "normal DNS adresi zehirli sayilmiyor");

            // DNS zehir tespiti (JudgeDns + HasKnownPoison)
            bool failed;
            var cf = new System.Collections.Generic.List<string> { "162.159.135.232", "162.159.136.232" };
            var gg = new System.Collections.Generic.List<string> { "162.159.136.232", "162.159.137.232" };
            var empty = new System.Collections.Generic.List<string>();

            Check(Tuner.JudgeDns(new System.Collections.Generic.List<string> { "195.175.254.2" }, cf, gg, out failed) && !failed,
                "referanslar uzlasirken sistem farkli adres donerse zehirli");
            Check(!Tuner.JudgeDns(new System.Collections.Generic.List<string> { "162.159.135.232" }, cf, gg, out failed) && !failed,
                "sistem referanslardan biriyle ortak adres verirse temiz");
            Check(!Tuner.JudgeDns(
                    new System.Collections.Generic.List<string> { "104.18.1.1" },
                    new System.Collections.Generic.List<string> { "104.18.2.2" },
                    new System.Collections.Generic.List<string> { "104.18.3.3" }, out failed) && !failed,
                "CDN her cozumleyiciye farkli adres donunce yanlis alarm verilmiyor");
            Check(!Tuner.JudgeDns(new System.Collections.Generic.List<string> { "1.2.3.4" }, empty, gg, out failed) && failed,
                "referans cozumleyiciye ulasilamayinca compareFailed kalkiyor");

            Check(Tuner.HasKnownPoison(new[] { System.Net.IPAddress.Parse("195.175.254.2") }),
                "bilinen engel sayfasi IPv4 yakalaniyor");
            Check(Tuner.HasKnownPoison(new[] { System.Net.IPAddress.Parse("2a01:358:4014:a00::3") }),
                "bilinen engel sayfasi IPv6 (buyuk-kucuk harf duyarsiz) yakalaniyor");
            Check(Tuner.HasKnownPoison(new[] { System.Net.IPAddress.IPv6Any }),
                "bosa dusurulmus :: adresi yakalaniyor");
            Check(!Tuner.HasKnownPoison(new[] { System.Net.IPAddress.Parse("162.159.135.232") }),
                "gercek sunucu adresi zehirli sayilmiyor");

            Check(Tuner.RequiredTargetFailure(true, true, false, false) == "DNS_BLOCKED",
                "zorunlu hedeflerden biri DNS'te gizliyse SNI taramasi baslamiyor");
            Check(Tuner.RequiredTargetFailure(true, false, true, false) == "DNS_UNAVAILABLE",
                "zorunlu hedeflerden biri cozulmuyorsa basari raporlanmiyor");
            Check(Tuner.RequiredTargetFailure(true, false, false, true) == "IP_BLOCKED",
                "zorunlu hedeflerden biri IP engelliyse digeri acik diye basarili sayilmiyor");
            Check(Tuner.RequiredTargetFailure(false, true, true, true) == null,
                "normal ayarlayici DPI ile asilabilen hedefleri ayiklamaya devam ediyor");

            // Tarama erken cikisi. Yanlis tarafa kayan bir esik calisan bir
            // stratejiyi eler, kullanici da bosuna "asilamiyor" cevabi alir.
            Check(!Tuner.CannotWin(0, 2, 1),
                "kalan denemeler en iyiyi gecebiliyorsa tarama surduruluyor");
            Check(Tuner.CannotWin(0, 1, 1),
                "kalan denemeler en iyiye ancak esitleniyorsa tarama kesiliyor");
            Check(!Tuner.CannotWin(2, 0, 1),
                "en iyiyi zaten gecmis strateji kesilmiyor");
            Check(!Tuner.CannotWin(0, 3, 0),
                "temel puan sifirken hicbir strateji pesinen elenmiyor");
            Check(!Tuner.CannotWin(0, 0, -1),
                "-1 esigi verildiginde hic vazgecilmiyor (temel olcum ve dogrulama)");

            // Ayarlayiciya hosts.txt disinda bir hedef verildiginde tespit edilmeli.
            string tmp = Path.Combine(Path.GetTempPath(), "etodpx-tune-hosts-test.txt");
            File.WriteAllLines(tmp, new[] { "discord.com" });
            try
            {
                string[] miss = Tuner.MissingHosts(tmp, new[] { "discord.com", "cdn.discord.com", "reddit.com" });
                Check(miss.Length == 1 && miss[0] == "reddit.com",
                    "hosts.txt'de olmayan olcum hedefi tespit ediliyor",
                    "gelen: " + string.Join(",", miss));
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // --- interop
        private static void TestInterop()
        {
            Console.WriteLine();
            Console.WriteLine("- WinDivert interop yapisi");

            // Bu yanlissa WinDivertRecv yigini bozar ve hata cok gec fark edilir.
            int size = Marshal.SizeOf(typeof(DivertAddress));
            Check(size == 80, "WINDIVERT_ADDRESS boyutu 80 bayt", "gelen: " + size);

            var a = default(DivertAddress);
            a.Outbound = true;
            Check(a.Outbound && !a.IPv6 && !a.Impostor, "Outbound biti dogru konumda");
            a.TcpChecksum = true;
            Check(a.TcpChecksum && a.Outbound, "TcpChecksum biti Outbound'u bozmuyor");
            a.Outbound = false;
            Check(!a.Outbound && a.TcpChecksum, "bitler birbirinden bagimsiz");
        }

        // --- desync korumalari
        private static void TestDesyncGuards()
        {
            Console.WriteLine();
            Console.WriteLine("- desync korumalari");

            var cfg = new Config();
            var st = new Stats();
            // Tutamak IntPtr.Zero: hepsi gonderim yapmadan false donmeli.
            using (var w = new DesyncWorker(cfg, st, IntPtr.Zero, 0))
            {
                byte[] hello = Fakes.BuildTlsClientHello("discord.com");

                // 1) SYN tasiyan paket (TCP Fast Open) - bolunmemeli
                byte[] syn = BuildTcpPacket(hello, Packet.TcpSyn | Packet.TcpPsh);
                Check(!Feed(w, syn), "SYN tasiyan ClientHello es geciliyor");

                // 2) RST tasiyan paket
                byte[] rst = BuildTcpPacket(hello, Packet.TcpRst);
                Check(!Feed(w, rst), "RST tasiyan paket es geciliyor");

                // 3) akis ortasindaki veri (ne TLS ne HTTP istegi)
                byte[] mid = BuildTcpPacket(Encoding.ASCII.GetBytes(
                    "rastgele govde verisi, istek satiri degil............"), Packet.TcpPsh);
                Check(!Feed(w, mid), "akis ortasindaki veriye dokunulmuyor");

                Check(st.SendErrors == 0, "bu durumlarda hic gonderim denenmedi",
                      "gonderim hatasi: " + st.SendErrors);
            }
        }

        // --- parcali ClientHello devam tablosu
        // Hibrit kuantum-sonrasi anahtar degisimi (X25519MLKEM768) ClientHello'yu
        // ~1800 bayta cikardi. LSO olmayan adaptorde (Wi-Fi) bu iki TCP segmentine
        // bolunuyor ve SNI bazen ikinci segmente kaliyor.
        private static void TestContinuationTable()
        {
            Console.WriteLine();
            Console.WriteLine("- parcali ClientHello devam tablosu");

            using (var ct = new ContinuationTable())
            {
                byte[] s1 = BuildTcpSegment(1000, new byte[] { 1, 2, 3, 4 });
                byte[] s2 = BuildTcpSegment(1004, new byte[] { 5, 6, 7 });
                byte[] sWrongSeq = BuildTcpSegment(2000, new byte[] { 9, 9 });
                var outBuf = new byte[16];

                fixed (byte* p1 = s1)
                fixed (byte* p2 = s2)
                fixed (byte* pw = sWrongSeq)
                fixed (byte* dst = outBuf)
                {
                    PacketView v1;
                    Packet.Parse(p1, s1.Length, out v1);

                    // Yanlis sira numarali segment eslesmemeli.
                    ct.Record(pw, sWrongSeq.Length);
                    Check(ct.CopyNext(p1, ref v1, dst, outBuf.Length, 0) == 0,
                          "farkli seq tasiyan segment eslesmiyor");

                    // Beklenen sira numarasi (1000 + 4 = 1004) kaydedildiginde bulunmali.
                    ct.Record(p2, s2.Length);
                    int n = ct.CopyNext(p1, ref v1, dst, outBuf.Length, 0);
                    Check(n == 3, "beklenen sira numarali ikinci segment kopyalandi", "gelen " + n);
                    Check(outBuf[0] == 5 && outBuf[1] == 6 && outBuf[2] == 7,
                          "kopyalanan devam baytlari dogru");
                }
            }
        }

        // --- iki segmente bolunmus buyuk ClientHello
        private static void TestSegmentedClientHello()
        {
            Console.WriteLine();
            Console.WriteLine("- iki segmente bolunmus ClientHello (hibrit PQ boyutu)");

            // SNI'si ~1450. bayttan sonra gelen ~1800 baytlik gecerli bir ClientHello
            // kur: ilk 1400 baytlik segmentte SNI YOK, ikinci segmentte var.
            byte[] full = BuildLargeClientHello("discord.com", 1420);
            const int seg1Len = 1400;
            var seg1Payload = new byte[seg1Len];
            var seg2Payload = new byte[full.Length - seg1Len];
            Buffer.BlockCopy(full, 0, seg1Payload, 0, seg1Len);
            Buffer.BlockCopy(full, seg1Len, seg2Payload, 0, seg2Payload.Length);

            fixed (byte* pf = full)
            fixed (byte* p1 = seg1Payload)
            {
                Check(Hostname.FindSni(pf, full.Length).Found,
                      "tam ClientHello'da SNI bulunuyor");
                Check(!Hostname.FindSni(p1, seg1Len).Found,
                      "ilk segmentte SNI henuz yok (ikinci segmentte)");
                Check(DesyncWorker.NeedsContinuation(p1, seg1Len),
                      "NeedsContinuation eksik uzantili ilk segmenti taniyor");
                Check(!DesyncWorker.NeedsContinuation(pf, full.Length),
                      "NeedsContinuation tam pakette gereksiz yere tetiklenmiyor");
            }

            // Birlestirilmis paketten bulunan bolme noktasi ikinci segmente
            // dusuyorsa ilk segmentin sinirina cekilmeli.
            Check(DesyncWorker.FitSplitToSegment(1450, 1400, true, 0) == 3,
                  "ikinci segmente dusen bolme noktasi ilk segmentte guvenli konuma cekiliyor");
            Check(DesyncWorker.FitSplitToSegment(200, 1400, true, 0) == 200,
                  "ilk segmentin icindeki bolme noktasi korunuyor");
            Check(DesyncWorker.FitSplitToSegment(1450, 1400, true, 1450) == -1,
                  "ilk segment disina tasan sabit bolme konumu iptal ediliyor");

            // Canli akis: isci ilk segmenti alinca sniffer'in yakaladigi ikinci
            // segmentle birlestirip hosts.txt eslesmesini yapabilmeli.
            string path = Path.Combine(Path.GetTempPath(), "etodpx-seg-hosts.txt");
            File.WriteAllLines(path, new[] { "discord.com" });
            Hostlist hosts = Hostlist.Load(path, null);
            try
            {
                using (var ct = new ContinuationTable())
                {
                    byte[] pkt1 = BuildTcpSegment(5000, seg1Payload);
                    byte[] pkt2 = BuildTcpSegment((uint)(5000 + seg1Len), seg2Payload);

                    // 1) Devam tablosu YOKKEN: ilk segmentte SNI yok -> eslesmez -> gonderim denenmez.
                    var cfg = new Config();
                    var stNoCont = new Stats();
                    using (var w = new DesyncWorker(cfg, stNoCont, IntPtr.Zero, 0, hosts, null))
                    {
                        Feed(w, pkt1);
                        Check(stNoCont.SendErrors == 0,
                              "devam tablosu yokken SNI'si 2. segmentte kalan paket kaciriliyor (eski davranis)");
                    }

                    // 2) Devam tablosu VARKEN: 2. segment tabloya girince SNI bulunur -> islenir.
                    var stWithCont = new Stats();
                    using (var w = new DesyncWorker(cfg, stWithCont, IntPtr.Zero, 0, hosts, ct))
                    {
                        // Ikinci segmenti 5 ms sonra sniffer yakalamis gibi enjekte et.
                        System.Threading.ThreadPool.QueueUserWorkItem(delegate
                        {
                            System.Threading.Thread.Sleep(5);
                            fixed (byte* p2 = pkt2)
                            {
                                ct.Record(p2, pkt2.Length);
                            }
                        });

                        Feed(w, pkt1);
                        Check(stWithCont.SendErrors > 0,
                              "devam tablosu ile 2. segmentteki SNI bulunup desync uygulandi");
                    }
                }
            }
            finally
            {
                if (hosts != null) hosts.Dispose();
                try { File.Delete(path); } catch { }
            }
        }

        private static byte[] BuildTcpSegment(uint seq, byte[] payload)
        {
            byte[] pkt = BuildTcpPacket(payload, Packet.TcpPsh);
            pkt[24] = (byte)(seq >> 24);
            pkt[25] = (byte)(seq >> 16);
            pkt[26] = (byte)(seq >> 8);
            pkt[27] = (byte)seq;
            return pkt;
        }

        // SNI uzantisindan once paddingByteCount baytlik sahte bir uzanti (0x0015 padding)
        // koyarak toplam boyutu >1500 bayta cikarir.
        private static byte[] BuildLargeClientHello(string sni, int paddingByteCount)
        {
            byte[] host = Encoding.ASCII.GetBytes(sni);
            int sniExtLen = 4 + 2 + 1 + 2 + host.Length;
            int padExtLen = 4 + paddingByteCount;
            int extTotal = padExtLen + sniExtLen;
            int bodyLen = 2 + 32 + 1 + 2 + 2 + 1 + 1 + 2 + extTotal;
            int recLen = 4 + bodyLen;

            var b = new byte[5 + recLen];
            int p = 0;
            b[p++] = 0x16; b[p++] = 0x03; b[p++] = 0x01;
            b[p++] = (byte)(recLen >> 8); b[p++] = (byte)recLen;
            b[p++] = 0x01;
            b[p++] = (byte)(bodyLen >> 16); b[p++] = (byte)(bodyLen >> 8); b[p++] = (byte)bodyLen;
            b[p++] = 0x03; b[p++] = 0x03;
            p += 32;                                     // random
            b[p++] = 0x00;                               // session id len
            b[p++] = 0x00; b[p++] = 0x02;                // cipher suites len
            b[p++] = 0x13; b[p++] = 0x01;
            b[p++] = 0x01; b[p++] = 0x00;                // compression
            b[p++] = (byte)(extTotal >> 8); b[p++] = (byte)extTotal;

            // Padding uzantisi (tip 0x0015) - X25519MLKEM768 key_share yerini simule eder.
            b[p++] = 0x00; b[p++] = 0x15;
            b[p++] = (byte)(paddingByteCount >> 8); b[p++] = (byte)paddingByteCount;
            p += paddingByteCount;

            // SNI uzantisi (tip 0x0000)
            int listLen = 1 + 2 + host.Length;
            int extDataLen = 2 + listLen;
            b[p++] = 0x00; b[p++] = 0x00;
            b[p++] = (byte)(extDataLen >> 8); b[p++] = (byte)extDataLen;
            b[p++] = (byte)(listLen >> 8); b[p++] = (byte)listLen;
            b[p++] = 0x00;
            b[p++] = (byte)(host.Length >> 8); b[p++] = (byte)host.Length;
            Buffer.BlockCopy(host, 0, b, p, host.Length);
            return b;
        }

        // --- Host: yeniden yazma guvenligi
        // http_host_case yakalanan paketin tamponunu yerinde degistiriyor. Paketi
        // kendimiz gonderemezsek WorkerLoop onu eski checksum ile enjekte eder ve
        // sunucu atar; geri dusme yolunda tampon eski haline donmus olmali.
        private static void TestHostCaseSafety()
        {
            Console.WriteLine();
            Console.WriteLine("- Host basligi yeniden yazma guvenligi");

            string req = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n";
            byte[] payload = Encoding.ASCII.GetBytes(req);
            int hostAt = req.IndexOf("Host:", StringComparison.Ordinal);
            const int hdr = 40;                       // IPv4 20 + TCP 20

            // 1) Gonderim basarisiz (tutamak gecersiz) -> geri dusme yolu
            var cfg = new Config();
            cfg.HttpHostCase = true;
            var st = new Stats();
            using (var w = new DesyncWorker(cfg, st, IntPtr.Zero, 0))
            {
                Check(!Feed(w, BuildTcpPacket(payload, Packet.TcpPsh)),
                      "gonderim basarisizken orijinale geri dusuluyor");
                Check(ReadAscii(w.RecvBuffer, hdr + hostAt, 5) == "Host:",
                      "geri dususte yuk ESKI haline donuyor",
                      "tamponda: " + ReadAscii(w.RecvBuffer, hdr + hostAt, 5));
            }

            // 2) HTTP kipi kapali -> hic gonderim denenmez, tampona dokunulmamali
            var cfg2 = new Config();
            cfg2.HttpHostCase = true;
            cfg2.HttpMode = "off";
            var st2 = new Stats();
            using (var w = new DesyncWorker(cfg2, st2, IntPtr.Zero, 0))
            {
                Check(!Feed(w, BuildTcpPacket(payload, Packet.TcpPsh)),
                      "http kipi kapaliyken pakete dokunulmuyor");
                Check(ReadAscii(w.RecvBuffer, hdr + hostAt, 5) == "Host:",
                      "dokunulmayan pakette yuk degismemis");
                Check(st2.SendErrors == 0, "hic gonderim denenmedi");
            }
        }

        private static string ReadAscii(byte* p, int at, int len)
        {
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) sb.Append((char)p[at + i]);
            return sb.ToString();
        }

        private static bool Feed(DesyncWorker w, byte[] pkt)
        {
            byte* dst = w.RecvBuffer;
            for (int i = 0; i < pkt.Length; i++) dst[i] = pkt[i];
            var addr = default(DivertAddress);
            addr.Outbound = true;
            return w.HandleTcp(pkt.Length, ref addr);
        }

        private static byte[] BuildTcpPacket(byte[] payload, byte flags)
        {
            var pkt = new byte[40 + payload.Length];
            pkt[0] = 0x45;
            pkt[9] = 6;
            pkt[2] = (byte)(pkt.Length >> 8); pkt[3] = (byte)pkt.Length;
            pkt[12] = 10; pkt[15] = 1;
            pkt[16] = 1; pkt[17] = 2; pkt[18] = 3; pkt[19] = 4;
            pkt[20] = 0xC0; pkt[22] = 0x01; pkt[23] = 0xBB;      // -> 443
            pkt[24] = 0x11; pkt[25] = 0x22; pkt[26] = 0x33; pkt[27] = 0x44;
            pkt[32] = 0x50;
            pkt[33] = flags;
            Buffer.BlockCopy(payload, 0, pkt, 40, payload.Length);
            return pkt;
        }

        // --- TCP yanlis eslesmeleri
        // Akis ortasindaki veri ClientHello ya da HTTP istegi sanilirsa araya
        // gecerli checksum'li sahte paket girer; TTL yuksekse sunucuya ulasir.
        private static void TestTcpFalsePositives()
        {
            Console.WriteLine();
            Console.WriteLine("- TCP yanlis eslesme korumasi");

            var cfg = new Config();
            var st = new Stats();
            using (var w = new DesyncWorker(cfg, st, IntPtr.Zero, 0))
            {
                // 1) Gercek ClientHello taninmali; gecersiz test tutamaginda
                // gonderim basarisiz olunca orijinal icin false donmeli.
                byte[] realHello = Fakes.BuildTlsClientHello("example.com");
                long beforeReal = st.SendErrors;
                bool handledReal = Feed(w, BuildTcpPacket(realHello, Packet.TcpPsh));
                Check(!handledReal && st.SendErrors > beforeReal,
                      "gonderim hatasinda ClientHello orijinale geri dusuyor");
                long baseErr = st.SendErrors;

                // 2) Gevsek kontrolu gecen ama yapisi bozuk paketler; sifreli akis
                //    ortasindaki rastgele veri boyle gorunebiliyor.
                var rnd = new Random(31337);
                int caught = 0, tries = 20000;
                for (int i = 0; i < tries; i++)
                {
                    var body = new byte[600];
                    rnd.NextBytes(body);
                    body[0] = 0x16; body[1] = 0x03; body[5] = 0x01;   // eski desen
                    long before = st.SendErrors;
                    Feed(w, BuildTcpPacket(body, Packet.TcpPsh));
                    if (st.SendErrors != before) caught++;
                }
                Check(caught == 0, "rastgele TLS benzeri veri es geciliyor",
                      caught + "/" + tries + " yanlislikla islendi");

                // 3) HTTP: yontemle baslayan ama istek satiri olmayan govde
                int httpCaught = 0;
                string[] bodies = {
                    "POST veri govdesi, istek satiri degil, sadece boyle basliyor.....",
                    "GET  bu da bir istek satiri sayilmaz cunku HTTP surumu yok....",
                    "PUT ornek govde icerigi HTTP/9.9 gecersiz surum\r\n\r\n",
                };
                foreach (var b in bodies)
                {
                    long before = st.SendErrors;
                    Feed(w, BuildTcpPacket(Encoding.ASCII.GetBytes(b), Packet.TcpPsh));
                    if (st.SendErrors != before) httpCaught++;
                }
                Check(httpCaught == 0, "HTTP govde verisi istek sanilmiyor",
                      httpCaught + " yanlislikla islendi");

                // 4) Gercek HTTP istekleri hala isleniyor
                int ok = 0;
                string[] reqs = {
                    "GET /index.html HTTP/1.1\r\nHost: example.com\r\n\r\n",
                    "POST /api/v1/x HTTP/1.0\r\nHost: example.com\r\n\r\n",
                };
                foreach (var r in reqs)
                {
                    long before = st.SendErrors;
                    bool handled = Feed(w, BuildTcpPacket(Encoding.ASCII.GetBytes(r), Packet.TcpPsh));
                    if (!handled && st.SendErrors > before) ok++;
                }
                Check(ok == reqs.Length, "gercek HTTP istekleri taniniyor ve hatada geri dusuyor", ok + "/" + reqs.Length);

                Check(st.SendErrors > baseErr, "islenen paketler icin gonderim denendi");
            }
        }

        // --- QUIC yanlis eslesmeleri
        // UDP/443'ten QUIC disi trafik gecebiliyor: Steam Datagram Relay (CS2)
        // kisitli aglarda 443'e dusuyor. Boyle bir pakete dokunmak ping sicratir.
        // Ilk bayti QUIC gibi gorunen paketlerin es gecildigini dogruluyoruz.
        private static void TestQuicFalsePositives()
        {
            Console.WriteLine();
            Console.WriteLine("- QUIC yanlis eslesme korumasi");

            var cfg = new Config { QuicMode = "drop" };
            var st = new Stats();
            using (var w = new DesyncWorker(cfg, st, IntPtr.Zero, 0))
            {
                // Gercek QUIC v1 Initial: islenmeli (gonderim denenir, tutamak yok)
                byte[] real = BuildUdp443(Fakes.BuildQuicInitial(1250));
                bool handledReal = FeedUdp(w, real);
                Check(handledReal, "gercek QUIC v1 Initial isleniyor");

                byte[] v2Payload = Fakes.BuildQuicInitial(1250);
                v2Payload[0] = 0xd3;
                v2Payload[1] = 0x6b; v2Payload[2] = 0x33; v2Payload[3] = 0x43; v2Payload[4] = 0xcf;
                Check(FeedUdp(w, BuildUdp443(v2Payload)), "gercek QUIC v2 Initial isleniyor");

                long errAfterReal = st.SendErrors;

                // Ayni ilk bayt, QUIC surumu degil -> dokunulmamali
                var rnd = new Random(4242);
                int skipped = 0, total = 0;
                for (int b0 = 0xc0; b0 <= 0xcf; b0++)
                {
                    var payload = new byte[1250];
                    rnd.NextBytes(payload);
                    payload[0] = (byte)b0;
                    // surum alanini kasitli olarak QUIC disi bir degere ayarla
                    payload[1] = 0x53; payload[2] = 0x44; payload[3] = 0x52; payload[4] = 0x01;
                    total++;
                    if (!FeedUdp(w, BuildUdp443(payload))) skipped++;
                }
                Check(skipped == total, "QUIC disi UDP/443 trafigine dokunulmuyor",
                      skipped + "/" + total + " es gecildi");

                // Gecerli surum ama sacma DCID uzunlugu -> yine dokunulmamali
                var bad = new byte[1250];
                rnd.NextBytes(bad);
                bad[0] = 0xc3; bad[1] = 0; bad[2] = 0; bad[3] = 0; bad[4] = 1;
                bad[5] = 200;                      // DCID en fazla 20 olabilir
                Check(!FeedUdp(w, BuildUdp443(bad)), "bozuk DCID uzunlugu es geciliyor");

                // Kisa paketler (oyun trafigi genelde kucuktur) hic girmemeli
                var small = new byte[120];
                rnd.NextBytes(small);
                small[0] = 0xc3; small[1] = 0; small[2] = 0; small[3] = 0; small[4] = 1;
                Check(!FeedUdp(w, BuildUdp443(small)), "kucuk UDP paketleri es geciliyor");

                Check(st.SendErrors == errAfterReal,
                      "es gecilen paketler icin hic gonderim denenmedi");
            }
        }

        private static bool FeedUdp(DesyncWorker w, byte[] pkt)
        {
            byte* dst = w.RecvBuffer;
            for (int i = 0; i < pkt.Length; i++) dst[i] = pkt[i];
            var addr = default(DivertAddress);
            addr.Outbound = true;
            return w.HandleQuic(pkt.Length, ref addr);
        }

        private static byte[] BuildUdp443(byte[] payload)
        {
            var pkt = new byte[28 + payload.Length];
            pkt[0] = 0x45;
            pkt[9] = 17;                                    // UDP
            pkt[2] = (byte)(pkt.Length >> 8); pkt[3] = (byte)pkt.Length;
            pkt[12] = 10; pkt[15] = 1;
            pkt[16] = 1; pkt[17] = 2; pkt[18] = 3; pkt[19] = 4;
            pkt[20] = 0xC0;                                  // kaynak port
            pkt[22] = 0x01; pkt[23] = 0xBB;                  // hedef port 443
            int ulen = 8 + payload.Length;
            pkt[24] = (byte)(ulen >> 8); pkt[25] = (byte)ulen;
            Buffer.BlockCopy(payload, 0, pkt, 28, payload.Length);
            return pkt;
        }

        // --- IPv6
        private static void TestIPv6()
        {
            Console.WriteLine();
            Console.WriteLine("- IPv6 paket yolu");

            byte[] payload = Fakes.BuildTlsClientHello("discord.com");
            byte[] pkt = BuildTcp6Packet(payload, Packet.TcpPsh);

            fixed (byte* p = pkt)
            {
                PacketView v;
                bool ok = Packet.Parse(p, pkt.Length, out v);
                Check(ok, "IPv6 paketi ayristirildi");
                if (!ok) return;

                Check(v.IsV6 && v.Protocol == Packet.ProtoTcp, "IPv6 + TCP tanindi");
                Check(v.IpHeaderLen == 40, "IPv6 baslik uzunlugu 40");
                Check(v.PayloadOffset == 60, "yuk konumu 60 (40 IP + 20 TCP)",
                      "gelen: " + v.PayloadOffset);
                Check(v.PayloadLength == payload.Length, "yuk uzunlugu dogru",
                      "gelen: " + v.PayloadLength + ", beklenen: " + payload.Length);

                HostLocation h = Hostname.FindSni(v.Payload, v.PayloadLength);
                Check(h.Found, "IPv6 paketinde SNI bulundu");

                // IPv6 uzunluk alani (ofset 4-5) TCP+yuk toplamini gostermeli
                int declared = (p[4] << 8) | p[5];
                Check(declared == 20 + payload.Length, "IPv6 payload-length alani tutarli",
                      "beyan: " + declared);
            }

            // Ayni korumalar IPv6 tarafinda da gecerli olmali
            var cfg = new Config();
            var st = new Stats();
            using (var w = new DesyncWorker(cfg, st, IntPtr.Zero, 0))
            {
                byte[] syn6 = BuildTcp6Packet(payload, (byte)(Packet.TcpSyn | Packet.TcpPsh));
                Check(!Feed(w, syn6), "IPv6 SYN paketi es geciliyor");

                cfg.EnableIPv6 = false;
                byte[] psh6 = BuildTcp6Packet(payload, Packet.TcpPsh);
                Check(!Feed(w, psh6), "enable_ipv6=false iken IPv6 paketine dokunulmuyor");
                Check(st.SendErrors == 0, "IPv6 korumalarinda gonderim denenmedi");
            }
        }

        private static byte[] BuildTcp6Packet(byte[] payload, byte flags)
        {
            var pkt = new byte[40 + 20 + payload.Length];
            pkt[0] = 0x60;                                   // version 6
            int plen = 20 + payload.Length;
            pkt[4] = (byte)(plen >> 8); pkt[5] = (byte)plen; // payload length
            pkt[6] = 6;                                      // next header = TCP
            pkt[7] = 64;                                     // hop limit
            pkt[8] = 0x20; pkt[9] = 0x01;                    // src 2001::1
            pkt[23] = 1;
            pkt[24] = 0x26; pkt[25] = 0x07;                  // dst 2607::2
            pkt[39] = 2;

            int t = 40;
            pkt[t] = 0xC0;                                   // src port
            pkt[t + 2] = 0x01; pkt[t + 3] = 0xBB;            // dst port 443
            pkt[t + 4] = 0x11; pkt[t + 5] = 0x22; pkt[t + 6] = 0x33; pkt[t + 7] = 0x44;
            pkt[t + 12] = 0x50;                              // data offset 5
            pkt[t + 13] = flags;
            Buffer.BlockCopy(payload, 0, pkt, 60, payload.Length);
            return pkt;
        }

        // --- filtreler
        private static void TestFilters()
        {
            Console.WriteLine();
            Console.WriteLine("- WinDivert filtre soz dizimi");

            var cfg = new Config();
            var eng = new DpiEngine(cfg);

            string[] tcp = eng.BuildTcpFilterCandidates();
            string[] quic = eng.BuildQuicFilterCandidates();
            string[] cont = eng.BuildContinuationFilterCandidates();

            for (int i = 0; i < tcp.Length; i++)
                Check(CompileWithMessage(tcp[i], i == 0), "TCP aday " + (i + 1) + " derleniyor");

            for (int i = 0; i < quic.Length; i++)
                Check(CompileWithMessage(quic[i], i == 0), "QUIC aday " + (i + 1) + " derleniyor");

            for (int i = 0; i < cont.Length; i++)
                Check(CompileWithMessage(cont[i], false), "TCP devam aday " + (i + 1) + " derleniyor");

            // En dar filtreler kullanilabilmeli; genise dusmek basarimi dusurur.
            Check(DpiEngine.FilterCompiles(tcp[0], DivertLayer.Network),
                  "TCP en dar filtre (payload duyarli) kabul edildi");
            Check(DpiEngine.FilterCompiles(quic[0], DivertLayer.Network),
                  "QUIC en dar filtre (Initial duyarli) kabul edildi");
            Check(DpiEngine.FilterCompiles(cont[0], DivertLayer.Network),
                  "TCP devam en dar filtre kabul edildi");
        }

        private static bool CompileWithMessage(string filter, bool verbose)
        {
            byte* err = null;
            uint pos = 0;
            bool ok;
            try
            {
                ok = WinDivert.WinDivertHelperCompileFilter(
                    filter, DivertLayer.Network, null, 0, &err, &pos);
            }
            catch (DllNotFoundException)
            {
                Console.WriteLine("         WinDivert.dll bulunamadi - test atlandi");
                return false;
            }

            if (!ok)
            {
                string msg = err == null ? "(mesaj yok)" : Marshal.PtrToStringAnsi(new IntPtr(err));
                Console.WriteLine("         konum " + pos + ": " + msg);
                Console.WriteLine("         " + filter);
            }
            else if (verbose)
            {
                Console.WriteLine("         " + Shorten(filter));
            }
            return ok;
        }

        private static string Shorten(string s)
        {
            return s.Length <= 150 ? s : s.Substring(0, 147) + "...";
        }

        // --- sahte ClientHello
        private static void TestFakeClientHello()
        {
            Console.WriteLine();
            Console.WriteLine("- sahte TLS ClientHello");

            byte[] fake = Fakes.BuildTlsClientHello("www.iana.org");
            Check(fake.Length > 100 && fake.Length < 600,
                  "makul boyut (" + fake.Length + " bayt)");
            Check(fake[0] == 0x16 && fake[1] == 0x03 && fake[5] == 0x01,
                  "kayit ve el sikisma basliklari dogru");

            int recLen = (fake[3] << 8) | fake[4];
            Check(recLen == fake.Length - 5,
                  "kayit uzunlugu tutarli", "beyan " + recLen + ", gercek " + (fake.Length - 5));

            int hsLen = (fake[6] << 16) | (fake[7] << 8) | fake[8];
            Check(hsLen == fake.Length - 9,
                  "el sikisma uzunlugu tutarli", "beyan " + hsLen + ", gercek " + (fake.Length - 9));

            // Kendi ureticimizin ciktisini kendi ayristiricimiz okuyabilmeli.
            fixed (byte* p = fake)
            {
                HostLocation h = Hostname.FindSni(p, fake.Length);
                Check(h.Found, "ayristirici sahte paketteki SNI'yi buluyor");
                if (h.Found)
                {
                    string sni = Encoding.ASCII.GetString(fake, h.Offset, h.Length);
                    Check(sni == "www.iana.org", "SNI degeri dogru", "okunan: " + sni);
                }
            }
        }

        // --- SNI ayristirici
        private static void TestSniParser()
        {
            Console.WriteLine();
            Console.WriteLine("- SNI ayristirici");

            byte[] ch = Fakes.BuildTlsClientHello("www.youtube.com");
            fixed (byte* p = ch)
            {
                HostLocation h = Hostname.FindSni(p, ch.Length);
                Check(h.Found && h.Length == 15, "www.youtube.com bulundu");

                // Kirpilmis paket cokmemeli, sadece bulamamali.
                for (int cut = 1; cut < ch.Length; cut += 7)
                {
                    HostLocation t = Hostname.FindSni(p, cut);
                    if (t.Found && (t.Offset + t.Length > cut))
                    {
                        Check(false, "kirpilmis pakette sinir asimi", "kesim " + cut);
                        return;
                    }
                }
                Check(true, "kirpilmis paketlerde sinir asimi yok (" + ch.Length + " varyant)");
            }

            // Cop veri ayristiriciyi dusurmemeli.
            var rnd = new Random(1);
            var junk = new byte[512];
            for (int i = 0; i < 400; i++)
            {
                rnd.NextBytes(junk);
                junk[0] = 0x16; junk[1] = 0x03; junk[5] = 0x01;
                fixed (byte* p = junk) { Hostname.FindSni(p, junk.Length); }
            }
            Check(true, "rastgele veride cokme yok (400 deneme)");
        }

        // --- bolme noktasi
        private static void TestMidSld()
        {
            Console.WriteLine();
            Console.WriteLine("- bolme konumu (midsld)");

            CheckSplit("www.youtube.com", 7);   // "www." (4) + "you" (3) -> 'y'+3
            CheckSplit("youtube.com", 3);       // "you" -> 3
            CheckSplit("a.b.example.com", 7);   // "exam" ortasi
            CheckSplit("localhost", 4);         // nokta yok -> ortadan
        }

        // --- split_pos secimi
        // Uc kip de gercekten farkli konum uretmeli. Sabit sayi, alan adi bulunsa
        // da gecerli olmali; yoksa "pos 2" sessizce midsld'e donusuyor ve
        // ayarlayicinin pos stratejileri onun kopyasi oluyor.
        private static void TestSplitPosModes()
        {
            Console.WriteLine();
            Console.WriteLine("- split_pos kipleri");

            byte[] ch = Fakes.BuildTlsClientHello("www.youtube.com");
            fixed (byte* p = ch)
            {
                HostLocation h = Hostname.FindSni(p, ch.Length);
                bool found;

                int fixedAt = DesyncWorker.ChooseSplitPos(p, ch.Length, true, false, 2, out found);
                Check(fixedAt == 2 && !found,
                      "split_pos = 2 alan adi bulunsa da sabit konumu kullaniyor",
                      "gelen " + fixedAt);

                int hostAt = DesyncWorker.ChooseSplitPos(p, ch.Length, true, true, 0, out found);
                Check(hostAt == h.Offset + 1 && found,
                      "split_pos = host alan adinin ilk harfinden sonra boluyor",
                      "gelen " + hostAt + ", beklenen " + (h.Offset + 1));

                int midAt = DesyncWorker.ChooseSplitPos(p, ch.Length, true, false, 0, out found);
                Check(midAt == Hostname.MidSld(p, h.Offset, h.Length) && found && midAt != hostAt,
                      "split_pos = midsld ana etiketin ortasindan boluyor",
                      "gelen " + midAt);

                int oversize = DesyncWorker.ChooseSplitPos(p, ch.Length, true, false, ch.Length + 1, out found);
                Check(oversize == -1, "yuk disina tasan sabit konum bolmeyi iptal ediyor",
                      "gelen " + oversize);
            }

            // Alan adi yoksa varsayilan konumlar: TLS 3, HTTP 2.
            byte[] noHost = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nAccept: */*\r\n\r\n");
            fixed (byte* p = noHost)
            {
                bool found;
                Check(DesyncWorker.ChooseSplitPos(p, noHost.Length, false, false, 0, out found) == 2 && !found,
                      "Host basligi yokken HTTP varsayilani 2");
                Check(DesyncWorker.ChooseSplitPos(p, noHost.Length, true, false, 0, out found) == 3 && !found,
                      "SNI yokken TLS varsayilani 3");
            }
        }

        private static void CheckSplit(string host, int expected)
        {
            byte[] b = Encoding.ASCII.GetBytes(host);
            fixed (byte* p = b)
            {
                int at = Hostname.MidSld(p, 0, b.Length);
                bool ok = at == expected && at > 0 && at < b.Length;
                string shown = host.Substring(0, Math.Min(at, host.Length)) + "|" +
                               host.Substring(Math.Min(at, host.Length));
                Check(ok, host + " -> " + shown,
                      ok ? null : "beklenen " + expected + ", gelen " + at);
            }
        }

        // --- HTTP Host
        private static void TestHttpHost()
        {
            Console.WriteLine();
            Console.WriteLine("- HTTP Host ayristirici");

            CheckHost("GET / HTTP/1.1\r\nHost: www.example.com\r\n\r\n", "www.example.com");
            CheckHost("GET / HTTP/1.1\r\nhost:www.example.com\r\n\r\n", "www.example.com");
            CheckHost("GET / HTTP/1.1\r\nHOST:  example.com:8080\r\n\r\n", "example.com");
            CheckHost("POST /x HTTP/1.1\r\nX: 1\r\nHost: a.b.co\r\nY: 2\r\n\r\n", "a.b.co");

            // Istek satirindaki "host:" yanlislikla eslesmemeli
            CheckHost("GET /host:evil HTTP/1.1\r\nHost: real.com\r\n\r\n", "real.com");

            byte[] rewritten = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");
            fixed (byte* p = rewritten)
            {
                uint saved;
                int at = Hostname.RewriteHttpHostCase(p, rewritten.Length, out saved);
                Check(at >= 0 && Encoding.ASCII.GetString(rewritten).Contains("\r\nhOSt: example.com"),
                      "http_host_case gercek basligi degistiriyor");

                // Geri alma tam olmali: tek bir bayt bile kalirsa checksum tutmaz.
                Hostname.RestoreHttpHostCase(p, at, saved);
                Check(Encoding.ASCII.GetString(rewritten).Contains("\r\nHost: example.com"),
                      "geri alma yuku tam olarak eski haline donduruyor");
            }
        }

        // --- ayar dosyasi
        private static void TestConfig()
        {
            Console.WriteLine();
            Console.WriteLine("- ayar dogrulama");

            var defaults = new Config();
            Check(defaults.TlsMode == "disorder" && !defaults.FakeTtl,
                  "ilk acilis varsayilanlari guvenli");
            Check(defaults.EffectiveFakeTtl(false) == 4 && defaults.EffectiveFakeTtl(true) == 4,
                  "fake_ttl_value_v6=0 iken IPv6 hop-limit IPv4 degerini izliyor");

            defaults.FakeTtlValueV6 = 6;
            Check(defaults.EffectiveFakeTtl(false) == 4 && defaults.EffectiveFakeTtl(true) == 6,
                  "fake_ttl_value_v6 ayarlandiginda IPv6 ayri hop-limit kullaniyor");

            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path,
                    "tls = yanlis-mod\n" +
                    "fake_ttl = belki\n" +
                    "fake_ttl_value_v6 = 7\n" +
                    "threads = 999\n" +
                    "bilinmeyen = deger\n",
                    new UTF8Encoding(false));
                Config c = Config.Load(path);
                Check(c.TlsMode == "disorder" && !c.FakeTtl && c.Threads == 16 && c.FakeTtlValueV6 == 7,
                      "gecersiz ayarlar guvenli degere donuyor ve fake_ttl_value_v6 okunuyor");
                Check(c.Warnings.Count == 4, "gecersiz ayarlar gorunur uyari uretiyor",
                      "uyari sayisi: " + c.Warnings.Count);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        private static void CheckHost(string req, string expected)
        {
            byte[] b = Encoding.ASCII.GetBytes(req);
            fixed (byte* p = b)
            {
                HostLocation h = Hostname.FindHttpHost(p, b.Length);
                string got = h.Found ? Encoding.ASCII.GetString(b, h.Offset, h.Length) : "(yok)";
                Check(got == expected, "Host = " + expected,
                      got == expected ? null : "gelen: " + got);
            }
        }

        // --- paket ayristirma
        private static void TestPacketParse()
        {
            Console.WriteLine();
            Console.WriteLine("- IP/TCP ayristirma");

            byte[] pkt = BuildTcpPacket("test-payload-abcdef");
            fixed (byte* p = pkt)
            {
                PacketView v;
                bool ok = Packet.Parse(p, pkt.Length, out v);
                Check(ok, "paket ayristirildi");
                if (!ok) return;

                Check(v.IsV6 == false && v.Protocol == Packet.ProtoTcp, "IPv4 + TCP");
                Check(v.IpHeaderLen == 20 && v.L4HeaderLen == 20, "baslik uzunluklari");
                Check(v.PayloadOffset == 40, "yuk konumu 40");
                Check(v.PayloadLength == 19, "yuk uzunlugu 19", "gelen " + v.PayloadLength);
                Check(Packet.TcpSeq(p, v.L4Offset) == 0x11223344, "seq okundu");

                // Bolme aritmetigi: iki parca orijinali tam kapsamali
                int split = 7;
                Check(split + (v.PayloadLength - split) == v.PayloadLength, "bolme toplami korunuyor");
                Check(Packet.TcpSeq(p, v.L4Offset) + (uint)split == 0x1122334B, "ikinci parca seq dogru");
            }

            // IP basligindaki uzunluk tampondan kisa ise fazlalik yuk sayilmamali
            byte[] padded = BuildTcpPacket(Encoding.ASCII.GetBytes("0123456789ABCDEFGHIJ"), Packet.TcpPsh);
            int declared = 40 + 10;                       // 10 bayt beyan, 20 bayt tamponda
            padded[2] = (byte)(declared >> 8); padded[3] = (byte)declared;
            fixed (byte* p = padded)
            {
                PacketView v;
                Packet.Parse(p, padded.Length, out v);
                Check(v.PayloadLength == 10, "dolgu baytlari yuk sayilmiyor",
                      "gelen yuk uzunlugu: " + v.PayloadLength);
            }

            // Bozuk paketler false donmeli, cokmemeli
            var rnd = new Random(7);
            var junk = new byte[64];
            for (int i = 0; i < 500; i++)
            {
                rnd.NextBytes(junk);
                fixed (byte* p = junk)
                {
                    PacketView v;
                    Packet.Parse(p, junk.Length, out v);
                }
            }
            Check(true, "bozuk paketlerde cokme yok (500 deneme)");
        }

        private static byte[] BuildTcpPacket(string payload)
        {
            byte[] pl = Encoding.ASCII.GetBytes(payload);
            var pkt = new byte[40 + pl.Length];

            pkt[0] = 0x45;                                   // IPv4, 20 bayt baslik
            pkt[9] = 6;                                      // TCP
            pkt[2] = (byte)(pkt.Length >> 8); pkt[3] = (byte)pkt.Length;
            // src 10.0.0.1 -> dst 1.2.3.4
            pkt[12] = 10; pkt[15] = 1;
            pkt[16] = 1; pkt[17] = 2; pkt[18] = 3; pkt[19] = 4;

            pkt[20] = 0xC0; pkt[21] = 0x00;                  // src port
            pkt[22] = 0x01; pkt[23] = 0xBB;                  // dst port 443
            pkt[24] = 0x11; pkt[25] = 0x22; pkt[26] = 0x33; pkt[27] = 0x44;   // seq
            pkt[32] = 0x50;                                  // data offset 5 -> 20 bayt
            pkt[33] = 0x18;                                  // PSH+ACK

            Buffer.BlockCopy(pl, 0, pkt, 40, pl.Length);
            return pkt;
        }

        // --- QUIC sahte
        private static void TestQuicFake()
        {
            Console.WriteLine();
            Console.WriteLine("- sahte QUIC Initial");

            byte[] q = Fakes.BuildQuicInitial(1250);
            Check(q.Length == 1250, "boyut 1250", "gelen " + q.Length);
            Check(q[0] >= 0xc0 && q[0] <= 0xcf, "uzun baslik / Initial tipi");
            Check(q[1] == 0 && q[2] == 0 && q[3] == 0 && q[4] == 1, "QUIC v1 surum alani");
            Check(q[5] == 8, "DCID uzunlugu 8");
            Check(q[14] == 8, "SCID uzunlugu 8");
            Check(q[23] == 0x00, "token uzunlugu 0");
            Check(q.Length >= 1200, "asgari Initial boyutunu asiyor");
        }
    }
}
