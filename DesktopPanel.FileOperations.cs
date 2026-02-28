using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        private const int SearchResultLimit = 80;
        private const int SearchMinCharsForDeepLookup = 2;
        private const int SearchFilterBatchSize = 220;
        private const int SearchResultBatchSize = 10;
        private const int FolderUiBatchSizeDefault = 8;
        private const int FolderUiBatchSizePhotos = 3;
        private const int FolderUiBatchDelayMs = 1;

        private bool IsSearchRequestCurrent(CancellationTokenSource cts)
        {
            return ReferenceEquals(_searchCts, cts);
        }

        private bool IsFolderLoadRequestCurrent(CancellationTokenSource cts, string folderPath)
        {
            return ReferenceEquals(_folderLoadCts, cts) &&
                PanelType == PanelKind.Folder &&
                string.Equals(currentFolderPath, folderPath, StringComparison.OrdinalIgnoreCase);
        }

        private CancellationTokenSource BeginFolderLoad()
        {
            var previousCts = _folderLoadCts;
            _folderLoadCts = null;
            previousCts?.Cancel();

            var currentCts = new CancellationTokenSource();
            _folderLoadCts = currentCts;
            return currentCts;
        }

        private void CancelPendingFolderLoad()
        {
            var pendingLoadCts = _folderLoadCts;
            _folderLoadCts = null;
            pendingLoadCts?.Cancel();
        }

        private bool MoveFolderIntoCurrent(string sourcePath, bool refreshAfterChange = true)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return false;

            string folderName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName)) return false;

            string targetPath = Path.Combine(currentFolderPath, folderName);

            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                return false;

            int counter = 1;
            string baseName = folderName;
            while (Directory.Exists(targetPath))
            {
                targetPath = Path.Combine(currentFolderPath, $"{baseName}_{counter++}");
            }

            try
            {
                try
                {
                    Directory.Move(sourcePath, targetPath);
                }
                catch (IOException)
                {
                    // Cross-volume move fallback for directory targets.
                    CopyDirectoryRecursive(sourcePath, targetPath);
                    Directory.Delete(sourcePath, true);
                }

                if (refreshAfterChange)
                {
                    LoadFolder(currentFolderPath, saveSettings: false);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFolderError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private bool MoveFileIntoCurrent(string sourcePath, bool refreshAfterChange = true)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return false;

            string fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            string targetPath = Path.Combine(currentFolderPath, fileName);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                return false;

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int counter = 1;

            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(currentFolderPath, $"{baseName}_{counter++}{extension}");
            }

            try
            {
                File.Move(sourcePath, targetPath);
                if (refreshAfterChange)
                {
                    LoadFolder(currentFolderPath, saveSettings: false);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFileError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private bool CopyFolderIntoCurrent(string sourcePath, bool refreshAfterChange = true)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return false;

            string folderName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName)) return false;

            string targetPath = Path.Combine(currentFolderPath, folderName);
            int counter = 1;
            string baseName = folderName;
            while (Directory.Exists(targetPath))
            {
                targetPath = Path.Combine(currentFolderPath, $"{baseName}_{counter++}");
            }

            try
            {
                CopyDirectoryRecursive(sourcePath, targetPath);
                if (refreshAfterChange)
                {
                    LoadFolder(currentFolderPath, saveSettings: false);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFolderError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private bool CopyFileIntoCurrent(string sourcePath, bool refreshAfterChange = true)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return false;

            string fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            string targetPath = Path.Combine(currentFolderPath, fileName);
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int counter = 1;

            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(currentFolderPath, $"{baseName}_{counter++}{extension}");
            }

            try
            {
                File.Copy(sourcePath, targetPath, overwrite: false);
                if (refreshAfterChange)
                {
                    LoadFolder(currentFolderPath, saveSettings: false);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFileError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private static void CopyDirectoryRecursive(string sourceDirectory, string targetDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException(sourceDirectory);
            }

            Directory.CreateDirectory(targetDirectory);
            foreach (var filePath in Directory.GetFiles(sourceDirectory))
            {
                string targetFile = Path.Combine(targetDirectory, Path.GetFileName(filePath));
                File.Copy(filePath, targetFile, overwrite: false);
            }

            foreach (var directoryPath in Directory.GetDirectories(sourceDirectory))
            {
                string targetSubDirectory = Path.Combine(targetDirectory, Path.GetFileName(directoryPath));
                CopyDirectoryRecursive(directoryPath, targetSubDirectory);
            }
        }

        private static bool TryGetPathAttributes(string path, out FileAttributes attributes)
        {
            attributes = default;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsHiddenPath(string path)
        {
            return TryGetPathAttributes(path, out var attributes) &&
                attributes.HasFlag(FileAttributes.Hidden);
        }

        private bool ShouldShowPath(string path)
        {
            if (!TryGetPathAttributes(path, out var attrs))
            {
                return true;
            }

            bool isSystem = attrs.HasFlag(FileAttributes.System);
            if (isSystem)
            {
                // Mirror default Windows Explorer behavior:
                // hidden files can be shown, protected system files stay hidden.
                return false;
            }

            bool isHidden = attrs.HasFlag(FileAttributes.Hidden);
            if (!showHiddenItems && isHidden)
            {
                return false;
            }

            return true;
        }

        private static string GetFolderDisplayName(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            string trimmedPath = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string folderName = Path.GetFileName(trimmedPath);
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                return folderName;
            }

            try
            {
                string fallback = new DirectoryInfo(folderPath).Name;
                return string.IsNullOrWhiteSpace(fallback) ? folderPath : fallback;
            }
            catch
            {
                return folderPath;
            }
        }

        private static string GetPathLeafName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leafName = Path.GetFileName(trimmedPath);
            return string.IsNullOrWhiteSpace(leafName) ? path : leafName;
        }

        private string GetDisplayNameForPath(string path)
        {
            string displayName = GetPathLeafName(path);
            if (string.IsNullOrWhiteSpace(displayName) || showFileExtensions)
            {
                return displayName;
            }

            bool isDirectory = false;
            try
            {
                isDirectory = Directory.Exists(path);
            }
            catch
            {
            }

            if (isDirectory)
            {
                return displayName;
            }

            string withoutExtension = Path.GetFileNameWithoutExtension(displayName);
            return string.IsNullOrWhiteSpace(withoutExtension) ? displayName : withoutExtension;
        }

        private void SetPanelTitleFromFolderPath(string folderPath)
        {
            string folderName = GetFolderDisplayName(folderPath);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return;
            }

            Title = folderName;
            if (PanelTitle != null)
            {
                PanelTitle.Text = folderName;
            }
        }

        public void LoadFolder(string folderPath, bool saveSettings = true, bool renamePanelTitle = false)
        {
            if (!Directory.Exists(folderPath)) return;

            var loadCts = BeginFolderLoad();
            ResetSearchState(clearSearchBox: true);
            PanelType = PanelKind.Folder;
            currentFolderPath = folderPath;
            PinnedItems.Clear();

            string? parentFolderPath = Path.GetDirectoryName(folderPath);
            this.Title = $"{GetFolderDisplayName(folderPath)}";
            if (renamePanelTitle)
            {
                SetPanelTitleFromFolderPath(folderPath);
            }

            FileList.Items.Clear();
            _baseItemPaths.Clear();
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();

            if (showParentNavigationItem && parentFolderPath != null)
            {
                string parentFolderName = BuildParentNavigationDisplayName(parentFolderPath);
                ListBoxItem backItem = new ListBoxItem
                {
                    Content = CreateListBoxItem(parentFolderName, parentFolderPath, true, _currentAppearance),
                    Tag = parentFolderPath
                };
                FileList.Items.Add(backItem);
                _baseItemPaths.Add(parentFolderPath);
            }

            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateDropZoneVisibility();
            _ = RunFolderLoadAsync(folderPath, loadCts);

            if (saveSettings)
            {
                MainWindow.SaveSettings();
            }
        }

        public void LoadList(IEnumerable<string> items, bool saveSettings = true)
        {
            CancelPendingFolderLoad();
            ResetSearchState(clearSearchBox: true);
            PanelType = PanelKind.List;
            currentFolderPath = "";
            FileList.Items.Clear();
            PinnedItems.Clear();
            _baseItemPaths.Clear();
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();

            foreach (var item in items)
            {
                AddFileToList(item, true);
            }

            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateDropZoneVisibility();

            if (saveSettings)
            {
                MainWindow.SaveSettings();
            }
        }

        public void ClearPanelItems()
        {
            CancelPendingFolderLoad();
            ResetSearchState(clearSearchBox: true);
            PanelType = PanelKind.None;
            currentFolderPath = "";
            PinnedItems.Clear();
            FileList.Items.Clear();
            _baseItemPaths.Clear();
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();
            UpdateDropZoneVisibility();
        }

        private async Task RunFolderLoadAsync(string folderPath, CancellationTokenSource cts)
        {
            var token = cts.Token;

            try
            {
                List<string> entries = await Task.Run(() => EnumerateVisibleFolderEntries(folderPath, token), token);
                token.ThrowIfCancellationRequested();

                if (!IsFolderLoadRequestCurrent(cts, folderPath) || entries.Count == 0)
                {
                    return;
                }

                int uiBatchSize = GetFolderLoadBatchSize();
                for (int start = 0; start < entries.Count; start += uiBatchSize)
                {
                    token.ThrowIfCancellationRequested();
                    string[] batch = entries
                        .Skip(start)
                        .Take(uiBatchSize)
                        .ToArray();

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsFolderLoadRequestCurrent(cts, folderPath))
                        {
                            return;
                        }

                        string activeFilter = SearchBox?.Text?.Trim() ?? string.Empty;
                        bool hasFilter = !string.IsNullOrWhiteSpace(activeFilter);
                        foreach (string entryPath in batch)
                        {
                            string displayName = GetDisplayNameForPath(entryPath);
                            if (string.IsNullOrWhiteSpace(displayName))
                            {
                                displayName = entryPath;
                            }

                            var listItem = new ListBoxItem
                            {
                                Content = CreateListBoxItem(displayName, entryPath, false, _currentAppearance),
                                Tag = entryPath
                            };

                            if (hasFilter &&
                                displayName.IndexOf(activeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                listItem.Visibility = Visibility.Collapsed;
                            }

                            FileList.Items.Add(listItem);
                            _baseItemPaths.Add(entryPath);
                        }
                    }, System.Windows.Threading.DispatcherPriority.ContextIdle, token);

                    if (start + uiBatchSize < entries.Count)
                    {
                        await Task.Delay(FolderUiBatchDelayMs, token);
                    }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsFolderLoadRequestCurrent(cts, folderPath))
                    {
                        return;
                    }

                    SortCurrentFolderItemsInPlace();
                    _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
                    UpdateDropZoneVisibility();
                }, System.Windows.Threading.DispatcherPriority.Background, token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(_folderLoadCts, cts))
                {
                    _folderLoadCts = null;
                }

                cts.Dispose();
            }
        }

        private int GetFolderLoadBatchSize()
        {
            string normalizedViewMode = NormalizeViewMode(viewMode);
            if (string.Equals(normalizedViewMode, ViewModePhotos, StringComparison.OrdinalIgnoreCase))
            {
                return FolderUiBatchSizePhotos;
            }

            return FolderUiBatchSizeDefault;
        }

        private List<string> EnumerateVisibleFolderEntries(string folderPath, CancellationToken token)
        {
            var entries = new List<string>(256);
            try
            {
                foreach (string directoryPath in Directory.EnumerateDirectories(folderPath))
                {
                    token.ThrowIfCancellationRequested();
                    if (ShouldShowPath(directoryPath))
                    {
                        entries.Add(directoryPath);
                    }
                }

                foreach (string filePath in Directory.EnumerateFiles(folderPath))
                {
                    token.ThrowIfCancellationRequested();
                    if (ShouldShowPath(filePath))
                    {
                        entries.Add(filePath);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            return entries;
        }

        public void AppendItemsToList(IEnumerable<string> filePaths, bool animateEntries)
        {
            if (filePaths == null)
            {
                return;
            }

            if (PanelType != PanelKind.List)
            {
                LoadList(Array.Empty<string>(), saveSettings: false);
            }

            int animationOrder = 0;
            bool addedAny = false;
            foreach (string path in filePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                bool added = AddFileToList(path, trackItem: true, entryAnimationOrder: animateEntries ? animationOrder : -1);
                if (added)
                {
                    addedAny = true;
                    animationOrder++;
                }
            }

            if (!addedAny)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
            UpdateDropZoneVisibility();
        }

        public void AnimateListItemsForPaths(IEnumerable<string> filePaths)
        {
            if (filePaths == null || FileList == null)
            {
                return;
            }

            var targetPaths = new HashSet<string>(
                filePaths.Where(p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);
            if (targetPaths.Count == 0)
            {
                return;
            }

            int animationOrder = 0;
            foreach (ListBoxItem item in FileList.Items.OfType<ListBoxItem>())
            {
                if (item.Tag is not string path || !targetPaths.Contains(path))
                {
                    continue;
                }

                AnimateListItemEntry(item, animationOrder++);
            }
        }

        private bool AddFileToList(string filePath, bool trackItem, int entryAnimationOrder = -1)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            if (trackItem)
            {
                if (PinnedItems.Any(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
                if (PanelType == PanelKind.None)
                {
                    PanelType = PanelKind.List;
                }
                PinnedItems.Add(filePath);
            }

            string displayName = GetDisplayNameForPath(filePath);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = filePath;
            }

            ListBoxItem item = new ListBoxItem
            {
                Content = CreateListBoxItem(displayName, filePath, false, _currentAppearance),
                Tag = filePath
            };
            FileList.Items.Add(item);
            _baseItemPaths.Add(filePath);
            if (entryAnimationOrder >= 0)
            {
                AnimateListItemEntry(item, entryAnimationOrder);
            }

            return true;
        }

        private void AnimateListItemEntry(ListBoxItem item, int order)
        {
            if (item.Content is not UIElement content)
            {
                return;
            }

            var translate = new TranslateTransform();
            var scale = new ScaleTransform(0.94, 0.94);
            var transforms = new TransformGroup();
            transforms.Children.Add(scale);
            transforms.Children.Add(translate);

            content.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            content.RenderTransform = transforms;
            content.BeginAnimation(UIElement.OpacityProperty, null);
            content.Opacity = 0;

            double panelCenterX = Left + (ActualWidth > 0 ? ActualWidth : Width) / 2.0;
            double screenCenterX = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width / 2.0);
            double fromX = panelCenterX >= screenCenterX ? 54 : -54;

            var delay = TimeSpan.FromMilliseconds(Math.Min(320, Math.Max(0, order) * 26));
            var moveEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };

            var moveX = new DoubleAnimation
            {
                From = fromX,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(340),
                BeginTime = delay,
                EasingFunction = moveEase
            };
            var moveY = new DoubleAnimation
            {
                From = -16,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(340),
                BeginTime = delay,
                EasingFunction = moveEase
            };
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(240),
                BeginTime = delay,
                EasingFunction = fadeEase
            };
            var scaleX = new DoubleAnimation
            {
                From = 0.94,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(320),
                BeginTime = delay,
                EasingFunction = moveEase
            };
            var scaleY = new DoubleAnimation
            {
                From = 0.94,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(320),
                BeginTime = delay,
                EasingFunction = moveEase
            };

            translate.BeginAnimation(TranslateTransform.XProperty, moveX, HandoffBehavior.SnapshotAndReplace);
            translate.BeginAnimation(TranslateTransform.YProperty, moveY, HandoffBehavior.SnapshotAndReplace);
            content.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY, HandoffBehavior.SnapshotAndReplace);
        }

        private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_renameEditBox != null)
            {
                e.Handled = true;
                return;
            }

            if (FileList.SelectedItem is ListBoxItem selectedItem && selectedItem.Tag is string path)
            {
                if (PanelType == PanelKind.List)
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        string msgKey = Directory.Exists(path) ? "Loc.MsgOpenFolderError" : "Loc.MsgOpenFileError";
                        System.Windows.MessageBox.Show(
                            string.Format(MainWindow.GetString(msgKey), ex.Message),
                            MainWindow.GetString("Loc.MsgError"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    return;
                }

                if (Directory.Exists(path))
                {
                    if (openFoldersExternally)
                    {
                        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                        catch (Exception ex)
                        {
                            System.Windows.MessageBox.Show(
                                string.Format(MainWindow.GetString("Loc.MsgOpenFolderError"), ex.Message),
                                MainWindow.GetString("Loc.MsgError"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                    }
                    else
                    {
                        LoadFolder(path);
                    }
                }
                else if (File.Exists(path))
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show(
                            string.Format(MainWindow.GetString("Loc.MsgOpenFileError"), ex.Message),
                            MainWindow.GetString("Loc.MsgError"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSearchTextChanged)
            {
                return;
            }

            string currentSearchText = SearchBox?.Text ?? string.Empty;
            if (!isContentVisible &&
                !_isCollapseAnimationRunning &&
                !string.IsNullOrWhiteSpace(currentSearchText))
            {
                ToggleCollapseAnimated();
            }

            BeginSearch(currentSearchText);
        }

        private void SearchClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchBox == null)
            {
                return;
            }

            SearchBox.Clear();
            SearchBox.Focus();
        }

        private void BeginSearch(string rawFilter)
        {
            var previousCts = _searchCts;
            _searchCts = null;
            previousCts?.Cancel();

            var currentCts = new CancellationTokenSource();
            _searchCts = currentCts;
            _ = RunSearchAsync(rawFilter ?? string.Empty, currentCts);
        }

        private async Task RunSearchAsync(string rawFilter, CancellationTokenSource cts)
        {
            var token = cts.Token;

            try
            {
                // Debounce keystrokes so typing stays responsive.
                await Task.Delay(260, token);
                string filter = rawFilter.Trim();
                await ApplyLocalSearchFilterAsync(filter, cts, token);

                if (!IsSearchRequestCurrent(cts) ||
                    string.IsNullOrWhiteSpace(filter) ||
                    filter.Length < SearchMinCharsForDeepLookup ||
                    PanelType != PanelKind.Folder ||
                    string.IsNullOrWhiteSpace(currentFolderPath) ||
                    !Directory.Exists(currentFolderPath))
                {
                    return;
                }

                FileSearchIndex.EnsureStarted();

                List<string> results = FileSearchIndex.IsReady
                    ? await FileSearchIndex.SearchAsync(currentFolderPath, filter, SearchResultLimit, token)
                    : await Task.Run(() => EnumerateMatches(currentFolderPath, filter, token), token);

                token.ThrowIfCancellationRequested();
                if (!IsSearchRequestCurrent(cts) || results.Count == 0)
                {
                    return;
                }

                await AppendInjectedSearchResultsAsync(results, cts, token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                bool shouldApplyDeferredSort = ReferenceEquals(_searchCts, cts) && _deferSortUntilSearchComplete;
                if (ReferenceEquals(_searchCts, cts))
                {
                    _searchCts = null;
                }

                if (shouldApplyDeferredSort)
                {
                    _deferSortUntilSearchComplete = false;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        SortCurrentFolderItemsInPlace();
                        RefreshParentNavigationItemVisual();
                        _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }

                cts.Dispose();
            }
        }

        private void ResetSearchState(bool clearSearchBox)
        {
            var pendingSearchCts = _searchCts;
            _searchCts = null;
            pendingSearchCts?.Cancel();
            _deferSortUntilSearchComplete = false;
            RemoveInjectedSearchItems();

            if (!clearSearchBox || SearchBox == null || string.IsNullOrEmpty(SearchBox.Text))
            {
                return;
            }

            _suppressSearchTextChanged = true;
            try
            {
                SearchBox.Text = string.Empty;
            }
            finally
            {
                _suppressSearchTextChanged = false;
            }
        }

        private void RestoreUnfilteredPanelItems()
        {
            RemoveInjectedSearchItems();
            foreach (var item in FileList.Items.OfType<ListBoxItem>())
            {
                item.Visibility = Visibility.Visible;
            }

            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task ApplyLocalSearchFilterAsync(string filter, CancellationTokenSource cts, CancellationToken token)
        {
            List<ListBoxItem> items = await Dispatcher.InvokeAsync(() =>
            {
                if (!IsSearchRequestCurrent(cts))
                {
                    return new List<ListBoxItem>();
                }

                RemoveInjectedSearchItems();
                return FileList.Items.OfType<ListBoxItem>().ToList();
            }, System.Windows.Threading.DispatcherPriority.Background, token);

            if (!IsSearchRequestCurrent(cts) || items.Count == 0)
            {
                return;
            }

            bool showAll = string.IsNullOrWhiteSpace(filter);
            for (int start = 0; start < items.Count; start += SearchFilterBatchSize)
            {
                token.ThrowIfCancellationRequested();
                int end = Math.Min(items.Count, start + SearchFilterBatchSize);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSearchRequestCurrent(cts))
                    {
                        return;
                    }

                    for (int i = start; i < end; i++)
                    {
                        var item = items[i];
                        bool isVisible = IsParentNavigationItem(item) ||
                            showAll ||
                            GetSearchCandidateText(item).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                        var target = isVisible ? Visibility.Visible : Visibility.Collapsed;
                        if (item.Visibility != target)
                        {
                            item.Visibility = target;
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Background, token);

                if (end < items.Count)
                {
                    await Task.Delay(1, token);
                }
            }

            SortCurrentFolderItemsInPlace();
            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RemoveInjectedSearchItems()
        {
            if (_searchInjectedItems.Count == 0)
            {
                _searchInjectedPaths.Clear();
                return;
            }

            foreach (var item in _searchInjectedItems)
            {
                FileList.Items.Remove(item);
            }

            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();
        }

        private async Task AppendInjectedSearchResultsAsync(IEnumerable<string> results, CancellationTokenSource cts, CancellationToken token)
        {
            var candidatePaths = results
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(SearchResultLimit)
                .ToList();

            if (candidatePaths.Count == 0)
            {
                return;
            }

            for (int start = 0; start < candidatePaths.Count; start += SearchResultBatchSize)
            {
                token.ThrowIfCancellationRequested();
                string[] batch = candidatePaths
                    .Skip(start)
                    .Take(SearchResultBatchSize)
                    .ToArray();

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSearchRequestCurrent(cts))
                    {
                        return;
                    }

                    foreach (var foundPath in batch)
                    {
                        if (!ShouldShowPath(foundPath) ||
                            _baseItemPaths.Contains(foundPath) ||
                            _searchInjectedPaths.Contains(foundPath))
                        {
                            continue;
                        }

                        string displayName = GetDisplayNameForPath(foundPath);
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            displayName = foundPath;
                        }

                        var listItem = new ListBoxItem
                        {
                            Content = CreateListBoxItem(displayName, foundPath, false, _currentAppearance),
                            Tag = foundPath
                        };

                        FileList.Items.Add(listItem);
                        _searchInjectedPaths.Add(foundPath);
                        _searchInjectedItems.Add(listItem);
                    }
                }, System.Windows.Threading.DispatcherPriority.ContextIdle, token);

                if (start + SearchResultBatchSize < candidatePaths.Count)
                {
                    await Task.Delay(1, token);
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsSearchRequestCurrent(cts))
                {
                    return;
                }

                SortCurrentFolderItemsInPlace();
                RefreshParentNavigationItemVisual();
                _deferSortUntilSearchComplete = false;
            }, System.Windows.Threading.DispatcherPriority.Background, token);

            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
        }

        private string GetSearchCandidateText(ListBoxItem item)
        {
            if (item.Tag is string path && !string.IsNullOrWhiteSpace(path))
            {
                string displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.IsNullOrWhiteSpace(displayName) ? path : displayName;
            }

            if (TryGetItemNameLabel(item, out var text) &&
                !string.IsNullOrWhiteSpace(text.Text))
            {
                return text.Text;
            }

            return string.Empty;
        }

        private List<string> EnumerateMatches(string root, string filter, CancellationToken token)
        {
            var list = new List<string>(Math.Min(SearchResultLimit, 32));
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (!ShouldShowPath(entry)) continue;
                    var name = Path.GetFileName(entry);
                    if (name != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        list.Add(entry);
                        if (list.Count >= SearchResultLimit) break;
                    }
                }
            }
            catch { }
            return list;
        }
    }
}

