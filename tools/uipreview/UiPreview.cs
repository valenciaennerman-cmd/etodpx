// etodpx - arayuz onizlemesi (gelistirme araci)
// Motoru baslatmadan pencereyi acar; boylece tasarim yonetici izni ve surucu
// olmadan gozden gecirilebilir. Uretim kodunda bu araca ozel hicbir sey yok:
// "acik" gorunumu yansima (reflection) ile kurulur.
//
// Kullanim: etodpx-uipreview.exe [on|off]
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Etodpx;
using Etodpx.Engine;
using Etodpx.Ui;

namespace Etodpx.Preview
{
    internal static class UiPreview
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

        [STAThread]
        private static int Main(string[] args)
        {
            var cfg = new Config();
            cfg.AutoStart = false;
            cfg.MinimizeToTray = false;
            cfg.SourcePath = null;

            bool pretendOn = Array.IndexOf(args, "on") >= 0;

            var engine = new DpiEngine(cfg);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var win = new MainWindow(cfg, engine);

            if (pretendOn)
            {
                win.Loaded += delegate
                {
                    win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                    {
                        FakeRunningState(win, engine);
                    }));
                };
            }

            app.MainWindow = win;
            win.Show();
            return app.Run();
        }

        /// <summary>Motoru calistirmadan "koruma acik" gorunumunu kurar.</summary>
        private static void FakeRunningState(MainWindow win, DpiEngine engine)
        {
            try
            {
                var running = typeof(DpiEngine).GetField("_running", Hidden);
                if (running != null) running.SetValue(engine, true);

                engine.Stats.MarkStart();
                for (int i = 0; i < 1284; i++) engine.Stats.BumpConnection();

                var apply = typeof(MainWindow).GetMethod("ApplyState", Hidden);
                if (apply != null) apply.Invoke(win, null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("onizleme durumu kurulamadi: " + ex.Message);
            }
        }
    }
}
