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

        // Tab drag state
        private const string TabDragFormat = "DesktopPlus_TabDrag";
        private const double TabDragThreshold = 8.0;
        private bool _isTabDragPending;
        private Point _tabDragStartPoint;
        private int _tabDragIndex = -1;
        private Window? _tabDragGhost;
        private int _animateNewTabIndex = -1;

        // Static shared payload so all panels can access the drag data
        // (WPF DragDrop can lose custom non-serializable data across windows)
        private static TabDragPayload? _activeTabDragPayload;

        public IReadOnlyList<PanelTabData> Tabs => _tabs;
        public int ActiveTabIndex => _activeTabIndex;
        public PanelTabData? ActiveTab =>
            _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
                ? _tabs[_activeTabIndex]
                : null;

        public void SaveActiveTabState()
        {
            if (ActiveTab == null) return;
            var tab = ActiveTab;
            tab.PanelType = PanelType.ToString();
            tab.FolderPath = currentFolderPath ?? "";
            tab.DefaultFolderPath = defaultFolderPath ?? "";
            tab.ShowHidden = showHiddenItems;
            tab.ShowParentNavigationItem = showParentNavigationItem;
            tab.ShowFileExtensions = showFileExtensions;
            tab.OpenFoldersExternally = openFoldersExternally;
            tab.ViewMode = viewMode;
            tab.ShowMetadataType = showMetadataType;
            tab.ShowMetadataSize = showMetadataSize;
            tab.ShowMetadataCreated = showMetadataCreated;
            tab.ShowMetadataModified = showMetadataModified;
            tab.ShowMetadataDimensions = showMetadataDimensions;
            tab.MetadataOrder = NormalizeMetadataOrder(metadataOrder);
            tab.PinnedItems = PinnedItems.ToList();
        }

        private void LoadTabState(PanelTabData tab)
        {
            showHiddenItems = tab.ShowHidden;
            showParentNavigationItem = tab.ShowParentNavigationItem;
            showFileExtensions = tab.ShowFileExtensions;
            openFoldersExternally = tab.OpenFoldersExternally;
            viewMode = NormalizeViewMode(tab.ViewMode);
            showMetadataType = tab.ShowMetadataType;
            showMetadataSize = tab.ShowMetadataSize;
            showMetadataCreated = tab.ShowMetadataCreated;
            showMetadataModified = tab.ShowMetadataModified;
            showMetadataDimensions = tab.ShowMetadataDimensions;
            metadataOrder = NormalizeMetadataOrder(tab.MetadataOrder);
            defaultFolderPath = tab.DefaultFolderPath ?? "";

            if (Enum.TryParse<PanelKind>(tab.PanelType, true, out var kind))
            {
                if (kind == PanelKind.Folder && !string.IsNullOrWhiteSpace(tab.FolderPath))
                {
                    LoadFolder(tab.FolderPath, saveSettings: false);
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
            if (index < 0 || index >= _tabs.Count || index == _activeTabIndex) return;
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
                TabId = Guid.NewGuid().ToString("N"),
                TabName = tabName,
                PanelType = !string.IsNullOrWhiteSpace(folderPath) ? PanelKind.Folder.ToString() : PanelKind.None.ToString(),
                FolderPath = folderPath ?? "",
                DefaultFolderPath = folderPath ?? "",
                ShowHidden = showHiddenItems,
                ShowParentNavigationItem = showParentNavigationItem,
                ShowFileExtensions = showFileExtensions,
                OpenFoldersExternally = openFoldersExternally,
                ViewMode = viewMode,
                ShowMetadataType = showMetadataType,
                ShowMetadataSize = showMetadataSize,
                ShowMetadataCreated = showMetadataCreated,
                ShowMetadataModified = showMetadataModified,
                ShowMetadataDimensions = showMetadataDimensions,
                MetadataOrder = NormalizeMetadataOrder(metadataOrder),
            };

            _tabs.Add(tab);

            if (switchTo)
            {
                _activeTabIndex = _tabs.Count - 1;
                LoadTabState(tab);
            }

            _animateNewTabIndex = _tabs.Count - 1;
            RebuildTabBar();
            MainWindow.SaveSettings();
            return tab;
        }

        /// <summary>
        /// Insert a tab (from another panel) at the given position.
        /// </summary>
        public void InsertTab(PanelTabData tab, int insertAt = -1, bool switchTo = true)
        {
            SaveActiveTabState();
            if (insertAt < 0 || insertAt > _tabs.Count) insertAt = _tabs.Count;
            _tabs.Insert(insertAt, tab);

            if (switchTo)
            {
                _activeTabIndex = insertAt;
                LoadTabState(tab);
            }

            _animateNewTabIndex = insertAt;
            RebuildTabBar();
            MainWindow.SaveSettings();
        }

        /// <summary>
        /// Detach a tab from this panel and return it. Does NOT create the new panel.
        /// Returns null if this is the last tab.
        /// </summary>
        public PanelTabData? DetachTab(int index)
        {
            if (_tabs.Count <= 1) return null;
            if (index < 0 || index >= _tabs.Count) return null;

            // Make sure the tab data is current if it's the active one
            if (index == _activeTabIndex)
            {
                SaveActiveTabState();
            }

            var tab = _tabs[index];
            bool wasActive = index == _activeTabIndex;
            _tabs.RemoveAt(index);

            if (_activeTabIndex >= _tabs.Count)
                _activeTabIndex = _tabs.Count - 1;
            else if (index < _activeTabIndex)
                _activeTabIndex--;

            if (wasActive)
            {
                LoadTabState(_tabs[_activeTabIndex]);
            }

            RebuildTabBar();
            MainWindow.SaveSettings();
            return tab;
        }

        public void RemoveTab(int index)
        {
            if (_tabs.Count <= 1) return;
            if (index < 0 || index >= _tabs.Count) return;

            bool wasActive = index == _activeTabIndex;
            _tabs.RemoveAt(index);

            if (_activeTabIndex >= _tabs.Count)
                _activeTabIndex = _tabs.Count - 1;
            else if (index < _activeTabIndex)
                _activeTabIndex--;

            if (wasActive)
            {
                LoadTabState(_tabs[_activeTabIndex]);
            }

            RebuildTabBar();
            MainWindow.SaveSettings();
        }

        public void RenameTab(int index, string newName)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _tabs[index].TabName = newName;
            RebuildTabBar();
            MainWindow.SaveSettings();
        }

        public void InitializeTabsFromData(List<PanelTabData> tabs, int activeIndex)
        {
            _tabs.Clear();
            foreach (var tab in tabs)
            {
                tab.PinnedItems ??= new List<string>();
                tab.ViewMode = NormalizeViewMode(tab.ViewMode);
                tab.MetadataOrder = NormalizeMetadataOrder(tab.MetadataOrder);
            }
            _tabs.AddRange(tabs);
            _activeTabIndex = Math.Max(0, Math.Min(activeIndex, _tabs.Count - 1));
            RebuildTabBar();
        }

        public void InitializeSingleTabFromCurrentState()
        {
            if (_tabs.Count > 0) return;
            _tabs.Add(new PanelTabData
            {
                TabId = Guid.NewGuid().ToString("N"),
                TabName = PanelTitle?.Text ?? MainWindow.GetString("Loc.PanelDefaultTitle"),
                PanelType = PanelType.ToString(),
                FolderPath = currentFolderPath ?? "",
                DefaultFolderPath = defaultFolderPath ?? "",
                ShowHidden = showHiddenItems,
                ShowParentNavigationItem = showParentNavigationItem,
                ShowFileExtensions = showFileExtensions,
                OpenFoldersExternally = openFoldersExternally,
                ViewMode = viewMode,
                ShowMetadataType = showMetadataType,
                ShowMetadataSize = showMetadataSize,
                ShowMetadataCreated = showMetadataCreated,
                ShowMetadataModified = showMetadataModified,
                ShowMetadataDimensions = showMetadataDimensions,
                MetadataOrder = NormalizeMetadataOrder(metadataOrder),
                PinnedItems = PinnedItems.ToList()
            });
            _activeTabIndex = 0;
            RebuildTabBar();
        }

        private void RebuildTabBar(bool? collapsedOverride = null)
        {
            if (TabBarPanel == null || TabBarContainer == null) return;

            TabBarPanel.Children.Clear();
            bool isCollapsedVisual = collapsedOverride ?? !isContentVisible;

            if (_tabs.Count <= 1)
            {
                TabBarContainer.Visibility = Visibility.Collapsed;
                if (PanelTitle != null)
                    PanelTitle.Visibility = Visibility.Visible;
                UpdateHeaderBottomBorderForCurrentState(collapsed: isCollapsedVisual);
                return;
            }

            TabBarContainer.Visibility = Visibility.Visible;
            if (PanelTitle != null)
                PanelTitle.Visibility = Visibility.Collapsed;
            // Keep border behavior in sync with collapsed/expanded visual state.
            UpdateHeaderBottomBorderForCurrentState(collapsed: isCollapsedVisual);

            int animateIndex = _animateNewTabIndex;
            _animateNewTabIndex = -1;
            bool showActiveSelection = !isCollapsedVisual;

            for (int i = 0; i < _tabs.Count; i++)
            {
                var tabItem = CreateTabBarItem(_tabs[i], i, showActiveSelection && i == _activeTabIndex);
                TabBarPanel.Children.Add(tabItem);

                if (i == animateIndex)
                {
                    AnimateNewTab(tabItem);
                }
            }
        }

        private Border CreateTabBarItem(PanelTabData tab, int index, bool isActive)
        {
            Brush activeTextBrush = (Brush)FindResource("PanelText");
            Brush inactiveTextBrush = (Brush)FindResource("PanelMuted");
            var appearance = _currentAppearance ?? MainWindow.Appearance ?? new AppearanceSettings();
            var activeColor = MainWindow.ResolvePanelTabActiveColor(appearance);
            var inactiveColor = MainWindow.ResolvePanelTabInactiveColor(appearance);
            var hoverColor = MainWindow.ResolvePanelTabHoverColor(appearance);
            static System.Windows.Media.Color ParseColor(string? value, System.Windows.Media.Color fallback)
            {
                if (string.IsNullOrWhiteSpace(value)) return fallback;
                try
                {
                    return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
                }
                catch
                {
                    return fallback;
                }
            }

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

            var headerColor = ParseColor(appearance.HeaderColor, System.Windows.Media.Color.FromRgb(42, 48, 59));
            var bodyColor = ParseColor(appearance.BackgroundColor, System.Windows.Media.Color.FromRgb(31, 36, 46));

            var white = System.Windows.Media.Color.FromRgb(255, 255, 255);
            // Active tab: use body color so it blends seamlessly into content
            var chromeActiveColor = bodyColor;
            var chromeActiveBorderColor = Blend(bodyColor, white, 0.12);
            // Hover pill for inactive tabs
            var hoverPillColor = Blend(headerColor, white, 0.10);
            var separatorColor = Blend(headerColor, white, 0.12);

            var activeBackgroundBrush = CreateBrush(chromeActiveColor, 255);
            var activeBorderBrush = CreateBrush(chromeActiveBorderColor, 160);
            var hoverPillBrush = CreateBrush(hoverPillColor, 255);
            var separatorBrush = CreateBrush(separatorColor, 160);

            var tabNameBlock = new TextBlock
            {
                Text = tab.TabName,
                FontSize = Math.Max(10, Math.Min(16, appearance.ItemFontSize - 1)),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 156,
                Foreground = isActive ? activeTextBrush : inactiveTextBrush,
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
                Opacity = (switchIndex < (_tabs.Count - 1) && !isActive && switchIndex != _activeTabIndex - 1) ? 1.0 : 0.0
            };

            // Active tab: Chrome-style shape drawn via Path on a Canvas
            var tabFill = new System.Windows.Shapes.Path
            {
                Fill = activeBackgroundBrush,
                Stroke = activeBorderBrush,
                StrokeThickness = 1,
                SnapsToDevicePixels = true,
                Stretch = Stretch.None,
                Visibility = isActive ? Visibility.Visible : Visibility.Collapsed,
            };
            var tabCanvas = new Canvas { SnapsToDevicePixels = true, ClipToBounds = false };
            tabCanvas.Children.Add(tabFill);

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
                SnapsToDevicePixels = true
            };

            border.Loaded += (_, __) =>
            {
                if (isActive || (int)border.Tag == _activeTabIndex)
                    tabFill.Data = BuildChromeTabShape(border.ActualWidth, border.ActualHeight);
            };
            border.SizeChanged += (_, sizeArgs) =>
            {
                if ((int)border.Tag == _activeTabIndex)
                    tabFill.Data = BuildChromeTabShape(sizeArgs.NewSize.Width, sizeArgs.NewSize.Height);
            };

            bool isHovering = false;

            void ApplyVisualState(bool activeState)
            {
                if (activeState)
                {
                    tabFill.Visibility = Visibility.Visible;
                    tabFill.Data = BuildChromeTabShape(border.ActualWidth, border.ActualHeight);
                    hoverPill.Background = System.Windows.Media.Brushes.Transparent;
                    separatorRight.Opacity = 0;
                    tabNameBlock.Foreground = activeTextBrush;
                    return;
                }

                tabFill.Visibility = Visibility.Collapsed;
                hoverPill.Background = isHovering ? hoverPillBrush : System.Windows.Media.Brushes.Transparent;
                bool canShowRight = switchIndex < (_tabs.Count - 1);
                bool neighborIsActive = (switchIndex + 1 == _activeTabIndex) || (switchIndex - 1 == _activeTabIndex);
                separatorRight.Opacity = (isHovering || !canShowRight || neighborIsActive || switchIndex == _activeTabIndex - 1) ? 0.0 : 1.0;
                tabNameBlock.Foreground = isHovering ? activeTextBrush : inactiveTextBrush;
            }

            ApplyVisualState(isActive);

            // Mouse down: start potential drag or tab switch
            border.MouseLeftButtonDown += (_, e) =>
            {
                _isTabDragPending = true;
                _tabDragStartPoint = e.GetPosition(null);
                _tabDragIndex = switchIndex;
                SwitchToTab(switchIndex);
                e.Handled = true;
            };

            border.MouseLeftButtonUp += (_, e) =>
            {
                _isTabDragPending = false;
                _tabDragIndex = -1;
            };

            border.MouseMove += (_, e) =>
            {
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
                    StartTabDrag(switchIndex);
                }
            };

            // Hover effects
            border.MouseEnter += (_, __) =>
            {
                isHovering = true;
                ApplyVisualState(switchIndex == _activeTabIndex);
            };
            border.MouseLeave += (_, __) =>
            {
                isHovering = false;
                ApplyVisualState(switchIndex == _activeTabIndex);
            };

            // Right-click context menu
            var contextMenu = new System.Windows.Controls.ContextMenu();

            var renameItem = new System.Windows.Controls.MenuItem { Header = MainWindow.GetString("Loc.TabRename") };
            int renameIndex = index;
            renameItem.Click += (_, __) => ShowTabRenameDialog(renameIndex);
            contextMenu.Items.Add(renameItem);

            var closeMenuItem = new System.Windows.Controls.MenuItem { Header = MainWindow.GetString("Loc.TabClose") };
            closeMenuItem.IsEnabled = _tabs.Count > 1;
            int closeMenuIndex = index;
            closeMenuItem.Click += (_, __) => RemoveTab(closeMenuIndex);
            contextMenu.Items.Add(closeMenuItem);

            var closeOthersItem = new System.Windows.Controls.MenuItem { Header = MainWindow.GetString("Loc.TabCloseOthers") };
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
                RebuildTabBar();
                MainWindow.SaveSettings();
            };
            contextMenu.Items.Add(closeOthersItem);

            border.ContextMenu = contextMenu;

            return border;
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

        // ---- Tab Drag & Drop: detach/merge tabs ----

        private void StartTabDrag(int tabIndex)
        {
            if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
            if (_tabs.Count <= 1) return; // can't detach the only tab

            SaveActiveTabState();
            var tabData = _tabs[tabIndex];

            // Create a ghost window that follows the cursor
            _tabDragGhost = CreateTabDragGhost(tabData.TabName);
            _tabDragGhost.Show();

            // Package tab data for drag — store in static field so all panels can access it
            var payload = new TabDragPayload
            {
                SourcePanelId = PanelId,
                TabIndex = tabIndex,
                TabData = tabData,
            };
            _activeTabDragPayload = payload;

            var dataObj = new System.Windows.DataObject();
            // Use a simple string marker so GetDataPresent works across windows
            dataObj.SetData(TabDragFormat, "tab");

            // Start WPF DragDrop — this blocks until drop completes
            var result = System.Windows.DragDrop.DoDragDrop(this, dataObj, System.Windows.DragDropEffects.Move);

            // Clean up
            _activeTabDragPayload = null;
            if (_tabDragGhost != null)
            {
                _tabDragGhost.Close();
                _tabDragGhost = null;
            }

            if (result == System.Windows.DragDropEffects.None)
            {
                // Dropped outside any panel — detach to a new panel
                DetachTabToNewPanel(tabIndex);
            }
            // If result == Move, the tab was already handled by TabBar_Drop
            // (either reordered within this panel or detached+inserted into another panel)
        }

        private Window CreateTabDragGhost(string tabName)
        {
            var ghost = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                IsHitTestVisible = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowActivated = false,
            };

            var border = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xDD, 0x1A, 0x20, 0x2A)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0x8E, 0xFF)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6),
                Child = new TextBlock
                {
                    Text = tabName,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                },
            };

            ghost.Content = border;

            // Follow mouse position using timer
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            timer.Tick += (_, __) =>
            {
                if (!ghost.IsVisible) { timer.Stop(); return; }
                var screenPos = GetMouseScreenPosition();
                ghost.Left = screenPos.X + 12;
                ghost.Top = screenPos.Y + 12;
            };
            timer.Start();

            // Position initially
            var initialPos = GetMouseScreenPosition();
            ghost.Left = initialPos.X + 12;
            ghost.Top = initialPos.Y + 12;

            return ghost;
        }

        private static Point GetMouseScreenPosition()
        {
            GetCursorPos(out var pt);
            return new Point(pt.X, pt.Y);
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
                }
            }
        }

        private void ShowTabRenameDialog(int tabIndex)
        {
            if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
            var tab = _tabs[tabIndex];

            var dialog = new Window
            {
                Title = MainWindow.GetString("Loc.TabRename"),
                Width = 300,
                Height = 130,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1D, 0x23)),
            };

            var stack = new StackPanel { Margin = new Thickness(12) };
            var textBox = new System.Windows.Controls.TextBox
            {
                Text = tab.TabName,
                FontSize = 14,
                Padding = new Thickness(6, 4, 6, 4),
            };
            textBox.SelectAll();
            stack.Children.Add(textBox);

            var btnPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var okBtn = new System.Windows.Controls.Button
            {
                Content = "OK",
                Width = 70,
                Padding = new Thickness(0, 4, 0, 4),
                IsDefault = true,
            };
            okBtn.Click += (_, __) =>
            {
                if (!string.IsNullOrWhiteSpace(textBox.Text))
                {
                    RenameTab(tabIndex, textBox.Text.Trim());
                }
                dialog.Close();
            };
            btnPanel.Children.Add(okBtn);
            stack.Children.Add(btnPanel);

            dialog.Content = stack;
            textBox.Focus();
            dialog.ShowDialog();
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
            if (e.Data.GetDataPresent(TabDragFormat)) return true;
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return false;
            return ExtractFileDropPaths(e.Data).Any(p => Directory.Exists(p));
        }

        /// <summary>
        /// Calculates the tab insert index based on mouse X position over tab bar items.
        /// Returns the index where the new/moved tab should be inserted (0.._tabs.Count).
        /// </summary>
        private int GetTabInsertIndex(System.Windows.DragEventArgs e)
        {
            if (TabBarPanel == null || _tabs.Count == 0)
                return _tabs.Count;

            var mousePos = e.GetPosition(TabBarPanel);
            double mouseX = mousePos.X;
            int tabIndex = 0;

            for (int i = 0; i < TabBarPanel.Children.Count; i++)
            {
                var child = TabBarPanel.Children[i];
                // Skip the insert indicator
                if (child == _tabInsertIndicator) continue;

                if (child is Border tabBorder)
                {
                    var tabPos = tabBorder.TranslatePoint(new Point(0, 0), TabBarPanel);
                    double tabCenter = tabPos.X + tabBorder.ActualWidth / 2;
                    if (mouseX < tabCenter)
                        return tabIndex;
                    tabIndex++;
                }
            }

            return _tabs.Count;
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

            // Calculate the visual insert position in the StackPanel
            // Tab items are at indices 0.._tabs.Count-1 in TabBarPanel
            int visualIndex = Math.Min(insertIndex, TabBarPanel.Children.Count);
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

            if (e.Data.GetDataPresent(TabDragFormat))
            {
                if (_tabs.Count > 1)
                {
                    int insertIdx = GetTabInsertIndex(e);
                    ShowInsertIndicator(insertIdx);
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
            HideInsertIndicator();
            TabDropPreview.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void TabBar_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (!IsHeaderTabDropCandidate(e)) return;

            if (e.Data.GetDataPresent(TabDragFormat))
            {
                var payload = _activeTabDragPayload;

                if (_tabs.Count > 1)
                {
                    int insertIdx = GetTabInsertIndex(e);

                    // For same-panel reorder, skip indicator right next to the dragged tab
                    if (payload != null &&
                        string.Equals(payload.SourcePanelId, PanelId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (insertIdx == payload.TabIndex || insertIdx == payload.TabIndex + 1)
                        {
                            HideInsertIndicator();
                            e.Effects = System.Windows.DragDropEffects.Move;
                            e.Handled = true;
                            return;
                        }
                    }

                    ShowInsertIndicator(insertIdx);
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
            int dropIndex = _tabInsertIndex >= 0 ? _tabInsertIndex : GetTabInsertIndex(e);
            HideInsertIndicator();
            TabDropPreview.Visibility = Visibility.Collapsed;

            // Handle tab drop
            if (e.Data.GetDataPresent(TabDragFormat))
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
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
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
                        TabId = Guid.NewGuid().ToString("N"),
                        TabName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? MainWindow.GetString("Loc.TabNewTab"),
                        PanelType = PanelKind.Folder.ToString(),
                        FolderPath = path,
                        DefaultFolderPath = path,
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
    }
}
