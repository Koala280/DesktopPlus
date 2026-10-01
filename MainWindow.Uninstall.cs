using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopPlus
{
    public partial class MainWindow
    {
        internal static int RunUninstallBackupRestore(string language)
        {
            CurrentLanguageCode = language;
            foreach (var pair in LocalizationData[language])
            {
                System.Windows.Application.Current.Resources[pair.Key] = pair.Value;
            }

            try
            {
                var backups = LoadAvailableUpdateBackups()
                    .Where(backup => backup.ContainsSettingsSnapshot || backup.ContainsCustomLanguages ||
                        backup.ContainsAutoSortStorage || backup.ContainsDesktopSnapshot ||
                        backup.ContainsDesktopMoveHistory)
                    .OrderByDescending(backup => backup.CreatedUtc)
                    .ToList();
                if (backups.Count == 0)
                {
                    System.Windows.MessageBox.Show(GetString("Loc.UninstallBackupEmpty"),
                        GetString("Loc.UninstallBackupTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return 2;
                }

                var dialog = new UninstallBackupWindow(backups);
                return dialog.ShowDialog() == true ? 0 : 2;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgBackupRestoreFailed"), ex.Message),
                    GetString("Loc.MsgError"), MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        internal static async Task RestoreBackupBeforeUninstallAsync(UpdateBackupInfo backup)
        {
            var safetyResult = await Task.Run(() =>
            {
                IReadOnlyList<string> desktopItems = ReadDesktopItemsForUninstallSafety(
                    backup.ArchivePath, GetDesktopDirectoryPaths());
                bool created = TryCreateManagedBackup(
                    "critical", GetString("Loc.BackupsReasonBeforeRestore"), GetString("Loc.BackupsNameBeforeRestore"),
                    includeApplication: false, captureAutoSortStorage: !backup.ContainsDesktopMoveHistory,
                    desktopItems, out _, out string error, preserveArchivePath: backup.ArchivePath);
                return (created, error);
            });
            if (!safetyResult.created)
            {
                throw new InvalidOperationException(string.Format(GetString("Loc.BackupsSafetyFailed"), safetyResult.error));
            }

            string script = BuildBackupRestoreScript(backup, Array.Empty<string>(), restoreBeforeUninstall: true);
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Restore helper process could not be started.");
            Task<string> errorOutput = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string errorMessage = await errorOutput;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(errorMessage.Trim());
            }
        }

        internal static IReadOnlyList<string> ReadDesktopItemsForUninstallSafety(
            string archivePath, IReadOnlyList<string> desktopRoots)
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            if (!TryReadUpdateBackupManifest(archive, out UpdateBackupManifest manifest))
            {
                return Array.Empty<string>();
            }

            var roots = desktopRoots.Select(root => Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (var item in manifest.DesktopItems ?? new List<DesktopBackupItemManifest>())
            {
                if (string.IsNullOrWhiteSpace(item.OriginalPath)) continue;
                string originalPath = Path.GetFullPath(item.OriginalPath);
                if (!roots.Contains(Path.GetDirectoryName(originalPath) ?? string.Empty))
                {
                    throw new InvalidDataException("Invalid desktop item in backup manifest.");
                }
                paths.Add(originalPath);
            }
            return paths;
        }
    }
}
