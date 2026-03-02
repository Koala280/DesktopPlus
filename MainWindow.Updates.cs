using System;
using System.Diagnostics;
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
        private static readonly Regex VersionPrefixRegex = new Regex(@"^\d+(?:\.\d+){0,3}", RegexOptions.Compiled);
        private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();
        private bool _autoCheckUpdates = false;
        private bool _isUpdateCheckInProgress = false;

        private static HttpClient CreateUpdateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopPlus/1.0 (+https://github.com/Koala280/DesktopPlus)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private sealed class LatestReleaseInfo
        {
            public string TagName { get; init; } = string.Empty;
            public string HtmlUrl { get; init; } = ReleasesPageUrl;
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

            return new LatestReleaseInfo
            {
                TagName = tagName,
                HtmlUrl = htmlUrl
            };
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

        private async Task CheckForUpdatesAsync(bool userInitiated)
        {
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

                bool updateAvailable;
                if (TryParseComparableVersion(currentVersionText, out var currentVersion) &&
                    TryParseComparableVersion(latestVersionText, out var latestVersion))
                {
                    updateAvailable = latestVersion > currentVersion;
                }
                else
                {
                    updateAvailable = !string.Equals(
                        NormalizeVersionToken(currentVersionText),
                        NormalizeVersionToken(latestVersionText),
                        StringComparison.OrdinalIgnoreCase);
                }

                if (updateAvailable)
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
