// dpetoos - bildirim alani simgesi. Pencere kapatilmadan arka planda calisabilsin diye.
using System;
using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using Dpetoos.Engine;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Dpetoos.Ui
{
    internal sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Window _window;
        private readonly DpiEngine _engine;
        private readonly System.Windows.Forms.Timer _poll;
        private bool _lastRunning;
        private bool _synced;   // ilk ipucu yazimi yapildi mi

        public TrayIcon(Window window, DpiEngine engine)
        {
            _window = window;
            _engine = engine;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Göster", null, delegate { Restore(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Çıkış", null, delegate
            {
                _window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    _window.Show();
                    _window.Close();
                }));
            });

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Visible = true,
                Text = "dpetoos",
                ContextMenuStrip = menu
            };
            // Tek tik da acsin - cogu kisi tepsi simgesine cift tiklamiyor.
            _icon.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Restore();
            };

            _window.StateChanged += OnStateChanged;

            // Motor kendi kendine durursa (surucu hatasi vb.) surec ayakta kalir.
            // Pencere tepsiye indirilmisse kullanicinin bunu fark etmesinin hicbir
            // yolu yok: korumasiz gezmeye devam eder. Bu yuzden baloncukla soyluyoruz.
            _engine.Fault += OnEngineFault;

            _poll = new System.Windows.Forms.Timer { Interval = 1000 };
            _poll.Tick += delegate { UpdateTooltip(); };
            _poll.Start();
            UpdateTooltip();
        }

        private static Icon LoadIcon()
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
            catch { }
            return SystemIcons.Shield;
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.Hide();
                _window.ShowInTaskbar = false;
                Native.TrimWorkingSet();
            }
        }

        private void Restore()
        {
            _window.Dispatcher.BeginInvoke(new Action(delegate
            {
                _window.ShowInTaskbar = true;
                _window.Show();
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            }));
        }

        private void OnEngineFault(string message)
        {
            try
            {
                _icon.BalloonTipIcon = ToolTipIcon.Warning;
                _icon.BalloonTipTitle = "dpetoos - koruma durdu";
                _icon.BalloonTipText = message.Length > 200 ? message.Substring(0, 200) : message;
                _icon.ShowBalloonTip(10000);
                _lastRunning = false;
                _synced = false;          // ipucu bir sonraki turda tazelensin
            }
            catch { }
        }

        private void UpdateTooltip()
        {
            bool run = _engine.IsRunning;
            if (run == _lastRunning && _synced) return;
            _lastRunning = run;
            _synced = true;
            _icon.Text = run ? "dpetoos - koruma açık" : "dpetoos - kapalı";
        }

        public void Dispose()
        {
            try
            {
                _window.StateChanged -= OnStateChanged;
                _engine.Fault -= OnEngineFault;
                _poll.Stop();
                _poll.Dispose();
                _icon.Visible = false;
                _icon.Dispose();
            }
            catch { }
        }
    }
}
