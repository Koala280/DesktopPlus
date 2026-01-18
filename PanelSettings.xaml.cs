using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace DesktopPlus
{
    public partial class PanelSettings : Window
    {
        private readonly DesktopPanel _panel;

        public PanelSettings(DesktopPanel panel)
        {
            InitializeComponent();
            _panel = panel;
            LoadPresets();
            Populate();
        }

        private void LoadPresets()
        {
            PresetSelect.ItemsSource = MainWindow.Presets;
            PresetSelect.SelectedValue = string.IsNullOrWhiteSpace(_panel.assignedPresetName)
                ? MainWindow.Presets.Find(p => p.Name == "Noir")?.Name
                : _panel.assignedPresetName;
        }

        private void Populate()
        {
            NameInput.Text = _panel.PanelTitle.Text;
            FolderPathLabel.Text = string.IsNullOrWhiteSpace(_panel.currentFolderPath) ? "(nicht gesetzt)" : _panel.currentFolderPath;
            HoverToggle.IsChecked = _panel.expandOnHover;
            HiddenToggle.IsChecked = _panel.showHiddenItems;
            FolderActionSelect.SelectedIndex = _panel.openFoldersExternally ? 1 : 0;
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

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _panel.PanelTitle.Text = NameInput.Text;
            _panel.Title = NameInput.Text;
            _panel.expandOnHover = HoverToggle.IsChecked == true;
            _panel.showHiddenItems = HiddenToggle.IsChecked == true;
            _panel.openFoldersExternally = FolderActionSelect.SelectedIndex == 1;

            if (PresetSelect.SelectedItem is MainWindow.AppearancePreset preset)
            {
                _panel.assignedPresetName = preset.Name;
                _panel.ApplyAppearance(preset.Settings);
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
