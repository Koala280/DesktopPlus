using System;
using System.Windows;
using System.Windows.Input;

namespace DesktopPlus
{
    public enum UpdateActionDialogChoice
    {
        Cancel = 0,
        OpenReleasePage = 1,
        InstallNow = 2
    }

    public partial class UpdateActionDialog : Window
    {
        public UpdateActionDialogChoice SelectedAction { get; private set; } = UpdateActionDialogChoice.Cancel;

        public UpdateActionDialog(string latestVersionText, string currentVersionText)
        {
            InitializeComponent();
            LatestVersionText.Text = string.IsNullOrWhiteSpace(latestVersionText) ? "-" : latestVersionText;
            InstalledVersionText.Text = string.IsNullOrWhiteSpace(currentVersionText) ? "-" : currentVersionText;
        }

        private void OpenReleaseButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = UpdateActionDialogChoice.OpenReleasePage;
            DialogResult = true;
            Close();
        }

        private void InstallNowButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = UpdateActionDialogChoice.InstallNow;
            DialogResult = true;
            Close();
        }

        private void LaterButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = UpdateActionDialogChoice.Cancel;
            DialogResult = false;
            Close();
        }

        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch
            {
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                SelectedAction = UpdateActionDialogChoice.Cancel;
                DialogResult = false;
                Close();
            }
        }
    }
}
