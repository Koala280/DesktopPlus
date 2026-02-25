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
        private void RefreshLayoutList()
        {
            if (LayoutOverviewList == null || LayoutOverviewCount == null) return;

            var ordered = Layouts.OrderBy(l => l.Name).ToList();
            var presets = Presets
                .OrderBy(p => p.IsBuiltIn ? 0 : 1)
                .ThenBy(p => p.Name)
                .ToList();

            _suspendLayoutPresetSelection = true;
            var items = ordered.Select(layout => BuildLayoutOverviewItem(layout, presets)).ToList();
            LayoutOverviewList.ItemsSource = items;
            _suspendLayoutPresetSelection = false;
            LayoutOverviewCount.Text = string.Format(GetString("Loc.LayoutCount"), items.Count);
        }

        private LayoutOverviewItem BuildLayoutOverviewItem(LayoutDefinition layout, List<AppearancePreset> presets)
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
                Presets = presets,
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

        private List<WindowData> CaptureOpenPanelsForLayout(string layoutDefaultPresetName)
        {
            var panels = Application.Current.Windows.OfType<DesktopPanel>()
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

        private void LayoutItemPresetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suspendLayoutPresetSelection) return;
            if (sender is not System.Windows.Controls.ComboBox combo) return;
            if (combo.Tag is not LayoutOverviewItem item) return;
            if (!combo.IsKeyboardFocusWithin && !combo.IsDropDownOpen) return;
            if (combo.SelectedItem is not AppearancePreset preset) return;

            item.DefaultPresetName = preset.Name;
            if (item.Layout != null)
            {
                item.Layout.DefaultPanelPresetName = preset.Name;
            }
            _layoutDefaultPresetName = preset.Name;
            SaveSettings();
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
                Appearance = CloneAppearance(Appearance),
                Panels = CaptureOpenPanelsForLayout(layoutDefaultPresetName)
            };
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
                Appearance = CloneAppearance(Appearance),
                Panels = new List<WindowData>()
            };
            Layouts.Add(layout);
            SaveSettings();
            RefreshLayoutList();
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
                SaveSettings();

                string layoutDefaultPresetName = ResolveLayoutDefaultPresetName(layout);
                layout.DefaultPanelPresetName = layoutDefaultPresetName;
                layout.ThemePresetName = GetSelectedPresetName();
                layout.Appearance = CloneAppearance(Appearance);

                var openPanelMap = CreateOpenPanelMap(Application.Current.Windows.OfType<DesktopPanel>());
                var layoutPanelMap = CreateWindowDataMap(layout.Panels ?? new List<WindowData>(), rewriteDuplicates: true);
                var updatedPanels = new List<WindowData>();

                foreach (var existingLayoutPanel in layoutPanelMap.Values)
                {
                    var key = GetPanelKey(existingLayoutPanel);
                    WindowData snapshot;

                    if (openPanelMap.TryGetValue(key, out var openPanel))
                    {
                        snapshot = BuildWindowDataFromPanel(openPanel);
                        snapshot.IsHidden = false;
                    }
                    else
                    {
                        snapshot = CloneWindowData(existingLayoutPanel);
                    }

                    NormalizeWindowData(snapshot);
                    if (!HasPersistableLayoutContent(snapshot)) continue;

                    snapshot.PresetName = EncodeLayoutPanelPreset(snapshot.PresetName, layoutDefaultPresetName);
                    updatedPanels.Add(snapshot);
                }

                layout.Panels = CreateWindowDataMap(updatedPanels, rewriteDuplicates: true)
                    .Values
                    .Select(CloneWindowData)
                    .ToList();

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

            var openPanels = Application.Current.Windows.OfType<DesktopPanel>().ToList();
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
