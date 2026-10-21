// Otomatik baglanti onarimi, surucu kurtarma ve ag degisimi DNS denetimi.
//
// MainWindow yalnizca gorsel agaci ve durumu cizer; arka plan tani surecleri,
// WinDivert servisi kurtarma, etodpx-tune calistirma ve captive-portal sonrasi
// DNS geri yukleme akislari burada yonetilir.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Etodpx.Engine;

namespace Etodpx.Ui
{
    internal enum RepairFollowUp
    {
        None,
        Retry,
        NetworkSettings,
        Report,
        AppFolder
    }

    internal sealed class TunerRun
    {
        public int ExitCode;
        public string Code;
        public string Output;
        public readonly List<string> OtherCopies = new List<string>();
    }

    internal sealed class RepairOutcome
    {
        public TunerRun Run;
        public TunerRun FirstRun;
        public string RepairError;
        public string EngineError;
        public bool DnsChanged;
        public string DnsProvider;
        public string LogPath;
        public string ConfigWarning;
    }

    internal sealed class RepairController
    {
        private readonly Config _cfg;
        private readonly DpiEngine _engine;
        private Process _tuneProcess;
        private int _networkCheckRunning;

        public RepairController(Config cfg, DpiEngine engine)
        {
            _cfg = cfg;
            _engine = engine;
        }

        public void StopTuner()
        {
            var p = _tuneProcess;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        // Eski GoodbyeDPI/zapret kurulumlarindan kalan devre disi WinDivert servisi
        // acilisi engelliyor. Yonetici oldugumuz icin temizleyip tekrar deniyoruz.
        public void StartEngineWithRecovery()
        {
            try
            {
                _engine.Start();
            }
            catch (Exception ex)
            {
                if (!Contains(ex.Message, "devre dışı bırakılmış eski bir WinDivert")) throw;
                try { _engine.Stop(); } catch { }
                if (!RepairDisabledWinDivert()) throw;
                Thread.Sleep(700);
                _engine.Start();
            }
        }

        public RepairOutcome RunAutomaticRepair(string tuner, string[] customTargets,
                                                Func<bool> isClosing,
                                                Action<string, string> onProgress)
        {
            TunerRun run = null;
            TunerRun firstRun = null;
            DnsBackup dnsBackup = null;
            bool dnsMayHaveChanged = false;
            bool keepDns = false;
            string repairError = null;
            string engineError = null;
            string dnsRepairError = null;
            string dnsProvider = null;
            string configWarning = null;
            var dnsTrace = new StringBuilder();
            List<string> closedCopies = null;
            string[] targets = (customTargets != null && customTargets.Length > 0)
                ? customTargets
                : NetworkRepair.RepairTargets;

            try
            {
                closedCopies = CloseOtherEtodpxCopies();
                if (onProgress != null)
                    onProgress("1/4 · Ağ sürücüsü kontrol ediliyor", "Koruma test için kısa süre durduruldu");
                _engine.Stop();

                if (onProgress != null)
                    onProgress("2/4 · DNS ve bağlantı engeli ölçülüyor", "İlk tanı birkaç saniye sürebilir");
                run = ExecuteTuner(tuner, targets, customTargets == null || customTargets.Length == 0);
                MergeCopies(run, closedCopies);
                firstRun = run;

                if (IsDnsProblem(run.Code))
                {
                    string dnsError;
                    dnsBackup = NetworkRepair.CaptureActiveDns(out dnsError);
                    if (dnsBackup == null)
                    {
                        dnsRepairError = "Aktif bağlantının DNS ayarı güvenle yedeklenemedi: " + dnsError;
                    }
                    else
                    {
                        // Komut son adimda patlasa bile DNS'e dokunmus olabilir;
                        // finally her durumda ilk ayara donebilsin.
                        dnsMayHaveChanged = true;
                        var failures = new List<string>();

                        foreach (DnsProvider provider in NetworkRepair.DohProviders)
                        {
                            if (onProgress != null)
                                onProgress("3/4 · " + provider.Name + " şifreli DNS deneniyor",
                                           "Başarısız olursa sıradaki güvenli sağlayıcı denenecek");

                            if (!NetworkRepair.ConfigureDoh(dnsBackup, provider, targets, out dnsError))
                            {
                                failures.Add(provider.Name + ": " + dnsError);
                                dnsTrace.AppendLine(provider.Name + " kurulum hatası: " + dnsError);
                            }
                            else
                            {
                                Thread.Sleep(500);
                                if (onProgress != null)
                                    onProgress("4/4 · Bağlantı doğrulanıyor",
                                               provider.Name + " üzerinden gerçek hedefler sınanıyor");
                                run = ExecuteTuner(tuner, targets, customTargets == null || customTargets.Length == 0);
                                MergeCopies(run, closedCopies);
                                dnsTrace.AppendLine("=== " + provider.Name + " sonrası tanı ===");
                                dnsTrace.AppendLine(run.Output);

                                if (!IsDnsProblem(run.Code))
                                {
                                    keepDns = true;
                                    dnsProvider = provider.Name;
                                    NetworkRepair.MarkManagedDns(dnsBackup, provider);
                                    break;
                                }

                                failures.Add(provider.Name + ": hedefler hâlâ çözümlenemedi (" + run.Code + ")");
                            }

                            // Sonraki saglayiciyi tamamen temiz bir baslangictan dene.
                            string restoreError;
                            if (!NetworkRepair.RestoreForRetry(dnsBackup, out restoreError))
                            {
                                repairError = "DNS denemesi geri alınamadı: " + restoreError;
                                break;
                            }
                        }

                        if (!keepDns && repairError == null)
                            dnsRepairError = failures.Count == 0
                                ? "Bu Windows sürümü güvenli DNS onarımını desteklemiyor."
                                : string.Join(" | ", failures.ToArray());
                    }
                }

                if (run != null && run.Code == "CONFIG_WRITTEN")
                {
                    configWarning = ReloadConfig();
                    _engine.ReloadHostlist();
                }
            }
            catch (Exception ex)
            {
                repairError = ex.Message;
            }
            finally
            {
                if (dnsMayHaveChanged && !keepDns && dnsBackup != null)
                {
                    string restoreError;
                    if (!NetworkRepair.Restore(dnsBackup, out restoreError))
                    {
                        string prefix = repairError == null ? string.Empty : repairError + "\n\n";
                        repairError = prefix + "Eski DNS ayarı da otomatik geri yüklenemedi: " + restoreError;
                    }
                }

                // Tani nasil biterse bitsin kullaniciyi korumasiz birakma.
                try
                {
                    if ((isClosing == null || !isClosing()) && !_engine.IsRunning)
                        StartEngineWithRecovery();
                }
                catch (Exception ex) { engineError = ex.Message; }
            }

            string logPath = SaveRepairLog(run, firstRun, repairError, engineError, keepDns,
                                           dnsProvider, dnsRepairError, dnsTrace.ToString());

            return new RepairOutcome
            {
                Run = run,
                FirstRun = firstRun,
                RepairError = repairError,
                EngineError = engineError,
                DnsChanged = keepDns,
                DnsProvider = dnsProvider,
                LogPath = logPath,
                ConfigWarning = configWarning
            };
        }

        public string ReloadConfig()
        {
            var fresh = Config.Load(Path.Combine(Program.AppDir(), "etodpx.conf"));
            _cfg.CopyFrom(fresh);
            return fresh.Warnings.Count == 0
                ? null
                : string.Join("\n", fresh.Warnings.ToArray());
        }

        // Otel / okul / havalimani Wi-Fi'ina gecildiginde dis DoH sunuculari
        // engellenip giris sayfasi acilmayabiliyor. Yalnizca etodpx'in yonettigi
        // DNS kilitlendiyse eski ayara sessizce doner.
        public void ScheduleManagedDnsCheck(Func<bool> isBusyOrClosing, Action<string> onRestored)
        {
            if (Interlocked.Exchange(ref _networkCheckRunning, 1) != 0) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(2500);
                    if (isBusyOrClosing != null && isBusyOrClosing()) return;

                    foreach (DnsBackup backup in NetworkRepair.ManagedBackups())
                    {
                        if (isBusyOrClosing != null && isBusyOrClosing()) return;
                        NetworkRepair.ManagedDnsState state = NetworkRepair.ProbeManagedDns(backup);
                        if (state != NetworkRepair.ManagedDnsState.BlockedHere) continue;

                        string restoreError;
                        if (NetworkRepair.Restore(backup, out restoreError) && onRestored != null)
                        {
                            onRestored("Bu ağda şifreli DNS engellendiği için eski DNS ayarı geri yüklendi");
                        }
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _networkCheckRunning, 0);
                }
            });
        }

        private TunerRun ExecuteTuner(string tuner, string[] targets, bool requireAllTargets)
        {
            var run = new TunerRun { ExitCode = -1, Output = string.Empty };
            Process p = null;
            try
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                string reqFlag = requireAllTargets ? "--require-all-targets " : string.Empty;
                p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = tuner,
                    // Hedefler on teshiste eleniyor: yalnizca SNI engelli olanlar
                    // taramaya giriyor, biri aciksa tarama suresi uzamiyor.
                    Arguments = "--auto --no-manage-app " + reqFlag + string.Join(" ", targets),
                    WorkingDirectory = Program.AppDir(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stdout.AppendLine(e.Data);
                };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stderr.AppendLine(e.Data);
                };
                _tuneProcess = p;
                if (!p.Start()) throw new InvalidOperationException("Otomatik test başlatılamadı.");
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(12 * 60 * 1000))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("Otomatik bağlantı testi zaman aşımına uğradı.");
                }
                // Asenkron cikti olaylarinin kalanini bosalt.
                p.WaitForExit();
                run.ExitCode = p.ExitCode;
                run.Output = stdout.ToString();
                if (stderr.Length > 0) run.Output += "\n" + stderr;
                run.Code = OutputMarker(run.Output, "ETODPX_RESULT");
                if (run.Code == null) run.Code = LegacyTunerCode(run.ExitCode, run.Output);
                ReadOtherCopies(run);
                return run;
            }
            finally
            {
                _tuneProcess = null;
                if (p != null) p.Dispose();
            }
        }

        public static List<string> CloseOtherEtodpxCopies()
        {
            var found = new List<string>();
            string expected = Path.GetFullPath(Path.Combine(Program.AppDir(), "etodpx.exe"));
            int ownId = Process.GetCurrentProcess().Id;

            foreach (var p in Process.GetProcessesByName("etodpx"))
            {
                try
                {
                    if (p.Id == ownId) continue;
                    string actual = Path.GetFullPath(p.MainModule.FileName);
                    if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!found.Contains(actual)) found.Add(actual);

                    bool exited = p.CloseMainWindow() && p.WaitForExit(2500);
                    if (!exited)
                    {
                        p.Kill();
                        p.WaitForExit(2500);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return found;
        }

        private static void MergeCopies(TunerRun run, List<string> copies)
        {
            if (run == null || copies == null) return;
            foreach (string path in copies)
                if (!run.OtherCopies.Contains(path)) run.OtherCopies.Add(path);
        }

        public static bool IsDnsProblem(string code)
        {
            return code == "DNS_POISONED" || code == "DNS_BLOCKED" ||
                   code == "DNS_UNAVAILABLE" || code == "DNS_UNVERIFIED";
        }

        private static string OutputMarker(string output, string key)
        {
            if (output == null) return null;
            string prefix = key + "=";
            foreach (string line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line.Substring(prefix.Length).Trim();
            return null;
        }

        private static string LegacyTunerCode(int exitCode, string output)
        {
            if (Contains(output, "Yazildi.")) return "CONFIG_WRITTEN";
            if (Contains(output, "zaten aciliyor")) return "ALREADY_OPEN";
            if (Contains(output, "DNS ZEHIRLI")) return "DNS_POISONED";
            if (Contains(output, "DNS dogrulanamadi")) return "DNS_UNVERIFIED";
            if (Contains(output, "alan adi cozulemiyor")) return "DNS_UNAVAILABLE";
            if (Contains(output, "IP seviyesinde")) return "IP_BLOCKED";
            if (Contains(output, "Hicbir strateji")) return "NO_STRATEGY";
            return exitCode == 0 ? "OK" : "FAILED";
        }

        private static void ReadOtherCopies(TunerRun run)
        {
            const string prefix = "ETODPX_OTHER_COPY_B64=";
            foreach (string line in run.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                try
                {
                    string path = Encoding.UTF8.GetString(
                        Convert.FromBase64String(line.Substring(prefix.Length).Trim()));
                    if (path.Length > 0 && !run.OtherCopies.Contains(path)) run.OtherCopies.Add(path);
                }
                catch { }
            }
        }

        private static string SaveRepairLog(TunerRun run, TunerRun firstRun, string repairError,
                                            string engineError, bool dnsChanged, string dnsProvider,
                                            string dnsRepairError, string dnsTrace)
        {
            try
            {
                string path = Path.Combine(Program.AppDir(), "etodpx-repair.log");
                var sb = new StringBuilder();
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("result=" + (run == null ? "NO_RESULT" : run.Code));
                if (firstRun != null && !ReferenceEquals(firstRun, run))
                    sb.AppendLine("first_result=" + firstRun.Code);
                sb.AppendLine("exit_code=" + (run == null ? -1 : run.ExitCode));
                sb.AppendLine("encrypted_dns_changed=" + dnsChanged);
                if (dnsProvider != null) sb.AppendLine("encrypted_dns_provider=" + dnsProvider);
                if (dnsRepairError != null)
                    sb.AppendLine("dns_repair_error=" + OneLine(dnsRepairError));
                if (repairError != null) sb.AppendLine("repair_error=" + repairError);
                if (engineError != null) sb.AppendLine("engine_error=" + engineError);
                sb.AppendLine();
                if (firstRun != null && !ReferenceEquals(firstRun, run))
                {
                    sb.AppendLine("=== 1. tani (degisiklikten once) ===");
                    sb.AppendLine(firstRun.Output);
                    sb.AppendLine("=== 2. tani (sifreli DNS kurulduktan sonra) ===");
                }
                if (run != null) sb.Append(run.Output);
                if (!string.IsNullOrEmpty(dnsTrace))
                {
                    sb.AppendLine();
                    sb.AppendLine("=== şifreli DNS sağlayıcı denemeleri ===");
                    sb.Append(dnsTrace);
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch { return null; }
        }

        private static bool RepairDisabledWinDivert()
        {
            RunSc("stop WinDivert");
            Thread.Sleep(250);
            if (!ServiceExists("WinDivert")) return true;

            RunSc("delete WinDivert");
            for (int i = 0; i < 8; i++)
            {
                if (!ServiceExists("WinDivert")) return true;
                Thread.Sleep(250);
            }
            return !ServiceExists("WinDivert");
        }

        private static bool ServiceExists(string name)
        {
            return RunSc("query " + name);
        }

        private static bool RunSc(string arguments)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    if (p == null) return false;
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        public static string FriendlyError(string message)
        {
            if (Contains(message, "WinDivert.dll bulunamadı") || Contains(message, "sürücü dosyası"))
                return "Paket eksik görünüyor. ZIP'i yeniden indir, sağ tıklayıp “Tümünü ayıkla” de ve çıkan klasördeki etodpx.exe'yi aç.";
            if (Contains(message, "imzası Windows tarafından kabul edilmedi"))
                return "Windows ağ sürücüsünü engelledi. Windows Güvenliği > Cihaz güvenliği > Çekirdek yalıtımı bölümünü kontrol edip bilgisayarı yeniden başlat.";
            if (Contains(message, "devre dışı bırakılmış eski bir WinDivert"))
                return "Eski ağ sürücüsü otomatik temizlenemedi. Bilgisayarı yeniden başlatıp etodpx'i tekrar aç.";
            return message;
        }

        public static bool IsPackageError(string message)
        {
            return Contains(message, "WinDivert.dll bulunamadı") ||
                   Contains(message, "sürücü dosyası") ||
                   Contains(message, "Paket eksik");
        }

        public static bool IsDriverSecurityError(string message)
        {
            return Contains(message, "imzası Windows tarafından kabul edilmedi") ||
                   Contains(message, "Sürücü Windows tarafından engellendi") ||
                   Contains(message, "Çekirdek yalıtımı") ||
                   Contains(message, "Bellek bütünlüğü");
        }

        public static void OpenNetworkSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:network",
                    UseShellExecute = true
                });
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "control.exe",
                        Arguments = "/name Microsoft.NetworkAndSharingCenter",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        public static void OpenDriverSecuritySettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "windowsdefender://coreisolation",
                    UseShellExecute = true
                });
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "ms-settings:windowsdefender",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        public static void OpenAppFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Program.AppDir(),
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static void OpenRepairLog(string logPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = logPath,
                        UseShellExecute = true
                    });
                    return;
                }
            }
            catch { }
            OpenAppFolder();
        }

        private static bool Contains(string text, string value)
        {
            return text != null && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string OneLine(string value)
        {
            return (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
