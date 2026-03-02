using System;
using System.IO;
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
            NotifyFilters.LastWrite |
            NotifyFilters.Size |
            NotifyFilters.Attributes |
            NotifyFilters.CreationTime |
            NotifyFilters.Security;

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

                _folderParentWatcher.Changed += FolderParentWatcher_Changed;
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
                    IncludeSubdirectories = false,
                    NotifyFilter = FolderWatcherNotifyFilters,
                    EnableRaisingEvents = true
                };

                _folderContentWatcher.Changed += FolderContentWatcher_Changed;
                _folderContentWatcher.Created += FolderContentWatcher_Created;
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
                previous.Changed -= FolderParentWatcher_Changed;
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
                previous.Changed -= FolderContentWatcher_Changed;
                previous.Created -= FolderContentWatcher_Created;
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

        private void FolderContentWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            QueueFolderRefreshFromWatcher();
        }

        private void FolderContentWatcher_Created(object sender, FileSystemEventArgs e)
        {
            QueueFolderRefreshFromWatcher();
        }

        private void FolderContentWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            QueueFolderRefreshFromWatcher();
        }

        private void FolderContentWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            QueueFolderRefreshFromWatcher();
        }

        private void FolderParentWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (!ArePathsEqual(e.FullPath, currentFolderPath))
            {
                return;
            }

            QueueFolderRefreshFromWatcher();
        }

        private void FolderParentWatcher_Created(object sender, FileSystemEventArgs e)
        {
            if (!ArePathsEqual(e.FullPath, currentFolderPath))
            {
                return;
            }

            QueueFolderRefreshFromWatcher(immediate: true);
        }

        private void FolderParentWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            if (!ArePathsEqual(e.FullPath, currentFolderPath))
            {
                return;
            }

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

                SetPanelTitleFromFolderPath(currentFolderPath);
                StartOrUpdateFolderWatchers(currentFolderPath);
                QueueFolderRefreshFromWatcher(immediate: true);
            }), DispatcherPriority.Background);
        }

        private void FolderWatcher_Error(object sender, ErrorEventArgs e)
        {
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
    }
}
