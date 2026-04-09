using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using WinForms = System.Windows.Forms;

namespace DesktopPlus
{
    public partial class DesktopPanel
    {
        private readonly List<PanelTabData> _tabs = new List<PanelTabData>();
        private int _activeTabIndex = 0;

        private static string CreateLogicalTabPanelId()
        {
            return $"panel:{Guid.NewGuid():N}";
        }

        // Tab drag state
        private const string TabDragFormat = "DesktopPlus_TabDrag";
        private const double TabDragThreshold = 8.0;
        private const double TabDragDetachYThreshold = 40.0;
        private bool _isTabDragPending;
        private Point _tabDragStartPoint;
        private int _tabDragIndex = -1;
        private const double TabDragPreviewAnimationMs = 190.0;
        private int _animateNewTabIndex = -1;
        private bool _isTabReorderDragging;
        private Border? _tabReorderSource;
        private int _tabReorderSourceIndex = -1;

        // Static shared payload so all panels can access the drag data
        // (WPF DragDrop can lose custom non-serializable data across windows)
        private static TabDragPayload? _activeTabDragPayload;
        private static Window? _activeTabDragAdorner;

        public IReadOnlyList<PanelTabData> Tabs => _tabs;
        public int ActiveTabIndex => _activeTabIndex;
        public PanelTabData? ActiveTab =>
            _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
                ? _tabs[_activeTabIndex]
                : null;

        private static void EnsureTabIdentity(PanelTabData tab, string? fallbackPanelId = null)
        {
            if (tab == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(tab.TabId))
            {
                tab.TabId = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(tab.PanelId))
            {
                tab.PanelId = !string.IsNullOrWhiteSpace(fallbackPanelId)
                    ? fallbackPanelId
                    : $"paneltab:{tab.TabId}";
            }

            tab.PinnedItems ??= new List<string>();
            tab.ViewMode = NormalizeViewMode(tab.ViewMode);
            tab.MetadataOrder = NormalizeMetadataOrder(tab.MetadataOrder);
            tab.MetadataWidths = NormalizeMetadataWidths(tab.MetadataWidths);
        }

        public bool IsTabHidden(int index)
        {
            return index < 0 || index >= _tabs.Count || _tabs[index].IsHidden;
        }

        public int GetVisibleTabCount()
        {
            return _tabs.Count(tab => !tab.IsHidden);
        }

        public bool HasVisibleTabs()
        {
            return GetVisibleTabCount() > 0;
        }

        private List<int> GetVisibleTabIndices()
        {
            var indices = new List<int>();
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (!_tabs[i].IsHidden)
                {
                    indices.Add(i);
                }
            }

            return indices;
        }

        private int FindFirstVisibleTabIndex()
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (!_tabs[i].IsHidden)
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindNextVisibleTabIndex(int preferredIndex)
        {
            if (_tabs.Count == 0)
            {
                return -1;
            }

            if (preferredIndex >= 0 && preferredIndex < _tabs.Count && !_tabs[preferredIndex].IsHidden)
            {
                return preferredIndex;
            }

            for (int i = preferredIndex + 1; i < _tabs.Count; i++)
            {
                if (!_tabs[i].IsHidden)
                {
                    return i;
                }
            }

            for (int i = preferredIndex - 1; i >= 0; i--)
            {
                if (!_tabs[i].IsHidden)
                {
                    return i;
                }
            }

            return FindFirstVisibleTabIndex();
        }

        private bool EnsureActiveTabVisible()
        {
            int visibleIndex = FindNextVisibleTabIndex(_activeTabIndex);
            if (visibleIndex < 0)
            {
                return false;
            }

            bool changed = _activeTabIndex != visibleIndex;
            _activeTabIndex = visibleIndex;
            return changed;
        }

        private void RefreshTabPresentation(bool reloadActiveState = false, bool persist = true)
        {
            bool activeTabChanged = EnsureActiveTabVisible();
            if (!HasVisibleTabs())
            {
                RebuildTabBar();

                if (IsVisible)
                {
                    Hide();
                }

                if (persist)
                {
                    MainWindow.SaveSettings();
                }
                return;
            }

            if (reloadActiveState || activeTabChanged)
            {
                LoadTabState(_tabs[_activeTabIndex]);
            }

            SyncSingleTabHeaderTitle();
            RebuildTabBar();
            if (persist)
            {
                MainWindow.SaveSettings();
            }
        }

        public bool HasRecycleBinTab()
        {
            return _tabs.Any(tab =>
                Enum.TryParse<PanelKind>(tab.PanelType, true, out var kind) &&
                kind == PanelKind.RecycleBin);
        }

        public void SaveActiveTabState()
        {
            if (ActiveTab == null) return;
            var tab = ActiveTab;
            EnsureTabIdentity(tab, _tabs.Count == 1 ? (string.IsNullOrWhiteSpace(PanelId) ? CreateLogicalTabPanelId() : PanelId) : null);
            if (string.IsNullOrWhiteSpace(tab.PanelId) && _tabs.Count == 1)
            {
                tab.PanelId = string.IsNullOrWhiteSpace(PanelId) ? CreateLogicalTabPanelId() : PanelId;
            }
            tab.PanelType = PanelType.ToString();
            tab.FolderPath = currentFolderPath ?? "";
            tab.DefaultFolderPath = defaultFolderPath ?? "";
            tab.ShowHidden = showHiddenItems;
            tab.ShowParentNavigationItem = showParentNavigationItem;
            tab.IconViewParentNavigationMode = NormalizeIconViewParentNavigationMode(iconViewParentNavigationMode, showParentNavigationItem);
            tab.ShowFileExtensions = showFileExtensions;
            tab.OpenFoldersExternally = openFoldersExternally;
            tab.OpenItemsOnSingleClick = openItemsOnSingleClick;
            tab.ViewMode = viewMode;
            tab.ShowMetadataType = showMetadataType;
            tab.ShowMetadataSize = showMetadataSize;
            tab.ShowMetadataCreated = showMetadataCreated;
            tab.ShowMetadataModified = showMetadataModified;
            tab.ShowMetadataDimensions = showMetadataDimensions;
            tab.ShowMetadataAuthors = showMetadataAuthors;
            tab.ShowMetadataCategories = showMetadataCategories;
            tab.ShowMetadataTags = showMetadataTags;
            tab.ShowMetadataTitle = showMetadataTitle;
            tab.MetadataOrder = NormalizeMetadataOrder(metadataOrder);
            tab.MetadataWidths = NormalizeMetadataWidths(metadataWidths);
            tab.PinnedItems = PinnedItems.ToList();
        }

        private void LoadTabState(PanelTabData tab)
        {
            EnsureTabIdentity(tab, _tabs.Count == 1 ? PanelId : null);

            if (GetVisibleTabCount() <= 1 && !string.IsNullOrWhiteSpace(tab.PanelId))
            {
                PanelId = tab.PanelId;
            }

            showHiddenItems = tab.ShowHidden;
            showParentNavigationItem = tab.ShowParentNavigationItem;
            iconViewParentNavigationMode = NormalizeIconViewParentNavigationMode(tab.IconViewParentNavigationMode, tab.ShowParentNavigationItem);
            showFileExtensions = tab.ShowFileExtensions;
            openFoldersExternally = tab.OpenFoldersExternally;
            openItemsOnSingleClick = tab.OpenItemsOnSingleClick;
            viewMode = NormalizeViewMode(tab.ViewMode);
            showMetadataType = tab.ShowMetadataType;
            showMetadataSize = tab.ShowMetadataSize;
            showMetadataCreated = tab.ShowMetadataCreated;
            showMetadataModified = tab.ShowMetadataModified;
            showMetadataDimensions = tab.ShowMetadataDimensions;
            showMetadataAuthors = tab.ShowMetadataAuthors;
            showMetadataCategories = tab.ShowMetadataCategories;
            showMetadataTags = tab.ShowMetadataTags;
            showMetadataTitle = tab.ShowMetadataTitle;
            metadataOrder = NormalizeMetadataOrder(tab.MetadataOrder);
            metadataWidths = NormalizeMetadataWidths(tab.MetadataWidths);
            defaultFolderPath = tab.DefaultFolderPath ?? "";

            if (Enum.TryParse<PanelKind>(tab.PanelType, true, out var kind))
            {
                if (kind == PanelKind.Folder && !string.IsNullOrWhiteSpace(tab.FolderPath))
                {
                    LoadFolder(tab.FolderPath, saveSettings: false);
                }
                else if (kind == PanelKind.RecycleBin)
                {
                    LoadRecycleBin(saveSettings: false);
                }
                else if (kind == PanelKind.List && tab.PinnedItems?.Count > 0)
                {
                    LoadList(tab.PinnedItems, saveSettings: false);
                }
                else
                {
                    ClearPanelItems();
                }
            }
            else
            {
                ClearPanelItems();
            }
        }

        public void SwitchToTab(int index)
        {
            if (index < 0 || index >= _tabs.Count || index == _activeTabIndex || _tabs[index].IsHidden) return;
            SaveActiveTabState();
            _activeTabIndex = index;
            LoadTabState(_tabs[index]);
            RebuildTabBar();
            MainWindow.SaveSettings();
        }

        public int FindTabIndexByName(string tabName)
        {
            if (string.IsNullOrWhiteSpace(tabName))
            {
                return -1;
            }

            string expected = tabName.Trim();
            for (int i = 0; i < _tabs.Count; i++)
            {
                string candidate = _tabs[i]?.TabName ?? "";
                if (string.Equals(candidate.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        public bool AppendItemsToTab(int tabIndex, IEnumerable<string> filePaths, bool animateEntries)
        {
            if (tabIndex < 0 || tabIndex >= _tabs.Count || filePaths == null)
            {
                return false;
            }

            var paths = filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0)
            {
                return false;
            }

            if (tabIndex == _activeTabIndex)
            {
                int before = PinnedItems.Count;
                AppendItemsToList(paths, animateEntries);
                SaveActiveTabState();
                return PinnedItems.Count > before;
            }

            var tab = _tabs[tabIndex];
            tab.PinnedItems ??= new List<string>();

            bool addedAny = false;
            foreach (string path in paths)
            {
                if (tab.PinnedItems.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                tab.PinnedItems.Add(path);
                addedAny = true;
            }

            if (!addedAny)
            {
                return false;
            }

            tab.PanelType = PanelKind.List.ToString();
            tab.FolderPath = "";
            tab.DefaultFolderPath = "";
            return true;
        }

        public PanelTabData AddTab(string? folderPath = null, bool switchTo = true)
        {
            SaveActiveTabState();

            string tabName = !string.IsNullOrWhiteSpace(folderPath)
                ? Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? MainWindow.GetString("Loc.TabNewTab")
                : MainWindow.GetString("Loc.TabNewTab");

            var tab = new PanelTabData
            {
                PanelId = CreateLogicalTabPanelId(),
                TabId = Guid.NewGuid().ToString("N"),
                TabName = tabName,
                IsHidden = false,
                PanelType = !string.IsNullOrWhiteSpace(folderPath) ? PanelKind.Folder.ToString() : PanelKind.None.ToString(),
                FolderPath = folderPath ?? "",
                DefaultFolderPath = folderPath ?? "",
                ShowHidden = showHiddenItems,
                ShowParentNavigationItem = showParentNavigationItem,
                IconViewParentNavigationMode = NormalizeIconViewParentNavigationMode(iconViewParentNavigationMode, showParentNavigationItem),
                ShowFileExtensions = showFileExtensions,
                OpenFoldersExternally = openFoldersExternally,
                OpenItemsOnSingleClick = openItemsOnSingleClick,
                ViewMode = viewMode,
                ShowMetadataType = showMetadataType,
                ShowMetadataSize = showMetadataSize,
                ShowMetadataCreated = showMetadataCreated,
                ShowMetadataModified = showMetadataModified,
                ShowMetadataDimensions = showMetadataDimensions,
                ShowMetadataAuthors = showMetadataAuthors,
                ShowMetadataCategories = showMetadataCategories,
                ShowMetadataTags = showMetadataTags,
                ShowMetadataTitle = showMetadataTitle,
                MetadataOrder = NormalizeMetadataOrder(metadataOrder),
                MetadataWidths = NormalizeMetadataWidths(metadataWidths),
            };

            _tabs.Add(tab);

            if (switchTo)
            {
                _activeTabIndex = _tabs.Count - 1;
                LoadTabState(tab);
            }

            _animateNewTabIndex = _tabs.Count - 1;
            RebuildTabBar();
            ScheduleBackgroundFolderIndexWarmup();
            MainWindow.SaveSettings();
            return tab;
        }

        /// <summary>
        /// Insert a tab (from another panel) at the given position.
        /// </summary>
        public void InsertTab(PanelTabData tab, int insertAt = -1, bool switchTo = true)
        {
            SaveActiveTabState();
            EnsureTabIdentity(tab);
            tab.IsHidden = false;
            if (insertAt < 0 || insertAt > _tabs.Count) insertAt = _tabs.Count;
            _tabs.Insert(insertAt, tab);

            if (switchTo)
            {
                _activeTabIndex = insertAt;
                LoadTabState(tab);
            }

            _animateNewTabIndex = insertAt;
            RebuildTabBar();
            ScheduleBackgroundFolderIndexWarmup();
            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
        }

        /// <summary>
        /// Detach a tab from this panel and return it. Does NOT create the new panel.
        /// Returns null if this is the last tab.
        /// </summary>
        public PanelTabData? DetachTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return null;
            if (_tabs.Count <= 1) return null;

            // Make sure the tab data is current if it's the active one
            if (index == _activeTabIndex)
            {
                SaveActiveTabState();
            }

            var tab = _tabs[index];
            bool wasActive = index == _activeTabIndex;
            _tabs.RemoveAt(index);

            if (GetVisibleTabCount() == 1)
            {
                int singleVisibleIndex = FindFirstVisibleTabIndex();
                if (singleVisibleIndex >= 0 && !string.IsNullOrWhiteSpace(_tabs[singleVisibleIndex].PanelId))
                {
                    PanelId = _tabs[singleVisibleIndex].PanelId;
                }
            }

            if (_activeTabIndex >= _tabs.Count)
                _activeTabIndex = _tabs.Count - 1;
            else if (index < _activeTabIndex)
                _activeTabIndex--;

            bool reloadActiveState = wasActive ||
                _activeTabIndex < 0 ||
                _activeTabIndex >= _tabs.Count ||
                _tabs[_activeTabIndex].IsHidden;
            RefreshTabPresentation(reloadActiveState);
            MainWindow.NotifyPanelsChanged();
            return tab;
        }

        public void RemoveTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            if (_tabs.Count <= 1) return;

            bool wasActive = index == _activeTabIndex;
            _tabs.RemoveAt(index);

            if (GetVisibleTabCount() == 1)
            {
                int singleVisibleIndex = FindFirstVisibleTabIndex();
                if (singleVisibleIndex >= 0 && !string.IsNullOrWhiteSpace(_tabs[singleVisibleIndex].PanelId))
                {
                    PanelId = _tabs[singleVisibleIndex].PanelId;
                }
            }

            if (_activeTabIndex >= _tabs.Count)
                _activeTabIndex = _tabs.Count - 1;
            else if (index < _activeTabIndex)
                _activeTabIndex--;

            bool reloadActiveState = wasActive ||
                _activeTabIndex < 0 ||
                _activeTabIndex >= _tabs.Count ||
                _tabs[_activeTabIndex].IsHidden;
            RefreshTabPresentation(reloadActiveState);
            MainWindow.NotifyPanelsChanged();
        }

        public void RenameTab(int index, string newName)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _tabs[index].TabName = newName;
            SyncSingleTabHeaderTitle();
            RebuildTabBar();
            MainWindow.SaveSettings();
            MainWindow.NotifyPanelsChanged();
        }

        public bool SetTabHidden(int index, bool isHidden, bool activateTabWhenShown = false)
        {
            if (index < 0 || index >= _tabs.Count)
            {
                return false;
            }

            var tab = _tabs[index];
            EnsureTabIdentity(tab, index == 0 ? PanelId : null);
            if (tab.IsHidden == isHidden)
            {
                if (!isHidden && activateTabWhenShown)
                {
                    SwitchToTab(index);
                }
                return false;
            }

            bool wasActive = index == _activeTabIndex;
            int visibleBeforeChange = GetVisibleTabCount();
            if (wasActive)
            {
                SaveActiveTabState();
            }

            tab.IsHidden = isHidden;

            if (!isHidden)
            {
                bool shouldActivate = activateTabWhenShown || visibleBeforeChange == 0 || IsTabHidden(_activeTabIndex);
                if (shouldActivate)
                {
                    _activeTabIndex = index;
                }

                RefreshTabPresentation(reloadActiveState: shouldActivate);
                if (!IsVisible)
                {
                    Show();
                    WindowState = WindowState.Normal;
                }
                MainWindow.NotifyPanelsChanged();
                return true;
            }

            if (!HasVisibleTabs())
            {
                RebuildTabBar();
                Hide();
                MainWindow.SaveSettings();
                MainWindow.NotifyPanelsChanged();
                return true;
            }

            if (wasActive)
            {
                RefreshTabPresentation(reloadActiveState: true);
            }
            else
            {
                SyncSingleTabHeaderTitle();
                RebuildTabBar();
                MainWindow.SaveSettings();
            }

            MainWindow.NotifyPanelsChanged();
            return true;
        }

        public void InitializeTabsFromData(List<PanelTabData> tabs, int activeIndex)
        {
            _tabs.Clear();
            for (int i = 0; i < tabs.Count; i++)
            {
                var tab = tabs[i];
                EnsureTabIdentity(tab, i == 0 ? PanelId : null);
            }
            _tabs.AddRange(tabs);
            if (GetVisibleTabCount() == 1)
            {
                int visibleIndex = FindFirstVisibleTabIndex();
                if (visibleIndex >= 0 && !string.IsNullOrWhiteSpace(_tabs[visibleIndex].PanelId))
                {
                    PanelId = _tabs[visibleIndex].PanelId;
                }
            }
            _activeTabIndex = Math.Max(0, Math.Min(activeIndex, _tabs.Count - 1));
            RefreshTabPresentation(reloadActiveState: true, persist: false);
            ScheduleBackgroundFolderIndexWarmup();
        }

        public void InitializeSingleTabFromCurrentState()
        {
            if (_tabs.Count > 0) return;
            _tabs.Add(new PanelTabData
            {
                PanelId = string.IsNullOrWhiteSpace(PanelId) ? CreateLogicalTabPanelId() : PanelId,
                TabId = Guid.NewGuid().ToString("N"),
                TabName = PanelTitle?.Text ?? MainWindow.GetString("Loc.PanelDefaultTitle"),
                IsHidden = false,
                PanelType = PanelType.ToString(),
                FolderPath = currentFolderPath ?? "",
                DefaultFolderPath = defaultFolderPath ?? "",
                ShowHidden = showHiddenItems,
                ShowParentNavigationItem = showParentNavigationItem,
                IconViewParentNavigationMode = NormalizeIconViewParentNavigationMode(iconViewParentNavigationMode, showParentNavigationItem),
                ShowFileExtensions = showFileExtensions,
                OpenFoldersExternally = openFoldersExternally,
                OpenItemsOnSingleClick = openItemsOnSingleClick,
                ViewMode = viewMode,
                ShowMetadataType = showMetadataType,
                ShowMetadataSize = showMetadataSize,
                ShowMetadataCreated = showMetadataCreated,
                ShowMetadataModified = showMetadataModified,
                ShowMetadataDimensions = showMetadataDimensions,
                ShowMetadataAuthors = showMetadataAuthors,
                ShowMetadataCategories = showMetadataCategories,
                ShowMetadataTags = showMetadataTags,
                ShowMetadataTitle = showMetadataTitle,
                MetadataOrder = NormalizeMetadataOrder(metadataOrder),
                MetadataWidths = NormalizeMetadataWidths(metadataWidths),
                PinnedItems = PinnedItems.ToList()
            });
            _activeTabIndex = 0;
            RefreshTabPresentation(reloadActiveState: false, persist: false);
        }

        private void RebuildTabBar(bool? collapsedOverride = null, bool animateSearch = false)
        {
            if (TabBarPanel == null || TabBarContainer == null) return;

            TabBarPanel.Children.Clear();
            bool isCollapsedVisual = collapsedOverride ?? _isCollapsedVisualState;
            int visibleTabCount = GetVisibleTabCount();

            if (visibleTabCount <= 1)
            {
                TabBarContainer.Visibility = Visibility.Collapsed;
                if (PanelTitle != null)
                {
                    PanelTitle.Visibility = Visibility.Visible;
                    if (visibleTabCount == 1)
                    {
                        SyncSingleTabHeaderTitle();
                    }
                }
                UpdateHeaderBottomBorderForCurrentState(collapsed: isCollapsedVisual);
                UpdateHeaderBackgroundCutout();
                ApplySearchVisibility(animate: animateSearch);
                ApplyHeaderContentAlignment(headerContentAlignment);
                return;
            }

            TabBarContainer.Visibility = Visibility.Visible;
            if (PanelTitle != null)
                PanelTitle.Visibility = Visibility.Collapsed;
            // Keep border behavior in sync with collapsed/expanded visual state.
            UpdateHeaderBottomBorderForCurrentState(collapsed: isCollapsedVisual);
            ApplySearchVisibility(animate: animateSearch);

            int animateIndex = _animateNewTabIndex;
            _animateNewTabIndex = -1;
            bool showActiveSelection = !isCollapsedVisual;

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].IsHidden)
                {
                    continue;
                }

                var tabItem = CreateTabBarItem(
                    _tabs[i],
                    i,
                    showActiveSelection && i == _activeTabIndex,
                    showActiveSelection);
                TabBarPanel.Children.Add(tabItem);

                if (i == animateIndex)
                {
                    AnimateNewTab(tabItem);
                }
            }

            UpdateHeaderBackgroundCutout();
            ApplyHeaderContentAlignment(headerContentAlignment);
        }

        private Border CreateTabBarItem(PanelTabData tab, int index, bool isActive, bool allowActiveSelection)
        {
            var appearance = _currentAppearance ?? MainWindow.Appearance ?? new AppearanceSettings();

            static System.Windows.Media.Color Blend(System.Windows.Media.Color from, System.Windows.Media.Color to, double amount)
            {
                double t = Math.Max(0.0, Math.Min(1.0, amount));
                return System.Windows.Media.Color.FromRgb(
                    (byte)Math.Round(from.R + ((to.R - from.R) * t)),
                    (byte)Math.Round(from.G + ((to.G - from.G) * t)),
                    (byte)Math.Round(from.B + ((to.B - from.B) * t)));
            }

            static SolidColorBrush CreateBrush(System.Windows.Media.Color color, byte alpha)
            {
                return new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B));
            }

            var contentBrush = ContentFrame?.Background?.CloneCurrentValue() ?? MainWindow.BuildPanelContentBrush(appearance);

            var white = System.Windows.Media.Color.FromRgb(255, 255, 255);
            var accentColor = MainWindow.ParseColorOrFallback(appearance.AccentColor, System.Windows.Media.Color.FromRgb(90, 200, 250));
            var mutedColor = MainWindow.ParseColorOrFallback(appearance.MutedTextColor, System.Windows.Media.Color.FromRgb(167, 176, 192));
            var activeBorderBaseColor = MainWindow.ResolvePanelTabActiveColor(appearance);
            var hoverPillBaseColor = MainWindow.ResolvePanelTabHoverColor(appearance);
            var separatorBaseColor = MainWindow.ResolvePanelTabInactiveColor(appearance);
            var chromeActiveColor = contentBrush is SolidColorBrush solidContentBrush
                ? System.Windows.Media.Color.FromRgb(solidContentBrush.Color.R, solidContentBrush.Color.G, solidContentBrush.Color.B)
                : activeBorderBaseColor;
            // Active tab: use the same brush as the content body so both surfaces merge cleanly.
            var chromeActiveBorderColor = Blend(chromeActiveColor, white, 0.12);
            var hoverPillColor = Blend(hoverPillBaseColor, white, 0.08);
            var separatorColor = Blend(separatorBaseColor, white, 0.10);
            var inactiveTitleColor = Blend(mutedColor, accentColor, 0.38);

            var activeBackgroundBrush = contentBrush;
            var activeBorderBrush = CreateBrush(chromeActiveBorderColor, 160);
            var hoverPillBrush = CreateBrush(hoverPillColor, 188);
            var separatorBrush = CreateBrush(separatorColor, 132);
            var activeTextBrush = CreateBrush(accentColor, 255);
            var inactiveTextBrush = CreateBrush(inactiveTitleColor, 228);

            var tabNameBlock = new TextBlock
            {
                Text = tab.TabName,
                FontSize = Math.Min(18, Math.Max(12, appearance.TitleFontSize - 1)),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 156,
                Foreground = isActive ? activeTextBrush : inactiveTextBrush,
                Opacity = 0.95,
                IsHitTestVisible = false,
            };

            int switchIndex = index;

            var separatorRight = new Border
            {
                Width = 1,
                Height = 14,
                Background = separatorBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Opacity = ShouldShowSeparator(switchIndex, isActive) ? 1.0 : 0.0
            };

            // Active tab: Chrome-style shape drawn via Path on a Canvas
            var tabBleed = new System.Windows.Shapes.Path
            {
                Fill = activeBackgroundBrush,
                Stroke = activeBackgroundBrush,
                StrokeThickness = 1.35,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                SnapsToDevicePixels = true,
                Stretch = Stretch.None,
                Visibility = isActive ? Visibility.Visible : Visibility.Collapsed,
            };
            var tabFill = new System.Windows.Shapes.Path
            {
                Fill = activeBackgroundBrush,
                StrokeThickness = 0,
                SnapsToDevicePixels = true,
                Stretch = Stretch.None,
                Visibility = isActive ? Visibility.Visible : Visibility.Collapsed,
            };
            var tabOutline = new System.Windows.Shapes.Path
            {
                Fill = System.Windows.Media.Brushes.Transparent,
                Stroke = activeBorderBrush,
                StrokeThickness = 1,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat,
                SnapsToDevicePixels = true,
                Stretch = Stretch.None,
                Visibility = isActive ? Visibility.Visible : Visibility.Collapsed,
            };
            var tabCanvas = new Canvas { SnapsToDevicePixels = true, ClipToBounds = false };
            tabCanvas.Children.Add(tabBleed);
            tabCanvas.Children.Add(tabFill);
            tabCanvas.Children.Add(tabOutline);

            // Inactive hover pill: simple rounded border behind text
            var hoverPill = new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(5, 9, 5, 9),
                IsHitTestVisible = false,
            };

            var tabGrid = new Grid { SnapsToDevicePixels = true };
            tabGrid.Children.Add(tabCanvas);     // active shape (behind everything)
            tabGrid.Children.Add(hoverPill);      // hover pill (behind text)
            tabGrid.Children.Add(tabNameBlock);   // text on top
            tabGrid.Children.Add(separatorRight); // separator on right edge

            tabNameBlock.Margin = new Thickness(16, 0, 16, 0);

            var border = new Border
            {
                MinWidth = 100,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, -1),
                Background = System.Windows.Media.Brushes.Transparent,
                Child = tabGrid,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
                SnapsToDevicePixels = true,
                RenderTransform = new TranslateTransform()
            };

            void UpdateActiveTabGeometry(double width, double height)
            {
                var tabShape = BuildChromeTabShape(width, height);
                tabBleed.Data = tabShape;
                tabFill.Data = tabShape;
                tabOutline.Data = BuildChromeTabOutline(width, height);
            }

            border.Loaded += (_, __) =>
            {
                if (isActive || (allowActiveSelection && (int)border.Tag == _activeTabIndex))
                {
                    UpdateActiveTabGeometry(border.ActualWidth, border.ActualHeight);
                    UpdateHeaderBackgroundCutout();
                }
            };
            border.SizeChanged += (_, sizeArgs) =>
            {
                if (allowActiveSelection && (int)border.Tag == _activeTabIndex)
                {
                    UpdateActiveTabGeometry(sizeArgs.NewSize.Width, sizeArgs.NewSize.Height);
                    UpdateHeaderBackgroundCutout();
                }
            };

            bool isHovering = false;

            void ApplyVisualState(bool activeState)
            {
                if (activeState)
                {
                    tabBleed.Visibility = Visibility.Visible;
                    tabFill.Visibility = Visibility.Visible;
                    tabOutline.Visibility = Visibility.Visible;
                    UpdateActiveTabGeometry(border.ActualWidth, border.ActualHeight);
                    hoverPill.Background = System.Windows.Media.Brushes.Transparent;
                    separatorRight.Opacity = 0;
                    tabNameBlock.Foreground = activeTextBrush;
                    return;
                }

                tabBleed.Visibility = Visibility.Collapsed;
                tabFill.Visibility = Visibility.Collapsed;
                tabOutline.Visibility = Visibility.Collapsed;
                hoverPill.Background = isHovering ? hoverPillBrush : System.Windows.Media.Brushes.Transparent;
                separatorRight.Opacity = (!isHovering && ShouldShowSeparator(switchIndex, false)) ? 1.0 : 0.0;
                tabNameBlock.Foreground = isHovering ? activeTextBrush : inactiveTextBrush;
            }

            ApplyVisualState(isActive);

            // Mouse down: start potential drag or tab switch without rebuilding the tab strip yet.
            border.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
                {
                    return;
                }

                _isTabDragPending = true;
                _tabDragStartPoint = e.GetPosition(null);
                _tabDragIndex = switchIndex;
                border.CaptureMouse();
                e.Handled = true;
            };

            border.MouseLeftButtonUp += (_, e) =>
            {
                bool shouldActivateTab = _isTabDragPending && _tabDragIndex == switchIndex;
                _isTabDragPending = false;
                _tabDragIndex = -1;

                if (_isTabReorderDragging && ReferenceEquals(_tabReorderSource, border))
                {
                    FinalizeTabReorderDrag();
                    e.Handled = true;
                    return;
                }

                if (border.IsMouseCaptured)
                {
                    border.ReleaseMouseCapture();
                }

                if (shouldActivateTab)
                {
                    bool shouldExpandPanel = !isContentVisible && !_isCollapseAnimationRunning;
                    if (switchIndex != _activeTabIndex)
                    {
                        SwitchToTab(switchIndex);
                    }

                    if (shouldExpandPanel)
                    {
                        ToggleCollapseAnimated();
                    }
                }

                e.Handled = true;
            };

            border.MouseMove += (_, e) =>
            {
                if (_isTabReorderDragging && ReferenceEquals(_tabReorderSource, border))
                {
                    UpdateTabReorderDrag(e);
                    e.Handled = true;
                    return;
                }

                if (!_isTabDragPending || _tabDragIndex != switchIndex) return;
                if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                {
                    _isTabDragPending = false;
                    return;
                }

                var currentPos = e.GetPosition(null);
                var diff = currentPos - _tabDragStartPoint;
                if (Math.Abs(diff.X) > TabDragThreshold || Math.Abs(diff.Y) > TabDragThreshold)
                {
                    _isTabDragPending = false;
                    BeginTabReorderDrag(switchIndex, border);
                }
            };

            border.LostMouseCapture += (_, _) =>
            {
                if (_isTabReorderDragging && ReferenceEquals(_tabReorderSource, border))
                {
                    FinalizeTabReorderDrag();
                    return;
                }

                if (_tabDragIndex == switchIndex)
                {
                    _isTabDragPending = false;
                    _tabDragIndex = -1;
                }
            };

            // Hover effects
            border.MouseEnter += (_, __) =>
            {
                isHovering = true;
                ApplyVisualState(allowActiveSelection && switchIndex == _activeTabIndex);
            };
            border.MouseLeave += (_, __) =>
            {
                isHovering = false;
                ApplyVisualState(allowActiveSelection && switchIndex == _activeTabIndex);
            };

            // Right-click context menu (styled to match panel theme)
            var contextMenu = new System.Windows.Controls.ContextMenu();
            StyleTabContextMenu(contextMenu);

            var renameItem = CreateStyledMenuItem(MainWindow.GetString("Loc.TabRename"));
            int renameIndex = index;
            renameItem.Click += (_, __) => ShowTabRenameDialog(renameIndex);
            contextMenu.Items.Add(renameItem);

            var closeMenuItem = CreateStyledMenuItem(MainWindow.GetString("Loc.TabClose"));
            closeMenuItem.IsEnabled = _tabs.Count > 1;
            int closeMenuIndex = index;
            closeMenuItem.Click += (_, __) => RemoveTab(closeMenuIndex);
            contextMenu.Items.Add(closeMenuItem);

            var closeOthersItem = CreateStyledMenuItem(MainWindow.GetString("Loc.TabCloseOthers"));
            closeOthersItem.IsEnabled = _tabs.Count > 1;
            int keepIndex = index;
            closeOthersItem.Click += (_, __) =>
            {
                SaveActiveTabState();
                var keep = _tabs[keepIndex];
                _tabs.Clear();
                _tabs.Add(keep);
                _activeTabIndex = 0;
                LoadTabState(keep);
                SyncSingleTabHeaderTitle();
                RebuildTabBar();
                MainWindow.SaveSettings();
            };
            contextMenu.Items.Add(closeOthersItem);

            border.ContextMenu = contextMenu;

            return border;
        }

        private static void StyleTabContextMenu(System.Windows.Controls.ContextMenu menu)
        {
            menu.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 43, 54));
            menu.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 65, 78));
            menu.BorderThickness = new Thickness(1);
            menu.Padding = new Thickness(4, 6, 4, 6);
            menu.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 245, 250));
            menu.FontSize = 12.5;
            menu.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            menu.HasDropShadow = true;

            // Override the ContextMenu template for rounded corners
            var template = new ControlTemplate(typeof(System.Windows.Controls.ContextMenu));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, menu.Background);
            borderFactory.SetValue(Border.BorderBrushProperty, menu.BorderBrush);
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(2, 6, 2, 6));
            borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var dropShadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.4,
                Color = System.Windows.Media.Colors.Black
            };
            borderFactory.SetValue(Border.EffectProperty, dropShadow);

            var itemsPresenter = new FrameworkElementFactory(typeof(System.Windows.Controls.ItemsPresenter));
            borderFactory.AppendChild(itemsPresenter);
            template.VisualTree = borderFactory;
            menu.Template = template;
        }

        private static System.Windows.Controls.MenuItem CreateStyledMenuItem(string header)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = header,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 236, 244)),
                FontSize = 12.5,
            };

            // Custom template for dark styled menu items
            var template = new ControlTemplate(typeof(System.Windows.Controls.MenuItem));
            var border = new FrameworkElementFactory(typeof(Border), "MenuItemBorder");
            border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.PaddingProperty, new Thickness(12, 7, 16, 7));
            border.SetValue(Border.MarginProperty, new Thickness(2, 1, 2, 1));
            border.SetValue(Border.CursorProperty, System.Windows.Input.Cursors.Hand);

            var contentPresenter = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            contentPresenter.SetValue(System.Windows.Controls.ContentPresenter.ContentSourceProperty, "Header");
            contentPresenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(contentPresenter);

            template.VisualTree = border;

            // Hover trigger
            var hoverTrigger = new Trigger { Property = System.Windows.Controls.MenuItem.IsHighlightedProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 57, 70)), "MenuItemBorder"));
            template.Triggers.Add(hoverTrigger);

            // Disabled trigger
            var disabledTrigger = new Trigger { Property = System.Windows.Controls.MenuItem.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(System.Windows.Controls.MenuItem.ForegroundProperty,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 108, 125))));
            disabledTrigger.Setters.Add(new Setter(Border.CursorProperty,
                System.Windows.Input.Cursors.Arrow, "MenuItemBorder"));
            template.Triggers.Add(disabledTrigger);

            item.Template = template;
            return item;
        }

        private bool ShouldShowSeparator(int index, bool thisTabIsActive)
        {
            if (index >= _tabs.Count - 1) return false; // last tab, no right separator
            if (thisTabIsActive) return false;           // active tab hides separators
            // When collapsed (no active selection visible), always show separators
            if (_isCollapsedVisualState) return true;
            // Hide separator if neighbor to the right is the active tab
            if (index + 1 == _activeTabIndex) return false;
            return true;
        }

        private static Geometry BuildChromeTabShape(double width, double height)
        {
            if (width <= 2 || height <= 2)
            {
                return Geometry.Empty;
            }

            double w = Math.Max(24, width);
            double h = Math.Max(12, height);
            double bottomY = h;
            double topY = 6.0;
            double cornerR = 8.0;     // top corner radius
            double footH = 10.0;      // foot curve height
            double footW = 8.0;       // foot extends outward
            double inset = 4.0;       // side inset

            var figure = new PathFigure
            {
                StartPoint = new Point(-footW, bottomY),
                IsFilled = true,
                IsClosed = false // open bottom so it merges with content
            };

            // Left foot: S-curve from bottom-left up into the tab
            figure.Segments.Add(new BezierSegment(
                new Point(-footW, bottomY - footH * 0.55),
                new Point(inset, bottomY - footH * 0.1),
                new Point(inset, bottomY - footH),
                true));

            // Left side going up
            figure.Segments.Add(new LineSegment(new Point(inset, topY + cornerR), true));

            // Top-left corner
            figure.Segments.Add(new ArcSegment(
                new Point(inset + cornerR, topY),
                new System.Windows.Size(cornerR, cornerR),
                0, false, SweepDirection.Clockwise, true));

            // Top edge
            figure.Segments.Add(new LineSegment(new Point(w - inset - cornerR, topY), true));

            // Top-right corner
            figure.Segments.Add(new ArcSegment(
                new Point(w - inset, topY + cornerR),
                new System.Windows.Size(cornerR, cornerR),
                0, false, SweepDirection.Clockwise, true));

            // Right side going down
            figure.Segments.Add(new LineSegment(new Point(w - inset, bottomY - footH), true));

            // Right foot: S-curve from tab down to bottom-right
            figure.Segments.Add(new BezierSegment(
                new Point(w - inset, bottomY - footH * 0.1),
                new Point(w + footW, bottomY - footH * 0.55),
                new Point(w + footW, bottomY),
                true));

            var geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            return geometry;
        }

        private static Geometry BuildChromeTabOutline(double width, double height)
        {
            if (width <= 2 || height <= 2)
            {
                return Geometry.Empty;
            }

            double w = Math.Max(24, width);
            double h = Math.Max(12, height);
            double topY = 6.0;
            double cornerR = 8.0;
            double footH = 10.0;
            double inset = 4.0;
            double sideBottomY = h - footH;

            var figure = new PathFigure
            {
                StartPoint = new Point(inset, sideBottomY),
                IsFilled = false,
                IsClosed = false
            };

            figure.Segments.Add(new LineSegment(new Point(inset, topY + cornerR), true));
            figure.Segments.Add(new ArcSegment(
                new Point(inset + cornerR, topY),
                new System.Windows.Size(cornerR, cornerR),
                0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(w - inset - cornerR, topY), true));
            figure.Segments.Add(new ArcSegment(
                new Point(w - inset, topY + cornerR),
                new System.Windows.Size(cornerR, cornerR),
                0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(w - inset, sideBottomY), true));

            var geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            return geometry;
        }

        private void AnimateNewTab(Border tabItem)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            // Slide in from right + fade in
            var translate = new TranslateTransform(20, 0);
            var scale = new ScaleTransform(0.9, 0.9);
            var transformGroup = new TransformGroup();
            if (tabItem.RenderTransform != null &&
                tabItem.RenderTransform != Transform.Identity)
            {
                transformGroup.Children.Add(tabItem.RenderTransform.CloneCurrentValue());
            }
            transformGroup.Children.Add(scale);
            transformGroup.Children.Add(translate);
            tabItem.RenderTransform = transformGroup;
            tabItem.RenderTransformOrigin = new Point(0.5, 0.5);
            tabItem.Opacity = 0;

            var slideAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            slideAnim.Completed += (_, __) => translate.X = 0;

            var scaleXAnim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            scaleXAnim.Completed += (_, __) => scale.ScaleX = 1;

            var scaleYAnim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            scaleYAnim.Completed += (_, __) => scale.ScaleY = 1;

            var fadeAnim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            fadeAnim.Completed += (_, __) => tabItem.Opacity = 1;

            translate.BeginAnimation(TranslateTransform.XProperty, slideAnim);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
            tabItem.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }

        // ---- Tab reorder via mouse capture (same-panel) ----

        private void BeginTabReorderDrag(int tabIndex, Border sourceBorder)
        {
            if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
            if (GetVisibleTabCount() <= 1) return;

            _isTabReorderDragging = true;
            _tabReorderSource = sourceBorder;
            _tabReorderSourceIndex = tabIndex;
            _tabDragIndex = -1;
            _isTabDragPending = false;

            sourceBorder.Opacity = 0.72;
            System.Windows.Controls.Panel.SetZIndex(sourceBorder, 3);

            if (!sourceBorder.IsMouseCaptured)
            {
                sourceBorder.CaptureMouse();
            }
        }

        private void UpdateTabReorderDrag(System.Windows.Input.MouseEventArgs e)
        {
            if (!_isTabReorderDragging || _tabReorderSource == null || TabBarPanel == null) return;

            Point posInTabBar = e.GetPosition(TabBarPanel);

            // If mouse moves far enough vertically, escalate to cross-panel DragDrop
            if (Math.Abs(posInTabBar.Y) > TabDragDetachYThreshold ||
                Math.Abs(posInTabBar.Y - TabBarPanel.ActualHeight) > TabDragDetachYThreshold)
            {
                int sourceIndex = _tabReorderSourceIndex;
                var sourceBorder = _tabReorderSource;

                // Clean up reorder state first
                ClearTabDragPreview();
                _tabReorderSource.Opacity = 1;
                System.Windows.Controls.Panel.SetZIndex(_tabReorderSource, 0);
                _isTabReorderDragging = false;
                _tabReorderSource = null;
                _tabReorderSourceIndex = -1;

                if (sourceBorder.IsMouseCaptured)
                {
                    sourceBorder.ReleaseMouseCapture();
                }

                // Escalate to WPF DragDrop for cross-panel
                StartTabDrag(sourceIndex, sourceBorder);
                return;
            }

            // Calculate insert index using accumulated widths (not TranslatePoint)
            // to avoid animation-affected positions causing laggy/incorrect swaps
            var tabBorders = GetTabBarTabBorders();
            if (tabBorders.Count == 0) return;

            int insertIndex = _tabReorderSourceIndex;
            double mouseX = posInTabBar.X;

            double runningX = 0;
            for (int i = 0; i < tabBorders.Count; i++)
            {
                double tabWidth = tabBorders[i].ActualWidth;
                double tabEnd = runningX + tabWidth;

                if (mouseX < tabEnd || i == tabBorders.Count - 1)
                {
                    if (tabBorders[i].Tag is int rawIndex)
                    {
                        if (rawIndex > _tabReorderSourceIndex)
                        {
                            insertIndex = Math.Min(_tabs.Count, rawIndex + 1);
                        }
                        else if (rawIndex < _tabReorderSourceIndex)
                        {
                            insertIndex = rawIndex;
                        }
                        else
                        {
                            insertIndex = _tabReorderSourceIndex;
                        }
                    }
                    break;
                }

                runningX = tabEnd;
            }

            var payload = new TabDragPayload
            {
                SourcePanelId = PanelId,
                TabIndex = _tabReorderSourceIndex,
                DraggedWidth = _tabReorderSource.ActualWidth > 0 ? _tabReorderSource.ActualWidth : 124,
            };
            ApplyTabDragPreview(insertIndex, payload);
        }

        private void FinalizeTabReorderDrag()
        {
            if (!_isTabReorderDragging || _tabReorderSource == null) return;

            int sourceIndex = _tabReorderSourceIndex;
            int dropIndex = _tabInsertIndex >= 0 ? _tabInsertIndex : sourceIndex;
            var sourceBorder = _tabReorderSource;

            // Clear state BEFORE ReleaseMouseCapture to prevent re-entrancy
            // (ReleaseMouseCapture fires LostMouseCapture synchronously which
            // would call FinalizeTabReorderDrag again, causing double ReorderTab)
            _isTabReorderDragging = false;
            _tabReorderSource = null;
            _tabReorderSourceIndex = -1;

            // Restore visual state
            sourceBorder.Opacity = 1;
            System.Windows.Controls.Panel.SetZIndex(sourceBorder, 0);
            if (sourceBorder.IsMouseCaptured)
            {
                sourceBorder.ReleaseMouseCapture();
            }

            ClearTabDragPreview();

            if (dropIndex != sourceIndex && dropIndex >= 0)
            {
                ReorderTab(sourceIndex, dropIndex);
            }
        }

        // ---- Tab Drag & Drop: detach/merge tabs (cross-panel via WPF DragDrop) ----

        private void StartTabDrag(int tabIndex, Border sourceBorder)
        {
            if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
            if (GetVisibleTabCount() <= 1) return; // can't detach the only visible tab

            SaveActiveTabState();
            var tabData = _tabs[tabIndex];

            // Package tab data for drag — store in static field so all panels can access it
            var payload = new TabDragPayload
            {
                SourcePanelId = PanelId,
                TabIndex = tabIndex,
                TabData = tabData,
                DraggedWidth = GetTabDragWidth(tabIndex),
            };
            _activeTabDragPayload = payload;

            var dataObj = new System.Windows.DataObject();
            // Use a simple string marker so GetDataPresent works across windows
            dataObj.SetData(TabDragFormat, "tab");

            // Show floating drag adorner with tab name
            var adorner = CreateTabDragAdorner(tabData.TabName ?? "");
            adorner.Show();
            _activeTabDragAdorner = adorner;

            // Override cursor during drag via GiveFeedback
            void OnGiveFeedback(object s, System.Windows.GiveFeedbackEventArgs gfArgs)
            {
                gfArgs.UseDefaultCursors = false;
                System.Windows.Input.Mouse.SetCursor(System.Windows.Input.Cursors.Hand);
                gfArgs.Handled = true;

                // Update adorner position
                var screenPos = GetMouseScreenPosition();
                adorner.Left = screenPos.X + 12;
                adorner.Top = screenPos.Y + 16;
            }

            sourceBorder.GiveFeedback += OnGiveFeedback;

            // Start WPF DragDrop — this blocks until drop completes
            var result = System.Windows.DragDrop.DoDragDrop(sourceBorder, dataObj, System.Windows.DragDropEffects.Move);

            sourceBorder.GiveFeedback -= OnGiveFeedback;
            adorner.Close();
            _activeTabDragAdorner = null;

            // Clean up
            _activeTabDragPayload = null;
            ClearTabDragPreviewAcrossPanels();
            if (result == System.Windows.DragDropEffects.None)
            {
                // Dropped outside any panel — detach to a new panel
                DetachTabToNewPanel(tabIndex);
            }
            // If result == Move, the tab was already handled by TabBar_Drop
            // (either reordered within this panel or detached+inserted into another panel)
        }

        private static Point GetMouseScreenPosition()
        {
            GetCursorPos(out var pt);
            return new Point(pt.X, pt.Y);
        }

        private static Window CreateTabDragAdorner(string tabName)
        {
            var label = new TextBlock
            {
                Text = tabName,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 235, 245)),
                FontSize = 12,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 160,
            };

            var pill = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 42, 48, 60)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 80, 90, 110)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6),
                Child = label,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 2,
                    Opacity = 0.45,
                    Color = System.Windows.Media.Colors.Black
                }
            };

            var adornerWindow = new Window
            {
                Content = pill,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                IsHitTestVisible = false,
                ResizeMode = ResizeMode.NoResize,
            };

            var screenPos = GetMouseScreenPosition();
            adornerWindow.Left = screenPos.X + 12;
            adornerWindow.Top = screenPos.Y + 16;

            return adornerWindow;
        }

        private void DetachTabToNewPanel(int tabIndex)
        {
            var tab = DetachTab(tabIndex);
            if (tab == null) return;

            // Create a new panel at the mouse position
            if (System.Windows.Application.Current?.MainWindow is MainWindow mainWindow)
            {
                var mousePos = GetMouseScreenPosition();
                var newPanel = mainWindow.CreateDetachedTabPanel(tab, this, mousePos);
                if (newPanel != null)
                {
                    newPanel.Show();
                    MainWindow.SaveSettings();
                    MainWindow.NotifyPanelsChanged();
                }
            }
        }

        private void ShowTabRenameDialog(int tabIndex)
        {
            if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
            var tab = _tabs[tabIndex];
            var dialog = new InputBox(MainWindow.GetString("Loc.TabRename"), tab.TabName)
            {
                Owner = this,
                Title = MainWindow.GetString("Loc.TabRename"),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                string newName = dialog.ResultText?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    RenameTab(tabIndex, newName);
                }
            }
        }

        private void SyncSingleTabHeaderTitle()
        {
            int visibleTabCount = GetVisibleTabCount();
            if (visibleTabCount == 0)
            {
                return;
            }

            if (visibleTabCount != 1)
            {
                return;
            }

            int visibleIndex = FindFirstVisibleTabIndex();
            if (visibleIndex < 0)
            {
                return;
            }

            string singleTabName = _tabs[visibleIndex].TabName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(singleTabName))
            {
                singleTabName = MainWindow.GetString("Loc.PanelDefaultTitle");
            }

            Title = singleTabName;
            if (PanelTitle != null)
            {
                PanelTitle.Text = singleTabName;
            }
        }

        private void AddTabButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new WinForms.FolderBrowserDialog();
            var result = dlg.ShowDialog();
            if (result == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                AddTab(dlg.SelectedPath, switchTo: true);
            }
        }

        // --- Drag & Drop on tab bar: accept folder drops AND tab drops from other panels ---

        private Border? _tabInsertIndicator;
        private int _tabInsertIndex = -1;

        private bool IsHeaderTabDropCandidate(System.Windows.DragEventArgs e)
        {
            if (TryDataObjectHasFormat(e.Data, TabDragFormat)) return true;
            if (!TryDataObjectHasFormat(e.Data, System.Windows.DataFormats.FileDrop)) return false;
            return ExtractFileDropPaths(e.Data).Any(p => Directory.Exists(p));
        }

        private int GetRawInsertIndexFromVisualPosition(int visualPosition)
        {
            var visibleIndices = GetVisibleTabIndices();
            if (visibleIndices.Count == 0)
            {
                return _tabs.Count;
            }

            if (visualPosition <= 0)
            {
                return visibleIndices[0];
            }

            if (visualPosition >= visibleIndices.Count)
            {
                return _tabs.Count;
            }

            return visibleIndices[visualPosition];
        }

        private int GetVisualPositionFromRawInsertIndex(int rawInsertIndex)
        {
            var visibleIndices = GetVisibleTabIndices();
            int visualPosition = 0;
            foreach (int visibleIndex in visibleIndices)
            {
                if (visibleIndex >= rawInsertIndex)
                {
                    return visualPosition;
                }

                visualPosition++;
            }

            return visualPosition;
        }

        /// <summary>
        /// Calculates the tab insert index based on mouse X position over tab bar items.
        /// Returns the index where the new/moved tab should be inserted (0.._tabs.Count).
        /// </summary>
        private int GetTabInsertIndex(System.Windows.DragEventArgs e)
        {
            if (TabBarPanel == null || _tabs.Count == 0)
                return _tabs.Count;

            var tabBorders = GetTabBarTabBorders();
            if (tabBorders.Count == 0)
            {
                return _tabs.Count;
            }

            var mousePos = e.GetPosition(TabBarPanel);
            double mouseX = mousePos.X;

            double runningX = 0;
            for (int i = 0; i < tabBorders.Count; i++)
            {
                double tabWidth = tabBorders[i].ActualWidth;
                double tabCenter = runningX + tabWidth / 2;
                if (mouseX < tabCenter && tabBorders[i].Tag is int rawIndex)
                {
                    return rawIndex;
                }
                runningX += tabWidth;
            }

            return _tabs.Count;
        }

        private int GetLiveTabInsertIndex(System.Windows.DragEventArgs e, TabDragPayload? payload)
        {
            if (TabBarPanel == null || _tabs.Count == 0)
            {
                return _tabs.Count;
            }

            var tabBorders = GetTabBarTabBorders();
            if (tabBorders.Count == 0)
            {
                return _tabs.Count;
            }

            var mousePos = e.GetPosition(TabBarPanel);
            double mouseX = mousePos.X;

            double runningX = 0;
            for (int i = 0; i < tabBorders.Count; i++)
            {
                double tabWidth = tabBorders[i].ActualWidth;
                double tabMid = runningX + (tabWidth * 0.5);
                if (mouseX < tabMid && tabBorders[i].Tag is int rawIndex)
                {
                    return rawIndex;
                }
                runningX += tabWidth;
            }

            return _tabs.Count;
        }

        private double GetTabDragWidth(int tabIndex)
        {
            Border? border = GetTabBarTabBorders()
                .FirstOrDefault(candidate => candidate.Tag is int index && index == tabIndex);
            if (border != null && border.ActualWidth > 0)
            {
                return border.ActualWidth;
            }

            return 124;
        }

        private List<Border> GetTabBarTabBorders()
        {
            return TabBarPanel?.Children
                .OfType<Border>()
                .Where(border => border.Tag is int)
                .OrderBy(border => (int)border.Tag)
                .ToList() ?? new List<Border>();
        }

        private void ApplyTabDragPreview(int insertIndex, TabDragPayload? payload)
        {
            if (TabBarPanel == null)
            {
                return;
            }

            if (insertIndex == _tabInsertIndex)
                return;

            HideInsertIndicator();
            TabDropPreview.Visibility = Visibility.Collapsed;
            _tabInsertIndex = insertIndex;

            var tabBorders = GetTabBarTabBorders();
            if (tabBorders.Count == 0)
            {
                return;
            }

            bool samePanel = payload != null &&
                string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase);
            int sourceIndex = samePanel && payload != null ? payload.TabIndex : -1;
            double draggedWidth = payload?.DraggedWidth ?? 124;

            Border? sourceBorder = null;
            if (samePanel)
            {
                sourceBorder = tabBorders.FirstOrDefault(border => border.Tag is int idx && idx == sourceIndex);
                if (sourceBorder != null && sourceBorder.ActualWidth > 0)
                {
                    draggedWidth = sourceBorder.ActualWidth;
                }
            }

            foreach (Border border in tabBorders)
            {
                ApplyTabBarItemOffset(border, 0);
                border.Opacity = 1;
                System.Windows.Controls.Panel.SetZIndex(border, 0);
            }

            if (samePanel && sourceBorder != null)
            {
                sourceBorder.Opacity = 0.72;
                System.Windows.Controls.Panel.SetZIndex(sourceBorder, 3);

                if (insertIndex > sourceIndex + 1)
                {
                    double sourceOffset = 0;
                    foreach (Border impacted in tabBorders
                        .Where(border => border.Tag is int idx && idx > sourceIndex && idx < insertIndex))
                    {
                        sourceOffset += impacted.ActualWidth;
                        ApplyTabBarItemOffset(impacted, -draggedWidth);
                    }

                    ApplyTabBarItemOffset(sourceBorder, sourceOffset);
                    return;
                }

                if (insertIndex < sourceIndex)
                {
                    double sourceOffset = 0;
                    foreach (Border impacted in tabBorders
                        .Where(border => border.Tag is int idx && idx >= insertIndex && idx < sourceIndex))
                    {
                        sourceOffset += impacted.ActualWidth;
                        ApplyTabBarItemOffset(impacted, draggedWidth);
                    }

                    ApplyTabBarItemOffset(sourceBorder, -sourceOffset);
                    return;
                }

                ApplyTabBarItemOffset(sourceBorder, 0);
                return;
            }

            foreach (Border impacted in tabBorders
                .Where(border => border.Tag is int idx && idx >= insertIndex))
            {
                ApplyTabBarItemOffset(impacted, draggedWidth);
            }
        }

        private void ClearTabDragPreview()
        {
            foreach (Border border in GetTabBarTabBorders())
            {
                ApplyTabBarItemOffset(border, 0);
                border.Opacity = 1;
                System.Windows.Controls.Panel.SetZIndex(border, 0);
            }

            _tabInsertIndex = -1;
        }

        private static void ClearTabDragPreviewAcrossPanels()
        {
            foreach (DesktopPanel panel in System.Windows.Application.Current?.Windows.OfType<DesktopPanel>() ?? Enumerable.Empty<DesktopPanel>())
            {
                panel.ClearTabDragPreview();
                panel.HideInsertIndicator();
                if (panel.TabDropPreview != null)
                {
                    panel.TabDropPreview.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static void ApplyTabBarItemOffset(Border border, double targetOffset)
        {
            TranslateTransform? transform = GetTabBarItemTransform(border);
            if (transform == null)
            {
                return;
            }

            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(targetOffset, TimeSpan.FromMilliseconds(TabDragPreviewAnimationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                    FillBehavior = FillBehavior.HoldEnd
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private static TranslateTransform? GetTabBarItemTransform(Border border)
        {
            if (border.RenderTransform is TranslateTransform translate)
            {
                return translate;
            }

            if (border.RenderTransform is TransformGroup existingGroup)
            {
                TranslateTransform? existing = existingGroup.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (existing != null)
                {
                    return existing;
                }

                var appended = new TranslateTransform();
                existingGroup.Children.Add(appended);
                return appended;
            }

            var created = new TranslateTransform();
            if (border.RenderTransform != null && border.RenderTransform != Transform.Identity)
            {
                var group = new TransformGroup();
                group.Children.Add(border.RenderTransform.CloneCurrentValue());
                group.Children.Add(created);
                border.RenderTransform = group;
            }
            else
            {
                border.RenderTransform = created;
            }

            return created;
        }

        /// <summary>
        /// Shows or updates the insert indicator at the given tab index position.
        /// </summary>
        private void ShowInsertIndicator(int insertIndex)
        {
            if (TabBarPanel == null) return;

            // Create indicator if needed
            if (_tabInsertIndicator == null)
            {
                _tabInsertIndicator = new Border
                {
                    Width = 2,
                    Height = 26,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0x8E, 0xFF)),
                    CornerRadius = new CornerRadius(1),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    IsHitTestVisible = false,
                    Margin = new Thickness(-1, 0, -1, 2),
                };
            }

            // Remove from previous position
            if (TabBarPanel.Children.Contains(_tabInsertIndicator))
                TabBarPanel.Children.Remove(_tabInsertIndicator);

            int visualIndex = Math.Min(GetVisualPositionFromRawInsertIndex(insertIndex), TabBarPanel.Children.Count);
            TabBarPanel.Children.Insert(visualIndex, _tabInsertIndicator);
            _tabInsertIndex = insertIndex;
        }

        private void HideInsertIndicator()
        {
            if (_tabInsertIndicator != null && TabBarPanel != null &&
                TabBarPanel.Children.Contains(_tabInsertIndicator))
            {
                TabBarPanel.Children.Remove(_tabInsertIndicator);
            }
            _tabInsertIndex = -1;
        }

        private void TabBar_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            if (!IsHeaderTabDropCandidate(e)) return;

            if (TryDataObjectHasFormat(e.Data, TabDragFormat))
            {
                var payload = _activeTabDragPayload;
                if (GetVisibleTabCount() > 1)
                {
                    ApplyTabDragPreview(GetLiveTabInsertIndex(e, payload), payload);
                }
                else
                {
                    // Single tab — show full header overlay
                    TabDropPreview.Visibility = Visibility.Visible;
                }
                e.Effects = System.Windows.DragDropEffects.Move;
            }
            else
            {
                TabDropPreview.Visibility = Visibility.Visible;
                e.Effects = System.Windows.DragDropEffects.Copy;
            }
            e.Handled = true;
        }

        private void TabBar_DragLeave(object sender, System.Windows.DragEventArgs e)
        {
            ClearTabDragPreview();
            HideInsertIndicator();
            TabDropPreview.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void TabBar_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (!IsHeaderTabDropCandidate(e)) return;

            if (TryDataObjectHasFormat(e.Data, TabDragFormat))
            {
                var payload = _activeTabDragPayload;

                if (GetVisibleTabCount() > 1)
                {
                    ApplyTabDragPreview(GetLiveTabInsertIndex(e, payload), payload);
                }
                else
                {
                    // Single tab — show full header overlay
                    TabDropPreview.Visibility = Visibility.Visible;
                }
                e.Effects = System.Windows.DragDropEffects.Move;
            }
            else
            {
                TabDropPreview.Visibility = Visibility.Visible;
                e.Effects = System.Windows.DragDropEffects.Copy;
            }
            e.Handled = true;
        }

        private void TabBar_Drop(object sender, System.Windows.DragEventArgs e)
        {
            int dropIndex = _tabInsertIndex >= 0
                ? _tabInsertIndex
                : GetLiveTabInsertIndex(e, _activeTabDragPayload);
            ClearTabDragPreview();
            HideInsertIndicator();
            TabDropPreview.Visibility = Visibility.Collapsed;

            // Handle tab drop
            if (TryDataObjectHasFormat(e.Data, TabDragFormat))
            {
                var payload = _activeTabDragPayload;
                if (payload != null)
                {
                    if (string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Same panel — reorder
                        ReorderTab(payload.TabIndex, dropIndex);
                    }
                    else
                    {
                        // Different panel — detach from source, insert here
                        var sourcePanel = System.Windows.Application.Current?.Windows
                            .OfType<DesktopPanel>()
                            .FirstOrDefault(p => string.Equals(p.PanelId, payload.SourcePanelId, StringComparison.OrdinalIgnoreCase));

                        if (sourcePanel != null)
                        {
                            var detached = sourcePanel.DetachTab(payload.TabIndex);
                            if (detached != null)
                            {
                                InsertTab(detached, dropIndex, switchTo: true);
                            }
                        }
                        else
                        {
                            InsertTab(payload.TabData, dropIndex, switchTo: true);
                        }
                    }
                    e.Effects = System.Windows.DragDropEffects.Move;
                }
                e.Handled = true;
                return;
            }

            // Handle folder drop as new tab
            if (!TryDataObjectHasFormat(e.Data, System.Windows.DataFormats.FileDrop))
            {
                e.Handled = true;
                return;
            }

            var paths = ExtractFileDropPaths(e.Data);
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    SaveActiveTabState();
                    var tab = new PanelTabData
                    {
                        PanelId = CreateLogicalTabPanelId(),
                        TabId = Guid.NewGuid().ToString("N"),
                        TabName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? MainWindow.GetString("Loc.TabNewTab"),
                        IsHidden = false,
                        PanelType = PanelKind.Folder.ToString(),
                        FolderPath = path,
                        DefaultFolderPath = path,
                        ShowHidden = showHiddenItems,
                        ShowParentNavigationItem = showParentNavigationItem,
                        IconViewParentNavigationMode = NormalizeIconViewParentNavigationMode(iconViewParentNavigationMode, showParentNavigationItem),
                        ShowFileExtensions = showFileExtensions,
                        OpenFoldersExternally = openFoldersExternally,
                        OpenItemsOnSingleClick = openItemsOnSingleClick,
                        ViewMode = viewMode,
                    };
                    InsertTab(tab, dropIndex, switchTo: true);
                    break;
                }
            }
            e.Handled = true;
        }

        /// <summary>
        /// Reorders a tab within this panel from one index to another.
        /// </summary>
        private void ReorderTab(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= _tabs.Count) return;
            if (toIndex < 0) toIndex = 0;
            if (toIndex > _tabs.Count) toIndex = _tabs.Count;
            if (fromIndex == toIndex || fromIndex + 1 == toIndex) return;

            SaveActiveTabState();

            // Track active tab by identity
            var activeTab = _tabs[_activeTabIndex];

            var tab = _tabs[fromIndex];
            _tabs.RemoveAt(fromIndex);

            int adjustedIndex = toIndex > fromIndex ? toIndex - 1 : toIndex;
            adjustedIndex = Math.Max(0, Math.Min(adjustedIndex, _tabs.Count));
            _tabs.Insert(adjustedIndex, tab);

            // Restore active tab index by identity
            _activeTabIndex = _tabs.IndexOf(activeTab);

            RebuildTabBar();
            MainWindow.SaveSettings();
        }
    }

    /// <summary>
    /// Payload for dragging a tab between panels.
    /// </summary>
    internal class TabDragPayload
    {
        public string SourcePanelId { get; set; } = "";
        public int TabIndex { get; set; }
        public PanelTabData TabData { get; set; } = new PanelTabData();
        public double DraggedWidth { get; set; }
    }
}
