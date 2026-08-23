// etodpx - aktif ag baglantisi icin geri alinabilir DNS onarimi.
//
// Bu kod yalnizca kullanici "Otomatik duzelt" dedikten ve ayarlayici sistem
// DNS'inin bozuk oldugunu olctukten sonra calisir. Degisiklikten ONCE mevcut
// sunucular diske yazilir. Yeni ayar dogrulanamazsa arayuz bu yedegi geri yukler.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Etodpx
{
    internal sealed class DnsBackup
    {
        public int InterfaceIndex;
        public string InterfaceAlias;
        public string[] Servers;
        public string FilePath;
    }

    internal static class NetworkRepair
    {
        private const int CommandTimeoutMs = 20000;

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
                "$dns=@(Get-DnsClientServerAddress -InterfaceIndex $best.Index|" +
                " ForEach-Object {$_.ServerAddresses}|Where-Object {$_});" +
                "$enc=[Text.Encoding]::UTF8;" +
                "'ETODPX_INDEX='+$best.Index;" +
                "'ETODPX_ALIAS_B64='+[Convert]::ToBase64String($enc.GetBytes($best.Alias));" +
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

                string path = Path.Combine(Program.AppDir(), "etodpx-dns-backup-" + index + ".conf");
                DnsBackup existing = LoadBackup(path);
                if (existing != null) return existing;

                var backup = new DnsBackup
                {
                    InterfaceIndex = index,
                    InterfaceAlias = alias,
                    Servers = servers,
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

            string script =
                "$ErrorActionPreference='Stop';" +
                "$n=Join-Path $env:SystemRoot 'System32\\netsh.exe';" +
                "function Set-Doh([string]$s){" +
                " & $n 'dnsclient' 'set' 'encryption' ('server='+$s)" +
                "  'dohtemplate=https://cloudflare-dns.com/dns-query' 'dothost=:'" +
                "  'autoupgrade=yes' 'udpfallback=no' | Out-Null;" +
                " if($LASTEXITCODE -ne 0){" +
                "  & $n 'dnsclient' 'add' 'encryption' ('server='+$s)" +
                "   'dohtemplate=https://cloudflare-dns.com/dns-query' 'dothost=:'" +
                "   'autoupgrade=yes' 'udpfallback=no' | Out-Null;" +
                "  if($LASTEXITCODE -ne 0){throw ('Sifreli DNS kaydedilemedi: '+$s)}" +
                " }" +
                "};" +
                "Set-Doh '1.1.1.1';Set-Doh '1.0.0.1';" +
                "Set-DnsClientServerAddress -InterfaceIndex " + backup.InterfaceIndex +
                " -ServerAddresses @('1.1.1.1','1.0.0.1');" +
                "Clear-DnsClientCache;" +
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

            string set;
            if (backup.Servers == null || backup.Servers.Length == 0)
            {
                set = "Set-DnsClientServerAddress -InterfaceIndex " + backup.InterfaceIndex +
                      " -ResetServerAddresses;";
            }
            else
            {
                var values = new List<string>();
                foreach (string server in backup.Servers) values.Add(PsQuote(server));
                set = "Set-DnsClientServerAddress -InterfaceIndex " + backup.InterfaceIndex +
                      " -ServerAddresses @(" + string.Join(",", values.ToArray()) + ");";
            }

            ProcessResult run = RunPowerShell(
                "$ErrorActionPreference='Stop';" + set +
                "Clear-DnsClientCache;'ETODPX_DNS_RESTORED=1'");
            if (run.Success && Marker(run.Output, "ETODPX_DNS_RESTORED") == "1") return true;
            error = CleanError(run);
            return false;
        }

        private static void SaveBackup(DnsBackup backup)
        {
            var lines = new[]
            {
                "# etodpx DNS geri alma noktasi",
                "interface_index=" + backup.InterfaceIndex,
                "interface_alias_b64=" + Encode(backup.InterfaceAlias ?? string.Empty),
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
                foreach (string raw in File.ReadAllLines(path))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq).Trim();
                    string value = raw.Substring(eq + 1).Trim();
                    if (key == "interface_index") int.TryParse(value, out index);
                    else if (key == "interface_alias_b64") alias = Decode(value);
                    else if (key == "servers_b64") servers = Decode(value);
                }
                if (index <= 0) return null;
                return new DnsBackup
                {
                    InterfaceIndex = index,
                    InterfaceAlias = alias,
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
