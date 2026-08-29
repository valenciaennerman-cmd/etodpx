// Ana pencere. XAML yok, hepsi kodla kuruluyor - tek csc.exe cagrisi yetsin diye.
//
// Baslik cubugu MouseLeftButtonDown'da DragMove() cagiriyor. DragMove modal bir
// surukleme dongusune girip fareyi yakaladigi icin ustune basilan dugmeye
// MouseLeftButtonUp hic ulasmiyor ve dugmeler olu gorunuyor. Dugmeler bu yuzden
// kendi MouseLeftButtonDown'larini Handled isaretliyor.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private string _configWarning;
        private Process _tuneProcess;

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

            SourceInitialized += delegate { ApplyWindowChrome(); };
            Loaded += OnLoaded;
            Closing += delegate
            {
                _closing = true;
                StopTuner();
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
                Text = "Discord açılmıyor mu?  Tek tıkla düzelt",
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
                    if (starting) StartEngineWithRecovery();
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

            string tuner = System.IO.Path.Combine(Program.AppDir(), "etodpx-tune.exe");
            if (!File.Exists(tuner))
            {
                _repairFailed = true;
                _lastRepairMessage = "Otomatik düzeltme dosyası eksik · ZIP'in tamamını yeniden çıkar";
                ApplyState();
                MessageBox.Show(this,
                    "Otomatik ayar dosyası eksik. ZIP'i yeniden indirip klasörün " +
                    "tamamını çıkardıktan sonra tekrar aç.",
                    "etodpx - paket eksik", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                "Discord bağlantısını adım adım kontrol edip güvenle düzeltebildiğim " +
                "sorunları uygulayacağım.\n\n" +
                "Gerekirse aktif internet bağlantısına şifreli DNS kurulur; eski DNS " +
                "ayarı önce yedeklenir ve doğrulama başarısız olursa geri alınır.\n\n" +
                "Test sırasında koruma kısa süre kapanır. İşlem birkaç dakika sürebilir.\n\n" +
                "Başlayalım mı?",
                "etodpx - otomatik düzelt", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            _busy = true;
            SetLastError(null);
            _repairFailed = false;
            _lastRepairMessage = null;
            _power.SetBusy(true);
            SetRepairBusy(true, "Bağlantı ayarlanıyor…");
            _title.Foreground = _text;
            _title.Text = "AYARLANIYOR";
            _subtitle.Text = "Discord için uygun bağlantı ayarı aranıyor";
            _footer.Foreground = _sub;
            _footer.Text = "Bu işlem birkaç dakika sürebilir · pencereyi kapatma";

            ThreadPool.QueueUserWorkItem(delegate { RunAutomaticRepair(tuner); });
        }

        private void RunAutomaticRepair(string tuner)
        {
            TunerRun run = null;
            TunerRun firstRun = null;
            DnsBackup dnsBackup = null;
            bool dnsMayHaveChanged = false;
            bool keepDns = false;
            string repairError = null;
            string engineError = null;
            List<string> closedCopies = null;

            try
            {
                closedCopies = CloseOtherEtodpxCopies();
                RepairProgress("1/4 · Ağ sürücüsü kontrol ediliyor", "Koruma test için kısa süre durduruldu");
                _engine.Stop();

                RepairProgress("2/4 · DNS ve bağlantı engeli ölçülüyor", "İlk tanı birkaç saniye sürebilir");
                run = ExecuteTuner(tuner);
                MergeCopies(run, closedCopies);
                firstRun = run;

                if (IsDnsProblem(run.Code))
                {
                    RepairProgress("3/4 · Şifreli DNS güvenle hazırlanıyor", "Eski DNS ayarı geri alma noktası olarak saklanıyor");
                    string dnsError;
                    dnsBackup = NetworkRepair.CaptureActiveDns(out dnsError);
                    if (dnsBackup == null)
                        throw new InvalidOperationException("Aktif ağ bağlantısının DNS ayarı yedeklenemedi: " + dnsError);

                    // Komut son adimda patlasa bile DNS'e dokunmus olabilir; bayrak
                    // once kalkiyor ki finally mutlaka geri alsin.
                    dnsMayHaveChanged = true;
                    if (!NetworkRepair.ConfigureCloudflareDoh(dnsBackup, out dnsError))
                        throw new InvalidOperationException("Şifreli DNS kurulamadı: " + dnsError);

                    Thread.Sleep(1200);
                    RepairProgress("4/4 · Bağlantı yeniden doğrulanıyor", "DNS düzeldiyse uygun DPI ayarı otomatik bulunacak");
                    run = ExecuteTuner(tuner);
                    MergeCopies(run, closedCopies);
                    if (IsDnsProblem(run.Code))
                        throw new InvalidOperationException(
                            "Şifreli DNS bu ağda doğrulanamadı. Eski DNS ayarı geri yüklenecek.");

                    // Ikinci tani DNS asamasini gecti; sonuc IP engeli bile olsa
                    // DNS artik dogru calisiyor demektir, yeni ayar kalsin.
                    keepDns = true;
                }

                if (run != null && run.Code == "CONFIG_WRITTEN") ReloadConfig();
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
                    if (!_closing && !_engine.IsRunning) StartEngineWithRecovery();
                }
                catch (Exception ex) { engineError = ex.Message; }
            }

            string logPath = SaveRepairLog(run, firstRun, repairError, engineError, keepDns);

            if (_closing) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_closing) return;
                    _busy = false;
                    _power.SetBusy(false);
                    SetRepairBusy(false);

                    if (engineError != null)
                    {
                        SetLastError(engineError);
                        ApplyState();
                        MessageBox.Show(this, _lastError, "etodpx - koruma yeniden açılamadı",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    SetLastError(null);
                    if (repairError != null)
                    {
                        _repairFailed = true;
                        _lastRepairMessage = "Otomatik düzeltme tamamlanamadı · tekrar deneyebilirsin";
                        ApplyState();
                        string detail = repairError;
                        if (logPath != null) detail += "\n\nTanı raporu:\n" + logPath;
                        MessageBox.Show(this, detail, "etodpx - otomatik düzeltme durdu",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    _repairFailed = false;
                    _lastRepairMessage = null;
                    ApplyState();
                    ShowTuneResult(run, keepDns, logPath);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private sealed class TunerRun
        {
            public int ExitCode;
            public string Code;
            public string Output;
            public readonly List<string> OtherCopies = new List<string>();
        }

        private TunerRun ExecuteTuner(string tuner)
        {
            var run = new TunerRun { ExitCode = -1, Output = string.Empty };
            Process p = null;
            try
            {
                var stdout = new System.Text.StringBuilder();
                var stderr = new System.Text.StringBuilder();
                p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = tuner,
                    Arguments = "--auto --no-manage-app discord.com",
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

        private static List<string> CloseOtherEtodpxCopies()
        {
            var found = new List<string>();
            string expected = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Program.AppDir(), "etodpx.exe"));
            int ownId = Process.GetCurrentProcess().Id;

            foreach (var p in Process.GetProcessesByName("etodpx"))
            {
                try
                {
                    if (p.Id == ownId) continue;
                    string actual = System.IO.Path.GetFullPath(p.MainModule.FileName);
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

        private void ReloadConfig()
        {
            var fresh = Config.Load(System.IO.Path.Combine(Program.AppDir(), "etodpx.conf"));
            _cfg.CopyFrom(fresh);
            _configWarning = fresh.Warnings.Count == 0
                ? null
                : string.Join("\n", fresh.Warnings.ToArray());
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

        private static bool IsDnsProblem(string code)
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
                    string path = System.Text.Encoding.UTF8.GetString(
                        Convert.FromBase64String(line.Substring(prefix.Length).Trim()));
                    if (path.Length > 0 && !run.OtherCopies.Contains(path)) run.OtherCopies.Add(path);
                }
                catch { }
            }
        }

        private static string SaveRepairLog(TunerRun run, TunerRun firstRun, string repairError,
                                            string engineError, bool dnsChanged)
        {
            try
            {
                string path = System.IO.Path.Combine(Program.AppDir(), "etodpx-repair.log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("result=" + (run == null ? "NO_RESULT" : run.Code));
                if (firstRun != null && !ReferenceEquals(firstRun, run))
                    sb.AppendLine("first_result=" + firstRun.Code);
                sb.AppendLine("exit_code=" + (run == null ? -1 : run.ExitCode));
                sb.AppendLine("encrypted_dns_changed=" + dnsChanged);
                if (repairError != null) sb.AppendLine("repair_error=" + repairError);
                if (engineError != null) sb.AppendLine("engine_error=" + engineError);
                sb.AppendLine();
                // Iki tur varsa ikisini de yaz. Eskiden yalnizca sonuncusu
                // kaliyordu ve baslangic durumu raporlardan okunamiyordu.
                if (firstRun != null && !ReferenceEquals(firstRun, run))
                {
                    sb.AppendLine("=== 1. tani (degisiklikten once) ===");
                    sb.AppendLine(firstRun.Output);
                    sb.AppendLine("=== 2. tani (sifreli DNS kurulduktan sonra) ===");
                }
                if (run != null) sb.Append(run.Output);
                File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                return path;
            }
            catch { return null; }
        }

        private void ShowTuneResult(TunerRun run, bool dnsChanged, string logPath)
        {
            string code = run == null ? "FAILED" : run.Code;
            string copyNote = OtherCopyNote(run);

            if (code == "CONFIG_WRITTEN")
            {
                _subtitle.Text = "Discord için ayarlandı · şimdi tekrar dene";
                string dns = dnsChanged
                    ? "Şifreli DNS kuruldu, bağlantı ayarı bulundu ve koruma yeniden açıldı."
                    : "Bağlantı ayarı bulundu ve koruma yeniden açıldı.";
                MessageBox.Show(this,
                    dns + "\n\nŞimdi Discord'u tekrar deneyebilirsin." + copyNote,
                    "etodpx - hazır", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (code == "ALREADY_OPEN" || code == "OK")
            {
                string text = dnsChanged
                    ? "DNS sorunu düzeltildi. Discord artık bu bağlantıda doğrudan açılabiliyor görünüyor."
                    : "Discord bu bağlantıda doğrudan açılabiliyor görünüyor.";
                MessageBox.Show(this,
                    text + " Koruma yeniden açıldı." + copyNote,
                    "etodpx - bağlantı açık", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (IsDnsProblem(code))
            {
                _repairFailed = true;
                _lastRepairMessage = "DNS otomatik düzeltilemedi · tekrar deneyebilir veya ağ ayarını açabilirsin";
                ApplyState();
                var open = MessageBox.Show(this,
                    "DNS sorunu bu ağda otomatik düzeltilemedi. Eski DNS ayarı korundu.\n\n" +
                    "Windows ağ ayarlarını şimdi açayım mı?" + LogNote(logPath),
                    "etodpx - DNS ayarı gerekiyor", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes) OpenNetworkSettings();
                return;
            }

            if (code == "IP_BLOCKED" || code == "NO_STRATEGY" || code == "NO_SNI")
            {
                _repairFailed = true;
                _lastRepairMessage = "Bağlantı ölçüldü ancak çalışan DPI ayarı bulunamadı";
                ApplyState();
                MessageBox.Show(this,
                    "Bu bağlantıdaki engel yalnızca paket ayarıyla aşılamıyor. Bu site için VPN gerekebilir.\n\n" +
                    "Koruma mevcut ayarla yeniden açıldı." + LogNote(logPath) + copyNote,
                    "etodpx - farklı bağlantı türü", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _repairFailed = true;
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

        private static bool Contains(string text, string value)
        {
            return text != null && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void OpenNetworkSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:network-advancedsettings",
                    UseShellExecute = true
                });
            }
            catch { }
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
                _repairText.Text = _repairFailed
                    ? "Tekrar tanıla ve düzelt"
                    : "Discord açılmıyor mu?  Tek tıkla düzelt";
                _repairText.Foreground = _repairFailed ? _danger : _text;
                _repairButton.BorderBrush = _repairFailed ? _danger : _borderB;
                return;
            }

            if (IsDriverSecurityError(_lastErrorRaw))
                _repairText.Text = "Windows güvenlik ayarını aç";
            else if (IsPackageError(_lastErrorRaw))
                _repairText.Text = "Paket klasörünü aç";
            else
                _repairText.Text = "Sorunu düzelt ve tekrar dene";

            _repairText.Foreground = _danger;
            _repairButton.BorderBrush = _danger;
        }

        private void OnErrorRepairClick()
        {
            if (IsDriverSecurityError(_lastErrorRaw))
            {
                OpenDriverSecuritySettings();
                return;
            }

            if (IsPackageError(_lastErrorRaw))
            {
                OpenAppFolder();
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
                    StartEngineWithRecovery();
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

        private void StopTuner()
        {
            var p = _tuneProcess;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        // Eski GoodbyeDPI/zapret kurulumlarindan kalan devre disi WinDivert servisi
        // acilisi engelliyor. Yonetici oldugumuz icin temizleyip tekrar deniyoruz.
        private void StartEngineWithRecovery()
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

        private static bool RepairDisabledWinDivert()
        {
            // WinDivert gecici bir servis: sc stop son tutamagi kapatinca kendini
            // kayittan silebiliyor. Sonraki sc delete o zaman 1060 verir - basari.
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

        private static string FriendlyError(string message)
        {
            if (Contains(message, "WinDivert.dll bulunamadı") || Contains(message, "sürücü dosyası"))
                return "Paket eksik görünüyor. ZIP'i yeniden indir, sağ tıklayıp “Tümünü ayıkla” de ve çıkan klasördeki etodpx.exe'yi aç.";
            if (Contains(message, "imzası Windows tarafından kabul edilmedi"))
                return "Windows ağ sürücüsünü engelledi. Windows Güvenliği > Cihaz güvenliği > Çekirdek yalıtımı bölümünü kontrol edip bilgisayarı yeniden başlat.";
            if (Contains(message, "devre dışı bırakılmış eski bir WinDivert"))
                return "Eski ağ sürücüsü otomatik temizlenemedi. Bilgisayarı yeniden başlatıp etodpx'i tekrar aç.";
            return message;
        }

        private void SetLastError(string raw)
        {
            _lastErrorRaw = raw;
            _lastError = raw == null ? null : FriendlyError(raw);
        }

        private static bool IsPackageError(string message)
        {
            return Contains(message, "WinDivert.dll bulunamadı") ||
                   Contains(message, "sürücü dosyası") ||
                   Contains(message, "Paket eksik");
        }

        private static bool IsDriverSecurityError(string message)
        {
            return Contains(message, "imzası Windows tarafından kabul edilmedi") ||
                   Contains(message, "Sürücü Windows tarafından engellendi") ||
                   Contains(message, "Çekirdek yalıtımı") ||
                   Contains(message, "Bellek bütünlüğü");
        }

        private static void OpenDriverSecuritySettings()
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

        private static void OpenAppFolder()
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
                _title.Text = "KORUMA AÇIK";
                _subtitle.Text = UsesFakePacket()
                    ? "Bu bilgisayara uygun bağlantı ayarı etkin"
                    : "Güvenli başlangıç ayarı etkin";
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

            // Sayacı kalabalıklastirmiyoruz; gonderim hatasi varsa alt satirda belirtiyoruz.
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
