using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinForms = System.Windows.Forms;

namespace DesktopPlus
{
    public partial class PanelSettings : Window
    {
        private readonly DesktopPanel? _panel;
        private readonly LayoutDefinition? _layout;
        private string _layoutStandardPresetName = "";
        private bool _isInitializing = true;
        private bool _isApplyingSettings;
        private System.Windows.Point _metadataDragStartPoint;
        private Border? _metadataDragSourceItem;
        private bool _metadataDragging;
        private int _metadataDragOriginalIndex;
        private int _metadataCurrentIndex;
        private double _metadataDragItemStartY;
        private bool IsGlobalLayoutMode => _layout != null;

        public PanelSettings(DesktopPanel panel)
        {
            InitializeComponent();
            TrySetWindowIcon();
            _panel = panel;
            LoadPresets();
            PopulatePanelSettings();
            UpdateResetPresetToStandardButtonState();
            RegisterAutoApplyHandlers();
            _isInitializing = false;
        }

        public PanelSettings(LayoutDefinition layout)
        {
            InitializeComponent();
            TrySetWindowIcon();
            _layout = layout;
            LoadPresetsForLayout(layout);
            ConfigureGlobalLayoutMode();
            PopulateGlobalPanelDefaults();
            RegisterAutoApplyHandlers();
            _isInitializing = false;
        }

        private void RegisterAutoApplyHandlers()
        {
            NameInput.TextChanged += (_, __) => TryAutoApplySettings();

            HoverToggle.Checked += (_, __) => TryAutoApplySettings();
            HoverToggle.Unchecked += (_, __) => TryAutoApplySettings();
            HiddenToggle.Checked += (_, __) => TryAutoApplySettings();
            HiddenToggle.Unchecked += (_, __) => TryAutoApplySettings();
            ParentNavigationToggle.Checked += (_, __) => TryAutoApplySettings();
            ParentNavigationToggle.Unchecked += (_, __) => TryAutoApplySettings();
            FileExtensionsToggle.Checked += (_, __) => TryAutoApplySettings();
            FileExtensionsToggle.Unchecked += (_, __) => TryAutoApplySettings();
            SettingsButtonToggle.Checked += (_, __) => TryAutoApplySettings();
            SettingsButtonToggle.Unchecked += (_, __) => TryAutoApplySettings();
            MetaTypeToggle.Checked += (_, __) => TryAutoApplySettings();
            MetaTypeToggle.Unchecked += (_, __) => TryAutoApplySettings();
            MetaSizeToggle.Checked += (_, __) => TryAutoApplySettings();
            MetaSizeToggle.Unchecked += (_, __) => TryAutoApplySettings();
            MetaCreatedToggle.Checked += (_, __) => TryAutoApplySettings();
            MetaCreatedToggle.Unchecked += (_, __) => TryAutoApplySettings();
            MetaModifiedToggle.Checked += (_, __) => TryAutoApplySettings();
            MetaModifiedToggle.Unchecked += (_, __) => TryAutoApplySettings();
            MetaDimensionsToggle.Checked += (_, __) => TryAutoApplySettings();
            MetaDimensionsToggle.Unchecked += (_, __) => TryAutoApplySettings();

            FolderActionSelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            MovementModeSelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            SearchVisibilitySelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            ViewModeSelect.SelectionChanged += (_, __) =>
            {
                UpdateMetadataOptionsVisibility();
                TryAutoApplySettings();
            };
        }

        private void TryAutoApplySettings()
        {
            if (_isInitializing || _isApplyingSettings)
            {
                return;
            }

            _isApplyingSettings = true;
            try
            {
                ApplyCurrentSettings(closeAfterApply: false);
            }
            finally
            {
                _isApplyingSettings = false;
            }
        }

        private void TrySetWindowIcon()
        {
            AppIconLoader.TryApplyWindowIcon(this);
        }

        private void SetNameInputWithoutAutoApply(string? text)
        {
            bool previousApplying = _isApplyingSettings;
            _isApplyingSettings = true;
            try
            {
                NameInput.Text = text ?? string.Empty;
            }
            finally
            {
                _isApplyingSettings = previousApplying;
            }
        }

        private void ConfigureGlobalLayoutMode()
        {
            Title = MainWindow.GetString("Loc.LayoutsGlobalPanelSettings");
            SettingsHeading.Text = MainWindow.GetString("Loc.LayoutsGlobalPanelSettings");
            SettingsModeHint.Text = MainWindow.GetString("Loc.PanelSettingsGlobalHint");
            SettingsModeHint.Visibility = Visibility.Visible;

            NameLabel.Visibility = Visibility.Collapsed;
            NameInput.Visibility = Visibility.Collapsed;
            PresetLabel.Text = MainWindow.GetString("Loc.LayoutsDefaultPreset");
            FolderSection.Visibility = Visibility.Collapsed;
            ViewSettingsSection.Visibility = Visibility.Collapsed;
            PanelActionsSection.Visibility = Visibility.Collapsed;
            ParentNavigationToggle.Visibility = Visibility.Collapsed;
            ResetPresetToStandardButton.Visibility = Visibility.Collapsed;
            FitContentButton.Visibility = Visibility.Collapsed;
            Height = 430;
        }

        private void LoadPresetsForLayout(LayoutDefinition layout)
        {
            PresetSelect.ItemsSource = MainWindow.Presets;
            string selectedPresetName = !string.IsNullOrWhiteSpace(layout.DefaultPanelPresetName)
                ? layout.DefaultPanelPresetName
                : MainWindow.GetCurrentStandardPresetName();

            if (string.IsNullOrWhiteSpace(selectedPresetName) ||
                !MainWindow.Presets.Any(p => string.Equals(p.Name, selectedPresetName, StringComparison.OrdinalIgnoreCase)))
            {
                selectedPresetName = MainWindow.Presets.FirstOrDefault()?.Name ?? "Graphite";
            }

            PresetSelect.SelectedValue = selectedPresetName;
        }

        private void LoadPresets()
        {
            if (_panel == null)
            {
                return;
            }

            PresetSelect.ItemsSource = MainWindow.Presets;
            _layoutStandardPresetName = MainWindow.GetCurrentStandardPresetName();

            string selectedPresetName = string.IsNullOrWhiteSpace(_panel.assignedPresetName)
                ? _layoutStandardPresetName
                : _panel.assignedPresetName;
            if (string.IsNullOrWhiteSpace(selectedPresetName) ||
                !MainWindow.Presets.Any(p => string.Equals(p.Name, selectedPresetName, StringComparison.OrdinalIgnoreCase)))
            {
                selectedPresetName = MainWindow.Presets.FirstOrDefault()?.Name ?? "Graphite";
            }

            PresetSelect.SelectedValue = selectedPresetName;
        }

        private void PopulatePanelSettings()
        {
            if (_panel == null)
            {
                return;
            }

            NameInput.Text = (_panel.Tabs.Count > 1 && _panel.ActiveTab != null)
                ? _panel.ActiveTab.TabName
                : _panel.PanelTitle.Text;
            FolderPathLabel.Text = string.IsNullOrWhiteSpace(_panel.defaultFolderPath)
                ? MainWindow.GetString("Loc.PanelSettingsFolderUnset")
                : _panel.defaultFolderPath;
            HoverToggle.IsChecked = _panel.expandOnHover;
            HiddenToggle.IsChecked = _panel.showHiddenItems;
            ParentNavigationToggle.IsChecked = _panel.showParentNavigationItem;
            FileExtensionsToggle.IsChecked = _panel.showFileExtensions;
            SettingsButtonToggle.IsChecked = _panel.showSettingsButton;
            FolderActionSelect.SelectedIndex = _panel.openFoldersExternally ? 1 : 0;
            SetMovementModeSelection(_panel.movementMode);
            SetSearchVisibilitySelection(_panel.searchVisibilityMode);
            SetViewModeSelection(_panel.viewMode);
            MetaTypeToggle.IsChecked = _panel.showMetadataType;
            MetaSizeToggle.IsChecked = _panel.showMetadataSize;
            MetaCreatedToggle.IsChecked = _panel.showMetadataCreated;
            MetaModifiedToggle.IsChecked = _panel.showMetadataModified;
            MetaDimensionsToggle.IsChecked = _panel.showMetadataDimensions;
            ApplyMetadataOrderToUi(_panel.metadataOrder);
            UpdateMetadataOptionsVisibility();
        }

        private void PopulateGlobalPanelDefaults()
        {
            if (_layout == null)
            {
                return;
            }

            HoverToggle.IsChecked = _layout.PanelDefaultExpandOnHover;
            HiddenToggle.IsChecked = _layout.PanelDefaultShowHidden;
            FileExtensionsToggle.IsChecked = _layout.PanelDefaultShowFileExtensions;
            SettingsButtonToggle.IsChecked = _layout.PanelDefaultShowSettingsButton;
            FolderActionSelect.SelectedIndex = _layout.PanelDefaultOpenFoldersExternally ? 1 : 0;
            SetMovementModeSelection(_layout.PanelDefaultMovementMode);
            SetSearchVisibilitySelection(_layout.PanelDefaultSearchVisibilityMode);
        }

        private void SetMovementModeSelection(string? mode)
        {
            string normalized = string.Equals(mode, "button", StringComparison.OrdinalIgnoreCase)
                ? "button"
                : string.Equals(mode, "locked", StringComparison.OrdinalIgnoreCase)
                    ? "locked"
                    : "titlebar";

            foreach (ComboBoxItem item in MovementModeSelect.Items)
            {
                if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    MovementModeSelect.SelectedItem = item;
                    break;
                }
            }

            if (MovementModeSelect.SelectedItem == null)
            {
                MovementModeSelect.SelectedIndex = 0;
            }
        }

        private void SetSearchVisibilitySelection(string? mode)
        {
            string searchMode = DesktopPanel.NormalizeSearchVisibilityMode(mode);
            foreach (ComboBoxItem item in SearchVisibilitySelect.Items)
            {
                if (string.Equals(item.Tag?.ToString(), searchMode, StringComparison.OrdinalIgnoreCase))
                {
                    SearchVisibilitySelect.SelectedItem = item;
                    break;
                }
            }

            if (SearchVisibilitySelect.SelectedItem == null)
            {
                SearchVisibilitySelect.SelectedIndex = 0;
            }
        }

        private void SetViewModeSelection(string? mode)
        {
            string normalized = DesktopPanel.NormalizeViewMode(mode);
            foreach (ComboBoxItem item in ViewModeSelect.Items)
            {
                if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    ViewModeSelect.SelectedItem = item;
                    break;
                }
            }

            if (ViewModeSelect.SelectedItem == null)
            {
                ViewModeSelect.SelectedIndex = 0;
            }

            UpdateMetadataOptionsVisibility();
        }

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_panel == null)
            {
                return;
            }

            var dlg = new WinForms.FolderBrowserDialog();
            var res = dlg.ShowDialog();
            if (res == WinForms.DialogResult.OK)
            {
                _panel.defaultFolderPath = dlg.SelectedPath;
                _panel.LoadFolder(dlg.SelectedPath);
                FolderPathLabel.Text = dlg.SelectedPath;
            }
        }

        private void PresetSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_panel != null && PresetSelect.SelectedItem is AppearancePreset preset)
            {
                _panel.ApplyAppearance(preset.Settings);
            }
            UpdateResetPresetToStandardButtonState();
            TryAutoApplySettings();
        }

        private void ResetPresetToStandardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_panel == null)
            {
                return;
            }

            _layoutStandardPresetName = MainWindow.GetCurrentStandardPresetName();
            if (string.IsNullOrWhiteSpace(_layoutStandardPresetName))
            {
                return;
            }

            PresetSelect.SelectedValue = _layoutStandardPresetName;
            UpdateResetPresetToStandardButtonState();
        }

        private void UpdateResetPresetToStandardButtonState()
        {
            if (ResetPresetToStandardButton == null) return;

            if (_panel == null || string.IsNullOrWhiteSpace(_layoutStandardPresetName))
            {
                ResetPresetToStandardButton.IsEnabled = false;
                return;
            }

            string selectedPresetName = (PresetSelect.SelectedItem as AppearancePreset)?.Name
                ?? PresetSelect.SelectedValue as string
                ?? _panel.assignedPresetName
                ?? string.Empty;

            ResetPresetToStandardButton.IsEnabled =
                !string.Equals(selectedPresetName, _layoutStandardPresetName, StringComparison.OrdinalIgnoreCase);
        }

        private void FitContent_Click(object sender, RoutedEventArgs e)
        {
            if (_panel == null)
            {
                return;
            }

            _panel.FitToContent();
            MainWindow.NotifyPanelsChanged();
        }

        private void ApplyCurrentSettings(bool closeAfterApply, bool applyName = true)
        {
            if (IsGlobalLayoutMode)
            {
                SaveGlobalLayoutSettings(closeAfterApply);
                return;
            }

            SaveSinglePanelSettings(closeAfterApply, applyName);
        }

        private void SaveGlobalLayoutSettings(bool closeAfterApply)
        {
            if (_layout == null)
            {
                if (closeAfterApply)
                {
                    Close();
                }
                return;
            }

            if (System.Windows.Application.Current?.MainWindow is MainWindow mainWindow)
            {
                string movementMode = (MovementModeSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "titlebar";
                string searchVisibilityMode = (SearchVisibilitySelect.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                    ?? DesktopPanel.SearchVisibilityAlways;

                mainWindow.ApplyLayoutGlobalPanelSettings(
                    _layout,
                    showHidden: HiddenToggle.IsChecked == true,
                    showFileExtensions: FileExtensionsToggle.IsChecked != false,
                    expandOnHover: HoverToggle.IsChecked == true,
                    openFoldersExternally: FolderActionSelect.SelectedIndex == 1,
                    showSettingsButton: SettingsButtonToggle.IsChecked != false,
                    movementMode: movementMode,
                    searchVisibilityMode: searchVisibilityMode,
                    defaultPresetName: (PresetSelect.SelectedItem as AppearancePreset)?.Name
                        ?? PresetSelect.SelectedValue as string
                        ?? _layout.DefaultPanelPresetName);
            }

            if (closeAfterApply)
            {
                Close();
            }
        }

        private void SaveSinglePanelSettings(bool closeAfterApply, bool applyName = true)
        {
            if (_panel == null)
            {
                if (closeAfterApply)
                {
                    Close();
                }
                return;
            }

            if (applyName)
            {
                if (_panel.Tabs.Count > 1 && _panel.ActiveTab != null)
                {
                    _panel.RenameTab(_panel.ActiveTabIndex, NameInput.Text);
                }
                else
                {
                    _panel.PanelTitle.Text = NameInput.Text;
                    _panel.Title = NameInput.Text;
                }
            }
            _panel.SetExpandOnHover(HoverToggle.IsChecked == true);
            bool hiddenChanged = _panel.showHiddenItems != (HiddenToggle.IsChecked == true);
            bool parentNavigationChanged = _panel.showParentNavigationItem != (ParentNavigationToggle.IsChecked != false);
            bool fileExtensionsChanged = _panel.showFileExtensions != (FileExtensionsToggle.IsChecked != false);
            _panel.showHiddenItems = HiddenToggle.IsChecked == true;
            _panel.showParentNavigationItem = ParentNavigationToggle.IsChecked != false;
            _panel.showFileExtensions = FileExtensionsToggle.IsChecked != false;
            _panel.showSettingsButton = SettingsButtonToggle.IsChecked != false;
            _panel.ApplySettingsButtonVisibility();
            _panel.openFoldersExternally = FolderActionSelect.SelectedIndex == 1;

            if (MovementModeSelect.SelectedItem is ComboBoxItem modeItem)
            {
                _panel.ApplyMovementMode(modeItem.Tag?.ToString() ?? "titlebar");
            }

            if (SearchVisibilitySelect.SelectedItem is ComboBoxItem searchVisibilityItem)
            {
                _panel.SetSearchVisibilityMode(searchVisibilityItem.Tag?.ToString());
            }

            if (ViewModeSelect.SelectedItem is ComboBoxItem viewModeItem)
            {
                _panel.ApplyViewSettings(
                    viewModeItem.Tag?.ToString(),
                    MetaTypeToggle.IsChecked != false,
                    MetaSizeToggle.IsChecked != false,
                    MetaCreatedToggle.IsChecked == true,
                    MetaModifiedToggle.IsChecked != false,
                    MetaDimensionsToggle.IsChecked != false,
                    metadataOrderOverride: GetMetadataOrderFromUi(),
                    persistSettings: false);
            }

            if (PresetSelect.SelectedItem is AppearancePreset preset)
            {
                _panel.assignedPresetName = preset.Name;
                _panel.ApplyAppearance(preset.Settings);
            }

            if ((hiddenChanged || fileExtensionsChanged || parentNavigationChanged) &&
                !string.IsNullOrWhiteSpace(_panel.currentFolderPath))
            {
                _panel.LoadFolder(_panel.currentFolderPath, false);
            }
            else if (fileExtensionsChanged && _panel.PanelType == PanelKind.List)
            {
                _panel.LoadList(_panel.PinnedItems.ToArray(), false);
            }

            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
            if (closeAfterApply)
            {
                Close();
            }
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
        {
            if (_panel == null || IsGlobalLayoutMode)
            {
                return;
            }

            // Keep name input in sync with the currently active tab before applying settings.
            // This prevents renaming a different tab when the settings window text is stale.
            if (_panel.Tabs.Count > 1 && _panel.ActiveTab != null)
            {
                SetNameInputWithoutAutoApply(_panel.ActiveTab.TabName);
            }

            ApplyCurrentSettings(closeAfterApply: false, applyName: false);
            _panel.AddTab(folderPath: null, switchTo: true);
            SetNameInputWithoutAutoApply(_panel.ActiveTab?.TabName ?? MainWindow.GetString("Loc.TabNewTab"));
            FolderPathLabel.Text = MainWindow.GetString("Loc.PanelSettingsFolderUnset");
            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
        }

        private void NewPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_panel == null || IsGlobalLayoutMode)
            {
                return;
            }

            ApplyCurrentSettings(closeAfterApply: false);

            var newPanel = new DesktopPanel();
            string presetName = string.IsNullOrWhiteSpace(_panel.assignedPresetName)
                ? (MainWindow.Presets.FirstOrDefault()?.Name ?? "Graphite")
                : _panel.assignedPresetName;

            newPanel.assignedPresetName = presetName;
            newPanel.ApplyAppearance(MainWindow.GetPresetSettings(presetName));
            newPanel.SetExpandOnHover(_panel.expandOnHover);
            newPanel.showHiddenItems = _panel.showHiddenItems;
            newPanel.showParentNavigationItem = _panel.showParentNavigationItem;
            newPanel.showFileExtensions = _panel.showFileExtensions;
            newPanel.showSettingsButton = _panel.showSettingsButton;
            newPanel.ApplySettingsButtonVisibility();
            newPanel.openFoldersExternally = _panel.openFoldersExternally;
            newPanel.ApplyMovementMode(_panel.movementMode);
            newPanel.SetSearchVisibilityMode(_panel.searchVisibilityMode);
            newPanel.ApplyViewSettings(
                _panel.viewMode,
                _panel.showMetadataType,
                _panel.showMetadataSize,
                _panel.showMetadataCreated,
                _panel.showMetadataModified,
                _panel.showMetadataDimensions,
                metadataOrderOverride: _panel.metadataOrder,
                persistSettings: false);

            newPanel.Width = _panel.Width;
            newPanel.Height = _panel.Height;
            newPanel.expandedHeight = _panel.expandedHeight > 0 ? _panel.expandedHeight : _panel.Height;
            newPanel.Left = _panel.Left + 36;
            newPanel.Top = _panel.Top + 36;
            newPanel.baseTopPosition = newPanel.Top;
            newPanel.collapsedTopPosition = newPanel.Top;
            newPanel.PanelTitle.Text = MainWindow.GetString("Loc.PanelDefaultTitle");
            newPanel.Title = MainWindow.GetString("Loc.PanelDefaultTitle");

            newPanel.InitializeSingleTabFromCurrentState();
            newPanel.Show();

            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            ApplyCurrentSettings(closeAfterApply: true);
        }

        private void UpdateMetadataOptionsVisibility()
        {
            if (MetadataLabel == null || MetadataDragHintText == null || MetadataItemsHost == null || ViewModeSelect == null)
            {
                return;
            }

            string selectedMode = DesktopPanel.ViewModeIcons;
            if (ViewModeSelect.SelectedItem is ComboBoxItem selectedItem)
            {
                selectedMode = DesktopPanel.NormalizeViewMode(selectedItem.Tag?.ToString());
            }

            bool showMetadata = !string.Equals(selectedMode, DesktopPanel.ViewModeIcons, StringComparison.OrdinalIgnoreCase);
            Visibility visibility = showMetadata ? Visibility.Visible : Visibility.Collapsed;
            MetadataLabel.Visibility = visibility;
            MetadataDragHintText.Visibility = visibility;
            MetadataItemsHost.Visibility = visibility;
        }

        private void ApplyMetadataOrderToUi(IEnumerable<string>? metadataOrder)
        {
            if (MetadataItemsHost == null)
            {
                return;
            }

            var itemMap = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase)
            {
                [DesktopPanel.MetadataType] = MetaTypeItem,
                [DesktopPanel.MetadataSize] = MetaSizeItem,
                [DesktopPanel.MetadataCreated] = MetaCreatedItem,
                [DesktopPanel.MetadataModified] = MetaModifiedItem,
                [DesktopPanel.MetadataDimensions] = MetaDimensionsItem
            };

            var normalized = DesktopPanel.NormalizeMetadataOrder(metadataOrder);
            MetadataItemsHost.Children.Clear();
            foreach (string key in normalized)
            {
                if (itemMap.TryGetValue(key, out Border? item))
                {
                    MetadataItemsHost.Children.Add(item);
                }
            }
        }

        private List<string> GetMetadataOrderFromUi()
        {
            if (MetadataItemsHost == null)
            {
                return DesktopPanel.NormalizeMetadataOrder(null);
            }

            var order = MetadataItemsHost.Children
                .OfType<Border>()
                .Select(item => item.Tag?.ToString() ?? string.Empty)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .ToList();

            return DesktopPanel.NormalizeMetadataOrder(order);
        }

        private Border? FindMetadataItemContainer(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Border border &&
                    MetadataItemsHost != null &&
                    MetadataItemsHost.Children.Contains(border))
                {
                    return border;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private void MetadataItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _metadataDragSourceItem = FindMetadataItemContainer(e.OriginalSource as DependencyObject);
            if (_metadataDragSourceItem != null && MetadataItemsHost != null)
            {
                _metadataDragStartPoint = e.GetPosition(MetadataItemsHost);
            }
        }

        private void MetadataItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_metadataDragging)
            {
                MetadataDrag_MouseMove(e);
                return;
            }

            if (_metadataDragSourceItem == null ||
                MetadataItemsHost == null ||
                e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            System.Windows.Point current = e.GetPosition(MetadataItemsHost);
            if (Math.Abs(current.X - _metadataDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _metadataDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            StartMetadataDrag();
        }

        private void StartMetadataDrag()
        {
            if (_metadataDragSourceItem == null || MetadataItemsHost == null) return;

            _metadataDragging = true;
            _metadataDragOriginalIndex = MetadataItemsHost.Children.IndexOf(_metadataDragSourceItem);
            _metadataCurrentIndex = _metadataDragOriginalIndex;

            // Calculate the Y position of the dragged item relative to the host
            var transform = _metadataDragSourceItem.TransformToAncestor(MetadataItemsHost);
            var itemPos = transform.Transform(new System.Windows.Point(0, 0));
            _metadataDragItemStartY = itemPos.Y;

            // Ensure all items have a TranslateTransform
            foreach (Border child in MetadataItemsHost.Children.OfType<Border>())
            {
                if (child.RenderTransform is not TranslateTransform)
                {
                    child.RenderTransform = new TranslateTransform();
                }
            }

            // Elevate dragged item
            System.Windows.Controls.Panel.SetZIndex(_metadataDragSourceItem, 10);
            _metadataDragSourceItem.Opacity = 0.9;

            _metadataDragSourceItem.CaptureMouse();
            _metadataDragSourceItem.PreviewMouseLeftButtonUp += MetadataDrag_MouseUp;
            _metadataDragSourceItem.LostMouseCapture += MetadataDrag_LostCapture;
        }

        private void MetadataDrag_MouseMove(System.Windows.Input.MouseEventArgs e)
        {
            if (!_metadataDragging || _metadataDragSourceItem == null || MetadataItemsHost == null) return;

            System.Windows.Point current = e.GetPosition(MetadataItemsHost);
            double deltaY = current.Y - _metadataDragStartPoint.Y;

            // Move the dragged item with the cursor
            var dragTransform = (TranslateTransform)_metadataDragSourceItem.RenderTransform;
            dragTransform.Y = deltaY;

            // Determine new index based on cursor position
            double draggedCenterY = _metadataDragItemStartY + deltaY + (_metadataDragSourceItem.ActualHeight / 2);
            int newIndex = CalculateTargetIndex(draggedCenterY);

            if (newIndex != _metadataCurrentIndex)
            {
                AnimateItemsToMakeRoom(newIndex);
                _metadataCurrentIndex = newIndex;
            }
        }

        private int CalculateTargetIndex(double centerY)
        {
            if (MetadataItemsHost == null) return _metadataCurrentIndex;

            var children = MetadataItemsHost.Children.OfType<Border>().ToList();
            double runningY = 0;

            for (int i = 0; i < children.Count; i++)
            {
                double itemHeight = children[i].ActualHeight + children[i].Margin.Top + children[i].Margin.Bottom;
                double itemCenterY = runningY + itemHeight / 2;
                if (centerY < itemCenterY)
                {
                    return i;
                }
                runningY += itemHeight;
            }

            return children.Count - 1;
        }

        private void AnimateItemsToMakeRoom(int targetIndex)
        {
            if (MetadataItemsHost == null || _metadataDragSourceItem == null) return;

            var children = MetadataItemsHost.Children.OfType<Border>().ToList();
            double itemHeight = _metadataDragSourceItem.ActualHeight + _metadataDragSourceItem.Margin.Top + _metadataDragSourceItem.Margin.Bottom;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(200);

            for (int i = 0; i < children.Count; i++)
            {
                Border child = children[i];
                if (ReferenceEquals(child, _metadataDragSourceItem)) continue;

                var tt = (TranslateTransform)child.RenderTransform;
                double targetY = 0;

                // If this item needs to shift to make room
                if (i >= targetIndex && i < _metadataDragOriginalIndex)
                {
                    // Items between target and original need to shift down
                    targetY = itemHeight;
                }
                else if (i <= targetIndex && i > _metadataDragOriginalIndex)
                {
                    // Items between original and target need to shift up
                    targetY = -itemHeight;
                }

                var anim = new DoubleAnimation(targetY, duration)
                {
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.HoldEnd
                };
                tt.BeginAnimation(TranslateTransform.YProperty, anim);
            }
        }

        private void MetadataDrag_MouseUp(object sender, MouseButtonEventArgs e)
        {
            FinishMetadataDrag();
        }

        private void MetadataDrag_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_metadataDragging)
            {
                FinishMetadataDrag();
            }
        }

        private void FinishMetadataDrag()
        {
            if (!_metadataDragging || _metadataDragSourceItem == null || MetadataItemsHost == null) return;
            _metadataDragging = false;

            Border draggedItem = _metadataDragSourceItem;
            int originalIndex = _metadataDragOriginalIndex;
            int finalIndex = _metadataCurrentIndex;

            draggedItem.PreviewMouseLeftButtonUp -= MetadataDrag_MouseUp;
            draggedItem.LostMouseCapture -= MetadataDrag_LostCapture;
            draggedItem.ReleaseMouseCapture();

            // Animate dragged item to its final slot position
            double itemHeight = draggedItem.ActualHeight + draggedItem.Margin.Top + draggedItem.Margin.Bottom;
            double targetY = (finalIndex - originalIndex) * itemHeight;

            var dragTransform = (TranslateTransform)draggedItem.RenderTransform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var snapAnim = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            snapAnim.Completed += (s, e) =>
            {
                // Clear all transforms and reorder children
                foreach (Border child in MetadataItemsHost.Children.OfType<Border>())
                {
                    var tt = (TranslateTransform)child.RenderTransform;
                    tt.BeginAnimation(TranslateTransform.YProperty, null);
                    tt.Y = 0;
                    System.Windows.Controls.Panel.SetZIndex(child, 0);
                    child.Opacity = 1;
                }

                // Actually reorder
                if (originalIndex != finalIndex)
                {
                    MetadataItemsHost.Children.RemoveAt(originalIndex);
                    int insertAt = finalIndex;
                    if (originalIndex < finalIndex) insertAt--;
                    MetadataItemsHost.Children.Insert(insertAt, draggedItem);
                    TryAutoApplySettings();
                }
            };

            dragTransform.BeginAnimation(TranslateTransform.YProperty, snapAnim);
            _metadataDragSourceItem = null;
        }
    }
}
