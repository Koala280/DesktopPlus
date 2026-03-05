using System;
using System.Diagnostics;
using System.IO;
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
        private static readonly string PendingUpdateInfoPath = Path.Combine(UpdatesRootDirectory, "pending-update.json");
        private static readonly Regex VersionPrefixRegex = new Regex(@"^\d+(?:\.\d+){0,3}", RegexOptions.Compiled);
        private static readonly HttpClient UpdateHttpClient = CreateUpdateRequestHttpClient();
        private static readonly HttpClient UpdateDownloadHttpClient = CreateUpdateDownloadHttpClient();
        private static readonly TimeSpan AutomaticUpdateCheckInterval = TimeSpan.FromSeconds(60);
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
                while (!_isExit && _autoCheckUpdates)
                {
                    if (AutomaticUpdateCheckInterval > TimeSpan.Zero)
                    {
                        await Task.Delay(AutomaticUpdateCheckInterval);
                    }

                    if (_isExit || !_autoCheckUpdates)
                    {
                        break;
                    }

                    await CheckForUpdatesOnceAsync(userInitiated: false);
                }
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
