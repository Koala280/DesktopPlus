using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private sealed class OpenPanelRepresentation
        {
            public string OwnerPanelKey { get; set; } = "";
            public string PanelKey { get; set; } = "";
            public string TabId { get; set; } = "";
            public DesktopPanel HostPanel { get; set; } = null!;
            public int TabIndex { get; set; } = -1;
            public PanelKind PanelType { get; set; } = PanelKind.None;
            public string Title { get; set; } = "";
            public string FolderPath { get; set; } = "";
            public string DefaultFolderPath { get; set; } = "";
            public List<string> PinnedItems { get; set; } = new List<string>();
            public string PresetName { get; set; } = "";
            public bool IsHidden { get; set; }
        }

        private sealed class SavedPanelRepresentation
        {
            public WindowData Window { get; set; } = null!;
            public PanelTabData? Tab { get; set; }
            public int TabIndex { get; set; } = -1;
            public string OwnerPanelKey { get; set; } = "";
            public string PanelKey { get; set; } = "";
            public string TabId { get; set; } = "";
            public PanelKind PanelType { get; set; } = PanelKind.None;
            public string Title { get; set; } = "";
            public string FolderPath { get; set; } = "";
            public string DefaultFolderPath { get; set; } = "";
            public List<string> PinnedItems { get; set; } = new List<string>();
            public string PresetName { get; set; } = "";
            public bool IsHidden { get; set; }
        }

        private static PanelKind ResolvePanelKind(PanelTabData tab)
        {
            if (tab == null)
            {
                return PanelKind.None;
            }

            if (!string.IsNullOrWhiteSpace(tab.PanelType) &&
                Enum.TryParse(tab.PanelType, true, out PanelKind parsed))
            {
                return parsed;
            }

            if (!string.IsNullOrWhiteSpace(tab.FolderPath))
            {
                return PanelKind.Folder;
            }

            if (tab.PinnedItems != null && tab.PinnedItems.Count > 0)
            {
                return PanelKind.List;
            }

            return PanelKind.None;
        }

        private static bool AreOverviewPathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                string normalizedLeft = System.IO.Path.GetFullPath(left)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string normalizedRight = System.IO.Path.GetFullPath(right)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool ArePinnedItemListsEquivalent(
            IReadOnlyCollection<string>? left,
            IReadOnlyCollection<string>? right)
        {
            var leftSet = new HashSet<string>(
                left?.Where(path => !string.IsNullOrWhiteSpace(path)) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var rightSet = new HashSet<string>(
                right?.Where(path => !string.IsNullOrWhiteSpace(path)) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            return leftSet.SetEquals(rightSet);
        }

        private static string BuildPanelOverviewFolderLabel(
            PanelKind kind,
            string? folderPath,
            IReadOnlyCollection<string>? pinnedItems)
        {
            return kind == PanelKind.List
                ? string.Format(GetString("Loc.PanelTypeList"), pinnedItems?.Count ?? 0)
                : kind == PanelKind.RecycleBin
                    ? GetString("Loc.PanelTypeRecycleBin")
                    : string.IsNullOrWhiteSpace(folderPath) ? GetString("Loc.NoFolder") : folderPath;
        }

        private List<OpenPanelRepresentation> CreateOpenPanelRepresentations(IReadOnlyList<DesktopPanel> openPanels)
        {
            var items = new List<OpenPanelRepresentation>();

            foreach (var panel in openPanels)
            {
                panel.SaveActiveTabState();
                string ownerPanelKey = GetPanelKey(panel);

                if (panel.Tabs.Count > 0)
                {
                    for (int i = 0; i < panel.Tabs.Count; i++)
                    {
                        PanelTabData tab = panel.Tabs[i];
                        string panelKey = !string.IsNullOrWhiteSpace(tab.PanelId)
                            ? tab.PanelId
                            : (i == 0 ? ownerPanelKey : $"paneltab:{tab.TabId}");
                        PanelKind kind = ResolvePanelKind(tab);
                        items.Add(new OpenPanelRepresentation
                        {
                            OwnerPanelKey = ownerPanelKey,
                            PanelKey = panelKey,
                            TabId = tab.TabId ?? string.Empty,
                            HostPanel = panel,
                            TabIndex = i,
                            PanelType = kind,
                            Title = !string.IsNullOrWhiteSpace(tab.TabName)
                                ? tab.TabName
                                : (!string.IsNullOrWhiteSpace(panel.Title) ? panel.Title : GetString("Loc.Untitled")),
                            FolderPath = tab.FolderPath ?? string.Empty,
                            DefaultFolderPath = tab.DefaultFolderPath ?? string.Empty,
                            PinnedItems = tab.PinnedItems?.ToList() ?? new List<string>(),
                            PresetName = string.IsNullOrWhiteSpace(panel.assignedPresetName) ? DefaultPresetName : panel.assignedPresetName,
                            IsHidden = tab.IsHidden
                        });
                    }

                    continue;
                }

                var kindFromPanel = ResolvePanelKind(panel);
                items.Add(new OpenPanelRepresentation
                {
                    OwnerPanelKey = ownerPanelKey,
                    PanelKey = ownerPanelKey,
                    HostPanel = panel,
                    TabIndex = -1,
                    PanelType = kindFromPanel,
                    Title = !string.IsNullOrWhiteSpace(panel.Title) ? panel.Title : GetString("Loc.Untitled"),
                    FolderPath = panel.currentFolderPath ?? string.Empty,
                    DefaultFolderPath = panel.defaultFolderPath ?? string.Empty,
                    PinnedItems = panel.PinnedItems.ToList(),
                    PresetName = string.IsNullOrWhiteSpace(panel.assignedPresetName) ? DefaultPresetName : panel.assignedPresetName,
                    IsHidden = false
                });
            }

            return items;
        }

        private static string BuildPanelOverviewTitle(string? title, PanelKind kind, string? folderPath)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            if (kind == PanelKind.RecycleBin)
            {
                return GetString("Loc.PanelsRecycleBin");
            }

            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                return System.IO.Path.GetFileName(folderPath) ?? folderPath;
            }

            return GetString("Loc.Untitled");
        }

        private static string NormalizeOverviewSignaturePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return System.IO.Path.GetFullPath(path)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                    .ToUpperInvariant();
            }
            catch
            {
                return path.Trim().ToUpperInvariant();
            }
        }

        private static string BuildOverviewSignature(
            PanelKind kind,
            string? title,
            string? folderPath,
            string? defaultFolderPath,
            IReadOnlyCollection<string>? pinnedItems)
        {
            string normalizedTitle = string.IsNullOrWhiteSpace(title)
                ? string.Empty
                : title.Trim().ToUpperInvariant();
            string normalizedFolder = NormalizeOverviewSignaturePath(folderPath);
            string normalizedDefaultFolder = NormalizeOverviewSignaturePath(defaultFolderPath);
            string normalizedPinned = string.Join("|",
                (pinnedItems ?? Array.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizeOverviewSignaturePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

            return $"{kind}|{normalizedTitle}|{normalizedFolder}|{normalizedDefaultFolder}|{normalizedPinned}";
        }

        private static string BuildContentOnlySignature(
            PanelKind kind,
            string? folderPath,
            string? defaultFolderPath,
            IReadOnlyCollection<string>? pinnedItems)
        {
            string normalizedFolder = NormalizeOverviewSignaturePath(folderPath);
            string normalizedDefaultFolder = NormalizeOverviewSignaturePath(defaultFolderPath);
            string normalizedPinned = string.Join("|",
                (pinnedItems ?? Array.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizeOverviewSignaturePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

            return $"{kind}|{normalizedFolder}|{normalizedDefaultFolder}|{normalizedPinned}";
        }

        private static string BuildWindowStructureSignature(WindowData window)
        {
            if (window == null)
            {
                return string.Empty;
            }

            NormalizeWindowData(window);
            if (window.Tabs == null || window.Tabs.Count == 0)
            {
                PanelKind kind = ResolvePanelKind(window);
                string title = BuildPanelOverviewTitle(window.PanelTitle, kind, window.FolderPath);
                return "single|" + BuildOverviewSignature(
                    kind,
                    title,
                    window.FolderPath,
                    window.DefaultFolderPath,
                    window.PinnedItems);
            }

            string tabsSignature = string.Join("||",
                window.Tabs.Select(tab => BuildOverviewSignature(
                    ResolvePanelKind(tab),
                    BuildPanelOverviewTitle(tab.TabName, ResolvePanelKind(tab), tab.FolderPath),
                    tab.FolderPath,
                    tab.DefaultFolderPath,
                    tab.PinnedItems)));

            string activeTabSignature = window.ActiveTabIndex >= 0 && window.ActiveTabIndex < window.Tabs.Count
                ? BuildOverviewSignature(
                    ResolvePanelKind(window.Tabs[window.ActiveTabIndex]),
                    BuildPanelOverviewTitle(window.Tabs[window.ActiveTabIndex].TabName, ResolvePanelKind(window.Tabs[window.ActiveTabIndex]), window.Tabs[window.ActiveTabIndex].FolderPath),
                    window.Tabs[window.ActiveTabIndex].FolderPath,
                    window.Tabs[window.ActiveTabIndex].DefaultFolderPath,
                    window.Tabs[window.ActiveTabIndex].PinnedItems)
                : string.Empty;

            return $"multi|{tabsSignature}|active:{activeTabSignature}";
        }

        private static int ScoreSavedWindowForDedup(WindowData window)
        {
            if (window == null)
            {
                return int.MinValue;
            }

            int score = 0;
            if (!window.IsHidden)
            {
                score += 100;
            }

            if (!string.IsNullOrWhiteSpace(window.PanelId) &&
                !window.PanelId.StartsWith("paneltab:", StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            if (window.Tabs != null && window.Tabs.Count > 0)
            {
                score += 10;
                if (window.ActiveTabIndex >= 0 && window.ActiveTabIndex < window.Tabs.Count)
                {
                    string activeTabPanelId = window.Tabs[window.ActiveTabIndex].PanelId ?? string.Empty;
                    if (!string.Equals(activeTabPanelId, window.PanelId, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 5;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(window.PanelTitle) &&
                !string.Equals(window.PanelTitle, "Panel", StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            return score;
        }

        private static bool PruneRedundantSavedWindows(IReadOnlyCollection<DesktopPanel>? openPanels = null)
        {
            savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
            foreach (var window in savedWindows)
            {
                NormalizeWindowData(window);
            }

            var openPanelKeys = new HashSet<string>(
                (openPanels ?? Array.Empty<DesktopPanel>())
                    .Where(IsUserPanel)
                    .Select(GetPanelKey),
                StringComparer.OrdinalIgnoreCase);

            bool changed = false;

            changed |= PruneShadowedSingleTabHosts(savedWindows, openPanelKeys);

            void RemoveWindows(HashSet<string> keys)
            {
                if (keys.Count == 0)
                {
                    return;
                }

                savedWindows = savedWindows
                    .Where(window => window != null && !keys.Contains(GetPanelKey(window)))
                    .ToList();
                changed = true;
            }

            var duplicateMultiTabKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var multiTabWindows = savedWindows
                .Where(window => window?.Tabs != null && window.Tabs.Count > 0)
                .ToList();

            foreach (var group in multiTabWindows.GroupBy(BuildWindowStructureSignature, StringComparer.OrdinalIgnoreCase))
            {
                var duplicates = group.ToList();
                if (duplicates.Count <= 1)
                {
                    continue;
                }

                WindowData keep = duplicates
                    .OrderByDescending(window =>
                        ScoreSavedWindowForDedup(window) +
                        (openPanelKeys.Contains(GetPanelKey(window)) ? 1000 : 0))
                    .ThenBy(window => window.PanelId, StringComparer.OrdinalIgnoreCase)
                    .First();

                foreach (var duplicate in duplicates)
                {
                    if (!ReferenceEquals(duplicate, keep))
                    {
                        duplicateMultiTabKeys.Add(GetPanelKey(duplicate));
                    }
                }
            }

            RemoveWindows(duplicateMultiTabKeys);

            savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
            foreach (var window in savedWindows)
            {
                NormalizeWindowData(window);
            }

            multiTabWindows = savedWindows
                .Where(window => window?.Tabs != null && window.Tabs.Count > 0)
                .ToList();

            var tabContentSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in multiTabWindows)
            {
                foreach (var tab in window.Tabs!)
                {
                    tabContentSignatures.Add(BuildOverviewSignature(
                        ResolvePanelKind(tab),
                        BuildPanelOverviewTitle(tab.TabName, ResolvePanelKind(tab), tab.FolderPath),
                        tab.FolderPath,
                        tab.DefaultFolderPath,
                        tab.PinnedItems));
                }
            }

            foreach (DesktopPanel panel in openPanels ?? Array.Empty<DesktopPanel>())
            {
                if (!IsUserPanel(panel))
                {
                    continue;
                }

                panel.SaveActiveTabState();
                foreach (var tab in panel.Tabs)
                {
                    tabContentSignatures.Add(BuildOverviewSignature(
                        ResolvePanelKind(tab),
                        BuildPanelOverviewTitle(tab.TabName, ResolvePanelKind(tab), tab.FolderPath),
                        tab.FolderPath,
                        tab.DefaultFolderPath,
                        tab.PinnedItems));
                }
            }

            var standaloneKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in savedWindows.Where(window => window != null && (window.Tabs == null || window.Tabs.Count == 0)))
            {
                PanelKind kind = ResolvePanelKind(window);
                string signature = BuildOverviewSignature(
                    kind,
                    BuildPanelOverviewTitle(window.PanelTitle, kind, window.FolderPath),
                    window.FolderPath,
                    window.DefaultFolderPath,
                    window.PinnedItems);

                if (tabContentSignatures.Contains(signature) &&
                    !openPanelKeys.Contains(GetPanelKey(window)))
                {
                    standaloneKeys.Add(GetPanelKey(window));
                }
            }

            RemoveWindows(standaloneKeys);

            savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
            foreach (var window in savedWindows)
            {
                NormalizeWindowData(window);
            }

            var representedOwnerKeys = new HashSet<string>(
                savedWindows.Where(window => window != null).Select(GetPanelKey),
                StringComparer.OrdinalIgnoreCase);
            foreach (string openKey in openPanelKeys)
            {
                representedOwnerKeys.Add(openKey);
            }

            var orphanedMultiTabKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in savedWindows.Where(window => window?.Tabs != null && window.Tabs.Count > 1))
            {
                string ownerKey = GetPanelKey(window);
                if (openPanelKeys.Contains(ownerKey))
                {
                    continue;
                }

                bool allTabsRepresentedElsewhere = window.Tabs!.All(tab =>
                    !string.IsNullOrWhiteSpace(tab.PanelId) &&
                    representedOwnerKeys.Any(key =>
                        !string.Equals(key, ownerKey, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(key, tab.PanelId, StringComparison.OrdinalIgnoreCase)));

                if (allTabsRepresentedElsewhere)
                {
                    orphanedMultiTabKeys.Add(ownerKey);
                }
            }

            RemoveWindows(orphanedMultiTabKeys);
            return changed;
        }

        private static bool PruneShadowedSingleTabHosts(
            List<WindowData> windows,
            IReadOnlyCollection<string>? livePanelKeys = null)
        {
            if (windows == null || windows.Count == 0)
            {
                return false;
            }

            var liveKeys = new HashSet<string>(
                livePanelKeys ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var standaloneKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var window in windows)
            {
                if (window == null)
                {
                    continue;
                }

                NormalizeWindowData(window);
                if (window.Tabs == null || window.Tabs.Count == 0)
                {
                    standaloneKeys.Add(GetPanelKey(window));
                }
            }

            var wrapperKeysToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in windows)
            {
                if (window?.Tabs == null || window.Tabs.Count != 1)
                {
                    continue;
                }

                string ownerKey = GetPanelKey(window);
                string tabPanelKey = window.Tabs[0].PanelId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(tabPanelKey) ||
                    string.Equals(ownerKey, tabPanelKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (standaloneKeys.Contains(tabPanelKey) || liveKeys.Contains(tabPanelKey))
                {
                    wrapperKeysToRemove.Add(ownerKey);
                }
            }

            if (wrapperKeysToRemove.Count == 0)
            {
                return false;
            }

            windows.RemoveAll(window =>
                window != null &&
                wrapperKeysToRemove.Contains(GetPanelKey(window)));
            return true;
        }

        private List<SavedPanelRepresentation> CreateSavedPanelRepresentations(IEnumerable<WindowData> windows)
        {
            var items = new List<SavedPanelRepresentation>();

            foreach (var window in windows)
            {
                if (window == null)
                {
                    continue;
                }

                NormalizeWindowData(window);
                string ownerPanelKey = GetPanelKey(window);
                string presetName = string.IsNullOrWhiteSpace(window.PresetName) ? DefaultPresetName : window.PresetName;

                if (window.Tabs != null && window.Tabs.Count > 0)
                {
                    for (int i = 0; i < window.Tabs.Count; i++)
                    {
                        PanelTabData tab = window.Tabs[i];
                        PanelKind kind = ResolvePanelKind(tab);
                        string folderPath = tab.FolderPath ?? string.Empty;

                        items.Add(new SavedPanelRepresentation
                        {
                            Window = window,
                            Tab = tab,
                            TabIndex = i,
                            OwnerPanelKey = ownerPanelKey,
                            PanelKey = tab.PanelId ?? string.Empty,
                            TabId = tab.TabId ?? string.Empty,
                            PanelType = kind,
                            Title = BuildPanelOverviewTitle(tab.TabName, kind, folderPath),
                            FolderPath = folderPath,
                            DefaultFolderPath = tab.DefaultFolderPath ?? string.Empty,
                            PinnedItems = tab.PinnedItems?.ToList() ?? new List<string>(),
                            PresetName = presetName,
                            IsHidden = window.IsHidden || tab.IsHidden
                        });
                    }

                    continue;
                }

                PanelKind windowKind = ResolvePanelKind(window);
                string windowFolderPath = window.FolderPath ?? string.Empty;
                items.Add(new SavedPanelRepresentation
                {
                    Window = window,
                    Tab = null,
                    TabIndex = -1,
                    OwnerPanelKey = ownerPanelKey,
                    PanelKey = ownerPanelKey,
                    TabId = string.Empty,
                    PanelType = windowKind,
                    Title = BuildPanelOverviewTitle(window.PanelTitle, windowKind, windowFolderPath),
                    FolderPath = windowFolderPath,
                    DefaultFolderPath = window.DefaultFolderPath ?? string.Empty,
                    PinnedItems = window.PinnedItems?.ToList() ?? new List<string>(),
                    PresetName = presetName,
                    IsHidden = window.IsHidden
                });
            }

            return items;
        }

        private bool TryResolveSavedOverviewTarget(
            PanelOverviewItem item,
            out WindowData? window,
            out PanelTabData? tab,
            out int tabIndex)
        {
            window = null;
            tab = null;
            tabIndex = -1;

            if (!string.IsNullOrWhiteSpace(item.OwnerPanelKey))
            {
                window = FindSavedWindow(item.OwnerPanelKey);
            }

            if (window == null && !string.IsNullOrWhiteSpace(item.PanelKey))
            {
                window = FindSavedWindow(item.PanelKey);
            }

            if (window == null)
            {
                foreach (var candidate in savedWindows)
                {
                    if (candidate?.Tabs == null || candidate.Tabs.Count == 0)
                    {
                        continue;
                    }

                    NormalizeWindowData(candidate);
                    for (int i = 0; i < candidate.Tabs.Count; i++)
                    {
                        if (string.Equals(candidate.Tabs[i].PanelId, item.PanelKey, StringComparison.OrdinalIgnoreCase))
                        {
                            window = candidate;
                            break;
                        }
                    }

                    if (window != null)
                    {
                        break;
                    }
                }
            }

            if (window == null)
            {
                return false;
            }

            NormalizeWindowData(window);
            if (window.Tabs != null)
            {
                for (int i = 0; i < window.Tabs.Count; i++)
                {
                    if (string.Equals(window.Tabs[i].PanelId, item.PanelKey, StringComparison.OrdinalIgnoreCase))
                    {
                        tab = window.Tabs[i];
                        tabIndex = i;
                        break;
                    }
                }
            }

            return true;
        }

        private static void SyncSavedWindowHiddenState(WindowData window)
        {
            if (window == null || window.Tabs == null || window.Tabs.Count == 0)
            {
                return;
            }

            if (window.Tabs.All(tab => tab.IsHidden))
            {
                window.IsHidden = true;
            }
            else if (window.IsHidden && window.Tabs.Any(tab => !tab.IsHidden))
            {
                window.IsHidden = false;
            }
        }

        private bool TryGetOrCreateSavedOverviewTarget(
            PanelOverviewItem item,
            out WindowData? window,
            out PanelTabData? tab,
            out int tabIndex)
        {
            if (TryResolveSavedOverviewTarget(item, out window, out tab, out tabIndex))
            {
                return true;
            }

            if (item.Panel == null)
            {
                return false;
            }

            var snapshot = BuildWindowDataFromPanel(item.Panel);
            string snapshotKey = GetPanelKey(snapshot);
            var existing = FindSavedWindow(snapshotKey);
            if (existing != null)
            {
                CopyWindowData(snapshot, existing);
            }
            else
            {
                savedWindows.Add(snapshot);
            }

            return TryResolveSavedOverviewTarget(item, out window, out tab, out tabIndex);
        }

        private int ComputeOpenRepresentationMatchScore(WindowData saved, OpenPanelRepresentation representation)
        {
            PanelKind savedKind = ResolvePanelKind(saved);
            if (savedKind != representation.PanelType)
            {
                return -1;
            }

            int score = 0;
            switch (savedKind)
            {
                case PanelKind.Folder:
                    string preferredFolder = ResolvePreferredFolderPath(saved);
                    if (!AreOverviewPathsEqual(saved.FolderPath, representation.FolderPath) &&
                        !AreOverviewPathsEqual(preferredFolder, representation.FolderPath) &&
                        !AreOverviewPathsEqual(saved.DefaultFolderPath, representation.DefaultFolderPath))
                    {
                        return -1;
                    }

                    score += 100;
                    break;
                case PanelKind.List:
                    if (!ArePinnedItemListsEquivalent(saved.PinnedItems, representation.PinnedItems))
                    {
                        return -1;
                    }

                    score += 100;
                    break;
                case PanelKind.RecycleBin:
                    score += 100;
                    break;
                default:
                    score += 10;
                    break;
            }

            if (!string.IsNullOrWhiteSpace(saved.PanelTitle) &&
                string.Equals(saved.PanelTitle.Trim(), representation.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            if (!string.IsNullOrWhiteSpace(saved.DefaultFolderPath) &&
                AreOverviewPathsEqual(saved.DefaultFolderPath, representation.DefaultFolderPath))
            {
                score += 10;
            }

            return score;
        }

        private OpenPanelRepresentation? FindOpenRepresentationForSavedWindow(
            WindowData saved,
            Dictionary<string, OpenPanelRepresentation> byKey,
            IReadOnlyList<OpenPanelRepresentation> allRepresentations)
        {
            string savedKey = GetPanelKey(saved);
            if (byKey.TryGetValue(savedKey, out var direct))
            {
                return direct;
            }

            OpenPanelRepresentation? best = null;
            int bestScore = -1;
            bool ambiguous = false;

            foreach (var representation in allRepresentations)
            {
                int score = ComputeOpenRepresentationMatchScore(saved, representation);
                if (score < 0)
                {
                    continue;
                }

                if (score > bestScore)
                {
                    best = representation;
                    bestScore = score;
                    ambiguous = false;
                }
                else if (score == bestScore)
                {
                    ambiguous = true;
                }
            }

            if (best == null || bestScore < 0 || ambiguous)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(best.PanelKey))
            {
                best.PanelKey = savedKey;
                if (best.TabIndex >= 0 &&
                    best.TabIndex < best.HostPanel.Tabs.Count &&
                    string.IsNullOrWhiteSpace(best.HostPanel.Tabs[best.TabIndex].PanelId))
                {
                    best.HostPanel.Tabs[best.TabIndex].PanelId = savedKey;
                }
            }

            byKey[savedKey] = best;
            return best;
        }

        private static void RevealPanelOverviewHost(PanelOverviewItem item, bool switchToTab)
        {
            if (item.Panel == null)
            {
                return;
            }

            item.Panel.Show();
            item.Panel.WindowState = WindowState.Normal;

            if (switchToTab &&
                item.HostTabIndex >= 0 &&
                item.HostTabIndex < item.Panel.Tabs.Count &&
                item.Panel.ActiveTabIndex != item.HostTabIndex)
            {
                item.Panel.SwitchToTab(item.HostTabIndex);
            }
        }

        private static bool IsDirectPanelOverviewBinding(PanelOverviewItem item)
        {
            return item.Panel != null &&
                string.Equals(GetPanelKey(item.Panel), item.PanelKey, StringComparison.OrdinalIgnoreCase);
        }

        private static int ScorePanelOverviewItemForDedup(PanelOverviewItem item)
        {
            int score = 0;

            if (item.Panel != null)
            {
                score += 100;
            }

            if (item.IsOpen)
            {
                score += 80;
            }

            if (!item.IsHidden)
            {
                score += 40;
            }

            if (!string.IsNullOrWhiteSpace(item.OwnerPanelKey) &&
                string.Equals(item.OwnerPanelKey, item.PanelKey, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (!string.IsNullOrWhiteSpace(item.TabId))
            {
                score += 5;
            }

            return score;
        }

        private static PanelOverviewItem SelectPreferredPanelOverviewItem(IEnumerable<PanelOverviewItem> items)
        {
            return items
                .OrderByDescending(item => item.Panel != null && IsDirectPanelOverviewBinding(item))
                .ThenByDescending(ScorePanelOverviewItemForDedup)
                .ThenBy(item => item.PanelKey, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        private static string GetLivePanelOverviewBindingKey(PanelOverviewItem item)
        {
            if (item.Panel == null)
            {
                return string.Empty;
            }

            return string.Concat(
                item.Panel.GetHashCode().ToString(),
                "|",
                GetPanelKey(item.Panel),
                "|",
                item.HostTabIndex.ToString(),
                "|",
                item.RepresentsTab ? "tab" : "panel");
        }

        private List<PanelOverviewItem> DeduplicatePanelOverviewItems(IEnumerable<PanelOverviewItem> items)
        {
            var deduped = new List<PanelOverviewItem>();

            foreach (var group in items
                .Where(item => item != null)
                .GroupBy(
                    item => string.IsNullOrWhiteSpace(item.OverviewSignature) ? item.PanelKey : item.OverviewSignature,
                    StringComparer.OrdinalIgnoreCase))
            {
                var duplicates = group.ToList();
                if (duplicates.Count <= 1)
                {
                    deduped.AddRange(duplicates);
                    continue;
                }

                var liveItems = duplicates
                    .Where(item => item.Panel != null)
                    .ToList();

                if (liveItems.Count > 0)
                {
                    var uniqueLiveItems = liveItems
                        .GroupBy(GetLivePanelOverviewBindingKey, StringComparer.OrdinalIgnoreCase)
                        .Select(SelectPreferredPanelOverviewItem)
                        .ToList();

                    deduped.AddRange(uniqueLiveItems);
                    continue;
                }

                deduped.Add(SelectPreferredPanelOverviewItem(duplicates));
            }

            return deduped;
        }

        private void RefreshPanelOverview()
        {
            if (PanelOverviewList == null || PanelOverviewCount == null) return;

            savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
            var openPanelList = System.Windows.Application.Current.Windows
                .OfType<DesktopPanel>()
                .Where(IsUserPanel)
                .ToList();
            PruneRedundantSavedWindows(openPanelList);
            var openRepresentations = CreateOpenPanelRepresentations(openPanelList);
            var openRepresentationByKey = openRepresentations
                .Where(item => !string.IsNullOrWhiteSpace(item.PanelKey))
                .GroupBy(item => item.PanelKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

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

            var openWindowSignatures = openRepresentations
                .Where(open => open.TabIndex >= 0)
                .GroupBy(open => open.OwnerPanelKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(open => BuildOverviewSignature(
                            open.PanelType,
                            open.Title,
                            open.FolderPath,
                            open.DefaultFolderPath,
                            open.PinnedItems))
                        .OrderBy(signature => signature, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var suppressedOwnerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in savedWindows)
            {
                if (window?.IsHidden != true || window.Tabs == null || window.Tabs.Count == 0)
                {
                    continue;
                }

                string ownerKey = GetPanelKey(window);
                if (openWindowSignatures.ContainsKey(ownerKey))
                {
                    continue;
                }

                var savedSignatures = window.Tabs
                    .Select(tab => BuildOverviewSignature(
                        ResolvePanelKind(tab),
                        tab.TabName,
                        tab.FolderPath,
                        tab.DefaultFolderPath,
                        tab.PinnedItems))
                    .OrderBy(signature => signature, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (savedSignatures.Count == 0)
                {
                    continue;
                }

                if (openWindowSignatures.Values.Any(openSignatures =>
                    openSignatures.Count == savedSignatures.Count &&
                    openSignatures.SequenceEqual(savedSignatures, StringComparer.OrdinalIgnoreCase)))
                {
                    suppressedOwnerKeys.Add(ownerKey);
                }
            }

            var savedRepresentations = CreateSavedPanelRepresentations(
                savedWindows.Where(window => window != null && !suppressedOwnerKeys.Contains(GetPanelKey(window))));
            var items = savedRepresentations.Select(saved =>
            {
                openRepresentationByKey.TryGetValue(saved.PanelKey, out var open);
                DesktopPanel? displayPanel = open?.HostPanel;
                bool hostVisible = displayPanel?.IsVisible == true;
                bool tabHidden = open?.IsHidden ?? saved.IsHidden;

                if (displayPanel != null && hostVisible && !tabHidden)
                {
                    saved.Window.IsHidden = false;
                    if (saved.Tab != null)
                    {
                        saved.Tab.IsHidden = false;
                    }
                }
                else if (saved.Tab != null && open != null && open.IsHidden)
                {
                    saved.Tab.IsHidden = true;
                }

                bool isOpen = hostVisible && !tabHidden;
                bool isHidden = saved.Window.IsHidden || tabHidden || (displayPanel != null && !hostVisible);
                string presetName = displayPanel != null && !string.IsNullOrWhiteSpace(displayPanel.assignedPresetName)
                    ? displayPanel.assignedPresetName
                    : (open?.PresetName ?? saved.PresetName);
                string title = BuildPanelOverviewTitle(open?.Title ?? saved.Title, open?.PanelType ?? saved.PanelType, open?.FolderPath ?? saved.FolderPath);
                string folderPath = open?.FolderPath ?? saved.FolderPath;
                string defaultFolderPath = open?.DefaultFolderPath ?? saved.DefaultFolderPath;
                PanelKind kind = open?.PanelType ?? saved.PanelType;
                var pinnedItems = open?.PinnedItems ?? saved.PinnedItems;
                string state = isOpen
                    ? (displayPanel!.isContentVisible ? GetString("Loc.PanelStateOpen") : GetString("Loc.PanelStateCollapsed"))
                    : (isHidden ? GetString("Loc.PanelStateHidden") : GetString("Loc.PanelStateClosed"));
                bool toggleIsShow = !isOpen;
                string toggleLabel = toggleIsShow ? GetString("Loc.PanelsShow") : GetString("Loc.PanelsHide");
                string folderLabel = BuildPanelOverviewFolderLabel(
                    kind,
                    folderPath,
                    pinnedItems);

                return new PanelOverviewItem
                {
                    Title = title,
                    Folder = folderLabel,
                    FolderPath = folderPath,
                    OwnerPanelKey = saved.OwnerPanelKey,
                    TabId = saved.TabId,
                    PanelKey = saved.PanelKey,
                    PanelType = kind,
                    State = state,
                    Size = displayPanel != null ? $"{(int)displayPanel.Width} x {(int)displayPanel.Height}" : $"{(int)saved.Window.Width} x {(int)saved.Window.Height}",
                    Position = displayPanel != null ? $"{(int)displayPanel.Left}, {(int)displayPanel.Top}" : $"{(int)saved.Window.Left}, {(int)saved.Window.Top}",
                    IsOpen = isOpen,
                    IsHidden = isHidden,
                    ToggleIsShow = toggleIsShow,
                    ToggleLabel = toggleLabel,
                    Panel = displayPanel,
                    HostTabIndex = open?.TabIndex ?? saved.TabIndex,
                    RepresentsTab = saved.TabIndex >= 0 || open?.TabIndex >= 0,
                    PresetName = presetName,
                    OverviewSignature = BuildOverviewSignature(
                        kind,
                        title,
                        folderPath,
                        defaultFolderPath,
                        pinnedItems)
                };
            })
            .ToList();

            var representedPanelKeys = new HashSet<string>(
                items.Select(x => x.PanelKey).Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            var openOnlyItems = openRepresentations
                .Where(open => !representedPanelKeys.Contains(open.PanelKey))
                .Select(open =>
                {
                    DesktopPanel panel = open.HostPanel;
                    bool hostVisible = panel.IsVisible;
                    bool isOpen = hostVisible && !open.IsHidden;
                    bool isHidden = open.IsHidden || !hostVisible;
                    string title = BuildPanelOverviewTitle(open.Title, open.PanelType, open.FolderPath);
                    return new PanelOverviewItem
                    {
                        Title = title,
                        Folder = BuildPanelOverviewFolderLabel(open.PanelType, open.FolderPath, open.PinnedItems),
                        FolderPath = open.FolderPath,
                        OwnerPanelKey = open.OwnerPanelKey,
                        TabId = open.TabId,
                        PanelKey = open.PanelKey,
                        PanelType = open.PanelType,
                        State = isOpen
                            ? (panel.isContentVisible ? GetString("Loc.PanelStateOpen") : GetString("Loc.PanelStateCollapsed"))
                            : (isHidden ? GetString("Loc.PanelStateHidden") : GetString("Loc.PanelStateClosed")),
                        Size = $"{(int)panel.Width} x {(int)panel.Height}",
                        Position = $"{(int)panel.Left}, {(int)panel.Top}",
                        IsOpen = isOpen,
                        IsHidden = isHidden,
                        ToggleIsShow = !isOpen,
                        ToggleLabel = isOpen ? GetString("Loc.PanelsHide") : GetString("Loc.PanelsShow"),
                        Panel = panel,
                        HostTabIndex = open.TabIndex,
                        RepresentsTab = open.TabIndex >= 0,
                        PresetName = open.PresetName,
                        OverviewSignature = BuildOverviewSignature(
                            open.PanelType,
                            title,
                            open.FolderPath,
                            open.DefaultFolderPath,
                            open.PinnedItems)
                    };
                })
                .ToList();

            items.AddRange(openOnlyItems);
            items = DeduplicatePanelOverviewItems(items)
                .OrderBy(p => p.Title)
                .ThenBy(p => p.PanelKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

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
            panel.showParentNavigationItem = data.ShowParentNavigationItem;
            panel.iconViewParentNavigationMode = DesktopPanel.NormalizeIconViewParentNavigationMode(data.IconViewParentNavigationMode, data.ShowParentNavigationItem);
            panel.showFileExtensions = data.ShowFileExtensions;
            panel.viewMode = DesktopPanel.NormalizeViewMode(data.ViewMode);
            panel.showMetadataType = data.ShowMetadataType;
            panel.showMetadataSize = data.ShowMetadataSize;
            panel.showMetadataCreated = data.ShowMetadataCreated;
            panel.showMetadataModified = data.ShowMetadataModified;
            panel.showMetadataDimensions = data.ShowMetadataDimensions;
            panel.showMetadataAuthors = data.ShowMetadataAuthors;
            panel.showMetadataCategories = data.ShowMetadataCategories;
            panel.showMetadataTags = data.ShowMetadataTags;
            panel.showMetadataTitle = data.ShowMetadataTitle;
            panel.metadataOrder = DesktopPanel.NormalizeMetadataOrder(data.MetadataOrder);
            panel.metadataWidths = DesktopPanel.NormalizeMetadataWidths(data.MetadataWidths);
            string resolvedDefaultFolderPath = !string.IsNullOrWhiteSpace(data.DefaultFolderPath)
                ? data.DefaultFolderPath
                : data.FolderPath;
            data.DefaultFolderPath = resolvedDefaultFolderPath ?? "";
            panel.defaultFolderPath = data.DefaultFolderPath;
            ApplyPanelContent(panel, data);
            if (!string.IsNullOrWhiteSpace(data.PanelTitle))
            {
                panel.Title = data.PanelTitle;
                panel.PanelTitle.Text = data.PanelTitle;
            }

            panel.assignedPresetName = string.IsNullOrWhiteSpace(data.PresetName) ? DefaultPresetName : data.PresetName;
            panel.SetExpandOnHover(data.ExpandOnHover);
            panel.openFoldersExternally = data.OpenFoldersExternally;
            panel.openItemsOnSingleClick = data.OpenItemsOnSingleClick;
            panel.ApplyCollapseBehavior(data.CollapseBehavior);
            panel.SetSettingsButtonVisibilityMode(data.SettingsButtonVisibilityMode);
            panel.showCloseButton = data.ShowCloseButton;
            panel.showEmptyRecycleBinButton = data.ShowEmptyRecycleBinButton;
            panel.ApplyMovementMode(string.IsNullOrWhiteSpace(data.MovementMode) ? "titlebar" : data.MovementMode);
            panel.ApplyCloseButtonVisibility();
            panel.UpdateEmptyRecycleBinButtonVisibility();
            panel.SetSearchVisibility(
                data.SearchVisibilityMode,
                DesktopPanel.NormalizeSearchVisibleOnlyExpanded(data.SearchVisibleOnlyExpanded, data.SearchVisibilityMode));
            panel.ApplyHeaderContentAlignment(data.HeaderContentAlignment);

            double storedTop = data.Top;
            double storedBaseTop = (Math.Abs(data.BaseTop) < 0.01 && Math.Abs(storedTop) > 0.01) ? storedTop : data.BaseTop;
            double storedCollapsedTop = (Math.Abs(data.CollapsedTop) < 0.01 && Math.Abs(storedBaseTop) > 0.01) ? storedBaseTop : data.CollapsedTop;

            panel.baseTopPosition = storedBaseTop;
            panel.collapsedTopPosition = storedCollapsedTop;
            panel.Left = data.Left;
            panel.Top = data.IsCollapsed ? storedCollapsedTop : storedTop;
            panel.Width = data.Width;
            double defaultExpandedHeight = panel.Height;
            double storedHeight = data.Height > 0 ? data.Height : panel.Height;
            panel.Height = storedHeight;
            double restoredExpandedHeight = data.ExpandedHeight;
            if (restoredExpandedHeight <= 0)
            {
                restoredExpandedHeight = data.IsCollapsed
                    ? Math.Max(panel.expandedHeight, storedHeight)
                    : storedHeight;
            }

            double collapsedHeight = panel.GetCollapsedHeightForRestore();
            if (!data.IsCollapsed &&
                storedHeight <= collapsedHeight + 0.5 &&
                restoredExpandedHeight <= collapsedHeight + 0.5)
            {
                restoredExpandedHeight = Math.Max(restoredExpandedHeight, defaultExpandedHeight);
            }

            panel.expandedHeight = Math.Max(storedHeight, restoredExpandedHeight);
            panel.IsBottomAnchored = data.IsBottomAnchored;
            panel.SetZoom(data.Zoom);
            var assigned = GetPresetSettings(data.PresetName);
            panel.ApplyAppearance(assigned);
            panel.ForceCollapseState(data.IsCollapsed);

            if (data.Tabs != null && data.Tabs.Count > 0)
            {
                panel.InitializeTabsFromData(data.Tabs, data.ActiveTabIndex);
            }
            else
            {
                panel.InitializeSingleTabFromCurrentState();
            }
        }

        private void ApplyPanelContent(DesktopPanel panel, WindowData data)
        {
            NormalizeWindowData(data);
            panel.PanelId = data.PanelId;
            var kind = ResolvePanelKind(data);
            panel.PanelType = kind;

            if (kind == PanelKind.Folder)
            {
                string folderPath = ResolvePreferredFolderPath(data);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    panel.LoadFolder(folderPath, false);
                }
            }
            else if (kind == PanelKind.RecycleBin)
            {
                panel.LoadRecycleBin(false);
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

        private static bool IsFiniteNumber(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static void RestorePanelBoundsFromSavedState(DesktopPanel panel, WindowData data)
        {
            if (panel == null || data == null)
            {
                return;
            }

            NormalizeWindowData(data);

            if (IsFiniteNumber(data.Left))
            {
                panel.Left = data.Left;
            }

            double storedTop = IsFiniteNumber(data.Top) ? data.Top : panel.Top;
            double storedBaseTop = (Math.Abs(data.BaseTop) < 0.01 && Math.Abs(storedTop) > 0.01) ? storedTop : data.BaseTop;
            double storedCollapsedTop = (Math.Abs(data.CollapsedTop) < 0.01 && Math.Abs(storedBaseTop) > 0.01) ? storedBaseTop : data.CollapsedTop;
            if (!IsFiniteNumber(storedBaseTop))
            {
                storedBaseTop = storedTop;
            }
            if (!IsFiniteNumber(storedCollapsedTop))
            {
                storedCollapsedTop = storedBaseTop;
            }

            if (data.Width > 0 && IsFiniteNumber(data.Width))
            {
                panel.Width = data.Width;
            }

            double storedHeight = (data.Height > 0 && IsFiniteNumber(data.Height))
                ? data.Height
                : (panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height);
            if (storedHeight > 0 && IsFiniteNumber(storedHeight))
            {
                panel.Height = storedHeight;
            }

            panel.baseTopPosition = storedBaseTop;
            panel.collapsedTopPosition = storedCollapsedTop;
            panel.IsBottomAnchored = data.IsBottomAnchored;

            double collapsedHeight = panel.GetCollapsedHeightForRestore();
            double restoredExpandedHeight = data.ExpandedHeight;
            if (restoredExpandedHeight <= 0 || !IsFiniteNumber(restoredExpandedHeight))
            {
                restoredExpandedHeight = data.IsCollapsed
                    ? Math.Max(panel.expandedHeight, storedHeight)
                    : storedHeight;
            }

            if (!data.IsCollapsed &&
                storedHeight <= collapsedHeight + 0.5 &&
                restoredExpandedHeight <= collapsedHeight + 0.5)
            {
                restoredExpandedHeight = Math.Max(restoredExpandedHeight, panel.expandedHeight);
            }

            panel.expandedHeight = Math.Max(storedHeight, restoredExpandedHeight);
            panel.Top = data.IsCollapsed ? storedCollapsedTop : storedTop;
            panel.ForceCollapseState(data.IsCollapsed);
        }

        public static void MarkPanelHidden(DesktopPanel panel)
        {
            if (!IsUserPanel(panel)) return;
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

        private static bool RemoveSavedPanelRepresentationsByPanelKey(string panelKey)
        {
            if (string.IsNullOrWhiteSpace(panelKey))
            {
                return false;
            }

            bool changed = false;
            var remainingWindows = new List<WindowData>(savedWindows.Count);

            foreach (var window in savedWindows)
            {
                if (window == null)
                {
                    changed = true;
                    continue;
                }

                NormalizeWindowData(window);
                if (string.Equals(GetPanelKey(window), panelKey, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    continue;
                }

                if (window.Tabs != null && window.Tabs.Count > 0)
                {
                    int removedCount = window.Tabs.RemoveAll(tab =>
                        string.Equals(tab.PanelId, panelKey, StringComparison.OrdinalIgnoreCase));
                    if (removedCount > 0)
                    {
                        changed = true;
                        if (window.Tabs.Count == 0)
                        {
                            continue;
                        }

                        window.ActiveTabIndex = Math.Max(0, Math.Min(window.ActiveTabIndex, window.Tabs.Count - 1));
                        SyncSavedWindowHiddenState(window);
                    }
                }

                remainingWindows.Add(window);
            }

            if (changed)
            {
                savedWindows = remainingWindows;
            }

            return changed;
        }

        private void PanelTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;

            if (sender is FrameworkElement fe && fe.Tag is PanelOverviewItem item)
            {
                string defaultName = string.IsNullOrWhiteSpace(item.Title) ? "Unbenannt" : item.Title;
                string? name = PromptName(GetString("Loc.PromptPanelName"), defaultName);
                if (string.IsNullOrWhiteSpace(name)) return;

                if (TryGetOrCreateSavedOverviewTarget(item, out var existing, out var tab, out int tabIndex) && existing != null)
                {
                    if (tab != null)
                    {
                        tab.TabName = name;
                    }
                    else
                    {
                        existing.PanelTitle = name;
                    }
                }

                if (item.Panel != null)
                {
                    int hostTabIndex = item.HostTabIndex >= 0 ? item.HostTabIndex : tabIndex;
                    if (hostTabIndex >= 0 && hostTabIndex < item.Panel.Tabs.Count)
                    {
                        item.Panel.RenameTab(hostTabIndex, name);
                    }
                    else
                    {
                        item.Panel.Title = name;
                        item.Panel.PanelTitle.Text = name;
                    }
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
                TryGetOrCreateSavedOverviewTarget(item, out var existing, out var tab, out int tabIndex);
                int hostTabIndex = item.HostTabIndex >= 0 ? item.HostTabIndex : tabIndex;

                if (item.Panel != null)
                {
                    if (hostTabIndex >= 0 && hostTabIndex < item.Panel.Tabs.Count && item.Panel.Tabs.Count > 1)
                    {
                        item.Panel.RemoveTab(hostTabIndex);
                    }
                    else
                    {
                        item.Panel.Close();
                    }
                }

                bool removedSavedRepresentation = RemoveSavedPanelRepresentationsByPanelKey(item.PanelKey);
                if (!removedSavedRepresentation && existing != null)
                {
                    if (tab != null && existing.Tabs != null && tabIndex >= 0 && tabIndex < existing.Tabs.Count)
                    {
                        existing.Tabs.RemoveAt(tabIndex);
                        if (existing.Tabs.Count == 0)
                        {
                            savedWindows.Remove(existing);
                        }
                        else
                        {
                            existing.ActiveTabIndex = Math.Max(0, Math.Min(existing.ActiveTabIndex, existing.Tabs.Count - 1));
                            SyncSavedWindowHiddenState(existing);
                        }
                    }
                    else
                    {
                        savedWindows.Remove(existing);
                    }
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
            bool directBinding = IsDirectPanelOverviewBinding(item);

            if (item.Panel != null)
            {
                if (directBinding)
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
                }

                item.Panel.Hide();
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
                TryGetOrCreateSavedOverviewTarget(item, out var existing, out var tab, out int tabIndex);
                int hostTabIndex = item.HostTabIndex >= 0 ? item.HostTabIndex : tabIndex;

                if (item.IsOpen)
                {
                    if (item.Panel != null && hostTabIndex >= 0 && hostTabIndex < item.Panel.Tabs.Count)
                    {
                        item.Panel.SetTabHidden(hostTabIndex, isHidden: true);
                    }
                    else
                    {
                        item.Panel?.Hide();
                    }

                    if (tab != null)
                    {
                        tab.IsHidden = true;
                    }
                    else if (existing != null)
                    {
                        existing.IsHidden = true;
                    }

                    if (existing != null)
                    {
                        SyncSavedWindowHiddenState(existing);
                    }
                }
                else
                {
                    if (tab != null)
                    {
                        tab.IsHidden = false;
                    }

                    if (existing != null)
                    {
                        existing.IsHidden = false;
                    }

                    if (item.Panel != null)
                    {
                        if (hostTabIndex >= 0 && hostTabIndex < item.Panel.Tabs.Count)
                        {
                            item.Panel.SetTabHidden(hostTabIndex, isHidden: false, activateTabWhenShown: true);
                        }
                        else
                        {
                            RevealPanelOverviewHost(item, switchToTab: false);
                        }
                    }
                    else if (existing != null)
                    {
                        var panel = OpenPanelFromData(existing);
                        int revealIndex = panel.Tabs
                            .Select((candidate, index) => new { candidate, index })
                            .Where(entry => string.Equals(entry.candidate.PanelId, item.PanelKey, StringComparison.OrdinalIgnoreCase))
                            .Select(entry => entry.index)
                            .DefaultIfEmpty(-1)
                            .First();
                        if (revealIndex >= 0)
                        {
                            panel.SwitchToTab(revealIndex);
                        }
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
                TryGetOrCreateSavedOverviewTarget(item, out var existing, out var tab, out int tabIndex);
                int hostTabIndex = item.HostTabIndex >= 0 ? item.HostTabIndex : tabIndex;

                if (panel == null)
                {
                    if (existing != null)
                    {
                        existing.IsHidden = false;
                        if (tab != null)
                        {
                            tab.IsHidden = false;
                        }
                        panel = OpenPanelFromData(existing);
                    }
                }
                else if (!panel.IsVisible)
                {
                    if (hostTabIndex >= 0 && hostTabIndex < panel.Tabs.Count)
                    {
                        panel.SetTabHidden(hostTabIndex, isHidden: false, activateTabWhenShown: true);
                    }
                    else
                    {
                        RevealPanelOverviewHost(item, switchToTab: false);
                    }
                }
                else if (hostTabIndex >= 0 &&
                    hostTabIndex < panel.Tabs.Count &&
                    panel.ActiveTabIndex != hostTabIndex)
                {
                    panel.SetTabHidden(hostTabIndex, isHidden: false, activateTabWhenShown: true);
                }

                if (panel != null && hostTabIndex >= 0 && hostTabIndex < panel.Tabs.Count)
                {
                    panel.SetTabHidden(hostTabIndex, isHidden: false, activateTabWhenShown: true);
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

                TryGetOrCreateSavedOverviewTarget(item, out var existing, out _, out _);
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
            panel.showHiddenItems = true;
            panel.showFileExtensions = false;
            panel.SetExpandOnHover(true);
            panel.SetSettingsButtonVisibilityMode(DesktopPanel.SettingsButtonVisibilityExpandedOnly);
            panel.SetSearchVisibility(DesktopPanel.SearchVisibilityButton, true);
            panel.assignedPresetName = string.IsNullOrWhiteSpace(presetName) ? DefaultPresetName : presetName;
            return panel;
        }

        private static bool IsRecycleBinPanelId(string? panelId)
        {
            return string.Equals(panelId, RecycleBinPanelId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRecycleBinTab(PanelTabData? tab)
        {
            return tab != null &&
                (ResolvePanelKind(tab) == PanelKind.RecycleBin || IsRecycleBinPanelId(tab.PanelId));
        }

        private static bool IsRecycleBinWindow(WindowData? window)
        {
            return window != null &&
                (ResolvePanelKind(window) == PanelKind.RecycleBin || IsRecycleBinPanelId(window.PanelId));
        }

        private (DesktopPanel Panel, int TabIndex)? FindOpenRecycleBinPanel()
        {
            foreach (var panel in Application.Current.Windows.OfType<DesktopPanel>().Where(IsUserPanel))
            {
                int tabIndex = panel.Tabs
                    .Select((tab, index) => new { tab, index })
                    .Where(entry => IsRecycleBinTab(entry.tab))
                    .Select(entry => entry.index)
                    .DefaultIfEmpty(-1)
                    .First();
                if (tabIndex >= 0)
                {
                    return (panel, tabIndex);
                }

                if (ResolvePanelKind(panel) == PanelKind.RecycleBin ||
                    IsRecycleBinPanelId(GetPanelKey(panel)))
                {
                    return (panel, -1);
                }
            }

            return null;
        }

        private (WindowData Window, int TabIndex)? FindSavedRecycleBinPanel()
        {
            foreach (var window in savedWindows)
            {
                int tabIndex = (window.Tabs ?? new List<PanelTabData>())
                    .Select((tab, index) => new { tab, index })
                    .Where(entry => IsRecycleBinTab(entry.tab))
                    .Select(entry => entry.index)
                    .DefaultIfEmpty(-1)
                    .First();
                if (tabIndex >= 0)
                {
                    return (window, tabIndex);
                }

                if (IsRecycleBinWindow(window))
                {
                    return (window, -1);
                }
            }

            return null;
        }

        private void OpenRecycleBinPanel_Click(object sender, RoutedEventArgs e)
        {
            OpenOrRevealRecycleBinPanel();
        }

        private void OpenOrRevealRecycleBinPanel()
        {
            var openMatch = FindOpenRecycleBinPanel();
            if (openMatch != null)
            {
                var (openPanel, tabIndex) = openMatch.Value;
                openPanel.Show();
                openPanel.WindowState = WindowState.Normal;

                if (tabIndex >= 0 && tabIndex < openPanel.Tabs.Count)
                {
                    var tab = openPanel.Tabs[tabIndex];
                    tab.PanelId = RecycleBinPanelId;
                    if (openPanel.GetVisibleTabCount() <= 1)
                    {
                        openPanel.PanelId = RecycleBinPanelId;
                    }

                    if (tab.IsHidden)
                    {
                        openPanel.SetTabHidden(tabIndex, isHidden: false, activateTabWhenShown: true);
                    }
                    else if (openPanel.ActiveTabIndex != tabIndex)
                    {
                        openPanel.SwitchToTab(tabIndex);
                    }
                }
                else
                {
                    openPanel.PanelId = RecycleBinPanelId;
                    openPanel.LoadRecycleBin(saveSettings: false, renamePanelTitle: false);
                }

                SaveSettings();
                NotifyPanelsChanged();
                return;
            }

            var savedMatch = FindSavedRecycleBinPanel();
            if (savedMatch != null)
            {
                var (savedPanel, tabIndex) = savedMatch.Value;
                savedPanel.IsHidden = false;

                if (tabIndex >= 0 && savedPanel.Tabs != null && tabIndex < savedPanel.Tabs.Count)
                {
                    savedPanel.Tabs[tabIndex].PanelId = RecycleBinPanelId;
                    savedPanel.Tabs[tabIndex].IsHidden = false;
                    var restoredPanel = OpenPanelFromData(savedPanel);
                    if (restoredPanel.ActiveTabIndex != tabIndex)
                    {
                        restoredPanel.SwitchToTab(tabIndex);
                    }
                }
                else
                {
                    savedPanel.PanelId = RecycleBinPanelId;
                    savedPanel.PanelType = PanelKind.RecycleBin.ToString();
                    OpenPanelFromData(savedPanel);
                }

                SaveSettings();
                NotifyPanelsChanged();
                return;
            }

            var selectedPreset = (PresetComboTop?.SelectedItem as AppearancePreset)?.Name ?? DefaultPresetName;
            var panel = CreatePanelWithPreset(selectedPreset);
            panel.PanelId = RecycleBinPanelId;
            panel.LoadRecycleBin(saveSettings: false);
            panel.Show();
            SaveSettings();
            NotifyPanelsChanged();
        }

        /// <summary>
        /// Creates a new panel from a detached tab, inheriting appearance from the source panel.
        /// </summary>
        public DesktopPanel? CreateDetachedTabPanel(PanelTabData tab, DesktopPanel sourcePanel, System.Windows.Point screenPos)
        {
            var panel = new DesktopPanel();
            panel.PanelId = string.IsNullOrWhiteSpace(tab.PanelId)
                ? $"panel:{Guid.NewGuid():N}"
                : tab.PanelId;

            // Inherit panel-wide settings from source
            panel.assignedPresetName = sourcePanel.assignedPresetName;
            panel.ApplyAppearance(GetPresetSettings(panel.assignedPresetName));
            panel.SetExpandOnHover(sourcePanel.expandOnHover);
            panel.ApplyCollapseBehavior(sourcePanel.collapseBehavior);
            panel.SetSettingsButtonVisibilityMode(sourcePanel.settingsButtonVisibilityMode);
            panel.showCloseButton = sourcePanel.showCloseButton;
            panel.showEmptyRecycleBinButton = sourcePanel.showEmptyRecycleBinButton;
            panel.ApplyCloseButtonVisibility();
            panel.UpdateEmptyRecycleBinButtonVisibility();
            panel.ApplyMovementMode(sourcePanel.movementMode);
            panel.SetSearchVisibility(sourcePanel.searchVisibilityMode, sourcePanel.searchVisibleOnlyExpanded);
            panel.ApplyHeaderContentAlignment(sourcePanel.headerContentAlignment);

            // Position at mouse
            panel.Left = screenPos.X - 100;
            panel.Top = screenPos.Y - 23;
            panel.Width = sourcePanel.Width;
            panel.Height = sourcePanel.Height;
            panel.expandedHeight = sourcePanel.expandedHeight;
            panel.baseTopPosition = panel.Top;
            panel.collapsedTopPosition = panel.Top;

            // Set title from tab name
            panel.PanelTitle.Text = tab.TabName;
            panel.Title = tab.TabName;

            // Initialize with a single tab
            panel.InitializeTabsFromData(
                new List<PanelTabData> { tab },
                activeIndex: 0);

            // Load the tab content
            if (Enum.TryParse<PanelKind>(tab.PanelType, true, out var kind))
            {
                if (kind == PanelKind.Folder && !string.IsNullOrWhiteSpace(tab.FolderPath))
                {
                    panel.LoadFolder(tab.FolderPath, saveSettings: false);
                }
                else if (kind == PanelKind.RecycleBin)
                {
                    panel.PanelId = RecycleBinPanelId;
                    panel.LoadRecycleBin(saveSettings: false);
                }
                else if (kind == PanelKind.List && tab.PinnedItems?.Count > 0)
                {
                    panel.LoadList(tab.PinnedItems, saveSettings: false);
                }
            }

            return panel;
        }

        private void ShowAllPanels_Click(object sender, RoutedEventArgs e)
        {
            ShowAllUserPanels();
        }

        private void ShowAllUserPanels()
        {
            savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
            var openPanelList = Application.Current.Windows
                .OfType<DesktopPanel>()
                .Where(IsUserPanel)
                .ToList();
            var openPanels = CreateOpenPanelMap(openPanelList);

            foreach (var saved in savedWindows)
            {
                NormalizeWindowData(saved);
                var kind = ResolvePanelKind(saved);
                if (kind == PanelKind.None) continue;
                if (saved.Tabs != null && saved.Tabs.Count > 0 && saved.Tabs.All(tab => tab.IsHidden))
                {
                    continue;
                }

                var key = GetPanelKey(saved);

                if (openPanels.TryGetValue(key, out var existingPanel))
                {
                    saved.IsHidden = false;
                    if (existingPanel.HasVisibleTabs())
                    {
                        existingPanel.Show();
                        existingPanel.WindowState = WindowState.Normal;
                        RestorePanelBoundsFromSavedState(existingPanel, saved);
                    }
                    continue;
                }

                if (kind == PanelKind.Folder &&
                    string.IsNullOrWhiteSpace(saved.FolderPath))
                {
                    continue;
                }

                saved.IsHidden = false;
                OpenPanelFromData(saved);
            }

            SaveSettings();
            NotifyPanelsChanged();
        }

        private void HideAllPanels_Click(object sender, RoutedEventArgs e)
        {
            HideAllUserPanels();
        }

        private void HideAllUserPanels()
        {
            savedWindows = CreateWindowDataMap(savedWindows, rewriteDuplicates: true).Values.ToList();
            var openPanels = Application.Current.Windows
                .OfType<DesktopPanel>()
                .Where(IsUserPanel)
                .ToList();
            foreach (var panel in openPanels)
            {
                var snapshot = BuildWindowDataFromPanel(panel);
                snapshot.IsHidden = true;
                string panelKey = GetPanelKey(snapshot);
                var existing = FindSavedWindow(panelKey);
                if (existing != null)
                {
                    CopyWindowData(snapshot, existing);
                    existing.IsHidden = true;
                }
                else
                {
                    savedWindows.Add(snapshot);
                }

                panel.Hide();
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
