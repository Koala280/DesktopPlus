using System;
using System.Collections.Generic;
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

        private void LoadSettings()
        {
            _hideMainWindowOnStartup = false;
            if (!File.Exists(settingsFilePath)) return;

            try
            {
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
                savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
                Appearance = state.Appearance ?? new AppearanceSettings();
                Layouts = state.Layouts ?? new List<LayoutDefinition>();
                _languageCode = string.IsNullOrWhiteSpace(state.Language) ? DefaultLanguageCode : state.Language;
                if (!LocalizationData.ContainsKey(_languageCode))
                {
                    _languageCode = DefaultLanguageCode;
                }
                CurrentLanguageCode = _languageCode;
                _closeBehavior = string.IsNullOrWhiteSpace(state.CloseBehavior) ? CloseBehaviorMinimize : state.CloseBehavior;
                if (!string.Equals(_closeBehavior, CloseBehaviorMinimize, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_closeBehavior, CloseBehaviorExit, StringComparison.OrdinalIgnoreCase))
                {
                    _closeBehavior = CloseBehaviorMinimize;
                }
                bool registryStartup = IsStartWithWindowsEnabled();
                _startWithWindows = state.StartWithWindows || registryStartup;
                if (_startWithWindows && !registryStartup)
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
                int openedPanels = 0;
                foreach (var winData in savedWindows)
                {
                    if (winData.IsHidden) continue;
                    var kind = ResolvePanelKind(winData);
                    if (kind == PanelKind.Folder)
                    {
                        string folderPath = ResolvePreferredFolderPath(winData);
                        if (!Directory.Exists(folderPath)) continue;
                    }
                    else if (kind == PanelKind.None)
                    {
                        continue;
                    }

                    var opened = Dispatcher.Invoke(() => OpenPanelFromData(winData));
                    if (opened != null)
                    {
                        openedPanels++;
                    }
                }
                _hideMainWindowOnStartup = openedPanels > 0 && IsStartupLaunch();

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
        }

        public static void SaveSettings()
        {
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
                var open = System.Windows.Application.Current.Windows
                    .OfType<DesktopPanel>()
                    .Where(IsUserPanel)
                    .Where(win => win.PanelType != PanelKind.None ||
                                  !string.IsNullOrWhiteSpace(win.currentFolderPath) ||
                                  win.PinnedItems.Count > 0)
                    .Select(BuildWindowDataFromPanel)
                    .ToList();

                var dict = CreateWindowDataMap(savedWindows, rewriteDuplicates: true);

                foreach (var item in open)
                {
                    NormalizeWindowData(item);
                    dict[GetPanelKey(item)] = item;
                }

                savedWindows = dict.Values
                    .OrderBy(x => string.IsNullOrWhiteSpace(x.PanelTitle) ? x.FolderPath : x.PanelTitle)
                    .ThenBy(x => x.FolderPath)
                    .ToList();

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
