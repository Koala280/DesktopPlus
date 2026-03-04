using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

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
        private bool _autoCheckUpdates = false;
        private bool _isUpdateCheckInProgress = false;
        private bool _isUpdateDownloadInProgress = false;
        private string _updateDownloadVersionInProgress = string.Empty;

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

        private static bool TryStartSilentInstaller(string installerPath)
        {
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start silent installer: {ex}");
                return false;
            }
        }

        private bool TryApplyPendingUpdateAndExit()
        {
            if (!TryReadPendingUpdateInfo(out var pendingInfo))
            {
                return false;
            }

            string pendingVersion = NormalizeVersionToken(pendingInfo.Version);
            if (string.IsNullOrWhiteSpace(pendingVersion) ||
                !IsRemoteVersionNewer(GetInstalledVersionText(), pendingVersion))
            {
                return false;
            }

            if (!TryStartSilentInstaller(pendingInfo.InstallerPath))
            {
                return false;
            }

            _isExit = true;
            IsExiting = true;
            CloseTrayMenuWindow();
            StopDesktopAutoSortWatcher();
            _notifyIcon?.Dispose();
            SaveSettingsImmediate();
            System.Windows.Application.Current.Shutdown();
            return true;
        }

        private bool TryApplyPendingUpdateOnMainWindowOpen()
        {
            if (IsDevelopmentBuildForUpdates)
            {
                return false;
            }

            if (!_autoCheckUpdates)
            {
                return false;
            }

            return TryApplyPendingUpdateAndExit();
        }

        private async Task CheckForUpdatesAsync(bool userInitiated)
        {
            if (IsDevelopmentBuildForUpdates)
            {
                return;
            }

            if (_isUpdateCheckInProgress)
            {
                return;
            }

            _isUpdateCheckInProgress = true;
            UpdateGeneralVersionLabel();

            try
            {
                var latestRelease = await FetchLatestReleaseInfoAsync();
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
                        var answer = System.Windows.MessageBox.Show(
                            string.Format(GetString("Loc.MsgUpdateAvailable"), latestVersionText, currentVersionText),
                            GetString("Loc.MsgInfo"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);

                        if (answer == MessageBoxResult.Yes &&
                            !TryOpenExternalUrl(latestRelease.HtmlUrl))
                        {
                            System.Windows.MessageBox.Show(
                                GetString("Loc.MsgUpdateOpenPageFailed"),
                                GetString("Loc.MsgError"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
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
        }

        private void UpdateGeneralVersionLabel()
        {
            if (CurrentVersionText != null)
            {
                CurrentVersionText.Text = string.Format(
                    GetString("Loc.GeneralCurrentVersion"),
                    GetInstalledVersionText());
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
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(userInitiated: true);
        }
    }
}
