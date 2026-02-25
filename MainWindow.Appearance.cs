using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private void PopulateAppearanceInputs(AppearanceSettings appearance)
        {
            if (appearance == null) return;

            BackgroundColorInput.Text = appearance.BackgroundColor;
            HeaderColorInput.Text = appearance.HeaderColor;
            AccentColorInput.Text = appearance.AccentColor;
            if (TextColorInput != null) TextColorInput.Text = appearance.TextColor;
            if (FolderColorInput != null)
            {
                FolderColorInput.Text = string.IsNullOrWhiteSpace(appearance.FolderTextColor)
                    ? appearance.AccentColor
                    : appearance.FolderTextColor;
            }
            if (FontFamilyInput != null) FontFamilyInput.Text = appearance.FontFamily;
            if (TitleFontSizeInput != null) TitleFontSizeInput.Text = appearance.TitleFontSize.ToString(CultureInfo.CurrentCulture);
            if (ItemFontSizeInput != null) ItemFontSizeInput.Text = appearance.ItemFontSize.ToString(CultureInfo.CurrentCulture);
            OpacitySlider.Value = appearance.BackgroundOpacity;
            CornerRadiusSlider.Value = appearance.CornerRadius;
            ShadowOpacitySlider.Value = appearance.ShadowOpacity;
            ShadowBlurSlider.Value = appearance.ShadowBlur;
            if (BackgroundModeCombo != null)
            {
                foreach (ComboBoxItem item in BackgroundModeCombo.Items)
                {
                    if ((item.Tag as string)?.Equals(appearance.BackgroundMode, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        BackgroundModeCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            if (PatternCombo != null)
            {
                foreach (ComboBoxItem item in PatternCombo.Items)
                {
                    if ((item.Tag as string)?.Equals(appearance.Pattern, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        PatternCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            if (ImageOpacitySlider != null) ImageOpacitySlider.Value = appearance.BackgroundImageOpacity;
            if (GlassToggle != null) GlassToggle.IsChecked = appearance.GlassEnabled;
            if (ImageFitToggle != null) ImageFitToggle.IsChecked = appearance.ImageStretchFill;
            if (ImagePathInput != null) ImagePathInput.Text = appearance.BackgroundImagePath ?? "";
            UpdateBackgroundEditorVisibility(appearance.BackgroundMode);
        }

        private void AppearanceInputChanged(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady) return;

            UpdateBackgroundEditorVisibility();
            var appearance = BuildAppearanceFromUi();
            UpdatePreview(appearance);
            UpdateAppearance(appearance);
        }

        private void UpdateBackgroundEditorVisibility(string? selectedMode = null)
        {
            string mode = selectedMode
                ?? (BackgroundModeCombo?.SelectedItem as ComboBoxItem)?.Tag as string
                ?? "Solid";

            bool isImage = string.Equals(mode, "Image", StringComparison.OrdinalIgnoreCase);
            bool isPattern = string.Equals(mode, "Pattern", StringComparison.OrdinalIgnoreCase);

            if (PatternSection != null)
            {
                PatternSection.Visibility = isPattern ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ImageSection != null)
            {
                ImageSection.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ImageFitToggle != null)
            {
                ImageFitToggle.IsEnabled = isImage;
            }

            if (ImageOpacitySlider != null)
            {
                ImageOpacitySlider.IsEnabled = isImage;
            }
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var name = (PresetNameInput.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                System.Windows.MessageBox.Show(
                    GetString("Loc.MsgPresetNameRequired"),
                    GetString("Loc.MsgInfo"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var appearance = BuildAppearanceFromUi();
            var existing = Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.IsBuiltIn)
            {
                System.Windows.MessageBox.Show(
                    GetString("Loc.MsgPresetBuiltIn"),
                    GetString("Loc.MsgInfo"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (existing != null)
            {
                existing.Settings = appearance;
                existing.IsBuiltIn = false;
            }
            else
            {
                Presets.Add(new AppearancePreset { Name = name, Settings = appearance, IsBuiltIn = false });
            }

            RefreshPresetSelectors(name);
            SaveSettings();
        }

        private void GlobalPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suspendPresetSelection) return;
            if (!_isUiReady) return;
            if (PresetComboTop.SelectedItem is AppearancePreset preset)
            {
                PopulateAppearanceInputs(preset.Settings);
                UpdatePreview(preset.Settings);
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboTop.SelectedItem is AppearancePreset preset)
            {
                if (preset.IsBuiltIn)
                {
                    System.Windows.MessageBox.Show(
                        GetString("Loc.MsgPresetBuiltInDelete"),
                        GetString("Loc.MsgInfo"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                Presets.Remove(preset);
                RefreshPresetSelectors();
                SaveSettings();
            }
        }

        private void ResetStandardPresets_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                GetString("Loc.MsgResetConfirmMessage"),
                GetString("Loc.MsgResetConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            var defaults = GetDefaultPresets();
            var defaultNames = new HashSet<string>(defaults.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var custom = Presets.Where(p => !p.IsBuiltIn && !defaultNames.Contains(p.Name)).ToList();

            var merged = new List<AppearancePreset>();
            foreach (var preset in defaults)
            {
                merged.Add(new AppearancePreset
                {
                    Name = preset.Name,
                    Settings = CloneAppearance(preset.Settings),
                    IsBuiltIn = true
                });
            }

            foreach (var preset in custom)
            {
                merged.Add(new AppearancePreset
                {
                    Name = preset.Name,
                    Settings = CloneAppearance(preset.Settings),
                    IsBuiltIn = false
                });
            }

            Presets = merged;
            string selectedName = GetSelectedPresetName();
            _suspendPresetSelection = true;
            RefreshPresetSelectors(selectedName);
            _suspendPresetSelection = false;
            SaveSettings();
            System.Windows.MessageBox.Show(
                GetString("Loc.MsgPresetReset"),
                GetString("Loc.MsgInfo"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ApplyPresetAll_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboTop.SelectedItem is AppearancePreset preset)
            {
                UpdateAppearance(preset.Settings);

                foreach (var panel in System.Windows.Application.Current.Windows.OfType<DesktopPanel>())
                {
                    panel.assignedPresetName = preset.Name;
                    panel.ApplyAppearance(preset.Settings);
                }

                foreach (var w in savedWindows)
                {
                    w.PresetName = preset.Name;
                }

                SaveSettings();
                NotifyPanelsChanged();
            }
        }

        private void RefreshPresetSelectors(string? preferredName = null)
        {
            var ordered = Presets.OrderBy(p => p.IsBuiltIn ? 0 : 1).ThenBy(p => p.Name).ToList();
            if (PresetComboTop != null)
            {
                string? selectName = preferredName;
                if (string.IsNullOrWhiteSpace(selectName) && PresetComboTop.SelectedItem is AppearancePreset current)
                {
                    selectName = current.Name;
                }

                PresetComboTop.ItemsSource = ordered;
                var selected = !string.IsNullOrWhiteSpace(selectName)
                    ? ordered.FirstOrDefault(p => string.Equals(p.Name, selectName, StringComparison.OrdinalIgnoreCase))
                    : null;
                PresetComboTop.SelectedItem = selected
                    ?? ordered.FirstOrDefault(p => p.Name == DefaultPresetName)
                    ?? ordered.FirstOrDefault();
            }

            var layoutDefault = ordered.FirstOrDefault(p => string.Equals(p.Name, _layoutDefaultPresetName, StringComparison.OrdinalIgnoreCase))
                ?? ordered.FirstOrDefault(p => p.Name == DefaultPresetName)
                ?? ordered.FirstOrDefault();
            if (layoutDefault != null)
            {
                _layoutDefaultPresetName = layoutDefault.Name;
            }

            if (PanelOverviewList != null)
            {
                foreach (var itemContainer in PanelOverviewList.Items)
                {
                    if (PanelOverviewList.ItemContainerGenerator.ContainerFromItem(itemContainer) is FrameworkElement fe)
                    {
                        var combo = FindChild<System.Windows.Controls.ComboBox>(fe, "PanelPresetCombo");
                        if (combo != null)
                        {
                            combo.ItemsSource = ordered;
                        }
                    }
                }
            }

            if (_isUiReady)
            {
                RefreshLayoutList();
            }
        }

        private string SanitizeColor(string input, string fallback)
        {
            string value = (input ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            try
            {
                System.Windows.Media.ColorConverter.ConvertFromString(value);
                return value;
            }
            catch
            {
                return fallback;
            }
        }

        private double SanitizeDouble(string? input, double fallback, double min, double max)
        {
            if (string.IsNullOrWhiteSpace(input)) return fallback;

            if (!double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
                !double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return fallback;
            }

            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;

            return Math.Max(min, Math.Min(max, value));
        }

        private void UpdatePreview(AppearanceSettings appearance)
        {
            if (appearance == null) return;

            var headerBrush = BuildPanelHeaderBrush(appearance);
            var contentBrush = BuildPanelContentBrush(appearance);
            var borderBrush = BuildPanelBorderBrush(appearance);
            var headerSwatchBrush = BuildBrush(appearance.HeaderColor, 1.0, MediaColor.FromRgb(34, 37, 42));
            var accentBrush = BuildBrush(appearance.AccentColor, 1.0, MediaColor.FromRgb(90, 200, 250));
            var textBrush = BuildBrush(appearance.TextColor, 1.0, MediaColor.FromRgb(242, 245, 250));
            var mutedBrush = BuildBrush(appearance.MutedTextColor, 1.0, MediaColor.FromRgb(167, 176, 192));
            var insetBrush = BuildDarkerBrush(appearance.BackgroundColor, 0.15, MediaColor.FromRgb(30, 35, 43));
            string folderColor = string.IsNullOrWhiteSpace(appearance.FolderTextColor)
                ? appearance.AccentColor
                : appearance.FolderTextColor;
            var folderBrush = BuildBrush(folderColor, 1.0, MediaColor.FromRgb(110, 139, 255));
            double outerRadius = Math.Max(10, appearance.CornerRadius + 2);
            double innerRadius = Math.Max(6, outerRadius - 2);

            PreviewPanel.Background = System.Windows.Media.Brushes.Transparent;
            PreviewPanel.BorderBrush = borderBrush;
            PreviewPanel.BorderThickness = new Thickness(1);
            PreviewPanel.CornerRadius = new CornerRadius(outerRadius);
            PreviewHeader.Background = headerBrush;
            PreviewHeader.CornerRadius = new CornerRadius(innerRadius, innerRadius, 0, 0);
            if (PreviewContent != null)
            {
                PreviewContent.CornerRadius = new CornerRadius(0, 0, innerRadius, innerRadius);
                PreviewContent.Background = contentBrush;
            }
            PreviewTitleText.Foreground = accentBrush;
            if (PreviewSearchText != null)
            {
                PreviewSearchText.FontSize = Math.Max(10, appearance.ItemFontSize - 1);
            }

            if (PreviewPanel.Resources != null)
            {
                PreviewPanel.Resources["PreviewTextBrush"] = textBrush;
                PreviewPanel.Resources["PreviewMutedBrush"] = mutedBrush;
                PreviewPanel.Resources["PreviewFolderBrush"] = folderBrush;
                PreviewPanel.Resources["PreviewBorderBrush"] = borderBrush;
                PreviewPanel.Resources["PreviewInsetBrush"] = insetBrush;
                PreviewPanel.Resources["PreviewTitleFontSize"] = appearance.TitleFontSize;
                PreviewPanel.Resources["PreviewItemFontSize"] = appearance.ItemFontSize;
            }

            if (!string.IsNullOrWhiteSpace(appearance.FontFamily))
            {
                try
                {
                    PreviewPanel.SetValue(TextElement.FontFamilyProperty, new System.Windows.Media.FontFamily(appearance.FontFamily));
                }
                catch
                {
                    PreviewPanel.SetValue(TextElement.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe UI"));
                }
            }
            else
            {
                PreviewPanel.SetValue(TextElement.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe UI"));
            }

            if (PreviewShadow != null)
            {
                PreviewShadow.BlurRadius = Math.Max(0, appearance.ShadowBlur);
                PreviewShadow.Opacity = Math.Max(0, Math.Min(1, appearance.ShadowOpacity));
            }

            BackgroundSwatch.Background = BuildBrush(appearance.BackgroundColor, 1.0, MediaColor.FromRgb(30, 30, 30));
            HeaderSwatch.Background = headerSwatchBrush;
            AccentSwatch.Background = accentBrush;
            if (TextColorSwatch != null) TextColorSwatch.Background = textBrush;
            if (FolderColorSwatch != null) FolderColorSwatch.Background = folderBrush;
        }

        private AppearanceSettings BuildAppearanceFromUi()
        {
            var current = Appearance ?? new AppearanceSettings();
            string mode = (BackgroundModeCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? current.BackgroundMode ?? "Solid";
            string pattern = (PatternCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? current.Pattern ?? "None";
            double imageOpacity = ImageOpacitySlider != null ? Math.Round(ImageOpacitySlider.Value, 2) : current.BackgroundImageOpacity;
            bool glass = GlassToggle?.IsChecked == true;
            bool fit = ImageFitToggle?.IsChecked != false;
            string imagePath = (ImagePathInput?.Text ?? current.BackgroundImagePath ?? "").Trim();
            string fontFamily = (FontFamilyInput?.Text ?? current.FontFamily ?? "").Trim();
            double titleSize = SanitizeDouble(TitleFontSizeInput?.Text, current.TitleFontSize, 10, 28);
            double itemSize = SanitizeDouble(ItemFontSizeInput?.Text, current.ItemFontSize, 9, 24);
            string textColor = SanitizeColor(TextColorInput?.Text ?? "", current.TextColor);
            string folderColorFallback = string.IsNullOrWhiteSpace(current.FolderTextColor)
                ? current.AccentColor
                : current.FolderTextColor;
            string folderColor = SanitizeColor(FolderColorInput?.Text ?? "", folderColorFallback);

            return new AppearanceSettings
            {
                BackgroundColor = SanitizeColor(BackgroundColorInput.Text, current.BackgroundColor),
                HeaderColor = SanitizeColor(HeaderColorInput.Text, current.HeaderColor),
                AccentColor = SanitizeColor(AccentColorInput.Text, current.AccentColor),
                TextColor = textColor,
                MutedTextColor = current.MutedTextColor,
                FolderTextColor = folderColor,
                FontFamily = string.IsNullOrWhiteSpace(fontFamily) ? current.FontFamily ?? "Segoe UI" : fontFamily,
                TitleFontSize = Math.Round(titleSize, 0),
                ItemFontSize = Math.Round(itemSize, 0),
                BackgroundOpacity = Math.Round(OpacitySlider.Value, 2),
                CornerRadius = Math.Round(CornerRadiusSlider.Value, 0),
                ShadowOpacity = Math.Round(ShadowOpacitySlider.Value, 2),
                ShadowBlur = Math.Round(ShadowBlurSlider.Value, 1),
                BackgroundMode = mode,
                BackgroundImagePath = imagePath,
                BackgroundImageOpacity = imageOpacity,
                GlassEnabled = glass,
                ImageStretchFill = fit,
                Pattern = pattern
            };
        }

        private void PickImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new WinForms.OpenFileDialog
            {
                Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Alle Dateien|*.*"
            };
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                if (ImagePathInput != null)
                {
                    ImagePathInput.Text = dialog.FileName;
                }
                var appearance = BuildAppearanceFromUi();
                UpdatePreview(appearance);
                UpdateAppearance(appearance);
            }
        }

        public static System.Windows.Media.Brush BuildBackgroundBrush(AppearanceSettings appearance, bool allowGlass)
        {
            if (appearance == null) return new SolidColorBrush(MediaColor.FromRgb(30, 30, 30));

            var baseColorBrush = BuildBrush(appearance.BackgroundColor, appearance.BackgroundOpacity, MediaColor.FromRgb(30, 30, 30));

            if (string.Equals(appearance.BackgroundMode, "Image", StringComparison.OrdinalIgnoreCase) &&
                TryBuildImageBrush(appearance, baseColorBrush, out var imageBrush))
            {
                return imageBrush;
            }

            if (string.Equals(appearance.BackgroundMode, "Pattern", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(appearance.Pattern, "None", StringComparison.OrdinalIgnoreCase))
            {
                return BuildPatternBrush(appearance);
            }

            if (allowGlass && appearance.GlassEnabled)
            {
                var glass = baseColorBrush.Clone();
                glass.Opacity = Math.Max(0.1, Math.Min(1.0, appearance.BackgroundOpacity));
                return glass;
            }

            return baseColorBrush;
        }

        public static System.Windows.Media.Brush BuildPanelHeaderBrush(AppearanceSettings appearance)
        {
            if (appearance == null) return new SolidColorBrush(MediaColor.FromRgb(34, 37, 42));

            double baseOpacity = Math.Max(0.12, Math.Min(1.0, appearance.BackgroundOpacity));
            double headerOpacity = appearance.GlassEnabled
                ? Math.Max(0.58, Math.Min(0.9, baseOpacity + 0.1))
                : 1.0;

            return BuildBrush(appearance.HeaderColor, headerOpacity, MediaColor.FromRgb(34, 37, 42));
        }

        public static System.Windows.Media.Brush BuildPanelContentBrush(AppearanceSettings appearance)
        {
            if (appearance == null) return new SolidColorBrush(MediaColor.FromRgb(30, 35, 43));

            bool isSolid = string.Equals(appearance.BackgroundMode, "Solid", StringComparison.OrdinalIgnoreCase);
            if (!isSolid)
            {
                return BuildBackgroundBrush(appearance, true);
            }

            if (!appearance.GlassEnabled)
            {
                return BuildDarkerBrush(appearance.BackgroundColor, 0.15, MediaColor.FromRgb(30, 35, 43));
            }

            double baseOpacity = Math.Max(0.12, Math.Min(1.0, appearance.BackgroundOpacity));
            double contentOpacity = Math.Max(0.52, Math.Min(0.88, baseOpacity));
            return BuildBrush(appearance.BackgroundColor, contentOpacity, MediaColor.FromRgb(30, 35, 43));
        }

        public static SolidColorBrush BuildPanelBorderBrush(AppearanceSettings appearance)
        {
            if (appearance == null) return new SolidColorBrush(MediaColor.FromArgb(210, 52, 59, 72));

            var header = ParseColorOrFallback(appearance.HeaderColor, MediaColor.FromRgb(42, 48, 59));
            var accent = ParseColorOrFallback(appearance.AccentColor, MediaColor.FromRgb(110, 139, 255));
            var mixed = BlendColor(header, accent, 0.24);
            return new SolidColorBrush(MediaColor.FromArgb(210, mixed.R, mixed.G, mixed.B));
        }

        private static bool TryBuildImageBrush(AppearanceSettings appearance, SolidColorBrush baseColorBrush, out System.Windows.Media.Brush result)
        {
            result = baseColorBrush;
            if (!TryLoadBackgroundImage(appearance.BackgroundImagePath, out var source))
            {
                return false;
            }

            double imageOpacity = Math.Max(0.05, Math.Min(1.0, appearance.BackgroundImageOpacity));
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(baseColorBrush.Color),
                null,
                new RectangleGeometry(new Rect(0, 0, 1, 1))));

            var imageBrush = new ImageBrush(source)
            {
                Stretch = appearance.ImageStretchFill ? Stretch.UniformToFill : Stretch.Uniform,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
                Opacity = imageOpacity
            };

            group.Children.Add(new GeometryDrawing(
                imageBrush,
                null,
                new RectangleGeometry(new Rect(0, 0, 1, 1))));

            result = new DrawingBrush(group)
            {
                Stretch = Stretch.Fill
            };
            return true;
        }

        private static bool TryLoadBackgroundImage(string? rawPath, out BitmapImage source)
        {
            source = null!;
            string? resolvedPath = ResolveBackgroundImagePath(rawPath);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                source = bitmap;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? ResolveBackgroundImagePath(string? rawPath)
        {
            string path = Environment.ExpandEnvironmentVariables((rawPath ?? string.Empty).Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (path.StartsWith("~\\", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal))
            {
                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string trimmed = path.Substring(2).TrimStart('\\', '/');
                path = Path.Combine(userHome, trimmed);
            }

            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile)
                {
                    return null;
                }
                path = uri.LocalPath;
            }

            if (Path.IsPathRooted(path))
            {
                return File.Exists(path) ? Path.GetFullPath(path) : null;
            }

            try
            {
                string baseRelative = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
                if (File.Exists(baseRelative))
                {
                    return baseRelative;
                }
            }
            catch
            {
                // Ignore invalid base-relative path and continue.
            }

            try
            {
                string? settingsDirectory = Path.GetDirectoryName(settingsFilePath);
                if (!string.IsNullOrWhiteSpace(settingsDirectory))
                {
                    string settingsRelative = Path.GetFullPath(Path.Combine(settingsDirectory, path));
                    if (File.Exists(settingsRelative))
                    {
                        return settingsRelative;
                    }
                }
            }
            catch
            {
                // Ignore invalid settings-relative path and continue.
            }

            try
            {
                string currentRelative = Path.GetFullPath(path);
                if (File.Exists(currentRelative))
                {
                    return currentRelative;
                }
            }
            catch
            {
                // Ignore invalid relative path.
            }

            return null;
        }

        private static MediaColor ParseColorOrFallback(string? colorValue, MediaColor fallback)
        {
            if (string.IsNullOrWhiteSpace(colorValue)) return fallback;
            try
            {
                return (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(colorValue);
            }
            catch
            {
                return fallback;
            }
        }

        private static MediaColor BlendColor(MediaColor baseColor, MediaColor tintColor, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));

            static byte Mix(byte from, byte to, double mix)
            {
                return (byte)Math.Round((from * (1 - mix)) + (to * mix));
            }

            return MediaColor.FromRgb(
                Mix(baseColor.R, tintColor.R, amount),
                Mix(baseColor.G, tintColor.G, amount),
                Mix(baseColor.B, tintColor.B, amount));
        }

        private static System.Windows.Media.Brush BuildPatternBrush(AppearanceSettings appearance)
        {
            var baseColor = BuildBrush(appearance.BackgroundColor, appearance.BackgroundOpacity, MediaColor.FromRgb(30, 30, 30)).Color;
            var accent = BuildBrush(appearance.AccentColor, 0.25, MediaColor.FromRgb(90, 200, 250)).Color;

            DrawingGroup group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));

            switch (appearance.Pattern?.ToLowerInvariant())
            {
                case "diagonal":
                    group.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(new SolidColorBrush(accent), 1), new LineGeometry(new System.Windows.Point(0, 8), new System.Windows.Point(8, 0))));
                    group.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(new SolidColorBrush(accent), 1), new LineGeometry(new System.Windows.Point(-4, 8), new System.Windows.Point(4, 0))));
                    break;
                case "grid":
                    group.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(new SolidColorBrush(accent), 0.8), new RectangleGeometry(new Rect(0, 0, 8, 8))));
                    group.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(new SolidColorBrush(accent), 0.8), new RectangleGeometry(new Rect(0, 0, 4, 4))));
                    break;
                case "dots":
                    group.Children.Add(new GeometryDrawing(new SolidColorBrush(accent), null, new EllipseGeometry(new System.Windows.Point(2, 2), 1, 1)));
                    group.Children.Add(new GeometryDrawing(new SolidColorBrush(accent), null, new EllipseGeometry(new System.Windows.Point(6, 6), 1, 1)));
                    break;
                default:
                    break;
            }

            return new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 8, 8),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None
            };
        }

        public static SolidColorBrush BuildBrush(string value, double opacity, MediaColor fallback)
        {
            byte alpha = (byte)Math.Max(0, Math.Min(255, opacity * 255));

            try
            {
                var color = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value);
                return new SolidColorBrush(MediaColor.FromArgb(alpha, color.R, color.G, color.B));
            }
            catch
            {
                return new SolidColorBrush(MediaColor.FromArgb(alpha, fallback.R, fallback.G, fallback.B));
            }
        }

        private static SolidColorBrush BuildDarkerBrush(string colorValue, double darkenAmount, MediaColor fallback)
        {
            try
            {
                var parsed = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(colorValue);
                byte r = (byte)Math.Max(0, parsed.R * (1 - darkenAmount));
                byte g = (byte)Math.Max(0, parsed.G * (1 - darkenAmount));
                byte b = (byte)Math.Max(0, parsed.B * (1 - darkenAmount));
                return new SolidColorBrush(MediaColor.FromRgb(r, g, b));
            }
            catch
            {
                return new SolidColorBrush(fallback);
            }
        }
    }
}
