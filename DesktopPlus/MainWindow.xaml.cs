using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Collections.Generic;
using System.Windows.Forms;
using Application = System.Windows.Application; // Für WPF

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        public class WindowData
        {
            public string FolderPath { get; set; }
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double Zoom { get; set; }
            public bool IsCollapsed { get; set; }
            public double CollapsedTop { get; set; }
            public double BaseTop { get; set; }
            public string PanelName { get; set; }
            public bool ExpandOnHover { get; set; }
            public bool ShowMinimizeButton { get; set; }
        }

        private static List<WindowData> openWindows = new List<WindowData>();
        private static readonly string settingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPlus_Settings.json"
        );

        private NotifyIcon _notifyIcon;
        private bool _isExit = false;

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            InitNotifyIcon();

            this.Closing += OnWindowClosing;
            this.StateChanged += OnWindowStateChanged;

            this.ShowInTaskbar = false;
            this.WindowState = WindowState.Minimized;
            this.Hide();
        }

        private void InitNotifyIcon()
        {
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = new System.Drawing.Icon("Resources/desktopplus_icon.ico");
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "DesktopPlus";

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Öffnen", null, (s, e) => ShowMainWindow());
            contextMenu.Items.Add("Beenden", null, (s, e) => ExitApplication());

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ExitApplication()
        {
            _isExit = true;
            _notifyIcon.Dispose();
            SaveSettings();
            Application.Current.Shutdown();
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExit)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
                this.Hide();
        }

        private void LoadSettings()
        {
            if (File.Exists(settingsFilePath))
            {
                try
                {
                    string json = File.ReadAllText(settingsFilePath);
                    if (string.IsNullOrWhiteSpace(json)) return;

                    openWindows = JsonSerializer.Deserialize<List<WindowData>>(json) ?? new List<WindowData>();

                    foreach (var winData in openWindows)
                    {
                        if (!Directory.Exists(winData.FolderPath)) continue;

                        Dispatcher.Invoke(() =>
                        {
                            DesktopPanel panel = new DesktopPanel();
                            panel.Show();
                            panel.LoadFolder(winData.FolderPath);

                            if (!string.IsNullOrWhiteSpace(winData.PanelName))
                            {
                                panel.PanelTitle.Text = winData.PanelName;
                                panel.Title = winData.PanelName;
                            }

                            panel.baseTopPosition = winData.BaseTop;
                            panel.Left = winData.Left;
                            panel.Width = winData.Width;
                            panel.Height = winData.Height;
                            panel.SetZoom(winData.Zoom);
                            panel.ForceCollapseState(winData.IsCollapsed);
                            panel.ExpandOnHoverSetting = winData.ExpandOnHover;
                            panel.ShowMinimizeButton = winData.ShowMinimizeButton;
                            panel.MinimizeButton.Visibility = panel.ShowMinimizeButton ? Visibility.Visible : Visibility.Collapsed;

                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Fehler beim Wiederherstellen der Fenster:\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public static void SaveSettings()
        {
            try
            {
                openWindows = Application.Current.Windows.OfType<DesktopPanel>()
                    .Where(win => !string.IsNullOrEmpty(win.currentFolderPath))
                    .Select(win => new WindowData
                    {
                        FolderPath = win.currentFolderPath,
                        Left = win.Left,
                        Top = win.Top,
                        Width = win.Width,
                        Height = win.Height,
                        Zoom = win.zoomFactor,
                        IsCollapsed = !win.isContentVisible,
                        BaseTop = win.baseTopPosition,
                        PanelName = win.PanelTitle.Text,
                        ExpandOnHover = win.ExpandOnHoverSetting,
                        ShowMinimizeButton = win.ShowMinimizeButton
                    })
                    .ToList();

                if (openWindows.Any())
                {
                    string json = JsonSerializer.Serialize(openWindows, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    });
                    File.WriteAllText(settingsFilePath, json);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Fehler beim Speichern der Fenster:\n{ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            System.Windows.MessageBox.Show("✅ Fenster wurden gespeichert!", "Speichern", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExitApplication_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        private void OpenDesktopPanel_Click(object sender, RoutedEventArgs e)
        {
            DesktopPanel panel = new DesktopPanel();
            panel.Show();
        }
    }
}
