using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Collections.Generic;
using WinForms = System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Diagnostics;
using Microsoft.Win32;
using System.Globalization;
using MediaColor = System.Windows.Media.Color;
using Application = System.Windows.Application; // Für WPF

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private WinForms.NotifyIcon? _notifyIcon;
        private TrayMenuWindow? _trayMenuWindow;
        private bool _isExit = false;
        public static bool IsExiting { get; private set; }
        public static event Action? AppearanceChanged;
        public static event Action? PanelsChanged;
        private bool _isUiReady = false;
        private bool _suspendPresetSelection = false;
        private bool _suspendLayoutPresetSelection = false;
        private bool _suspendGeneralHandlers = false;
        private const string DefaultPresetName = "Noir";
        private const string DefaultLanguageCode = "de";
        private const string CloseBehaviorMinimize = "Minimize";
        private const string CloseBehaviorExit = "Exit";
        private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRegistryValue = "DesktopPlus";
        public static string CurrentLanguageCode { get; private set; } = DefaultLanguageCode;

        private string _languageCode = DefaultLanguageCode;
        private string _layoutDefaultPresetName = DefaultPresetName;
        private bool _startWithWindows = false;
        private string _closeBehavior = CloseBehaviorMinimize;



        private static PanelKind ResolvePanelKind(WindowData data)
        {
            if (!string.IsNullOrWhiteSpace(data.PanelType) &&
                Enum.TryParse(data.PanelType, true, out PanelKind parsed))
            {
                return parsed;
            }

            if (!string.IsNullOrWhiteSpace(data.FolderPath))
            {
                return PanelKind.Folder;
            }

            if (data.PinnedItems != null && data.PinnedItems.Count > 0)
            {
                return PanelKind.List;
            }

            return PanelKind.None;
        }

        private static PanelKind ResolvePanelKind(DesktopPanel panel)
        {
            if (panel == null) return PanelKind.None;
            if (panel.PanelType != PanelKind.None) return panel.PanelType;
            if (!string.IsNullOrWhiteSpace(panel.currentFolderPath)) return PanelKind.Folder;
            if (panel.PinnedItems.Count > 0) return PanelKind.List;
            return PanelKind.None;
        }

        private static string GeneratePanelId()
        {
            return $"panel:{Guid.NewGuid():N}";
        }

        private static string EnsurePanelId(WindowData data)
        {
            // Legacy folder-based keys break when two panels share the same folder.
            if (string.IsNullOrWhiteSpace(data.PanelId) ||
                data.PanelId.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                data.PanelId = GeneratePanelId();
            }
            return data.PanelId;
        }

        private static string GetPanelKey(WindowData data)
        {
            return EnsurePanelId(data);
        }

        private static string GetPanelKey(DesktopPanel panel)
        {
            if (string.IsNullOrWhiteSpace(panel.PanelId))
            {
                panel.PanelId = GeneratePanelId();
            }
            return panel.PanelId;
        }

        private static WindowData? FindSavedWindow(string panelKey)
        {
            return savedWindows.FirstOrDefault(x =>
                string.Equals(GetPanelKey(x), panelKey, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, WindowData> CreateWindowDataMap(IEnumerable<WindowData> windows, bool rewriteDuplicates = false)
        {
            var map = new Dictionary<string, WindowData>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in windows)
            {
                if (window == null) continue;
                NormalizeWindowData(window);
                string key = GetPanelKey(window);

                if (rewriteDuplicates)
                {
                    while (map.ContainsKey(key))
                    {
                        window.PanelId = GeneratePanelId();
                        key = GetPanelKey(window);
                    }
                }

                map[key] = window;
            }

            return map;
        }

        private static Dictionary<string, DesktopPanel> CreateOpenPanelMap(IEnumerable<DesktopPanel> panels)
        {
            var map = new Dictionary<string, DesktopPanel>(StringComparer.OrdinalIgnoreCase);
            foreach (var panel in panels)
            {
                if (panel == null) continue;
                map[GetPanelKey(panel)] = panel;
            }
            return map;
        }

        private static void NormalizeWindowData(WindowData data)
        {
            if (data == null) return;
            data.PinnedItems ??= new List<string>();
            var kind = ResolvePanelKind(data);
            if (string.IsNullOrWhiteSpace(data.PanelType))
            {
                data.PanelType = kind.ToString();
            }
            EnsurePanelId(data);
        }

        private static WindowData BuildWindowDataFromPanel(DesktopPanel panel)
        {
            var kind = ResolvePanelKind(panel);
            var folderPath = kind == PanelKind.Folder ? panel.currentFolderPath : "";
            var pinnedItems = kind == PanelKind.List
                ? panel.PinnedItems.ToList()
                : new List<string>();

            return new WindowData
            {
                PanelId = string.IsNullOrWhiteSpace(panel.PanelId) ? (panel.PanelId = GeneratePanelId()) : panel.PanelId,
                PanelType = kind.ToString(),
                FolderPath = folderPath ?? "",
                DefaultFolderPath = panel.defaultFolderPath ?? "",
                Left = panel.Left,
                Top = panel.Top,
                Width = panel.Width,
                Height = panel.Height,
                Zoom = panel.zoomFactor,
                IsCollapsed = !panel.isContentVisible,
                IsHidden = false,
                CollapsedTop = panel.collapsedTopPosition,
                BaseTop = panel.baseTopPosition,
                PanelTitle = panel.PanelTitle.Text,
                PresetName = string.IsNullOrWhiteSpace(panel.assignedPresetName) ? DefaultPresetName : panel.assignedPresetName,
                ShowHidden = panel.showHiddenItems,
                ShowSettingsButton = panel.showSettingsButton,
                ExpandOnHover = panel.expandOnHover,
                OpenFoldersExternally = panel.openFoldersExternally,
                MovementMode = panel.movementMode,
                PinnedItems = pinnedItems
            };
        }

        public MainWindow()
        {
            InitializeComponent();
            AddHandler(MouseWheelEvent, new MouseWheelEventHandler(MainScrollViewer_PreviewMouseWheel), true);
            TrySetWindowIcon();
            LoadSettings();
            ApplyLanguage(_languageCode);
            if (!Presets.Any())
            {
                Presets = GetDefaultPresets();
            }
            InitNotifyIcon();

            this.Closing += OnWindowClosing;
            this.StateChanged += OnWindowStateChanged;
            this.Loaded += (s, e) => RefreshPanelOverview();
            PanelsChanged += RefreshPanelOverview;
            this.Closed += (s, e) => PanelsChanged -= RefreshPanelOverview;

        }

        private void InitNotifyIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon();
            _notifyIcon.Icon = new System.Drawing.Icon("Resources/desktopplus_icon.ico");
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "DesktopPlus";
            UpdateNotifyIconMenu();
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
            _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        }

        private void UpdateNotifyIconMenu()
        {
            if (_notifyIcon == null) return;
            _notifyIcon.ContextMenuStrip = null;
            _trayMenuWindow?.RefreshTexts();
        }

        private void NotifyIcon_MouseUp(object? sender, WinForms.MouseEventArgs e)
        {
            if (e.Button != WinForms.MouseButtons.Right) return;

            Dispatcher.Invoke(ShowTrayMenuAtCursor);
        }

        private void CloseTrayMenuWindow()
        {
            _trayMenuWindow?.RequestClose();
        }

        private void ShowTrayMenuAtCursor()
        {
            CloseTrayMenuWindow();

            var trayWindow = new TrayMenuWindow(ShowMainWindow, ExitApplication);
            _trayMenuWindow = trayWindow;
            PositionTrayMenu(trayWindow);
            trayWindow.Show();
            trayWindow.Activate();
            trayWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_trayMenuWindow, trayWindow))
                {
                    _trayMenuWindow = null;
                }
            };
        }

        private static void PositionTrayMenu(Window menu)
        {
            var mouse = WinForms.Control.MousePosition;
            var workArea = SystemParameters.WorkArea;

            double x = mouse.X - menu.Width + 8;
            double y = mouse.Y - menu.Height - 8;

            if (x < workArea.Left + 8)
            {
                x = workArea.Left + 8;
            }
            if (x + menu.Width > workArea.Right - 8)
            {
                x = workArea.Right - menu.Width - 8;
            }
            if (y < workArea.Top + 8)
            {
                y = mouse.Y + 8;
            }
            if (y + menu.Height > workArea.Bottom - 8)
            {
                y = workArea.Bottom - menu.Height - 8;
            }

            menu.Left = x;
            menu.Top = y;
        }

        private void ExitApp_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        private void TrySetWindowIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "desktopplus_icon.ico");
                if (!File.Exists(iconPath)) return;

                using var stream = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Icon = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Icon.Freeze();
            }
            catch
            {
                // Ignore invalid or missing icon assets to keep the window usable.
            }
        }

        private static bool IsStartWithWindowsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
                if (key == null) return false;
                var value = key.GetValue(StartupRegistryValue) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }

        private void SetStartWithWindows(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
                if (key == null) return;

                if (enabled)
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName
                        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (string.IsNullOrWhiteSpace(exePath)) return;
                    key.SetValue(StartupRegistryValue, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(StartupRegistryValue, false);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgStartupError"), ex.Message),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ShowMainWindow()
        {
            CloseTrayMenuWindow();
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ExitApplication()
        {
            _isExit = true;
            IsExiting = true;
            CloseTrayMenuWindow();
            _notifyIcon?.Dispose();
            SaveSettingsImmediate();
            Application.Current.Shutdown();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (IsSourceInsideButton(e.OriginalSource as DependencyObject)) return;

            if (e.ClickCount == 2)
            {
                MaximizeRestore_Click(sender, new RoutedEventArgs());
                return;
            }

            try
            {
                if (WindowState == WindowState.Maximized)
                {
                    var mouseInWindow = e.GetPosition(this);
                    var screenPoint = PointToScreen(mouseInWindow);

                    double ratioX = ActualWidth > 0 ? mouseInWindow.X / ActualWidth : 0.5;
                    var restore = RestoreBounds;
                    double targetWidth = restore.Width > 0 ? restore.Width : Width;
                    double targetHeight = restore.Height > 0 ? restore.Height : Height;

                    WindowState = WindowState.Normal;
                    Width = targetWidth;
                    Height = targetHeight;
                    Left = screenPoint.X - (targetWidth * ratioX);
                    Top = Math.Max(SystemParameters.WorkArea.Top, screenPoint.Y - 12);
                }

                DragMove();
            }
            catch
            {
                // Ignore invalid drag transitions.
            }
        }

        private static bool IsSourceInsideButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is System.Windows.Controls.Button) return true;
                source = GetParentObject(source);
            }
            return false;
        }

        private static DependencyObject? GetParentObject(DependencyObject? source)
        {
            if (source == null) return null;

            if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
            {
                return VisualTreeHelper.GetParent(source);
            }

            return LogicalTreeHelper.GetParent(source);
        }

        private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T typed) return typed;
                source = GetParentObject(source);
            }

            return null;
        }

        private static bool IsInsidePopupRoot(DependencyObject? source)
        {
            while (source != null)
            {
                if (source.GetType().Name.IndexOf("PopupRoot", StringComparison.Ordinal) >= 0 ||
                    source is System.Windows.Controls.Primitives.Popup)
                {
                    return true;
                }
                source = GetParentObject(source);
            }

            return false;
        }

        private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MainScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (MainScrollViewer != null)
            {
                if (e.OriginalSource is DependencyObject source)
                {
                    if (FindAncestor<System.Windows.Controls.ComboBox>(source) != null ||
                        FindAncestor<System.Windows.Controls.ComboBoxItem>(source) != null ||
                        IsInsidePopupRoot(source))
                    {
                        return;
                    }

                    var sourceScrollViewer = FindAncestor<System.Windows.Controls.ScrollViewer>(source);
                    if (sourceScrollViewer != null && !ReferenceEquals(sourceScrollViewer, MainScrollViewer))
                    {
                        return;
                    }
                }

                double newOffset = MainScrollViewer.VerticalOffset - (e.Delta * 0.5);
                newOffset = Math.Max(0, Math.Min(newOffset, MainScrollViewer.ScrollableHeight));
                MainScrollViewer.ScrollToVerticalOffset(newOffset);
                e.Handled = true;
            }
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isExit) return;

            if (string.Equals(_closeBehavior, CloseBehaviorExit, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                ExitApplication();
                return;
            }

            e.Cancel = true;
            WindowState = WindowState.Minimized;
        }

        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            // Früher wurde das Fenster bei Minimierung ausgeblendet, was zu
            // Verwirrung führte. Jetzt bleibt es normal minimiert sichtbar.
        }

        public static void NotifyPanelsChanged()
        {
            PanelsChanged?.Invoke();
        }

        private List<AppearancePreset> GetDefaultPresets()
        {
            return new List<AppearancePreset>
            {
                new AppearancePreset { Name = "Noir", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#242833", BackgroundOpacity=0.84, HeaderColor="#2A303B", AccentColor="#6E8BFF", FolderTextColor="#6E8BFF", CornerRadius=14, ShadowOpacity=0.3, ShadowBlur=20, GlassEnabled = true } },
                new AppearancePreset { Name = "Slate", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#1C2430", BackgroundOpacity=0.82, HeaderColor="#192029", AccentColor="#6BD5C1", FolderTextColor="#6BD5C1", CornerRadius=12, ShadowOpacity=0.3, ShadowBlur=16, GlassEnabled = true } },
                new AppearancePreset { Name = "Frost", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#0F141C", BackgroundOpacity=0.8, HeaderColor="#101723", AccentColor="#64A9FF", FolderTextColor="#64A9FF", CornerRadius=16, ShadowOpacity=0.28, ShadowBlur=20, GlassEnabled = true } },
                new AppearancePreset { Name = "Carbon", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#161616", BackgroundOpacity=0.86, HeaderColor="#1E1E1E", AccentColor="#F5A524", FolderTextColor="#F5A524", CornerRadius=10, ShadowOpacity=0.32, ShadowBlur=14, GlassEnabled = true } },
                new AppearancePreset { Name = "Emerald", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#0F1714", BackgroundOpacity=0.82, HeaderColor="#12211B", AccentColor="#4ADE80", FolderTextColor="#4ADE80", CornerRadius=13, ShadowOpacity=0.33, ShadowBlur=18, GlassEnabled = true } },
                new AppearancePreset { Name = "Rosé", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#1B1418", BackgroundOpacity=0.82, HeaderColor="#221820", AccentColor="#FF7EB6", FolderTextColor="#FF7EB6", CornerRadius=14, ShadowOpacity=0.3, ShadowBlur=16, GlassEnabled = true } },
                new AppearancePreset { Name = "Cobalt", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#0B1624", BackgroundOpacity=0.82, HeaderColor="#0E1C2E", AccentColor="#3B82F6", FolderTextColor="#3B82F6", CornerRadius=12, ShadowOpacity=0.34, ShadowBlur=18, GlassEnabled = true } },
                new AppearancePreset { Name = "Graphite", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#202327", BackgroundOpacity=0.84, HeaderColor="#252A30", AccentColor="#A3B1C2", FolderTextColor="#A3B1C2", CornerRadius=11, ShadowOpacity=0.27, ShadowBlur=14, GlassEnabled = true } },
                new AppearancePreset { Name = "Sand", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#1B1916", BackgroundOpacity=0.84, HeaderColor="#25221D", AccentColor="#E8B76B", FolderTextColor="#E8B76B", CornerRadius=12, ShadowOpacity=0.3, ShadowBlur=16, GlassEnabled = true } },
                new AppearancePreset { Name = "Mint", IsBuiltIn = true, Settings = new AppearanceSettings { BackgroundColor="#101817", BackgroundOpacity=0.82, HeaderColor="#13201D", AccentColor="#70E4C6", FolderTextColor="#70E4C6", CornerRadius=14, ShadowOpacity=0.32, ShadowBlur=18, GlassEnabled = true } },
            };
        }

        private void OpenDesktopPanel_Click(object sender, RoutedEventArgs e)
        {
            var selectedPreset = (PresetComboTop?.SelectedItem as AppearancePreset)?.Name ?? DefaultPresetName;
            var panel = CreatePanelWithPreset(selectedPreset);
            panel.Show();
            PanelsChanged?.Invoke();
        }

        private void ApplyGeneralSettingsToUi()
        {
            _suspendGeneralHandlers = true;
            if (LanguageCombo != null)
            {
                LanguageCombo.SelectedValue = _languageCode;
            }
            if (StartupToggle != null)
            {
                StartupToggle.IsChecked = _startWithWindows;
            }
            if (CloseBehaviorCombo != null)
            {
                CloseBehaviorCombo.SelectedValue = _closeBehavior;
            }
            _suspendGeneralHandlers = false;
        }

        private void LanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suspendGeneralHandlers) return;
            if (LanguageCombo?.SelectedValue is string code)
            {
                _languageCode = code;
                ApplyLanguage(_languageCode);
                RefreshPanelOverview();
                RefreshLayoutList();
                RefreshPresetSelectors();
                SaveSettings();
            }
        }

        private void StartupToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendGeneralHandlers) return;
            _startWithWindows = StartupToggle?.IsChecked == true;
            SetStartWithWindows(_startWithWindows);
            SaveSettings();
        }

        private void CloseBehaviorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suspendGeneralHandlers) return;
            if (CloseBehaviorCombo?.SelectedValue is string value)
            {
                _closeBehavior = value;
                SaveSettings();
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PopulateAppearanceInputs(Appearance);
            UpdatePreview(Appearance);
            RefreshPresetSelectors();
            RefreshLayoutList();
            ApplyGeneralSettingsToUi();
            _isUiReady = true;

            if (MainTabs != null)
            {
                AnimateTabContent(MainTabs);
            }
        }

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TabControl tabControl) return;
            if (!ReferenceEquals(e.OriginalSource, tabControl)) return;
            AnimateTabContent(tabControl);
        }

        private static void AnimateTabContent(System.Windows.Controls.TabControl tabControl)
        {
            if (tabControl.Template.FindName("TabContentHost", tabControl) is not FrameworkElement host) return;

            host.BeginAnimation(UIElement.OpacityProperty, null);
            var shift = new TranslateTransform();
            host.RenderTransform = shift;

            host.Opacity = 0;
            shift.Y = 8;

            var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = easing
            };
            var slide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = easing
            };

            host.BeginAnimation(UIElement.OpacityProperty, fade);
            shift.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private static T? FindChild<T>(DependencyObject parent, string childName) where T : FrameworkElement
        {
            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T tChild && (string.IsNullOrEmpty(childName) || tChild.Name == childName))
                {
                    return tChild;
                }

                var result = FindChild<T>(child, childName);
                if (result != null) return result;
            }
            return null;
        }

        private static WindowData CloneWindowData(WindowData source)
        {
            return new WindowData
            {
                PanelId = source.PanelId ?? "",
                PanelType = source.PanelType ?? "",
                FolderPath = source.FolderPath ?? "",
                DefaultFolderPath = source.DefaultFolderPath ?? "",
                Left = source.Left,
                Top = source.Top,
                Width = source.Width,
                Height = source.Height,
                Zoom = source.Zoom,
                IsCollapsed = source.IsCollapsed,
                IsHidden = source.IsHidden,
                CollapsedTop = source.CollapsedTop,
                BaseTop = source.BaseTop,
                PanelTitle = source.PanelTitle ?? "",
                PresetName = source.PresetName ?? "",
                ShowHidden = source.ShowHidden,
                ShowSettingsButton = source.ShowSettingsButton,
                ExpandOnHover = source.ExpandOnHover,
                OpenFoldersExternally = source.OpenFoldersExternally,
                PinnedItems = source.PinnedItems?.ToList() ?? new List<string>()
            };
        }

        private static void CopyWindowData(WindowData source, WindowData target)
        {
            target.PanelId = source.PanelId ?? "";
            target.PanelType = source.PanelType ?? "";
            target.FolderPath = source.FolderPath ?? "";
            target.DefaultFolderPath = source.DefaultFolderPath ?? "";
            target.Left = source.Left;
            target.Top = source.Top;
            target.Width = source.Width;
            target.Height = source.Height;
            target.Zoom = source.Zoom;
            target.IsCollapsed = source.IsCollapsed;
            target.IsHidden = source.IsHidden;
            target.CollapsedTop = source.CollapsedTop;
            target.BaseTop = source.BaseTop;
            target.PanelTitle = source.PanelTitle ?? "";
            target.PresetName = source.PresetName ?? "";
            target.ShowHidden = source.ShowHidden;
            target.ShowSettingsButton = source.ShowSettingsButton;
            target.ExpandOnHover = source.ExpandOnHover;
            target.OpenFoldersExternally = source.OpenFoldersExternally;
            target.PinnedItems = source.PinnedItems?.ToList() ?? new List<string>();
        }

        private static AppearanceSettings CloneAppearance(AppearanceSettings source)
        {
            return new AppearanceSettings
            {
                BackgroundColor = source.BackgroundColor,
                BackgroundOpacity = source.BackgroundOpacity,
                HeaderColor = source.HeaderColor,
                AccentColor = source.AccentColor,
                TextColor = source.TextColor,
                MutedTextColor = source.MutedTextColor,
                FolderTextColor = source.FolderTextColor,
                FontFamily = source.FontFamily,
                TitleFontSize = source.TitleFontSize,
                ItemFontSize = source.ItemFontSize,
                CornerRadius = source.CornerRadius,
                ShadowOpacity = source.ShadowOpacity,
                ShadowBlur = source.ShadowBlur,
                BackgroundMode = source.BackgroundMode,
                BackgroundImagePath = source.BackgroundImagePath,
                BackgroundImageOpacity = source.BackgroundImageOpacity,
                GlassEnabled = source.GlassEnabled,
                ImageStretchFill = source.ImageStretchFill,
                Pattern = source.Pattern
            };
        }
    }
}

