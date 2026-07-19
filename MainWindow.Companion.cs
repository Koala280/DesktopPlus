using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using DesktopPlus.Companion;

namespace DesktopPlus
{
    public partial class MainWindow : Window, ICompanionHost
    {
        private CompanionSettings _companion = new CompanionSettings();
        private CompanionServer? _companionServer;
        private bool _suppressCompanionToggle;
        private bool _companionPanelsHooked;

        private void InitializeCompanion()
        {
            if (_companion.Port <= 0 || _companion.Port > 65535)
            {
                _companion.Port = 8443;
            }

            if (!_companionPanelsHooked)
            {
                PanelsChanged += OnCompanionPanelsChanged;
                _companionPanelsHooked = true;
            }

            if (_companion.Enabled)
            {
                _ = StartCompanionAsync(showErrors: false);
            }

            UpdateCompanionUi();
        }

        private void OnCompanionPanelsChanged() => _companionServer?.NotifyPanelsChanged();

        Task<IReadOnlyList<CompanionPanelInfo>> ICompanionHost.GetPanelsAsync()
        {
            return Dispatcher.InvokeAsync(() =>
            {
                var panels = new List<CompanionPanelInfo>();

                try
                {
                    var app = System.Windows.Application.Current;
                    if (app != null)
                    {
                        foreach (var panel in app.Windows.OfType<DesktopPanel>())
                        {
                            if (!IsUserPanel(panel))
                            {
                                continue;
                            }

                            panels.Add(new CompanionPanelInfo
                            {
                                Id = panel.PanelId,
                                Title = BuildCompanionPanelTitle(panel),
                                Type = panel.PanelType.ToString(),
                                FolderPath = panel.currentFolderPath ?? string.Empty
                            });
                        }
                    }
                }
                catch
                {
                    // Never surface an enumeration hiccup as a 500 to the phone; an empty list is
                    // the safe fallback and the next refresh / panelsChanged push will recover.
                }

                return (IReadOnlyList<CompanionPanelInfo>)panels;
            }).Task;
        }

        Task<IReadOnlyList<string>?> ICompanionHost.GetPanelItemsAsync(string panelId)
        {
            return Dispatcher.InvokeAsync(() =>
            {
                if (string.IsNullOrWhiteSpace(panelId))
                {
                    return (IReadOnlyList<string>?)null;
                }

                try
                {
                    foreach (var panel in System.Windows.Application.Current.Windows.OfType<DesktopPanel>())
                    {
                        if (!IsUserPanel(panel) || panel.PanelId != panelId)
                        {
                            continue;
                        }

                        if (panel.PanelType != PanelKind.List)
                        {
                            return (IReadOnlyList<string>?)null;
                        }

                        return (IReadOnlyList<string>?)panel.PinnedItems.ToList();
                    }
                }
                catch
                {
                    // Treat any enumeration hiccup as "not available" rather than a 500.
                }

                return (IReadOnlyList<string>?)null;
            }).Task;
        }

        Task<bool> ICompanionHost.NavigatePanelAsync(string panelId, string path)
        {
            return Dispatcher.InvokeAsync(() =>
            {
                if (string.IsNullOrWhiteSpace(panelId) || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    return false;
                }

                foreach (var panel in System.Windows.Application.Current.Windows.OfType<DesktopPanel>())
                {
                    if (!IsUserPanel(panel) || panel.PanelId != panelId)
                    {
                        continue;
                    }

                    try
                    {
                        panel.LoadFolder(path, saveSettings: true);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                return false;
            }).Task;
        }

        // Builds a human-friendly panel title, mirroring the in-app overview fallbacks
        // (folder leaf name, then panel type) when the window has no explicit title.
        private static string BuildCompanionPanelTitle(DesktopPanel panel)
        {
            string? title = panel.Title;
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            string? folder = panel.currentFolderPath;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                string leaf = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
                return string.IsNullOrWhiteSpace(leaf) ? folder : leaf;
            }

            return panel.PanelType.ToString();
        }

        private async Task StartCompanionAsync(bool showErrors)
        {
            if (string.IsNullOrEmpty(_companion.Token))
            {
                _companion.Token = CompanionAuth.GenerateToken();
            }

            _companionServer ??= new CompanionServer();

            try
            {
                await _companionServer.StartAsync(_companion.Port, _companion.Token, this);
            }
            catch (Exception ex)
            {
                _companion.Enabled = false;
                if (showErrors)
                {
                    System.Windows.MessageBox.Show(
                        string.Format(GetString("Loc.CompanionStartFailed"), ex.Message),
                        GetString("Loc.MsgError"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            UpdateCompanionUi();
        }

        private async Task StopCompanionAsync()
        {
            if (_companionServer != null)
            {
                await _companionServer.StopAsync();
            }

            UpdateCompanionUi();
        }

        private async void CompanionToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressCompanionToggle)
            {
                return;
            }

            bool enable = CompanionEnableToggle?.IsChecked == true;
            _companion.Enabled = enable;

            if (enable)
            {
                await StartCompanionAsync(showErrors: true);
            }
            else
            {
                await StopCompanionAsync();
            }

            SaveSettings();
        }

        private void CompanionRegenerateToken_Click(object sender, RoutedEventArgs e)
        {
            _companion.Token = CompanionAuth.GenerateToken();
            if (_companionServer?.IsRunning == true)
            {
                _companionServer.UpdateToken(_companion.Token);
            }

            SaveSettings();
            UpdateCompanionUi();
        }

        private void CompanionCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            string url = BuildCompanionPairingUrl();
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(url);
            }
            catch
            {
            }
        }

        private void UpdateCompanionUi()
        {
            if (CompanionEnableToggle == null)
            {
                return;
            }

            _suppressCompanionToggle = true;
            CompanionEnableToggle.IsChecked = _companion.Enabled;
            _suppressCompanionToggle = false;

            bool running = _companionServer?.IsRunning == true;

            if (CompanionConnectionPanel != null)
            {
                CompanionConnectionPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            }

            if (running)
            {
                if (CompanionUrlText != null)
                {
                    CompanionUrlText.Text = BuildCompanionUrl();
                }

                if (CompanionStatusText != null)
                {
                    CompanionStatusText.Text = GetString("Loc.CompanionStatusRunning");
                }

                RenderCompanionQr(BuildCompanionPairingUrl());
            }
            else if (CompanionStatusText != null)
            {
                CompanionStatusText.Text = _companion.Enabled
                    ? GetString("Loc.CompanionStatusError")
                    : GetString("Loc.CompanionStatusDisabled");
            }
        }

        private string BuildCompanionUrl()
        {
            string ip = CompanionNetwork.GetPrimaryLanAddress() ?? "127.0.0.1";
            int port = (_companionServer?.IsRunning == true && _companionServer.Port > 0)
                ? _companionServer.Port
                : _companion.Port;
            return $"https://{ip}:{port}/";
        }

        private string BuildCompanionPairingUrl()
        {
            if (string.IsNullOrEmpty(_companion.Token))
            {
                return string.Empty;
            }

            return $"{BuildCompanionUrl()}#t={Uri.EscapeDataString(_companion.Token)}";
        }

        private void RenderCompanionQr(string url)
        {
            if (CompanionQrImage == null || string.IsNullOrEmpty(url))
            {
                return;
            }

            try
            {
                byte[] png = QrCodeGenerator.CreatePng(url);
                using var stream = new MemoryStream(png);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                CompanionQrImage.Source = bitmap;
            }
            catch
            {
            }
        }

        private void CleanupCompanion()
        {
            var server = _companionServer;
            _companionServer = null;
            if (server != null)
            {
                try
                {
                    server.StopAsync().GetAwaiter().GetResult();
                }
                catch
                {
                }
            }
        }
    }
}
