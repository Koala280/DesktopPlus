using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;

namespace DesktopPlus
{
    public partial class UninstallBackupWindow : Window
    {
        private bool _restoreInProgress;

        public UninstallBackupWindow(IReadOnlyList<UpdateBackupInfo> backups)
        {
            InitializeComponent();
            BackupsList.ItemsSource = backups;
            BackupsList.SelectedIndex = 0;
        }

        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_restoreInProgress || BackupsList.SelectedItem is not UpdateBackupInfo backup)
            {
                return;
            }

            _restoreInProgress = true;
            BackupsList.IsEnabled = CancelButton.IsEnabled = RestoreButton.IsEnabled = false;
            StatusText.Text = MainWindow.GetString("Loc.UninstallBackupRestoring");
            try
            {
                await MainWindow.RestoreBackupBeforeUninstallAsync(backup);
                _restoreInProgress = false;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(MainWindow.GetString("Loc.MsgBackupRestoreFailed"), ex.Message);
            }
            finally
            {
                _restoreInProgress = false;
                BackupsList.IsEnabled = CancelButton.IsEnabled = RestoreButton.IsEnabled = true;
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            e.Cancel = _restoreInProgress;
        }
    }
}
