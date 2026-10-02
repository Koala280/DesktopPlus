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
        private readonly object _recycleBinRefreshLock = new object();
        private int _recycleBinRefreshSuspensionCount;
        private int _recycleBinRefreshPending;
        private CancellationTokenSource? _folderEntryStateCts;
        private bool _currentFolderHasEntries;
        public bool showEmptyRecycleBinButton = false;

        private const int RecycleBinSnapshotReloadThreshold = 64;

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

            _contentViewGeneration++;
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

        private bool TryApplyRecycleBinSnapshot(IReadOnlyList<RecycleBinItemEntry> entries)
        {
            if (FileList == null ||
                PanelType != PanelKind.RecycleBin ||
                _recycleBinLoadCts != null)
            {
                return false;
            }

            if (entries.Count == 0)
            {
                FileList.Items.Clear();
                _baseItemPaths.Clear();
                _detailsDefaultOrderPaths.Clear();
                _searchInjectedItems.Clear();
                _searchInjectedPaths.Clear();
                RefreshDetailsHeader();
                if (CurrentViewNeedsContentLayoutRefresh())
                {
                    QueueWrapPanelWidthUpdate();
                }
                UpdateDropZoneVisibility();
                UpdateEmptyRecycleBinButtonVisibility();
                return true;
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

            var snapshotPaths = new HashSet<string>(
                entries.Select(entry => entry.DataPath),
                StringComparer.OrdinalIgnoreCase);
            int addedCount = snapshotPaths.Count(path => !existingByPath.ContainsKey(path));
            int removedCount = existingByPath.Keys.Count(path => !snapshotPaths.Contains(path));

            // Rebuilding a large delta item-by-item causes thousands of WPF layout invalidations.
            // The normal recycle-bin loader already clears once and adds items in dispatcher batches.
            if (addedCount + removedCount >= RecycleBinSnapshotReloadThreshold)
            {
                return false;
            }

            var desiredOrder = new List<ListBoxItem>(entries.Count);

            foreach (RecycleBinItemEntry entry in entries)
            {
                if (!existingByPath.TryGetValue(entry.DataPath, out ListBoxItem? item))
                {
                    item = CreateRecycleBinListBoxItem(entry);
                    FileList.Items.Add(item);
                }
                else
                {
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

            // Deleting entries preserves the relative order of all survivors. Reordering is only
            // needed when newly-created recycle-bin entries were appended to the current list.
            if (addedCount > 0)
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

        private async Task ApplyQueuedRecycleBinSnapshotAsync(
            IReadOnlyList<RecycleBinItemEntry> entries,
            CancellationTokenSource cts,
            CancellationToken token)
        {
            try
            {
                if (!IsRecycleBinRefreshCurrent(cts, token) ||
                    PanelType != PanelKind.RecycleBin)
                {
                    return;
                }

                if (TryApplyRecycleBinSnapshot(entries))
                {
                    return;
                }

                CancelPendingRecycleBinLoad();
                await RebuildRecycleBinSnapshotAsync(entries, cts, token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RebuildRecycleBinSnapshotAsync(
            IReadOnlyList<RecycleBinItemEntry> entries,
            CancellationTokenSource cts,
            CancellationToken token)
        {
            if (FileList == null ||
                !IsRecycleBinRefreshCurrent(cts, token) ||
                PanelType != PanelKind.RecycleBin)
            {
                return;
            }

            var selectedPaths = new HashSet<string>(
                FileList.SelectedItems
                    .OfType<ListBoxItem>()
                    .Select(item => item.Tag as string)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>(),
                StringComparer.OrdinalIgnoreCase);
            long selectionVersion = _fileListSelectionVersion;

            _useLightweightItemVisuals = entries.Count >= FolderLightweightVisualThreshold;
            _suppressFileListSelectionVersion = true;
            try
            {
                FileList.Items.Clear();
            }
            finally
            {
                _suppressFileListSelectionVersion = false;
            }
            _baseItemPaths.Clear();
            _detailsDefaultOrderPaths.Clear();
            _detailsDefaultOrderPaths.AddRange(entries.Select(entry => entry.DataPath));
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();

            int uiBatchSize = GetFolderLoadBatchSize();
            for (int start = 0; start < entries.Count; start += uiBatchSize)
            {
                if (!IsRecycleBinRefreshCurrent(cts, token) ||
                    PanelType != PanelKind.RecycleBin)
                {
                    return;
                }

                int end = Math.Min(start + uiBatchSize, entries.Count);
                string activeFilter = SearchBox?.Text?.Trim() ?? string.Empty;
                IReadOnlyList<string> activeTerms = GetSearchTerms(activeFilter);

                for (int index = start; index < end; index++)
                {
                    RecycleBinItemEntry entry = entries[index];
                    ListBoxItem item = CreateRecycleBinListBoxItem(entry);
                    if (!MatchesSearchTerms(entry.DisplayName, activeTerms))
                    {
                        item.Visibility = Visibility.Collapsed;
                    }

                    FileList.Items.Add(item);
                    _baseItemPaths.Add(entry.DataPath);
                    if (_fileListSelectionVersion == selectionVersion &&
                        selectedPaths.Contains(entry.DataPath))
                    {
                        _suppressFileListSelectionVersion = true;
                        try
                        {
                            item.IsSelected = true;
                        }
                        finally
                        {
                            _suppressFileListSelectionVersion = false;
                        }
                    }
                }

                UpdateDropZoneVisibility();
                UpdateEmptyRecycleBinButtonVisibility();

                if (end < entries.Count)
                {
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.ContextIdle);
                }
            }

            if (!IsRecycleBinRefreshCurrent(cts, token) ||
                PanelType != PanelKind.RecycleBin)
            {
                return;
            }

            RefreshDetailsHeader();
            QueueWrapPanelWidthUpdate();
            UpdateDropZoneVisibility();
            UpdateEmptyRecycleBinButtonVisibility();
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
                        IReadOnlyList<string> activeTerms = GetSearchTerms(activeFilter);

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

                            if (!MatchesSearchTerms(entry.DisplayName, activeTerms))
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

        private bool TryGetRecycleBinDisplayNameForDataPath(string dataPath, out string displayName)
        {
            displayName = string.Empty;

            if (PanelType != PanelKind.RecycleBin || string.IsNullOrWhiteSpace(dataPath))
            {
                return false;
            }

            string? infoPath = TryGetRecycleBinInfoPath(dataPath);
            if (string.IsNullOrWhiteSpace(infoPath) || !File.Exists(infoPath))
            {
                return false;
            }

            if (!TryReadRecycleBinMetadata(infoPath, out string originalPath, out _))
            {
                return false;
            }

            displayName = GetRecycleBinDisplayName(originalPath, dataPath);
            return !string.IsNullOrWhiteSpace(displayName);
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
                    Directory.Delete(dataPath, recursive: true);
                }
                else if (File.Exists(dataPath))
                {
                    File.Delete(dataPath);
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

        private static bool TrySetRecycleBinFileNameMap(
            System.Windows.DataObject dataObject,
            IReadOnlyList<string> dataPaths)
        {
            if (dataObject == null || dataPaths == null || dataPaths.Count == 0)
            {
                return false;
            }

            var originalNames = new List<string>(dataPaths.Count);
            foreach (string dataPath in dataPaths)
            {
                if (!TryGetRecycleBinOriginalName(dataPath, out string originalName))
                {
                    return false;
                }

                originalNames.Add(originalName);
            }

            byte[] payload = ShellFileNameMap.BuildUnicodePayload(originalNames);
            dataObject.SetData(
                ShellFileNameMap.UnicodeFormat,
                new MemoryStream(payload, writable: false),
                autoConvert: false);
            return true;
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
            if (watchers != null)
            {
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

            CancelPendingRecycleBinRefresh();
        }

        private void RecycleBinWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            QueueRecycleBinRefresh();
        }

        private void RecycleBinWatcher_Error(object sender, ErrorEventArgs e)
        {
            QueueRecycleBinRefresh();
        }

        private void SuspendRecycleBinRefreshes()
        {
            CancellationTokenSource? pendingRefresh = null;
            lock (_recycleBinRefreshLock)
            {
                _recycleBinRefreshSuspensionCount++;
                if (_recycleBinRefreshSuspensionCount == 1)
                {
                    pendingRefresh = _recycleBinRefreshCts;
                    _recycleBinRefreshCts = null;
                    if (pendingRefresh != null)
                    {
                        _recycleBinRefreshPending = 1;
                    }
                }
            }

            pendingRefresh?.Cancel();
            pendingRefresh?.Dispose();
        }

        private void ResumeRecycleBinRefreshes(bool forceRefresh)
        {
            bool shouldRefresh = forceRefresh;
            lock (_recycleBinRefreshLock)
            {
                if (_recycleBinRefreshSuspensionCount <= 0)
                {
                    return;
                }

                _recycleBinRefreshSuspensionCount--;
                if (_recycleBinRefreshSuspensionCount > 0)
                {
                    if (forceRefresh)
                    {
                        _recycleBinRefreshPending = 1;
                    }
                    return;
                }

                shouldRefresh |= _recycleBinRefreshPending != 0;
                _recycleBinRefreshPending = 0;
            }

            if (!_isClosed && shouldRefresh && PanelType == PanelKind.RecycleBin)
            {
                QueueRecycleBinRefresh(immediate: true);
            }
        }

        private void CancelPendingRecycleBinRefresh()
        {
            CancellationTokenSource? pendingRefresh;
            lock (_recycleBinRefreshLock)
            {
                pendingRefresh = _recycleBinRefreshCts;
                _recycleBinRefreshCts = null;
                _recycleBinRefreshPending = 0;
            }

            pendingRefresh?.Cancel();
            pendingRefresh?.Dispose();
        }

        private bool IsRecycleBinRefreshCurrent(CancellationTokenSource cts, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            lock (_recycleBinRefreshLock)
            {
                return _recycleBinRefreshSuspensionCount == 0 &&
                    ReferenceEquals(_recycleBinRefreshCts, cts);
            }
        }

        private void QueueRecycleBinRefresh(bool immediate = false)
        {
            if (_isClosed)
            {
                return;
            }

            CancellationTokenSource? pendingRefresh;
            CancellationTokenSource cts;
            CancellationToken token;
            lock (_recycleBinRefreshLock)
            {
                if (_recycleBinRefreshSuspensionCount > 0)
                {
                    _recycleBinRefreshPending = 1;
                    return;
                }

                pendingRefresh = _recycleBinRefreshCts;
                cts = new CancellationTokenSource();
                token = cts.Token;
                _recycleBinRefreshCts = cts;
            }

            pendingRefresh?.Cancel();
            pendingRefresh?.Dispose();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(immediate ? 0 : 300, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!IsRecycleBinRefreshCurrent(cts, token))
                {
                    return;
                }

                List<RecycleBinItemEntry> snapshot;
                try
                {
                    snapshot = EnumerateRecycleBinItems(token);
                }
                catch
                {
                    return;
                }

                if (!IsRecycleBinRefreshCurrent(cts, token))
                {
                    return;
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (PanelType == PanelKind.RecycleBin &&
                        IsRecycleBinRefreshCurrent(cts, token))
                    {
                        _ = ApplyQueuedRecycleBinSnapshotAsync(snapshot, cts, token);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        public async Task EmptyRecycleBinAsync()
        {
            if (_isDeleteOperationRunning)
            {
                return;
            }

            var result = System.Windows.MessageBox.Show(
                MainWindow.GetString("Loc.EmptyRecycleBinConfirm"),
                MainWindow.GetString("Loc.EmptyRecycleBinTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            SuspendRecycleBinRefreshes();
            SetDeleteOperationState(true);
            try
            {
                await RunStaFileOperationAsync(() =>
                {
                    int hresult = SHEmptyRecycleBin(
                        IntPtr.Zero,
                        null,
                        SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                    Marshal.ThrowExceptionForHR(hresult);
                    return true;
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgDeletePermanentError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetDeleteOperationState(false);
                ResumeRecycleBinRefreshes(forceRefresh: true);
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

        private static bool HasFolderEntriesForClearAction(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
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
                    .Any(path => !string.IsNullOrWhiteSpace(path) &&
                        (File.Exists(path) || Directory.Exists(path)));
            }
            catch
            {
                return false;
            }
        }

        private void QueueFolderEntryStateRefresh(string folderPath)
        {
            if (_isClosed || string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            var cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            var pending = Interlocked.Exchange(ref _folderEntryStateCts, cts);
            pending?.Cancel();
            pending?.Dispose();

            _ = Task.Run(() =>
            {
                bool hasEntries = HasFolderEntriesForClearAction(folderPath);
                if (token.IsCancellationRequested ||
                    !ReferenceEquals(Volatile.Read(ref _folderEntryStateCts), cts))
                {
                    return;
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested ||
                        !ReferenceEquals(Volatile.Read(ref _folderEntryStateCts), cts) ||
                        PanelType != PanelKind.Folder ||
                        !string.Equals(currentFolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    _currentFolderHasEntries = hasEntries;
                    UpdateEmptyRecycleBinButtonVisibility();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }, token);
        }

        private void CancelPendingFolderEntryStateRefresh()
        {
            var pending = Interlocked.Exchange(ref _folderEntryStateCts, null);
            pending?.Cancel();
            pending?.Dispose();
            _currentFolderHasEntries = false;
        }

        public async Task ClearCurrentFolderAsync()
        {
            string folderPath = currentFolderPath;
            PanelTabData? tabAtStart = ActiveTab;
            long viewGenerationAtStart = _contentViewGeneration;
            if (_isDeleteOperationRunning ||
                PanelType != PanelKind.Folder ||
                string.IsNullOrWhiteSpace(folderPath) ||
                !Directory.Exists(folderPath))
            {
                UpdateEmptyRecycleBinButtonVisibility();
                return;
            }

            SetDeleteOperationState(true);
            bool folderWatchersStopped = false;
            try
            {
                List<string> entries = await Task.Run(() => GetFolderEntriesForClearAction(folderPath));
                if (!IsContentViewCurrent(viewGenerationAtStart, tabAtStart) ||
                    PanelType != PanelKind.Folder ||
                    !string.Equals(currentFolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

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
                CancelPendingFolderLoad();
                folderWatchersStopped = true;

                List<string> clearFailures = await RunStaFileOperationAsync(() =>
                {
                    var failures = new List<string>();

                    foreach (string path in entries)
                    {
                        if (TryMovePathToRecycleBin(path, out string? error))
                        {
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

                    return failures;
                });

                InvalidateFolderSearchIndex(
                    folderPath,
                    rerunActiveSearch: false);

                if (IsContentViewCurrent(viewGenerationAtStart, tabAtStart) &&
                    PanelType == PanelKind.Folder &&
                    string.Equals(currentFolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    ReloadFolderAfterDelete(folderPath);
                    folderWatchersStopped = false;
                }

                if (clearFailures.Count > 0)
                {
                    System.Windows.MessageBox.Show(
                        string.Format(MainWindow.GetString("Loc.MsgDeletePathError"), string.Join(Environment.NewLine, clearFailures)),
                        MainWindow.GetString("Loc.MsgError"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgDeletePathError"), ex.Message),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (!_isClosed &&
                    folderWatchersStopped &&
                    IsContentViewCurrent(viewGenerationAtStart, tabAtStart) &&
                    PanelType == PanelKind.Folder &&
                    string.Equals(currentFolderPath, folderPath, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(folderPath))
                {
                    StartOrUpdateFolderWatchers(folderPath);
                }

                SetDeleteOperationState(false);
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
                         !string.IsNullOrWhiteSpace(currentFolderPath))
                {
                    hasItems = _currentFolderHasEntries;
                }
            }

            EmptyRecycleBinButton.Visibility =
                hasItems
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }
}
