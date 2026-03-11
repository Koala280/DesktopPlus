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
            public bool ShowParentNavigationItem { get; set; } = true;
            public string IconViewParentNavigationMode { get; set; } = DesktopPanel.IconParentNavigationModeItem;
            public bool ShowFileExtensions { get; set; } = true;
            public bool ExpandOnHover { get; set; } = false;
            public bool OpenFoldersExternally { get; set; }
            public bool OpenItemsOnSingleClick { get; set; }
            public bool ShowSettingsButton { get; set; } = true;
            public bool ShowEmptyRecycleBinButton { get; set; } = false;
            public string MovementMode { get; set; } = "titlebar";
            public string SearchVisibilityMode { get; set; } = DesktopPanel.SearchVisibilityAlways;
            public string ViewMode { get; set; } = DesktopPanel.ViewModeIcons;
            public bool ShowMetadataType { get; set; } = true;
            public bool ShowMetadataSize { get; set; } = true;
            public bool ShowMetadataCreated { get; set; }
            public bool ShowMetadataModified { get; set; } = true;
            public bool ShowMetadataDimensions { get; set; }
            public bool ShowMetadataAuthors { get; set; }
            public bool ShowMetadataCategories { get; set; }
            public bool ShowMetadataTags { get; set; }
            public bool ShowMetadataTitle { get; set; }
            public List<string> MetadataOrder { get; set; } = DesktopPanel.NormalizeMetadataOrder(null);
            public Dictionary<string, double> MetadataWidths { get; set; } = DesktopPanel.NormalizeMetadataWidths(null);
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
            layout.PanelDefaultViewMode = DesktopPanel.NormalizeViewMode(layout.PanelDefaultViewMode);
            layout.PanelDefaultIconViewParentNavigationMode = DesktopPanel.NormalizeIconViewParentNavigationMode(
                layout.PanelDefaultIconViewParentNavigationMode,
                layout.PanelDefaultShowParentNavigationItem);
            layout.PanelDefaultMetadataOrder = DesktopPanel.NormalizeMetadataOrder(layout.PanelDefaultMetadataOrder);
            layout.PanelDefaultMetadataWidths = DesktopPanel.NormalizeMetadataWidths(layout.PanelDefaultMetadataWidths);
        }

        private static LayoutPanelDefaultsSnapshot CaptureLayoutPanelDefaults(LayoutDefinition layout)
        {
            NormalizeLayoutPanelDefaults(layout);
            return new LayoutPanelDefaultsSnapshot
            {
                ShowHidden = layout.PanelDefaultShowHidden,
                ShowParentNavigationItem = layout.PanelDefaultShowParentNavigationItem,
                IconViewParentNavigationMode = DesktopPanel.NormalizeIconViewParentNavigationMode(
                    layout.PanelDefaultIconViewParentNavigationMode,
                    layout.PanelDefaultShowParentNavigationItem),
                ShowFileExtensions = layout.PanelDefaultShowFileExtensions,
                ExpandOnHover = layout.PanelDefaultExpandOnHover,
                OpenFoldersExternally = layout.PanelDefaultOpenFoldersExternally,
                OpenItemsOnSingleClick = layout.PanelDefaultOpenItemsOnSingleClick,
                ShowSettingsButton = layout.PanelDefaultShowSettingsButton,
                ShowEmptyRecycleBinButton = layout.PanelDefaultShowEmptyRecycleBinButton,
                MovementMode = layout.PanelDefaultMovementMode,
                SearchVisibilityMode = layout.PanelDefaultSearchVisibilityMode,
                ViewMode = layout.PanelDefaultViewMode,
                ShowMetadataType = layout.PanelDefaultShowMetadataType,
                ShowMetadataSize = layout.PanelDefaultShowMetadataSize,
                ShowMetadataCreated = layout.PanelDefaultShowMetadataCreated,
                ShowMetadataModified = layout.PanelDefaultShowMetadataModified,
                ShowMetadataDimensions = layout.PanelDefaultShowMetadataDimensions,
                ShowMetadataAuthors = layout.PanelDefaultShowMetadataAuthors,
                ShowMetadataCategories = layout.PanelDefaultShowMetadataCategories,
                ShowMetadataTags = layout.PanelDefaultShowMetadataTags,
                ShowMetadataTitle = layout.PanelDefaultShowMetadataTitle,
                MetadataOrder = DesktopPanel.NormalizeMetadataOrder(layout.PanelDefaultMetadataOrder),
                MetadataWidths = DesktopPanel.NormalizeMetadataWidths(layout.PanelDefaultMetadataWidths)
            };
        }

        private static void ApplyLayoutPanelDefaults(LayoutDefinition layout, LayoutPanelDefaultsSnapshot defaults)
        {
            layout.PanelDefaultShowHidden = defaults.ShowHidden;
            layout.PanelDefaultShowParentNavigationItem = defaults.ShowParentNavigationItem;
            layout.PanelDefaultIconViewParentNavigationMode = DesktopPanel.NormalizeIconViewParentNavigationMode(
                defaults.IconViewParentNavigationMode,
                defaults.ShowParentNavigationItem);
            layout.PanelDefaultShowFileExtensions = defaults.ShowFileExtensions;
            layout.PanelDefaultExpandOnHover = defaults.ExpandOnHover;
            layout.PanelDefaultOpenFoldersExternally = defaults.OpenFoldersExternally;
            layout.PanelDefaultOpenItemsOnSingleClick = defaults.OpenItemsOnSingleClick;
            layout.PanelDefaultShowSettingsButton = defaults.ShowSettingsButton;
            layout.PanelDefaultShowEmptyRecycleBinButton = defaults.ShowEmptyRecycleBinButton;
            layout.PanelDefaultMovementMode = NormalizePanelMovementMode(defaults.MovementMode);
            layout.PanelDefaultSearchVisibilityMode = DesktopPanel.NormalizeSearchVisibilityMode(defaults.SearchVisibilityMode);
            layout.PanelDefaultViewMode = DesktopPanel.NormalizeViewMode(defaults.ViewMode);
            layout.PanelDefaultShowMetadataType = defaults.ShowMetadataType;
            layout.PanelDefaultShowMetadataSize = defaults.ShowMetadataSize;
            layout.PanelDefaultShowMetadataCreated = defaults.ShowMetadataCreated;
            layout.PanelDefaultShowMetadataModified = defaults.ShowMetadataModified;
            layout.PanelDefaultShowMetadataDimensions = defaults.ShowMetadataDimensions;
            layout.PanelDefaultShowMetadataAuthors = defaults.ShowMetadataAuthors;
            layout.PanelDefaultShowMetadataCategories = defaults.ShowMetadataCategories;
            layout.PanelDefaultShowMetadataTags = defaults.ShowMetadataTags;
            layout.PanelDefaultShowMetadataTitle = defaults.ShowMetadataTitle;
            layout.PanelDefaultMetadataOrder = DesktopPanel.NormalizeMetadataOrder(defaults.MetadataOrder);
            layout.PanelDefaultMetadataWidths = DesktopPanel.NormalizeMetadataWidths(defaults.MetadataWidths);
        }

        private static void ApplyLayoutDefaultsToPanelWhenMatching(
            WindowData panel,
            LayoutPanelDefaultsSnapshot oldDefaults,
            LayoutPanelDefaultsSnapshot newDefaults)
        {
            if (panel.ShowHidden == oldDefaults.ShowHidden)
            {
                panel.ShowHidden = newDefaults.ShowHidden;
            }

            if (panel.ShowParentNavigationItem == oldDefaults.ShowParentNavigationItem)
            {
                panel.ShowParentNavigationItem = newDefaults.ShowParentNavigationItem;
            }

            if (string.Equals(
                DesktopPanel.NormalizeIconViewParentNavigationMode(panel.IconViewParentNavigationMode, panel.ShowParentNavigationItem),
                oldDefaults.IconViewParentNavigationMode,
                StringComparison.OrdinalIgnoreCase))
            {
                panel.IconViewParentNavigationMode = newDefaults.IconViewParentNavigationMode;
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

            if (panel.OpenItemsOnSingleClick == oldDefaults.OpenItemsOnSingleClick)
            {
                panel.OpenItemsOnSingleClick = newDefaults.OpenItemsOnSingleClick;
            }

            if (panel.ShowSettingsButton == oldDefaults.ShowSettingsButton)
            {
                panel.ShowSettingsButton = newDefaults.ShowSettingsButton;
            }

            if (panel.ShowEmptyRecycleBinButton == oldDefaults.ShowEmptyRecycleBinButton)
            {
                panel.ShowEmptyRecycleBinButton = newDefaults.ShowEmptyRecycleBinButton;
            }

            if (string.Equals(NormalizePanelMovementMode(panel.MovementMode), oldDefaults.MovementMode, StringComparison.OrdinalIgnoreCase))
            {
                panel.MovementMode = newDefaults.MovementMode;
            }

            if (string.Equals(DesktopPanel.NormalizeSearchVisibilityMode(panel.SearchVisibilityMode), oldDefaults.SearchVisibilityMode, StringComparison.OrdinalIgnoreCase))
            {
                panel.SearchVisibilityMode = newDefaults.SearchVisibilityMode;
            }

            if (string.Equals(DesktopPanel.NormalizeViewMode(panel.ViewMode), oldDefaults.ViewMode, StringComparison.OrdinalIgnoreCase))
            {
                panel.ViewMode = newDefaults.ViewMode;
            }

            if (panel.ShowMetadataType == oldDefaults.ShowMetadataType)
            {
                panel.ShowMetadataType = newDefaults.ShowMetadataType;
            }

            if (panel.ShowMetadataSize == oldDefaults.ShowMetadataSize)
            {
                panel.ShowMetadataSize = newDefaults.ShowMetadataSize;
            }

            if (panel.ShowMetadataCreated == oldDefaults.ShowMetadataCreated)
            {
                panel.ShowMetadataCreated = newDefaults.ShowMetadataCreated;
            }

            if (panel.ShowMetadataModified == oldDefaults.ShowMetadataModified)
            {
                panel.ShowMetadataModified = newDefaults.ShowMetadataModified;
            }

            if (panel.ShowMetadataDimensions == oldDefaults.ShowMetadataDimensions)
            {
                panel.ShowMetadataDimensions = newDefaults.ShowMetadataDimensions;
            }

            if (panel.ShowMetadataAuthors == oldDefaults.ShowMetadataAuthors)
            {
                panel.ShowMetadataAuthors = newDefaults.ShowMetadataAuthors;
            }

            if (panel.ShowMetadataCategories == oldDefaults.ShowMetadataCategories)
            {
                panel.ShowMetadataCategories = newDefaults.ShowMetadataCategories;
            }

            if (panel.ShowMetadataTags == oldDefaults.ShowMetadataTags)
            {
                panel.ShowMetadataTags = newDefaults.ShowMetadataTags;
            }

            if (panel.ShowMetadataTitle == oldDefaults.ShowMetadataTitle)
            {
                panel.ShowMetadataTitle = newDefaults.ShowMetadataTitle;
            }

            var normalizedPanelMetadataOrder = DesktopPanel.NormalizeMetadataOrder(panel.MetadataOrder);
            if (normalizedPanelMetadataOrder.SequenceEqual(oldDefaults.MetadataOrder, StringComparer.OrdinalIgnoreCase))
            {
                panel.MetadataOrder = DesktopPanel.NormalizeMetadataOrder(newDefaults.MetadataOrder);
            }

            var normalizedPanelMetadataWidths = DesktopPanel.NormalizeMetadataWidths(panel.MetadataWidths);
            if (normalizedPanelMetadataWidths.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(oldDefaults.MetadataWidths.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)))
            {
                panel.MetadataWidths = DesktopPanel.NormalizeMetadataWidths(newDefaults.MetadataWidths);
            }
        }

        private static void ApplyLayoutDefaultsToTabWhenMatching(
            PanelTabData tab,
            LayoutPanelDefaultsSnapshot oldDefaults,
            LayoutPanelDefaultsSnapshot newDefaults)
        {
            if (tab.ShowHidden == oldDefaults.ShowHidden)
            {
                tab.ShowHidden = newDefaults.ShowHidden;
            }

            if (tab.ShowParentNavigationItem == oldDefaults.ShowParentNavigationItem)
            {
                tab.ShowParentNavigationItem = newDefaults.ShowParentNavigationItem;
            }

            if (string.Equals(
                DesktopPanel.NormalizeIconViewParentNavigationMode(tab.IconViewParentNavigationMode, tab.ShowParentNavigationItem),
                oldDefaults.IconViewParentNavigationMode,
                StringComparison.OrdinalIgnoreCase))
            {
                tab.IconViewParentNavigationMode = newDefaults.IconViewParentNavigationMode;
            }

            if (tab.ShowFileExtensions == oldDefaults.ShowFileExtensions)
            {
                tab.ShowFileExtensions = newDefaults.ShowFileExtensions;
            }

            if (tab.OpenFoldersExternally == oldDefaults.OpenFoldersExternally)
            {
                tab.OpenFoldersExternally = newDefaults.OpenFoldersExternally;
            }

            if (tab.OpenItemsOnSingleClick == oldDefaults.OpenItemsOnSingleClick)
            {
                tab.OpenItemsOnSingleClick = newDefaults.OpenItemsOnSingleClick;
            }

            if (string.Equals(DesktopPanel.NormalizeViewMode(tab.ViewMode), oldDefaults.ViewMode, StringComparison.OrdinalIgnoreCase))
            {
                tab.ViewMode = newDefaults.ViewMode;
            }

            if (tab.ShowMetadataType == oldDefaults.ShowMetadataType)
            {
                tab.ShowMetadataType = newDefaults.ShowMetadataType;
            }

            if (tab.ShowMetadataSize == oldDefaults.ShowMetadataSize)
            {
                tab.ShowMetadataSize = newDefaults.ShowMetadataSize;
            }

            if (tab.ShowMetadataCreated == oldDefaults.ShowMetadataCreated)
            {
                tab.ShowMetadataCreated = newDefaults.ShowMetadataCreated;
            }

            if (tab.ShowMetadataModified == oldDefaults.ShowMetadataModified)
            {
                tab.ShowMetadataModified = newDefaults.ShowMetadataModified;
            }

            if (tab.ShowMetadataDimensions == oldDefaults.ShowMetadataDimensions)
            {
                tab.ShowMetadataDimensions = newDefaults.ShowMetadataDimensions;
            }

            if (tab.ShowMetadataAuthors == oldDefaults.ShowMetadataAuthors)
            {
                tab.ShowMetadataAuthors = newDefaults.ShowMetadataAuthors;
            }

            if (tab.ShowMetadataCategories == oldDefaults.ShowMetadataCategories)
            {
                tab.ShowMetadataCategories = newDefaults.ShowMetadataCategories;
            }

            if (tab.ShowMetadataTags == oldDefaults.ShowMetadataTags)
            {
                tab.ShowMetadataTags = newDefaults.ShowMetadataTags;
            }

            if (tab.ShowMetadataTitle == oldDefaults.ShowMetadataTitle)
            {
                tab.ShowMetadataTitle = newDefaults.ShowMetadataTitle;
            }

            var normalizedTabMetadataOrder = DesktopPanel.NormalizeMetadataOrder(tab.MetadataOrder);
            if (normalizedTabMetadataOrder.SequenceEqual(oldDefaults.MetadataOrder, StringComparer.OrdinalIgnoreCase))
            {
                tab.MetadataOrder = DesktopPanel.NormalizeMetadataOrder(newDefaults.MetadataOrder);
            }

            var normalizedTabMetadataWidths = DesktopPanel.NormalizeMetadataWidths(tab.MetadataWidths);
            if (normalizedTabMetadataWidths.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(oldDefaults.MetadataWidths.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)))
            {
                tab.MetadataWidths = DesktopPanel.NormalizeMetadataWidths(newDefaults.MetadataWidths);
            }
        }

        private static void ApplyLayoutDefaultsToPanelTabsWhenMatching(
            WindowData panel,
            LayoutPanelDefaultsSnapshot oldDefaults,
            LayoutPanelDefaultsSnapshot newDefaults)
        {
            if (panel.Tabs == null || panel.Tabs.Count == 0)
            {
                return;
            }

            foreach (var tab in panel.Tabs)
            {
                if (tab == null)
                {
                    continue;
                }

                tab.ViewMode = DesktopPanel.NormalizeViewMode(tab.ViewMode);
                tab.MetadataOrder = DesktopPanel.NormalizeMetadataOrder(tab.MetadataOrder);
                tab.MetadataWidths = DesktopPanel.NormalizeMetadataWidths(tab.MetadataWidths);
                ApplyLayoutDefaultsToTabWhenMatching(tab, oldDefaults, newDefaults);
            }
        }

        private static void CopyTabBehaviorSettings(PanelTabData source, PanelTabData target)
        {
            target.ShowHidden = source.ShowHidden;
            target.ShowParentNavigationItem = source.ShowParentNavigationItem;
            target.IconViewParentNavigationMode = DesktopPanel.NormalizeIconViewParentNavigationMode(
                source.IconViewParentNavigationMode,
                source.ShowParentNavigationItem);
            target.ShowFileExtensions = source.ShowFileExtensions;
            target.OpenFoldersExternally = source.OpenFoldersExternally;
            target.OpenItemsOnSingleClick = source.OpenItemsOnSingleClick;
            target.ViewMode = DesktopPanel.NormalizeViewMode(source.ViewMode);
            target.ShowMetadataType = source.ShowMetadataType;
            target.ShowMetadataSize = source.ShowMetadataSize;
            target.ShowMetadataCreated = source.ShowMetadataCreated;
            target.ShowMetadataModified = source.ShowMetadataModified;
            target.ShowMetadataDimensions = source.ShowMetadataDimensions;
            target.ShowMetadataAuthors = source.ShowMetadataAuthors;
            target.ShowMetadataCategories = source.ShowMetadataCategories;
            target.ShowMetadataTags = source.ShowMetadataTags;
            target.ShowMetadataTitle = source.ShowMetadataTitle;
            target.MetadataOrder = DesktopPanel.NormalizeMetadataOrder(source.MetadataOrder);
            target.MetadataWidths = DesktopPanel.NormalizeMetadataWidths(source.MetadataWidths);
        }

        private static PanelTabData? FindMatchingTab(IReadOnlyList<PanelTabData> sourceTabs, PanelTabData targetTab, int targetIndex)
        {
            if (!string.IsNullOrWhiteSpace(targetTab.TabId))
            {
                var byId = sourceTabs.FirstOrDefault(tab =>
                    !string.IsNullOrWhiteSpace(tab.TabId) &&
                    string.Equals(tab.TabId, targetTab.TabId, StringComparison.OrdinalIgnoreCase));
                if (byId != null)
                {
                    return byId;
                }
            }

            return targetIndex >= 0 && targetIndex < sourceTabs.Count ? sourceTabs[targetIndex] : null;
        }

        private static void CopyPanelTabBehaviorSettings(WindowData source, WindowData target)
        {
            if (source.Tabs == null || target.Tabs == null || source.Tabs.Count == 0 || target.Tabs.Count == 0)
            {
                return;
            }

            for (int i = 0; i < target.Tabs.Count; i++)
            {
                var targetTab = target.Tabs[i];
                if (targetTab == null)
                {
                    continue;
                }

                var sourceTab = FindMatchingTab(source.Tabs, targetTab, i);
                if (sourceTab == null)
                {
                    continue;
                }

                CopyTabBehaviorSettings(sourceTab, targetTab);
            }
        }

        private static void ApplyPanelTabBehaviorToOpenPanel(DesktopPanel panel, WindowData source)
        {
            if (source.Tabs == null || source.Tabs.Count == 0 || panel.Tabs.Count == 0)
            {
                return;
            }

            panel.SaveActiveTabState();

            for (int i = 0; i < panel.Tabs.Count; i++)
            {
                var targetTab = panel.Tabs[i];
                if (targetTab == null)
                {
                    continue;
                }

                var sourceTab = FindMatchingTab(source.Tabs, targetTab, i);
                if (sourceTab == null)
                {
                    continue;
                }

                CopyTabBehaviorSettings(sourceTab, targetTab);
            }

            panel.SaveActiveTabState();
        }

        private static void CopyPanelBehaviorSettings(WindowData source, WindowData target)
        {
            target.PresetName = source.PresetName;
            target.ShowHidden = source.ShowHidden;
            target.ShowParentNavigationItem = source.ShowParentNavigationItem;
            target.IconViewParentNavigationMode = DesktopPanel.NormalizeIconViewParentNavigationMode(
                source.IconViewParentNavigationMode,
                source.ShowParentNavigationItem);
            target.ShowFileExtensions = source.ShowFileExtensions;
            target.ExpandOnHover = source.ExpandOnHover;
            target.OpenFoldersExternally = source.OpenFoldersExternally;
            target.OpenItemsOnSingleClick = source.OpenItemsOnSingleClick;
            target.ShowSettingsButton = source.ShowSettingsButton;
            target.ShowEmptyRecycleBinButton = source.ShowEmptyRecycleBinButton;
            target.ViewMode = DesktopPanel.NormalizeViewMode(source.ViewMode);
            target.ShowMetadataType = source.ShowMetadataType;
            target.ShowMetadataSize = source.ShowMetadataSize;
            target.ShowMetadataCreated = source.ShowMetadataCreated;
            target.ShowMetadataModified = source.ShowMetadataModified;
            target.ShowMetadataDimensions = source.ShowMetadataDimensions;
            target.ShowMetadataAuthors = source.ShowMetadataAuthors;
            target.ShowMetadataCategories = source.ShowMetadataCategories;
            target.ShowMetadataTags = source.ShowMetadataTags;
            target.ShowMetadataTitle = source.ShowMetadataTitle;
            target.MetadataOrder = DesktopPanel.NormalizeMetadataOrder(source.MetadataOrder);
            target.MetadataWidths = DesktopPanel.NormalizeMetadataWidths(source.MetadataWidths);
            target.MovementMode = NormalizePanelMovementMode(source.MovementMode);
            target.SearchVisibilityMode = DesktopPanel.NormalizeSearchVisibilityMode(source.SearchVisibilityMode);
            CopyPanelTabBehaviorSettings(source, target);
        }

        private static void ApplyPanelBehaviorToOpenPanel(DesktopPanel panel, WindowData source)
        {
            bool hiddenChanged = panel.showHiddenItems != source.ShowHidden;
            bool parentNavigationChanged = panel.showParentNavigationItem != source.ShowParentNavigationItem;
            bool iconParentNavigationModeChanged = !string.Equals(
                DesktopPanel.NormalizeIconViewParentNavigationMode(panel.iconViewParentNavigationMode, panel.showParentNavigationItem),
                DesktopPanel.NormalizeIconViewParentNavigationMode(source.IconViewParentNavigationMode, source.ShowParentNavigationItem),
                StringComparison.OrdinalIgnoreCase);
            bool fileExtensionsChanged = panel.showFileExtensions != source.ShowFileExtensions;
            string sourcePresetName = string.IsNullOrWhiteSpace(source.PresetName) ? DefaultPresetName : source.PresetName;

            if (!string.Equals(panel.assignedPresetName, sourcePresetName, StringComparison.OrdinalIgnoreCase))
            {
                panel.assignedPresetName = sourcePresetName;
                panel.ApplyAppearance(GetPresetSettings(sourcePresetName));
            }

            panel.showHiddenItems = source.ShowHidden;
            panel.showParentNavigationItem = source.ShowParentNavigationItem;
            panel.iconViewParentNavigationMode = DesktopPanel.NormalizeIconViewParentNavigationMode(
                source.IconViewParentNavigationMode,
                source.ShowParentNavigationItem);
            panel.showFileExtensions = source.ShowFileExtensions;
            panel.SetExpandOnHover(source.ExpandOnHover);
            panel.openFoldersExternally = source.OpenFoldersExternally;
            panel.openItemsOnSingleClick = source.OpenItemsOnSingleClick;
            panel.showSettingsButton = source.ShowSettingsButton;
            panel.showEmptyRecycleBinButton = source.ShowEmptyRecycleBinButton;
            panel.ApplySettingsButtonVisibility();
            panel.UpdateEmptyRecycleBinButtonVisibility();
            panel.ApplyMovementMode(NormalizePanelMovementMode(source.MovementMode));
            panel.SetSearchVisibilityMode(source.SearchVisibilityMode);
            panel.ApplyViewSettings(
                source.ViewMode,
                source.ShowMetadataType,
                source.ShowMetadataSize,
                source.ShowMetadataCreated,
                source.ShowMetadataModified,
                source.ShowMetadataDimensions,
                source.ShowMetadataAuthors,
                source.ShowMetadataCategories,
                source.ShowMetadataTags,
                source.ShowMetadataTitle,
                metadataOrderOverride: source.MetadataOrder,
                metadataWidthsOverride: source.MetadataWidths,
                persistSettings: false);
            ApplyPanelTabBehaviorToOpenPanel(panel, source);

            if ((hiddenChanged || parentNavigationChanged || iconParentNavigationModeChanged || fileExtensionsChanged) &&
                !string.IsNullOrWhiteSpace(panel.currentFolderPath))
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
                PanelKind.RecycleBin => true,
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
                PanelDefaultShowParentNavigationItem = true,
                PanelDefaultIconViewParentNavigationMode = DesktopPanel.IconParentNavigationModeItem,
                PanelDefaultShowFileExtensions = true,
                PanelDefaultExpandOnHover = false,
                PanelDefaultOpenFoldersExternally = false,
                PanelDefaultOpenItemsOnSingleClick = false,
                PanelDefaultShowSettingsButton = true,
                PanelDefaultShowEmptyRecycleBinButton = false,
                PanelDefaultMovementMode = "titlebar",
                PanelDefaultSearchVisibilityMode = DesktopPanel.SearchVisibilityAlways,
                PanelDefaultViewMode = DesktopPanel.ViewModeIcons,
                PanelDefaultShowMetadataType = true,
                PanelDefaultShowMetadataSize = true,
                PanelDefaultShowMetadataCreated = false,
                PanelDefaultShowMetadataModified = true,
                PanelDefaultShowMetadataDimensions = false,
                PanelDefaultShowMetadataAuthors = false,
                PanelDefaultShowMetadataCategories = false,
                PanelDefaultShowMetadataTags = false,
                PanelDefaultShowMetadataTitle = false,
                PanelDefaultMetadataOrder = DesktopPanel.NormalizeMetadataOrder(null),
                PanelDefaultMetadataWidths = DesktopPanel.NormalizeMetadataWidths(null),
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
                PanelDefaultShowParentNavigationItem = true,
                PanelDefaultIconViewParentNavigationMode = DesktopPanel.IconParentNavigationModeItem,
                PanelDefaultShowFileExtensions = true,
                PanelDefaultExpandOnHover = false,
                PanelDefaultOpenFoldersExternally = false,
                PanelDefaultOpenItemsOnSingleClick = false,
                PanelDefaultShowSettingsButton = true,
                PanelDefaultShowEmptyRecycleBinButton = false,
                PanelDefaultMovementMode = "titlebar",
                PanelDefaultSearchVisibilityMode = DesktopPanel.SearchVisibilityAlways,
                PanelDefaultViewMode = DesktopPanel.ViewModeIcons,
                PanelDefaultShowMetadataType = true,
                PanelDefaultShowMetadataSize = true,
                PanelDefaultShowMetadataCreated = false,
                PanelDefaultShowMetadataModified = true,
                PanelDefaultShowMetadataDimensions = false,
                PanelDefaultShowMetadataAuthors = false,
                PanelDefaultShowMetadataCategories = false,
                PanelDefaultShowMetadataTags = false,
                PanelDefaultShowMetadataTitle = false,
                PanelDefaultMetadataOrder = DesktopPanel.NormalizeMetadataOrder(null),
                PanelDefaultMetadataWidths = DesktopPanel.NormalizeMetadataWidths(null),
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
            bool showParentNavigationItem,
            string iconViewParentNavigationMode,
            bool showFileExtensions,
            bool expandOnHover,
            bool openFoldersExternally,
            bool openItemsOnSingleClick,
            bool showSettingsButton,
            string movementMode,
            string searchVisibilityMode,
            string viewMode,
            bool showMetadataType,
            bool showMetadataSize,
            bool showMetadataCreated,
            bool showMetadataModified,
            bool showMetadataDimensions,
            bool showMetadataAuthors,
            bool showMetadataCategories,
            bool showMetadataTags,
            bool showMetadataTitle,
            IEnumerable<string>? metadataOrder,
            IDictionary<string, double>? metadataWidths,
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
                ShowParentNavigationItem = showParentNavigationItem,
                IconViewParentNavigationMode = DesktopPanel.NormalizeIconViewParentNavigationMode(
                    iconViewParentNavigationMode,
                    showParentNavigationItem),
                ShowFileExtensions = showFileExtensions,
                ExpandOnHover = expandOnHover,
                OpenFoldersExternally = openFoldersExternally,
                OpenItemsOnSingleClick = openItemsOnSingleClick,
                ShowSettingsButton = showSettingsButton,
                MovementMode = NormalizePanelMovementMode(movementMode),
                SearchVisibilityMode = DesktopPanel.NormalizeSearchVisibilityMode(searchVisibilityMode),
                ViewMode = DesktopPanel.NormalizeViewMode(viewMode),
                ShowMetadataType = showMetadataType,
                ShowMetadataSize = showMetadataSize,
                ShowMetadataCreated = showMetadataCreated,
                ShowMetadataModified = showMetadataModified,
                ShowMetadataDimensions = showMetadataDimensions,
                ShowMetadataAuthors = showMetadataAuthors,
                ShowMetadataCategories = showMetadataCategories,
                ShowMetadataTags = showMetadataTags,
                ShowMetadataTitle = showMetadataTitle,
                MetadataOrder = DesktopPanel.NormalizeMetadataOrder(metadataOrder),
                MetadataWidths = DesktopPanel.NormalizeMetadataWidths(metadataWidths)
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
                ApplyLayoutDefaultsToPanelWhenMatching(panel, oldDefaults, newDefaults);
                ApplyLayoutDefaultsToPanelTabsWhenMatching(panel, oldDefaults, newDefaults);
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
