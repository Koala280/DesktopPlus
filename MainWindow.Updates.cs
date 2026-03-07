using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/Koala280/DesktopPlus/releases/latest";
        private const string ReleasesPageUrl = "https://github.com/Koala280/DesktopPlus/releases";
        private static readonly string UpdatesRootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopPlus",
            "Updates");
        private static readonly string UpdateBackupsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopPlus",
            "Backups");
        private static readonly string PendingUpdateInfoPath = Path.Combine(UpdatesRootDirectory, "pending-update.json");
        private static readonly Regex VersionPrefixRegex = new Regex(@"^\d+(?:\.\d+){0,3}", RegexOptions.Compiled);
        private static readonly HttpClient UpdateHttpClient = CreateUpdateRequestHttpClient();
        private static readonly HttpClient UpdateDownloadHttpClient = CreateUpdateDownloadHttpClient();
        private static readonly TimeSpan AutomaticUpdateCheckDelay = TimeSpan.FromMinutes(10);
        private const int MaxUpdateBackupsToKeep = 5;
        private bool _autoCheckUpdates = false;
        private bool _isUpdateCheckInProgress = false;
        private bool _isAutomaticUpdateRoutineInProgress = false;
        private bool _isUpdateDownloadInProgress = false;
        private string _updateDownloadVersionInProgress = string.Empty;
        private string _manualUpdateStatusText = string.Empty;

        private static bool IsDevelopmentBuildForUpdates
        {
            get
            {
#if DEBUG
                return true;
#else
                string assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? string.Empty;
                return assemblyName.EndsWith("Dev", StringComparison.OrdinalIgnoreCase);
#endif
            }
        }

        private static HttpClient CreateUpdateRequestHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopPlus/1.0 (+https://github.com/Koala280/DesktopPlus)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static HttpClient CreateUpdateDownloadHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(15)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopPlus/1.0 (+https://github.com/Koala280/DesktopPlus)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
            return client;
        }

        private sealed class LatestReleaseInfo
        {
            public string TagName { get; init; } = string.Empty;
            public string HtmlUrl { get; init; } = ReleasesPageUrl;
            public string InstallerAssetName { get; init; } = string.Empty;
            public string InstallerDownloadUrl { get; init; } = string.Empty;
        }

        private sealed class PendingUpdateInfo
        {
            public string Version { get; set; } = string.Empty;
            public string InstallerPath { get; set; } = string.Empty;
            public DateTime DownloadedUtc { get; set; } = DateTime.UtcNow;
        }

        private sealed class UpdateBackupManifest
        {
            public string CurrentVersion { get; set; } = string.Empty;
            public string TargetVersion { get; set; } = string.Empty;
            public string CreatedUtc { get; set; } = string.Empty;
            public string SourceExecutablePath { get; set; } = string.Empty;
            public string SourceInstallDirectory { get; set; } = string.Empty;
            public List<string> IncludedEntries { get; set; } = new List<string>();
        }

        private static string GetInstalledVersionText()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                int metadataIndex = informationalVersion.IndexOf('+');
                return metadataIndex >= 0
                    ? informationalVersion[..metadataIndex]
                    : informationalVersion;
            }

            var version = assembly.GetName().Version;
            if (version != null)
            {
                int patch = version.Build >= 0 ? version.Build : 0;
                return $"{version.Major}.{version.Minor}.{patch}";
            }

            return "0.0.0";
        }

        private static string NormalizeVersionToken(string rawVersion)
        {
            if (string.IsNullOrWhiteSpace(rawVersion))
            {
                return string.Empty;
            }

            string normalized = rawVersion.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[1..];
            }

            var match = VersionPrefixRegex.Match(normalized);
            return match.Success ? match.Value : normalized;
        }

        private static bool TryParseComparableVersion(string rawVersion, out Version version)
        {
            version = new Version(0, 0, 0, 0);
            string normalized = NormalizeVersionToken(rawVersion);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (!Version.TryParse(normalized, out var parsedVersion) || parsedVersion == null)
            {
                return false;
            }

            version = parsedVersion;
            return true;
        }

        private static bool IsRemoteVersionNewer(string currentVersionText, string latestVersionText)
        {
            if (TryParseComparableVersion(currentVersionText, out var currentVersion) &&
                TryParseComparableVersion(latestVersionText, out var latestVersion))
            {
                return latestVersion > currentVersion;
            }

            return !string.Equals(
                NormalizeVersionToken(currentVersionText),
                NormalizeVersionToken(latestVersionText),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static bool TryReadPendingUpdateInfo(out PendingUpdateInfo info)
        {
            info = new PendingUpdateInfo();

            try
            {
                if (!File.Exists(PendingUpdateInfoPath))
                {
                    return false;
                }

                string json = File.ReadAllText(PendingUpdateInfoPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    TryDeleteFile(PendingUpdateInfoPath);
                    return false;
                }

                var parsed = JsonSerializer.Deserialize<PendingUpdateInfo>(json);
                if (parsed == null ||
                    string.IsNullOrWhiteSpace(parsed.Version) ||
                    string.IsNullOrWhiteSpace(parsed.InstallerPath) ||
                    !File.Exists(parsed.InstallerPath))
                {
                    TryDeleteFile(PendingUpdateInfoPath);
                    return false;
                }

                info = parsed;
                return true;
            }
            catch
            {
                TryDeleteFile(PendingUpdateInfoPath);
                return false;
            }
        }

        private static void PersistPendingUpdateInfo(PendingUpdateInfo info)
        {
            Directory.CreateDirectory(UpdatesRootDirectory);
            string json = JsonSerializer.Serialize(
                info,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            File.WriteAllText(PendingUpdateInfoPath, json);
        }

        private static void CleanupOldDownloadedInstallers(string keepInstallerPath)
        {
            try
            {
                if (!Directory.Exists(UpdatesRootDirectory))
                {
                    return;
                }

                foreach (string installerPath in Directory.EnumerateFiles(UpdatesRootDirectory, "DesktopPlus-Setup-*.exe"))
                {
                    if (string.Equals(installerPath, keepInstallerPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TryDeleteFile(installerPath);
                }
            }
            catch
            {
            }
        }

        private static string SanitizeVersionTokenForFileName(string versionToken)
        {
            if (string.IsNullOrWhiteSpace(versionToken))
            {
                return "latest";
            }

            string sanitized = versionToken;
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(invalidChar, '-');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "latest" : sanitized;
        }

        private static string? GetCurrentInstallDirectory()
        {
            string processPath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return null;
            }

            try
            {
                string fullProcessPath = Path.GetFullPath(processPath);
                string? installDirectory = Path.GetDirectoryName(fullProcessPath);
                return string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory)
                    ? null
                    : installDirectory;
            }
            catch
            {
                return null;
            }
        }

        private static string GetAutoSortStorageRootPathForBackup()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopPlus",
                "AutoSortStorage");
        }

        private static void CopyDirectoryRecursive(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);

            foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativeDirectory = Path.GetRelativePath(sourceDirectory, directory);
                Directory.CreateDirectory(Path.Combine(targetDirectory, relativeDirectory));
            }

            foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativeFile = Path.GetRelativePath(sourceDirectory, file);
                string targetFile = Path.Combine(targetDirectory, relativeFile);
                string? targetFileDirectory = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrWhiteSpace(targetFileDirectory))
                {
                    Directory.CreateDirectory(targetFileDirectory);
                }

                File.Copy(file, targetFile, overwrite: true);
            }
        }

        private static string BuildUpdateBackupRestoreInstructions(string archiveFileName)
        {
            return string.Join(Environment.NewLine, new[]
            {
                "DesktopPlus update backup",
                string.Empty,
                $"Archive: {archiveFileName}",
                string.Empty,
                "Restore steps:",
                "1. Install the DesktopPlus version you want to roll back to.",
                "2. Close DesktopPlus.",
                "3. Extract this archive to a temporary folder.",
                $"4. Restore 'user-data\\roaming\\{Path.GetFileName(settingsFilePath)}' to '%APPDATA%\\{Path.GetFileName(settingsFilePath)}'.",
                "5. Restore 'user-data\\roaming\\DesktopPlus\\' to '%APPDATA%\\DesktopPlus\\' if present.",
                "6. Restore 'user-data\\local\\AutoSortStorage\\' to '%LOCALAPPDATA%\\DesktopPlus\\AutoSortStorage\\' if present.",
                "7. Optional: the 'app\\' folder contains the pre-update program files of the previous installation."
            });
        }

        private static void PruneOldUpdateBackups(string keepArchivePath)
        {
            try
            {
                if (!Directory.Exists(UpdateBackupsDirectory))
                {
                    return;
                }

                var oldBackups = Directory
                    .EnumerateFiles(UpdateBackupsDirectory, "DesktopPlus-backup-*.zip", SearchOption.TopDirectoryOnly)
                    .Where(path => !string.Equals(path, keepArchivePath, StringComparison.OrdinalIgnoreCase))
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .Skip(Math.Max(0, MaxUpdateBackupsToKeep - 1));

                foreach (FileInfo backup in oldBackups)
                {
                    TryDeleteFile(backup.FullName);
                }
            }
            catch
            {
            }
        }

        private static bool TryCreatePreUpdateBackup(
            string targetVersion,
            out string backupArchivePath,
            out string errorMessage)
        {
            backupArchivePath = string.Empty;
            errorMessage = string.Empty;

            string currentVersion = NormalizeVersionToken(GetInstalledVersionText());
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                currentVersion = "current";
            }

            string normalizedTargetVersion = NormalizeVersionToken(targetVersion);
            if (string.IsNullOrWhiteSpace(normalizedTargetVersion))
            {
                normalizedTargetVersion = "next";
            }

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string archiveFileName =
                $"DesktopPlus-backup-{SanitizeVersionTokenForFileName(currentVersion)}-before-{SanitizeVersionTokenForFileName(normalizedTargetVersion)}-{timestamp}.zip";
            string archivePath = Path.Combine(UpdateBackupsDirectory, archiveFileName);
            string stagingDirectory = Path.Combine(UpdateBackupsDirectory, $"staging-{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(UpdateBackupsDirectory);
                Directory.CreateDirectory(stagingDirectory);

                var includedEntries = new List<string>();

                string? installDirectory = GetCurrentInstallDirectory();
                if (!string.IsNullOrWhiteSpace(installDirectory) && Directory.Exists(installDirectory))
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
                    string roamingDesktopPlusDirectory = Path.Combine(stagingDirectory, "user-data", "roaming", "DesktopPlus");
                    CopyDirectoryRecursive(CustomLanguageFolderPath, Path.Combine(roamingDesktopPlusDirectory, "Languages"));
                    includedEntries.Add(CustomLanguageFolderPath);
                }

                string autoSortStorageRootPath = GetAutoSortStorageRootPathForBackup();
                if (Directory.Exists(autoSortStorageRootPath))
                {
                    CopyDirectoryRecursive(
                        autoSortStorageRootPath,
                        Path.Combine(stagingDirectory, "user-data", "local", "AutoSortStorage"));
                    includedEntries.Add(autoSortStorageRootPath);
                }

                var manifest = new UpdateBackupManifest
                {
                    CurrentVersion = currentVersion,
                    TargetVersion = normalizedTargetVersion,
                    CreatedUtc = DateTime.UtcNow.ToString("O"),
                    SourceExecutablePath = Environment.ProcessPath ?? string.Empty,
                    SourceInstallDirectory = installDirectory ?? string.Empty,
                    IncludedEntries = includedEntries
                };

                File.WriteAllText(
                    Path.Combine(stagingDirectory, "manifest.json"),
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(
                    Path.Combine(stagingDirectory, "RESTORE.txt"),
                    BuildUpdateBackupRestoreInstructions(archiveFileName));

                TryDeleteFile(archivePath);
                ZipFile.CreateFromDirectory(stagingDirectory, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
                PruneOldUpdateBackups(archivePath);
                backupArchivePath = archivePath;
                Debug.WriteLine($"Created pre-update backup: {archivePath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create pre-update backup: {ex}");
                errorMessage = ex.Message;
                TryDeleteFile(archivePath);
                return false;
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }

        private static string EscapePowerShellSingleQuotedLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string BuildPowerShellStringArrayLiteral(IEnumerable<string> values)
        {
            return string.Join(
                ",",
                values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => $"'{EscapePowerShellSingleQuotedLiteral(value)}'"));
        }

        private static bool ZipArchiveContainsEntryPrefix(ZipArchive archive, string prefix)
        {
            if (archive == null || string.IsNullOrWhiteSpace(prefix))
            {
                return false;
            }

            string normalizedPrefix = prefix.Replace('\\', '/').TrimStart('/');
            return archive.Entries.Any(entry =>
                !string.IsNullOrWhiteSpace(entry.FullName) &&
                entry.FullName.Replace('\\', '/').StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryReadUpdateBackupManifest(
            ZipArchive archive,
            out UpdateBackupManifest manifest)
        {
            manifest = new UpdateBackupManifest();

            if (archive == null)
            {
                return false;
            }

            try
            {
                ZipArchiveEntry? entry = archive.GetEntry("manifest.json");
                if (entry == null)
                {
                    return false;
                }

                using Stream stream = entry.Open();
                UpdateBackupManifest? parsed = JsonSerializer.Deserialize<UpdateBackupManifest>(stream);
                if (parsed == null)
                {
                    return false;
                }

                manifest = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static IReadOnlyList<UpdateBackupInfo> LoadAvailableUpdateBackups()
        {
            var backups = new List<UpdateBackupInfo>();
            if (!Directory.Exists(UpdateBackupsDirectory))
            {
                return backups;
            }

            IEnumerable<string> backupArchives = Directory
                .EnumerateFiles(UpdateBackupsDirectory, "DesktopPlus-backup-*.zip", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc);

            foreach (string archivePath in backupArchives)
            {
                var fileInfo = new FileInfo(archivePath);
                var backup = new UpdateBackupInfo
                {
                    ArchivePath = archivePath,
                    ArchiveFileName = fileInfo.Name,
                    CreatedUtc = fileInfo.LastWriteTimeUtc,
                    FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0
                };

                try
                {
                    using ZipArchive archive = ZipFile.OpenRead(archivePath);
                    if (TryReadUpdateBackupManifest(archive, out UpdateBackupManifest manifest))
                    {
                        backup.CurrentVersion = manifest.CurrentVersion ?? string.Empty;
                        backup.TargetVersion = manifest.TargetVersion ?? string.Empty;
                        backup.SourceInstallDirectory = manifest.SourceInstallDirectory ?? string.Empty;
                        backup.SourceExecutablePath = manifest.SourceExecutablePath ?? string.Empty;

                        if (DateTime.TryParse(
                            manifest.CreatedUtc,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out DateTime createdUtc))
                        {
                            backup.CreatedUtc = createdUtc;
                        }
                    }

                    backup.ContainsAppSnapshot = ZipArchiveContainsEntryPrefix(archive, "app/");
                    backup.ContainsSettingsSnapshot = ZipArchiveContainsEntryPrefix(archive, "user-data/roaming/DesktopPlus_Settings.json");
                    backup.ContainsCustomLanguages = ZipArchiveContainsEntryPrefix(archive, "user-data/roaming/DesktopPlus/Languages/");
                    backup.ContainsAutoSortStorage = ZipArchiveContainsEntryPrefix(archive, "user-data/local/AutoSortStorage/");
                    backups.Add(backup);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Skipping unreadable backup archive '{archivePath}': {ex}");
                }
            }

            return backups;
        }

        private static IReadOnlyList<string> BuildBackupRestoreLaunchCandidates(UpdateBackupInfo backup)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                try
                {
                    string fullPath = Path.GetFullPath(path);
                    if (seen.Add(fullPath))
                    {
                        candidates.Add(fullPath);
                    }
                }
                catch
                {
                }
            }

            string currentProcessPath = Environment.ProcessPath ?? string.Empty;
            string? currentInstallDirectory = GetCurrentInstallDirectory();
            AddCandidate(currentProcessPath);

            if (!string.IsNullOrWhiteSpace(currentInstallDirectory) &&
                !string.IsNullOrWhiteSpace(currentProcessPath))
            {
                AddCandidate(Path.Combine(currentInstallDirectory, Path.GetFileName(currentProcessPath)));
            }

            if (!string.IsNullOrWhiteSpace(currentInstallDirectory) &&
                !string.IsNullOrWhiteSpace(backup.SourceExecutablePath))
            {
                AddCandidate(Path.Combine(currentInstallDirectory, Path.GetFileName(backup.SourceExecutablePath)));
            }

            if (!string.IsNullOrWhiteSpace(currentInstallDirectory))
            {
                AddCandidate(Path.Combine(currentInstallDirectory, "DesktopPlus.exe"));
                AddCandidate(Path.Combine(currentInstallDirectory, "DesktopPlusDev.exe"));
            }

            if (!string.IsNullOrWhiteSpace(backup.SourceExecutablePath))
            {
                AddCandidate(backup.SourceExecutablePath);
            }

            return candidates;
        }

        private static string BuildBackupRestoreScript(
            UpdateBackupInfo backup,
            IReadOnlyList<string> launchCandidates)
        {
            string currentInstallDirectory = GetCurrentInstallDirectory()
                ?? backup.SourceInstallDirectory
                ?? string.Empty;
            string targetLanguagesDirectory = CustomLanguageFolderPath;
            string targetAutoSortDirectory = GetAutoSortStorageRootPathForBackup();
            string targetSettingsPath = settingsFilePath;
            string errorTitle = GetString("Loc.MsgError");
            string launchMissingMessage = GetString("Loc.MsgBackupRestoreLaunchMissing");
            string launchTargetsLiteral = BuildPowerShellStringArrayLiteral(launchCandidates);

            var script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine("Add-Type -AssemblyName PresentationFramework | Out-Null");
            script.AppendLine($"$sourceProcessId = {Environment.ProcessId}");
            script.AppendLine($"$archivePath = '{EscapePowerShellSingleQuotedLiteral(backup.ArchivePath)}'");
            script.AppendLine($"$targetInstallDir = '{EscapePowerShellSingleQuotedLiteral(currentInstallDirectory)}'");
            script.AppendLine($"$settingsPath = '{EscapePowerShellSingleQuotedLiteral(targetSettingsPath)}'");
            script.AppendLine($"$targetLanguagesDir = '{EscapePowerShellSingleQuotedLiteral(targetLanguagesDirectory)}'");
            script.AppendLine($"$targetAutoSortDir = '{EscapePowerShellSingleQuotedLiteral(targetAutoSortDirectory)}'");
            script.AppendLine($"$pendingInfoPath = '{EscapePowerShellSingleQuotedLiteral(PendingUpdateInfoPath)}'");
            script.AppendLine($"$errorTitle = '{EscapePowerShellSingleQuotedLiteral(errorTitle)}'");
            script.AppendLine($"$launchMissingMessage = '{EscapePowerShellSingleQuotedLiteral(launchMissingMessage)}'");
            script.AppendLine($"$launchTargets = @({launchTargetsLiteral})");
            script.AppendLine("$restoreRoot = Join-Path $env:LOCALAPPDATA ('DesktopPlus\\\\Backups\\\\restore-' + [guid]::NewGuid().ToString('N'))");
            script.AppendLine("$extractPath = Join-Path $restoreRoot 'extract'");
            script.AppendLine("function Show-RestoreError([string]$message) { [System.Windows.MessageBox]::Show($message, $errorTitle, 'OK', 'Error') | Out-Null }");
            script.AppendLine("function Ensure-Directory([string]$path) { if ([string]::IsNullOrWhiteSpace($path)) { return } if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null } }");
            script.AppendLine("function Reset-DirectoryContents([string]$path) { if ([string]::IsNullOrWhiteSpace($path)) { throw 'Restore target path is missing.' } Ensure-Directory $path; Get-ChildItem -LiteralPath $path -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force }");
            script.AppendLine("function Copy-DirectoryContents([string]$source, [string]$destination) { if (-not (Test-Path -LiteralPath $source)) { return } Ensure-Directory $destination; foreach ($child in Get-ChildItem -LiteralPath $source -Force) { Copy-Item -LiteralPath $child.FullName -Destination (Join-Path $destination $child.Name) -Recurse -Force } }");
            script.AppendLine("try { Wait-Process -Id $sourceProcessId -ErrorAction SilentlyContinue } catch { }");
            script.AppendLine("Start-Sleep -Milliseconds 900");
            script.AppendLine("try {");
            script.AppendLine("  Ensure-Directory $restoreRoot");
            script.AppendLine("  Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force");
            script.AppendLine("  $appSource = Join-Path $extractPath 'app'");
            script.AppendLine("  if (Test-Path -LiteralPath $appSource) { Reset-DirectoryContents $targetInstallDir; Copy-DirectoryContents $appSource $targetInstallDir }");
            script.AppendLine("  $settingsSource = Join-Path $extractPath 'user-data\\roaming\\DesktopPlus_Settings.json'");
            script.AppendLine("  if (Test-Path -LiteralPath $settingsSource) { Ensure-Directory (Split-Path -Path $settingsPath -Parent); Copy-Item -LiteralPath $settingsSource -Destination $settingsPath -Force }");
            script.AppendLine("  $languagesSource = Join-Path $extractPath 'user-data\\roaming\\DesktopPlus\\Languages'");
            script.AppendLine("  if (Test-Path -LiteralPath $languagesSource) { if (Test-Path -LiteralPath $targetLanguagesDir) { Remove-Item -LiteralPath $targetLanguagesDir -Recurse -Force }; Ensure-Directory (Split-Path -Path $targetLanguagesDir -Parent); Copy-DirectoryContents $languagesSource $targetLanguagesDir }");
            script.AppendLine("  $autoSortSource = Join-Path $extractPath 'user-data\\local\\AutoSortStorage'");
            script.AppendLine("  if (Test-Path -LiteralPath $autoSortSource) { if (Test-Path -LiteralPath $targetAutoSortDir) { Remove-Item -LiteralPath $targetAutoSortDir -Recurse -Force }; Ensure-Directory (Split-Path -Path $targetAutoSortDir -Parent); Copy-DirectoryContents $autoSortSource $targetAutoSortDir }");
            script.AppendLine("  if (Test-Path -LiteralPath $pendingInfoPath) { Remove-Item -LiteralPath $pendingInfoPath -Force -ErrorAction SilentlyContinue }");
            script.AppendLine("} catch {");
            script.AppendLine("  Show-RestoreError($_.Exception.Message)");
            script.AppendLine("  exit 1");
            script.AppendLine("} finally {");
            script.AppendLine("  try { if (Test-Path -LiteralPath $extractPath) { Remove-Item -LiteralPath $extractPath -Recurse -Force } } catch { }");
            script.AppendLine("}");
            script.AppendLine("$launched = $false");
            script.AppendLine("foreach ($candidate in $launchTargets) {");
            script.AppendLine("  if ([string]::IsNullOrWhiteSpace($candidate) -or -not (Test-Path -LiteralPath $candidate)) { continue }");
            script.AppendLine("  try { Start-Process -FilePath $candidate -ArgumentList '--startup' | Out-Null; $launched = $true; break } catch { }");
            script.AppendLine("}");
            script.AppendLine("if (-not $launched -and -not [string]::IsNullOrWhiteSpace($targetInstallDir) -and (Test-Path -LiteralPath $targetInstallDir)) {");
            script.AppendLine("  $fallback = Get-ChildItem -LiteralPath $targetInstallDir -Filter 'DesktopPlus*.exe' -File -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1");
            script.AppendLine("  if ($fallback) { try { Start-Process -FilePath $fallback.FullName -ArgumentList '--startup' | Out-Null; $launched = $true } catch { } }");
            script.AppendLine("}");
            script.AppendLine("if (-not $launched) { Show-RestoreError($launchMissingMessage) }");
            script.AppendLine("try { if (Test-Path -LiteralPath $restoreRoot) { Remove-Item -LiteralPath $restoreRoot -Recurse -Force } } catch { }");
            return script.ToString();
        }

        private static bool TryStartBackupRestoreScript(
            UpdateBackupInfo backup,
            out string errorMessage)
        {
            errorMessage = string.Empty;

            if (backup == null)
            {
                errorMessage = "Backup is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(backup.ArchivePath) || !File.Exists(backup.ArchivePath))
            {
                errorMessage = "Backup archive was not found.";
                return false;
            }

            try
            {
                string script = BuildBackupRestoreScript(
                    backup,
                    BuildBackupRestoreLaunchCandidates(backup));
                string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedScript}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                if (process == null)
                {
                    errorMessage = "Restore helper process could not be started.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static async Task<LatestReleaseInfo> FetchLatestReleaseInfoAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            using var response = await UpdateHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
            }

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = document.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp)
                ? tagProp.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                throw new InvalidOperationException("Latest release response did not include a tag_name.");
            }

            string htmlUrl = root.TryGetProperty("html_url", out var htmlProp)
                ? htmlProp.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(htmlUrl))
            {
                htmlUrl = ReleasesPageUrl;
            }

            string installerAssetName = string.Empty;
            string installerDownloadUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assetsProp) &&
                assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nameProp)
                        ? nameProp.GetString() ?? string.Empty
                        : string.Empty;
                    string downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp)
                        ? urlProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        continue;
                    }

                    bool isPreferredInstaller = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        name.StartsWith("DesktopPlus-Setup-", StringComparison.OrdinalIgnoreCase);
                    if (isPreferredInstaller)
                    {
                        installerAssetName = name;
                        installerDownloadUrl = downloadUrl;
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(installerDownloadUrl) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerAssetName = name;
                        installerDownloadUrl = downloadUrl;
                    }
                }
            }

            return new LatestReleaseInfo
            {
                TagName = tagName,
                HtmlUrl = htmlUrl,
                InstallerAssetName = installerAssetName,
                InstallerDownloadUrl = installerDownloadUrl
            };
        }

        private async Task EnsureLatestInstallerDownloadedAsync(LatestReleaseInfo latestRelease, string latestVersionText)
        {
            if (latestRelease == null ||
                string.IsNullOrWhiteSpace(latestRelease.InstallerDownloadUrl) ||
                string.IsNullOrWhiteSpace(latestVersionText))
            {
                return;
            }

            string normalizedTargetVersion = NormalizeVersionToken(latestVersionText);
            if (string.IsNullOrWhiteSpace(normalizedTargetVersion))
            {
                return;
            }

            if (TryReadPendingUpdateInfo(out var pendingInfo) &&
                string.Equals(
                    NormalizeVersionToken(pendingInfo.Version),
                    normalizedTargetVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_isUpdateDownloadInProgress &&
                string.Equals(
                    NormalizeVersionToken(_updateDownloadVersionInProgress),
                    normalizedTargetVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _isUpdateDownloadInProgress = true;
            _updateDownloadVersionInProgress = latestVersionText;

            string installerFileName = !string.IsNullOrWhiteSpace(latestRelease.InstallerAssetName)
                ? latestRelease.InstallerAssetName
                : $"DesktopPlus-Setup-{SanitizeVersionTokenForFileName(normalizedTargetVersion)}.exe";
            string targetInstallerPath = Path.Combine(UpdatesRootDirectory, installerFileName);
            string tempInstallerPath = targetInstallerPath + ".download";

            try
            {
                Directory.CreateDirectory(UpdatesRootDirectory);

                using var request = new HttpRequestMessage(HttpMethod.Get, latestRelease.InstallerDownloadUrl);
                using var response = await UpdateDownloadHttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
                }

                await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                await using (var destination = new FileStream(
                    tempInstallerPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    await source.CopyToAsync(destination).ConfigureAwait(false);
                }

                var downloadedFile = new FileInfo(tempInstallerPath);
                if (!downloadedFile.Exists || downloadedFile.Length < 200 * 1024)
                {
                    throw new InvalidOperationException("Downloaded installer is missing or unexpectedly small.");
                }

                File.Move(tempInstallerPath, targetInstallerPath, overwrite: true);

                PersistPendingUpdateInfo(new PendingUpdateInfo
                {
                    Version = normalizedTargetVersion,
                    InstallerPath = targetInstallerPath,
                    DownloadedUtc = DateTime.UtcNow
                });
                CleanupOldDownloadedInstallers(targetInstallerPath);
                ShowUpdateInstallerReadyNotification(normalizedTargetVersion);

                Debug.WriteLine($"Update installer downloaded: {targetInstallerPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Background update download failed: {ex}");
            }
            finally
            {
                TryDeleteFile(tempInstallerPath);
                _isUpdateDownloadInProgress = false;
                _updateDownloadVersionInProgress = string.Empty;
            }
        }

        private void ShowUpdateInstallerReadyNotification(string versionText)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => ShowUpdateInstallerReadyNotification(versionText)));
                return;
            }

            if (_notifyIcon == null)
            {
                return;
            }

            string normalizedVersion = NormalizeVersionToken(versionText);
            string displayVersion = string.IsNullOrWhiteSpace(normalizedVersion)
                ? versionText
                : normalizedVersion;
            string title = GetString("Loc.UpdateInstallerReadyTitle");
            string message = string.Format(GetString("Loc.UpdateInstallerReadyBody"), displayVersion);

            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(6000);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to show update ready notification: {ex}");
            }
        }

        private static bool TryOpenExternalUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private UpdateActionDialogChoice ShowUpdateActionDialog(string latestVersionText, string currentVersionText)
        {
            var dialog = new UpdateActionDialog(latestVersionText, currentVersionText)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            dialog.ShowDialog();
            return dialog.SelectedAction;
        }

        private static bool TryStartSilentInstaller(string installerPath, out Process? installerProcess)
        {
            installerProcess = null;

            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                return false;
            }

            try
            {
                installerProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    // App shutdown is handled by DesktopPlus itself after installer start.
                    // Avoid /CLOSEAPPLICATIONS here so panel windows are not marked hidden by forced close events.
                    Arguments = "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty
                });
                return installerProcess != null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start silent installer: {ex}");
                return false;
            }
        }

        private static IReadOnlyList<string> GetPostUpdateExecutableCandidates()
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(path);
                }
                catch
                {
                    return;
                }

                if (seen.Add(fullPath))
                {
                    candidates.Add(fullPath);
                }
            }

            string defaultInstalledPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "DesktopPlus",
                "DesktopPlus.exe");
            AddCandidate(defaultInstalledPath);

            string processPath = Environment.ProcessPath ?? string.Empty;
            AddCandidate(processPath);

            if (!string.IsNullOrWhiteSpace(processPath))
            {
                string? processDirectory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(processDirectory))
                {
                    AddCandidate(Path.Combine(processDirectory, "DesktopPlus.exe"));
                }
            }

            return candidates;
        }

        private static void TrySchedulePostUpdateRelaunch(
            int installerProcessId,
            int sourceProcessId,
            IReadOnlyList<string> executableCandidates)
        {
            if (installerProcessId <= 0 ||
                executableCandidates == null ||
                executableCandidates.Count == 0)
            {
                return;
            }

            static string EscapeForPowerShellSingleQuoted(string value)
            {
                return value.Replace("'", "''");
            }

            string targetArrayLiteral = string.Join(
                ",",
                executableCandidates
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => $"'{EscapeForPowerShellSingleQuoted(path)}'"));
            if (string.IsNullOrWhiteSpace(targetArrayLiteral))
            {
                return;
            }

            string relaunchScript =
                $"$installerId={installerProcessId};" +
                $"$sourceAppId={sourceProcessId};" +
                "try { Wait-Process -Id $installerId -ErrorAction SilentlyContinue } catch { };" +
                "if ($sourceAppId -gt 0) { try { Wait-Process -Id $sourceAppId -ErrorAction SilentlyContinue } catch { } };" +
                "Start-Sleep -Milliseconds 1200;" +
                $"$targets=@({targetArrayLiteral});" +
                "foreach ($target in $targets) {" +
                " if ([string]::IsNullOrWhiteSpace($target)) { continue }" +
                " if (-not (Test-Path -LiteralPath $target)) { continue }" +
                " try { Start-Process -FilePath $target -ArgumentList '--startup' | Out-Null; break } catch { }" +
                "}";

            try
            {
                string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(relaunchScript));
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedScript}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to schedule post-update relaunch: {ex}");
            }
        }

        private void SetManualUpdateStatus(string? statusText)
        {
            _manualUpdateStatusText = statusText?.Trim() ?? string.Empty;
            UpdateGeneralVersionLabel();
        }

        private void ShutdownForUpdateInstall()
        {
            _isExit = true;
            IsExiting = true;
            CloseTrayMenuWindow();
            StopDesktopAutoSortWatcher();
            _notifyIcon?.Dispose();
            SaveSettingsImmediate();
            System.Windows.Application.Current.Shutdown();
        }

        private async Task<bool> TryInstallLatestUpdateInteractivelyAsync(
            LatestReleaseInfo latestRelease,
            string latestVersionText)
        {
            if (latestRelease == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(latestRelease.InstallerDownloadUrl))
            {
                System.Windows.MessageBox.Show(
                    GetString("Loc.MsgUpdateNoInstallerAsset"),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            string normalizedLatestVersion = NormalizeVersionToken(latestVersionText);
            if (string.IsNullOrWhiteSpace(normalizedLatestVersion))
            {
                normalizedLatestVersion = latestVersionText;
            }

            SetManualUpdateStatus(string.Format(GetString("Loc.UpdateStatusDownloading"), normalizedLatestVersion));
            bool sameVersionDownloadRunning =
                _isUpdateDownloadInProgress &&
                string.Equals(
                    NormalizeVersionToken(_updateDownloadVersionInProgress),
                    NormalizeVersionToken(normalizedLatestVersion),
                    StringComparison.OrdinalIgnoreCase);

            if (sameVersionDownloadRunning)
            {
                DateTime waitUntil = DateTime.UtcNow.AddMinutes(4);
                while (_isUpdateDownloadInProgress &&
                       DateTime.UtcNow < waitUntil &&
                       string.Equals(
                           NormalizeVersionToken(_updateDownloadVersionInProgress),
                           NormalizeVersionToken(normalizedLatestVersion),
                           StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(250);
                }
            }

            bool hasInstallerForVersion =
                TryReadPendingUpdateInfo(out var existingPendingInfo) &&
                string.Equals(
                    NormalizeVersionToken(existingPendingInfo.Version),
                    NormalizeVersionToken(normalizedLatestVersion),
                    StringComparison.OrdinalIgnoreCase);

            if (!hasInstallerForVersion)
            {
                await EnsureLatestInstallerDownloadedAsync(latestRelease, normalizedLatestVersion);
            }

            if (!TryReadPendingUpdateInfo(out var pendingInfo) ||
                !string.Equals(
                    NormalizeVersionToken(pendingInfo.Version),
                    NormalizeVersionToken(normalizedLatestVersion),
                    StringComparison.OrdinalIgnoreCase))
            {
                SetManualUpdateStatus(string.Empty);
                System.Windows.MessageBox.Show(
                    GetString("Loc.MsgUpdateDownloadFailedForInstall"),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            SaveSettingsImmediate();
            SetManualUpdateStatus(GetString("Loc.UpdateStatusBackingUp"));
            if (!TryCreatePreUpdateBackup(normalizedLatestVersion, out _, out string backupError))
            {
                SetManualUpdateStatus(string.Empty);
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgUpdateBackupFailed"), backupError),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            SetManualUpdateStatus(string.Format(GetString("Loc.UpdateStatusInstalling"), normalizedLatestVersion));

            if (!TryStartSilentInstaller(pendingInfo.InstallerPath, out var installerProcess))
            {
                SetManualUpdateStatus(string.Empty);
                System.Windows.MessageBox.Show(
                    GetString("Loc.MsgUpdateInstallStartFailed"),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            if (installerProcess != null)
            {
                TrySchedulePostUpdateRelaunch(
                    installerProcess.Id,
                    Environment.ProcessId,
                    GetPostUpdateExecutableCandidates());
            }

            // Do not block here with a dialog: installer can already start closing windows.
            // Immediate shutdown avoids persisting panel visibility in an inconsistent transient state.
            ShutdownForUpdateInstall();
            return true;
        }

        internal static bool TryStartPendingUpdateInstall()
        {
            if (IsDevelopmentBuildForUpdates)
            {
                return false;
            }

            if (!TryReadPendingUpdateInfo(out var pendingInfo))
            {
                return false;
            }

            string pendingVersion = NormalizeVersionToken(pendingInfo.Version);
            if (string.IsNullOrWhiteSpace(pendingVersion) ||
                !IsRemoteVersionNewer(GetInstalledVersionText(), pendingVersion))
            {
                TryDeleteFile(PendingUpdateInfoPath);
                TryDeleteFile(pendingInfo.InstallerPath);
                return false;
            }

            if (!TryCreatePreUpdateBackup(pendingVersion, out _, out string backupError))
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgUpdateBackupFailed"), backupError),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            if (!TryStartSilentInstaller(pendingInfo.InstallerPath, out var installerProcess))
            {
                return false;
            }

            if (installerProcess != null)
            {
                TrySchedulePostUpdateRelaunch(
                    installerProcess.Id,
                    Environment.ProcessId,
                    GetPostUpdateExecutableCandidates());
            }

            return true;
        }

        private async Task CheckForUpdatesAsync(bool userInitiated)
        {
            if (userInitiated)
            {
                await CheckForUpdatesOnceAsync(userInitiated: true);
                return;
            }

            if (IsDevelopmentBuildForUpdates ||
                !_autoCheckUpdates ||
                _isExit ||
                _isAutomaticUpdateRoutineInProgress)
            {
                return;
            }

            _isAutomaticUpdateRoutineInProgress = true;
            try
            {
                if (AutomaticUpdateCheckDelay > TimeSpan.Zero)
                {
                    await Task.Delay(AutomaticUpdateCheckDelay);
                }

                if (_isExit || !_autoCheckUpdates)
                {
                    return;
                }

                await CheckForUpdatesOnceAsync(userInitiated: false);
            }
            finally
            {
                _isAutomaticUpdateRoutineInProgress = false;
            }
        }

        private async Task<bool> CheckForUpdatesOnceAsync(bool userInitiated)
        {
            if (IsDevelopmentBuildForUpdates)
            {
                return false;
            }

            if (_isUpdateCheckInProgress)
            {
                return false;
            }

            _isUpdateCheckInProgress = true;
            UpdateGeneralVersionLabel();
            bool fetchSucceeded = false;

            try
            {
                var latestRelease = await FetchLatestReleaseInfoAsync();
                fetchSucceeded = true;
                string currentVersionText = GetInstalledVersionText();
                string latestVersionText = NormalizeVersionToken(latestRelease.TagName);
                if (string.IsNullOrWhiteSpace(latestVersionText))
                {
                    latestVersionText = latestRelease.TagName;
                }

                bool updateAvailable = IsRemoteVersionNewer(currentVersionText, latestVersionText);

                if (updateAvailable)
                {
                    if (_autoCheckUpdates)
                    {
                        _ = EnsureLatestInstallerDownloadedAsync(latestRelease, latestVersionText);
                    }

                    if (userInitiated)
                    {
                        var action = ShowUpdateActionDialog(latestVersionText, currentVersionText);
                        if (action == UpdateActionDialogChoice.OpenReleasePage)
                        {
                            if (!TryOpenExternalUrl(latestRelease.HtmlUrl))
                            {
                                System.Windows.MessageBox.Show(
                                    GetString("Loc.MsgUpdateOpenPageFailed"),
                                    GetString("Loc.MsgError"),
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                            }
                        }
                        else if (action == UpdateActionDialogChoice.InstallNow)
                        {
                            await TryInstallLatestUpdateInteractivelyAsync(latestRelease, latestVersionText);
                        }
                    }
                }
                else if (userInitiated)
                {
                    System.Windows.MessageBox.Show(
                        string.Format(GetString("Loc.MsgUpdateUpToDate"), currentVersionText),
                        GetString("Loc.MsgInfo"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                if (userInitiated)
                {
                    System.Windows.MessageBox.Show(
                        string.Format(GetString("Loc.MsgUpdateCheckFailed"), ex.Message),
                        GetString("Loc.MsgError"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                else
                {
                    Debug.WriteLine($"Automatic update check failed: {ex}");
                }
            }
            finally
            {
                _isUpdateCheckInProgress = false;
                UpdateGeneralVersionLabel();
            }

            return fetchSucceeded;
        }

        private void UpdateGeneralVersionLabel()
        {
            if (CurrentVersionText != null)
            {
                string baseVersionText = string.Format(
                    GetString("Loc.GeneralCurrentVersion"),
                    GetInstalledVersionText());
                CurrentVersionText.Text = string.IsNullOrWhiteSpace(_manualUpdateStatusText)
                    ? baseVersionText
                    : $"{baseVersionText}\n{_manualUpdateStatusText}";
            }

            if (CheckUpdatesButton != null)
            {
                CheckUpdatesButton.IsEnabled = !_isUpdateCheckInProgress;
            }
        }

        private void AutoUpdateToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendGeneralHandlers)
            {
                return;
            }

            _autoCheckUpdates = AutoUpdateToggle?.IsChecked == true;
            SaveSettings();
            if (_autoCheckUpdates)
            {
                _ = CheckForUpdatesAsync(userInitiated: false);
            }
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(userInitiated: true);
        }

        private void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new BackupRestoreDialog(LoadAvailableUpdateBackups)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            bool? result = dialog.ShowDialog();
            if (result != true || dialog.SelectedBackup == null)
            {
                return;
            }

            UpdateBackupInfo backup = dialog.SelectedBackup;
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
    }
}
