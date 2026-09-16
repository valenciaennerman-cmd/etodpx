// Aktif baglanti icin geri alinabilir DNS onarimi.
//
// Yalnizca kullanici "Otomatik duzelt" dedikten ve ayarlayici sistem DNS'inin
// bozuk oldugunu olctukten sonra calisir. Mevcut sunucular degisiklikten once
// diske yazilir; yeni ayar dogrulanamazsa arayuz yedegi geri yukler.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace Etodpx
{
    internal sealed class DnsBackup
    {
        public int InterfaceIndex;
        public string InterfaceAlias;
        public string[] Servers;
        public bool WasDhcp;
        public string FilePath;
        // Kayit defterinden okunan, aile bazinda elle yazilmis sunucular.
        // null = eski bicimli yedek; bos dizi = o aile DHCP'deydi.
        public string[] Static4;
        public string[] Static6;
        // Link-local disi IPv6 adresi var mi. Yoksa IPv6 cozumleyicisi yazmak
        // her sorguya olu bir deneme eklemekten baska ise yaramaz.
        public bool HasGlobalV6;
        // Dokunacagimiz sunucularin DEGISIKLIKTEN ONCEKI sifreleme ayari.
        // "sunucu|autoupgrade|udpfallback|sablon" ya da kayit yoksa "sunucu|-".
        // null = eski bicimli yedek; o dosyalarda bu bilgi yok.
        public string[] DohBefore;
        // Son basarili onarimda etodpx'in bu adaptore yazdigi sunucular.
        // Boylece kullanici daha sonra DNS'i elle degistirirse eski bir yedegi
        // geri yukleyip onun yeni ayarini ezmiyoruz.
        public string[] ManagedServers;
    }

    internal sealed class DnsProvider
    {
        public string Name;
        public string Template;
        public string[] Ipv4;
        public string[] Ipv6;

        public string[] Servers(bool includeIpv6)
        {
            var values = new List<string>();
            if (Ipv4 != null) values.AddRange(Ipv4);
            if (includeIpv6 && Ipv6 != null) values.AddRange(Ipv6);
            return values.ToArray();
        }
    }

    internal static class NetworkRepair
    {
        private const int CommandTimeoutMs = 20000;

        // "Tek tikla duzelt" akisinin olctugu hedefler. Hem strateji bulucuya
        // hem DNS dogrulamasina ayni liste gidiyor; ikisi ayrisirsa DNS'i
        // Bir ana ada gore onaylayip uygulamanin API/CDN ucunu bozuk birakmak
        // mumkun olurdu. Ayni hizmetin farkli sunucu havuzlari DPI kutusundan
        // ayni cevabi vermeyebiliyor; kazanan ayar gercek kullanim uclarinin
        // hepsini gecmek zorunda.
        internal static readonly string[] RepairTargets =
        {
            "discord.com", "discordapp.com", "cdn.discordapp.com",
            "updates.discord.com", "gateway.discord.gg", "discord.gg", "status.discord.com",
            "roblox.com", "www.roblox.com", "apis.roblox.com", "auth.roblox.com",
            "assetgame.roblox.com", "gamejoin.roblox.com", "clientsettings.roblox.com"
        };

        // Tek saglayici bazi ISS'lerde engellenebiliyor. Sirayla uc bagimsiz
        // Windows DoH saglayicisi deneniyor; her deneme gercek hedeflerle
        // dogrulaniyor.
        internal static readonly DnsProvider[] DohProviders =
        {
            new DnsProvider
            {
                Name = "Cloudflare",
                Template = "https://cloudflare-dns.com/dns-query",
                Ipv4 = new[] { "1.1.1.1", "1.0.0.1" },
                Ipv6 = new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" }
            },
            new DnsProvider
            {
                Name = "Google",
                Template = "https://dns.google/dns-query",
                Ipv4 = new[] { "8.8.8.8", "8.8.4.4" },
                Ipv6 = new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" }
            },
            new DnsProvider
            {
                Name = "Quad9",
                Template = "https://dns.quad9.net/dns-query",
                Ipv4 = new[] { "9.9.9.9", "149.112.112.112" },
                Ipv6 = new[] { "2620:fe::fe", "2620:fe::9" }
            }
        };

        // Ayni liste hem yedeklemede hem geri almada kullanilmali; aksi halde
        // basarisiz bir saglayici denemesinden makine-geneli DoH kaydi kalir.
        private const string AllDohTargets =
            "'1.1.1.1','1.0.0.1','2606:4700:4700::1111','2606:4700:4700::1001'," +
            "'8.8.8.8','8.8.4.4','2001:4860:4860::8888','2001:4860:4860::8844'," +
            "'9.9.9.9','149.112.112.112','2620:fe::fe','2620:fe::9'";

        // Program.AppDir yerine dogrudan - boylece dosya WPF'siz derleniyor ve
        // oz test onu iceri alabiliyor.
        private static string AppDir()
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }

        public static DnsBackup CaptureActiveDns(out string error)
        {
            error = null;
            const string script =
                "$ErrorActionPreference='Stop';" +
                "$items=@();" +
                "foreach($r in @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0')){" +
                " if($r.NextHop -eq '0.0.0.0'){continue};" +
                " $a=Get-NetAdapter -InterfaceIndex $r.InterfaceIndex -ErrorAction SilentlyContinue;" +
                " $i=Get-NetIPInterface -InterfaceIndex $r.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue;" +
                " if($a -and $i -and $a.Status -eq 'Up'){" +
                "  $items+=[pscustomobject]@{Index=[int]$r.InterfaceIndex;Alias=[string]$a.Name;" +
                "   Metric=[int]$r.RouteMetric+[int]$i.InterfaceMetric;Hardware=[bool]$a.HardwareInterface}" +
                " }" +
                "};" +
                // Gercek varsayilan yol en dusuk toplam metriktir. Donanimi once
                // secmek VPN/tunel acikken yanlis adaptore DNS yaziyordu.
                "$best=$items|Sort-Object Metric,@{Expression={if($_.Hardware){0}else{1}}}|Select-Object -First 1;" +
                "if(-not $best){throw 'Aktif internet baglantisi bulunamadi'};" +
                "$adapter=Get-NetAdapter -InterfaceIndex $best.Index -ErrorAction Stop;" +
                "$dns=@(Get-DnsClientServerAddress -InterfaceIndex $best.Index|" +
                " ForEach-Object {$_.ServerAddresses}|Where-Object {$_});" +
                // InterfaceGuid zaten '{...}' bicimli. Bir kez daha sarmalayinca yol
                // '{{...}}' oluyordu: okuma hep bos donuyor, elle DNS yazilmis makine
                // DHCP sanilip geri almada DHCP'ye donduruluyordu.
                "$g=[string]$adapter.InterfaceGuid;" +
                "if($g -notmatch '^\\{'){$g='{'+$g+'}'};" +
                "$k4='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\'+$g;" +
                "$k6='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\Interfaces\\'+$g;" +
                "$n4=[string](Get-ItemProperty -Path $k4 -Name NameServer -ErrorAction SilentlyContinue).NameServer;" +
                "$n6=[string](Get-ItemProperty -Path $k6 -Name NameServer -ErrorAction SilentlyContinue).NameServer;" +
                "$dhcp=if([string]::IsNullOrWhiteSpace($n4)-and[string]::IsNullOrWhiteSpace($n6)){'1'}else{'0'};" +
                // Global IPv6 yoksa IPv6 cozumleyicisi yazmiyoruz.
                "$v6=@(Get-NetIPAddress -InterfaceIndex $best.Index -AddressFamily IPv6" +
                " -ErrorAction SilentlyContinue|Where-Object {$_.PrefixOrigin -ne 'WellKnown' -and" +
                " -not $_.IPAddress.StartsWith('fe80') -and $_.IPAddress -ne '::1'});" +
                "$enc=[Text.Encoding]::UTF8;" +
                // Dokunacagimiz sunucularin mevcut sifreleme ayari; geri alma
                // bunu birebir yerine koyuyor. Cmdlet eski Windows'ta yok.
                "$doh=@();" +
                "if(Get-Command Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue){" +
                " foreach($s in @(" + AllDohTargets + ")){" +
                "  $e=Get-DnsClientDohServerAddress -ServerAddress $s -ErrorAction SilentlyContinue;" +
                "  if($e){$doh+=($s+'|'+[int][bool]$e.AutoUpgrade+'|'+[int][bool]$e.AllowFallbackToUdp+'|'+$e.DohTemplate)}" +
                "  else{$doh+=($s+'|-')}" +
                " }" +
                "};" +
                "'ETODPX_DOH_B64='+[Convert]::ToBase64String($enc.GetBytes(($doh -join \"`n\")));" +
                "'ETODPX_INDEX='+$best.Index;" +
                "'ETODPX_ALIAS_B64='+[Convert]::ToBase64String($enc.GetBytes($best.Alias));" +
                "'ETODPX_WAS_DHCP='+$dhcp;" +
                "'ETODPX_HAS_V6='+$(if($v6.Count -gt 0){'1'}else{'0'});" +
                "'ETODPX_STATIC4_B64='+[Convert]::ToBase64String($enc.GetBytes($n4));" +
                "'ETODPX_STATIC6_B64='+[Convert]::ToBase64String($enc.GetBytes($n6));" +
                "'ETODPX_SERVERS_B64='+[Convert]::ToBase64String($enc.GetBytes(($dns -join \"`n\")))";

            ProcessResult run = RunPowerShell(script);
            if (!run.Success)
            {
                error = CleanError(run);
                return null;
            }

            int index;
            if (!int.TryParse(Marker(run.Output, "ETODPX_INDEX"), out index) || index <= 0)
            {
                error = "Aktif ağ bağlantısı belirlenemedi.";
                return null;
            }

            try
            {
                string alias = Decode(Marker(run.Output, "ETODPX_ALIAS_B64"));
                string serverText = Decode(Marker(run.Output, "ETODPX_SERVERS_B64"));
                string[] servers = serverText.Length == 0
                    ? new string[0]
                    : serverText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                string path = Path.Combine(AppDir(), "etodpx-dns-backup-" + index + ".conf");
                var backup = new DnsBackup
                {
                    InterfaceIndex = index,
                    InterfaceAlias = alias,
                    Servers = servers,
                    WasDhcp = Marker(run.Output, "ETODPX_WAS_DHCP") == "1",
                    HasGlobalV6 = Marker(run.Output, "ETODPX_HAS_V6") == "1",
                    Static4 = SplitServers(Decode(Marker(run.Output, "ETODPX_STATIC4_B64"))),
                    Static6 = SplitServers(Decode(Marker(run.Output, "ETODPX_STATIC6_B64"))),
                    DohBefore = SplitLines(Decode(Marker(run.Output, "ETODPX_DOH_B64"))),
                    FilePath = path
                };

                DnsBackup existing = LoadBackup(path);
                if (CanReuseBackup(existing, servers))
                {
                    // v2.3 yalniz Cloudflare kayitlarini yedekliyordu. Google ve
                    // Quad9'un mevcut makine-geneli ayarlarini eklerken eski
                    // Cloudflare tabanini kesinlikle ezme.
                    existing.DohBefore = MergeDohEntries(existing.DohBefore, backup.DohBefore);
                    if (existing.ManagedServers == null) existing.ManagedServers = servers;
                    SaveBackup(existing);
                    return existing;
                }

                SaveBackup(backup);
                return backup;
            }
            catch (Exception ex)
            {
                error = "DNS yedeği oluşturulamadı: " + ex.Message;
                return null;
            }
        }

        public static bool ConfigureDoh(DnsBackup backup, DnsProvider provider,
                                        string[] validationHosts, out string error)
        {
            error = null;
            if (backup == null)
            {
                error = "DNS yedeği bulunamadı.";
                return false;
            }
            if (provider == null)
            {
                error = "Şifreli DNS sağlayıcısı bulunamadı.";
                return false;
            }
            // Zehirlenme alan adi basina: ISS Discord'a dogru cevap verip
            // Roblox'a engel sayfasi dondurebiliyor. Tek ada bakan bir dogrulama
            // "DNS duzeldi" deyip digerini bozuk birakiyordu.
            if (validationHosts == null || validationHosts.Length == 0)
            {
                error = "Doğrulama alan adı verilmedi.";
                return false;
            }
            foreach (string host in validationHosts)
            {
                if (Uri.CheckHostName(host) != UriHostNameType.Dns)
                {
                    error = "Doğrulama alan adı geçersiz: " + host;
                    return false;
                }
            }

            // Yalnizca IPv4'u duzeltmek yetmiyor: cift yiginli bir hatta Windows
            // once IPv6 cozumleyicisini soruyor, yani ISS'nin zehirli sunucusu
            // cevap vermeye devam ediyor. Global IPv6 varsa iki aileyi de yaziyoruz.
            string[] selected = provider.Servers(backup.HasGlobalV6);
            string servers = PsArray(selected);

            string script = BuildDohScript(servers, backup.InterfaceIndex,
                                           provider.Template, validationHosts);

            ProcessResult run = RunPowerShell(script);
            if (run.Success && Marker(run.Output, "ETODPX_DNS_CHANGED") == "1") return true;
            error = CleanError(run);
            return false;
        }

        // netsh yerine DoH cmdlet'leri.
        //
        // "netsh dnsclient set encryption ... dothost=:" bu makinede sozdizimi
        // hatasi veriyor ama CIKIS KODU 0 donuyor. Eski kod cikis koduna bakip
        // "kaydedildi" sayiyor, add'e dusen yedek yol hic calismiyordu - yani
        // sifreli DNS hicbir zaman gercekten kurulmadi. Kullanicinin makinesinde
        // AutoUpgrade hala False cikmasinin sebebi bu.
        //
        // Cmdlet gercek bir hata firlatiyor, ustelik sonucu geri okuyup
        // dogruluyoruz: sessizce hicbir sey yapmayan bir adim kalmasin.
        internal static string BuildDohScript(string servers, int interfaceIndex,
                                              string template, string[] validationHosts)
        {
            return
                "$ErrorActionPreference='Stop';" +
                "if(-not (Get-Command Set-DnsClientDohServerAddress -ErrorAction SilentlyContinue))" +
                "{throw 'Bu Windows surumu sifreli DNS ayarini desteklemiyor'};" +
                "$t=" + PsQuote(template) + ";" +
                "function Set-Doh([string]$s){" +
                " if(Get-DnsClientDohServerAddress -ServerAddress $s -ErrorAction SilentlyContinue){" +
                "  Set-DnsClientDohServerAddress -ServerAddress $s -DohTemplate $t" +
                "   -AutoUpgrade $true -AllowFallbackToUdp $false | Out-Null" +
                " }else{" +
                "  Add-DnsClientDohServerAddress -ServerAddress $s -DohTemplate $t" +
                "   -AutoUpgrade $true -AllowFallbackToUdp $false | Out-Null" +
                " };" +
                // Geri okuyup dogrula; yoksa sessiz basarisizlik tekrarlanir.
                " $v=Get-DnsClientDohServerAddress -ServerAddress $s -ErrorAction SilentlyContinue;" +
                " if(-not $v -or -not $v.AutoUpgrade -or $v.AllowFallbackToUdp)" +
                "  {throw ('Sifreli DNS etkinlestirilemedi: '+$s)}" +
                "};" +
                "foreach($s in @(" + servers + ")){Set-Doh $s};" +
                "Set-DnsClientServerAddress -InterfaceIndex " + interfaceIndex +
                " -ServerAddresses @(" + servers + ");" +
                "Clear-DnsClientCache;" +
                // Genel bir alan adinin acilmasi yeterli degil: ISS yalnizca
                // Discord'a NXDOMAIN verebilir. Dogrudan hedefleri ve bilinen
                // zehirli adresleri denetliyoruz. Engel alan adi basina konuldugu
                // icin HEPSI cozulmeden "DNS duzeldi" demiyoruz - biri hala
                // zehirliyse o site icin hicbir sey degismemis olur.
                "$bad=@('195.175.254.2','0.0.0.0');" +
                "foreach($h in @(" + PsArray(validationHosts) + ")){" +
                " $ok=$false;" +
                " for($i=0;$i -lt 12;$i++){" +
                "  try{$a=@(Resolve-DnsName -Name $h -Type A -DnsOnly" +
                "   -ErrorAction Stop|Where-Object {$_.IPAddress -and $bad -notcontains $_.IPAddress});" +
                "   if($a.Count -gt 0){$ok=$true;break}}catch{}" +
                "  Start-Sleep -Milliseconds 800" +
                " };" +
                " if(-not $ok){throw ('Yeni DNS ayariyla cozumlenemedi: '+$h)}" +
                "};" +
                "'ETODPX_DNS_CHANGED=1'";
        }

        public static void MarkManagedDns(DnsBackup backup, DnsProvider provider)
        {
            if (backup == null || provider == null) return;
            backup.ManagedServers = provider.Servers(backup.HasGlobalV6);
            SaveBackup(backup);
        }

        public static bool Restore(DnsBackup backup, out string error)
        {
            return RestoreCore(backup, true, out error);
        }

        // Saglayicilar arasinda gecis yaparken ilk geri alma noktasini silme.
        public static bool RestoreForRetry(DnsBackup backup, out string error)
        {
            return RestoreCore(backup, false, out error);
        }

        private static bool RestoreCore(DnsBackup backup, bool deleteBackup, out string error)
        {
            error = null;
            if (backup == null)
            {
                error = "Geri yüklenecek DNS yedeği bulunamadı.";
                return false;
            }

            // Once iki aileyi de DHCP'ye dondur, sonra elle yazilmis olanlari geri
            // koy. Tek adimda -ServerAddresses vermek, yalnizca bir ailesi elle
            // ayarli makinede digerini bizim Cloudflare adresimizde birakiyordu.
            string set = "Set-DnsClientServerAddress -InterfaceIndex " + backup.InterfaceIndex +
                         " -ResetServerAddresses;";

            string[] servers = RestoreServers(backup);
            if (servers.Length > 0)
            {
                var values = new List<string>();
                foreach (string server in servers) values.Add(PsQuote(server));
                set += "Set-DnsClientServerAddress -InterfaceIndex " + backup.InterfaceIndex +
                       " -ServerAddresses @(" + string.Join(",", values.ToArray()) + ");";
            }

            // Sunucu adreslerini geri koymak yetmiyor: netsh ile yazdigimiz
            // sifreleme ayari makine genelinde ve sunucuya bagli kaliyor.
            // Kullanicinin DNS'i zaten 1.1.1.1 ise geri alma sonrasi bile
            // "DoH zorunlu" durumunda kaliyordu.
            foreach (string cmd in DohRestoreCommands(backup)) set += cmd;

            ProcessResult run = RunPowerShell(
                "$ErrorActionPreference='Stop';" + set +
                "Clear-DnsClientCache;'ETODPX_DNS_RESTORED=1'");
            if (run.Success && Marker(run.Output, "ETODPX_DNS_RESTORED") == "1")
            {
                if (deleteBackup && !string.IsNullOrEmpty(backup.FilePath))
                {
                    try { File.Delete(backup.FilePath); } catch { }
                }
                return true;
            }
            error = CleanError(run);
            return false;
        }

        // Geri yuklenecek sunucular; elle ayarli aile yoksa bos doner (DHCP kalir).
        internal static string[] RestoreServers(DnsBackup backup)
        {
            if (backup == null) return new string[0];

            if (backup.Static4 != null || backup.Static6 != null)
            {
                var l = new List<string>();
                if (backup.Static4 != null) l.AddRange(backup.Static4);
                if (backup.Static6 != null) l.AddRange(backup.Static6);
                return l.ToArray();
            }

            // Eski bicimli yedek: aile ayrimi yok, yalnizca "hepsi DHCP miydi" var.
            if (backup.WasDhcp || backup.Servers == null) return new string[0];
            return backup.Servers;
        }

        // Yedekteki sifreleme durumunu geri koyan PowerShell parcalari.
        // Kayit bizden once yoksa siliniyor, varsa eski degerleriyle yaziliyor.
        //
        // netsh degil cmdlet: "netsh dnsclient set encryption" yalnizca statik
        // listede ZATEN olan bir kaydi duzenliyor, yoksa sozdizimi hatasi
        // veriyor - elle denendiginde de oyle oldu. Ayrica DohBefore'u ancak
        // Get-DnsClientDohServerAddress varsa dolduruyoruz, yani buraya
        // gelindiginde cmdlet ailesi kesinlikle mevcut.
        internal static string[] DohRestoreCommands(DnsBackup backup)
        {
            var cmds = new List<string>();
            if (backup == null || backup.DohBefore == null) return cmds.ToArray();

            foreach (string line in backup.DohBefore)
            {
                string[] f = line.Split('|');
                if (f.Length < 2 || f[0].Length == 0) continue;

                if (f[1] == "-")
                {
                    cmds.Add("Remove-DnsClientDohServerAddress -ServerAddress " + PsQuote(f[0]) +
                             " -ErrorAction SilentlyContinue | Out-Null;");
                    continue;
                }
                if (f.Length < 4) continue;

                cmds.Add("Set-DnsClientDohServerAddress -ServerAddress " + PsQuote(f[0]) +
                         " -DohTemplate " + PsQuote(f[3]) +
                         " -AutoUpgrade $" + (f[1] == "1" ? "true" : "false") +
                         " -AllowFallbackToUdp $" + (f[2] == "1" ? "true" : "false") +
                         " -ErrorAction SilentlyContinue | Out-Null;");
            }
            return cmds.ToArray();
        }

        // Satir basina bir kayit; bos gelirse null (bilgi yok) doner.
        internal static string[] SplitLines(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // NameServer metni virgul ya da bosluk ayrimli geliyor.
        internal static string[] SplitServers(string value)
        {
            if (string.IsNullOrEmpty(value)) return new string[0];
            return value.Split(new[] { ',', ' ', '\t', '\r', '\n' },
                               StringSplitOptions.RemoveEmptyEntries);
        }

        internal static bool CanReuseBackup(DnsBackup existing, string[] currentServers)
        {
            if (existing == null) return false;
            if (existing.ManagedServers != null)
                return SameServers(existing.ManagedServers, currentServers);

            // v2.3 yedeklerinde managed_servers alani yoktu ve o surum yalnizca
            // Cloudflare yazabiliyordu. Google/Quad9'u burada kabul etmek,
            // kullanicinin sonradan elle sectigi DNS'i eski yedekle ezerdi.
            DnsProvider legacyProvider = DohProviders[0];
            return SameServers(legacyProvider.Servers(false), currentServers) ||
                   SameServers(legacyProvider.Servers(true), currentServers);
        }

        internal static string[] MergeDohEntries(string[] original, string[] current)
        {
            if (original == null) return current;
            if (current == null) return original;

            var merged = new List<string>(original);
            foreach (string candidate in current)
            {
                string server = DohEntryServer(candidate);
                bool found = false;
                foreach (string saved in original)
                    if (string.Equals(DohEntryServer(saved), server,
                                      StringComparison.OrdinalIgnoreCase))
                    { found = true; break; }
                if (!found) merged.Add(candidate);
            }
            return merged.ToArray();
        }

        private static string DohEntryServer(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int bar = value.IndexOf('|');
            return bar < 0 ? value : value.Substring(0, bar);
        }

        private static bool SameServers(string[] left, string[] right)
        {
            left = left ?? new string[0];
            right = right ?? new string[0];
            if (left.Length != right.Length) return false;
            foreach (string a in left)
            {
                bool found = false;
                foreach (string b in right)
                    if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        private static string PsArray(string[] values)
        {
            var quoted = new List<string>();
            foreach (string value in values ?? new string[0]) quoted.Add(PsQuote(value));
            return string.Join(",", quoted.ToArray());
        }

        private static void SaveBackup(DnsBackup backup)
        {
            var lines = new List<string>
            {
                "# etodpx DNS geri alma noktasi",
                "interface_index=" + backup.InterfaceIndex,
                "interface_alias_b64=" + Encode(backup.InterfaceAlias ?? string.Empty),
                "was_dhcp=" + (backup.WasDhcp ? "true" : "false"),
                "has_global_v6=" + (backup.HasGlobalV6 ? "true" : "false"),
                "static4_b64=" + Encode(string.Join("\n", backup.Static4 ?? new string[0])),
                "static6_b64=" + Encode(string.Join("\n", backup.Static6 ?? new string[0])),
                "doh_before_b64=" + Encode(string.Join("\n", backup.DohBefore ?? new string[0])),
                "servers_b64=" + Encode(string.Join("\n", backup.Servers ?? new string[0]))
            };
            if (backup.ManagedServers != null)
                lines.Add("managed_servers_b64=" + Encode(string.Join("\n", backup.ManagedServers)));
            File.WriteAllLines(backup.FilePath, lines.ToArray(), new UTF8Encoding(false));
        }

        private static DnsBackup LoadBackup(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                int index = 0;
                string alias = string.Empty, servers = string.Empty;
                bool wasDhcp = false, hasV6 = false;
                // null kalirlarsa dosya eski bicimlidir; RestoreServers buna gore davranir.
                string[] static4 = null, static6 = null, dohBefore = null, managedServers = null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq).Trim();
                    string value = raw.Substring(eq + 1).Trim();
                    if (key == "interface_index") int.TryParse(value, out index);
                    else if (key == "interface_alias_b64") alias = Decode(value);
                    else if (key == "was_dhcp") bool.TryParse(value, out wasDhcp);
                    else if (key == "has_global_v6") bool.TryParse(value, out hasV6);
                    else if (key == "static4_b64") static4 = SplitServers(Decode(value));
                    else if (key == "static6_b64") static6 = SplitServers(Decode(value));
                    else if (key == "doh_before_b64") dohBefore = SplitLines(Decode(value));
                    else if (key == "managed_servers_b64")
                        managedServers = SplitServers(Decode(value));
                    else if (key == "servers_b64") servers = Decode(value);
                }
                if (index <= 0) return null;
                return new DnsBackup
                {
                    InterfaceIndex = index,
                    InterfaceAlias = alias,
                    WasDhcp = wasDhcp,
                    HasGlobalV6 = hasV6,
                    Static4 = static4,
                    Static6 = static6,
                    DohBefore = dohBefore,
                    ManagedServers = managedServers,
                    Servers = servers.Length == 0
                        ? new string[0]
                        : servers.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                    FilePath = path
                };
            }
            catch { return null; }
        }

        private static string Marker(string output, string key)
        {
            if (output == null) return null;
            string prefix = key + "=";
            foreach (string line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line.Substring(prefix.Length).Trim();
            return null;
        }

        private static string PsQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static string CleanError(ProcessResult run)
        {
            string text = (run.Error + "\n" + run.Output).Trim();
            if (text.Length == 0) text = "Windows ağ ayarı komutu başarısız oldu.";
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? text : lines[lines.Length - 1].Trim();
        }

        private sealed class ProcessResult
        {
            public bool Success;
            public string Output;
            public string Error;
        }

        private static ProcessResult RunPowerShell(string script)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var output = new StringBuilder();
            var error = new StringBuilder();

            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) output.AppendLine(e.Data);
                    };
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) error.AppendLine(e.Data);
                    };

                    if (!p.Start())
                        return new ProcessResult { Error = "PowerShell başlatılamadı." };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(CommandTimeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return new ProcessResult { Output = output.ToString(), Error = "Ağ ayarı zaman aşımına uğradı." };
                    }
                    p.WaitForExit();
                    return new ProcessResult
                    {
                        Success = p.ExitCode == 0,
                        Output = output.ToString(),
                        Error = error.ToString()
                    };
                }
            }
            catch (Exception ex)
            {
                return new ProcessResult { Output = output.ToString(), Error = ex.Message };
            }
        }
    }
}
