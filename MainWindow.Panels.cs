using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private void RefreshPanelOverview()
        {
            if (PanelOverviewList == null || PanelOverviewCount == null) return;

            savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
            var openPanelList = System.Windows.Application.Current.Windows.OfType<DesktopPanel>().ToList();
            var openPanels = CreateOpenPanelMap(openPanelList);

            foreach (var panel in openPanelList)
            {
                var kind = ResolvePanelKind(panel);
                if (kind == PanelKind.None &&
                    string.IsNullOrWhiteSpace(panel.currentFolderPath) &&
                    panel.PinnedItems.Count == 0)
                {
                    continue;
                }

                string key = GetPanelKey(panel);
                if (!savedWindows.Any(w => string.Equals(GetPanelKey(w), key, StringComparison.OrdinalIgnoreCase)))
                {
                    savedWindows.Add(BuildWindowDataFromPanel(panel));
                }
            }

            var items = savedWindows.Select(s =>
            {
                string panelKey = GetPanelKey(s);
                openPanels.TryGetValue(panelKey, out var panel);
                var kind = ResolvePanelKind(s);
                string title = !string.IsNullOrWhiteSpace(panel?.Title) ? panel.Title :
                    !string.IsNullOrWhiteSpace(s.PanelTitle) ? s.PanelTitle :
                    (!string.IsNullOrWhiteSpace(s.FolderPath) ? System.IO.Path.GetFileName(s.FolderPath) : GetString("Loc.Untitled"));

                bool isOpen = panel != null;
                bool isHidden = !isOpen && s.IsHidden;
                if (isOpen && s.IsHidden)
                {
                    s.IsHidden = false;
                }
                bool toggleIsShow = !isOpen && isHidden;

                string state = isOpen
                    ? (panel!.isContentVisible ? GetString("Loc.PanelStateOpen") : GetString("Loc.PanelStateCollapsed"))
                    : (isHidden ? GetString("Loc.PanelStateHidden") : GetString("Loc.PanelStateClosed"));
                string actionLabel = GetString("Loc.PanelActionFocus");
                string toggleLabel = toggleIsShow ? GetString("Loc.PanelsShow") : GetString("Loc.PanelsHide");

                string folderLabel = kind == PanelKind.List
                    ? string.Format(GetString("Loc.PanelTypeList"), s.PinnedItems?.Count ?? 0)
                    : string.IsNullOrWhiteSpace(s.FolderPath) ? GetString("Loc.NoFolder") : s.FolderPath;

                return new PanelOverviewItem
                {
                    Title = string.IsNullOrWhiteSpace(title) ? GetString("Loc.Untitled") : title,
                    Folder = folderLabel,
                    FolderPath = s.FolderPath ?? "",
                    PanelKey = panelKey,
                    PanelType = kind,
                    State = state,
                    Size = panel != null ? $"{(int)panel.Width} x {(int)panel.Height}" : $"{(int)s.Width} x {(int)s.Height}",
                    Position = panel != null ? $"{(int)panel.Left}, {(int)panel.Top}" : $"{(int)s.Left}, {(int)s.Top}",
                    IsOpen = isOpen,
                    IsHidden = isHidden,
                    ToggleIsShow = toggleIsShow,
                    ActionLabel = actionLabel,
                    ToggleLabel = toggleLabel,
                    Panel = panel,
                    PresetName = string.IsNullOrWhiteSpace(s.PresetName) ? DefaultPresetName : s.PresetName
                };
            })
            .ToList();

            var openWithoutType = openPanelList
                .Where(p => ResolvePanelKind(p) == PanelKind.None &&
                            string.IsNullOrWhiteSpace(p.currentFolderPath) &&
                            p.PinnedItems.Count == 0)
                .Select(panel =>
                {
                    string title = !string.IsNullOrWhiteSpace(panel.Title) ? panel.Title : GetString("Loc.Untitled");
                    string preset = string.IsNullOrWhiteSpace(panel.assignedPresetName) ? DefaultPresetName : panel.assignedPresetName;
                    return new PanelOverviewItem
                    {
                        Title = title,
                        Folder = GetString("Loc.NoFolder"),
                        FolderPath = "",
                        PanelKey = GetPanelKey(panel),
                        PanelType = PanelKind.None,
                        State = panel.isContentVisible ? GetString("Loc.PanelStateOpen") : GetString("Loc.PanelStateCollapsed"),
                        Size = $"{(int)panel.Width} x {(int)panel.Height}",
                        Position = $"{(int)panel.Left}, {(int)panel.Top}",
                        IsOpen = true,
                        IsHidden = false,
                        ToggleIsShow = false,
                        ActionLabel = GetString("Loc.PanelActionFocus"),
                        ToggleLabel = GetString("Loc.PanelsHide"),
                        Panel = panel,
                        PresetName = preset
                    };
                })
                .ToList();

            items.AddRange(openWithoutType);
            items = items.OrderBy(p => p.Title).ToList();

            PanelOverviewList.ItemsSource = items;
            int hiddenCount = items.Count(p => p.IsHidden);
            PanelOverviewCount.Text = hiddenCount > 0
                ? string.Format(GetString("Loc.PanelCountHidden"), items.Count, hiddenCount)
                : string.Format(GetString("Loc.PanelCount"), items.Count);
            RefreshPresetSelectors();
        }

        private DesktopPanel OpenPanelFromData(WindowData data)
        {
            var panel = new DesktopPanel();
            ApplyWindowDataToPanel(panel, data);
            panel.Show();
            return panel;
        }

        private void ApplyWindowDataToPanel(DesktopPanel panel, WindowData data)
        {
            panel.showHiddenItems = data.ShowHidden;
            ApplyPanelContent(panel, data);
            if (!string.IsNullOrWhiteSpace(data.PanelTitle))
            {
                panel.Title = data.PanelTitle;
                panel.PanelTitle.Text = data.PanelTitle;
            }

            panel.assignedPresetName = string.IsNullOrWhiteSpace(data.PresetName) ? DefaultPresetName : data.PresetName;
            panel.SetExpandOnHover(data.ExpandOnHover);
            panel.openFoldersExternally = data.OpenFoldersExternally;
            panel.showSettingsButton = data.ShowSettingsButton;
            panel.ApplyMovementMode(string.IsNullOrWhiteSpace(data.MovementMode) ? "titlebar" : data.MovementMode);
            panel.ApplySettingsButtonVisibility();
            panel.defaultFolderPath = data.DefaultFolderPath;

            double storedTop = data.Top;
            double storedBaseTop = (Math.Abs(data.BaseTop) < 0.01 && Math.Abs(storedTop) > 0.01) ? storedTop : data.BaseTop;
            double storedCollapsedTop = (Math.Abs(data.CollapsedTop) < 0.01 && Math.Abs(storedBaseTop) > 0.01) ? storedBaseTop : data.CollapsedTop;

            panel.baseTopPosition = storedBaseTop;
            panel.collapsedTopPosition = storedCollapsedTop;
            panel.Left = data.Left;
            panel.Top = data.IsCollapsed ? storedCollapsedTop : storedTop;
            panel.Width = data.Width;
            double storedHeight = data.Height > 0 ? data.Height : panel.Height;
            panel.Height = storedHeight;
            double restoredExpandedHeight = data.ExpandedHeight;
            if (restoredExpandedHeight <= 0)
            {
                restoredExpandedHeight = data.IsCollapsed
                    ? Math.Max(panel.expandedHeight, storedHeight)
                    : storedHeight;
            }
            panel.expandedHeight = Math.Max(storedHeight, restoredExpandedHeight);
            panel.IsBottomAnchored = false;
            panel.SetZoom(data.Zoom);
            var assigned = GetPresetSettings(data.PresetName);
            panel.ApplyAppearance(assigned);
            panel.ForceCollapseState(data.IsCollapsed);
        }

        private void ApplyPanelContent(DesktopPanel panel, WindowData data)
        {
            NormalizeWindowData(data);
            panel.PanelId = data.PanelId;
            var kind = ResolvePanelKind(data);
            panel.PanelType = kind;

            if (kind == PanelKind.Folder)
            {
                if (!string.IsNullOrWhiteSpace(data.FolderPath))
                {
                    panel.LoadFolder(data.FolderPath, false);
                }
            }
            else if (kind == PanelKind.List)
            {
                panel.LoadList(data.PinnedItems, false);
            }
            else
            {
                panel.ClearPanelItems();
            }
        }

        public static void MarkPanelHidden(DesktopPanel panel)
        {
            if (panel == null) return;
            var snapshot = BuildWindowDataFromPanel(panel);
            snapshot.IsHidden = true;
            var panelKey = GetPanelKey(snapshot);

            var existing = FindSavedWindow(panelKey);
            if (existing != null)
            {
                CopyWindowData(snapshot, existing);
                existing.IsHidden = true;
                return;
            }

            savedWindows.Add(snapshot);
        }

        public static void MarkPanelHidden(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            var existing = savedWindows.FirstOrDefault(w =>
                ResolvePanelKind(w) == PanelKind.Folder &&
                string.Equals(w.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsHidden = true;
                return;
            }

            savedWindows.Add(new WindowData
            {
                PanelId = GeneratePanelId(),
                PanelType = PanelKind.Folder.ToString(),
                FolderPath = folderPath,
                IsHidden = true
            });
        }

        private void FocusPanel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PanelOverviewItem item)
            {
                string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                    ? item.PanelKey
                    : (item.Panel != null ? GetPanelKey(item.Panel) : "");
                var existing = FindSavedWindow(panelKey);
                if (item.Panel != null)
                {
                    item.Panel.Show();
                    item.Panel.WindowState = WindowState.Normal;
                    item.Panel.Activate();
                    if (existing != null && existing.IsHidden)
                    {
                        existing.IsHidden = false;
                    }
                    SaveSettings();
                    NotifyPanelsChanged();
                }
                else
                {
                    if (existing != null)
                    {
                        existing.IsHidden = false;
                        OpenPanelFromData(existing);
                        SaveSettings();
                        NotifyPanelsChanged();
                    }
                }
            }
        }

        private void PanelTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;

            if (sender is FrameworkElement fe && fe.Tag is PanelOverviewItem item)
            {
                string defaultName = string.IsNullOrWhiteSpace(item.Title) ? "Unbenannt" : item.Title;
                string? name = PromptName(GetString("Loc.PromptPanelName"), defaultName);
                if (string.IsNullOrWhiteSpace(name)) return;

                string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                    ? item.PanelKey
                    : (item.Panel != null ? GetPanelKey(item.Panel) : "");
                var existing = FindSavedWindow(panelKey);
                if (existing != null)
                {
                    existing.PanelTitle = name;
                }
                else if (item.Panel != null)
                {
                    var snapshot = BuildWindowDataFromPanel(item.Panel);
                    snapshot.PanelTitle = name;
                    snapshot.IsHidden = item.IsHidden;
                    savedWindows.Add(snapshot);
                }

                if (item.Panel != null)
                {
                    item.Panel.Title = name;
                    item.Panel.PanelTitle.Text = name;
                }

                SaveSettings();
                NotifyPanelsChanged();
                e.Handled = true;
            }
        }

        private void DeletePanelFromList_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PanelOverviewItem item)
            {
                if (item.Panel != null)
                {
                    item.Panel.Close();
                }

                string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                    ? item.PanelKey
                    : (item.Panel != null ? GetPanelKey(item.Panel) : "");
                var existing = FindSavedWindow(panelKey);
                if (existing != null)
                {
                    savedWindows.Remove(existing);
                }

                SaveSettings();
                NotifyPanelsChanged();
            }
        }

        private void HidePanelFromList_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not PanelOverviewItem item) return;

            string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                ? item.PanelKey
                : (item.Panel != null ? GetPanelKey(item.Panel) : "");
            var existing = FindSavedWindow(panelKey);

            if (item.Panel != null)
            {
                var snapshot = BuildWindowDataFromPanel(item.Panel);
                snapshot.IsHidden = true;

                if (existing != null)
                {
                    CopyWindowData(snapshot, existing);
                    existing.IsHidden = true;
                }
                else
                {
                    savedWindows.Add(snapshot);
                }

                item.Panel.Close();
            }
            else if (existing != null)
            {
                existing.IsHidden = true;
            }

            SaveSettings();
            NotifyPanelsChanged();
        }

        private void TogglePanelVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PanelOverviewItem item)
            {
                string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                    ? item.PanelKey
                    : (item.Panel != null ? GetPanelKey(item.Panel) : "");
                var existing = FindSavedWindow(panelKey);

                if (item.IsOpen || !item.IsHidden)
                {
                    // Currently visible → hide
                    if (item.Panel != null)
                    {
                        item.Panel.Close();
                    }
                    if (existing != null)
                    {
                        existing.IsHidden = true;
                    }
                    else if (item.Panel != null)
                    {
                        var snapshot = BuildWindowDataFromPanel(item.Panel);
                        snapshot.IsHidden = true;
                        savedWindows.Add(snapshot);
                    }
                }
                else
                {
                    // Currently hidden → show
                    if (existing != null)
                    {
                        existing.IsHidden = false;
                        OpenPanelFromData(existing);
                    }
                }

                SaveSettings();
                NotifyPanelsChanged();
            }
        }

        private void OpenPanelSettings_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PanelOverviewItem item)
            {
                DesktopPanel? panel = item.Panel;

                if (panel == null)
                {
                    string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                        ? item.PanelKey
                        : (item.Panel != null ? GetPanelKey(item.Panel) : "");
                    var existing = FindSavedWindow(panelKey);
                    if (existing != null)
                    {
                        existing.IsHidden = false;
                        panel = OpenPanelFromData(existing);
                    }
                }

                if (panel != null)
                {
                    var settings = new PanelSettings(panel);
                    settings.Owner = this;
                    settings.Show();
                }

                SaveSettings();
                NotifyPanelsChanged();
            }
        }

        private void PanelPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox cb && cb.Tag is PanelOverviewItem item && cb.SelectedItem is AppearancePreset preset)
            {
                item.PresetName = preset.Name;

                string panelKey = !string.IsNullOrWhiteSpace(item.PanelKey)
                    ? item.PanelKey
                    : (item.Panel != null ? GetPanelKey(item.Panel) : "");
                var existing = FindSavedWindow(panelKey);
                if (existing != null)
                {
                    existing.PresetName = preset.Name;
                }
                else if (item.Panel != null)
                {
                    var snapshot = BuildWindowDataFromPanel(item.Panel);
                    snapshot.PresetName = preset.Name;
                    snapshot.IsHidden = item.IsHidden;
                    savedWindows.Add(snapshot);
                }

                if (item.Panel != null)
                {
                    item.Panel.assignedPresetName = preset.Name;
                    item.Panel.ApplyAppearance(preset.Settings);
                }

                SaveSettings();
                NotifyPanelsChanged();
            }
        }

        private DesktopPanel CreatePanelWithPreset(string presetName)
        {
            var panel = new DesktopPanel();
            var appearance = GetPresetSettings(presetName);
            panel.ApplyAppearance(appearance);
            panel.assignedPresetName = string.IsNullOrWhiteSpace(presetName) ? DefaultPresetName : presetName;
            return panel;
        }

        private void ShowAllPanels_Click(object sender, RoutedEventArgs e)
        {
            savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
            var openPanels = CreateOpenPanelMap(Application.Current.Windows.OfType<DesktopPanel>());

            foreach (var saved in savedWindows)
            {
                NormalizeWindowData(saved);
                var kind = ResolvePanelKind(saved);
                if (kind == PanelKind.None) continue;

                saved.IsHidden = false;
                var key = GetPanelKey(saved);

                if (openPanels.TryGetValue(key, out var existingPanel))
                {
                    existingPanel.Show();
                    existingPanel.WindowState = WindowState.Normal;
                    continue;
                }

                if (kind == PanelKind.Folder &&
                    string.IsNullOrWhiteSpace(saved.FolderPath))
                {
                    continue;
                }

                OpenPanelFromData(saved);
            }

            SaveSettings();
            NotifyPanelsChanged();
        }

        private void HideAllPanels_Click(object sender, RoutedEventArgs e)
        {
            var openPanels = Application.Current.Windows.OfType<DesktopPanel>().ToList();
            foreach (var panel in openPanels)
            {
                panel.Close();
            }

            foreach (var saved in savedWindows)
            {
                saved.IsHidden = true;
            }

            SaveSettings();
            NotifyPanelsChanged();
        }
    }
}
