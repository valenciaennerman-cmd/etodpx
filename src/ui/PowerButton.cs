// Ortadaki buyuk baslat/durdur dugmesi.
//
// Her animasyonda Timeline.DesiredFrameRate var: WPF aksi halde ekranin yenileme
// hizinda kare uretiyor ve 240 Hz bir monitorde yavas bir parilti icin saniyede
// 240 kare cizmek bosuna islemci yakiyor. Parilti de DropShadowEffect degil
// radyal gradyan - blur her karede yeniden hesaplaniyor, gradyan hesaplanmiyor.
//
// Path'i HorizontalAlignment=Center ile ortalamak ise ise yaramiyor: WPF onu
// geometrinin sinir kutusuna gore yerlestirdigi icin negatif koordinatli bir
// geometride simge gozle gorulur sekilde kayiyor. Path kutuyu tamamen kapliyor,
// koordinatlar kutu icinde mutlak; merkez (96,96).
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Etodpx.Ui
{
    internal sealed class PowerButton : Grid
    {
        private const double Box = 192;          // kontrolun kare olculeri
        private const double Mid = Box / 2;      // merkez: 96
        private const double RingSize = 134;
        private const double FaceSize = 118;

        private const double GlyphR = 18;        // guc simgesi yayinin yaricapi
        private const double GapX = 12.7;        // yay ucu = R * cos(45)
        private const double BarTop = 20;
        private const double BarBottom = 3;

        private readonly Ellipse _glow;
        private readonly Ellipse _ring;
        private readonly Ellipse _face;
        private readonly System.Windows.Shapes.Path _glyph;
        private readonly ScaleTransform _scale;

        private Brush _ringOff;
        private Brush _accent;
        private Brush _dim;
        private bool _isOn;

        public event Action Click;

        public PowerButton()
        {
            Width = Box;
            Height = Box;
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;   // vurus testine dahil olsun
            RenderTransformOrigin = new Point(0.5, 0.5);
            _scale = new ScaleTransform(1, 1);
            RenderTransform = _scale;

            _glow = new Ellipse
            {
                Width = Box,
                Height = Box,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false
            };

            _ring = new Ellipse
            {
                Width = RingSize,
                Height = RingSize,
                StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            _face = new Ellipse
            {
                Width = FaceSize,
                Height = FaceSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            // Kutuyu tamamen kaplar; koordinatlar kutu icinde mutlaktir.
            _glyph = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(BuildGlyph()),
                StrokeThickness = 3.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Stretch = Stretch.None,
                IsHitTestVisible = false
            };

            Children.Add(_glow);
            Children.Add(_ring);
            Children.Add(_face);
            Children.Add(_glyph);

            MouseEnter += delegate { AnimateScale(1.035); };
            MouseLeave += delegate { AnimateScale(1.0); };
            MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;               // baslik cubugunun surukleme kancasina gitmesin
                CaptureMouse();
                AnimateScale(0.965);
            };
            MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                bool inside = IsMouseCaptured && IsMouseOver;
                if (IsMouseCaptured) ReleaseMouseCapture();
                AnimateScale(IsMouseOver ? 1.035 : 1.0);
                if (inside && Click != null) Click();
            };
        }

        // Ustte bosluklu yay + dikey cizgi.
        private static string BuildGlyph()
        {
            var ci = CultureInfo.InvariantCulture;
            double ax = Mid - GapX, ay = Mid - GapX;   // yayin sol ust ucu
            double bx = Mid + GapX;                    // yayin sag ust ucu
            return string.Format(ci,
                "M {0},{1} A {2},{2} 0 1 0 {3},{1} M {4},{5} L {4},{6}",
                ax, ay, GlyphR, bx, Mid, Mid - BarTop, Mid - BarBottom);
        }

        private void AnimateScale(double to)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(a, 60);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        public void Bind(Brush face, Brush ringOff)
        {
            _face.Fill = face;
            _ringOff = ringOff;
            if (!_isOn) _ring.Stroke = ringOff;
        }

        public void SetAccent(Brush accent, Brush dim)
        {
            _accent = accent;
            _dim = dim;
            if (!_isOn) _glyph.Stroke = dim;
        }

        // Acikken halka vurgu rengine gecer, hale nefes almaya baslar.
        public void SetActive(bool on, Color glowColor)
        {
            _isOn = on;
            _ring.Stroke = on ? _accent : _ringOff;
            _glyph.Stroke = on ? _accent : _dim;

            var stops = new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0.00),
                new GradientStop(Color.FromArgb(0, glowColor.R, glowColor.G, glowColor.B), 0.63),
                new GradientStop(Color.FromArgb(0x5E, glowColor.R, glowColor.G, glowColor.B), 0.72),
                new GradientStop(Colors.Transparent, 1.00)
            };
            var b = new RadialGradientBrush(stops)
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            b.Freeze();
            _glow.Fill = b;

            var thick = new DoubleAnimation(on ? 3 : 2, TimeSpan.FromMilliseconds(260))
            { FillBehavior = FillBehavior.HoldEnd };
            Timeline.SetDesiredFrameRate(thick, 30);
            _ring.BeginAnimation(Ellipse.StrokeThicknessProperty, thick);

            if (on) StartPulse();
            else StopPulse();
        }

        private void StartPulse()
        {
            var pulse = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(2400))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            // Surekli calisan tek animasyon; kare tavani asil burada onemli.
            Timeline.SetDesiredFrameRate(pulse, 15);
            _glow.BeginAnimation(OpacityProperty, pulse);
        }

        private void StopPulse()
        {
            _glow.BeginAnimation(OpacityProperty, null);
            _glow.Opacity = 0;
        }

        // Pencere gizlendiginde animasyon tamamen durur.
        public void SetAnimationsEnabled(bool enabled)
        {
            if (enabled) { if (_isOn) StartPulse(); }
            else
            {
                _glow.BeginAnimation(OpacityProperty, null);
                _glow.Opacity = _isOn ? 0.7 : 0;
            }
        }

        // Baslatma/durdurma sirasindaki bekleme gorunumu.
        public void SetBusy(bool busy)
        {
            IsEnabled = !busy;
            Opacity = busy ? 0.55 : 1.0;
        }
    }
}
