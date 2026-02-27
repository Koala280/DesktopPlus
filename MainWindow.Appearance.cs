using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private DesktopPanel? _appearancePreviewPanel;
        private FrameworkElement? _appearancePreviewRoot;
        private string? _appearancePreviewFolderPath;
        private const int PatternEditorResolution = 16;
        private readonly bool[,] _patternEditorMask = new bool[PatternEditorResolution, PatternEditorResolution];
        private readonly List<Border> _patternEditorCells = new List<Border>(PatternEditorResolution * PatternEditorResolution);
        private bool _patternEditorInitialized;
        private bool _patternEditorIsDragging;
        private bool _patternEditorDragValue;
        private bool _suppressPatternEditorApply;
        private readonly List<string> _fontFamilyChoices = new List<string>();
        private bool _fontFamilyChoicesInitialized;

        private void EnsureFontFamilyChoices()
        {
            if (_fontFamilyChoicesInitialized) return;
            if (FontFamilyCombo == null) return;

            _fontFamilyChoices.Clear();
            foreach (string familyName in Fonts.SystemFontFamilies
                .Select(family => family.Source)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            {
                _fontFamilyChoices.Add(familyName);
            }

            if (!_fontFamilyChoices.Any(name => string.Equals(name, "Segoe UI", StringComparison.OrdinalIgnoreCase)))
            {
                _fontFamilyChoices.Insert(0, "Segoe UI");
            }

            FontFamilyCombo.ItemsSource = _fontFamilyChoices;
            _fontFamilyChoicesInitialized = true;
        }

        private string ResolveFontFamilySelection(string? requestedFontFamily)
        {
            EnsureFontFamilyChoices();

            string resolved = string.IsNullOrWhiteSpace(requestedFontFamily)
                ? "Segoe UI"
                : requestedFontFamily.Trim();

            string? existing = _fontFamilyChoices.FirstOrDefault(name =>
                string.Equals(name, resolved, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                _fontFamilyChoices.Add(resolved);
                existing = resolved;
            }

            return existing;
        }

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
            if (FontFamilyCombo != null)
            {
                string selectedFontFamily = ResolveFontFamilySelection(appearance.FontFamily);
                FontFamilyCombo.SelectedItem = selectedFontFamily;
            }
            if (TitleFontSizeInput != null) TitleFontSizeInput.Text = appearance.TitleFontSize.ToString(CultureInfo.CurrentCulture);
            if (ItemFontSizeInput != null) ItemFontSizeInput.Text = appearance.ItemFontSize.ToString(CultureInfo.CurrentCulture);
            OpacitySlider.Value = appearance.BackgroundOpacity;
            CornerRadiusSlider.Value = appearance.CornerRadius;
            if (HeaderShadowOpacitySlider != null) HeaderShadowOpacitySlider.Value = ResolveHeaderShadowOpacity(appearance);
            if (HeaderShadowBlurSlider != null) HeaderShadowBlurSlider.Value = ResolveHeaderShadowBlur(appearance);
            if (BodyShadowOpacitySlider != null) BodyShadowOpacitySlider.Value = ResolveBodyShadowOpacity(appearance);
            if (BodyShadowBlurSlider != null) BodyShadowBlurSlider.Value = ResolveBodyShadowBlur(appearance);
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
            _suppressPatternEditorApply = true;
            try
            {
                string patternColor = string.IsNullOrWhiteSpace(appearance.PatternColor)
                    ? appearance.AccentColor
                    : appearance.PatternColor;
                if (PatternColorInput != null) PatternColorInput.Text = patternColor;
                if (PatternColorSwatch != null)
                {
                    PatternColorSwatch.Background = BuildBrush(patternColor, 1.0, MediaColor.FromRgb(110, 139, 255));
                }
                if (PatternOpacitySlider != null)
                {
                    PatternOpacitySlider.Value = Math.Max(0.05, Math.Min(1.0, appearance.PatternOpacity > 0 ? appearance.PatternOpacity : 0.25));
                }
                if (PatternTileSizeSlider != null)
                {
                    PatternTileSizeSlider.Value = Math.Max(6, Math.Min(64, appearance.PatternTileSize > 0 ? appearance.PatternTileSize : 8));
                }
                if (PatternStrokeSlider != null)
                {
                    PatternStrokeSlider.Value = Math.Max(0.5, Math.Min(8, appearance.PatternStrokeThickness > 0 ? appearance.PatternStrokeThickness : 1));
                }
                InitializePatternEditorGrid();
                LoadPatternEditorMask(appearance.PatternCustomData);
                RefreshPatternEditorVisuals();
            }
            finally
            {
                _suppressPatternEditorApply = false;
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
            if (_suppressPatternEditorApply) return;

            UpdateBackgroundEditorVisibility();
            RefreshPatternEditorVisuals();
            if (PatternColorSwatch != null)
            {
                string swatchColor = (PatternColorInput?.Text ?? AccentColorInput?.Text ?? "#6E8BFF").Trim();
                PatternColorSwatch.Background = BuildBrush(swatchColor, 1.0, MediaColor.FromRgb(110, 139, 255));
            }
            var appearance = BuildAppearanceFromUi();
            UpdatePreview(appearance);
            UpdateAppearance(appearance);
        }

        private System.Windows.Controls.TextBox? _activeColorTextBox;

        private void Swatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.Border swatch) return;

            System.Windows.Controls.TextBox? targetInput = null;
            if (swatch == BackgroundSwatch) targetInput = BackgroundColorInput;
            else if (swatch == HeaderSwatch) targetInput = HeaderColorInput;
            else if (swatch == AccentSwatch) targetInput = AccentColorInput;
            else if (swatch == TextColorSwatch) targetInput = TextColorInput;
            else if (swatch == FolderColorSwatch) targetInput = FolderColorInput;
            else if (swatch == PatternColorSwatch) targetInput = PatternColorInput;

            if (targetInput == null) return;
            _activeColorTextBox = targetInput;

            // Parse current color
            MediaColor currentColor;
            try
            {
                currentColor = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(targetInput.Text.Trim());
            }
            catch
            {
                currentColor = MediaColor.FromRgb(110, 139, 255);
            }

            ColorPicker.ColorChanged -= OnColorPickerChanged;
            ColorPicker.SetColor(currentColor);
            ColorPicker.ColorChanged += OnColorPickerChanged;

            ColorPickerPopup.PlacementTarget = swatch;
            ColorPickerPopup.IsOpen = true;
        }

        private void OnColorPickerChanged(MediaColor color)
        {
            if (_activeColorTextBox == null) return;
            _activeColorTextBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private void UpdateBackgroundEditorVisibility(string? selectedMode = null)
        {
            string mode = selectedMode
                ?? (BackgroundModeCombo?.SelectedItem as ComboBoxItem)?.Tag as string
                ?? "Solid";

            bool isImage = string.Equals(mode, "Image", StringComparison.OrdinalIgnoreCase);
            bool isPattern = string.Equals(mode, "Pattern", StringComparison.OrdinalIgnoreCase);
            string selectedPattern = (PatternCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "None";
            bool isCustomPattern = isPattern && string.Equals(selectedPattern, "Custom", StringComparison.OrdinalIgnoreCase);

            if (PatternSection != null)
            {
                PatternSection.Visibility = isPattern ? Visibility.Visible : Visibility.Collapsed;
            }
            if (PatternEditorSection != null)
            {
                PatternEditorSection.Visibility = isCustomPattern ? Visibility.Visible : Visibility.Collapsed;
            }
            if (isCustomPattern)
            {
                InitializePatternEditorGrid();
                RefreshPatternEditorVisuals();
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

        private void InitializePatternEditorGrid()
        {
            if (_patternEditorInitialized || PatternEditorGrid == null) return;

            PatternEditorGrid.Children.Clear();
            _patternEditorCells.Clear();

            for (int y = 0; y < PatternEditorResolution; y++)
            {
                for (int x = 0; x < PatternEditorResolution; x++)
                {
                    int index = (y * PatternEditorResolution) + x;
                    var cell = new Border
                    {
                        Tag = index,
                        Margin = new Thickness(0.5),
                        CornerRadius = new CornerRadius(1),
                        BorderThickness = new Thickness(0.5)
                    };
                    cell.MouseLeftButtonDown += PatternCell_MouseLeftButtonDown;
                    cell.MouseRightButtonDown += PatternCell_MouseRightButtonDown;
                    cell.MouseEnter += PatternCell_MouseEnter;

                    _patternEditorCells.Add(cell);
                    PatternEditorGrid.Children.Add(cell);
                }
            }

            PatternEditorGrid.MouseLeftButtonUp += PatternEditorGrid_MouseLeftButtonUp;
            PatternEditorGrid.MouseRightButtonUp += PatternEditorGrid_MouseRightButtonUp;
            PatternEditorGrid.MouseLeave += PatternEditorGrid_MouseLeave;
            _patternEditorInitialized = true;
        }

        private void PatternCell_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Border cell) return;
            _patternEditorIsDragging = true;
            _patternEditorDragValue = true;
            PatternEditorGrid?.CaptureMouse();
            ApplyPatternCellFromBorder(cell, _patternEditorDragValue);
            e.Handled = true;
        }

        private void PatternCell_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Border cell) return;
            _patternEditorIsDragging = true;
            _patternEditorDragValue = false;
            PatternEditorGrid?.CaptureMouse();
            ApplyPatternCellFromBorder(cell, _patternEditorDragValue);
            e.Handled = true;
        }

        private void PatternCell_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_patternEditorIsDragging) return;
            if (sender is not Border cell) return;
            ApplyPatternCellFromBorder(cell, _patternEditorDragValue);
        }

        private void PatternEditorGrid_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            StopPatternEditorDrag();
        }

        private void PatternEditorGrid_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            StopPatternEditorDrag();
        }

        private void PatternEditorGrid_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Released &&
                e.RightButton == System.Windows.Input.MouseButtonState.Released)
            {
                StopPatternEditorDrag();
            }
        }

        private void StopPatternEditorDrag()
        {
            if (!_patternEditorIsDragging) return;
            _patternEditorIsDragging = false;
            if (PatternEditorGrid?.IsMouseCaptured == true)
            {
                PatternEditorGrid.ReleaseMouseCapture();
            }
        }

        private static bool TryGetPatternCellCoordinates(Border cell, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (cell.Tag is not int index) return false;
            if (index < 0 || index >= PatternEditorResolution * PatternEditorResolution) return false;
            y = index / PatternEditorResolution;
            x = index % PatternEditorResolution;
            return true;
        }

        private void ApplyPatternCellFromBorder(Border cell, bool value)
        {
            if (!TryGetPatternCellCoordinates(cell, out int x, out int y)) return;
            if (_patternEditorMask[x, y] == value) return;

            _patternEditorMask[x, y] = value;
            RefreshPatternEditorVisuals();
            ApplyPatternEditorToAppearance();
        }

        private void LoadPatternEditorMask(string? serializedData)
        {
            for (int y = 0; y < PatternEditorResolution; y++)
            {
                for (int x = 0; x < PatternEditorResolution; x++)
                {
                    _patternEditorMask[x, y] = false;
                }
            }

            if (string.IsNullOrWhiteSpace(serializedData))
            {
                return;
            }

            int expectedLength = PatternEditorResolution * PatternEditorResolution;
            int max = Math.Min(expectedLength, serializedData.Length);
            for (int i = 0; i < max; i++)
            {
                char ch = serializedData[i];
                if (ch == '1' || ch == 'x' || ch == 'X' || ch == '#')
                {
                    int y = i / PatternEditorResolution;
                    int x = i % PatternEditorResolution;
                    _patternEditorMask[x, y] = true;
                }
            }
        }

        private string SerializePatternEditorMask()
        {
            var sb = new StringBuilder(PatternEditorResolution * PatternEditorResolution);
            for (int y = 0; y < PatternEditorResolution; y++)
            {
                for (int x = 0; x < PatternEditorResolution; x++)
                {
                    sb.Append(_patternEditorMask[x, y] ? '1' : '0');
                }
            }
            return sb.ToString();
        }

        private void RefreshPatternEditorVisuals()
        {
            if (!_patternEditorInitialized) return;

            string colorValue = (PatternColorInput?.Text ?? AccentColorInput?.Text ?? "#6E8BFF").Trim();
            double fillOpacity = PatternOpacitySlider?.Value ?? 0.25;
            fillOpacity = Math.Max(0.05, Math.Min(1.0, fillOpacity));
            var onBrush = BuildBrush(colorValue, fillOpacity, MediaColor.FromRgb(110, 139, 255));
            var offBrush = new SolidColorBrush(MediaColor.FromArgb(35, 58, 70, 89));
            var offBorder = new SolidColorBrush(MediaColor.FromArgb(90, 72, 84, 102));
            var onBorder = new SolidColorBrush(MediaColor.FromArgb(200, onBrush.Color.R, onBrush.Color.G, onBrush.Color.B));

            for (int i = 0; i < _patternEditorCells.Count; i++)
            {
                int y = i / PatternEditorResolution;
                int x = i % PatternEditorResolution;
                bool isOn = _patternEditorMask[x, y];
                var cell = _patternEditorCells[i];
                cell.Background = isOn ? onBrush : offBrush;
                cell.BorderBrush = isOn ? onBorder : offBorder;
            }
        }

        private void ApplyPatternEditorToAppearance()
        {
            if (!_isUiReady || _suppressPatternEditorApply) return;

            var appearance = BuildAppearanceFromUi();
            UpdatePreview(appearance);
            UpdateAppearance(appearance);
        }

        private void PatternClear_Click(object sender, RoutedEventArgs e)
        {
            for (int y = 0; y < PatternEditorResolution; y++)
            {
                for (int x = 0; x < PatternEditorResolution; x++)
                {
                    _patternEditorMask[x, y] = false;
                }
            }
            RefreshPatternEditorVisuals();
            ApplyPatternEditorToAppearance();
        }

        private void CreatePreset_Click(object sender, RoutedEventArgs e)
        {
            string defaultName = GetString("Loc.ThemesPresetDefaultName");
            string? enteredName = PromptName(GetString("Loc.PromptPresetName"), defaultName);
            if (string.IsNullOrWhiteSpace(enteredName))
            {
                return;
            }

            string name = enteredName.Trim();
            var appearance = BuildAppearanceFromUi();
            var existing = Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                System.Windows.MessageBox.Show(
                    GetString(existing.IsBuiltIn ? "Loc.MsgPresetBuiltIn" : "Loc.MsgPresetNameExists"),
                    GetString("Loc.MsgInfo"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Presets.Add(new AppearancePreset { Name = name, Settings = CloneAppearance(appearance), IsBuiltIn = false });

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

        private void EnsurePreviewPanel()
        {
            if (PreviewPanelHost == null) return;

            if (_appearancePreviewPanel == null || _appearancePreviewRoot == null)
            {
                var previewPanel = new DesktopPanel();
                previewPanel.IsPreviewPanel = true;
                previewPanel.PanelId = "preview:appearance";
                previewPanel.expandOnHover = false;
                previewPanel.SetExpandOnHover(false);
                previewPanel.ApplyMovementMode("locked");

                if (previewPanel.Content is not FrameworkElement previewRoot)
                {
                    return;
                }

                previewPanel.Content = null;
                previewRoot.Width = 820;
                previewRoot.Height = 480;
                previewRoot.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                previewRoot.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
                previewRoot.Margin = new Thickness(0);
                previewRoot.IsHitTestVisible = false;

                _appearancePreviewPanel = previewPanel;
                _appearancePreviewRoot = previewRoot;
            }

            if (_appearancePreviewRoot != null && !PreviewPanelHost.Children.Contains(_appearancePreviewRoot))
            {
                PreviewPanelHost.Children.Clear();
                PreviewPanelHost.Children.Add(_appearancePreviewRoot);
            }

            if (_appearancePreviewPanel == null) return;

            if (string.IsNullOrWhiteSpace(_appearancePreviewFolderPath) || !Directory.Exists(_appearancePreviewFolderPath))
            {
                _appearancePreviewFolderPath = EnsurePreviewSampleFolder();
            }

            if (!string.Equals(_appearancePreviewPanel.currentFolderPath, _appearancePreviewFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                _appearancePreviewPanel.LoadFolder(_appearancePreviewFolderPath, saveSettings: false);
            }
        }

        private string EnsurePreviewSampleFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "DesktopPlus", "PreviewPanelSample");
            Directory.CreateDirectory(root);

            string docsFolder = Path.Combine(root, SanitizePreviewEntryName(GetString("Loc.PreviewFolderDocs"), "Documents"));
            string photosFolder = Path.Combine(root, SanitizePreviewEntryName(GetString("Loc.PreviewFolderPhotos"), "Pictures"));
            string projectsFolder = Path.Combine(root, SanitizePreviewEntryName(GetString("Loc.PreviewFolderProjects"), "Projects"));

            Directory.CreateDirectory(docsFolder);
            Directory.CreateDirectory(photosFolder);
            Directory.CreateDirectory(projectsFolder);

            EnsurePreviewFile(
                Path.Combine(root, SanitizePreviewEntryName(GetString("Loc.PreviewFileReadme"), "readme.md")),
                "# DesktopPlus Preview" + Environment.NewLine + Environment.NewLine + "Sample content for panel preview.");
            EnsurePreviewFile(
                Path.Combine(root, SanitizePreviewEntryName(GetString("Loc.PreviewFileTodo"), "todo.txt")),
                "- Sync panel colors" + Environment.NewLine + "- Check spacing" + Environment.NewLine + "- Validate contrast");
            EnsurePreviewFile(
                Path.Combine(root, SanitizePreviewEntryName(GetString("Loc.PreviewFileBudget"), "Budget.xlsx")),
                "Sample preview file placeholder");

            return root;
        }

        private static string SanitizePreviewEntryName(string value, string fallback)
        {
            string entry = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                entry = entry.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(entry) ? fallback : entry;
        }

        private static void EnsurePreviewFile(string path, string content)
        {
            if (File.Exists(path)) return;
            File.WriteAllText(path, content);
        }

        private void UpdatePreview(AppearanceSettings appearance)
        {
            if (appearance == null) return;

            EnsurePreviewPanel();
            if (_appearancePreviewPanel != null)
            {
                _appearancePreviewPanel.ApplyAppearance(appearance);
            }

            var headerSwatchBrush = BuildBrush(appearance.HeaderColor, 1.0, MediaColor.FromRgb(34, 37, 42));
            var accentBrush = BuildBrush(appearance.AccentColor, 1.0, MediaColor.FromRgb(90, 200, 250));
            var textBrush = BuildBrush(appearance.TextColor, 1.0, MediaColor.FromRgb(242, 245, 250));
            string folderColor = string.IsNullOrWhiteSpace(appearance.FolderTextColor)
                ? appearance.AccentColor
                : appearance.FolderTextColor;
            var folderBrush = BuildBrush(folderColor, 1.0, MediaColor.FromRgb(110, 139, 255));

            BackgroundSwatch.Background = BuildBrush(appearance.BackgroundColor, 1.0, MediaColor.FromRgb(30, 30, 30));
            HeaderSwatch.Background = headerSwatchBrush;
            AccentSwatch.Background = accentBrush;
            if (TextColorSwatch != null) TextColorSwatch.Background = textBrush;
            if (FolderColorSwatch != null) FolderColorSwatch.Background = folderBrush;
            if (PatternColorSwatch != null)
            {
                string previewPatternColor = string.IsNullOrWhiteSpace(appearance.PatternColor) ? appearance.AccentColor : appearance.PatternColor;
                PatternColorSwatch.Background = BuildBrush(previewPatternColor, 1.0, MediaColor.FromRgb(110, 139, 255));
            }
        }

        public static double ResolveHeaderShadowOpacity(AppearanceSettings appearance)
        {
            if (appearance == null) return 0.3;
            if (appearance.HeaderShadowOpacity >= 0) return Math.Max(0, Math.Min(0.8, appearance.HeaderShadowOpacity));
            return Math.Max(0, Math.Min(0.8, appearance.ShadowOpacity));
        }

        public static double ResolveHeaderShadowBlur(AppearanceSettings appearance)
        {
            if (appearance == null) return 20;
            if (appearance.HeaderShadowBlur >= 0) return Math.Max(0, appearance.HeaderShadowBlur);
            return Math.Max(0, appearance.ShadowBlur);
        }

        public static double ResolveBodyShadowOpacity(AppearanceSettings appearance)
        {
            if (appearance == null) return 0.3;
            if (appearance.BodyShadowOpacity >= 0) return Math.Max(0, Math.Min(0.8, appearance.BodyShadowOpacity));
            return Math.Max(0, Math.Min(0.8, appearance.ShadowOpacity));
        }

        public static double ResolveBodyShadowBlur(AppearanceSettings appearance)
        {
            if (appearance == null) return 20;
            if (appearance.BodyShadowBlur >= 0) return Math.Max(0, appearance.BodyShadowBlur);
            return Math.Max(0, appearance.ShadowBlur);
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
            string fontFamily = ((FontFamilyCombo?.SelectedItem as string) ?? FontFamilyCombo?.Text ?? current.FontFamily ?? "").Trim();
            double titleSize = SanitizeDouble(TitleFontSizeInput?.Text, current.TitleFontSize, 10, 28);
            double itemSize = SanitizeDouble(ItemFontSizeInput?.Text, current.ItemFontSize, 9, 24);
            string textColor = SanitizeColor(TextColorInput?.Text ?? "", current.TextColor);
            string patternColorFallback = !string.IsNullOrWhiteSpace(current.PatternColor)
                ? current.PatternColor
                : (string.IsNullOrWhiteSpace(current.AccentColor) ? "#6E8BFF" : current.AccentColor);
            string patternColor = SanitizeColor(PatternColorInput?.Text ?? "", patternColorFallback);
            double patternOpacity = PatternOpacitySlider != null
                ? Math.Round(PatternOpacitySlider.Value, 2)
                : (current.PatternOpacity > 0 ? current.PatternOpacity : 0.25);
            double patternTileSize = PatternTileSizeSlider != null
                ? Math.Round(PatternTileSizeSlider.Value, 1)
                : (current.PatternTileSize > 0 ? current.PatternTileSize : 8);
            double patternStroke = PatternStrokeSlider != null
                ? Math.Round(PatternStrokeSlider.Value, 1)
                : (current.PatternStrokeThickness > 0 ? current.PatternStrokeThickness : 1);
            string folderColorFallback = string.IsNullOrWhiteSpace(current.FolderTextColor)
                ? current.AccentColor
                : current.FolderTextColor;
            string folderColor = SanitizeColor(FolderColorInput?.Text ?? "", folderColorFallback);
            double headerShadowOpacity = HeaderShadowOpacitySlider != null ? HeaderShadowOpacitySlider.Value : ResolveHeaderShadowOpacity(current);
            double headerShadowBlur = HeaderShadowBlurSlider != null ? HeaderShadowBlurSlider.Value : ResolveHeaderShadowBlur(current);
            double bodyShadowOpacity = BodyShadowOpacitySlider != null ? BodyShadowOpacitySlider.Value : ResolveBodyShadowOpacity(current);
            double bodyShadowBlur = BodyShadowBlurSlider != null ? BodyShadowBlurSlider.Value : ResolveBodyShadowBlur(current);

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
                ShadowOpacity = Math.Round(bodyShadowOpacity, 2),
                ShadowBlur = Math.Round(bodyShadowBlur, 1),
                HeaderShadowOpacity = Math.Round(headerShadowOpacity, 2),
                HeaderShadowBlur = Math.Round(headerShadowBlur, 1),
                BodyShadowOpacity = Math.Round(bodyShadowOpacity, 2),
                BodyShadowBlur = Math.Round(bodyShadowBlur, 1),
                PatternColor = patternColor,
                PatternOpacity = Math.Max(0.05, Math.Min(1.0, patternOpacity)),
                PatternTileSize = Math.Max(6, Math.Min(64, patternTileSize)),
                PatternStrokeThickness = Math.Max(0.5, Math.Min(8, patternStroke)),
                PatternCustomData = SerializePatternEditorMask(),
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
            string patternColorValue = string.IsNullOrWhiteSpace(appearance.PatternColor)
                ? appearance.AccentColor
                : appearance.PatternColor;
            double patternOpacity = appearance.PatternOpacity > 0 ? appearance.PatternOpacity : 0.25;
            patternOpacity = Math.Max(0.05, Math.Min(1.0, patternOpacity));
            var accent = BuildBrush(patternColorValue, patternOpacity, MediaColor.FromRgb(90, 200, 250)).Color;
            double tileSize = appearance.PatternTileSize > 0 ? appearance.PatternTileSize : 8;
            tileSize = Math.Max(6, Math.Min(64, tileSize));
            double stroke = appearance.PatternStrokeThickness > 0 ? appearance.PatternStrokeThickness : 1;
            stroke = Math.Max(0.5, Math.Min(8, stroke));

            DrawingGroup group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, tileSize, tileSize))));
            var pen = new System.Windows.Media.Pen(new SolidColorBrush(accent), stroke);

            switch (appearance.Pattern?.ToLowerInvariant())
            {
                case "diagonal":
                    group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new System.Windows.Point(0, tileSize), new System.Windows.Point(tileSize, 0))));
                    group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new System.Windows.Point(-tileSize * 0.5, tileSize), new System.Windows.Point(tileSize * 0.5, 0))));
                    group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new System.Windows.Point(tileSize * 0.5, tileSize), new System.Windows.Point(tileSize * 1.5, 0))));
                    break;
                case "grid":
                    double half = tileSize * 0.5;
                    group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(0, 0, tileSize, tileSize))));
                    group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new System.Windows.Point(half, 0), new System.Windows.Point(half, tileSize))));
                    group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new System.Windows.Point(0, half), new System.Windows.Point(tileSize, half))));
                    break;
                case "dots":
                    double radius = Math.Max(0.8, stroke * 0.9);
                    group.Children.Add(new GeometryDrawing(new SolidColorBrush(accent), null, new EllipseGeometry(new System.Windows.Point(tileSize * 0.25, tileSize * 0.25), radius, radius)));
                    group.Children.Add(new GeometryDrawing(new SolidColorBrush(accent), null, new EllipseGeometry(new System.Windows.Point(tileSize * 0.75, tileSize * 0.75), radius, radius)));
                    break;
                case "custom":
                    string data = appearance.PatternCustomData ?? string.Empty;
                    int expected = PatternEditorResolution * PatternEditorResolution;
                    if (data.Length >= expected)
                    {
                        double pixel = tileSize / PatternEditorResolution;
                        var fill = new SolidColorBrush(accent);
                        for (int i = 0; i < expected; i++)
                        {
                            char ch = data[i];
                            if (ch != '1' && ch != 'x' && ch != 'X' && ch != '#') continue;

                            int y = i / PatternEditorResolution;
                            int x = i % PatternEditorResolution;
                            var rect = new Rect(x * pixel, y * pixel, pixel, pixel);
                            group.Children.Add(new GeometryDrawing(fill, null, new RectangleGeometry(rect)));
                        }
                    }
                    break;
                default:
                    break;
            }

            return new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, tileSize, tileSize),
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
