using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPlus
{
    public partial class DesktopPanel
    {
        private static readonly NotifyFilters FolderWatcherNotifyFilters =
            NotifyFilters.FileName |
            NotifyFilters.DirectoryName |
            NotifyFilters.Attributes |
            NotifyFilters.CreationTime |
            NotifyFilters.LastWrite |
            NotifyFilters.Size;

        private static readonly string[] TransientDownloadExtensions =
        {
            ".crdownload",
            ".part",
            ".partial",
            ".tmp",
            ".temp",
            ".download",
            ".opdownload",
            ".filepart",
            ".!qb"
        };

        private enum FolderWatcherChangeKind
        {
            Created,
            Deleted,
            Renamed,
            Changed
        }

        private readonly record struct FolderWatcherChange(
            FolderWatcherChangeKind Kind,
            string? FullPath,
            string? OldFullPath);

        private static bool ArePathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                string normalizedLeft = Path.GetFullPath(left)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedRight = Path.GetFullPath(right)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsTransientDownloadPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string fileName;
            try
            {
                fileName = Path.GetFileName(path);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return TransientDownloadExtensions.Any(candidate =>
                string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ShouldIgnoreContentWatcherEvent(FileSystemEventArgs e)
        {
            if (e == null)
            {
                return true;
            }

            if (e is RenamedEventArgs renamed)
            {
                bool oldIsTransient = IsTransientDownloadPath(renamed.OldFullPath);
                bool newIsTransient = IsTransientDownloadPath(renamed.FullPath);
                // Keep refresh when a temp download is finalized (.part -> .zip).
                return oldIsTransient && newIsTransient;
            }

            return IsTransientDownloadPath(e.FullPath);
        }

        private bool ShouldRefreshVisibleFolderItemsForWatcherPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            try
            {
                string normalizedFolder = Path.GetFullPath(currentFolderPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedPath = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? parentPath = Path.GetDirectoryName(normalizedPath)?
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Only refresh the visible panel when a direct child of the bound folder changed.
                // Descendant changes inside nested subfolders should update the search index, but
                // must not force a full visible-folder reload and flicker the panel.
                return ArePathsEqual(parentPath, normalizedFolder);
            }
            catch
            {
                return true;
            }
        }

        private void HandleFolderContentWatcherChange(
            FolderWatcherChangeKind kind,
            string? fullPath,
            string? oldFullPath = null)
        {
            if (PanelType != PanelKind.Folder || string.IsNullOrWhiteSpace(currentFolderPath))
            {
                return;
            }

            ShellPropertyReader.InvalidatePath(fullPath);
            ShellPropertyReader.InvalidatePath(oldFullPath);
            InvalidateFolderSearchIndex(currentFolderPath, rebuildInBackground: true, rerunActiveSearch: true);

            bool refreshVisibleItems =
                ShouldRefreshVisibleFolderItemsForWatcherPath(fullPath) ||
                ShouldRefreshVisibleFolderItemsForWatcherPath(oldFullPath);
            if (refreshVisibleItems)
            {
                EnqueueFolderWatcherChange(kind, fullPath, oldFullPath);
                QueueFolderRefreshFromWatcher();
            }
        }

        private void NotifyFolderContentChangeImmediate(
            FolderWatcherChangeKind kind,
            string? fullPath,
            string? oldFullPath = null)
        {
            HandleFolderContentWatcherChange(kind, fullPath, oldFullPath);
            QueueFolderRefreshFromWatcher(immediate: true);
        }

        private void EnqueueFolderWatcherChange(
            FolderWatcherChangeKind kind,
            string? fullPath,
            string? oldFullPath = null)
        {
            lock (_pendingFolderWatcherChangesLock)
            {
                _pendingFolderWatcherChanges.Add(new FolderWatcherChange(kind, fullPath, oldFullPath));
            }
        }

        private void RequireFullFolderWatcherRefresh()
        {
            lock (_pendingFolderWatcherChangesLock)
            {
                _folderWatcherRequiresFullRefresh = true;
                _pendingFolderWatcherChanges.Clear();
            }
        }

        private (bool RequiresFullRefresh, List<FolderWatcherChange> Changes) TakePendingFolderWatcherChanges()
        {
            lock (_pendingFolderWatcherChangesLock)
            {
                bool requiresFullRefresh = _folderWatcherRequiresFullRefresh;
                _folderWatcherRequiresFullRefresh = false;

                var changes = _pendingFolderWatcherChanges.Count == 0
                    ? new List<FolderWatcherChange>()
                    : new List<FolderWatcherChange>(_pendingFolderWatcherChanges);
                _pendingFolderWatcherChanges.Clear();
                return (requiresFullRefresh, changes);
            }
        }

        private void StartOrUpdateFolderWatchers(string folderPath)
        {
            if (PanelType != PanelKind.Folder || string.IsNullOrWhiteSpace(folderPath))
            {
                StopFolderWatchers();
                return;
            }

            string normalizedFolderPath;
            try
            {
                normalizedFolderPath = Path.GetFullPath(folderPath);
            }
            catch
            {
                StopFolderWatchers();
                return;
            }

            ConfigureParentFolderWatcher(normalizedFolderPath);
            ConfigureContentFolderWatcher(normalizedFolderPath);
        }

        private void ConfigureParentFolderWatcher(string normalizedFolderPath)
        {
            string? parentPath;
            try
            {
                parentPath = Path.GetDirectoryName(
                    normalizedFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                parentPath = null;
            }

            if (string.IsNullOrWhiteSpace(parentPath) || !Directory.Exists(parentPath))
            {
                StopParentFolderWatcher();
                return;
            }

            if (_folderParentWatcher != null &&
                string.Equals(_folderParentWatcher.Path, parentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopParentFolderWatcher();

            try
            {
                _folderParentWatcher = new FileSystemWatcher(parentPath)
                {
                    Filter = "*",
                    IncludeSubdirectories = false,
                    NotifyFilter = FolderWatcherNotifyFilters,
                    EnableRaisingEvents = true
                };

                _folderParentWatcher.Created += FolderParentWatcher_Created;
                _folderParentWatcher.Deleted += FolderParentWatcher_Deleted;
                _folderParentWatcher.Renamed += FolderParentWatcher_Renamed;
                _folderParentWatcher.Error += FolderWatcher_Error;
            }
            catch
            {
                StopParentFolderWatcher();
            }
        }

        private void ConfigureContentFolderWatcher(string normalizedFolderPath)
        {
            if (!Directory.Exists(normalizedFolderPath))
            {
                StopContentFolderWatcher();
                return;
            }

            if (_folderContentWatcher != null &&
                string.Equals(_folderContentWatcher.Path, normalizedFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopContentFolderWatcher();

            try
            {
                _folderContentWatcher = new FileSystemWatcher(normalizedFolderPath)
                {
                    Filter = "*",
                    IncludeSubdirectories = true,
                    NotifyFilter = FolderWatcherNotifyFilters,
                    EnableRaisingEvents = true
                };

                _folderContentWatcher.Created += FolderContentWatcher_Created;
                _folderContentWatcher.Changed += FolderContentWatcher_Changed;
                _folderContentWatcher.Deleted += FolderContentWatcher_Deleted;
                _folderContentWatcher.Renamed += FolderContentWatcher_Renamed;
                _folderContentWatcher.Error += FolderWatcher_Error;
            }
            catch
            {
                StopContentFolderWatcher();
            }
        }

        private void StopParentFolderWatcher()
        {
            var previous = _folderParentWatcher;
            _folderParentWatcher = null;
            if (previous == null)
            {
                return;
            }

            try
            {
                previous.EnableRaisingEvents = false;
                previous.Created -= FolderParentWatcher_Created;
                previous.Deleted -= FolderParentWatcher_Deleted;
                previous.Renamed -= FolderParentWatcher_Renamed;
                previous.Error -= FolderWatcher_Error;
                previous.Dispose();
            }
            catch
            {
            }
        }

        private void StopContentFolderWatcher()
        {
            var previous = _folderContentWatcher;
            _folderContentWatcher = null;
            if (previous == null)
            {
                return;
            }

            try
            {
                previous.EnableRaisingEvents = false;
                previous.Created -= FolderContentWatcher_Created;
                previous.Changed -= FolderContentWatcher_Changed;
                previous.Deleted -= FolderContentWatcher_Deleted;
                previous.Renamed -= FolderContentWatcher_Renamed;
                previous.Error -= FolderWatcher_Error;
                previous.Dispose();
            }
            catch
            {
            }
        }

        private void StopFolderWatchers()
        {
            StopContentFolderWatcher();
            StopParentFolderWatcher();
        }

        private void FolderContentWatcher_Created(object sender, FileSystemEventArgs e)
        {
            if (ShouldIgnoreContentWatcherEvent(e))
            {
                return;
            }

            HandleFolderContentWatcherChange(FolderWatcherChangeKind.Created, e.FullPath);
        }

        private void FolderContentWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (ShouldIgnoreContentWatcherEvent(e))
            {
                return;
            }

            HandleFolderContentWatcherChange(FolderWatcherChangeKind.Changed, e.FullPath);
        }

        private void FolderContentWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            if (ShouldIgnoreContentWatcherEvent(e))
            {
                return;
            }

            HandleFolderContentWatcherChange(FolderWatcherChangeKind.Deleted, e.FullPath);
        }

        private void FolderContentWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            if (ShouldIgnoreContentWatcherEvent(e))
            {
                return;
            }

            HandleFolderContentWatcherChange(FolderWatcherChangeKind.Renamed, e.FullPath, e.OldFullPath);
        }

        private void FolderParentWatcher_Created(object sender, FileSystemEventArgs e)
        {
            if (!ArePathsEqual(e.FullPath, currentFolderPath))
            {
                return;
            }

            InvalidateFolderSearchIndex(currentFolderPath, rebuildInBackground: true, rerunActiveSearch: true);
            RequireFullFolderWatcherRefresh();
            QueueFolderRefreshFromWatcher(immediate: true);
        }

        private void FolderParentWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            if (!ArePathsEqual(e.FullPath, currentFolderPath))
            {
                return;
            }

            InvalidateFolderSearchIndex(currentFolderPath, rebuildInBackground: false, rerunActiveSearch: true);
            RequireFullFolderWatcherRefresh();
            QueueFolderRefreshFromWatcher(immediate: true);
        }

        private void FolderParentWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            if (!ArePathsEqual(e.OldFullPath, currentFolderPath))
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PanelType != PanelKind.Folder ||
                    string.IsNullOrWhiteSpace(currentFolderPath) ||
                    !ArePathsEqual(e.OldFullPath, currentFolderPath))
                {
                    return;
                }

                currentFolderPath = e.FullPath ?? currentFolderPath;
                if (string.Equals(defaultFolderPath, e.OldFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    defaultFolderPath = currentFolderPath;
                }

                InvalidateFolderSearchIndex(e.OldFullPath ?? currentFolderPath, rebuildInBackground: false, rerunActiveSearch: false);
                InvalidateFolderSearchIndex(currentFolderPath, rebuildInBackground: true, rerunActiveSearch: true);
                SetPanelTitleFromFolderPath(currentFolderPath);
                StartOrUpdateFolderWatchers(currentFolderPath);
                RequireFullFolderWatcherRefresh();
                QueueFolderRefreshFromWatcher(immediate: true);
            }), DispatcherPriority.Background);
        }

        private void FolderWatcher_Error(object sender, ErrorEventArgs e)
        {
            RequireFullFolderWatcherRefresh();
            QueueFolderRefreshFromWatcher();
        }

        private void QueueFolderRefreshFromWatcher(bool immediate = false)
        {
            if (PanelType != PanelKind.Folder || string.IsNullOrWhiteSpace(currentFolderPath))
            {
                return;
            }

            var pending = Interlocked.Exchange(ref _folderWatcherRefreshCts, new CancellationTokenSource());
            pending?.Cancel();
            pending?.Dispose();

            var cts = _folderWatcherRefreshCts;
            int delayMs = immediate ? 0 : 180;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cts!.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    return;
                }

                if (cts == null || cts.Token.IsCancellationRequested)
                {
                    return;
                }

                _ = Dispatcher.BeginInvoke(new Action(ApplyFolderRefreshFromWatcher), DispatcherPriority.Background);
            });
        }

        private void ApplyFolderRefreshFromWatcher()
        {
            if (FileList == null ||
                PanelType != PanelKind.Folder ||
                string.IsNullOrWhiteSpace(currentFolderPath))
            {
                return;
            }

            if (Directory.Exists(currentFolderPath))
            {
                if (TryApplyLightweightFolderRefreshFromWatcher())
                {
                    return;
                }

                LoadFolder(currentFolderPath, saveSettings: false);
                return;
            }

            // Keep folder mode/path, but clear stale entries immediately when target folder is gone.
            CancelPendingFolderLoad();
            FileList.Items.Clear();
            _baseItemPaths.Clear();
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();
            UpdateDropZoneVisibility();

            // Parent watcher stays active and can catch recreation/rename of the bound folder.
            StartOrUpdateFolderWatchers(currentFolderPath);
        }

        private bool TryApplyLightweightFolderRefreshFromWatcher()
        {
            if (FileList == null ||
                PanelType != PanelKind.Folder ||
                string.IsNullOrWhiteSpace(currentFolderPath) ||
                !Directory.Exists(currentFolderPath))
            {
                return false;
            }

            var pendingRefresh = TakePendingFolderWatcherChanges();
            if (pendingRefresh.RequiresFullRefresh)
            {
                return false;
            }

            if (_folderLoadCts != null)
            {
                return false;
            }

            if (pendingRefresh.Changes.Count == 0)
            {
                return true;
            }

            if (SearchBox != null && !string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                bool changedAny = false;
                foreach (var change in pendingRefresh.Changes)
                {
                    changedAny |= TryApplyFolderWatcherChange(change);
                }

                if (changedAny)
                {
                    RefreshParentNavigationItemVisual();
                    RebuildListItemVisuals(sortItems: true);
                }

                return true;
            }

            bool changed = false;
            foreach (var change in pendingRefresh.Changes)
            {
                changed |= TryApplyFolderWatcherChange(change);
            }

            if (changed)
            {
                RefreshParentNavigationItemVisual();
                ApplyFolderWatcherItemOrder();
                RefreshDetailsHeader();
                if (CurrentViewNeedsContentLayoutRefresh())
                {
                    QueueWrapPanelWidthUpdate();
                }
                UpdateDropZoneVisibility();
                UpdateEmptyRecycleBinButtonVisibility();
            }

            return true;
        }

        private bool TryApplyFolderWatcherChange(FolderWatcherChange change)
        {
            switch (change.Kind)
            {
                case FolderWatcherChangeKind.Created:
                    return EnsureFolderWatcherItemState(change.FullPath);
                case FolderWatcherChangeKind.Deleted:
                    return RemoveFolderWatcherItem(change.FullPath);
                case FolderWatcherChangeKind.Renamed:
                    return ApplyFolderWatcherRename(change.OldFullPath, change.FullPath);
                case FolderWatcherChangeKind.Changed:
                    return EnsureFolderWatcherItemState(change.FullPath);
                default:
                    return false;
            }
        }

        private bool ApplyFolderWatcherRename(string? oldPath, string? newPath)
        {
            bool renamedVisibleItem = TryRenameFolderWatcherVisibleItem(oldPath, newPath);
            if (renamedVisibleItem)
            {
                return true;
            }

            bool removedOld = RemoveFolderWatcherItem(oldPath);
            bool ensuredNew = EnsureFolderWatcherItemState(newPath);
            return removedOld || ensuredNew;
        }

        private bool EnsureFolderWatcherItemState(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var existingItem = FindFolderWatcherItem(path);
            if (!CanDisplayFolderWatcherPath(path))
            {
                return existingItem != null && RemoveFolderWatcherItem(path);
            }

            if (existingItem != null)
            {
                RefreshFolderWatcherItem(existingItem, path);
                return true;
            }

            string displayName = GetDisplayNameForPath(path);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = GetPathLeafName(path);
            }

            var newItem = CreateFileListBoxItem(
                displayName,
                path,
                isBackButton: false,
                _currentAppearance);

            InsertFolderWatcherItem(newItem, path);
            _baseItemPaths.Add(path);
            InsertFolderWatcherDefaultOrderPath(path);
            return true;
        }

        private bool RemoveFolderWatcherItem(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            bool changed = false;
            var existingItem = FindFolderWatcherItem(path);
            if (existingItem != null && FileList != null)
            {
                FileList.Items.Remove(existingItem);
                _searchInjectedItems.Remove(existingItem);
                changed = true;
            }

            return RemoveFolderWatcherTrackedPath(path) || changed;
        }

        private bool RemoveFolderWatcherTrackedPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            bool changed = false;
            changed |= _baseItemPaths.Remove(path);
            changed |= _searchInjectedPaths.Remove(path);
            changed |= _detailsDefaultOrderPaths.RemoveAll(existing =>
                ArePathsEqual(existing, path)) > 0;
            return changed;
        }

        private bool TryRenameFolderWatcherVisibleItem(string? oldPath, string? newPath)
        {
            if (string.IsNullOrWhiteSpace(oldPath) ||
                string.IsNullOrWhiteSpace(newPath) ||
                !CanDisplayFolderWatcherPath(newPath))
            {
                return false;
            }

            var existingItem = FindFolderWatcherItem(oldPath);
            if (existingItem == null)
            {
                return false;
            }

            var duplicateTargetItem = FindFolderWatcherItem(newPath);
            if (duplicateTargetItem != null && !ReferenceEquals(duplicateTargetItem, existingItem))
            {
                FileList?.Items.Remove(duplicateTargetItem);
                _searchInjectedItems.Remove(duplicateTargetItem);
            }

            existingItem.Tag = newPath;
            RefreshFolderWatcherItem(existingItem, newPath);

            _baseItemPaths.Remove(oldPath);
            _baseItemPaths.Add(newPath);
            _searchInjectedPaths.Remove(oldPath);
            _searchInjectedPaths.Remove(newPath);

            int existingIndex = _detailsDefaultOrderPaths.FindIndex(existing =>
                ArePathsEqual(existing, oldPath));
            if (existingIndex >= 0)
            {
                _detailsDefaultOrderPaths[existingIndex] = newPath;
            }
            else
            {
                InsertFolderWatcherDefaultOrderPath(newPath);
            }

            return true;
        }

        private void RefreshFolderWatcherItem(System.Windows.Controls.ListBoxItem item, string path)
        {
            string displayName = GetDisplayNameForPath(path);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = GetPathLeafName(path);
            }

            item.Tag = path;
            item.Content = CreateListBoxItem(displayName, path, isBackButton: false, _currentAppearance);
            item.Focusable = true;
            ApplyListItemContainerSpacing(item);
            item.Visibility = System.Windows.Visibility.Visible;
        }

        private System.Windows.Controls.ListBoxItem? FindFolderWatcherItem(string? path)
        {
            if (FileList == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            foreach (var item in FileList.Items.OfType<System.Windows.Controls.ListBoxItem>())
            {
                if (item.Tag is string itemPath && ArePathsEqual(itemPath, path))
                {
                    return item;
                }
            }

            return null;
        }

        private bool CanDisplayFolderWatcherPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !ShouldRefreshVisibleFolderItemsForWatcherPath(path))
            {
                return false;
            }

            try
            {
                return (Directory.Exists(path) || File.Exists(path)) && ShouldShowPath(path);
            }
            catch
            {
                return false;
            }
        }

        private void InsertFolderWatcherItem(System.Windows.Controls.ListBoxItem item, string path)
        {
            if (FileList == null)
            {
                return;
            }

            int insertIndex = FileList.Items.Count;
            bool isDirectory = Directory.Exists(path);

            if (isDirectory)
            {
                for (int i = 0; i < FileList.Items.Count; i++)
                {
                    if (FileList.Items[i] is not System.Windows.Controls.ListBoxItem existingItem ||
                        IsParentNavigationItem(existingItem) ||
                        existingItem.Tag is not string existingPath)
                    {
                        continue;
                    }

                    if (!Directory.Exists(existingPath))
                    {
                        insertIndex = i;
                        break;
                    }
                }
            }

            FileList.Items.Insert(insertIndex, item);
        }

        private void InsertFolderWatcherDefaultOrderPath(string path)
        {
            _detailsDefaultOrderPaths.RemoveAll(existing => ArePathsEqual(existing, path));

            if (Directory.Exists(path))
            {
                int firstFileIndex = _detailsDefaultOrderPaths.FindIndex(existing => !Directory.Exists(existing));
                if (firstFileIndex >= 0)
                {
                    _detailsDefaultOrderPaths.Insert(firstFileIndex, path);
                    return;
                }
            }

            _detailsDefaultOrderPaths.Add(path);
        }

        private void ApplyFolderWatcherItemOrder()
        {
            if (_detailsSortActive)
            {
                SortCurrentFolderItemsInPlace();
                return;
            }

            if (string.Equals(NormalizeViewMode(viewMode), ViewModeDetails, StringComparison.OrdinalIgnoreCase))
            {
                RestoreDefaultDetailsOrderInPlace();
            }
        }
    }
}
