using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private static List<WindowData> savedWindows = new List<WindowData>();
        public static AppearanceSettings Appearance { get; private set; } = new AppearanceSettings();
        public static List<AppearancePreset> Presets { get; private set; } = new List<AppearancePreset>();
        public static List<LayoutDefinition> Layouts { get; private set; } = new List<LayoutDefinition>();
        private static readonly string settingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPlus_Settings.json"
        );
        private static readonly JsonSerializerOptions SettingsJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals |
                             JsonNumberHandling.AllowReadingFromString
        };

        private static DispatcherTimer? _saveDebounceTimer;
        private static readonly object _saveTimerLock = new object();
        private static bool _isLoadingSettings;

        private static void ResetSavedFolderPathsToDefaults(IEnumerable<WindowData>? windows)
        {
            if (windows == null)
            {
                return;
            }

            foreach (WindowData? window in windows)
            {
                if (window == null)
                {
                    continue;
                }

                NormalizeWindowData(window);

                if (ResolvePanelKind(window) == PanelKind.Folder &&
                    !string.IsNullOrWhiteSpace(window.DefaultFolderPath) &&
                    Directory.Exists(window.DefaultFolderPath))
                {
                    window.FolderPath = window.DefaultFolderPath;
                }

                if (window.Tabs == null || window.Tabs.Count == 0)
                {
                    continue;
                }

                foreach (PanelTabData? tab in window.Tabs)
                {
                    if (tab == null)
                    {
                        continue;
                    }

                    if (ResolvePanelKind(tab) == PanelKind.Folder &&
                        !string.IsNullOrWhiteSpace(tab.DefaultFolderPath) &&
                        Directory.Exists(tab.DefaultFolderPath))
                    {
                        tab.FolderPath = tab.DefaultFolderPath;
                    }
                }
            }
        }

        private void LoadSettings()
        {
            _isLoadingSettings = true;
            try
            {
                _hideMainWindowOnStartup = false;
                LoadCustomLanguagesFromDisk();
                if (!File.Exists(settingsFilePath)) return;

                string json = File.ReadAllText(settingsFilePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                AppState state;

                try
                {
                    state = JsonSerializer.Deserialize<AppState>(json, SettingsJsonOptions) ?? new AppState();
                }
                catch
                {
                    var legacy = JsonSerializer.Deserialize<List<WindowData>>(json, SettingsJsonOptions) ?? new List<WindowData>();
                    state = new AppState { Windows = legacy };
                }

                savedWindows = state.Windows ?? new List<WindowData>();
                foreach (var window in savedWindows)
                {
                    NormalizeWindowData(window);
                }
                ResetSavedFolderPathsToDefaults(savedWindows);
                savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
                PruneRedundantSavedWindows();
                Appearance = state.Appearance ?? new AppearanceSettings();
                Layouts = state.Layouts ?? new List<LayoutDefinition>();
                _languageCode = string.IsNullOrWhiteSpace(state.Language) ? DefaultLanguageCode : state.Language;
                if (!LocalizationData.ContainsKey(_languageCode))
                {
                    _languageCode = DefaultLanguageCode;
                }
                CurrentLanguageCode = _languageCode;
                _closeBehavior = string.IsNullOrWhiteSpace(state.CloseBehavior) ? CloseBehaviorMinimize : state.CloseBehavior;
                _autoCheckUpdates = state.AutoCheckUpdates;
                if (!string.Equals(_closeBehavior, CloseBehaviorMinimize, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_closeBehavior, CloseBehaviorExit, StringComparison.OrdinalIgnoreCase))
                {
                    _closeBehavior = CloseBehaviorMinimize;
                }
                bool startupRegistrationPresent = IsStartWithWindowsEnabled();
                bool preferredStartupRegistrationPresent = HasPreferredStartupRegistration();
                _startWithWindows = state.StartWithWindows || startupRegistrationPresent;
                if (_startWithWindows && !preferredStartupRegistrationPresent)
                {
                    SetStartWithWindows(true);
                }
                _desktopAutoSort = state.DesktopAutoSort ?? new DesktopAutoSortSettings();
                NormalizeDesktopAutoSortSettings();
                _globalShortcuts = state.GlobalShortcuts ?? new GlobalShortcutSettings();
                NormalizeGlobalShortcutSettings();
                _layoutDefaultPresetName = string.IsNullOrWhiteSpace(state.LayoutDefaultPresetName)
                    ? DefaultPresetName
                    : state.LayoutDefaultPresetName;
                foreach (var layout in Layouts)
                {
                    layout.Panels ??= new List<WindowData>();
                    layout.Appearance ??= new AppearanceSettings();
                    NormalizeLayoutPanelDefaults(layout);
                    if (string.IsNullOrWhiteSpace(layout.DefaultPanelPresetName))
                    {
                        layout.DefaultPanelPresetName = _layoutDefaultPresetName;
                    }
                    foreach (var panel in layout.Panels)
                    {
                        NormalizeWindowData(panel);
                    }
                    layout.Panels = CreateWindowDataMap(layout.Panels, rewriteDuplicates: true).Values.ToList();
                }

                var defaults = GetDefaultPresets();
                var defaultNames = new HashSet<string>(defaults.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                var loadedPresets = state.Presets ?? new List<AppearancePreset>();

                var mergedPresets = new List<AppearancePreset>();
                foreach (var preset in defaults)
                {
                    mergedPresets.Add(new AppearancePreset
                    {
                        Name = preset.Name,
                        Settings = CloneAppearance(preset.Settings ?? new AppearanceSettings()),
                        IsBuiltIn = true
                    });
                }

                var customPresets = loadedPresets
                    .Where(p => p != null &&
                                !string.IsNullOrWhiteSpace(p.Name) &&
                                !defaultNames.Contains(p.Name))
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last());

                foreach (var preset in customPresets)
                {
                    mergedPresets.Add(new AppearancePreset
                    {
                        Name = preset.Name,
                        Settings = CloneAppearance(preset.Settings ?? new AppearanceSettings()),
                        IsBuiltIn = false
                    });
                }

                Presets = mergedPresets
                    .OrderBy(p => p.IsBuiltIn ? 0 : 1)
                    .ThenBy(p => p.Name)
                    .ToList();
                if (!Presets.Any(p => string.Equals(p.Name, _layoutDefaultPresetName, StringComparison.OrdinalIgnoreCase)))
                {
                    _layoutDefaultPresetName = DefaultPresetName;
                }
                foreach (var layout in Layouts)
                {
                    if (!Presets.Any(p => string.Equals(p.Name, layout.DefaultPanelPresetName, StringComparison.OrdinalIgnoreCase)))
                    {
                        layout.DefaultPanelPresetName = _layoutDefaultPresetName;
                    }
                }
                bool startupLaunch = IsStartupLaunch();
                RestoreSavedPanels(onlyWhenNoPanelsOpen: false);
                _hideMainWindowOnStartup = startupLaunch;

                AppearanceChanged?.Invoke();
                NotifyPanelsChanged();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgRestoreError"), ex.Message),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private void RestoreSavedPanels(bool onlyWhenNoPanelsOpen)
        {
            var openPanels = CreateOpenPanelMap(
                System.Windows.Application.Current.Windows.OfType<DesktopPanel>().Where(IsUserPanel));
            if (onlyWhenNoPanelsOpen && openPanels.Count > 0)
            {
                return;
            }

            foreach (var winData in savedWindows)
            {
                if (winData == null || winData.IsHidden)
                {
                    continue;
                }

                try
                {
                    NormalizeWindowData(winData);
                    if (winData.Tabs != null && winData.Tabs.Count > 0 && winData.Tabs.All(tab => tab.IsHidden))
                    {
                        winData.IsHidden = true;
                        continue;
                    }

                    PanelKind kind = ResolvePanelKind(winData);
                    if (kind == PanelKind.None)
                    {
                        continue;
                    }

                    if (kind == PanelKind.Folder)
                    {
                        string folderPath = ResolvePreferredFolderPath(winData);
                        if (!Directory.Exists(folderPath))
                        {
                            continue;
                        }
                    }

                    string panelKey = GetPanelKey(winData);
                    if (openPanels.ContainsKey(panelKey))
                    {
                        continue;
                    }

                    var panel = OpenPanelFromData(winData);
                    openPanels[panelKey] = panel;
                }
                catch (Exception ex)
                {
                    string panelLabel = !string.IsNullOrWhiteSpace(winData.PanelTitle)
                        ? winData.PanelTitle
                        : winData.FolderPath ?? "(list)";
                    Debug.WriteLine($"Failed to restore panel '{panelLabel}': {ex}");
                }
            }
        }

        public static void SaveSettings()
        {
            if (_isLoadingSettings)
            {
                return;
            }

            lock (_saveTimerLock)
            {
                if (_saveDebounceTimer == null)
                {
                    _saveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(300)
                    };
                    _saveDebounceTimer.Tick += (s, e) =>
                    {
                        _saveDebounceTimer.Stop();
                        SaveSettingsInternal();
                    };
                }

                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        }

        public static void SaveSettingsImmediate()
        {
            if (_isLoadingSettings)
            {
                return;
            }

            lock (_saveTimerLock)
            {
                _saveDebounceTimer?.Stop();
            }
            SaveSettingsInternal();
        }

        private static void SaveSettingsInternal()
        {
            try
            {
                var openPanels = System.Windows.Application.Current.Windows
                    .OfType<DesktopPanel>()
                    .Where(IsUserPanel)
                    .Where(win => win.PanelType != PanelKind.None ||
                                  !string.IsNullOrWhiteSpace(win.currentFolderPath) ||
                                  win.PinnedItems.Count > 0)
                    .ToList();

                var dict = CreateWindowDataMap(savedWindows, rewriteDuplicates: true);

                foreach (var panel in openPanels)
                {
                    var item = BuildWindowDataFromPanel(panel);
                    string panelKey = GetPanelKey(item);

                    if (panel.IsVisible)
                    {
                        item.IsHidden = false;
                    }
                    else if (IsExiting)
                    {
                        // During shutdown/update install, WPF can transiently report panels as invisible.
                        // Preserve explicit hidden state instead of turning every panel into hidden=true.
                        if (dict.TryGetValue(panelKey, out var existing))
                        {
                            item.IsHidden = existing.IsHidden;
                        }
                        else
                        {
                            item.IsHidden = false;
                        }
                    }

                    NormalizeWindowData(item);
                    dict[panelKey] = item;
                }

                savedWindows = dict.Values
                    .OrderBy(x => string.IsNullOrWhiteSpace(x.PanelTitle) ? x.FolderPath : x.PanelTitle)
                    .ThenBy(x => x.FolderPath)
                    .ToList();
                PruneRedundantSavedWindows(openPanels);

                var mainWindow = System.Windows.Application.Current?.MainWindow as MainWindow;
                string language = mainWindow?._languageCode ?? CurrentLanguageCode;
                bool startWithWindows = mainWindow?._startWithWindows ?? IsStartWithWindowsEnabled();
                string closeBehavior = mainWindow?._closeBehavior ?? CloseBehaviorMinimize;
                DesktopAutoSortSettings desktopAutoSort = mainWindow?._desktopAutoSort ?? new DesktopAutoSortSettings();
                GlobalShortcutSettings globalShortcuts = mainWindow?._globalShortcuts ?? new GlobalShortcutSettings();
                string layoutDefaultPreset = mainWindow?._layoutDefaultPresetName ?? DefaultPresetName;
                if (string.IsNullOrWhiteSpace(layoutDefaultPreset))
                {
                    layoutDefaultPreset = DefaultPresetName;
                }
                string activeLayoutName = "";

                var state = new AppState
                {
                    Windows = savedWindows,
                    Appearance = Appearance ?? new AppearanceSettings(),
                    Presets = Presets,
                    Layouts = Layouts,
                    LayoutDefaultPresetName = layoutDefaultPreset,
                    ActiveLayoutName = activeLayoutName,
                    Language = language,
                    StartWithWindows = startWithWindows,
                    AutoCheckUpdates = mainWindow?._autoCheckUpdates ?? false,
                    CloseBehavior = closeBehavior,
                    DesktopAutoSort = desktopAutoSort,
                    GlobalShortcuts = new GlobalShortcutSettings
                    {
                        HidePanelsHotkey = globalShortcuts.HidePanelsHotkey,
                        ForegroundPanelsHotkey = globalShortcuts.ForegroundPanelsHotkey
                    }
                };

                string json = JsonSerializer.Serialize(
                    state,
                    new JsonSerializerOptions(SettingsJsonOptions)
                    {
                        WriteIndented = true
                    });
                File.WriteAllText(settingsFilePath, json);
                NotifyPanelsChanged();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(GetString("Loc.MsgSaveError"), ex.Message),
                    GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public static void UpdateAppearance(AppearanceSettings newAppearance)
        {
            Appearance = newAppearance ?? new AppearanceSettings();
            AppearanceChanged?.Invoke();
            SaveSettings();
        }

        public static AppearanceSettings GetPresetSettings(string presetName)
        {
            if (!string.IsNullOrWhiteSpace(presetName))
            {
                var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
                if (preset != null) return preset.Settings;
            }
            return Presets.FirstOrDefault(p => p.Name == DefaultPresetName)?.Settings ?? Appearance;
        }
    }
}
