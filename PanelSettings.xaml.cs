using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace DesktopPlus
{
    public partial class PanelSettings : Window
    {
        private readonly DesktopPanel? _panel;
        private readonly LayoutDefinition? _layout;
        private string _layoutStandardPresetName = "";
        private bool IsGlobalLayoutMode => _layout != null;

        public PanelSettings(DesktopPanel panel)
        {
            InitializeComponent();
            TrySetWindowIcon();
            _panel = panel;
            LoadPresets();
            PopulatePanelSettings();
            UpdateResetPresetToStandardButtonState();
        }

        public PanelSettings(LayoutDefinition layout)
        {
            InitializeComponent();
            TrySetWindowIcon();
            _layout = layout;
            LoadPresetsForLayout(layout);
            ConfigureGlobalLayoutMode();
            PopulateGlobalPanelDefaults();
        }

        private void TrySetWindowIcon()
        {
            AppIconLoader.TryApplyWindowIcon(this);
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
            DefaultFolderLabel.Visibility = Visibility.Collapsed;
            DefaultFolderRow.Visibility = Visibility.Collapsed;
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

            NameInput.Text = _panel.PanelTitle.Text;
            FolderPathLabel.Text = string.IsNullOrWhiteSpace(_panel.defaultFolderPath)
                ? MainWindow.GetString("Loc.PanelSettingsFolderUnset")
                : _panel.defaultFolderPath;
            HoverToggle.IsChecked = _panel.expandOnHover;
            HiddenToggle.IsChecked = _panel.showHiddenItems;
            FileExtensionsToggle.IsChecked = _panel.showFileExtensions;
            SettingsButtonToggle.IsChecked = _panel.showSettingsButton;
            FolderActionSelect.SelectedIndex = _panel.openFoldersExternally ? 1 : 0;
            SetMovementModeSelection(_panel.movementMode);
            SetSearchVisibilitySelection(_panel.searchVisibilityMode);
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
            if (_panel == null)
            {
                return;
            }

            if (PresetSelect.SelectedItem is AppearancePreset preset)
            {
                _panel.ApplyAppearance(preset.Settings);
            }
            UpdateResetPresetToStandardButtonState();
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

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (IsGlobalLayoutMode)
            {
                SaveGlobalLayoutSettings();
                return;
            }

            SaveSinglePanelSettings();
        }

        private void SaveGlobalLayoutSettings()
        {
            if (_layout == null)
            {
                Close();
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

            Close();
        }

        private void SaveSinglePanelSettings()
        {
            if (_panel == null)
            {
                Close();
                return;
            }

            _panel.PanelTitle.Text = NameInput.Text;
            _panel.Title = NameInput.Text;
            _panel.SetExpandOnHover(HoverToggle.IsChecked == true);
            bool hiddenChanged = _panel.showHiddenItems != (HiddenToggle.IsChecked == true);
            bool fileExtensionsChanged = _panel.showFileExtensions != (FileExtensionsToggle.IsChecked != false);
            _panel.showHiddenItems = HiddenToggle.IsChecked == true;
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

            if (PresetSelect.SelectedItem is AppearancePreset preset)
            {
                _panel.assignedPresetName = preset.Name;
                _panel.ApplyAppearance(preset.Settings);
            }

            if ((hiddenChanged || fileExtensionsChanged) && !string.IsNullOrWhiteSpace(_panel.currentFolderPath))
            {
                _panel.LoadFolder(_panel.currentFolderPath, false);
            }
            else if (fileExtensionsChanged && _panel.PanelType == PanelKind.List)
            {
                _panel.LoadList(_panel.PinnedItems.ToArray(), false);
            }

            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
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
            Close();
        }
    }
}
