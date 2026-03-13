using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Application = System.Windows.Application;
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
        private DetailColumnSelectionState _detailColumnsState = new DetailColumnSelectionState();

        private bool IsGlobalLayoutMode => _layout != null;

        public PanelSettings(DesktopPanel panel)
        {
            InitializeComponent();
            TrySetWindowIcon();
            _panel = panel;
            _panel.SizeChanged += Panel_SizeChanged;
            Closed += PanelSettings_Closed;
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
            IconParentNavigationModeSelect.SelectionChanged += (_, __) =>
            {
                SyncParentNavigationToggleFromIconMode();
                TryAutoApplySettings();
            };
            FileExtensionsToggle.Checked += (_, __) => TryAutoApplySettings();
            FileExtensionsToggle.Unchecked += (_, __) => TryAutoApplySettings();
            CloseButtonToggle.Checked += (_, __) => TryAutoApplySettings();
            CloseButtonToggle.Unchecked += (_, __) => TryAutoApplySettings();
            EmptyRecycleBinToggle.Checked += (_, __) => TryAutoApplySettings();
            EmptyRecycleBinToggle.Unchecked += (_, __) => TryAutoApplySettings();

            FolderActionSelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            OpenClickBehaviorSelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            MovementModeSelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            CollapseBehaviorSelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            SettingsVisibilitySelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            SearchVisibilitySelect.SelectionChanged += (_, __) =>
            {
                UpdateSearchVisibilityOptions();
                TryAutoApplySettings();
            };
            SearchExpandedOnlyToggle.Checked += (_, __) => TryAutoApplySettings();
            SearchExpandedOnlyToggle.Unchecked += (_, __) => TryAutoApplySettings();
            HeaderAlignmentSelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            ViewModeSelect.SelectionChanged += (_, __) =>
            {
                UpdateParentNavigationOptionsVisibility();
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
            Grid.SetColumn(PresetLabel, 0);
            Grid.SetColumnSpan(PresetLabel, 3);
            Grid.SetColumn(PresetSection, 0);
            Grid.SetColumnSpan(PresetSection, 3);
            DefaultFolderLabel.Visibility = Visibility.Collapsed;
            DefaultFolderRow.Visibility = Visibility.Collapsed;
            PanelActionsSection.Visibility = Visibility.Collapsed;
            ResetPresetToStandardButton.Visibility = Visibility.Collapsed;
            if (FitWidthButton.Parent is Grid fitButtonsGrid)
            {
                fitButtonsGrid.Visibility = Visibility.Collapsed;
            }
            FitWidthButton.Visibility = Visibility.Collapsed;
            FitHeightButton.Visibility = Visibility.Collapsed;
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

            NameInput.Text = (_panel.ActiveTab != null)
                ? _panel.ActiveTab.TabName
                : _panel.PanelTitle.Text;
            FolderPathLabel.Text = string.IsNullOrWhiteSpace(_panel.defaultFolderPath)
                ? MainWindow.GetString("Loc.PanelSettingsFolderUnset")
                : _panel.defaultFolderPath;
            HoverToggle.IsChecked = _panel.expandOnHover;
            HiddenToggle.IsChecked = _panel.showHiddenItems;
            ParentNavigationToggle.IsChecked = _panel.showParentNavigationItem;
            FileExtensionsToggle.IsChecked = _panel.showFileExtensions;
            CloseButtonToggle.IsChecked = _panel.showCloseButton;
            EmptyRecycleBinToggle.IsChecked = _panel.showEmptyRecycleBinButton;
            FolderActionSelect.SelectedIndex = _panel.openFoldersExternally ? 1 : 0;
            SetOpenClickBehaviorSelection(_panel.openItemsOnSingleClick);
            SetMovementModeSelection(_panel.movementMode);
            SetCollapseBehaviorSelection(_panel.collapseBehavior);
            SetSettingsVisibilitySelection(_panel.settingsButtonVisibilityMode, _panel.showSettingsButton);
            SetSearchVisibilitySelection(_panel.searchVisibilityMode);
            SearchExpandedOnlyToggle.IsChecked = _panel.searchVisibleOnlyExpanded;
            SetHeaderAlignmentSelection(_panel.headerContentAlignment);
            SetViewModeSelection(_panel.viewMode);
            SetIconParentNavigationModeSelection(_panel.iconViewParentNavigationMode, _panel.showParentNavigationItem);
            _detailColumnsState = _panel.CreateDetailColumnSelectionState();
            UpdateParentNavigationOptionsVisibility();
            UpdateMetadataOptionsVisibility();
            UpdateSearchVisibilityOptions();
        }

        private void PopulateGlobalPanelDefaults()
        {
            if (_layout == null)
            {
                return;
            }

            HoverToggle.IsChecked = _layout.PanelDefaultExpandOnHover;
            HiddenToggle.IsChecked = _layout.PanelDefaultShowHidden;
            ParentNavigationToggle.IsChecked = _layout.PanelDefaultShowParentNavigationItem;
            FileExtensionsToggle.IsChecked = _layout.PanelDefaultShowFileExtensions;
            CloseButtonToggle.IsChecked = _layout.PanelDefaultShowCloseButton;
            EmptyRecycleBinToggle.IsChecked = _layout.PanelDefaultShowEmptyRecycleBinButton;
            FolderActionSelect.SelectedIndex = _layout.PanelDefaultOpenFoldersExternally ? 1 : 0;
            SetOpenClickBehaviorSelection(_layout.PanelDefaultOpenItemsOnSingleClick);
            SetMovementModeSelection(_layout.PanelDefaultMovementMode);
            SetCollapseBehaviorSelection(_layout.PanelDefaultCollapseBehavior);
            SetSettingsVisibilitySelection(_layout.PanelDefaultSettingsButtonVisibilityMode, _layout.PanelDefaultShowSettingsButton);
            SetSearchVisibilitySelection(_layout.PanelDefaultSearchVisibilityMode);
            SearchExpandedOnlyToggle.IsChecked = DesktopPanel.NormalizeSearchVisibleOnlyExpanded(
                _layout.PanelDefaultSearchVisibleOnlyExpanded,
                _layout.PanelDefaultSearchVisibilityMode);
            SetHeaderAlignmentSelection(_layout.PanelDefaultHeaderContentAlignment);
            SetViewModeSelection(_layout.PanelDefaultViewMode);
            _detailColumnsState = new DetailColumnSelectionState
            {
                ShowType = _layout.PanelDefaultShowMetadataType,
                ShowSize = _layout.PanelDefaultShowMetadataSize,
                ShowCreated = _layout.PanelDefaultShowMetadataCreated,
                ShowModified = _layout.PanelDefaultShowMetadataModified,
                ShowDimensions = _layout.PanelDefaultShowMetadataDimensions,
                ShowAuthors = _layout.PanelDefaultShowMetadataAuthors,
                ShowCategories = _layout.PanelDefaultShowMetadataCategories,
                ShowTags = _layout.PanelDefaultShowMetadataTags,
                ShowTitle = _layout.PanelDefaultShowMetadataTitle,
                MetadataOrder = DesktopPanel.NormalizeMetadataOrder(_layout.PanelDefaultMetadataOrder),
                MetadataWidths = DesktopPanel.NormalizeMetadataWidths(_layout.PanelDefaultMetadataWidths)
            };
            SetIconParentNavigationModeSelection(_layout.PanelDefaultIconViewParentNavigationMode, _layout.PanelDefaultShowParentNavigationItem);
            UpdateParentNavigationOptionsVisibility();
            UpdateMetadataOptionsVisibility();
            UpdateSearchVisibilityOptions();
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

            UpdateSearchVisibilityOptions();
        }

        private void SetCollapseBehaviorSelection(string? mode)
        {
            string normalized = DesktopPanel.NormalizeCollapseBehavior(mode);
            foreach (ComboBoxItem item in CollapseBehaviorSelect.Items)
            {
                if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    CollapseBehaviorSelect.SelectedItem = item;
                    break;
                }
            }

            if (CollapseBehaviorSelect.SelectedItem == null)
            {
                CollapseBehaviorSelect.SelectedIndex = 0;
            }
        }

        private void SetSettingsVisibilitySelection(string? mode, bool legacyShowSettingsButton = true)
        {
            string normalized = DesktopPanel.NormalizeSettingsButtonVisibilityMode(mode, legacyShowSettingsButton);
            foreach (ComboBoxItem item in SettingsVisibilitySelect.Items)
            {
                if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    SettingsVisibilitySelect.SelectedItem = item;
                    break;
                }
            }

            if (SettingsVisibilitySelect.SelectedItem == null)
            {
                SettingsVisibilitySelect.SelectedIndex = 0;
            }
        }

        private void SetHeaderAlignmentSelection(string? alignment)
        {
            string normalized = DesktopPanel.NormalizeHeaderContentAlignment(alignment);
            foreach (ComboBoxItem item in HeaderAlignmentSelect.Items)
            {
                if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    HeaderAlignmentSelect.SelectedItem = item;
                    break;
                }
            }

            if (HeaderAlignmentSelect.SelectedItem == null)
            {
                HeaderAlignmentSelect.SelectedIndex = 0;
            }
        }

        private void Panel_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            UpdateSearchVisibilityOptions();
        }

        private void PanelSettings_Closed(object? sender, EventArgs e)
        {
            if (_panel != null)
            {
                _panel.SizeChanged -= Panel_SizeChanged;
            }
        }

        private void UpdateSearchVisibilityOptions()
        {
            if (SearchVisibilityFieldItem == null)
            {
                return;
            }

            bool canUseInlineSearch = _panel == null || _panel.CanDisplayInlineSearchFieldInCurrentHeader();
            SearchVisibilityFieldItem.IsEnabled = canUseInlineSearch;

            string? blockedToolTip = canUseInlineSearch
                ? null
                : MainWindow.GetString("Loc.PanelSettingsSearchFieldUnavailable");

            SearchVisibilityFieldItem.ToolTip = blockedToolTip;
            SearchVisibilitySelect.ToolTip = !canUseInlineSearch &&
                                             ReferenceEquals(SearchVisibilitySelect.SelectedItem, SearchVisibilityFieldItem)
                ? blockedToolTip
                : null;
        }

        private void SetOpenClickBehaviorSelection(bool openOnSingleClick)
        {
            string expectedTag = openOnSingleClick ? "single" : "double";
            foreach (ComboBoxItem item in OpenClickBehaviorSelect.Items)
            {
                if (string.Equals(item.Tag?.ToString(), expectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    OpenClickBehaviorSelect.SelectedItem = item;
                    break;
                }
            }

            if (OpenClickBehaviorSelect.SelectedItem == null)
            {
                OpenClickBehaviorSelect.SelectedIndex = 0;
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

        private void SetIconParentNavigationModeSelection(string? mode, bool showParentNavigationItem)
        {
            if (IconParentNavigationModeSelect == null)
            {
                return;
            }

            string normalized = GetParentNavigationModeSelectionTag(GetSelectedViewMode(), mode, showParentNavigationItem);
            foreach (ComboBoxItem item in IconParentNavigationModeSelect.Items)
            {
                if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    IconParentNavigationModeSelect.SelectedItem = item;
                    break;
                }
            }

            if (IconParentNavigationModeSelect.SelectedItem == null)
            {
                IconParentNavigationModeSelect.SelectedIndex = 0;
            }
        }

        private string GetSelectedViewMode()
        {
            return DesktopPanel.NormalizeViewMode(
                (ViewModeSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        }

        private string GetSelectedIconParentNavigationMode()
        {
            string selectedTag = (IconParentNavigationModeSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            string selectedViewMode = GetSelectedViewMode();
            bool showParentNavigationItem = ParentNavigationToggle.IsChecked != false;

            if (string.Equals(selectedViewMode, DesktopPanel.ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                return selectedTag switch
                {
                    DesktopPanel.IconParentNavigationModeItem => DesktopPanel.DetailsParentNavigationModeItem,
                    DesktopPanel.IconParentNavigationModeNone => DesktopPanel.DetailsParentNavigationModeNone,
                    _ => DesktopPanel.DetailsParentNavigationModeHeader
                };
            }

            return DesktopPanel.NormalizeIconViewParentNavigationMode(selectedTag, showParentNavigationItem);
        }

        private bool GetSelectedShowParentNavigationItem()
        {
            string selectedViewMode = GetSelectedViewMode();
            if (string.Equals(selectedViewMode, DesktopPanel.ViewModePhotos, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !IsParentNavigationModeNone(selectedViewMode, GetSelectedIconParentNavigationMode());
        }

        private void SetParentNavigationToggleWithoutAutoApply(bool isChecked)
        {
            bool previousApplying = _isApplyingSettings;
            _isApplyingSettings = true;
            try
            {
                ParentNavigationToggle.IsChecked = isChecked;
            }
            finally
            {
                _isApplyingSettings = previousApplying;
            }
        }

        private void SetIconParentNavigationModeWithoutAutoApply(string mode, bool showParentNavigationItem)
        {
            bool previousApplying = _isApplyingSettings;
            _isApplyingSettings = true;
            try
            {
                SetIconParentNavigationModeSelection(mode, showParentNavigationItem);
            }
            finally
            {
                _isApplyingSettings = previousApplying;
            }
        }

        private void SyncParentNavigationToggleFromIconMode()
        {
            if (ParentNavigationToggle == null || IconParentNavigationModeSelect == null)
            {
                return;
            }

            bool showParentNavigation = !IsParentNavigationModeNone(GetSelectedViewMode(), GetSelectedIconParentNavigationMode());
            SetParentNavigationToggleWithoutAutoApply(showParentNavigation);
        }

        private void UpdateParentNavigationOptionsVisibility()
        {
            if (ParentNavigationToggle == null ||
                IconParentNavigationModeLabel == null ||
                IconParentNavigationModeSelect == null ||
                ViewModeSelect == null)
            {
                return;
            }

            if (_panel != null && _panel.PanelType != PanelKind.Folder)
            {
                ParentNavigationToggle.Visibility = Visibility.Collapsed;
                IconParentNavigationModeLabel.Visibility = Visibility.Collapsed;
                IconParentNavigationModeSelect.Visibility = Visibility.Collapsed;
                return;
            }

            string selectedViewMode = GetSelectedViewMode();
            bool isIconView = string.Equals(selectedViewMode, DesktopPanel.ViewModeIcons, StringComparison.OrdinalIgnoreCase);
            bool isDetailsView = string.Equals(selectedViewMode, DesktopPanel.ViewModeDetails, StringComparison.OrdinalIgnoreCase);
            bool isPhotoView = string.Equals(selectedViewMode, DesktopPanel.ViewModePhotos, StringComparison.OrdinalIgnoreCase);

            if (isIconView || isDetailsView)
            {
                string storedMode = _panel != null
                    ? _panel.iconViewParentNavigationMode
                    : _layout?.PanelDefaultIconViewParentNavigationMode ?? DesktopPanel.IconParentNavigationModeHeader;
                bool storedShowParentNavigation = _panel != null
                    ? _panel.showParentNavigationItem
                    : _layout?.PanelDefaultShowParentNavigationItem ?? true;
                SetIconParentNavigationModeWithoutAutoApply(storedMode, storedShowParentNavigation);

                ParentNavigationToggle.Visibility = Visibility.Collapsed;
                IconParentNavigationModeLabel.Visibility = Visibility.Visible;
                IconParentNavigationModeSelect.Visibility = Visibility.Visible;
                SyncParentNavigationToggleFromIconMode();
                return;
            }

            if (isPhotoView)
            {
                ParentNavigationToggle.Visibility = Visibility.Collapsed;
                IconParentNavigationModeLabel.Visibility = Visibility.Collapsed;
                IconParentNavigationModeSelect.Visibility = Visibility.Collapsed;
                return;
            }

            ParentNavigationToggle.Visibility = Visibility.Visible;
            IconParentNavigationModeLabel.Visibility = Visibility.Collapsed;
            IconParentNavigationModeSelect.Visibility = Visibility.Collapsed;
        }

        private static bool IsParentNavigationModeNone(string viewMode, string mode)
        {
            if (string.Equals(viewMode, DesktopPanel.ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(
                    DesktopPanel.NormalizeDetailsViewParentNavigationMode(mode),
                    DesktopPanel.DetailsParentNavigationModeNone,
                    StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(
                DesktopPanel.NormalizeIconViewParentNavigationMode(mode),
                DesktopPanel.IconParentNavigationModeNone,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetParentNavigationModeSelectionTag(string viewMode, string? mode, bool showParentNavigationItem)
        {
            if (string.Equals(viewMode, DesktopPanel.ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                string normalizedDetailsMode = DesktopPanel.NormalizeDetailsViewParentNavigationMode(mode, showParentNavigationItem);
                return normalizedDetailsMode switch
                {
                    DesktopPanel.DetailsParentNavigationModeItem => DesktopPanel.IconParentNavigationModeItem,
                    DesktopPanel.DetailsParentNavigationModeNone => DesktopPanel.IconParentNavigationModeNone,
                    _ => DesktopPanel.IconParentNavigationModeHeader
                };
            }

            string normalizedIconMode = DesktopPanel.NormalizeIconViewParentNavigationMode(mode, showParentNavigationItem);
            return normalizedIconMode switch
            {
                DesktopPanel.IconParentNavigationModeHeader => DesktopPanel.IconParentNavigationModeHeader,
                DesktopPanel.IconParentNavigationModeNone => DesktopPanel.IconParentNavigationModeNone,
                _ => DesktopPanel.IconParentNavigationModeItem
            };
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
                UpdateParentNavigationOptionsVisibility();
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
            if (ResetPresetToStandardButton == null)
            {
                return;
            }

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

        private void FitWidth_Click(object sender, RoutedEventArgs e)
        {
            _panel?.FitWidthToContent();
        }

        private void FitHeight_Click(object sender, RoutedEventArgs e)
        {
            _panel?.FitHeightToContent();
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

            if (Application.Current?.MainWindow is MainWindow mainWindow)
            {
                string movementMode = (MovementModeSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "titlebar";
                string collapseBehavior = (CollapseBehaviorSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                    ?? DesktopPanel.CollapseBehaviorBoth;
                string settingsButtonVisibilityMode = (SettingsVisibilitySelect.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                    ?? DesktopPanel.SettingsButtonVisibilityExpandedOnly;
                string searchVisibilityMode = (SearchVisibilitySelect.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                    ?? DesktopPanel.SearchVisibilityButton;
                bool searchVisibleOnlyExpanded = SearchExpandedOnlyToggle.IsChecked == true;
                string headerContentAlignment = (HeaderAlignmentSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                    ?? DesktopPanel.HeaderContentAlignmentLeft;
                string viewMode = (ViewModeSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                    ?? DesktopPanel.ViewModeIcons;
                bool showParentNavigationItem = GetSelectedShowParentNavigationItem();
                string iconParentNavigationMode = GetSelectedIconParentNavigationMode();

                mainWindow.ApplyLayoutGlobalPanelSettings(
                    _layout,
                    showHidden: HiddenToggle.IsChecked == true,
                    showParentNavigationItem: showParentNavigationItem,
                    iconViewParentNavigationMode: iconParentNavigationMode,
                    showFileExtensions: FileExtensionsToggle.IsChecked != false,
                    expandOnHover: HoverToggle.IsChecked == true,
                    openFoldersExternally: FolderActionSelect.SelectedIndex == 1,
                    openItemsOnSingleClick: string.Equals(
                        (OpenClickBehaviorSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                        "single",
                        StringComparison.OrdinalIgnoreCase),
                    collapseBehavior: collapseBehavior,
                    settingsButtonVisibilityMode: settingsButtonVisibilityMode,
                    showCloseButton: CloseButtonToggle.IsChecked != false,
                    movementMode: movementMode,
                    searchVisibilityMode: searchVisibilityMode,
                    searchVisibleOnlyExpanded: searchVisibleOnlyExpanded,
                    headerContentAlignment: headerContentAlignment,
                    viewMode: viewMode,
                    showMetadataType: _detailColumnsState.ShowType,
                    showMetadataSize: _detailColumnsState.ShowSize,
                    showMetadataCreated: _detailColumnsState.ShowCreated,
                    showMetadataModified: _detailColumnsState.ShowModified,
                    showMetadataDimensions: _detailColumnsState.ShowDimensions,
                    showMetadataAuthors: _detailColumnsState.ShowAuthors,
                    showMetadataCategories: _detailColumnsState.ShowCategories,
                    showMetadataTags: _detailColumnsState.ShowTags,
                    showMetadataTitle: _detailColumnsState.ShowTitle,
                    metadataOrder: _detailColumnsState.MetadataOrder,
                    metadataWidths: _detailColumnsState.MetadataWidths,
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
                if (_panel.ActiveTab != null)
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
            bool selectedShowParentNavigationItem = GetSelectedShowParentNavigationItem();
            string selectedIconParentNavigationMode = GetSelectedIconParentNavigationMode();
            bool hiddenChanged = _panel.showHiddenItems != (HiddenToggle.IsChecked == true);
            bool parentNavigationChanged = _panel.showParentNavigationItem != selectedShowParentNavigationItem;
            bool iconParentNavigationModeChanged = !string.Equals(
                DesktopPanel.NormalizeIconViewParentNavigationMode(_panel.iconViewParentNavigationMode, _panel.showParentNavigationItem),
                selectedIconParentNavigationMode,
                StringComparison.OrdinalIgnoreCase);
            bool fileExtensionsChanged = _panel.showFileExtensions != (FileExtensionsToggle.IsChecked != false);
            _panel.showHiddenItems = HiddenToggle.IsChecked == true;
            _panel.showParentNavigationItem = selectedShowParentNavigationItem;
            _panel.iconViewParentNavigationMode = selectedIconParentNavigationMode;
            _panel.showFileExtensions = FileExtensionsToggle.IsChecked != false;
            _panel.showCloseButton = CloseButtonToggle.IsChecked != false;
            _panel.showEmptyRecycleBinButton = EmptyRecycleBinToggle.IsChecked != false;
            _panel.SetSettingsButtonVisibilityMode((SettingsVisibilitySelect.SelectedItem as ComboBoxItem)?.Tag?.ToString());
            _panel.ApplyCollapseBehavior((CollapseBehaviorSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString());
            _panel.ApplyCloseButtonVisibility();
            _panel.UpdateEmptyRecycleBinButtonVisibility();
            _panel.openFoldersExternally = FolderActionSelect.SelectedIndex == 1;
            _panel.openItemsOnSingleClick = string.Equals(
                (OpenClickBehaviorSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                "single",
                StringComparison.OrdinalIgnoreCase);

            if (MovementModeSelect.SelectedItem is ComboBoxItem modeItem)
            {
                _panel.ApplyMovementMode(modeItem.Tag?.ToString() ?? "titlebar");
            }

            if (SearchVisibilitySelect.SelectedItem is ComboBoxItem searchVisibilityItem)
            {
                _panel.SetSearchVisibility(searchVisibilityItem.Tag?.ToString(), SearchExpandedOnlyToggle.IsChecked == true);
            }

            if (HeaderAlignmentSelect.SelectedItem is ComboBoxItem headerAlignmentItem)
            {
                _panel.ApplyHeaderContentAlignment(headerAlignmentItem.Tag?.ToString());
            }

            if (ViewModeSelect.SelectedItem is ComboBoxItem viewModeItem)
            {
                _panel.ApplyViewSettings(
                    viewModeItem.Tag?.ToString(),
                    _detailColumnsState.ShowType,
                    _detailColumnsState.ShowSize,
                    _detailColumnsState.ShowCreated,
                    _detailColumnsState.ShowModified,
                    _detailColumnsState.ShowDimensions,
                    _detailColumnsState.ShowAuthors,
                    _detailColumnsState.ShowCategories,
                    _detailColumnsState.ShowTags,
                    _detailColumnsState.ShowTitle,
                    metadataOrderOverride: _detailColumnsState.MetadataOrder,
                    metadataWidthsOverride: _detailColumnsState.MetadataWidths,
                    persistSettings: false);
            }

            if (PresetSelect.SelectedItem is AppearancePreset preset)
            {
                _panel.assignedPresetName = preset.Name;
                _panel.ApplyAppearance(preset.Settings);
            }

            if ((hiddenChanged || fileExtensionsChanged || parentNavigationChanged || iconParentNavigationModeChanged) &&
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

            if (_panel.ActiveTab != null)
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
            newPanel.iconViewParentNavigationMode = DesktopPanel.NormalizeIconViewParentNavigationMode(
                _panel.iconViewParentNavigationMode,
                _panel.showParentNavigationItem);
            newPanel.showFileExtensions = _panel.showFileExtensions;
            newPanel.SetSettingsButtonVisibilityMode(_panel.settingsButtonVisibilityMode);
            newPanel.showCloseButton = _panel.showCloseButton;
            newPanel.showEmptyRecycleBinButton = _panel.showEmptyRecycleBinButton;
            newPanel.ApplyCollapseBehavior(_panel.collapseBehavior);
            newPanel.ApplyCloseButtonVisibility();
            newPanel.openFoldersExternally = _panel.openFoldersExternally;
            newPanel.openItemsOnSingleClick = _panel.openItemsOnSingleClick;
            newPanel.ApplyMovementMode(_panel.movementMode);
            newPanel.SetSearchVisibility(_panel.searchVisibilityMode, _panel.searchVisibleOnlyExpanded);
            newPanel.ApplyHeaderContentAlignment(_panel.headerContentAlignment);
            newPanel.ApplyViewSettings(
                _panel.viewMode,
                _panel.showMetadataType,
                _panel.showMetadataSize,
                _panel.showMetadataCreated,
                _panel.showMetadataModified,
                _panel.showMetadataDimensions,
                _panel.showMetadataAuthors,
                _panel.showMetadataCategories,
                _panel.showMetadataTags,
                _panel.showMetadataTitle,
                metadataOrderOverride: _panel.metadataOrder,
                metadataWidthsOverride: _panel.metadataWidths,
                persistSettings: false);

            newPanel.Width = _panel.Width;
            newPanel.Height = _panel.Height;
            newPanel.expandedHeight = _panel.expandedHeight > 0 ? _panel.expandedHeight : _panel.Height;
            const double sideGap = 20;
            double preferredLeft = _panel.Left + _panel.Width + sideGap;
            double targetLeft = preferredLeft;
            double targetTop = _panel.Top;

            try
            {
                int sourceLeft = (int)Math.Round(_panel.Left);
                int sourceTop = (int)Math.Round(_panel.Top);
                int sourceWidth = Math.Max(1, (int)Math.Round(_panel.Width));
                int sourceHeight = Math.Max(1, (int)Math.Round(_panel.Height));
                var sourceRect = new System.Drawing.Rectangle(sourceLeft, sourceTop, sourceWidth, sourceHeight);
                var workArea = WinForms.Screen.FromRectangle(sourceRect).WorkingArea;

                double maxLeft = workArea.Right - newPanel.Width;
                double minLeft = workArea.Left;
                double leftFallback = _panel.Left - newPanel.Width - sideGap;

                if (preferredLeft <= maxLeft)
                {
                    targetLeft = preferredLeft;
                }
                else if (leftFallback >= minLeft)
                {
                    targetLeft = leftFallback;
                }
                else
                {
                    targetLeft = Math.Max(minLeft, Math.Min(preferredLeft, maxLeft));
                }

                double maxTop = workArea.Bottom - newPanel.Height;
                targetTop = Math.Max(workArea.Top, Math.Min(targetTop, maxTop));
            }
            catch
            {
                targetLeft = preferredLeft;
                targetTop = _panel.Top;
            }

            newPanel.Left = targetLeft;
            newPanel.Top = targetTop;
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
            if (MetadataLabel == null ||
                MetadataColumnsHintText == null ||
                ChooseColumnsButton == null ||
                ViewModeSelect == null)
            {
                return;
            }

            string selectedMode = DesktopPanel.ViewModeIcons;
            if (ViewModeSelect.SelectedItem is ComboBoxItem selectedItem)
            {
                selectedMode = DesktopPanel.NormalizeViewMode(selectedItem.Tag?.ToString());
            }

            bool showMetadata = string.Equals(selectedMode, DesktopPanel.ViewModeDetails, StringComparison.OrdinalIgnoreCase);
            Visibility visibility = showMetadata ? Visibility.Visible : Visibility.Collapsed;
            MetadataLabel.Visibility = visibility;
            MetadataColumnsHintText.Visibility = visibility;
            ChooseColumnsButton.Visibility = visibility;
        }

        private void ChooseColumnsButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new DetailColumnsWindow(new DetailColumnSelectionState
            {
                ShowType = _detailColumnsState.ShowType,
                ShowSize = _detailColumnsState.ShowSize,
                ShowCreated = _detailColumnsState.ShowCreated,
                ShowModified = _detailColumnsState.ShowModified,
                ShowDimensions = _detailColumnsState.ShowDimensions,
                ShowAuthors = _detailColumnsState.ShowAuthors,
                ShowCategories = _detailColumnsState.ShowCategories,
                ShowTags = _detailColumnsState.ShowTags,
                ShowTitle = _detailColumnsState.ShowTitle,
                MetadataOrder = DesktopPanel.NormalizeMetadataOrder(_detailColumnsState.MetadataOrder),
                MetadataWidths = DesktopPanel.NormalizeMetadataWidths(_detailColumnsState.MetadataWidths)
            }, CreateAvailableDetailColumnOptions())
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true || dialog.ResultState == null)
            {
                return;
            }

            _detailColumnsState = dialog.ResultState;
            TryAutoApplySettings();
        }

        private IReadOnlyList<DetailColumnOption> CreateAvailableDetailColumnOptions()
        {
            if (_panel != null)
            {
                return _panel.CreateAvailableDetailColumnOptions();
            }

            return new[]
            {
                new DetailColumnOption(DesktopPanel.MetadataName, MainWindow.GetString("Loc.DetailColumnName"), true, false),
                new DetailColumnOption(DesktopPanel.MetadataModified, MainWindow.GetString("Loc.PanelSettingsMetaModified"), _detailColumnsState.ShowModified, true),
                new DetailColumnOption(DesktopPanel.MetadataType, MainWindow.GetString("Loc.PanelSettingsMetaType"), _detailColumnsState.ShowType, true),
                new DetailColumnOption(DesktopPanel.MetadataSize, MainWindow.GetString("Loc.PanelSettingsMetaSize"), _detailColumnsState.ShowSize, true),
                new DetailColumnOption(DesktopPanel.MetadataCreated, MainWindow.GetString("Loc.PanelSettingsMetaCreated"), _detailColumnsState.ShowCreated, true),
                new DetailColumnOption(DesktopPanel.MetadataDimensions, MainWindow.GetString("Loc.PanelSettingsMetaDimensions"), _detailColumnsState.ShowDimensions, true),
                new DetailColumnOption(DesktopPanel.MetadataAuthors, MainWindow.GetString("Loc.PanelSettingsMetaAuthors"), _detailColumnsState.ShowAuthors, true),
                new DetailColumnOption(DesktopPanel.MetadataCategories, MainWindow.GetString("Loc.PanelSettingsMetaCategories"), _detailColumnsState.ShowCategories, true),
                new DetailColumnOption(DesktopPanel.MetadataTags, MainWindow.GetString("Loc.PanelSettingsMetaTags"), _detailColumnsState.ShowTags, true),
                new DetailColumnOption(DesktopPanel.MetadataTitle, MainWindow.GetString("Loc.PanelSettingsMetaTitle"), _detailColumnsState.ShowTitle, true)
            };
        }
    }
}
