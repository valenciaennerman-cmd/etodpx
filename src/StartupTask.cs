// Windows acilisinda otomatik baslatma (Gorev Zamanlayici / schtasks).
//
// Kayit defterindeki HKCU\...\Run anahtari UAC manifesti tasiyan (requireAdministrator)
// uygulamalari Windows acilisinda sessizce engeller. Gorev Zamanlayici'da
// /RL HIGHEST ve /SC ONLOGON ile kayit acildiginda ise kullanici oturum actiginda
// UAC onay kutusu cikmadan yonetici yetkisiyle baslar.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace Etodpx
{
    internal static class StartupTask
    {
        public const string TaskName = "etodpx";

        public static string BuildCreateArguments(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentNullException("exePath");
            string quoted = "\\\"" + exePath.Trim().Trim('"') + "\\\"";
            return "/Create /TN \"" + TaskName + "\" /TR \"" + quoted +
                   "\" /SC ONLOGON /RL HIGHEST /F";
        }

        public static string BuildDeleteArguments()
        {
            return "/Delete /TN \"" + TaskName + "\" /F";
        }

        public static string BuildQueryArguments()
        {
            return "/Query /TN \"" + TaskName + "\"";
        }

        public static bool IsEnabled()
        {
            string output;
            return RunSchtasks(BuildQueryArguments(), out output) == 0;
        }

        public static bool SetEnabled(bool enable, out string error)
        {
            error = null;
            string exePath = DefaultExePath();
            if (enable && (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)))
            {
                error = "Uygulama dosyası bulunamadı.";
                return false;
            }

            string args = enable ? BuildCreateArguments(exePath) : BuildDeleteArguments();
            string output;
            int code = RunSchtasks(args, out output);
            if (code == 0) return true;

            // Zaten silinmisse basarili say.
            if (!enable && !IsEnabled()) return true;

            error = string.IsNullOrEmpty(output)
                ? "Görev Zamanlayıcı işlemi başarısız oldu (kod " + code + ")."
                : output.Trim();
            return false;
        }

        private static string DefaultExePath()
        {
            string loc = null;
            try
            {
                loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc)) return Path.GetFullPath(loc);
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "etodpx.exe");
        }

        private static int RunSchtasks(string arguments, out string output)
        {
            output = string.Empty;
            try
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = arguments,
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
                    if (!p.Start()) return -1;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(8000))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                    p.WaitForExit();
                    output = (stdout.ToString() + "\n" + stderr.ToString()).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }
    }
}
