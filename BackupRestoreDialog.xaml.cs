using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopPlus
{
    public partial class BackupRestoreDialog : Window
    {
        private readonly Func<IReadOnlyList<UpdateBackupInfo>> _backupLoader;

        public UpdateBackupInfo? SelectedBackup { get; private set; }

        public BackupRestoreDialog(Func<IReadOnlyList<UpdateBackupInfo>> backupLoader)
        {
            InitializeComponent();
            _backupLoader = backupLoader;
            ReloadBackups();
        }

        private void ReloadBackups()
        {
            IReadOnlyList<UpdateBackupInfo> backups = _backupLoader?.Invoke() ?? Array.Empty<UpdateBackupInfo>();
            BackupList.ItemsSource = backups;
            EmptyStateText.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BackupList.Visibility = backups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            if (backups.Count == 0)
            {
                BackupList.SelectedItem = null;
                SetSelectedBackup(null);
                return;
            }

            if (SelectedBackup != null)
            {
                UpdateBackupInfo? matchingBackup = backups.FirstOrDefault(backup =>
                    string.Equals(backup.ArchivePath, SelectedBackup.ArchivePath, StringComparison.OrdinalIgnoreCase));
                BackupList.SelectedItem = matchingBackup ?? backups[0];
            }
            else
            {
                BackupList.SelectedIndex = 0;
            }
        }

        private void SetSelectedBackup(UpdateBackupInfo? backup)
        {
            SelectedBackup = backup;
            bool hasSelection = backup != null;
            RestoreButton.IsEnabled = hasSelection;
            NoSelectionText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            DetailsScrollViewer.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

            if (!hasSelection || backup == null)
            {
                VersionsValueText.Text = string.Empty;
                CreatedValueText.Text = string.Empty;
                FileNameValueText.Text = string.Empty;
                FileSizeValueText.Text = string.Empty;
                SourceDirectoryValueText.Text = string.Empty;
                IncludesValueText.Text = string.Empty;
                return;
            }

            string versionSummary = !string.IsNullOrWhiteSpace(backup.CurrentVersion) &&
                !string.IsNullOrWhiteSpace(backup.TargetVersion)
                ? $"{backup.CurrentVersion} -> {backup.TargetVersion}"
                : (!string.IsNullOrWhiteSpace(backup.CurrentVersion)
                    ? backup.CurrentVersion
                    : backup.ArchiveFileName);

            var includes = new List<string>();
            if (backup.ContainsAppSnapshot)
            {
                includes.Add(MainWindow.GetString("Loc.BackupRestoreContainsApp"));
            }

            if (backup.ContainsSettingsSnapshot)
            {
                includes.Add(MainWindow.GetString("Loc.BackupRestoreContainsSettings"));
            }

            if (backup.ContainsCustomLanguages)
            {
                includes.Add(MainWindow.GetString("Loc.BackupRestoreContainsLanguages"));
            }

            if (backup.ContainsAutoSortStorage)
            {
                includes.Add(MainWindow.GetString("Loc.BackupRestoreContainsAutoSort"));
            }

            VersionsValueText.Text = versionSummary;
            CreatedValueText.Text = backup.CreatedDisplay;
            FileNameValueText.Text = string.IsNullOrWhiteSpace(backup.ArchiveFileName) ? "-" : backup.ArchiveFileName;
            FileSizeValueText.Text = backup.FileSizeDisplay;
            SourceDirectoryValueText.Text = string.IsNullOrWhiteSpace(backup.SourceInstallDirectory)
                ? "-"
                : backup.SourceInstallDirectory;
            IncludesValueText.Text = includes.Count == 0
                ? "-"
                : string.Join(Environment.NewLine, includes.Select(item => "- " + item));
        }

        private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SetSelectedBackup(BackupList.SelectedItem as UpdateBackupInfo);
        }

        private void BackupList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedBackup == null)
            {
                return;
            }

            DialogResult = true;
            Close();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ReloadBackups();
        }

        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedBackup == null)
            {
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
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
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }
    }
}
