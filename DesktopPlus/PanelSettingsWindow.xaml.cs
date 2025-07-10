using System.Windows;

namespace DesktopPlus
{
    public partial class PanelSettingsWindow : Window
    {
        public string PanelName { get; private set; }
        public bool ExpandOnHover { get; private set; }
        public bool ShowMinimizeButton { get; private set; }

        public PanelSettingsWindow(string name, bool expandOnHover, bool showMinimize)
        {
            InitializeComponent();
            TxtName.Text = name;
            ChkHover.IsChecked = expandOnHover;
            ChkMinimize.IsChecked = showMinimize;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            PanelName = TxtName.Text;
            ExpandOnHover = ChkHover.IsChecked == true;
            ShowMinimizeButton = ChkMinimize.IsChecked == true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
