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
    }

    internal static class NetworkRepair
    {
        private const int CommandTimeoutMs = 20000;

        // Sifreli DNS icin kullandigimiz sunucular. Ayni liste hem kurulumda
        // hem de geri almada gecerli olmali, yoksa temizlenmeyen kayit kalir.
        private const string DohTargets =
            "'1.1.1.1','1.0.0.1','2606:4700:4700::1111','2606:4700:4700::1001'";
        private const string DohTemplate = "https://cloudflare-dns.com/dns-query";

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
                "$best=$items|Sort-Object @{Expression={if($_.Hardware){0}else{1}}},Metric|Select-Object -First 1;" +
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
                " foreach($s in @(" + DohTargets + ")){" +
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
                DnsBackup existing = LoadBackup(path);
                if (existing != null) return existing;

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
                SaveBackup(backup);
                return backup;
            }
            catch (Exception ex)
            {
                error = "DNS yedeği oluşturulamadı: " + ex.Message;
                return null;
            }
        }

        public static bool ConfigureCloudflareDoh(DnsBackup backup, out string error)
        {
            error = null;
            if (backup == null)
            {
                error = "DNS yedeği bulunamadı.";
                return false;
            }

            // Yalnizca IPv4'u duzeltmek yetmiyor: cift yiginli bir hatta Windows
            // once IPv6 cozumleyicisini soruyor, yani ISS'nin zehirli sunucusu
            // cevap vermeye devam ediyor. Global IPv6 varsa iki aileyi de yaziyoruz.
            string servers = backup.HasGlobalV6
                ? DohTargets
                : "'1.1.1.1','1.0.0.1'";

            // udpfallback=no makineyi "DoH yoksa hic DNS yok" durumuna sokuyor.
            // Aginda DoH engelli bir kullanicida ad cozumlemesi tamamen durdu ve
            // tani "boyle bir kayit yok" dedi. Duz UDP'ye dusmek, ISS'nin
            // cozumleyicisinde kalmaktan kotu degil - internetin gitmesi kotu.
            string script =
                "$ErrorActionPreference='Stop';" +
                "$n=Join-Path $env:SystemRoot 'System32\\netsh.exe';" +
                "function Set-Doh([string]$s){" +
                " & $n 'dnsclient' 'set' 'encryption' ('server='+$s)" +
                "  ('dohtemplate=" + DohTemplate + "') 'dothost=:'" +
                "  'autoupgrade=yes' 'udpfallback=yes' | Out-Null;" +
                " if($LASTEXITCODE -ne 0){" +
                "  & $n 'dnsclient' 'add' 'encryption' ('server='+$s)" +
                "   ('dohtemplate=" + DohTemplate + "') 'dothost=:'" +
                "   'autoupgrade=yes' 'udpfallback=yes' | Out-Null;" +
                "  if($LASTEXITCODE -ne 0){throw ('Sifreli DNS kaydedilemedi: '+$s)}" +
                " }" +
                "};" +
                "foreach($s in @(" + servers + ")){Set-Doh $s};" +
                "Set-DnsClientServerAddress -InterfaceIndex " + backup.InterfaceIndex +
                " -ServerAddresses @(" + servers + ");" +
                "Clear-DnsClientCache;" +
                // Sifreli baglanti kurulana kadar sorgular basarisiz olabiliyor.
                // Sabit bir bekleme yerine ad cozumlemesinin GERCEKTEN calistigini
                // bekliyoruz; olmazsa burada durup geri aliyoruz - "DNS'te boyle
                // bir kayit yok" diye yaniltici bir tani uretmektense.
                "$ok=$false;" +
                "for($i=0;$i -lt 12;$i++){" +
                " try{if(@(Resolve-DnsName -Name 'www.microsoft.com' -Type A -DnsOnly" +
                "  -ErrorAction Stop).Count -gt 0){$ok=$true;break}}catch{}" +
                " Start-Sleep -Milliseconds 800" +
                "};" +
                "if(-not $ok){throw 'Yeni DNS ayariyla ad cozumlemesi calismadi'};" +
                "'ETODPX_DNS_CHANGED=1'";

            ProcessResult run = RunPowerShell(script);
            if (run.Success && Marker(run.Output, "ETODPX_DNS_CHANGED") == "1") return true;
            error = CleanError(run);
            return false;
        }

        public static bool Restore(DnsBackup backup, out string error)
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
                "$ErrorActionPreference='Stop';" +
                "$n=Join-Path $env:SystemRoot 'System32\\netsh.exe';" + set +
                "Clear-DnsClientCache;'ETODPX_DNS_RESTORED=1'");
            if (run.Success && Marker(run.Output, "ETODPX_DNS_RESTORED") == "1") return true;
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
                    cmds.Add("& $n 'dnsclient' 'delete' 'encryption' ('server=" + f[0] + "') | Out-Null;");
                    continue;
                }
                if (f.Length < 4) continue;

                cmds.Add("& $n 'dnsclient' 'set' 'encryption' ('server=" + f[0] + "')" +
                         " ('dohtemplate=" + f[3] + "') 'dothost=:'" +
                         " 'autoupgrade=" + (f[1] == "1" ? "yes" : "no") + "'" +
                         " 'udpfallback=" + (f[2] == "1" ? "yes" : "no") + "' | Out-Null;");
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

        private static void SaveBackup(DnsBackup backup)
        {
            var lines = new[]
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
            File.WriteAllLines(backup.FilePath, lines, new UTF8Encoding(false));
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
                string[] static4 = null, static6 = null, dohBefore = null;
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
