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
        private readonly DesktopPanel _panel;
        private string _layoutStandardPresetName = "";

        public PanelSettings(DesktopPanel panel)
        {
            InitializeComponent();
            _panel = panel;
            LoadPresets();
            Populate();
            UpdateResetPresetToStandardButtonState();
        }

        private void LoadPresets()
        {
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

        private void Populate()
        {
            NameInput.Text = _panel.PanelTitle.Text;
            FolderPathLabel.Text = string.IsNullOrWhiteSpace(_panel.currentFolderPath) ? "(nicht gesetzt)" : _panel.currentFolderPath;
            HoverToggle.IsChecked = _panel.expandOnHover;
            HiddenToggle.IsChecked = _panel.showHiddenItems;
            SettingsButtonToggle.IsChecked = _panel.showSettingsButton;
            FolderActionSelect.SelectedIndex = _panel.openFoldersExternally ? 1 : 0;

            foreach (ComboBoxItem item in MovementModeSelect.Items)
            {
                if (item.Tag?.ToString() == _panel.movementMode)
                {
                    MovementModeSelect.SelectedItem = item;
                    break;
                }
            }
            if (MovementModeSelect.SelectedItem == null)
                MovementModeSelect.SelectedIndex = 0;
        }

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new WinForms.FolderBrowserDialog();
            var res = dlg.ShowDialog();
            if (res == WinForms.DialogResult.OK)
            {
                _panel.defaultFolderPath = dlg.SelectedPath;
                _panel.LoadFolder(dlg.SelectedPath);
                FolderPathLabel.Text = dlg.SelectedPath;
            }
        }

        private void PresetSelect_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (PresetSelect.SelectedItem is AppearancePreset preset)
            {
                _panel.ApplyAppearance(preset.Settings);
            }
            UpdateResetPresetToStandardButtonState();
        }

        private void ResetPresetToStandardButton_Click(object sender, RoutedEventArgs e)
        {
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

            if (string.IsNullOrWhiteSpace(_layoutStandardPresetName))
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

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _panel.PanelTitle.Text = NameInput.Text;
            _panel.Title = NameInput.Text;
            _panel.SetExpandOnHover(HoverToggle.IsChecked == true);
            bool hiddenChanged = _panel.showHiddenItems != (HiddenToggle.IsChecked == true);
            _panel.showHiddenItems = HiddenToggle.IsChecked == true;
            _panel.showSettingsButton = SettingsButtonToggle.IsChecked != false;
            _panel.ApplySettingsButtonVisibility();
            _panel.openFoldersExternally = FolderActionSelect.SelectedIndex == 1;

            if (MovementModeSelect.SelectedItem is ComboBoxItem modeItem)
            {
                _panel.ApplyMovementMode(modeItem.Tag?.ToString() ?? "titlebar");
            }

            if (PresetSelect.SelectedItem is AppearancePreset preset)
            {
                _panel.assignedPresetName = preset.Name;
                _panel.ApplyAppearance(preset.Settings);
            }

            if (hiddenChanged && !string.IsNullOrWhiteSpace(_panel.currentFolderPath))
            {
                _panel.LoadFolder(_panel.currentFolderPath, false);
            }

            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
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
