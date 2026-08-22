// dpetoos - giris noktasi
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Dpetoos.Engine;
using Dpetoos.Ui;

namespace Dpetoos
{
    internal static class Program
    {
        private const string MutexName = @"Global\dpetoos-single-instance";

        [STAThread]
        private static int Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("dpetoos zaten çalışıyor.", "dpetoos",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }

                if (!IsAdministrator())
                {
                    MessageBox.Show(
                        "dpetoos yönetici izniyle çalışmalı.\n\n" +
                        "Ağ sürücüsü (WinDivert) yalnızca yönetici olarak yüklenebilir.",
                        "dpetoos", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 1;
                }

                string dir = AppDir();
                var cfg = Config.Load(Path.Combine(dir, "dpetoos.conf"));

                TuneProcess(cfg);

                var engine = new DpiEngine(cfg);
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

                app.DispatcherUnhandledException += delegate (object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    e.Handled = true;
                    MessageBox.Show("Beklenmeyen hata:\n\n" + e.Exception.Message,
                        "dpetoos", MessageBoxButton.OK, MessageBoxImage.Error);
                };

                var win = new MainWindow(cfg, engine);
                TrayIcon tray = null;
                if (cfg.MinimizeToTray)
                {
                    try { tray = new TrayIcon(win, engine); }
                    catch { tray = null; }   // tepsi zorunlu degil
                }

                app.MainWindow = win;
                win.Show();

                int rc = app.Run();

                if (tray != null) tray.Dispose();
                engine.Dispose();
                return rc;
            }
        }

        internal static string AppDir()
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Paket isleme gecikmesinin oynamamasi icin sureci biraz onceliklendirip
        /// cop toplayiciyi dusuk gecikme kipine aliyoruz. Paket yolunda zaten
        /// tahsis yok; bu ayar yalnizca arayuz kaynakli duraklamalari da keser.
        /// </summary>
        private static void TuneProcess(Config cfg)
        {
            try
            {
                if (cfg.HighPriority)
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            catch { }

            try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; }
            catch { }
        }
    }
}
