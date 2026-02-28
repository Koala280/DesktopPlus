using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        private void OnAppearanceChanged()
        {
            Dispatcher.Invoke(() => ApplyAppearance(MainWindow.Appearance));
        }

        public void ApplyAppearance(AppearanceSettings appearance)
        {
            if (appearance == null) return;
            _currentAppearance = appearance;

            double outerRadius = Math.Max(10, appearance.CornerRadius + 2);
            double innerRadius = Math.Max(6, outerRadius - 2);
            var panelBorderBrush = MainWindow.BuildPanelBorderBrush(appearance);

            PanelChrome.CornerRadius = new CornerRadius(outerRadius);
            PanelChrome.Background = System.Windows.Media.Brushes.Transparent;
            PanelChrome.BorderBrush = panelBorderBrush;
            PanelChrome.BorderThickness = new Thickness(1);
            if (HeaderShadowHost != null)
            {
                HeaderShadowHost.CornerRadius = new CornerRadius(outerRadius, outerRadius, 0, 0);
            }
            if (BodyShadowHost != null)
            {
                BodyShadowHost.CornerRadius = new CornerRadius(0, 0, outerRadius, outerRadius);
            }

            SetHeaderCornerBaseRadius(innerRadius);
            if (ContentFrame != null)
            {
                ContentFrame.CornerRadius = new CornerRadius(0, 0, innerRadius, innerRadius);
            }
            if (HeaderShadow != null)
            {
                HeaderShadow.BlurRadius = MainWindow.ResolveHeaderShadowBlur(appearance);
                HeaderShadow.Opacity = MainWindow.ResolveHeaderShadowOpacity(appearance);
            }
            if (BodyShadow != null)
            {
                BodyShadow.BlurRadius = MainWindow.ResolveBodyShadowBlur(appearance);
                BodyShadow.Opacity = MainWindow.ResolveBodyShadowOpacity(appearance);
            }

            HeaderBar.Background = MainWindow.BuildPanelHeaderBrush(appearance);
            if (ContentFrame != null)
            {
                ContentFrame.Background = MainWindow.BuildPanelContentBrush(appearance);
            }

            var accentBrush = CreateBrush(appearance.AccentColor, 1.0, MediaColor.FromRgb(90, 200, 250));
            PanelTitle.Foreground = accentBrush;
            SearchBox.CaretBrush = new SolidColorBrush(MediaColor.FromRgb(242, 245, 250));
            PanelTitle.FontSize = Math.Max(12, appearance.TitleFontSize);
            SearchBox.FontSize = Math.Max(10, appearance.ItemFontSize - 1);

            ApplyFontFamily(appearance.FontFamily);
            if (Resources.Contains("PanelBorder"))
            {
                Resources["PanelBorder"] = panelBorderBrush;
            }
            UpdateResourceBrush("PanelText", appearance.TextColor, MediaColor.FromRgb(242, 245, 250));
            UpdateResourceBrush("PanelMuted", appearance.MutedTextColor, MediaColor.FromRgb(167, 176, 192));
            UpdateListItemAppearance();
        }

        private void ApplyFontFamily(string? fontFamily)
        {
            if (string.IsNullOrWhiteSpace(fontFamily)) return;
            try
            {
                FontFamily = new System.Windows.Media.FontFamily(fontFamily);
            }
            catch
            {
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            }
        }

        private void UpdateResourceBrush(string key, string value, MediaColor fallback)
        {
            if (Resources.Contains(key))
            {
                Resources[key] = CreateBrush(value, 1.0, fallback);
            }
        }

        private void UpdateListItemAppearance()
        {
            var appearance = _currentAppearance ?? MainWindow.Appearance;
            if (appearance == null || FileList == null) return;

            var fileBrush = CreateBrush(appearance.TextColor, 1.0, MediaColor.FromRgb(242, 245, 250));
            var folderBrush = CreateBrush(ResolveFolderColor(appearance), 1.0, MediaColor.FromRgb(110, 139, 255));
            double textSize = Math.Max(8, appearance.ItemFontSize) * zoomFactor;

            foreach (var item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Content is StackPanel panel)
                {
                    var text = panel.Children.OfType<TextBlock>().FirstOrDefault();
                    if (text == null) continue;

                    bool isFolder = IsFolderItem(item.Tag);
                    text.Foreground = isFolder ? folderBrush : fileBrush;
                    text.FontSize = textSize;
                    text.FontFamily = FontFamily;
                }
            }
        }

        private static bool IsFolderItem(object? tag)
        {
            if (tag is string path)
            {
                return System.IO.Directory.Exists(path);
            }
            return false;
        }

        private static string ResolveFolderColor(AppearanceSettings appearance)
        {
            if (!string.IsNullOrWhiteSpace(appearance.FolderTextColor)) return appearance.FolderTextColor;
            if (!string.IsNullOrWhiteSpace(appearance.AccentColor)) return appearance.AccentColor;
            return "#6E8BFF";
        }

        private SolidColorBrush CreateBrush(string colorValue, double opacity, MediaColor fallback)
        {
            byte alpha = (byte)Math.Max(0, Math.Min(255, opacity * 255));

            try
            {
                var parsed = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(colorValue);
                var withOpacity = MediaColor.FromArgb(alpha, parsed.R, parsed.G, parsed.B);
                return new SolidColorBrush(withOpacity);
            }
            catch
            {
                var withOpacity = MediaColor.FromArgb(alpha, fallback.R, fallback.G, fallback.B);
                return new SolidColorBrush(withOpacity);
            }
        }

    }
}
