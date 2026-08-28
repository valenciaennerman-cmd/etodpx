// Giris noktasi: tek ornek kilidi, yonetici kontrolu, motor + pencere.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Etodpx.Engine;
using Etodpx.Ui;

namespace Etodpx
{
    internal static class Program
    {
        private const string MutexName = @"Global\etodpx-single-instance";

        [STAThread]
        private static int Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("etodpx zaten çalışıyor.", "etodpx",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }

                if (!IsAdministrator())
                {
                    MessageBox.Show(
                        "etodpx yönetici izniyle çalışmalı.\n\n" +
                        "Ağ sürücüsü (WinDivert) yalnızca yönetici olarak yüklenebilir.",
                        "etodpx", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 1;
                }

                string dir = AppDir();
                if (!ValidatePackage(dir)) return 2;
                var cfg = Config.Load(Path.Combine(dir, "etodpx.conf"));

                TuneProcess(cfg);

                var engine = new DpiEngine(cfg);
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

                app.DispatcherUnhandledException += delegate (object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    e.Handled = true;
                    MessageBox.Show("Beklenmeyen hata:\n\n" + e.Exception.Message,
                        "etodpx", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // ZIP'in icinden cift tiklanan exe'yi Windows gecici bir klasore tek basina
        // kopyalayabiliyor. "DLL bulunamadi" demek yerine ne yapmasi gerektigini
        // daha motor baslamadan soyluyoruz.
        private static bool ValidatePackage(string dir)
        {
            string[] required = { "WinDivert.dll", "WinDivert64.sys" };
            foreach (string file in required)
            {
                if (File.Exists(Path.Combine(dir, file))) continue;
                MessageBox.Show(
                    "Uygulamanın bazı dosyaları eksik görünüyor.\n\n" +
                    "ZIP dosyasına sağ tıkla, “Tümünü ayıkla” seçeneğine bas ve " +
                    "çıkan klasördeki etodpx.exe'yi aç. ZIP'in içinden doğrudan çalıştırma.",
                    "etodpx - önce ZIP'i çıkar",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        // Paket yolunda tahsis zaten yok; bu ayar arayuz kaynakli duraklamalari keser.
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
