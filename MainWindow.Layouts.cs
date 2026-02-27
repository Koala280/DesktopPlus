using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Application = System.Windows.Application;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private sealed class LayoutPanelDefaultsSnapshot
        {
            public bool ShowHidden { get; set; }
            public bool ShowFileExtensions { get; set; } = true;
            public bool ExpandOnHover { get; set; } = true;
            public bool OpenFoldersExternally { get; set; }
            public bool ShowSettingsButton { get; set; } = true;
            public string MovementMode { get; set; } = "titlebar";
            public string SearchVisibilityMode { get; set; } = DesktopPanel.SearchVisibilityAlways;
        }

        private static string NormalizePanelMovementMode(string? mode)
        {
            if (string.Equals(mode, "button", StringComparison.OrdinalIgnoreCase))
            {
                return "button";
            }

            if (string.Equals(mode, "locked", StringComparison.OrdinalIgnoreCase))
            {
                return "locked";
            }

            return "titlebar";
        }

        private static void NormalizeLayoutPanelDefaults(LayoutDefinition layout)
        {
            if (layout == null)
            {
                return;
            }

            layout.PanelDefaultMovementMode = NormalizePanelMovementMode(layout.PanelDefaultMovementMode);
            layout.PanelDefaultSearchVisibilityMode = DesktopPanel.NormalizeSearchVisibilityMode(layout.PanelDefaultSearchVisibilityMode);
        }

        private static LayoutPanelDefaultsSnapshot CaptureLayoutPanelDefaults(LayoutDefinition layout)
        {
            NormalizeLayoutPanelDefaults(layout);
            return new LayoutPanelDefaultsSnapshot
            {
                ShowHidden = layout.PanelDefaultShowHidden,
                ShowFileExtensions = layout.PanelDefaultShowFileExtensions,
                ExpandOnHover = layout.PanelDefaultExpandOnHover,
                OpenFoldersExternally = layout.PanelDefaultOpenFoldersExternally,
                ShowSettingsButton = layout.PanelDefaultShowSettingsButton,
                MovementMode = layout.PanelDefaultMovementMode,
                SearchVisibilityMode = layout.PanelDefaultSearchVisibilityMode
            };
        }

        private static void ApplyLayoutPanelDefaults(LayoutDefinition layout, LayoutPanelDefaultsSnapshot defaults)
        {
            layout.PanelDefaultShowHidden = defaults.ShowHidden;
            layout.PanelDefaultShowFileExtensions = defaults.ShowFileExtensions;
            layout.PanelDefaultExpandOnHover = defaults.ExpandOnHover;
            layout.PanelDefaultOpenFoldersExternally = defaults.OpenFoldersExternally;
            layout.PanelDefaultShowSettingsButton = defaults.ShowSettingsButton;
            layout.PanelDefaultMovementMode = NormalizePanelMovementMode(defaults.MovementMode);
            layout.PanelDefaultSearchVisibilityMode = DesktopPanel.NormalizeSearchVisibilityMode(defaults.SearchVisibilityMode);
        }

        private static void CopyPanelBehaviorSettings(WindowData source, WindowData target)
        {
            target.PresetName = source.PresetName;
            target.ShowHidden = source.ShowHidden;
            target.ShowFileExtensions = source.ShowFileExtensions;
            target.ExpandOnHover = source.ExpandOnHover;
            target.OpenFoldersExternally = source.OpenFoldersExternally;
            target.ShowSettingsButton = source.ShowSettingsButton;
            target.MovementMode = NormalizePanelMovementMode(source.MovementMode);
            target.SearchVisibilityMode = DesktopPanel.NormalizeSearchVisibilityMode(source.SearchVisibilityMode);
        }

        private static void ApplyPanelBehaviorToOpenPanel(DesktopPanel panel, WindowData source)
        {
            bool hiddenChanged = panel.showHiddenItems != source.ShowHidden;
            bool fileExtensionsChanged = panel.showFileExtensions != source.ShowFileExtensions;
            string sourcePresetName = string.IsNullOrWhiteSpace(source.PresetName) ? DefaultPresetName : source.PresetName;

            if (!string.Equals(panel.assignedPresetName, sourcePresetName, StringComparison.OrdinalIgnoreCase))
            {
                panel.assignedPresetName = sourcePresetName;
                panel.ApplyAppearance(GetPresetSettings(sourcePresetName));
            }

            panel.showHiddenItems = source.ShowHidden;
            panel.showFileExtensions = source.ShowFileExtensions;
            panel.SetExpandOnHover(source.ExpandOnHover);
            panel.openFoldersExternally = source.OpenFoldersExternally;
            panel.showSettingsButton = source.ShowSettingsButton;
            panel.ApplySettingsButtonVisibility();
            panel.ApplyMovementMode(NormalizePanelMovementMode(source.MovementMode));
            panel.SetSearchVisibilityMode(source.SearchVisibilityMode);

            if ((hiddenChanged || fileExtensionsChanged) && !string.IsNullOrWhiteSpace(panel.currentFolderPath))
            {
                panel.LoadFolder(panel.currentFolderPath, false);
            }
            else if (fileExtensionsChanged && panel.PanelType == PanelKind.List)
            {
                panel.LoadList(panel.PinnedItems.ToArray(), false);
            }
        }

        private void RefreshLayoutList()
        {
            if (LayoutOverviewList == null || LayoutOverviewCount == null) return;

            var ordered = Layouts.OrderBy(l => l.Name).ToList();
            var items = ordered.Select(BuildLayoutOverviewItem).ToList();
            LayoutOverviewList.ItemsSource = items;
            LayoutOverviewCount.Text = string.Format(GetString("Loc.LayoutCount"), items.Count);
        }

        private LayoutOverviewItem BuildLayoutOverviewItem(LayoutDefinition layout)
        {
            int total = layout.Panels?.Count ?? 0;
            int hidden = layout.Panels?.Count(p => p.IsHidden) ?? 0;
            int visible = Math.Max(0, total - hidden);

            string summary = total == 0
                ? GetString("Loc.LayoutSummaryEmpty")
                : string.Format(GetString("Loc.LayoutSummaryVisibleHidden"), visible, hidden);
            if (!string.IsNullOrWhiteSpace(layout.ThemePresetName))
            {
                summary = $"{summary}, {GetString("Loc.LayoutSummaryTheme")}: {layout.ThemePresetName}";
            }

            string name = string.IsNullOrWhiteSpace(layout.Name) ? GetString("Loc.Untitled") : layout.Name;
            string defaultPresetName = ResolveLayoutDefaultPresetName(layout);
            layout.DefaultPanelPresetName = defaultPresetName;

            return new LayoutOverviewItem
            {
                Name = name,
                Summary = summary,
                DefaultPresetName = defaultPresetName,
                Layout = layout
            };
        }

        private string GetSelectedLayoutDefaultPresetName()
        {
            if (!string.IsNullOrWhiteSpace(_layoutDefaultPresetName) &&
                Presets.Any(p => string.Equals(p.Name, _layoutDefaultPresetName, StringComparison.OrdinalIgnoreCase)))
            {
                return _layoutDefaultPresetName;
            }

            return Presets.FirstOrDefault(p => string.Equals(p.Name, DefaultPresetName, StringComparison.OrdinalIgnoreCase))?.Name
                ?? Presets.FirstOrDefault()?.Name
                ?? DefaultPresetName;
        }

        private string ResolveLayoutDefaultPresetName(LayoutDefinition layout)
        {
            string candidate = !string.IsNullOrWhiteSpace(layout.DefaultPanelPresetName)
                ? layout.DefaultPanelPresetName
                : GetSelectedLayoutDefaultPresetName();

            if (string.IsNullOrWhiteSpace(candidate) ||
                !Presets.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = DefaultPresetName;
            }

            return candidate;
        }

        private static string EncodeLayoutPanelPreset(string panelPresetName, string layoutDefaultPresetName)
        {
            if (string.IsNullOrWhiteSpace(panelPresetName) ||
                string.Equals(panelPresetName, layoutDefaultPresetName, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            return panelPresetName;
        }

        private static string ResolveLayoutPanelPreset(string storedPresetName, string layoutDefaultPresetName)
        {
            if (string.IsNullOrWhiteSpace(storedPresetName))
            {
                return layoutDefaultPresetName;
            }

            return storedPresetName;
        }

        private static bool HasPersistableLayoutContent(WindowData data)
        {
            var kind = ResolvePanelKind(data);
            return kind switch
            {
                PanelKind.Folder => !string.IsNullOrWhiteSpace(data.FolderPath),
                PanelKind.List => data.PinnedItems != null && data.PinnedItems.Count > 0,
                _ => false
            };
        }

        public static string GetCurrentStandardPresetName()
        {
            var mainWindow = Application.Current?.MainWindow as MainWindow;
            string candidate = mainWindow?._layoutDefaultPresetName ?? DefaultPresetName;

            if (string.IsNullOrWhiteSpace(candidate) ||
                !Presets.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = Presets.FirstOrDefault(p => string.Equals(p.Name, DefaultPresetName, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? Presets.FirstOrDefault()?.Name
                    ?? DefaultPresetName;
            }

            return candidate;
        }

        private List<WindowData> CaptureOpenPanelsForLayout(string layoutDefaultPresetName)
        {
            var panels = Application.Current.Windows.OfType<DesktopPanel>()
                .Where(IsUserPanel)
                .Select(BuildWindowDataFromPanel)
                .Where(HasPersistableLayoutContent)
                .ToList();

            foreach (var panel in panels)
            {
                NormalizeWindowData(panel);
                panel.IsHidden = false;
                panel.PresetName = EncodeLayoutPanelPreset(panel.PresetName, layoutDefaultPresetName);
            }

            return CreateWindowDataMap(panels, rewriteDuplicates: true)
                .Values
                .Select(CloneWindowData)
                .ToList();
        }

        private void CreateLayoutFromCurrent_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            string defaultName = $"Layout {Layouts.Count + 1}";
            string? name = PromptName(GetString("Loc.PromptLayoutName"), defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            name = EnsureUniqueLayoutName(name);
            string layoutDefaultPresetName = GetSelectedLayoutDefaultPresetName();
            var layout = new LayoutDefinition
            {
                Name = name,
                ThemePresetName = GetSelectedPresetName(),
                DefaultPanelPresetName = layoutDefaultPresetName,
                PanelDefaultShowHidden = false,
                PanelDefaultShowFileExtensions = true,
                PanelDefaultExpandOnHover = true,
                PanelDefaultOpenFoldersExternally = false,
                PanelDefaultShowSettingsButton = true,
                PanelDefaultMovementMode = "titlebar",
                PanelDefaultSearchVisibilityMode = DesktopPanel.SearchVisibilityAlways,
                Appearance = CloneAppearance(Appearance),
                Panels = CaptureOpenPanelsForLayout(layoutDefaultPresetName)
            };
            NormalizeLayoutPanelDefaults(layout);
            Layouts.Add(layout);
            SaveSettings();
            RefreshLayoutList();
        }

        private void CreateEmptyLayout_Click(object sender, RoutedEventArgs e)
        {
            string defaultName = $"Layout {Layouts.Count + 1}";
            string? name = PromptName(GetString("Loc.PromptLayoutName"), defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            name = EnsureUniqueLayoutName(name);
            var layout = new LayoutDefinition
            {
                Name = name,
                ThemePresetName = GetSelectedPresetName(),
                DefaultPanelPresetName = GetSelectedLayoutDefaultPresetName(),
                PanelDefaultShowHidden = false,
                PanelDefaultShowFileExtensions = true,
                PanelDefaultExpandOnHover = true,
                PanelDefaultOpenFoldersExternally = false,
                PanelDefaultShowSettingsButton = true,
                PanelDefaultMovementMode = "titlebar",
                PanelDefaultSearchVisibilityMode = DesktopPanel.SearchVisibilityAlways,
                Appearance = CloneAppearance(Appearance),
                Panels = new List<WindowData>()
            };
            NormalizeLayoutPanelDefaults(layout);
            Layouts.Add(layout);
            SaveSettings();
            RefreshLayoutList();
        }

        private void OpenLayoutGlobalPanelSettings_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LayoutDefinition layout)
            {
                NormalizeLayoutPanelDefaults(layout);
                var settings = new PanelSettings(layout);
                settings.Owner = this;
                settings.Show();
            }
        }

        internal void ApplyLayoutGlobalPanelSettings(
            LayoutDefinition layout,
            bool showHidden,
            bool showFileExtensions,
            bool expandOnHover,
            bool openFoldersExternally,
            bool showSettingsButton,
            string movementMode,
            string searchVisibilityMode,
            string? defaultPresetName)
        {
            if (layout == null)
            {
                return;
            }

            string oldDefaultPresetName = ResolveLayoutDefaultPresetName(layout);
            string nextDefaultPresetName = !string.IsNullOrWhiteSpace(defaultPresetName)
                ? defaultPresetName
                : oldDefaultPresetName;
            if (string.IsNullOrWhiteSpace(nextDefaultPresetName) ||
                !Presets.Any(p => string.Equals(p.Name, nextDefaultPresetName, StringComparison.OrdinalIgnoreCase)))
            {
                nextDefaultPresetName = oldDefaultPresetName;
            }
            bool defaultPresetChanged = !string.Equals(oldDefaultPresetName, nextDefaultPresetName, StringComparison.OrdinalIgnoreCase);

            var oldDefaults = CaptureLayoutPanelDefaults(layout);
            var newDefaults = new LayoutPanelDefaultsSnapshot
            {
                ShowHidden = showHidden,
                ShowFileExtensions = showFileExtensions,
                ExpandOnHover = expandOnHover,
                OpenFoldersExternally = openFoldersExternally,
                ShowSettingsButton = showSettingsButton,
                MovementMode = NormalizePanelMovementMode(movementMode),
                SearchVisibilityMode = DesktopPanel.NormalizeSearchVisibilityMode(searchVisibilityMode)
            };

            var panelMap = CreateWindowDataMap(layout.Panels ?? new List<WindowData>(), rewriteDuplicates: true);
            foreach (var openPanel in Application.Current.Windows.OfType<DesktopPanel>().Where(IsUserPanel))
            {
                string key = GetPanelKey(openPanel);
                if (!panelMap.ContainsKey(key))
                {
                    continue;
                }

                var snapshot = BuildWindowDataFromPanel(openPanel);
                NormalizeWindowData(snapshot);
                panelMap[key] = snapshot;
            }

            foreach (var panel in panelMap.Values)
            {
                NormalizeWindowData(panel);
                string effectivePresetName = ResolveLayoutPanelPreset(panel.PresetName, oldDefaultPresetName);
                bool usesOldLayoutStandardPreset =
                    string.IsNullOrWhiteSpace(panel.PresetName) ||
                    string.Equals(effectivePresetName, oldDefaultPresetName, StringComparison.OrdinalIgnoreCase);

                if (defaultPresetChanged && usesOldLayoutStandardPreset)
                {
                    effectivePresetName = nextDefaultPresetName;
                }

                panel.PresetName = effectivePresetName;

                if (panel.ShowHidden == oldDefaults.ShowHidden)
                {
                    panel.ShowHidden = newDefaults.ShowHidden;
                }

                if (panel.ShowFileExtensions == oldDefaults.ShowFileExtensions)
                {
                    panel.ShowFileExtensions = newDefaults.ShowFileExtensions;
                }

                if (panel.ExpandOnHover == oldDefaults.ExpandOnHover)
                {
                    panel.ExpandOnHover = newDefaults.ExpandOnHover;
                }

                if (panel.OpenFoldersExternally == oldDefaults.OpenFoldersExternally)
                {
                    panel.OpenFoldersExternally = newDefaults.OpenFoldersExternally;
                }

                if (panel.ShowSettingsButton == oldDefaults.ShowSettingsButton)
                {
                    panel.ShowSettingsButton = newDefaults.ShowSettingsButton;
                }

                if (string.Equals(NormalizePanelMovementMode(panel.MovementMode), oldDefaults.MovementMode, StringComparison.OrdinalIgnoreCase))
                {
                    panel.MovementMode = newDefaults.MovementMode;
                }

                if (string.Equals(DesktopPanel.NormalizeSearchVisibilityMode(panel.SearchVisibilityMode), oldDefaults.SearchVisibilityMode, StringComparison.OrdinalIgnoreCase))
                {
                    panel.SearchVisibilityMode = newDefaults.SearchVisibilityMode;
                }
            }

            layout.Panels = panelMap.Values
                .Select(p =>
                {
                    var clone = CloneWindowData(p);
                    clone.PresetName = EncodeLayoutPanelPreset(clone.PresetName, nextDefaultPresetName);
                    return clone;
                })
                .ToList();
            ApplyLayoutPanelDefaults(layout, newDefaults);
            layout.DefaultPanelPresetName = nextDefaultPresetName;
            _layoutDefaultPresetName = nextDefaultPresetName;

            var updatedByKey = panelMap;
            foreach (var saved in savedWindows)
            {
                if (!updatedByKey.TryGetValue(GetPanelKey(saved), out var source))
                {
                    continue;
                }

                CopyPanelBehaviorSettings(source, saved);
            }

            foreach (var openPanel in Application.Current.Windows.OfType<DesktopPanel>().Where(IsUserPanel))
            {
                if (!updatedByKey.TryGetValue(GetPanelKey(openPanel), out var source))
                {
                    continue;
                }

                ApplyPanelBehaviorToOpenPanel(openPanel, source);
            }

            SaveSettings();
            RefreshLayoutList();
            NotifyPanelsChanged();
        }

        private void ApplyLayout_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LayoutDefinition layout)
            {
                ApplyLayout(layout);
            }
        }

        private void UpdateLayoutFromCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LayoutDefinition layout)
            {
                string layoutDefaultPresetName = ResolveLayoutDefaultPresetName(layout);
                layout.DefaultPanelPresetName = layoutDefaultPresetName;
                layout.ThemePresetName = GetSelectedPresetName();
                layout.Appearance = CloneAppearance(Appearance);
                layout.Panels = CaptureOpenPanelsForLayout(layoutDefaultPresetName);

                SaveSettings();
                RefreshLayoutList();
            }
        }

        private void DeleteLayout_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LayoutDefinition layout)
            {
                Layouts.Remove(layout);
                SaveSettings();
                RefreshLayoutList();
            }
        }

        private void ApplyLayout(LayoutDefinition layout)
        {
            if (layout == null) return;

            string layoutDefaultPresetName = ResolveLayoutDefaultPresetName(layout);
            layout.DefaultPanelPresetName = layoutDefaultPresetName;
            _layoutDefaultPresetName = layoutDefaultPresetName;

            var layoutPanels = (layout.Panels ?? new List<WindowData>())
                .Select(CloneWindowData)
                .Where(HasPersistableLayoutContent)
                .ToList();

            foreach (var panel in layoutPanels)
            {
                NormalizeWindowData(panel);
            }

            var layoutDict = CreateWindowDataMap(layoutPanels, rewriteDuplicates: true);

            var openPanels = Application.Current.Windows
                .OfType<DesktopPanel>()
                .Where(IsUserPanel)
                .ToList();
            foreach (var panel in openPanels)
            {
                panel.Close();
            }

            savedWindows = layoutDict.Values
                .Select(CloneWindowData)
                .ToList();

            foreach (var saved in savedWindows)
            {
                saved.PresetName = ResolveLayoutPanelPreset(saved.PresetName, layoutDefaultPresetName);
                var kind = ResolvePanelKind(saved);

                if (kind == PanelKind.None)
                {
                    saved.IsHidden = true;
                    continue;
                }

                if (saved.IsHidden) continue;

                if (kind == PanelKind.Folder && !Directory.Exists(saved.FolderPath))
                {
                    saved.IsHidden = true;
                    continue;
                }

                OpenPanelFromData(saved);
            }

            savedWindows = savedWindows
                .OrderBy(x => string.IsNullOrWhiteSpace(x.PanelTitle) ? x.FolderPath : x.PanelTitle)
                .ThenBy(x => x.FolderPath)
                .ToList();

            RefreshPresetSelectors();
            ApplyLayoutAppearance(layout);
            SaveSettings();
            NotifyPanelsChanged();
        }

        private void ApplyLayoutAppearance(LayoutDefinition layout)
        {
            if (!string.IsNullOrWhiteSpace(layout.ThemePresetName))
            {
                var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, layout.ThemePresetName, StringComparison.OrdinalIgnoreCase));
                if (preset != null)
                {
                    UpdateAppearance(CloneAppearance(preset.Settings));
                    if (PresetComboTop != null)
                    {
                        PresetComboTop.SelectedItem = preset;
                    }
                    if (_isUiReady)
                    {
                        PopulateAppearanceInputs(Appearance);
                        UpdatePreview(Appearance);
                    }
                    return;
                }
            }

            if (layout.Appearance != null)
            {
                UpdateAppearance(CloneAppearance(layout.Appearance));
                if (_isUiReady)
                {
                    PopulateAppearanceInputs(Appearance);
                    UpdatePreview(Appearance);
                }
            }
        }

        private string GetSelectedPresetName()
        {
            return (PresetComboTop?.SelectedItem as AppearancePreset)?.Name ?? "";
        }

        private string? PromptName(string message, string defaultName)
        {
            var input = new InputBox(message, defaultName) { Owner = this };
            if (input.ShowDialog() == true)
            {
                string name = (input.ResultText ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            return null;
        }

        private string EnsureUniqueLayoutName(string name)
        {
            if (!Layouts.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }

            int index = 2;
            string candidate = $"{name} {index}";
            while (Layouts.Any(l => string.Equals(l.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                index++;
                candidate = $"{name} {index}";
            }
            return candidate;
        }
    }
}
