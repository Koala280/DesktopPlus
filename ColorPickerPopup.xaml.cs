using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace DesktopPlus
{
    public partial class ColorPickerPopup : System.Windows.Controls.UserControl
    {
        private double _hue;
        private double _saturation;
        private double _value = 1;
        private bool _draggingSv;
        private bool _draggingHue;
        private bool _suppressUpdate;
        private MediaColor _originalColor;

        public event Action<MediaColor>? ColorChanged;

        public MediaColor SelectedColor { get; private set; }

        public ColorPickerPopup()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateOverlaySize();
            SvCanvas.SizeChanged += (_, _) => { UpdateOverlaySize(); UpdateSvCursorPosition(); };
            HueCanvas.SizeChanged += (_, _) => UpdateHueCursorPosition();
        }

        public void SetColor(MediaColor color)
        {
            _originalColor = color;
            PreviewOriginal.Background = new SolidColorBrush(color);

            RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);

            _suppressUpdate = true;
            UpdateAllFromHsv();
            _suppressUpdate = false;
        }

        // ─── HSV ↔ RGB ───────────────────────────────────

        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;

            v = max;
            s = max == 0 ? 0 : delta / max;

            if (delta == 0)
                h = 0;
            else if (max == rd)
                h = 60 * (((gd - bd) / delta) % 6);
            else if (max == gd)
                h = 60 * (((bd - rd) / delta) + 2);
            else
                h = 60 * (((rd - gd) / delta) + 4);

            if (h < 0) h += 360;
        }

        private static MediaColor HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;

            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return MediaColor.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private static MediaColor HueToColor(double hue)
        {
            return HsvToRgb(hue, 1, 1);
        }

        // ─── Update all UI ───────────────────────────────

        private void UpdateAllFromHsv()
        {
            var color = HsvToRgb(_hue, _saturation, _value);
            SelectedColor = color;

            SvCanvas.Background = new SolidColorBrush(HueToColor(_hue));
            PreviewNew.Background = new SolidColorBrush(color);

            _suppressUpdate = true;
            HexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            RInput.Text = color.R.ToString();
            GInput.Text = color.G.ToString();
            BInput.Text = color.B.ToString();
            _suppressUpdate = false;

            UpdateSvCursorPosition();
            UpdateHueCursorPosition();

            ColorChanged?.Invoke(color);
        }

        // ─── SV Canvas ──────────────────────────────────

        private void UpdateOverlaySize()
        {
            if (SvCanvas.ActualWidth <= 0 || SvCanvas.ActualHeight <= 0) return;
            SvWhiteOverlay.Width = SvCanvas.ActualWidth;
            SvWhiteOverlay.Height = SvCanvas.ActualHeight;
            SvBlackOverlay.Width = SvCanvas.ActualWidth;
            SvBlackOverlay.Height = SvCanvas.ActualHeight;
        }

        private void UpdateSvCursorPosition()
        {
            if (SvCanvas.ActualWidth <= 0 || SvCanvas.ActualHeight <= 0) return;
            double x = _saturation * SvCanvas.ActualWidth - SvCursor.Width / 2;
            double y = (1 - _value) * SvCanvas.ActualHeight - SvCursor.Height / 2;
            Canvas.SetLeft(SvCursor, x);
            Canvas.SetTop(SvCursor, y);
        }

        private void SvCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = true;
            SvCanvas.CaptureMouse();
            UpdateSvFromMouse(e.GetPosition(SvCanvas));
        }

        private void SvCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_draggingSv) return;
            UpdateSvFromMouse(e.GetPosition(SvCanvas));
        }

        private void SvCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = false;
            SvCanvas.ReleaseMouseCapture();
        }

        private void UpdateSvFromMouse(WpfPoint pos)
        {
            _saturation = Math.Max(0, Math.Min(1, pos.X / SvCanvas.ActualWidth));
            _value = Math.Max(0, Math.Min(1, 1 - pos.Y / SvCanvas.ActualHeight));
            UpdateAllFromHsv();
        }

        // ─── Hue Canvas ─────────────────────────────────

        private void UpdateHueCursorPosition()
        {
            if (HueCanvas.ActualHeight <= 0) return;
            double y = (_hue / 360) * HueCanvas.ActualHeight;
            Canvas.SetTop(HueCursor, y - HueCursor.Height / 2);
            HueCursor.Width = HueCanvas.ActualWidth;
        }

        private void HueCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = true;
            HueCanvas.CaptureMouse();
            UpdateHueFromMouse(e.GetPosition(HueCanvas));
        }

        private void HueCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_draggingHue) return;
            UpdateHueFromMouse(e.GetPosition(HueCanvas));
        }

        private void HueCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = false;
            HueCanvas.ReleaseMouseCapture();
        }

        private void UpdateHueFromMouse(WpfPoint pos)
        {
            _hue = Math.Max(0, Math.Min(359.99, (pos.Y / HueCanvas.ActualHeight) * 360));
            UpdateAllFromHsv();
        }

        // ─── Text inputs ────────────────────────────────

        private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressUpdate) return;
            var text = HexInput.Text.Trim();
            if (!text.StartsWith("#")) text = "#" + text;
            if (text.Length != 7) return;

            try
            {
                var color = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(text);
                RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);
                _suppressUpdate = true;
                var c = HsvToRgb(_hue, _saturation, _value);
                SelectedColor = c;
                SvCanvas.Background = new SolidColorBrush(HueToColor(_hue));
                PreviewNew.Background = new SolidColorBrush(c);
                RInput.Text = c.R.ToString();
                GInput.Text = c.G.ToString();
                BInput.Text = c.B.ToString();
                UpdateSvCursorPosition();
                UpdateHueCursorPosition();
                _suppressUpdate = false;
                ColorChanged?.Invoke(c);
            }
            catch { }
        }

        private void RgbInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressUpdate) return;
            if (!byte.TryParse(RInput.Text, out byte r)) return;
            if (!byte.TryParse(GInput.Text, out byte g)) return;
            if (!byte.TryParse(BInput.Text, out byte b)) return;

            RgbToHsv(r, g, b, out _hue, out _saturation, out _value);
            _suppressUpdate = true;
            var c = HsvToRgb(_hue, _saturation, _value);
            SelectedColor = c;
            SvCanvas.Background = new SolidColorBrush(HueToColor(_hue));
            PreviewNew.Background = new SolidColorBrush(c);
            HexInput.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            UpdateSvCursorPosition();
            UpdateHueCursorPosition();
            _suppressUpdate = false;
            ColorChanged?.Invoke(c);
        }

        // ─── Presets ─────────────────────────────────────

        private void Preset_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.Border border) return;
            if (border.Background is not SolidColorBrush brush) return;
            var color = brush.Color;
            RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);
            UpdateAllFromHsv();
        }
    }
}
