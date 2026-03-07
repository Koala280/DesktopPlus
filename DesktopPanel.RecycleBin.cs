using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic.FileIO;

namespace DesktopPlus
{
    public partial class DesktopPanel : Window
    {
        [DllImport("shell32.dll")]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        private List<FileSystemWatcher>? _recycleBinWatchers;
        private CancellationTokenSource? _recycleBinRefreshCts;
        public bool showEmptyRecycleBinButton = true;

        private static readonly HashSet<string> RecycleBinDefaultTitles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Papierkorb",
                "Recycle Bin",
                "Atkritne"
            };

        private static readonly HashSet<string> RecycleBinLegacyActionTitles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Papierkorb-Panel öffnen",
                "Open Recycle Bin Panel"
            };

        private sealed class RecycleBinItemEntry
        {
            public string DataPath { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string OriginalPath { get; init; } = string.Empty;
            public DateTime? DeletedUtc { get; init; }
        }

        private bool IsRecycleBinLoadRequestCurrent(CancellationTokenSource cts)
        {
            return ReferenceEquals(_recycleBinLoadCts, cts) &&
                PanelType == PanelKind.RecycleBin;
        }

        private CancellationTokenSource BeginRecycleBinLoad()
        {
            var previousCts = _recycleBinLoadCts;
            _recycleBinLoadCts = null;
            previousCts?.Cancel();

            var currentCts = new CancellationTokenSource();
            _recycleBinLoadCts = currentCts;
            return currentCts;
        }

        private void CancelPendingRecycleBinLoad()
        {
            var pendingLoadCts = _recycleBinLoadCts;
            _recycleBinLoadCts = null;
            pendingLoadCts?.Cancel();
        }

        public void RefreshRecycleBinTitle(bool forceDefault = false)
        {
            string localizedTitle = MainWindow.GetString("Loc.PanelTypeRecycleBin");
            string currentTitle = !string.IsNullOrWhiteSpace(PanelTitle?.Text)
                ? PanelTitle.Text.Trim()
                : (Title?.Trim() ?? string.Empty);

            bool useDefaultTitle = forceDefault ||
                string.IsNullOrWhiteSpace(currentTitle) ||
                RecycleBinDefaultTitles.Contains(currentTitle) ||
                RecycleBinLegacyActionTitles.Contains(currentTitle);

            string resolvedTitle = useDefaultTitle
                ? localizedTitle
                : currentTitle;

            Title = resolvedTitle;
            if (PanelTitle != null)
            {
                PanelTitle.Text = resolvedTitle;
            }
        }

        public void LoadRecycleBin(bool saveSettings = true, bool renamePanelTitle = true)
        {
            if (FileList == null)
            {
                return;
            }

            var loadCts = BeginRecycleBinLoad();
            CancelPendingFolderLoad();
            CancelPendingFolderSearchIndex();
            StopFolderWatchers();
            ResetSearchState(clearSearchBox: true);
            PanelType = PanelKind.RecycleBin;
            _useLightweightItemVisuals = false;
            currentFolderPath = string.Empty;
            defaultFolderPath = string.Empty;
            PinnedItems.Clear();
            FileList.Items.Clear();
            _baseItemPaths.Clear();
            _detailsDefaultOrderPaths.Clear();
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();

            RefreshRecycleBinTitle(forceDefault: renamePanelTitle);

            RefreshDetailsHeader();
            _ = Dispatcher.BeginInvoke(
                new Action(UpdateWrapPanelWidth),
                System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateDropZoneVisibility();
            UpdateEmptyRecycleBinButtonVisibility();
            StartRecycleBinWatchers();
            _ = RunRecycleBinLoadAsync(loadCts);

            if (saveSettings)
            {
                MainWindow.SaveSettings();
            }
        }

        private ListBoxItem CreateRecycleBinListBoxItem(RecycleBinItemEntry entry)
        {
            ListBoxItem item = CreateFileListBoxItem(
                entry.DisplayName,
                entry.DataPath,
                isBackButton: false,
                _currentAppearance);
            if (item.Content is FrameworkElement root)
            {
                ToolTipService.SetToolTip(root, BuildRecycleBinToolTip(entry));
            }

            return item;
        }

        private void RefreshRecycleBinListBoxItem(ListBoxItem item, RecycleBinItemEntry entry)
        {
            item.Tag = entry.DataPath;
            item.Content = CreateListBoxItem(
                entry.DisplayName,
                entry.DataPath,
                isBackButton: false,
                _currentAppearance);
            item.Focusable = true;
            ApplyListItemContainerSpacing(item);

            if (item.Content is FrameworkElement root)
            {
                ToolTipService.SetToolTip(root, BuildRecycleBinToolTip(entry));
            }
        }

        private bool TryApplyRecycleBinSnapshot(IReadOnlyList<RecycleBinItemEntry> entries)
        {
            if (FileList == null ||
                PanelType != PanelKind.RecycleBin ||
                _recycleBinLoadCts != null)
            {
                return false;
            }

            var selectedPaths = new HashSet<string>(
                FileList.SelectedItems
                    .OfType<ListBoxItem>()
                    .Select(item => item.Tag as string)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>(),
                StringComparer.OrdinalIgnoreCase);

            var existingByPath = FileList.Items
                .OfType<ListBoxItem>()
                .Where(item => item.Tag is string)
                .GroupBy(item => item.Tag as string ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var desiredOrder = new List<ListBoxItem>(entries.Count);
            var snapshotPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RecycleBinItemEntry entry in entries)
            {
                snapshotPaths.Add(entry.DataPath);

                if (!existingByPath.TryGetValue(entry.DataPath, out ListBoxItem? item))
                {
                    item = CreateRecycleBinListBoxItem(entry);
                    FileList.Items.Add(item);
                }
                else
                {
                    RefreshRecycleBinListBoxItem(item, entry);
                    existingByPath.Remove(entry.DataPath);
                }

                bool matchesSearch = ShouldItemMatchCurrentSearchFilter(item);
                item.Visibility = matchesSearch ? Visibility.Visible : Visibility.Collapsed;
                item.Opacity = matchesSearch ? 1 : 0;
                item.IsHitTestVisible = matchesSearch;
                desiredOrder.Add(item);
            }

            foreach (ListBoxItem staleItem in existingByPath.Values.ToList())
            {
                FileList.Items.Remove(staleItem);
                _searchInjectedItems.Remove(staleItem);
            }

            _baseItemPaths.Clear();
            foreach (string path in snapshotPaths)
            {
                _baseItemPaths.Add(path);
            }

            _detailsDefaultOrderPaths.Clear();
            _detailsDefaultOrderPaths.AddRange(entries.Select(entry => entry.DataPath));
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();

            if (desiredOrder.Count == 0)
            {
                FileList.Items.Clear();
            }
            else
            {
                ApplyFileListOrderInPlace(desiredOrder, selectedPaths);
            }

            RefreshDetailsHeader();
            if (CurrentViewNeedsContentLayoutRefresh())
            {
                QueueWrapPanelWidthUpdate();
            }
            UpdateDropZoneVisibility();
            UpdateEmptyRecycleBinButtonVisibility();
            return true;
        }

        private async Task RunRecycleBinLoadAsync(CancellationTokenSource cts)
        {
            var token = cts.Token;

            try
            {
                List<RecycleBinItemEntry> entries = await Task.Run(() => EnumerateRecycleBinItems(token));
                if (token.IsCancellationRequested || !IsRecycleBinLoadRequestCurrent(cts))
                {
                    return;
                }

                if (!IsRecycleBinLoadRequestCurrent(cts) || entries.Count == 0)
                {
                    return;
                }

                bool useLightweightVisuals = entries.Count >= FolderLightweightVisualThreshold;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (IsRecycleBinLoadRequestCurrent(cts))
                    {
                        _useLightweightItemVisuals = useLightweightVisuals;
                    }
                }, System.Windows.Threading.DispatcherPriority.Send);

                int uiBatchSize = GetFolderLoadBatchSize();
                for (int start = 0; start < entries.Count; start += uiBatchSize)
                {
                    if (token.IsCancellationRequested || !IsRecycleBinLoadRequestCurrent(cts))
                    {
                        return;
                    }

                    RecycleBinItemEntry[] batch = entries
                        .Skip(start)
                        .Take(uiBatchSize)
                        .ToArray();

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsRecycleBinLoadRequestCurrent(cts))
                        {
                            return;
                        }

                        bool isPhotoMode = string.Equals(
                            NormalizeViewMode(viewMode),
                            ViewModePhotos,
                            StringComparison.OrdinalIgnoreCase);
                        string activeFilter = SearchBox?.Text?.Trim() ?? string.Empty;
                        bool hasFilter = !string.IsNullOrWhiteSpace(activeFilter);

                        foreach (RecycleBinItemEntry entry in batch)
                        {
                            ListBoxItem item = CreateFileListBoxItem(
                                entry.DisplayName,
                                entry.DataPath,
                                isBackButton: false,
                                _currentAppearance);

                            if (item.Content is FrameworkElement root)
                            {
                                ToolTipService.SetToolTip(root, BuildRecycleBinToolTip(entry));
                            }

                            if (hasFilter &&
                                entry.DisplayName.IndexOf(activeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                item.Visibility = Visibility.Collapsed;
                            }

                            FileList.Items.Add(item);
                            _baseItemPaths.Add(entry.DataPath);
                        }

                        UpdateEmptyRecycleBinButtonVisibility();
                        UpdateDropZoneVisibility();
                        if (isPhotoMode)
                        {
                            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }, System.Windows.Threading.DispatcherPriority.ContextIdle);

                    if (start + uiBatchSize < entries.Count)
                    {
                        try
                        {
                            await Task.Delay(FolderUiBatchDelayMs, token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsRecycleBinLoadRequestCurrent(cts))
                    {
                        return;
                    }

                    _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
                    UpdateEmptyRecycleBinButtonVisibility();
                    UpdateDropZoneVisibility();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(_recycleBinLoadCts, cts))
                {
                    _recycleBinLoadCts = null;
                }

                cts.Dispose();
            }
        }

        private static List<string> EnumerateRecycleBinRoots()
        {
            var roots = new List<string>();
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
            {
                return roots;
            }

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    string root = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                    if (Directory.Exists(root))
                    {
                        roots.Add(root);
                    }
                }
                catch
                {
                }
            }

            return roots;
        }

        private List<RecycleBinItemEntry> EnumerateRecycleBinItems(CancellationToken token)
        {
            var entries = new List<RecycleBinItemEntry>();

            foreach (string recycleRoot in EnumerateRecycleBinRoots())
            {
                if (token.IsCancellationRequested)
                {
                    return entries;
                }

                IEnumerable<string> metadataFiles;
                try
                {
                    metadataFiles = Directory.EnumerateFiles(recycleRoot, "$I*", System.IO.SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (string infoPath in metadataFiles)
                {
                    if (token.IsCancellationRequested)
                    {
                        return entries;
                    }

                    string? dataPath = TryGetRecycleBinDataPath(infoPath);
                    if (string.IsNullOrWhiteSpace(dataPath) ||
                        (!File.Exists(dataPath) && !Directory.Exists(dataPath)))
                    {
                        continue;
                    }

                    TryReadRecycleBinMetadata(infoPath, out string originalPath, out DateTime? deletedUtc);
                    string displayName = GetRecycleBinDisplayName(originalPath, dataPath);
                    entries.Add(new RecycleBinItemEntry
                    {
                        DataPath = dataPath,
                        DisplayName = displayName,
                        OriginalPath = originalPath,
                        DeletedUtc = deletedUtc
                    });
                }
            }

            return entries
                .OrderByDescending(entry => entry.DeletedUtc ?? DateTime.MinValue)
                .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string BuildRecycleBinToolTip(RecycleBinItemEntry entry)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(entry.OriginalPath))
            {
                lines.Add(entry.OriginalPath);
            }

            if (entry.DeletedUtc.HasValue)
            {
                lines.Add(entry.DeletedUtc.Value.ToLocalTime().ToString(CultureInfo.CurrentCulture));
            }

            return lines.Count > 0
                ? string.Join(Environment.NewLine, lines)
                : entry.DisplayName;
        }

        private static string GetRecycleBinDisplayName(string originalPath, string dataPath)
        {
            string preferred = string.IsNullOrWhiteSpace(originalPath)
                ? string.Empty
                : originalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leaf = string.IsNullOrWhiteSpace(preferred)
                ? string.Empty
                : Path.GetFileName(preferred);

            if (!string.IsNullOrWhiteSpace(leaf))
            {
                return leaf;
            }

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            return Path.GetFileName(dataPath);
        }

        private static bool TryReadRecycleBinMetadata(string infoPath, out string originalPath, out DateTime? deletedUtc)
        {
            originalPath = string.Empty;
            deletedUtc = null;

            try
            {
                using var stream = File.OpenRead(infoPath);
                using var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false);

                long version = reader.ReadInt64();
                _ = reader.ReadInt64(); // original size, currently unused
                long deletedFileTime = reader.ReadInt64();
                if (deletedFileTime > 0)
                {
                    deletedUtc = DateTime.FromFileTimeUtc(deletedFileTime);
                }

                if (version >= 2 && stream.Length >= 28)
                {
                    int charCount = reader.ReadInt32();
                    if (charCount > 0)
                    {
                        int byteCount = Math.Min(charCount * 2, (int)Math.Max(0, stream.Length - stream.Position));
                        originalPath = Encoding.Unicode.GetString(reader.ReadBytes(byteCount)).TrimEnd('\0');
                    }
                }

                if (string.IsNullOrWhiteSpace(originalPath))
                {
                    int remainingBytes = (int)Math.Max(0, stream.Length - stream.Position);
                    if (remainingBytes > 0)
                    {
                        originalPath = Encoding.Unicode.GetString(reader.ReadBytes(remainingBytes)).TrimEnd('\0');
                    }
                }

                return !string.IsNullOrWhiteSpace(originalPath) || deletedUtc.HasValue;
            }
            catch
            {
                return false;
            }
        }

        private static string? TryGetRecycleBinDataPath(string infoPath)
        {
            if (string.IsNullOrWhiteSpace(infoPath))
            {
                return null;
            }

            string fileName = Path.GetFileName(infoPath);
            if (!fileName.StartsWith("$I", StringComparison.OrdinalIgnoreCase) ||
                fileName.Length <= 2)
            {
                return null;
            }

            string directory = Path.GetDirectoryName(infoPath) ?? string.Empty;
            return Path.Combine(directory, "$R" + fileName.Substring(2));
        }

        private static string? TryGetRecycleBinInfoPath(string dataPath)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                return null;
            }

            string fileName = Path.GetFileName(dataPath);
            if (!fileName.StartsWith("$R", StringComparison.OrdinalIgnoreCase) ||
                fileName.Length <= 2)
            {
                return null;
            }

            string directory = Path.GetDirectoryName(dataPath) ?? string.Empty;
            return Path.Combine(directory, "$I" + fileName.Substring(2));
        }

        private static bool TryDeleteRecycleBinItemPermanently(string dataPath, out string? errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                return false;
            }

            try
            {
                if (Directory.Exists(dataPath))
                {
                    FileSystem.DeleteDirectory(
                        dataPath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.DeletePermanently,
                        UICancelOption.DoNothing);
                }
                else if (File.Exists(dataPath))
                {
                    FileSystem.DeleteFile(
                        dataPath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.DeletePermanently,
                        UICancelOption.DoNothing);
                }
                else
                {
                    return false;
                }

                string? infoPath = TryGetRecycleBinInfoPath(dataPath);
                if (!string.IsNullOrWhiteSpace(infoPath) && File.Exists(infoPath))
                {
                    File.Delete(infoPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static bool TryGetRecycleBinOriginalName(string dataPath, out string originalName)
        {
            originalName = string.Empty;
            string? infoPath = TryGetRecycleBinInfoPath(dataPath);
            if (string.IsNullOrWhiteSpace(infoPath) || !File.Exists(infoPath))
            {
                return false;
            }

            if (!TryReadRecycleBinMetadata(infoPath, out string originalPath, out _) ||
                string.IsNullOrWhiteSpace(originalPath))
            {
                return false;
            }

            string trimmedOriginalPath = originalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            originalName = Path.GetFileName(trimmedOriginalPath);
            return !string.IsNullOrWhiteSpace(originalName);
        }

        private static bool TryTransferRecycleBinItemToDirectory(
            string dataPath,
            string destinationDirectory,
            bool move,
            out string? errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(dataPath) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(destinationDirectory))
            {
                return false;
            }

            if (!TryGetRecycleBinOriginalName(dataPath, out string originalName))
            {
                return false;
            }

            bool isDirectory = Directory.Exists(dataPath);
            bool isFile = File.Exists(dataPath);
            if (!isDirectory && !isFile)
            {
                return false;
            }

            string targetPath = isDirectory
                ? BuildUniqueDirectoryTargetPath(destinationDirectory, originalName)
                : BuildUniqueFileTargetPath(destinationDirectory, originalName);

            try
            {
                if (isDirectory)
                {
                    if (move)
                    {
                        try
                        {
                            Directory.Move(dataPath, targetPath);
                        }
                        catch (IOException)
                        {
                            CopyDirectoryRecursive(dataPath, targetPath);
                            Directory.Delete(dataPath, true);
                        }
                    }
                    else
                    {
                        CopyDirectoryRecursive(dataPath, targetPath);
                    }
                }
                else
                {
                    if (move)
                    {
                        ShortcutFileTransfer.MoveFile(dataPath, targetPath);
                    }
                    else
                    {
                        ShortcutFileTransfer.CopyFile(dataPath, targetPath, overwrite: false);
                    }
                }

                if (move)
                {
                    string? infoPath = TryGetRecycleBinInfoPath(dataPath);
                    if (!string.IsNullOrWhiteSpace(infoPath) && File.Exists(infoPath))
                    {
                        File.Delete(infoPath);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private void StartRecycleBinWatchers()
        {
            StopRecycleBinWatchers();
            var roots = EnumerateRecycleBinRoots();
            if (roots.Count == 0)
            {
                return;
            }

            _recycleBinWatchers = new List<FileSystemWatcher>();
            foreach (string root in roots)
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        Filter = "*",
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    watcher.Created += RecycleBinWatcher_Changed;
                    watcher.Deleted += RecycleBinWatcher_Changed;
                    watcher.Renamed += RecycleBinWatcher_Changed;
                    watcher.Error += RecycleBinWatcher_Error;
                    _recycleBinWatchers.Add(watcher);
                }
                catch
                {
                }
            }
        }

        private void StopRecycleBinWatchers()
        {
            var watchers = _recycleBinWatchers;
            _recycleBinWatchers = null;
            if (watchers == null)
            {
                return;
            }

            foreach (var watcher in watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= RecycleBinWatcher_Changed;
                    watcher.Deleted -= RecycleBinWatcher_Changed;
                    watcher.Renamed -= RecycleBinWatcher_Changed;
                    watcher.Error -= RecycleBinWatcher_Error;
                    watcher.Dispose();
                }
                catch
                {
                }
            }
        }

        private void RecycleBinWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            QueueRecycleBinRefresh();
        }

        private void RecycleBinWatcher_Error(object sender, ErrorEventArgs e)
        {
            QueueRecycleBinRefresh();
        }

        private void QueueRecycleBinRefresh(bool immediate = false)
        {
            var pending = Interlocked.Exchange(ref _recycleBinRefreshCts, new CancellationTokenSource());
            pending?.Cancel();
            pending?.Dispose();

            var cts = _recycleBinRefreshCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(immediate ? 0 : 300, cts!.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (cts == null || cts.Token.IsCancellationRequested)
                {
                    return;
                }

                List<RecycleBinItemEntry> snapshot;
                try
                {
                    snapshot = EnumerateRecycleBinItems(cts.Token);
                }
                catch
                {
                    snapshot = new List<RecycleBinItemEntry>();
                }

                if (cts.Token.IsCancellationRequested)
                {
                    return;
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (PanelType == PanelKind.RecycleBin)
                    {
                        if (!TryApplyRecycleBinSnapshot(snapshot))
                        {
                            LoadRecycleBin(saveSettings: false, renamePanelTitle: false);
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        public void EmptyRecycleBin()
        {
            var result = System.Windows.MessageBox.Show(
                MainWindow.GetString("Loc.EmptyRecycleBinConfirm"),
                MainWindow.GetString("Loc.EmptyRecycleBinTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            }
            catch
            {
            }

            if (PanelType == PanelKind.RecycleBin)
            {
                QueueRecycleBinRefresh(immediate: true);
            }
        }

        private List<string> GetFolderEntriesForClearAction(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return new List<string>();
            }

            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0
                };

                return Directory.EnumerateFileSystemEntries(folderPath, "*", options)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public async Task ClearCurrentFolderAsync()
        {
            string folderPath = currentFolderPath;
            if (PanelType != PanelKind.Folder ||
                string.IsNullOrWhiteSpace(folderPath) ||
                !Directory.Exists(folderPath))
            {
                UpdateEmptyRecycleBinButtonVisibility();
                return;
            }

            List<string> entries = GetFolderEntriesForClearAction(folderPath);
            if (entries.Count == 0)
            {
                UpdateEmptyRecycleBinButtonVisibility();
                return;
            }

            string folderName = GetFolderDisplayName(folderPath);
            var result = System.Windows.MessageBox.Show(
                string.Format(MainWindow.GetString("Loc.EmptyFolderConfirm"), folderName),
                MainWindow.GetString("Loc.EmptyFolderTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            StopFolderWatchers();
            if (EmptyRecycleBinButton != null)
            {
                EmptyRecycleBinButton.IsEnabled = false;
            }

            (bool DeletedAny, List<string> Failures) clearResult;
            try
            {
                clearResult = await Task.Run(() =>
                {
                    bool deletedAny = false;
                    var failures = new List<string>();

                    foreach (string path in entries)
                    {
                        if (TryMovePathToRecycleBin(path, out string? error))
                        {
                            deletedAny = true;
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            string displayName = GetDisplayNameForPath(path);
                            if (string.IsNullOrWhiteSpace(displayName))
                            {
                                displayName = GetPathLeafName(path);
                            }

                            failures.Add($"{displayName}: {error}");
                        }
                    }

                    return (deletedAny, failures);
                });
            }
            finally
            {
                if (EmptyRecycleBinButton != null)
                {
                    EmptyRecycleBinButton.IsEnabled = true;
                }
            }

            InvalidateFolderSearchIndex(folderPath, rebuildInBackground: true, rerunActiveSearch: true);

            if (PanelType == PanelKind.Folder &&
                string.Equals(currentFolderPath, folderPath, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(folderPath))
            {
                LoadFolder(folderPath, saveSettings: false, renamePanelTitle: false);
            }

            if (clearResult.Failures.Count > 0)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgDeletePathError"), string.Join(Environment.NewLine, clearResult.Failures)),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public void UpdateEmptyRecycleBinButtonVisibility()
        {
            if (EmptyRecycleBinButton == null)
            {
                return;
            }

            bool hasItems = false;
            if (showEmptyRecycleBinButton)
            {
                if (PanelType == PanelKind.RecycleBin)
                {
                    hasItems = FileList != null && FileList.Items.Count > 0;
                }
                else if (PanelType == PanelKind.Folder &&
                         !string.IsNullOrWhiteSpace(currentFolderPath) &&
                         Directory.Exists(currentFolderPath))
                {
                    hasItems = GetFolderEntriesForClearAction(currentFolderPath).Count > 0;
                }
            }

            EmptyRecycleBinButton.Visibility =
                hasItems
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }
}
