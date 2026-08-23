// etodpx - ana pencere. Tamami kod ile kurulur (XAML yok), boylece
// tek bir csc.exe cagrisiyla derlenir ve ek arac gerektirmez.
//
// Baslik cubugu dugmeleri hakkinda:
// Baslik cubugu MouseLeftButtonDown olayinda DragMove() cagirir. DragMove
// modal bir surukleme dongusune girip fareyi yakalar, dolayisiyla ustune
// basilan dugmeye MouseLeftButtonUp HIC ULASMAZ - dugmeler calismaz gorunur.
// Cozum: dugmeler kendi MouseLeftButtonDown olayini Handled isaretler,
// boylece olay baslik cubuguna hic ulasmaz.
using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Etodpx.Engine;

namespace Etodpx.Ui
{
    internal sealed class MainWindow : Window
    {
        private static readonly FontFamily UiFont =
            new FontFamily("Segoe UI Variable Display, Segoe UI, Tahoma");
        private static readonly FontFamily IconFont =
            new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");

        private const string GlyphMinimize = "\uE921";
        private const string GlyphClose = "\uE8BB";

        private readonly Config _cfg;
        private readonly DpiEngine _engine;

        private readonly SolidColorBrush _bg = New(Palette.Bg);
        private readonly SolidColorBrush _surface = New(Palette.Surface);
        private readonly SolidColorBrush _borderB = New(Palette.Border);
        private readonly SolidColorBrush _text = New(Palette.Text);
        private readonly SolidColorBrush _sub = New(Palette.SubText);
        private readonly SolidColorBrush _accent = New(Palette.Accent);
        private readonly SolidColorBrush _idle = New(Palette.Idle);
        private readonly SolidColorBrush _danger = New(Palette.Danger);
        private readonly SolidColorBrush _hover = New(Palette.Hover);

        private PowerButton _power;
        private TextBlock _title, _subtitle, _footer, _uptime;
        private DispatcherTimer _timer;
        private bool _busy;
        private string _lastError;
        private readonly string _configWarning;

        private static SolidColorBrush New(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public MainWindow(Config cfg, DpiEngine engine)
        {
            _cfg = cfg;
            _engine = engine;
            if (cfg.Warnings.Count > 0)
                _configWarning = string.Join("\n", cfg.Warnings.ToArray());

            Title = "etodpx";
            Width = 384;
            Height = 470;
            WindowStyle = WindowStyle.None;
            // CanMinimize: boyutlandirmayi kapatir ama WS_MINIMIZEBOX stilini
            // KORUR. NoResize bu stili kaldirir ve kucultme guvenilmez olur.
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            FontFamily = UiFont;
            Background = _bg;

            Content = BuildUi();

            _engine.Fault += OnEngineFault;

            SourceInitialized += delegate { ApplyWindowChrome(); };
            Loaded += OnLoaded;
            Closing += delegate { _engine.Fault -= OnEngineFault; _engine.Stop(); };

            // Pencere gizliyken (tepsiye indirilmisken) ne animasyon ne de sayac
            // tazelemesi calisir; gorunmeyen pencere icin kare uretmek israftir.
            IsVisibleChanged += delegate
            {
                bool vis = IsVisible;
                if (_power != null) _power.SetAnimationsEnabled(vis);
                if (_timer != null) { if (vis) _timer.Start(); else _timer.Stop(); }
            };

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _timer.Tick += delegate { RefreshStats(); };
            _timer.Start();
        }

        // ==================================================================== kurulum

        private UIElement BuildUi()
        {
            var root = new Border
            {
                Background = _bg,
                BorderBrush = _borderB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Child = grid;

            grid.Children.Add(BuildTitleBar());

            var body = BuildBody();
            Grid.SetRow(body, 1);
            grid.Children.Add(body);

            return root;
        }

        private UIElement BuildTitleBar()
        {
            var bar = new Grid { Background = Brushes.Transparent };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                // Ikinci guvenlik: tiklama bir dugmeden geliyorsa surukleme baslatma.
                // (Dugmeler olayi zaten Handled isaretliyor; bu ondan bagimsiz calisir.)
                if (IsTitleBarButton(e.OriginalSource as DependencyObject)) return;
                try { DragMove(); } catch (InvalidOperationException) { }
            };

            var brand = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            brand.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = _accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 9, 0)
            });
            brand.Children.Add(new TextBlock
            {
                Text = "etodpx",
                Foreground = _text,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            bar.Children.Add(brand);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var minBtn = IconButton(GlyphMinimize, delegate { WindowState = WindowState.Minimized; });
            minBtn.ToolTip = "Simge durumuna küçült";
            buttons.Children.Add(minBtn);

            var closeBtn = IconButton(GlyphClose, delegate { Close(); });
            closeBtn.ToolTip = "Kapat";
            buttons.Children.Add(closeBtn);

            Grid.SetColumn(buttons, 1);
            bar.Children.Add(buttons);
            return bar;
        }

        /// <summary>Verilen ogenin gorsel agacta bir baslik cubugu dugmesi icinde olup olmadigi.</summary>
        private static bool IsTitleBarButton(DependencyObject d)
        {
            while (d != null)
            {
                var b = d as Border;
                if (b != null && ReferenceEquals(b.Tag, ButtonTag)) return true;
                d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private static readonly object ButtonTag = new object();

        private Border IconButton(string glyph, Action onClick)
        {
            var tb = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = 12,
                Foreground = _sub,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var b = new Border
            {
                Width = 34,
                Height = 30,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = tb,
                Tag = ButtonTag
            };
            b.MouseEnter += delegate { b.Background = _hover; tb.Foreground = _text; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; tb.Foreground = _sub; };

            // Handled=true sart: yoksa olay baslik cubuguna gidip DragMove()
            // tetikler ve asagidaki MouseLeftButtonUp hic gelmez.
            b.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; };
            b.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                onClick();
            };
            return b;
        }

        private UIElement BuildBody()
        {
            var g = new Grid { Margin = new Thickness(24, 0, 24, 20) };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _power = new PowerButton
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _power.Bind(_surface, _idle);
            _power.SetAccent(_accent, _sub);
            _power.Click += OnPowerClick;
            g.Children.Add(_power);

            _title = new TextBlock
            {
                Text = "KAPALI",
                Foreground = _text,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(_title, 1);
            g.Children.Add(_title);

            _subtitle = new TextBlock
            {
                Text = "Korumayı başlatmak için dokun",
                Foreground = _sub,
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 20)
            };
            Grid.SetRow(_subtitle, 2);
            g.Children.Add(_subtitle);

            var card = BuildUptimeCard();
            Grid.SetRow(card, 3);
            g.Children.Add(card);

            _footer = new TextBlock
            {
                Text = string.Empty,
                Foreground = _sub,
                FontSize = 11,
                Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(_footer, 4);
            g.Children.Add(_footer);

            return g;
        }

        private Border BuildUptimeCard()
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            _uptime = new TextBlock
            {
                Text = "00:00:00",
                Foreground = _text,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            sp.Children.Add(_uptime);
            sp.Children.Add(new TextBlock
            {
                Text = "SÜRE",
                Foreground = _sub,
                FontSize = 9.5,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            return new Border
            {
                Background = _surface,
                BorderBrush = _borderB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(6, 14, 6, 14),
                Child = sp
            };
        }

        // ==================================================================== davranis

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyState();
            if (_configWarning != null)
            {
                MessageBox.Show("Ayar dosyasında düzeltilmesi gereken noktalar var:\n\n" +
                    _configWarning, "etodpx - ayar uyarısı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (_cfg.AutoStart && !_engine.IsRunning) OnPowerClick();
        }

        private void OnPowerClick()
        {
            if (_busy) return;
            _busy = true;
            _lastError = null;
            bool starting = !_engine.IsRunning;

            _power.SetBusy(true);
            _title.Text = starting ? "BAŞLATILIYOR" : "DURDURULUYOR";
            _subtitle.Text = starting ? "Sürücü hazırlanıyor" : "Bağlantılar bırakılıyor";
            _footer.Text = string.Empty;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                try
                {
                    if (starting) _engine.Start();
                    else _engine.Stop();
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    try { _engine.Stop(); } catch { }
                }

                // Kullanici baslatma sirasinda pencereyi kaparsa Dispatcher kapanmis
                // olur ve BeginInvoke InvalidOperationException atar. Bu bir havuz
                // is parcaciginda oldugu icin yakalanmazsa SURECI COKERTIR.
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        _lastError = err;
                        _power.SetBusy(false);
                        ApplyState();

                        // Baslatma hatalari genelde kullanicinin yapabilecegi bir
                        // sey iceriyor (servisi sil, Bellek Butunlugu'nu kapat).
                        // Bunu dar alt satira sigdirmak yerine kopyalanabilir bir
                        // pencerede gosteriyoruz.
                        if (err != null && starting)
                        {
                            MessageBox.Show(this, err, "etodpx - koruma başlatılamadı",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }));
                }
                catch (InvalidOperationException) { }   // pencere zaten kapandi
            });
        }

        private void OnEngineFault(string message)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _lastError = message;
                    ApplyState();
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void ApplyState()
        {
            bool on = _engine.IsRunning && _lastError == null;
            _power.SetActive(on, Palette.Accent);

            if (_lastError != null)
            {
                _title.Text = "HATA";
                _title.Foreground = _danger;
                _subtitle.Text = "Koruma başlatılamadı";
                _footer.Foreground = _danger;
                // Alt satir dar: cok satirli bir cozum metni burada kirpilir.
                // Ilk satiri gosterip gerisini pencereye biraktik.
                _footer.Text = FirstLine(_lastError);
                return;
            }

            _title.Foreground = _text;
            _footer.Foreground = _sub;

            if (on)
            {
                _title.Text = "KORUMA AÇIK";
                // Varsayilan kip sahte paket gondermez: hicbir baglantiyi bozmaz
                // ama bazi hatlarda tek basina yetmez. Kullanici "acik" yazip da
                // site acilmayinca uygulamayi bozuk sanmasin diye bunu soyluyoruz.
                _subtitle.Text = UsesFakePacket()
                    ? "DPI atlatma çalışıyor"
                    : "Temel kip · açılmayan site varsa etodpx-tune.exe çalıştır";
                _footer.Text = FooterInfo();
            }
            else
            {
                _title.Text = "KAPALI";
                _subtitle.Text = "Korumayı başlatmak için dokun";
                _footer.Foreground = _configWarning == null ? _sub : _danger;
                _footer.Text = _configWarning == null ? string.Empty : "Ayar uyarısı · " + _configWarning;
            }
            RefreshStats();
        }

        /// <summary>
        /// Sahte paket yontemlerinden biri acik mi? Hicbiri acik degilse yalnizca
        /// bolme yapiliyor demektir - guvenli ama paketleri yeniden birlestiren
        /// DPI kutularina karsi genelde yetersiz.
        /// </summary>
        private bool UsesFakePacket()
        {
            if (!_cfg.FakeBadSum && !_cfg.FakeTtl && !_cfg.FakeWrongSeq) return false;
            return _cfg.TlsMode.Contains("fake") || _cfg.HttpMode.Contains("fake");
        }

        /// <summary>Cok satirli bir mesajin dar alt satira sigan ilk satiri.</summary>
        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            string head = i < 0 ? s : s.Substring(0, i);
            return i < 0 ? head : head + "  (ayrıntı için pencereye bak)";
        }

        private string FooterInfo()
        {
            string q;
            if (_engine.QuicWarning != null) q = "QUIC: devre dışı";
            else switch (_cfg.QuicMode)
            {
                case "drop": q = "QUIC: TCP'ye düşür"; break;
                case "off":
                case "none": q = "QUIC: dokunma"; break;
                default: q = "QUIC: sahte"; break;
            }
            string info = "TLS: " + _cfg.TlsMode + " · HTTP: " + _cfg.HttpMode + " · " + q;
            if (_engine.QuicWarning != null) info += " · " + _engine.QuicWarning;
            if (_configWarning != null) info += " · Ayar uyarısı: " + _configWarning;
            return info;
        }

        private void RefreshStats()
        {
            if (!_engine.IsRunning) return;
            var s = _engine.Stats;

            var up = s.Uptime;
            _uptime.Text = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                (int)up.TotalHours, up.Minutes, up.Seconds);

            // Sayacı kalabalıklastirmiyoruz; gonderim hatasi varsa alt satirda belirtiyoruz.
            if (_lastError == null)
            {
                long errs = s.SendErrors;
                string info = FooterInfo();
                bool warning = errs > 0 || _engine.QuicWarning != null || _configWarning != null;
                _footer.Foreground = warning ? _danger : _sub;
                _footer.Text = errs > 0 ? info + "  ·  " + errs + " gönderim hatası" : info;
            }
        }

        private void ApplyWindowChrome()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.ApplyChrome(hwnd, true, Native.ToBgr(Palette.Border));
        }
    }
}
