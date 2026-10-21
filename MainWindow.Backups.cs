using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DesktopPlus
{
    public partial class MainWindow
    {
        private bool _isManualBackupInProgress;
        private static bool _backupCleanupScheduled;

        private static bool TryCreateManagedBackup(
            string backupKind,
            string reason,
            string displayName,
            out string backupArchivePath,
            out string errorMessage,
            string? preserveArchivePath = null,
            string targetVersion = "",
            IReadOnlyCollection<DesktopSortMovedItem>? desktopMoves = null)
        {
            backupArchivePath = string.Empty;
            errorMessage = string.Empty;

            string normalizedKind = string.IsNullOrWhiteSpace(backupKind)
                ? "manual"
                : backupKind.Trim().ToLowerInvariant();
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string archiveFileName =
                $"DesktopPlus-backup-{SanitizeVersionTokenForFileName(normalizedKind)}-{timestamp}-{Guid.NewGuid():N}.zip";
            string archivePath = Path.Combine(UpdateBackupsDirectory, archiveFileName);
            string pendingPath = Path.Combine(UpdateBackupsDirectory, $".pending-{Guid.NewGuid():N}.zip");

            lock (BackupRetention.SyncRoot)
            {
                try
                {
                    Directory.CreateDirectory(UpdateBackupsDirectory);
                    WriteReferenceBackupArchive(pendingPath, settingsFilePath, CustomLanguageFolderPath,
                        GetDesktopDirectoryPaths(), GetAutoSortStorageRootPathForBackup(),
                        normalizedKind, reason, displayName, targetVersion, desktopMoves);
                    File.Move(pendingPath, archivePath);
                    BackupRetention.Maintain(UpdateBackupsDirectory, archivePath, preserveArchivePath);
                    backupArchivePath = archivePath;
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to create {normalizedKind} backup: {ex}");
                    errorMessage = ex.Message;
                    return false;
                }
                finally { TryDeleteFile(pendingPath); }
            }
        }

        internal static void WriteReferenceBackupArchive(
            string archivePath, string settingsPath, string languageDirectory,
            IReadOnlyList<string> desktopRoots, string storageRoot,
            string backupKind, string reason, string displayName, string targetVersion = "",
            IReadOnlyCollection<DesktopSortMovedItem>? desktopMoves = null)
        {
            var moves = desktopMoves?.ToList() ?? new List<DesktopSortMovedItem>();
            var manifest = new UpdateBackupManifest
            {
                SchemaVersion = 4,
                BackupKind = backupKind,
                Reason = reason,
                DisplayName = displayName,
                CurrentVersion = NormalizeVersionToken(GetInstalledVersionText()),
                TargetVersion = targetVersion,
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                SourceExecutablePath = Environment.ProcessPath ?? string.Empty,
                SourceInstallDirectory = GetCurrentInstallDirectory() ?? string.Empty,
                UsesFileReferences = true,
                FileReferences = BackupFileReferences.Capture(desktopRoots, storageRoot),
                DesktopMoves = moves
            };
            using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            if (File.Exists(settingsPath))
            {
                archive.CreateEntryFromFile(settingsPath, "user-data/roaming/DesktopPlus_Settings.json", CompressionLevel.Fastest);
                manifest.IncludedEntries.Add(settingsPath);
            }
            if (Directory.Exists(languageDirectory))
            {
                foreach (string path in Directory.EnumerateFiles(languageDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    archive.CreateEntryFromFile(path, "user-data/roaming/DesktopPlus/Languages/" + Path.GetFileName(path), CompressionLevel.Fastest);
                    manifest.IncludedEntries.Add(path);
                }
            }
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json", CompressionLevel.Fastest).Open()))
            {
                writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            using (var writer = new StreamWriter(archive.CreateEntry("RESTORE.txt", CompressionLevel.Fastest).Open()))
            {
                writer.Write(BuildUpdateBackupRestoreInstructions(Path.GetFileName(archivePath)));
            }
        }

        private bool EnsureCriticalBackup(string reason)
        {
            SaveSettingsImmediate();
            bool created = TryCreateManagedBackup(
                "critical",
                reason,
                reason,
                out _,
                out string errorMessage);
            if (created)
            {
                RefreshBackupsTab();
                return true;
            }

            System.Windows.MessageBox.Show(
                string.Format(GetString("Loc.BackupsSafetyFailed"), errorMessage),
                GetString("Loc.MsgError"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        private static bool TryCreateAutoSortBackup(
            IReadOnlyCollection<DesktopSortMovedItem> desktopMoves,
            string reason,
            string displayName,
            out string errorMessage)
        {
            return TryCreateManagedBackup("auto-sort", reason, displayName,
                out _, out errorMessage, desktopMoves: desktopMoves);
        }

        private void RefreshBackupsTab(string? preferredArchivePath = null)
        {
            if (BackupsList == null)
            {
                return;
            }

            if (!_backupCleanupScheduled)
            {
                _backupCleanupScheduled = true;
                _ = Task.Run(() => BackupRetention.CleanAbandonedWorkDirectories(UpdateBackupsDirectory));
            }

            IReadOnlyList<UpdateBackupInfo> backups = LoadAvailableUpdateBackups();
            BackupsList.ItemsSource = backups;
            BackupsEmptyText.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BackupsList.Visibility = backups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            BackupsCountText.Text = string.Format(GetString("Loc.BackupsCount"), backups.Count);

            UpdateBackupInfo? preferred = !string.IsNullOrWhiteSpace(preferredArchivePath)
                ? backups.FirstOrDefault(backup =>
                    string.Equals(backup.ArchivePath, preferredArchivePath, StringComparison.OrdinalIgnoreCase))
                : null;
            BackupsList.SelectedItem = preferred ?? backups.FirstOrDefault();
            UpdateBackupDetails(BackupsList.SelectedItem as UpdateBackupInfo);
        }

        private void UpdateBackupDetails(UpdateBackupInfo? backup)
        {
            bool hasSelection = backup != null;
            bool busy = _desktopAutoSortInProgress || _isManualBackupInProgress;
            BackupRestoreSelectedButton.IsEnabled = hasSelection && backup!.CanRestore && !busy;
            BackupDeleteSelectedButton.IsEnabled = hasSelection && !busy;
            CreateManualBackupButton.IsEnabled = !busy;
            BackupNoSelectionText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            BackupDetailsPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

            if (backup == null)
            {
                return;
            }

            BackupDetailsNameText.Text = backup.DisplayName;
            BackupDetailsTypeText.Text = GetBackupKindDisplayName(backup.BackupKind);
            BackupDetailsReasonText.Text = string.IsNullOrWhiteSpace(backup.Reason) ? "-" : backup.Reason;
            BackupDetailsCreatedText.Text = backup.CreatedDisplay;
            BackupDetailsSizeText.Text = backup.FileSizeDisplay;
            BackupDetailsFileText.Text = backup.ArchiveFileName;

            var includes = new List<string>();
            if (backup.ContainsAppSnapshot) includes.Add(GetString("Loc.BackupRestoreContainsApp"));
            if (backup.ContainsSettingsSnapshot) includes.Add(GetString("Loc.BackupRestoreContainsSettings"));
            if (backup.ContainsCustomLanguages) includes.Add(GetString("Loc.BackupRestoreContainsLanguages"));
            if (backup.ContainsAutoSortStorage) includes.Add(GetString("Loc.BackupRestoreContainsAutoSort"));
            if (backup.ContainsDesktopSnapshot) includes.Add(GetString("Loc.BackupsContainsDesktop"));
            if (backup.ContainsDesktopMoveHistory) includes.Add(GetString("Loc.BackupsContainsDesktopMoves"));
            if (backup.ContainsFileReferences) includes.Add(GetString("Loc.BackupsContainsFileReferences"));
            BackupDetailsIncludesText.Text = includes.Count == 0
                ? "-"
                : string.Join(Environment.NewLine, includes.Select(item => "• " + item));
        }

        private static string GetBackupKindDisplayName(string? backupKind)
        {
            return (backupKind ?? string.Empty).ToLowerInvariant() switch
            {
                "manual" => GetString("Loc.BackupsTypeManual"),
                "auto-sort" => GetString("Loc.BackupsTypeAutoSort"),
                "critical" => GetString("Loc.BackupsTypeCritical"),
                _ => GetString("Loc.BackupsTypeUpdate")
            };
        }

        private async void CreateManualBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_isManualBackupInProgress || _desktopAutoSortInProgress)
            {
                return;
            }

            _isManualBackupInProgress = true;
            CreateManualBackupButton.IsEnabled = false;
            UpdateBackupDetails(BackupsList.SelectedItem as UpdateBackupInfo);
            BackupOperationStatusText.Text = GetString("Loc.BackupsCreating");
            SaveSettingsImmediate();
            string manualReason = GetString("Loc.BackupsReasonManual");
            string manualDisplayName = GetString("Loc.BackupsNameManual");

            try
            {
                var result = await Task.Run(() =>
                {
                    bool success = TryCreateManagedBackup(
                        "manual",
                        manualReason,
                        manualDisplayName,
                        out string archivePath,
                        out string errorMessage);
                    return (success, archivePath, errorMessage);
                });

                if (!result.success)
                {
                    BackupOperationStatusText.Text = string.Format(
                        GetString("Loc.BackupsCreateFailed"),
                        result.errorMessage);
                    return;
                }

                BackupOperationStatusText.Text = GetString("Loc.BackupsCreated");
                RefreshBackupsTab(result.archivePath);
            }
            finally
            {
                _isManualBackupInProgress = false;
                UpdateBackupDetails(BackupsList.SelectedItem as UpdateBackupInfo);
            }
        }

        private void RefreshBackups_Click(object sender, RoutedEventArgs e)
        {
            RefreshBackupsTab((BackupsList.SelectedItem as UpdateBackupInfo)?.ArchivePath);
        }

        private void BackupsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateBackupDetails(BackupsList.SelectedItem as UpdateBackupInfo);
        }

        private void RestoreSelectedBackup_Click(object sender, RoutedEventArgs e)
        {
            if (BackupsList.SelectedItem is UpdateBackupInfo backup)
            {
                RestoreBackup(backup);
            }
        }

        private void RestoreBackup(UpdateBackupInfo backup)
        {
            if (_desktopAutoSortInProgress || _isManualBackupInProgress || !backup.CanRestore)
            {
                return;
            }

            string displayName = string.IsNullOrWhiteSpace(backup.DisplayName)
                ? backup.ArchiveFileName
                : backup.DisplayName;
            MessageBoxResult confirmRestore = System.Windows.MessageBox.Show(
                string.Format(GetString("Loc.BackupRestoreConfirm"), displayName),
                GetString("Loc.BackupRestoreConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmRestore != MessageBoxResult.Yes)
            {
                return;
            }

            SaveSettingsImmediate();
            if (!TryCreateManagedBackup(
                "critical",
                GetString("Loc.BackupsReasonBeforeRestore"),
                GetString("Loc.BackupsNameBeforeRestore"),
                out _,
                out string safetyError,
                preserveArchivePath: backup.ArchivePath))
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.BackupsSafetyFailed"), safetyError),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (!TryStartBackupRestoreScript(backup, out string errorMessage))
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgBackupRestoreFailed"), errorMessage),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            ShutdownForUpdateInstall();
        }

        private void DeleteSelectedBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_desktopAutoSortInProgress || _isManualBackupInProgress) return;
            if (BackupsList.SelectedItem is not UpdateBackupInfo backup)
            {
                return;
            }

            MessageBoxResult confirm = System.Windows.MessageBox.Show(
                string.Format(GetString("Loc.BackupsDeleteConfirm"), backup.DisplayName),
                GetString("Loc.BackupsDeleteTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                lock (BackupRetention.SyncRoot) { File.Delete(backup.ArchivePath); }
                BackupOperationStatusText.Text = GetString("Loc.BackupsDeleted");
                RefreshBackupsTab();
            }
            catch (Exception ex)
            {
                BackupOperationStatusText.Text = string.Format(GetString("Loc.BackupsDeleteFailed"), ex.Message);
            }
        }

        private void OpenBackupsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(UpdateBackupsDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = UpdateBackupsDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                BackupOperationStatusText.Text = ex.Message;
            }
        }
    }
}
