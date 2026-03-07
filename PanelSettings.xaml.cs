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
            EmptyRecycleBinToggle.Checked += (_, __) => TryAutoApplySettings();
            EmptyRecycleBinToggle.Unchecked += (_, __) => TryAutoApplySettings();

            FolderActionSelect.SelectionChanged += (_, __) => TryAutoApplySettings();
            OpenClickBehaviorSelect.SelectionChanged += (_, __) => TryAutoApplySettings();
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
            SettingsButtonToggle.IsChecked = _panel.showSettingsButton;
            EmptyRecycleBinToggle.IsChecked = _panel.showEmptyRecycleBinButton;
            FolderActionSelect.SelectedIndex = _panel.openFoldersExternally ? 1 : 0;
            SetOpenClickBehaviorSelection(_panel.openItemsOnSingleClick);
            SetMovementModeSelection(_panel.movementMode);
            SetSearchVisibilitySelection(_panel.searchVisibilityMode);
            SetViewModeSelection(_panel.viewMode);
            _detailColumnsState = _panel.CreateDetailColumnSelectionState();
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
            ParentNavigationToggle.IsChecked = _layout.PanelDefaultShowParentNavigationItem;
            FileExtensionsToggle.IsChecked = _layout.PanelDefaultShowFileExtensions;
            SettingsButtonToggle.IsChecked = _layout.PanelDefaultShowSettingsButton;
            EmptyRecycleBinToggle.IsChecked = _layout.PanelDefaultShowEmptyRecycleBinButton;
            FolderActionSelect.SelectedIndex = _layout.PanelDefaultOpenFoldersExternally ? 1 : 0;
            SetOpenClickBehaviorSelection(_layout.PanelDefaultOpenItemsOnSingleClick);
            SetMovementModeSelection(_layout.PanelDefaultMovementMode);
            SetSearchVisibilitySelection(_layout.PanelDefaultSearchVisibilityMode);
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
            UpdateMetadataOptionsVisibility();
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
                string searchVisibilityMode = (SearchVisibilitySelect.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                    ?? DesktopPanel.SearchVisibilityAlways;
                string viewMode = (ViewModeSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                    ?? DesktopPanel.ViewModeIcons;

                mainWindow.ApplyLayoutGlobalPanelSettings(
                    _layout,
                    showHidden: HiddenToggle.IsChecked == true,
                    showParentNavigationItem: ParentNavigationToggle.IsChecked != false,
                    showFileExtensions: FileExtensionsToggle.IsChecked != false,
                    expandOnHover: HoverToggle.IsChecked == true,
                    openFoldersExternally: FolderActionSelect.SelectedIndex == 1,
                    openItemsOnSingleClick: string.Equals(
                        (OpenClickBehaviorSelect.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                        "single",
                        StringComparison.OrdinalIgnoreCase),
                    showSettingsButton: SettingsButtonToggle.IsChecked != false,
                    movementMode: movementMode,
                    searchVisibilityMode: searchVisibilityMode,
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
            bool hiddenChanged = _panel.showHiddenItems != (HiddenToggle.IsChecked == true);
            bool parentNavigationChanged = _panel.showParentNavigationItem != (ParentNavigationToggle.IsChecked != false);
            bool fileExtensionsChanged = _panel.showFileExtensions != (FileExtensionsToggle.IsChecked != false);
            _panel.showHiddenItems = HiddenToggle.IsChecked == true;
            _panel.showParentNavigationItem = ParentNavigationToggle.IsChecked != false;
            _panel.showFileExtensions = FileExtensionsToggle.IsChecked != false;
            _panel.showSettingsButton = SettingsButtonToggle.IsChecked != false;
            _panel.showEmptyRecycleBinButton = EmptyRecycleBinToggle.IsChecked != false;
            _panel.ApplySettingsButtonVisibility();
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
                _panel.SetSearchVisibilityMode(searchVisibilityItem.Tag?.ToString());
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
            newPanel.showFileExtensions = _panel.showFileExtensions;
            newPanel.showSettingsButton = _panel.showSettingsButton;
            newPanel.showEmptyRecycleBinButton = _panel.showEmptyRecycleBinButton;
            newPanel.ApplySettingsButtonVisibility();
            newPanel.openFoldersExternally = _panel.openFoldersExternally;
            newPanel.openItemsOnSingleClick = _panel.openItemsOnSingleClick;
            newPanel.ApplyMovementMode(_panel.movementMode);
            newPanel.SetSearchVisibilityMode(_panel.searchVisibilityMode);
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
            })
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
    }
}
