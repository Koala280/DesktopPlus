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
        private bool _suspendGeneralHandlers = false;
        private bool _isMainWindowDragActive = false;
        private bool _hideMainWindowOnStartup = false;
        private UIElement? _mainWindowDragHandle;
        private System.Windows.Point _mainWindowDragStartMouseScreen;
        private System.Windows.Point _mainWindowDragStartWindowPosition;
        private const string DefaultPresetName = "Graphite";
        private const string DefaultLanguageCode = "de";
        private const string CloseBehaviorMinimize = "Minimize";
        private const string CloseBehaviorExit = "Exit";
        private const string PreviewPanelIdPrefix = "preview:";
        private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRegistryValue = "DesktopPlus";
        private const string StartupLaunchArgument = "--startup";
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

        private static string ResolvePreferredFolderPath(WindowData data)
        {
            if (data == null)
            {
                return string.Empty;
            }

            string preferred = data.DefaultFolderPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(preferred) && Directory.Exists(preferred))
            {
                return preferred;
            }

            return data.FolderPath ?? string.Empty;
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

        private static bool IsPreviewSampleFolderPath(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            try
            {
                string candidate = Path.GetFullPath(folderPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string previewRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DesktopPlus", "PreviewPanelSample"))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return candidate.StartsWith(previewRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return folderPath.IndexOf("PreviewPanelSample", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       folderPath.IndexOf("DesktopPlus", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static bool IsInternalPreviewWindowData(WindowData? data)
        {
            if (data == null) return false;

            if (!string.IsNullOrWhiteSpace(data.PanelId) &&
                data.PanelId.StartsWith(PreviewPanelIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IsPreviewSampleFolderPath(data.FolderPath);
        }

        private static bool IsUserPanel(DesktopPanel? panel)
        {
            return panel != null && !panel.IsPreviewPanel;
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
                if (IsInternalPreviewWindowData(window)) continue;
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
                if (!IsUserPanel(panel)) continue;
                map[GetPanelKey(panel)] = panel;
            }
            return map;
        }

        private static void NormalizeWindowData(WindowData data)
        {
            if (data == null) return;
            data.PinnedItems ??= new List<string>();
            data.SearchVisibilityMode = DesktopPanel.NormalizeSearchVisibilityMode(data.SearchVisibilityMode);
            data.ViewMode = DesktopPanel.NormalizeViewMode(data.ViewMode);
            data.MetadataOrder = DesktopPanel.NormalizeMetadataOrder(data.MetadataOrder);
            if (!data.IsCollapsed)
            {
                if (data.ExpandedHeight <= 0 || data.ExpandedHeight < data.Height)
                {
                    data.ExpandedHeight = data.Height;
                }
            }
            else if (data.ExpandedHeight > 0 && data.ExpandedHeight < data.Height)
            {
                data.ExpandedHeight = data.Height;
            }
            var kind = ResolvePanelKind(data);
            if (string.IsNullOrWhiteSpace(data.PanelType))
            {
                data.PanelType = kind.ToString();
            }
            EnsurePanelId(data);

            if (data.Tabs != null)
            {
                foreach (var tab in data.Tabs)
                {
                    tab.PinnedItems ??= new List<string>();
                    tab.ViewMode = DesktopPanel.NormalizeViewMode(tab.ViewMode);
                    tab.MetadataOrder = DesktopPanel.NormalizeMetadataOrder(tab.MetadataOrder);
                }

                if (data.Tabs.Count > 0)
                {
                    data.ActiveTabIndex = Math.Max(0, Math.Min(data.ActiveTabIndex, data.Tabs.Count - 1));
                }
                else
                {
                    data.ActiveTabIndex = 0;
                }
            }
        }

        private static WindowData BuildWindowDataFromPanel(DesktopPanel panel)
        {
            if (!IsUserPanel(panel))
            {
                return new WindowData
                {
                    PanelId = string.IsNullOrWhiteSpace(panel.PanelId) ? $"{PreviewPanelIdPrefix}appearance" : panel.PanelId,
                    PanelType = PanelKind.None.ToString(),
                    FolderPath = panel.currentFolderPath ?? "",
                    IsHidden = true
                };
            }

            panel.SaveActiveTabState();

            var kind = ResolvePanelKind(panel);
            var folderPath = kind == PanelKind.Folder ? panel.currentFolderPath : "";
            var pinnedItems = kind == PanelKind.List
                ? panel.PinnedItems.ToList()
                : new List<string>();
            double currentHeight = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
            // Keep the last known expanded size even if a save happens mid-collapse animation.
            double rememberedExpandedHeight = panel.expandedHeight > 0 ? panel.expandedHeight : currentHeight;
            double persistedExpandedHeight = Math.Max(currentHeight, rememberedExpandedHeight);

            var wd = new WindowData
            {
                PanelId = string.IsNullOrWhiteSpace(panel.PanelId) ? (panel.PanelId = GeneratePanelId()) : panel.PanelId,
                PanelType = kind.ToString(),
                FolderPath = folderPath ?? "",
                DefaultFolderPath = panel.defaultFolderPath ?? "",
                Left = panel.Left,
                Top = panel.Top,
                Width = panel.Width,
                Height = currentHeight,
                ExpandedHeight = persistedExpandedHeight,
                Zoom = panel.zoomFactor,
                IsCollapsed = !panel.isContentVisible,
                IsHidden = !panel.IsVisible,
                CollapsedTop = panel.collapsedTopPosition,
                BaseTop = panel.baseTopPosition,
                IsBottomAnchored = panel.IsBottomAnchored,
                PanelTitle = panel.PanelTitle.Text,
                PresetName = string.IsNullOrWhiteSpace(panel.assignedPresetName) ? DefaultPresetName : panel.assignedPresetName,
                ShowHidden = panel.showHiddenItems,
                ShowParentNavigationItem = panel.showParentNavigationItem,
                ShowFileExtensions = panel.showFileExtensions,
                ShowSettingsButton = panel.showSettingsButton,
                ExpandOnHover = panel.expandOnHover,
                OpenFoldersExternally = panel.openFoldersExternally,
                ViewMode = panel.viewMode,
                ShowMetadataType = panel.showMetadataType,
                ShowMetadataSize = panel.showMetadataSize,
                ShowMetadataCreated = panel.showMetadataCreated,
                ShowMetadataModified = panel.showMetadataModified,
                ShowMetadataDimensions = panel.showMetadataDimensions,
                MetadataOrder = DesktopPanel.NormalizeMetadataOrder(panel.metadataOrder),
                MovementMode = panel.movementMode,
                SearchVisibilityMode = panel.searchVisibilityMode,
                PinnedItems = pinnedItems
            };

            if (panel.Tabs.Count > 1)
            {
                wd.Tabs = panel.Tabs.Select(t => new PanelTabData
                {
                    TabId = t.TabId,
                    TabName = t.TabName,
                    PanelType = t.PanelType,
                    FolderPath = t.FolderPath,
                    DefaultFolderPath = t.DefaultFolderPath,
                    ShowHidden = t.ShowHidden,
                    ShowParentNavigationItem = t.ShowParentNavigationItem,
                    ShowFileExtensions = t.ShowFileExtensions,
                    OpenFoldersExternally = t.OpenFoldersExternally,
                    ViewMode = t.ViewMode,
                    ShowMetadataType = t.ShowMetadataType,
                    ShowMetadataSize = t.ShowMetadataSize,
                    ShowMetadataCreated = t.ShowMetadataCreated,
                    ShowMetadataModified = t.ShowMetadataModified,
                    ShowMetadataDimensions = t.ShowMetadataDimensions,
                    MetadataOrder = DesktopPanel.NormalizeMetadataOrder(t.MetadataOrder),
                    PinnedItems = t.PinnedItems?.ToList() ?? new List<string>()
                }).ToList();
                wd.ActiveTabIndex = panel.ActiveTabIndex;
            }

            return wd;
        }

        public MainWindow()
        {
            InitializeComponent();
            InitializeGlobalShortcuts();
            AddHandler(MouseWheelEvent, new MouseWheelEventHandler(MainScrollViewer_PreviewMouseWheel), true);
            TrySetWindowIcon();
            LoadSettings();
            RegisterGlobalShortcuts();
            NormalizeDesktopAutoSortSettings();
            FileSearchIndex.EnsureStarted();
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
            this.Closed += (s, e) =>
            {
                PanelsChanged -= RefreshPanelOverview;
                StopDesktopAutoSortWatcher();
                CleanupGlobalShortcuts();
            };
            ConfigureDesktopAutoSortWatcher();
            ApplyStartupWindowVisibilityPreference();

        }

        private void ApplyStartupWindowVisibilityPreference()
        {
            if (!_hideMainWindowOnStartup || _isExit) return;
            _hideMainWindowOnStartup = false;

            ShowInTaskbar = false;
            if (WindowState != WindowState.Minimized)
            {
                WindowState = WindowState.Minimized;
            }
            Hide();
        }

        private void InitNotifyIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon();
            _notifyIcon.Icon = ResolveNotifyIcon();
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "DesktopPlus";
            UpdateNotifyIconMenu();
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
            _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        }

        private static System.Drawing.Icon ResolveNotifyIcon()
        {
            try
            {
                string iconPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Resources",
                    "desktopplus_icon.ico");

                if (File.Exists(iconPath))
                {
                    return new System.Drawing.Icon(iconPath);
                }
            }
            catch
            {
            }

            try
            {
                string exePath = GetCurrentExecutablePath();
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    var associatedIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (associatedIcon != null)
                    {
                        return associatedIcon;
                    }
                }
            }
            catch
            {
            }

            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
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
            // Force layout so ActualHeight/ActualWidth are available (SizeToContent)
            menu.Measure(new System.Windows.Size(menu.Width, double.PositiveInfinity));
            menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
            menu.UpdateLayout();

            double menuW = menu.DesiredSize.Width > 0 ? menu.DesiredSize.Width : menu.Width;
            double menuH = menu.DesiredSize.Height > 0 ? menu.DesiredSize.Height : 200;

            var mouse = WinForms.Control.MousePosition;
            var workArea = SystemParameters.WorkArea;

            double x = mouse.X - menuW + 8;
            double y = mouse.Y - menuH - 8;

            if (x < workArea.Left + 8)
                x = workArea.Left + 8;
            if (x + menuW > workArea.Right - 8)
                x = workArea.Right - menuW - 8;
            if (y < workArea.Top + 8)
                y = mouse.Y + 8;
            if (y + menuH > workArea.Bottom - 8)
                y = workArea.Bottom - menuH - 8;

            menu.Left = x;
            menu.Top = y;
        }

        private void ExitApp_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        private void TrySetWindowIcon()
        {
            AppIconLoader.TryApplyWindowIcon(this);
        }

        private static string GetCurrentExecutablePath()
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                return processPath;
            }

            try
            {
                using var currentProcess = Process.GetCurrentProcess();
                processPath = currentProcess.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    return processPath;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractExecutablePathFromStartupValue(string startupValue)
        {
            if (string.IsNullOrWhiteSpace(startupValue))
            {
                return string.Empty;
            }

            string value = startupValue.Trim();
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int closingQuote = value.IndexOf('"', 1);
                if (closingQuote > 1)
                {
                    return value.Substring(1, closingQuote - 1);
                }
            }

            int separator = value.IndexOfAny(new[] { ' ', '\t' });
            if (separator > 0)
            {
                return value.Substring(0, separator);
            }

            return value;
        }

        private static bool PathsPointToSameLocation(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                string normalizedLeft = Path.GetFullPath(left)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedRight = Path.GetFullPath(right)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsStartupLaunch()
        {
            try
            {
                return Environment.GetCommandLineArgs()
                    .Any(arg => string.Equals(arg, StartupLaunchArgument, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsStartWithWindowsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
                if (key == null) return false;
                var value = key.GetValue(StartupRegistryValue) as string;
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                string configuredPath = ExtractExecutablePathFromStartupValue(value);
                if (string.IsNullOrWhiteSpace(configuredPath) || !File.Exists(configuredPath))
                {
                    return false;
                }

                string currentPath = GetCurrentExecutablePath();
                if (string.IsNullOrWhiteSpace(currentPath))
                {
                    return true;
                }

                return PathsPointToSameLocation(configuredPath, currentPath);
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
                    string exePath = GetCurrentExecutablePath();
                    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
                    key.SetValue(StartupRegistryValue, $"\"{exePath}\" {StartupLaunchArgument}");
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
            ShowInTaskbar = true;
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }

            if (!IsVisible)
            {
                Show();
            }

            Activate();
            Focus();
        }

        private void ExitApplication()
        {
            _isExit = true;
            IsExiting = true;
            CloseTrayMenuWindow();
            StopDesktopAutoSortWatcher();
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
            if (sender is UIElement dragHandle)
            {
                BeginMainWindowDrag(dragHandle);
                e.Handled = true;
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

        private static bool CanScrollVertically(System.Windows.Controls.ScrollViewer scrollViewer, int wheelDelta)
        {
            if (scrollViewer.ScrollableHeight <= 0) return false;

            if (wheelDelta > 0)
            {
                return scrollViewer.VerticalOffset > 0;
            }

            if (wheelDelta < 0)
            {
                return scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
            }

            return false;
        }

        private System.Windows.Point GetMouseScreenPositionDip()
        {
            var raw = WinForms.Control.MousePosition;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                return source.CompositionTarget.TransformFromDevice.Transform(new System.Windows.Point(raw.X, raw.Y));
            }

            return new System.Windows.Point(raw.X, raw.Y);
        }

        private void BeginMainWindowDrag(UIElement dragHandle)
        {
            if (_isMainWindowDragActive || WindowState != WindowState.Normal) return;

            _mainWindowDragHandle = dragHandle;
            _mainWindowDragStartMouseScreen = GetMouseScreenPositionDip();
            _mainWindowDragStartWindowPosition = new System.Windows.Point(Left, Top);
            _isMainWindowDragActive = true;

            dragHandle.MouseMove += MainWindowDragHandle_MouseMove;
            dragHandle.MouseLeftButtonUp += MainWindowDragHandle_MouseLeftButtonUp;
            dragHandle.LostMouseCapture += MainWindowDragHandle_LostMouseCapture;
            dragHandle.CaptureMouse();
        }

        private void EndMainWindowDrag()
        {
            var handle = _mainWindowDragHandle;
            if (handle != null)
            {
                handle.MouseMove -= MainWindowDragHandle_MouseMove;
                handle.MouseLeftButtonUp -= MainWindowDragHandle_MouseLeftButtonUp;
                handle.LostMouseCapture -= MainWindowDragHandle_LostMouseCapture;
                if (handle.IsMouseCaptured)
                {
                    handle.ReleaseMouseCapture();
                }
                _mainWindowDragHandle = null;
            }

            _isMainWindowDragActive = false;
        }

        private void MainWindowDragHandle_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isMainWindowDragActive) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndMainWindowDrag();
                return;
            }

            System.Windows.Point current = GetMouseScreenPositionDip();
            double deltaX = current.X - _mainWindowDragStartMouseScreen.X;
            double deltaY = current.Y - _mainWindowDragStartMouseScreen.Y;
            Left = _mainWindowDragStartWindowPosition.X + deltaX;
            Top = _mainWindowDragStartWindowPosition.Y + deltaY;
        }

        private void MainWindowDragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndMainWindowDrag();
            e.Handled = true;
        }

        private void MainWindowDragHandle_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            EndMainWindowDrag();
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
                    if (sourceScrollViewer != null &&
                        !ReferenceEquals(sourceScrollViewer, MainScrollViewer) &&
                        CanScrollVertically(sourceScrollViewer, e.Delta))
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
            if (_isExit) return;

            if (WindowState == WindowState.Minimized)
            {
                ShowInTaskbar = false;
                Hide();
                return;
            }

            ShowInTaskbar = true;
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
            SaveSettings();
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
                RefreshDesktopAutoSortRuleViews();
                SetDesktopAutoSortStatus(_desktopAutoSort.AutoSortEnabled
                    ? GetString("Loc.AutoSortStatusEnabled")
                    : GetString("Loc.AutoSortStatusDisabled"));
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
            SetThemeSection("header", updatePreview: false);
            UpdatePreview(Appearance);
            RefreshPresetSelectors();
            RefreshLayoutList();
            ApplyGeneralSettingsToUi();
            ApplyDesktopAutoSortSettingsToUi();
            ApplyGlobalShortcutSettingsToUi();
            ConfigureDesktopAutoSortWatcher();
            _isUiReady = true;
            ApplyStartupWindowVisibilityPreference();

            if (MainTabs != null)
            {
                AnimateSlidingIndicator(MainTabs, instant: true);
            }
        }

        private bool _indicatorInitialized;

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TabControl tabControl) return;
            if (!ReferenceEquals(e.OriginalSource, tabControl)) return;
            AnimateSlidingIndicator(tabControl, !_indicatorInitialized);
        }

        private void AnimateSlidingIndicator(System.Windows.Controls.TabControl tabControl, bool instant = false)
        {
            if (tabControl.Template.FindName("SlidingIndicator", tabControl) is not Border indicator) return;
            if (tabControl.SelectedItem is not TabItem selectedTab) return;
            if (!selectedTab.IsLoaded)
            {
                selectedTab.Loaded += (_, _) => AnimateSlidingIndicator(tabControl, instant);
                return;
            }

            var tabPanel = tabControl.Template.FindName("HeaderPanel", tabControl) as System.Windows.Controls.Primitives.TabPanel;
            if (tabPanel == null) return;

            var transform = selectedTab.TransformToAncestor(tabPanel);
            var position = transform.Transform(new System.Windows.Point(0, 0));
            var targetX = position.X + 2;
            var targetWidth = selectedTab.ActualWidth;

            var duration = instant ? TimeSpan.Zero : TimeSpan.FromMilliseconds(300);
            var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

            var translateTransform = indicator.RenderTransform as TranslateTransform;
            if (translateTransform == null || translateTransform.IsFrozen)
            {
                translateTransform = new TranslateTransform();
                indicator.RenderTransform = translateTransform;
            }

            if (instant)
            {
                translateTransform.BeginAnimation(TranslateTransform.XProperty, null);
                indicator.BeginAnimation(Border.WidthProperty, null);
                translateTransform.X = targetX;
                indicator.Width = targetWidth;
            }
            else
            {
                var slideAnim = new DoubleAnimation(targetX, duration) { EasingFunction = easing };
                translateTransform.BeginAnimation(TranslateTransform.XProperty, slideAnim);

                var widthAnim = new DoubleAnimation(targetWidth, duration) { EasingFunction = easing };
                indicator.BeginAnimation(Border.WidthProperty, widthAnim);
            }

            _indicatorInitialized = true;
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
                ExpandedHeight = source.ExpandedHeight,
                Zoom = source.Zoom,
                IsCollapsed = source.IsCollapsed,
                IsHidden = source.IsHidden,
                CollapsedTop = source.CollapsedTop,
                BaseTop = source.BaseTop,
                IsBottomAnchored = source.IsBottomAnchored,
                PanelTitle = source.PanelTitle ?? "",
                PresetName = source.PresetName ?? "",
                ShowHidden = source.ShowHidden,
                ShowParentNavigationItem = source.ShowParentNavigationItem,
                ShowFileExtensions = source.ShowFileExtensions,
                ShowSettingsButton = source.ShowSettingsButton,
                ExpandOnHover = source.ExpandOnHover,
                OpenFoldersExternally = source.OpenFoldersExternally,
                ViewMode = source.ViewMode ?? DesktopPanel.ViewModeIcons,
                ShowMetadataType = source.ShowMetadataType,
                ShowMetadataSize = source.ShowMetadataSize,
                ShowMetadataCreated = source.ShowMetadataCreated,
                ShowMetadataModified = source.ShowMetadataModified,
                ShowMetadataDimensions = source.ShowMetadataDimensions,
                MetadataOrder = DesktopPanel.NormalizeMetadataOrder(source.MetadataOrder),
                MovementMode = source.MovementMode ?? "titlebar",
                SearchVisibilityMode = source.SearchVisibilityMode ?? DesktopPanel.SearchVisibilityAlways,
                PinnedItems = source.PinnedItems?.ToList() ?? new List<string>(),
                Tabs = source.Tabs?.Select(ClonePanelTabData).ToList(),
                ActiveTabIndex = source.ActiveTabIndex
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
            target.ExpandedHeight = source.ExpandedHeight;
            target.Zoom = source.Zoom;
            target.IsCollapsed = source.IsCollapsed;
            target.IsHidden = source.IsHidden;
            target.CollapsedTop = source.CollapsedTop;
            target.BaseTop = source.BaseTop;
            target.IsBottomAnchored = source.IsBottomAnchored;
            target.PanelTitle = source.PanelTitle ?? "";
            target.PresetName = source.PresetName ?? "";
            target.ShowHidden = source.ShowHidden;
            target.ShowParentNavigationItem = source.ShowParentNavigationItem;
            target.ShowFileExtensions = source.ShowFileExtensions;
            target.ShowSettingsButton = source.ShowSettingsButton;
            target.ExpandOnHover = source.ExpandOnHover;
            target.OpenFoldersExternally = source.OpenFoldersExternally;
            target.ViewMode = source.ViewMode ?? DesktopPanel.ViewModeIcons;
            target.ShowMetadataType = source.ShowMetadataType;
            target.ShowMetadataSize = source.ShowMetadataSize;
            target.ShowMetadataCreated = source.ShowMetadataCreated;
            target.ShowMetadataModified = source.ShowMetadataModified;
            target.ShowMetadataDimensions = source.ShowMetadataDimensions;
            target.MetadataOrder = DesktopPanel.NormalizeMetadataOrder(source.MetadataOrder);
            target.MovementMode = source.MovementMode ?? "titlebar";
            target.SearchVisibilityMode = source.SearchVisibilityMode ?? DesktopPanel.SearchVisibilityAlways;
            target.PinnedItems = source.PinnedItems?.ToList() ?? new List<string>();
            target.Tabs = source.Tabs?.Select(ClonePanelTabData).ToList();
            target.ActiveTabIndex = source.ActiveTabIndex;
        }

        private static PanelTabData ClonePanelTabData(PanelTabData source)
        {
            return new PanelTabData
            {
                TabId = source.TabId ?? "",
                TabName = source.TabName ?? "",
                PanelType = source.PanelType ?? "",
                FolderPath = source.FolderPath ?? "",
                DefaultFolderPath = source.DefaultFolderPath ?? "",
                ShowHidden = source.ShowHidden,
                ShowParentNavigationItem = source.ShowParentNavigationItem,
                ShowFileExtensions = source.ShowFileExtensions,
                OpenFoldersExternally = source.OpenFoldersExternally,
                ViewMode = source.ViewMode ?? DesktopPanel.ViewModeIcons,
                ShowMetadataType = source.ShowMetadataType,
                ShowMetadataSize = source.ShowMetadataSize,
                ShowMetadataCreated = source.ShowMetadataCreated,
                ShowMetadataModified = source.ShowMetadataModified,
                ShowMetadataDimensions = source.ShowMetadataDimensions,
                MetadataOrder = DesktopPanel.NormalizeMetadataOrder(source.MetadataOrder),
                PinnedItems = source.PinnedItems?.ToList() ?? new List<string>()
            };
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
                TabActiveColor = source.TabActiveColor,
                TabInactiveColor = source.TabInactiveColor,
                TabHoverColor = source.TabHoverColor,
                FontFamily = source.FontFamily,
                TitleFontSize = source.TitleFontSize,
                ItemFontSize = source.ItemFontSize,
                CornerRadius = source.CornerRadius,
                ShadowOpacity = source.ShadowOpacity,
                ShadowBlur = source.ShadowBlur,
                HeaderShadowOpacity = source.HeaderShadowOpacity,
                HeaderShadowBlur = source.HeaderShadowBlur,
                BodyShadowOpacity = source.BodyShadowOpacity,
                BodyShadowBlur = source.BodyShadowBlur,
                PatternColor = source.PatternColor,
                PatternOpacity = source.PatternOpacity,
                PatternTileSize = source.PatternTileSize,
                PatternStrokeThickness = source.PatternStrokeThickness,
                PatternCustomData = source.PatternCustomData,
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

