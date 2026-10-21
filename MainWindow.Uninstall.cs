using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopPlus
{
    public partial class MainWindow
    {
        internal static async Task<int> RunUninstallBackupRestoreAsync(string language)
        {
            CurrentLanguageCode = language;
            foreach (var pair in LocalizationData[language])
            {
                System.Windows.Application.Current.Resources[pair.Key] = pair.Value;
            }

            try
            {
                var backups = (await Task.Run(() => LoadAvailableUpdateBackups()))
                    .Where(backup => backup.CanRestore && (backup.ContainsSettingsSnapshot || backup.ContainsCustomLanguages ||
                        backup.ContainsAutoSortStorage || backup.ContainsDesktopSnapshot ||
                        backup.ContainsDesktopMoveHistory || backup.ContainsFileReferences))
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
                bool created = TryCreateManagedBackup(
                    "critical", GetString("Loc.BackupsReasonBeforeRestore"), GetString("Loc.BackupsNameBeforeRestore"),
                    out _, out string error, preserveArchivePath: backup.ArchivePath);
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
                    EncodeBackupRestoreScript(script),
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

    }
}
