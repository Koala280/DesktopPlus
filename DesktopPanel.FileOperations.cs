using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        private const int SearchResultLimit = 80;
        private const int SearchMinCharsForDeepLookup = 2;
        private const int SearchFilterBatchSize = 220;
        private const int SearchResultBatchSize = 10;

        private bool IsSearchRequestCurrent(CancellationTokenSource cts)
        {
            return ReferenceEquals(_searchCts, cts);
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

            if (parentFolderPath != null)
            {
                string parentFolderName = GetDisplayNameForPath(parentFolderPath);
                ListBoxItem backItem = new ListBoxItem
                {
                    Content = CreateListBoxItem("↩ " + parentFolderName, parentFolderPath, true, _currentAppearance),
                    Tag = parentFolderPath
                };
                FileList.Items.Add(backItem);
                _baseItemPaths.Add(parentFolderPath);
            }

            foreach (var dir in Directory.GetDirectories(folderPath).Where(ShouldShowPath))
            {
                FileList.Items.Add(new ListBoxItem { Content = CreateListBoxItem(GetDisplayNameForPath(dir), dir, false, _currentAppearance), Tag = dir });
                _baseItemPaths.Add(dir);
            }
            foreach (var file in Directory.GetFiles(folderPath).Where(ShouldShowPath))
            {
                FileList.Items.Add(new ListBoxItem { Content = CreateListBoxItem(GetDisplayNameForPath(file), file, false, _currentAppearance), Tag = file });
                _baseItemPaths.Add(file);
            }

            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateDropZoneVisibility();

            if (saveSettings)
            {
                MainWindow.SaveSettings();
            }
        }

        public void LoadList(IEnumerable<string> items, bool saveSettings = true)
        {
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

        private void AddFileToList(string filePath, bool trackItem)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            if (trackItem)
            {
                if (PinnedItems.Any(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
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
                if (ReferenceEquals(_searchCts, cts))
                {
                    _searchCts = null;
                }

                cts.Dispose();
            }
        }

        private void ResetSearchState(bool clearSearchBox)
        {
            var pendingSearchCts = _searchCts;
            _searchCts = null;
            pendingSearchCts?.Cancel();
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
                        bool isVisible = showAll ||
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

            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static string GetSearchCandidateText(ListBoxItem item)
        {
            if (item.Tag is string path && !string.IsNullOrWhiteSpace(path))
            {
                string displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.IsNullOrWhiteSpace(displayName) ? path : displayName;
            }

            if (item.Content is StackPanel panel &&
                panel.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock text &&
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
