// Ana pencere. XAML yok, hepsi kodla kuruluyor - tek csc.exe cagrisi yetsin diye.
//
// Baslik cubugu MouseLeftButtonDown'da DragMove() cagiriyor. DragMove modal bir
// surukleme dongusune girip fareyi yakaladigi icin ustune basilan dugmeye
// MouseLeftButtonUp hic ulasmiyor ve dugmeler olu gorunuyor. Dugmeler bu yuzden
// kendi MouseLeftButtonDown'larini Handled isaretliyor.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
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
        private const string GlyphAdd = "\uE710";

        // Dugme metni iki yerde kuruluyor (ilk cizim ve durum tazeleme); ayni
        // sabitten okunmazsa biri guncellenip digeri geride kaliyor.
        // 12 px SemiBold'da 258 px cizer, dugmeye kalan yer 306 px - tek satir.
        private const string RepairPrompt = "Discord / Roblox açılmıyor mu?  Tek tıkla düzelt";

        private readonly Config _cfg;
        private readonly DpiEngine _engine;
        private readonly RepairController _repair;

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
        private TextBlock _title, _subtitle, _footer;
        private Border _repairButton;
        private TextBlock _repairText;
        private DispatcherTimer _timer;
        private bool _busy;
        private bool _closing;
        private string _lastError;
        private string _lastErrorRaw;
        private bool _repairFailed;
        private string _lastRepairMessage;
        private string _dnsNotice;
        private RepairFollowUp _repairFollowUp;
        private string _lastRepairLogPath;
        private string _configWarning;

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
            _repair = new RepairController(cfg, engine);
            if (cfg.Warnings.Count > 0)
                _configWarning = string.Join("\n", cfg.Warnings.ToArray());

            Title = "etodpx";
            Width = 384;
            Height = 438;
            WindowStyle = WindowStyle.None;
            // CanMinimize boyutlandirmayi kapatir ama WS_MINIMIZEBOX'i korur;
            // NoResize onu kaldirir ve kucultme guvenilmez olur.
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            FontFamily = UiFont;
            Background = _bg;

            Content = BuildUi();

            _engine.Fault += OnEngineFault;
            _engine.HostlistReloaded += OnHostlistReloaded;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

            SourceInitialized += delegate { ApplyWindowChrome(); };
            Loaded += OnLoaded;
            Closing += delegate
            {
                _closing = true;
                _repair.StopTuner();
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                _engine.HostlistReloaded -= OnHostlistReloaded;
                _engine.Fault -= OnEngineFault;
                _engine.Stop();
            };

            // Tepsideyken ne animasyon ne sayac tazelemesi calisir.
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

        // --- kurulum

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
                // Dugmeler olayi zaten Handled isaretliyor; bu ikinci emniyet.
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

            var addBtn = IconButton(GlyphAdd, delegate { PromptAddDomain(); });
            addBtn.ToolTip = "Engelli site ekle (hosts.txt)";
            buttons.Children.Add(addBtn);

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

        // Oge, gorsel agacta bir baslik cubugu dugmesinin icinde mi.
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

            // Handled olmazsa olay baslik cubuguna gidip DragMove() tetikler.
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
                Margin = new Thickness(0, 6, 0, 14)
            };
            Grid.SetRow(_subtitle, 2);
            g.Children.Add(_subtitle);

            _repairButton = BuildRepairButton();
            Grid.SetRow(_repairButton, 3);
            g.Children.Add(_repairButton);

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

        private Border BuildRepairButton()
        {
            _repairText = new TextBlock
            {
                Text = RepairPrompt,
                Foreground = _text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var button = new Border
            {
                MinWidth = 252,
                Background = _surface,
                BorderBrush = _borderB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = _repairText
            };
            button.MouseEnter += delegate { if (!_busy) button.Background = _hover; };
            button.MouseLeave += delegate { button.Background = _surface; button.Opacity = 1; };
            button.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (_busy) return;
                e.Handled = true;
                button.CaptureMouse();
                button.Opacity = 0.72;
            };
            button.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                bool inside = button.IsMouseCaptured && button.IsMouseOver;
                if (button.IsMouseCaptured) button.ReleaseMouseCapture();
                button.Opacity = 1;
                if (inside) OnRepairClick();
            };
            return button;
        }

        // --- davranis

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
            ScheduleManagedDnsCheck();
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            ScheduleManagedDnsCheck();
        }

        private void ScheduleManagedDnsCheck()
        {
            _repair.ScheduleManagedDnsCheck(
                delegate { return _closing || _busy; },
                delegate (string notice)
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (_closing) return;
                            _dnsNotice = notice;
                            ApplyState();
                        }));
                    }
                    catch (InvalidOperationException) { }
                });
        }

        private void OnHostlistReloaded(int count)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_closing || _busy) return;
                    _dnsNotice = "Alan adı listesi güncellendi (" + count + " kayıt)";
                    ApplyState();
                }));
            }
            catch (InvalidOperationException) { }
        }

        public void PromptAddDomain()
        {
            if (_busy) return;

            var dlg = new Window
            {
                Title = "etodpx - engelli site ekle",
                Width = 340,
                Height = 188,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Background = _bg,
                FontFamily = UiFont
            };
            if (IsVisible) dlg.Owner = this;

            var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            panel.Children.Add(new TextBlock
            {
                Text = "Kapsama alınacak alan adını gir (ör. x.com):",
                Foreground = _text,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var box = new TextBox
            {
                Background = _surface,
                Foreground = _text,
                BorderBrush = _borderB,
                CaretBrush = _text,
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12.5
            };
            panel.Children.Add(box);

            var tuneCheck = new CheckBox
            {
                Content = "Ekledikten sonra bu site için otomatik ayar ara",
                Foreground = _sub,
                FontSize = 11.5,
                Margin = new Thickness(0, 10, 0, 12)
            };
            panel.Children.Add(tuneCheck);

            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancelBtn = new Button
            {
                Content = "İptal",
                Width = 76,
                Height = 26,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            var okBtn = new Button
            {
                Content = "Ekle",
                Width = 76,
                Height = 26,
                IsDefault = true
            };
            bool accepted = false;
            okBtn.Click += delegate { accepted = true; dlg.Close(); };
            btns.Children.Add(cancelBtn);
            btns.Children.Add(okBtn);
            panel.Children.Add(btns);

            dlg.Content = panel;
            dlg.Loaded += delegate { box.Focus(); };
            dlg.ShowDialog();
            if (!accepted) return;

            string raw = box.Text;
            string hostsPath = System.IO.Path.Combine(Program.AppDir(),
                string.IsNullOrEmpty(_cfg.HostlistPath) ? "hosts.txt" : _cfg.HostlistPath);
            string normalized;
            string error;
            bool added = Hostlist.AddHostToFile(hostsPath, raw, out normalized, out error);
            if (error != null)
            {
                MessageBox.Show(this, error, "etodpx - geçersiz alan adı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _engine.ReloadHostlist();
            if (tuneCheck.IsChecked == true && !string.IsNullOrEmpty(normalized))
            {
                StartRepairForTargets(new[] { normalized }, normalized);
                return;
            }

            _dnsNotice = added
                ? (normalized + " listeye eklendi ve anında etkinleştirildi")
                : (normalized + " zaten listede yer alıyor");
            ApplyState();
        }

        private void OnPowerClick()
        {
            if (_busy) return;
            _busy = true;
            SetLastError(null);
            bool starting = !_engine.IsRunning;

            _power.SetBusy(true);
            SetRepairBusy(true, starting ? "Koruma hazırlanıyor…" : "Koruma durduruluyor…");
            _title.Text = starting ? "BAŞLATILIYOR" : "DURDURULUYOR";
            _subtitle.Text = starting ? "Sürücü hazırlanıyor" : "Bağlantılar bırakılıyor";
            _footer.Text = string.Empty;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                try
                {
                    if (starting) _repair.StartEngineWithRecovery();
                    else _engine.Stop();
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    try { _engine.Stop(); } catch { }
                }

                // Pencere bu sirada kapanirsa Dispatcher da kapanir ve BeginInvoke
                // atar. Havuz is parcaciginda oldugumuz icin yakalamak sart.
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        SetLastError(err);
                        _power.SetBusy(false);
                        SetRepairBusy(false);
                        ApplyState();

                        // Baslatma hatalari genelde yapilabilecek bir sey iceriyor
                        // (servisi sil, Bellek Butunlugu'nu kapat); dar alt satir
                        // yerine kopyalanabilir bir pencerede gosteriyoruz.
                        if (err != null && starting)
                        {
                            MessageBox.Show(this, _lastError, "etodpx - koruma başlatılamadı",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }));
                }
                catch (InvalidOperationException) { }   // pencere zaten kapandi
            });
        }

        private void OnRepairClick()
        {
            if (_busy) return;

            if (_lastError != null)
            {
                OnErrorRepairClick();
                return;
            }

            if (_repairFollowUp == RepairFollowUp.NetworkSettings)
            {
                RepairController.OpenNetworkSettings();
                return;
            }
            if (_repairFollowUp == RepairFollowUp.Report)
            {
                RepairController.OpenRepairLog(_lastRepairLogPath);
                return;
            }
            if (_repairFollowUp == RepairFollowUp.AppFolder)
            {
                RepairController.OpenAppFolder();
                return;
            }

            string tuner = System.IO.Path.Combine(Program.AppDir(), "etodpx-tune.exe");
            if (!File.Exists(tuner))
            {
                _repairFailed = true;
                _lastRepairMessage = "Otomatik düzeltme dosyası eksik · ZIP'in tamamını yeniden çıkar";
                _repairFollowUp = RepairFollowUp.AppFolder;
                ApplyState();
                MessageBox.Show(this,
                    "Otomatik ayar dosyası eksik. ZIP'i yeniden indirip klasörün " +
                    "tamamını çıkardıktan sonra tekrar aç.",
                    "etodpx - paket eksik", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                "Discord ve Roblox bağlantısını adım adım kontrol edip güvenle " +
                "düzeltebildiğim " +
                "sorunları uygulayacağım.\n\n" +
                "Gerekirse aktif internet bağlantısına şifreli DNS kurulur; eski DNS " +
                "ayarı önce yedeklenir ve doğrulama başarısız olursa geri alınır.\n\n" +
                "Test sırasında koruma kısa süre kapanır. İşlem birkaç dakika sürebilir.\n\n" +
                "Başlayalım mı?",
                "etodpx - otomatik düzelt", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            StartRepairForTargets(null, "Discord ve Roblox");
        }

        private void StartRepairForTargets(string[] customTargets, string label)
        {
            string tuner = System.IO.Path.Combine(Program.AppDir(), "etodpx-tune.exe");
            if (!File.Exists(tuner)) return;

            _busy = true;
            SetLastError(null);
            _repairFailed = false;
            _lastRepairMessage = null;
            _dnsNotice = null;
            _repairFollowUp = RepairFollowUp.None;
            _lastRepairLogPath = null;
            _power.SetBusy(true);
            SetRepairBusy(true, "Bağlantı ayarlanıyor…");
            _title.Foreground = _text;
            _title.Text = "AYARLANIYOR";
            _subtitle.Text = label + " için uygun ayar aranıyor";
            _footer.Foreground = _sub;
            _footer.Text = "Bu işlem birkaç dakika sürebilir · pencereyi kapatma";

            ThreadPool.QueueUserWorkItem(delegate
            {
                RepairOutcome outcome = _repair.RunAutomaticRepair(
                    tuner,
                    customTargets,
                    delegate { return _closing; },
                    RepairProgress);

                if (_closing) return;
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (_closing) return;
                        _busy = false;
                        if (outcome.ConfigWarning != null || (outcome.Run != null && outcome.Run.Code == "CONFIG_WRITTEN"))
                            _configWarning = outcome.ConfigWarning;
                        _power.SetBusy(false);
                        SetRepairBusy(false);

                        if (outcome.EngineError != null)
                        {
                            SetLastError(outcome.EngineError);
                            ApplyState();
                            MessageBox.Show(this, _lastError, "etodpx - koruma yeniden açılamadı",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        SetLastError(null);
                        if (outcome.RepairError != null)
                        {
                            _repairFailed = true;
                            _lastRepairMessage = "Otomatik düzeltme tamamlanamadı · tekrar deneyebilirsin";
                            _repairFollowUp = RepairFollowUp.Retry;
                            _lastRepairLogPath = outcome.LogPath;
                            ApplyState();
                            string detail = outcome.RepairError;
                            if (outcome.LogPath != null) detail += "\n\nTanı raporu:\n" + outcome.LogPath;
                            MessageBox.Show(this, detail, "etodpx - otomatik düzeltme durdu",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        _repairFailed = false;
                        _lastRepairMessage = null;
                        _repairFollowUp = RepairFollowUp.None;
                        _lastRepairLogPath = outcome.LogPath;
                        ApplyState();
                        ShowTuneResult(outcome.Run, outcome.DnsChanged, outcome.LogPath, outcome.DnsProvider);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        private void RepairProgress(string subtitle, string footer)
        {
            if (_closing) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_closing) return;
                    _subtitle.Text = subtitle;
                    _footer.Text = footer;
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void ShowTuneResult(TunerRun run, bool dnsChanged, string logPath,
                                    string dnsProvider)
        {
            string code = run == null ? "FAILED" : run.Code;
            string copyNote = OtherCopyNote(run);

            if (code == "CONFIG_WRITTEN")
            {
                _subtitle.Text = "Discord ve Roblox için ayarlandı · şimdi dene";
                string dns = dnsChanged
                    ? (dnsProvider + " şifreli DNS kuruldu, bağlantı ayarı bulundu ve koruma yeniden açıldı.")
                    : "Bağlantı ayarı bulundu ve koruma yeniden açıldı.";
                MessageBox.Show(this,
                    dns + "\n\nŞimdi Discord'u ve Roblox'u tekrar deneyebilirsin." + copyNote,
                    "etodpx - hazır", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (code == "ALREADY_OPEN" || code == "OK")
            {
                string text = dnsChanged
                    ? (dnsProvider + " şifreli DNS ile sorun düzeltildi. Discord ve Roblox artık bu bağlantıda doğrudan açılabiliyor görünüyor.")
                    : "Discord ve Roblox bu bağlantıda doğrudan açılabiliyor görünüyor.";
                MessageBox.Show(this,
                    text + " Koruma yeniden açıldı." + copyNote,
                    "etodpx - bağlantı açık", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (RepairController.IsDnsProblem(code))
            {
                _repairFailed = true;
                _repairFollowUp = RepairFollowUp.NetworkSettings;
                _lastRepairLogPath = logPath;
                _lastRepairMessage = "DNS otomatik düzeltilemedi · ağ ayarlarını açabilirsin";
                ApplyState();
                MessageBox.Show(this,
                    "Güvenli DNS seçenekleri bu bilgisayarda tamamlanamadı. " +
                    "Eski DNS ayarın korundu ve koruma yeniden açıldı.\n\n" +
                    "Aynı testi tekrarlamak yerine alttaki “Ağ ayarlarını aç” düğmesini kullanabilirsin. " +
                    "Cloudflare, Google ve Quad9 bağlantılarının üçü de engelleniyorsa bu ağda VPN gerekebilir." +
                    LogNote(logPath),
                    "etodpx - bu ağda DNS aşılamadı", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (code == "IP_BLOCKED" || code == "NO_STRATEGY" || code == "NO_SNI")
            {
                _repairFailed = true;
                _repairFollowUp = RepairFollowUp.Report;
                _lastRepairLogPath = logPath;
                _lastRepairMessage = "Bağlantı ölçüldü ancak çalışan DPI ayarı bulunamadı";
                ApplyState();
                MessageBox.Show(this,
                    "Bu bağlantıdaki engel yalnızca paket ayarıyla aşılamıyor. Bu site için VPN gerekebilir.\n\n" +
                    "Koruma mevcut ayarla yeniden açıldı." + LogNote(logPath) + copyNote,
                    "etodpx - farklı bağlantı türü", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _repairFailed = true;
            _repairFollowUp = RepairFollowUp.Retry;
            _lastRepairLogPath = logPath;
            _lastRepairMessage = "Otomatik tanı tamamlanamadı · yeniden deneyebilirsin";
            ApplyState();
            MessageBox.Show(this,
                "Otomatik ayar bu kez tamamlanamadı. İnternet bağlantını kontrol edip yeniden deneyebilirsin.\n\n" +
                "Koruma mevcut ayarla yeniden açıldı." + LogNote(logPath) + copyNote,
                "etodpx - yeniden dene", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string OtherCopyNote(TunerRun run)
        {
            if (run == null || run.OtherCopies.Count == 0) return string.Empty;
            return "\n\nBaşka klasörden çalışan eski bir etodpx kopyası bulundu:\n" +
                   string.Join("\n", run.OtherCopies.ToArray()) +
                   "\n\nAyar bu uygulamanın bulunduğu klasöre yazıldı. Eski kopyayı açma.";
        }

        private static string LogNote(string path)
        {
            return path == null ? string.Empty : "\n\nTanı raporu:\n" + path;
        }

        private void SetRepairBusy(bool busy, string busyText = null)
        {
            if (_repairButton == null) return;
            _repairButton.IsEnabled = !busy;
            _repairButton.Opacity = busy ? 0.55 : 1;
            if (busy)
            {
                if (_repairText != null) _repairText.Text = busyText ?? "Lütfen bekle…";
                return;
            }
            UpdateRepairButton();
        }

        private void UpdateRepairButton()
        {
            if (_repairButton == null || _repairText == null) return;

            if (_lastError == null)
            {
                if (_repairFollowUp == RepairFollowUp.NetworkSettings)
                    _repairText.Text = "Ağ ayarlarını aç";
                else if (_repairFollowUp == RepairFollowUp.Report)
                    _repairText.Text = "Tanı raporunu aç";
                else if (_repairFollowUp == RepairFollowUp.AppFolder)
                    _repairText.Text = "Paket klasörünü aç";
                else if (_repairFollowUp == RepairFollowUp.Retry)
                    _repairText.Text = "Tanıyı yeniden dene";
                else
                    _repairText.Text = RepairPrompt;
                _repairText.Foreground = _repairFailed ? _danger : _text;
                _repairButton.BorderBrush = _repairFailed ? _danger : _borderB;
                return;
            }

            if (RepairController.IsDriverSecurityError(_lastErrorRaw))
                _repairText.Text = "Windows güvenlik ayarını aç";
            else if (RepairController.IsPackageError(_lastErrorRaw))
                _repairText.Text = "Paket klasörünü aç";
            else
                _repairText.Text = "Sorunu düzelt ve tekrar dene";

            _repairText.Foreground = _danger;
            _repairButton.BorderBrush = _danger;
        }

        private void OnErrorRepairClick()
        {
            if (RepairController.IsDriverSecurityError(_lastErrorRaw))
            {
                RepairController.OpenDriverSecuritySettings();
                return;
            }

            if (RepairController.IsPackageError(_lastErrorRaw))
            {
                RepairController.OpenAppFolder();
                MessageBox.Show(this,
                    "Bu klasörde WinDivert.dll ve WinDivert64.sys birlikte bulunmalı. " +
                    "Dosyalardan biri yoksa ZIP'i yeniden indirip tamamını çıkar.",
                    "etodpx - paket dosyaları", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            RetryProtectionAfterError();
        }

        private void RetryProtectionAfterError()
        {
            _busy = true;
            _power.SetBusy(true);
            SetRepairBusy(true, "Sorun düzeltiliyor…");
            _title.Foreground = _text;
            _title.Text = "DÜZELTİLİYOR";
            _subtitle.Text = "Ağ sürücüsü temizlenip koruma yeniden deneniyor";
            _footer.Foreground = _sub;
            _footer.Text = "Pencereyi kapatma";

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                try
                {
                    _engine.Stop();
                    _repair.StartEngineWithRecovery();
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    try { _engine.Stop(); } catch { }
                }

                if (_closing) return;
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (_closing) return;
                        _busy = false;
                        SetLastError(err);
                        _power.SetBusy(false);
                        SetRepairBusy(false);
                        ApplyState();

                        if (err == null)
                        {
                            MessageBox.Show(this,
                                "Sorun giderildi ve koruma yeniden açıldı.",
                                "etodpx - hazır", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            MessageBox.Show(this, _lastError,
                                "etodpx - sorun otomatik düzeltilemedi",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        private void SetLastError(string raw)
        {
            _lastErrorRaw = raw;
            _lastError = raw == null ? null : RepairController.FriendlyError(raw);
        }

        private void OnEngineFault(string message)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    SetLastError(message);
                    ApplyState();
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void ApplyState()
        {
            bool on = _engine.IsRunning && _lastError == null;
            _power.SetActive(on, Palette.Accent);
            UpdateRepairButton();

            if (_lastError != null)
            {
                _title.Text = "HATA";
                _title.Foreground = _danger;
                _subtitle.Text = "Koruma başlatılamadı";
                _footer.Foreground = _danger;
                // Alt satir dar; ilk satiri gosterip gerisini pencereye birakiyoruz.
                _footer.Text = FirstLine(_lastError);
                return;
            }

            _title.Foreground = _text;
            _footer.Foreground = _sub;

            if (on)
            {
                if (_repairFailed)
                {
                    _title.Text = "KORUMA SINIRLI";
                    _subtitle.Text = "Koruma çalışıyor · bağlantı doğrulanamadı";
                    _footer.Foreground = _danger;
                }
                else
                {
                    _title.Text = "KORUMA AÇIK";
                    _subtitle.Text = UsesFakePacket()
                        ? "Bu bilgisayara uygun bağlantı ayarı etkin"
                        : "Güvenli başlangıç ayarı etkin";
                }
                _footer.Text = FooterInfo();
            }
            else
            {
                _title.Text = "KAPALI";
                _subtitle.Text = "Korumayı başlatmak için dokun";
                _footer.Foreground = _configWarning == null ? _sub : _danger;
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(_dnsNotice)) parts.Add(_dnsNotice);
                if (_configWarning != null) parts.Add("Ayar uyarısı · " + _configWarning);
                _footer.Text = string.Join(" · ", parts.ToArray());
            }
            RefreshStats();
        }

        // Hicbiri acik degilse yalnizca bolme var: guvenli, ama paketleri yeniden
        // birlestiren DPI kutularina karsi genelde yetmiyor.
        private bool UsesFakePacket()
        {
            if (!_cfg.FakeBadSum && !_cfg.FakeTtl && !_cfg.FakeWrongSeq) return false;
            return _cfg.TlsMode.Contains("fake") || _cfg.HttpMode.Contains("fake");
        }

        // Cok satirli mesajin alt satira sigan ilk parcasi.
        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            string head = i < 0 ? s : s.Substring(0, i);
            return i < 0 ? head : head + "  (ayrıntı için pencereye bak)";
        }

        // Alt satir yalnizca soylenecek bir sey varsa doluyor - hangi ayarin etkin
        // oldugunu zaten alt baslik yaziyor.
        private string FooterInfo()
        {
            if (_repairFailed && !string.IsNullOrEmpty(_lastRepairMessage))
                return _lastRepairMessage;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(_dnsNotice))
                parts.Add(_dnsNotice);
            if (_engine.QuicWarning != null)
                parts.Add("Hızlı bağlantı kullanılamadı, standart koruma devam ediyor");
            if (_configWarning != null)
                parts.Add("Ayar uyarısı: " + _configWarning);
            return string.Join(" · ", parts.ToArray());
        }

        private void RefreshStats()
        {
            if (!_engine.IsRunning) return;
            var s = _engine.Stats;

            // Sayaci kalabaliklastirmiyoruz; gonderim hatasi varsa alt satirda belirtiyoruz.
            if (_lastError == null)
            {
                long errs = s.SendErrors;
                string info = FooterInfo();
                if (errs > 0)
                {
                    // FooterInfo bos olabilir; bos iken ayraci basa koymuyoruz.
                    string note = errs + " bağlantı paketi gönderilemedi";
                    info = info.Length == 0 ? note : info + "  ·  " + note;
                }
                bool warning = errs > 0 || _repairFailed ||
                               _engine.QuicWarning != null || _configWarning != null;
                _footer.Foreground = warning ? _danger : _sub;
                _footer.Text = info;
            }
        }

        private void ApplyWindowChrome()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.ApplyChrome(hwnd, true, Native.ToBgr(Palette.Border));
        }
    }
}
