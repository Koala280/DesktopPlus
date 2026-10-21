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
            public int SchemaVersion { get; set; } = 2;
            public string BackupKind { get; set; } = "update";
            public string Reason { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string CurrentVersion { get; set; } = string.Empty;
            public string TargetVersion { get; set; } = string.Empty;
            public string CreatedUtc { get; set; } = string.Empty;
            public string SourceExecutablePath { get; set; } = string.Empty;
            public string SourceInstallDirectory { get; set; } = string.Empty;
            public bool CapturedAutoSortStorage { get; set; }
            public List<string> IncludedEntries { get; set; } = new List<string>();
            public List<DesktopBackupItemManifest> DesktopItems { get; set; } = new List<DesktopBackupItemManifest>();
            public List<DesktopSortMovedItem> DesktopMoves { get; set; } = new List<DesktopSortMovedItem>();
            public bool UsesFileReferences { get; set; }
            public List<BackupFileReference> FileReferences { get; set; } = new();
        }

        private sealed class DesktopBackupItemManifest
        {
            public string OriginalPath { get; set; } = string.Empty;
            public string ArchiveRelativePath { get; set; } = string.Empty;
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

        private static string BuildUpdateBackupRestoreInstructions(string archiveFileName)
        {
            return string.Join(Environment.NewLine, new[]
            {
                "DesktopPlus reference backup",
                $"Archive: {archiveFileName}",
                "Open the Backups tab, select this archive and choose Restore.",
                "Contains settings, custom languages and file/folder positions only.",
                "No program files or desktop/AutoSortStorage file contents are copied.",
                "Restore moves existing matching items without overwriting other files.",
                "Missing, deleted, inaccessible or ambiguous items are skipped and reported.",
                "Deleted file contents cannot be recovered from a reference backup.",
                "The five newest backups are retained across all backup types."
            });
        }

        private static bool TryCreatePreUpdateBackup(
            string targetVersion,
            out string backupArchivePath,
            out string errorMessage)
        {
            string version = NormalizeVersionToken(targetVersion);
            return TryCreateManagedBackup("update", $"Before update to {version}",
                $"Before update {NormalizeVersionToken(GetInstalledVersionText())} → {version}",
                out backupArchivePath, out errorMessage, targetVersion: version);
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

        internal static IReadOnlyList<UpdateBackupInfo> LoadAvailableUpdateBackups(string? directory = null)
        {
            lock (BackupRetention.SyncRoot)
            {
                string backupDirectory = directory ?? UpdateBackupsDirectory;
                BackupRetention.Maintain(backupDirectory);
                var backups = new List<UpdateBackupInfo>();
                if (!Directory.Exists(backupDirectory))
                {
                    return backups;
                }

                IEnumerable<string> backupArchives = Directory
                    .EnumerateFiles(backupDirectory, "DesktopPlus-backup-*.zip", SearchOption.TopDirectoryOnly)
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
                        bool hasManifest = TryReadUpdateBackupManifest(archive, out UpdateBackupManifest manifest);
                        if (!hasManifest && archive.GetEntry("manifest.json") != null)
                            throw new InvalidDataException("Invalid backup manifest.");
                        if (hasManifest)
                        {
                            backup.CurrentVersion = manifest.CurrentVersion ?? string.Empty;
                            backup.TargetVersion = manifest.TargetVersion ?? string.Empty;
                            backup.SourceInstallDirectory = manifest.SourceInstallDirectory ?? string.Empty;
                            backup.SourceExecutablePath = manifest.SourceExecutablePath ?? string.Empty;
                            backup.BackupKind = string.IsNullOrWhiteSpace(manifest.BackupKind)
                                ? "update"
                                : manifest.BackupKind;
                            backup.Reason = manifest.Reason ?? string.Empty;
                            backup.CustomDisplayName = manifest.DisplayName ?? string.Empty;
                            backup.ContainsDesktopSnapshot = manifest.DesktopItems?.Count > 0;
                            backup.ContainsDesktopMoveHistory = manifest.DesktopMoves?.Count > 0;
                            backup.ContainsFileReferences = manifest.UsesFileReferences;
                            backup.ContainsAutoSortStorage = manifest.CapturedAutoSortStorage;

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
                        backup.ContainsAutoSortStorage |= ZipArchiveContainsEntryPrefix(archive, "user-data/local/AutoSortStorage/");
                        backup.ContainsDesktopSnapshot |= ZipArchiveContainsEntryPrefix(archive, "user-data/desktop-items/");
                        backup.CanRestore = backup.ContainsAppSnapshot || backup.ContainsSettingsSnapshot || backup.ContainsCustomLanguages ||
                            backup.ContainsAutoSortStorage || backup.ContainsDesktopSnapshot || backup.ContainsDesktopMoveHistory || backup.ContainsFileReferences;
                        if (!backup.CanRestore) backup.Reason = GetString("Loc.BackupsUnreadable");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Unreadable backup archive '{archivePath}': {ex}");
                        backup.CanRestore = false;
                        backup.Reason = GetString("Loc.BackupsUnreadable");
                    }
                    backups.Add(backup);
                }

                return backups;
            }
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

        internal static string BuildDesktopMoveRestoreScript()
        {
            return """
              if ($manifest -and $manifest.DesktopMoves) {
                $sortRoot = [IO.Path]::GetFullPath($targetAutoSortDir).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
                $duplicatesRoot = $sortRoot + '.DuplicateShortcuts' + [IO.Path]::DirectorySeparatorChar
                foreach ($move in $manifest.DesktopMoves) {
                  if ([string]::IsNullOrWhiteSpace($move.SourcePath) -or [string]::IsNullOrWhiteSpace($move.TargetPath)) { continue }
                  $originalPath = [IO.Path]::GetFullPath($move.SourcePath)
                  $sortedPath = [IO.Path]::GetFullPath($move.TargetPath)
                  $originalParent = [IO.Path]::GetDirectoryName($originalPath)
                  $isStoredDuplicate = $originalPath.StartsWith($sortRoot, [StringComparison]::OrdinalIgnoreCase) -and $sortedPath.StartsWith($duplicatesRoot, [StringComparison]::OrdinalIgnoreCase)
                  if (($desktopRoots -notcontains $originalParent -and -not $isStoredDuplicate) -or -not $sortedPath.StartsWith($sortRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid desktop move in backup manifest.' }
                  if (-not (Test-Path -LiteralPath $sortedPath) -or (Test-Path -LiteralPath $originalPath)) {
                    if ($null -ne $restoreSkipped) { $restoreSkipped.Add($originalPath + ' - missing source or occupied destination') }
                    continue
                  }
                  try {
                    Ensure-Directory $originalParent
                    if ([IO.Directory]::Exists($sortedPath)) { [IO.Directory]::Move($sortedPath, $originalPath) } else { [IO.File]::Move($sortedPath, $originalPath) }
                  } catch { if ($null -ne $restoreSkipped) { $restoreSkipped.Add($originalPath + ' - ' + $_.Exception.Message) } }
                }
              }
            """;
        }

        internal static string BuildFileReferenceRestoreScript()
        {
            return "Add-Type -TypeDefinition @'" + Environment.NewLine + BackupFileReferences.RestoreIdentityType +
                Environment.NewLine + "'@" + Environment.NewLine + """
                  $sortRoot = [IO.Path]::GetFullPath($targetAutoSortDir).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
                  $wantedPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
                  foreach ($reference in $manifest.FileReferences) {
                    if ([string]::IsNullOrWhiteSpace($reference.Path)) { continue }
                    $path = [IO.Path]::GetFullPath($reference.Path)
                    if ($desktopRoots -notcontains [IO.Path]::GetDirectoryName($path) -and -not $path.StartsWith($sortRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid file reference in backup manifest.' }
                    [void]$wantedPaths.Add($path)
                  }
                  $inventory = New-Object 'System.Collections.Generic.List[System.IO.FileSystemInfo]'
                  foreach ($root in $desktopRoots) {
                    foreach ($item in Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue) { $inventory.Add($item) }
                  }
                  foreach ($category in Get-ChildItem -LiteralPath $targetAutoSortDir -Force -ErrorAction SilentlyContinue) {
                    if (-not $category.PSIsContainer) { $inventory.Add($category); continue }
                    if ($category.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
                    if ($category.Name -eq '.DuplicateShortcuts') {
                      foreach ($group in Get-ChildItem -LiteralPath $category.FullName -Directory -Force -ErrorAction SilentlyContinue) {
                        if ($group.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
                        foreach ($item in Get-ChildItem -LiteralPath $group.FullName -Force -ErrorAction SilentlyContinue) { $inventory.Add($item) }
                      }
                    } else {
                      foreach ($item in Get-ChildItem -LiteralPath $category.FullName -Force -ErrorAction SilentlyContinue) { $inventory.Add($item) }
                    }
                  }
                  $byId = @{}
                  foreach ($item in $inventory) {
                    try {
                      $id = [DesktopPlusBackupIdentity]::GetId($item.FullName)
                      if ([string]::IsNullOrWhiteSpace($id)) { continue }
                      if (-not $byId.ContainsKey($id)) { $byId[$id] = New-Object 'System.Collections.Generic.List[System.IO.FileSystemInfo]' }
                      $byId[$id].Add($item)
                    } catch { }
                  }
                  foreach ($reference in $manifest.FileReferences) {
                    if ([string]::IsNullOrWhiteSpace($reference.Path)) { continue }
                    $destination = [IO.Path]::GetFullPath($reference.Path)
                    try {
                      if (Test-Path -LiteralPath $destination) {
                        if ([DesktopPlusBackupIdentity]::GetId($destination) -ne $reference.FileId) {
                          $restoreSkipped.Add($destination + ' - destination occupied by another item')
                        }
                        continue
                      }
                      $matches = @()
                      if (-not [string]::IsNullOrWhiteSpace($reference.FileId) -and $byId.ContainsKey($reference.FileId)) {
                        $matches = @($byId[$reference.FileId] | Where-Object {
                          -not $wantedPaths.Contains($_.FullName) -and
                          $_.CreationTimeUtc.Ticks -eq $reference.CreationTimeUtcTicks -and
                          [bool]$_.PSIsContainer -eq [bool]$reference.IsDirectory -and
                          (Test-Path -LiteralPath $_.FullName) -and
                          [DesktopPlusBackupIdentity]::GetId($_.FullName) -eq $reference.FileId
                        })
                      }
                      if ($matches.Count -ne 1) {
                        $restoreSkipped.Add($destination + ' - deleted, moved outside the managed folders, inaccessible or ambiguous')
                        continue
                      }
                      Ensure-Directory ([IO.Path]::GetDirectoryName($destination))
                      if ($reference.IsDirectory) { [IO.Directory]::Move($matches[0].FullName, $destination) }
                      else { [IO.File]::Move($matches[0].FullName, $destination) }
                    } catch { $restoreSkipped.Add($destination + ' - ' + $_.Exception.Message) }
                  }
                """;
        }

        internal static string BuildBackupRestoreScript(
            UpdateBackupInfo backup,
            IReadOnlyList<string> launchCandidates,
            bool restoreBeforeUninstall = false)
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
            string desktopRootsLiteral = BuildPowerShellStringArrayLiteral(GetDesktopDirectoryPaths());

            var script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine("$restoreSkipped = New-Object 'System.Collections.Generic.List[string]'");
            if (!restoreBeforeUninstall)
            {
                script.AppendLine("Add-Type -AssemblyName PresentationFramework | Out-Null");
            }
            script.AppendLine($"$sourceProcessId = {Environment.ProcessId}");
            script.AppendLine($"$archivePath = '{EscapePowerShellSingleQuotedLiteral(backup.ArchivePath)}'");
            script.AppendLine($"$targetInstallDir = '{EscapePowerShellSingleQuotedLiteral(currentInstallDirectory)}'");
            script.AppendLine($"$settingsPath = '{EscapePowerShellSingleQuotedLiteral(targetSettingsPath)}'");
            script.AppendLine($"$targetLanguagesDir = '{EscapePowerShellSingleQuotedLiteral(targetLanguagesDirectory)}'");
            script.AppendLine($"$targetAutoSortDir = '{EscapePowerShellSingleQuotedLiteral(targetAutoSortDirectory)}'");
            script.AppendLine($"$desktopRoots = @({desktopRootsLiteral})");
            script.AppendLine($"$pendingInfoPath = '{EscapePowerShellSingleQuotedLiteral(PendingUpdateInfoPath)}'");
            script.AppendLine($"$errorTitle = '{EscapePowerShellSingleQuotedLiteral(errorTitle)}'");
            script.AppendLine($"$restoreTitle = '{EscapePowerShellSingleQuotedLiteral(GetString("Loc.BackupsRestore"))}'");
            script.AppendLine($"$launchMissingMessage = '{EscapePowerShellSingleQuotedLiteral(launchMissingMessage)}'");
            script.AppendLine($"$launchTargets = @({launchTargetsLiteral})");
            script.AppendLine("$restoreRoot = Join-Path $env:LOCALAPPDATA ('DesktopPlus\\\\Backups\\\\restore-' + [guid]::NewGuid().ToString('N'))");
            script.AppendLine("$extractPath = Join-Path $restoreRoot 'extract'");
            script.AppendLine(restoreBeforeUninstall
                ? "function Show-RestoreError([string]$message) { [Console]::Error.WriteLine($message) }"
                : "function Show-RestoreError([string]$message) { [System.Windows.MessageBox]::Show($message, $errorTitle, 'OK', 'Error') | Out-Null }");
            script.AppendLine("function Ensure-Directory([string]$path) { if ([string]::IsNullOrWhiteSpace($path)) { return } [IO.Directory]::CreateDirectory($path) | Out-Null }");
            script.AppendLine("function Reset-DirectoryContents([string]$path) { if ([string]::IsNullOrWhiteSpace($path)) { throw 'Restore target path is missing.' } Ensure-Directory $path; Get-ChildItem -LiteralPath $path -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force }");
            script.AppendLine("function Copy-DirectoryContents([string]$source, [string]$destination) { if (-not (Test-Path -LiteralPath $source)) { return } Ensure-Directory $destination; foreach ($child in Get-ChildItem -LiteralPath $source -Force) { Copy-Item -LiteralPath $child.FullName -Destination (Join-Path $destination $child.Name) -Recurse -Force } }");
            script.AppendLine("function Copy-MissingDirectoryContents([string]$source, [string]$destination) {");
            script.AppendLine("  if (-not (Test-Path -LiteralPath $source)) { return }");
            script.AppendLine("  foreach ($child in Get-ChildItem -LiteralPath $source -Force) {");
            script.AppendLine("    $target = Join-Path $destination $child.Name");
            script.AppendLine("    try {");
            script.AppendLine("      if (Test-Path -LiteralPath $target) {");
            script.AppendLine("        $existing = Get-Item -LiteralPath $target -Force");
            script.AppendLine("        if ($child.PSIsContainer -and $existing.PSIsContainer -and -not ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) { Copy-MissingDirectoryContents $child.FullName $target }");
            script.AppendLine("        else { $restoreSkipped.Add($target + ' - destination already exists') }");
            script.AppendLine("      } else { Ensure-Directory $destination; Copy-Item -LiteralPath $child.FullName -Destination $target -Recurse }");
            script.AppendLine("    } catch { $restoreSkipped.Add($target + ' - ' + $_.Exception.Message) }");
            script.AppendLine("  }");
            script.AppendLine("}");
            if (!restoreBeforeUninstall)
            {
                script.AppendLine("try { Wait-Process -Id $sourceProcessId -ErrorAction SilentlyContinue } catch { }");
                script.AppendLine("Start-Sleep -Milliseconds 900");
            }
            script.AppendLine("try {");
            script.AppendLine("  Ensure-Directory $restoreRoot");
            script.AppendLine("  Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null");
            script.AppendLine("  [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $extractPath)");
            script.AppendLine("  $manifestPath = Join-Path $extractPath 'manifest.json'");
            script.AppendLine("  $manifest = if (Test-Path -LiteralPath $manifestPath) { Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json } else { $null }");
            script.AppendLine("  if ($manifest -and $manifest.UsesFileReferences) {");
            script.AppendLine(BuildFileReferenceRestoreScript());
            script.AppendLine("  } else {");
            script.AppendLine(BuildDesktopMoveRestoreScript());
            script.AppendLine("  }");
            if (!restoreBeforeUninstall)
            {
                script.AppendLine("  $appSource = Join-Path $extractPath 'app'");
                script.AppendLine("  if (Test-Path -LiteralPath $appSource) { Reset-DirectoryContents $targetInstallDir; Copy-DirectoryContents $appSource $targetInstallDir }");
            }
            script.AppendLine("  $settingsSource = Join-Path $extractPath 'user-data\\roaming\\DesktopPlus_Settings.json'");
            script.AppendLine("  if (Test-Path -LiteralPath $settingsSource) { Ensure-Directory (Split-Path -Path $settingsPath -Parent); Copy-Item -LiteralPath $settingsSource -Destination $settingsPath -Force }");
            script.AppendLine("  $languagesSource = Join-Path $extractPath 'user-data\\roaming\\DesktopPlus\\Languages'");
            script.AppendLine("  if (Test-Path -LiteralPath $languagesSource) { if (Test-Path -LiteralPath $targetLanguagesDir) { Remove-Item -LiteralPath $targetLanguagesDir -Recurse -Force }; Ensure-Directory (Split-Path -Path $targetLanguagesDir -Parent); Copy-DirectoryContents $languagesSource $targetLanguagesDir }");
            script.AppendLine("  $autoSortSource = Join-Path $extractPath 'user-data\\local\\AutoSortStorage'");
            script.AppendLine("  $capturedAutoSort = ($manifest -and $manifest.CapturedAutoSortStorage) -or (Test-Path -LiteralPath $autoSortSource)");
            script.AppendLine("  if ($capturedAutoSort) { Copy-MissingDirectoryContents $autoSortSource $targetAutoSortDir }");
            script.AppendLine("  if ($manifest -and $manifest.DesktopItems) {");
            script.AppendLine("    $desktopItemsRoot = [IO.Path]::GetFullPath((Join-Path $extractPath 'user-data\\desktop-items')) + [IO.Path]::DirectorySeparatorChar");
            script.AppendLine("    foreach ($desktopItem in $manifest.DesktopItems) {");
            script.AppendLine("      if ([string]::IsNullOrWhiteSpace($desktopItem.OriginalPath) -or [string]::IsNullOrWhiteSpace($desktopItem.ArchiveRelativePath)) { continue }");
            script.AppendLine("      $desktopOriginalPath = [IO.Path]::GetFullPath($desktopItem.OriginalPath)");
            script.AppendLine("      $desktopRelativePath = $desktopItem.ArchiveRelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)");
            script.AppendLine("      $desktopSource = [IO.Path]::GetFullPath((Join-Path $extractPath $desktopRelativePath))");
            script.AppendLine("      if ($desktopRoots -notcontains [IO.Path]::GetDirectoryName($desktopOriginalPath) -or -not $desktopSource.StartsWith($desktopItemsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid desktop item in backup manifest.' }");
            script.AppendLine("      if (-not (Test-Path -LiteralPath $desktopSource)) { continue }");
            script.AppendLine("      Ensure-Directory (Split-Path -Path $desktopOriginalPath -Parent)");
            script.AppendLine("      if (Test-Path -LiteralPath $desktopOriginalPath) { $restoreSkipped.Add($desktopOriginalPath + ' - destination already exists'); continue }");
            script.AppendLine("      try { Copy-Item -LiteralPath $desktopSource -Destination $desktopOriginalPath -Recurse } catch { $restoreSkipped.Add($desktopOriginalPath + ' - ' + $_.Exception.Message) }");
            script.AppendLine("    }");
            script.AppendLine("  }");
            script.AppendLine("  if (Test-Path -LiteralPath $pendingInfoPath) { Remove-Item -LiteralPath $pendingInfoPath -Force -ErrorAction SilentlyContinue }");
            script.AppendLine("  $reportPath = Join-Path ([IO.Path]::GetDirectoryName($archivePath)) 'DesktopPlus-last-restore.txt'");
            script.AppendLine("  $report = @([DateTime]::Now.ToString('s'), ('Skipped items: ' + $restoreSkipped.Count)) + @($restoreSkipped.ToArray())");
            script.AppendLine("  try { [IO.File]::WriteAllLines($reportPath, [string[]]$report) } catch { }");
            if (!restoreBeforeUninstall)
            {
                string skippedMessage = EscapePowerShellSingleQuotedLiteral(GetString("Loc.BackupsRestoreSkipped"));
                script.AppendLine($"  if ($restoreSkipped.Count -gt 0) {{ [System.Windows.MessageBox]::Show(('{skippedMessage}' -f $restoreSkipped.Count, $reportPath), $restoreTitle, 'OK', 'Information') | Out-Null }}");
            }
            script.AppendLine("} catch {");
            script.AppendLine("  Show-RestoreError($_.Exception.Message)");
            script.AppendLine("  exit 1");
            script.AppendLine("} finally {");
            script.AppendLine("  try { if (Test-Path -LiteralPath $extractPath) { Remove-Item -LiteralPath $extractPath -Recurse -Force } } catch { }");
            if (restoreBeforeUninstall)
            {
                script.AppendLine("  try { if (Test-Path -LiteralPath $restoreRoot) { Remove-Item -LiteralPath $restoreRoot -Recurse -Force } } catch { }");
            }
            script.AppendLine("}");
            if (restoreBeforeUninstall)
            {
                script.AppendLine("exit 0");
                return script.ToString();
            }
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
                string encodedScript = EncodeBackupRestoreScript(script);
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

        internal static string EncodeBackupRestoreScript(string script)
        {
            // Stay below Windows' command-line limit even with the identity/restore helpers and long paths.
            using var buffer = new MemoryStream();
            using (var compressed = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(script);
                compressed.Write(bytes, 0, bytes.Length);
            }
            string payload = Convert.ToBase64String(buffer.ToArray());
            string wrapper = "$stream = New-Object IO.MemoryStream(,[Convert]::FromBase64String('" + payload + "')); " +
                "$gzip = New-Object IO.Compression.GZipStream($stream, [IO.Compression.CompressionMode]::Decompress); " +
                "$reader = New-Object IO.StreamReader($gzip, [Text.Encoding]::UTF8); " +
                "try { & ([ScriptBlock]::Create($reader.ReadToEnd())) } finally { $reader.Dispose(); $stream.Dispose() }";
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper));
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
            if (_isBackupOperationInProgress || _isExit || _isExitPending) return false;
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

            await _desktopAutoSortTask;
            SaveSettingsImmediate();
            SetManualUpdateStatus(GetString("Loc.UpdateStatusBackingUp"));
            (bool created, string error) backupResult;
            _isBackupOperationInProgress = true;
            UpdateBackupDetails(BackupsList.SelectedItem as UpdateBackupInfo);
            try
            {
                backupResult = await Task.Run(() =>
                {
                    bool created = TryCreatePreUpdateBackup(normalizedLatestVersion, out _, out string error);
                    return (created, error);
                });
            }
            finally
            {
                FinishBackupOperation();
            }
            if (!backupResult.created)
            {
                SetManualUpdateStatus(string.Empty);
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgUpdateBackupFailed"), backupResult.error),
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

        internal static async Task<bool> TryStartPendingUpdateInstallAsync()
        {
            var result = await Task.Run(() =>
            {
                bool started = TryStartPendingUpdateInstall(out string error);
                return (started, error);
            });
            if (!string.IsNullOrWhiteSpace(result.error))
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgUpdateBackupFailed"), result.error),
                    GetString("Loc.MsgError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return result.started;
        }

        private static bool TryStartPendingUpdateInstall(out string backupError)
        {
            backupError = string.Empty;
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

            if (!TryCreatePreUpdateBackup(pendingVersion, out _, out backupError))
            {
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

    }
}
