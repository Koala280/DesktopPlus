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
        private const int MaxAutomaticBackupsPerKind = 10;
        private bool _isManualBackupInProgress;

        private static bool TryCreateManagedBackup(
            string backupKind,
            string reason,
            string displayName,
            bool includeApplication,
            bool captureAutoSortStorage,
            IReadOnlyCollection<string>? desktopItems,
            out string backupArchivePath,
            out string errorMessage)
        {
            backupArchivePath = string.Empty;
            errorMessage = string.Empty;

            string normalizedKind = string.IsNullOrWhiteSpace(backupKind)
                ? "manual"
                : backupKind.Trim().ToLowerInvariant();
            string currentVersion = NormalizeVersionToken(GetInstalledVersionText());
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                currentVersion = "current";
            }

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string archiveFileName =
                $"DesktopPlus-backup-{SanitizeVersionTokenForFileName(normalizedKind)}-{timestamp}.zip";
            string archivePath = Path.Combine(UpdateBackupsDirectory, archiveFileName);
            string stagingDirectory = Path.Combine(UpdateBackupsDirectory, $"staging-{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(UpdateBackupsDirectory);
                Directory.CreateDirectory(stagingDirectory);

                var includedEntries = new List<string>();
                var desktopManifestItems = new List<DesktopBackupItemManifest>();
                string? installDirectory = GetCurrentInstallDirectory();

                if (includeApplication &&
                    !string.IsNullOrWhiteSpace(installDirectory) &&
                    Directory.Exists(installDirectory))
                {
                    CopyDirectoryRecursive(installDirectory, Path.Combine(stagingDirectory, "app"));
                    includedEntries.Add(installDirectory);
                }

                if (File.Exists(settingsFilePath))
                {
                    string settingsTargetDirectory = Path.Combine(stagingDirectory, "user-data", "roaming");
                    Directory.CreateDirectory(settingsTargetDirectory);
                    File.Copy(
                        settingsFilePath,
                        Path.Combine(settingsTargetDirectory, Path.GetFileName(settingsFilePath)),
                        overwrite: true);
                    includedEntries.Add(settingsFilePath);
                }

                if (Directory.Exists(CustomLanguageFolderPath))
                {
                    string languageTarget = Path.Combine(
                        stagingDirectory,
                        "user-data",
                        "roaming",
                        "DesktopPlus",
                        "Languages");
                    CopyDirectoryRecursive(CustomLanguageFolderPath, languageTarget);
                    includedEntries.Add(CustomLanguageFolderPath);
                }

                string autoSortStorageRootPath = GetAutoSortStorageRootPathForBackup();
                if (captureAutoSortStorage && Directory.Exists(autoSortStorageRootPath))
                {
                    CopyDirectoryRecursive(
                        autoSortStorageRootPath,
                        Path.Combine(stagingDirectory, "user-data", "local", "AutoSortStorage"));
                    includedEntries.Add(autoSortStorageRootPath);
                }

                int desktopItemIndex = 0;
                foreach (string sourcePath in (desktopItems ?? Array.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    bool isDirectory = Directory.Exists(sourcePath);
                    bool isFile = File.Exists(sourcePath);
                    if (!isDirectory && !isFile)
                    {
                        continue;
                    }

                    string itemName = Path.GetFileName(
                        sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(itemName))
                    {
                        itemName = $"item-{desktopItemIndex:D4}";
                    }

                    string archiveRelativePath = Path.Combine(
                        "user-data",
                        "desktop-items",
                        desktopItemIndex.ToString("D4"),
                        itemName);
                    string targetPath = Path.Combine(stagingDirectory, archiveRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                    if (isDirectory)
                    {
                        CopyDirectoryRecursive(sourcePath, targetPath);
                    }
                    else
                    {
                        File.Copy(sourcePath, targetPath, overwrite: true);
                    }

                    desktopManifestItems.Add(new DesktopBackupItemManifest
                    {
                        OriginalPath = Path.GetFullPath(sourcePath),
                        ArchiveRelativePath = archiveRelativePath.Replace('\\', '/')
                    });
                    includedEntries.Add(sourcePath);
                    desktopItemIndex++;
                }

                var manifest = new UpdateBackupManifest
                {
                    SchemaVersion = 2,
                    BackupKind = normalizedKind,
                    Reason = reason ?? string.Empty,
                    DisplayName = displayName ?? string.Empty,
                    CurrentVersion = currentVersion,
                    TargetVersion = string.Empty,
                    CreatedUtc = DateTime.UtcNow.ToString("O"),
                    SourceExecutablePath = Environment.ProcessPath ?? string.Empty,
                    SourceInstallDirectory = installDirectory ?? string.Empty,
                    CapturedAutoSortStorage = captureAutoSortStorage,
                    IncludedEntries = includedEntries,
                    DesktopItems = desktopManifestItems
                };

                File.WriteAllText(
                    Path.Combine(stagingDirectory, "manifest.json"),
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(
                    Path.Combine(stagingDirectory, "RESTORE.txt"),
                    BuildUpdateBackupRestoreInstructions(archiveFileName));

                TryDeleteFile(archivePath);
                ZipFile.CreateFromDirectory(
                    stagingDirectory,
                    archivePath,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false);

                if (!string.Equals(normalizedKind, "manual", StringComparison.OrdinalIgnoreCase))
                {
                    PruneManagedBackups(normalizedKind, archivePath);
                }

                backupArchivePath = archivePath;
                Debug.WriteLine($"Created {normalizedKind} backup: {archivePath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create {normalizedKind} backup: {ex}");
                errorMessage = ex.Message;
                TryDeleteFile(archivePath);
                return false;
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }

        private static void PruneManagedBackups(string backupKind, string keepArchivePath)
        {
            try
            {
                if (!Directory.Exists(UpdateBackupsDirectory))
                {
                    return;
                }

                string pattern = $"DesktopPlus-backup-{backupKind}-*.zip";
                foreach (FileInfo backup in Directory
                    .EnumerateFiles(UpdateBackupsDirectory, pattern, SearchOption.TopDirectoryOnly)
                    .Where(path => !string.Equals(path, keepArchivePath, StringComparison.OrdinalIgnoreCase))
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .Skip(Math.Max(0, MaxAutomaticBackupsPerKind - 1)))
                {
                    TryDeleteFile(backup.FullName);
                }
            }
            catch
            {
            }
        }

        private bool EnsureCriticalBackup(string reason, bool captureAutoSortStorage = false)
        {
            SaveSettingsImmediate();
            bool created = TryCreateManagedBackup(
                "critical",
                reason,
                reason,
                includeApplication: false,
                captureAutoSortStorage,
                desktopItems: null,
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

        private bool TryCreateAutoSortBackup(
            IReadOnlyCollection<string> desktopItems,
            out string errorMessage)
        {
            SaveSettingsImmediate();
            return TryCreateManagedBackup(
                "auto-sort",
                GetString("Loc.BackupsReasonAutoSort"),
                GetString("Loc.BackupsNameBeforeAutoSort"),
                includeApplication: false,
                captureAutoSortStorage: true,
                desktopItems,
                out _,
                out errorMessage);
        }

        private void RefreshBackupsTab(string? preferredArchivePath = null)
        {
            if (BackupsList == null)
            {
                return;
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
            BackupRestoreSelectedButton.IsEnabled = hasSelection;
            BackupDeleteSelectedButton.IsEnabled = hasSelection;
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
            if (_isManualBackupInProgress)
            {
                return;
            }

            _isManualBackupInProgress = true;
            CreateManualBackupButton.IsEnabled = false;
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
                        includeApplication: true,
                        captureAutoSortStorage: true,
                        desktopItems: null,
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
                CreateManualBackupButton.IsEnabled = true;
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

            if (!TryCreateManagedBackup(
                "critical",
                GetString("Loc.BackupsReasonBeforeRestore"),
                GetString("Loc.BackupsNameBeforeRestore"),
                includeApplication: true,
                captureAutoSortStorage: true,
                desktopItems: null,
                out _,
                out string safetyError))
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
                File.Delete(backup.ArchivePath);
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
