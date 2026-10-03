using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Text;

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
        private const int FolderLightweightVisualThreshold = 700;

        private sealed class FolderSearchMatch
        {
            public string Path { get; init; } = string.Empty;
            public string SortName { get; init; } = string.Empty;
            public int Depth { get; init; }
            public int Score { get; init; }
        }

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
            if (ReferenceEquals(_runningFolderLoadCts, previousCts))
            {
                _runningFolderLoadCts = null;
            }

            var currentCts = new CancellationTokenSource();
            _folderLoadCts = currentCts;
            return currentCts;
        }

        private void CancelPendingFolderLoad()
        {
            var pendingLoadCts = _folderLoadCts;
            _folderLoadCts = null;
            pendingLoadCts?.Cancel();
            if (ReferenceEquals(_runningFolderLoadCts, pendingLoadCts))
            {
                _runningFolderLoadCts = null;
            }
        }

        private void StartFolderLoad(string folderPath, CancellationTokenSource cts)
        {
            // Loading the visible folder contents is foreground work. Do not gate it
            // on the main window's background-work lifecycle: panels can be loaded
            // before or independently of that signal and would otherwise stay empty.
            if (!IsLoaded ||
                !IsFolderLoadRequestCurrent(cts, folderPath) ||
                ReferenceEquals(_runningFolderLoadCts, cts))
            {
                return;
            }

            _runningFolderLoadCts = cts;
            _ = RunFolderLoadAsync(folderPath, cts);
        }

        internal void StartFolderBackgroundWorkAfterUiReady()
        {
            if (!IsLoaded)
            {
                return;
            }

            // Always resume a pending visible-folder load. Only index warm-up is
            // background work and remains guarded inside its scheduler.
            if (_folderLoadCts is CancellationTokenSource pendingLoad &&
                PanelType == PanelKind.Folder &&
                !string.IsNullOrWhiteSpace(currentFolderPath))
            {
                StartFolderLoad(currentFolderPath, pendingLoad);
            }

            ScheduleBackgroundFolderListingWarmup();
        }

        private void CancelPendingFolderSearchIndex()
        {
            // Index lifetime belongs to configured folders, not the search box.
        }

        private static string NormalizeFolderSearchIndexRoot(string folderPath)
        {
            try { return FolderSearchIndexService.Normalize(folderPath); }
            catch { return Path.TrimEndingDirectorySeparator(folderPath); }
        }

        private static string BuildRelativeSearchPath(string rootPath, string entryPath, string? fallbackName = null)
        {
            try { return Path.GetRelativePath(rootPath, entryPath); }
            catch { return fallbackName ?? GetPathLeafName(entryPath); }
        }

        private void InvalidateFolderSearchIndex(string folderPath, bool rerunActiveSearch = false, bool invalidateFolderListing = true)
        {
            if (invalidateFolderListing) FolderListingCache.Invalidate(folderPath);
            // The application-wide watcher keeps names current even during panel operations.
            // A full rescan is reserved for lost watcher events.
            if (rerunActiveSearch) OnSearchIndexChanged(NormalizeFolderSearchIndexRoot(folderPath));
        }

        private static readonly object SearchIndexNotificationLock = new();
        private static readonly HashSet<string> PendingSearchIndexNotifications = new(StringComparer.OrdinalIgnoreCase);
        private static bool _searchIndexNotificationQueued;

        internal static void OnSearchIndexChanged(string root)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            lock (SearchIndexNotificationLock)
            {
                PendingSearchIndexNotifications.Add(root);
                if (_searchIndexNotificationQueued) return;
                _searchIndexNotificationQueued = true;
            }
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(150) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    HashSet<string> changed;
                    lock (SearchIndexNotificationLock)
                    {
                        changed = new HashSet<string>(PendingSearchIndexNotifications, StringComparer.OrdinalIgnoreCase);
                        PendingSearchIndexNotifications.Clear();
                        _searchIndexNotificationQueued = false;
                    }
                    if (System.Windows.Application.Current == null) return;
                    foreach (var panel in System.Windows.Application.Current.Windows.OfType<DesktopPanel>())
                    {
                        if (panel._isClosed || panel.IsPreviewPanel || panel.PanelType != PanelKind.Folder ||
                            string.IsNullOrWhiteSpace(panel.currentFolderPath) ||
                            !changed.Contains(NormalizeFolderSearchIndexRoot(panel.currentFolderPath))) continue;
                        panel.RefreshActiveSearch();
                    }
                };
                timer.Start();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdateSearchIndexStatus()
        {
            bool preparing = PanelType == PanelKind.Folder && !string.IsNullOrWhiteSpace(currentFolderPath) &&
                (SearchBox?.Text.Trim().Length ?? 0) >= SearchMinCharsForDeepLookup &&
                SearchIndexService.GetStatus(currentFolderPath).IsPreparing;
            Visibility target = preparing ? Visibility.Visible : Visibility.Collapsed;
            if (SearchIndexStatus.Visibility != target) SearchIndexStatus.Visibility = target;
        }
        private static string GetSearchDisplayName(FolderSearchIndexEntry entry, bool showExtensions)
        {
            if (entry.IsDirectory || showExtensions)
            {
                return entry.Name;
            }

            string withoutExtension = Path.GetFileNameWithoutExtension(entry.Name);
            return string.IsNullOrWhiteSpace(withoutExtension)
                ? entry.Name
                : withoutExtension;
        }

        private static IReadOnlyList<string> GetSearchTerms(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return Array.Empty<string>();
            }

            return filter.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static int FindSearchMatchIndex(string candidate, string term)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(term))
            {
                return -1;
            }

            int matchIndex = candidate.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (matchIndex >= 0)
            {
                return matchIndex;
            }

            string normalizedCandidate = candidate
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            string normalizedTerm = term
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            return normalizedCandidate.IndexOf(normalizedTerm, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesSearchTerms(string candidate, IReadOnlyList<string> terms)
        {
            if (terms.Count == 0)
            {
                return true;
            }

            foreach (string term in terms)
            {
                if (FindSearchMatchIndex(candidate, term) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static int ComputeSearchScore(string candidateName, string filter, int matchIndex, int depth)
        {
            bool exact = string.Equals(candidateName, filter, StringComparison.OrdinalIgnoreCase);
            bool prefix = candidateName.StartsWith(filter, StringComparison.OrdinalIgnoreCase);
            bool wordPrefix = matchIndex == 0 ||
                (matchIndex > 0 && !char.IsLetterOrDigit(candidateName[matchIndex - 1]));

            int score = 0;
            if (!exact)
            {
                score += 100;
            }

            if (!prefix)
            {
                score += 28;
            }

            if (!wordPrefix)
            {
                score += 12;
            }

            score += Math.Min(64, matchIndex * 4);
            score += Math.Min(30, Math.Max(0, depth - 1) * 3);
            score += Math.Min(22, Math.Abs(candidateName.Length - filter.Length));
            return score;
        }

        private static int ComputePathSearchScore(string relativePath, string filter, int matchIndex, int depth)
        {
            bool prefix = matchIndex == 0;
            bool segmentPrefix = matchIndex == 0 ||
                (matchIndex > 0 &&
                    (relativePath[matchIndex - 1] == Path.DirectorySeparatorChar ||
                     relativePath[matchIndex - 1] == Path.AltDirectorySeparatorChar ||
                     !char.IsLetterOrDigit(relativePath[matchIndex - 1])));

            int score = 240;
            if (!prefix)
            {
                score += 48;
            }

            if (!segmentPrefix)
            {
                score += 20;
            }

            score += Math.Min(120, matchIndex * 2);
            score += Math.Min(36, Math.Max(0, depth - 1) * 4);
            score += Math.Min(36, Math.Abs(relativePath.Length - filter.Length));
            return score;
        }

        private static FolderSearchMatch? TryCreateSearchMatch(FolderSearchIndexEntry entry,
            IReadOnlyList<string> terms, string root, bool showExtensions)
        {
            if (terms.Count == 0)
            {
                return null;
            }

            string displayName = GetSearchDisplayName(entry, showExtensions);
            string fullName = entry.Name;
            string relativePath = string.IsNullOrWhiteSpace(entry.RelativePath)
                ? BuildRelativeSearchPath(root, entry.Path, entry.Name)
                : entry.RelativePath;
            int totalScore = 0;

            foreach (string term in terms)
            {
                int displayIndex = FindSearchMatchIndex(displayName, term);
                if (displayIndex >= 0)
                {
                    totalScore += ComputeSearchScore(displayName, term, displayIndex, entry.Depth);
                    continue;
                }

                if (!string.Equals(displayName, fullName, StringComparison.OrdinalIgnoreCase))
                {
                    int fullNameIndex = FindSearchMatchIndex(fullName, term);
                    if (fullNameIndex >= 0)
                    {
                        totalScore += ComputeSearchScore(fullName, term, fullNameIndex, entry.Depth) + 10;
                        continue;
                    }
                }

                int relativePathIndex = FindSearchMatchIndex(relativePath, term);
                if (relativePathIndex >= 0)
                {
                    totalScore += ComputePathSearchScore(relativePath, term, relativePathIndex, entry.Depth);
                    continue;
                }

                return null;
            }

            return new FolderSearchMatch
            {
                Path = entry.Path,
                SortName = displayName,
                Depth = entry.Depth,
                Score = totalScore + Math.Min(24, Math.Max(0, terms.Count - 1) * 6)
            };
        }

        private static List<string> FinalizeSearchMatches(IEnumerable<FolderSearchMatch> matches)
        {
            return matches
                .OrderBy(match => match.Score)
                .ThenBy(match => match.Depth)
                .ThenBy(match => match.SortName, StringComparer.OrdinalIgnoreCase)
                .Select(match => match.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(SearchResultLimit)
                .ToList();
        }

        private Task<List<string>> GetIndexedMatchesAsync(string root, string filter, CancellationToken token)
        {
            var service = SearchIndexService;
            bool showExtensions = showFileExtensions;
            IReadOnlyList<string> terms = GetSearchTerms(filter);
            return Task.Run(async () =>
            {
                var matches = new List<FolderSearchMatch>(SearchResultLimit * 2);
                await foreach (var entry in service.SearchEntriesAsync(root, token).ConfigureAwait(false))
                {
                    FolderSearchMatch? match = TryCreateSearchMatch(entry, terms, root, showExtensions);
                    if (match != null) matches.Add(match);
                    if (matches.Count >= SearchResultLimit * 4)
                        matches = matches.OrderBy(match => match.Score).ThenBy(match => match.Depth)
                            .ThenBy(match => match.SortName, StringComparer.OrdinalIgnoreCase)
                            .Take(SearchResultLimit * 2).ToList();
                }
                token.ThrowIfCancellationRequested();
                return FinalizeSearchMatches(matches);
            }, token);
        }

        private List<string>? TryGetIndexedVisibleFolderEntries(string folderPath)
        {
            if (FolderListingCache.TryGet(folderPath, out IReadOnlyList<string> cachedPaths))
                return cachedPaths.Where(ShouldShowPath).ToList();
            var paths = SearchIndexService.GetDirectChildren(folderPath);
            return paths?.Where(ShouldShowPath).ToList();
        }

        internal IEnumerable<string> GetFolderPathsForBackgroundListingWarmup()
        {
            if (PanelType == PanelKind.Folder && !string.IsNullOrWhiteSpace(currentFolderPath))
                yield return NormalizeFolderSearchIndexRoot(currentFolderPath);
            foreach (var tab in _tabs)
            {
                bool folder = Enum.TryParse<PanelKind>(tab.PanelType, true, out var kind)
                    ? kind == PanelKind.Folder : !string.IsNullOrWhiteSpace(tab.FolderPath);
                if (folder && !string.IsNullOrWhiteSpace(tab.FolderPath))
                    yield return NormalizeFolderSearchIndexRoot(tab.FolderPath);
            }
        }

        private void ScheduleBackgroundFolderListingWarmup()
        {
            if (!IsPreviewPanel && IsLoaded && MainWindow.IsUiReadyForBackgroundWork)
                MainWindow.RefreshConfiguredSearchFolders();
        }
        private static string BuildUniqueDirectoryTargetPath(string destinationDirectory, string requestedName)
        {
            string targetPath = Path.Combine(destinationDirectory, requestedName);
            if (!Directory.Exists(targetPath))
            {
                return targetPath;
            }

            int counter = 1;
            string baseName = requestedName;
            while (Directory.Exists(targetPath))
            {
                targetPath = Path.Combine(destinationDirectory, $"{baseName}_{counter++}");
            }

            return targetPath;
        }

        private static string BuildUniqueFileTargetPath(string destinationDirectory, string fileName)
        {
            string targetPath = Path.Combine(destinationDirectory, fileName);
            if (!File.Exists(targetPath))
            {
                return targetPath;
            }

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int counter = 1;

            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(destinationDirectory, $"{baseName}_{counter++}{extension}");
            }

            return targetPath;
        }

        private static bool TryTransferFolderToDirectory(
            string sourcePath,
            string destinationDirectory,
            bool move,
            out string? transferredPath,
            out string? errorMessage)
        {
            transferredPath = null;
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(sourcePath) ||
                !Directory.Exists(destinationDirectory))
            {
                return false;
            }

            string folderName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            string targetPath = BuildUniqueDirectoryTargetPath(destinationDirectory, folderName);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                if (move)
                {
                    try
                    {
                        Directory.Move(sourcePath, targetPath);
                    }
                    catch (IOException)
                    {
                        CopyDirectoryRecursive(sourcePath, targetPath);
                        Directory.Delete(sourcePath, true);
                    }
                }
                else
                {
                    CopyDirectoryRecursive(sourcePath, targetPath);
                }

                transferredPath = targetPath;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static bool TryTransferFileToDirectory(
            string sourcePath,
            string destinationDirectory,
            bool move,
            out string? transferredPath,
            out string? errorMessage)
        {
            transferredPath = null;
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !File.Exists(sourcePath) ||
                !Directory.Exists(destinationDirectory))
            {
                return false;
            }

            string fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            string targetPath = BuildUniqueFileTargetPath(destinationDirectory, fileName);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                if (move)
                {
                    ShortcutFileTransfer.MoveFile(sourcePath, targetPath);
                }
                else
                {
                    ShortcutFileTransfer.CopyFile(sourcePath, targetPath, overwrite: false);
                }

                transferredPath = targetPath;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private bool MoveFolderIntoCurrent(string sourcePath, bool refreshAfterChange = true)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return false;

            if (TryTransferFolderToDirectory(sourcePath, currentFolderPath, move: true, out string? targetPath, out string? errorMessage))
            {
                if (refreshAfterChange && !string.IsNullOrWhiteSpace(targetPath))
                {
                    NotifyFolderContentChangeImmediate(FolderWatcherChangeKind.Created, targetPath);
                }
                return true;
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFolderError"), errorMessage),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
        }

        private bool MoveFileIntoCurrent(string sourcePath, bool refreshAfterChange = true)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return false;

            if (TryTransferFileToDirectory(sourcePath, currentFolderPath, move: true, out string? targetPath, out string? errorMessage))
            {
                if (refreshAfterChange && !string.IsNullOrWhiteSpace(targetPath))
                {
                    NotifyFolderContentChangeImmediate(FolderWatcherChangeKind.Created, targetPath);
                }
                return true;
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFileError"), errorMessage),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
        }

        private bool CopyFolderIntoCurrent(string sourcePath, bool refreshAfterChange = true)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return false;

            if (TryTransferFolderToDirectory(sourcePath, currentFolderPath, move: false, out string? targetPath, out string? errorMessage))
            {
                if (refreshAfterChange && !string.IsNullOrWhiteSpace(targetPath))
                {
                    NotifyFolderContentChangeImmediate(FolderWatcherChangeKind.Created, targetPath);
                }
                return true;
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFolderError"), errorMessage),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
        }

        private bool CopyFileIntoCurrent(string sourcePath, bool refreshAfterChange = true)
        {
            if (string.IsNullOrWhiteSpace(currentFolderPath) || !Directory.Exists(currentFolderPath)) return false;

            if (TryTransferFileToDirectory(sourcePath, currentFolderPath, move: false, out string? targetPath, out string? errorMessage))
            {
                if (refreshAfterChange && !string.IsNullOrWhiteSpace(targetPath))
                {
                    NotifyFolderContentChangeImmediate(FolderWatcherChangeKind.Created, targetPath);
                }
                return true;
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                System.Windows.MessageBox.Show(
                    string.Format(MainWindow.GetString("Loc.MsgMoveFileError"), errorMessage),
                    MainWindow.GetString("Loc.MsgError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
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

        private static bool ShouldIndexPath(string path)
        {
            if (!TryGetPathAttributes(path, out var attrs))
            {
                return true;
            }

            return !attrs.HasFlag(FileAttributes.System);
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
            string displayName = TryGetRecycleBinDisplayNameForDataPath(path, out string recycleBinDisplayName)
                ? recycleBinDisplayName
                : GetPathLeafName(path);
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

        public bool LoadFolder(
            string folderPath,
            bool saveSettings = true,
            bool renamePanelTitle = false,
            bool preserveSearch = false)
        {
            if (!Directory.Exists(folderPath))
            {
                return false;
            }

            _contentViewGeneration++;
            var loadCts = BeginFolderLoad();
            CancelPendingFolderSearchIndex();
            CancelPendingRecycleBinLoad();
            StopRecycleBinWatchers();
            ResetSearchState(clearSearchBox: !preserveSearch, removeInjectedItems: false);
            PanelType = PanelKind.Folder;
            currentFolderPath = folderPath;
            _currentFolderHasEntries = false;
            StartOrUpdateFolderWatchers(folderPath);
            _useLightweightItemVisuals = false;
            PinnedItems.Clear();

            this.Title = $"{GetFolderDisplayName(folderPath)}";
            if (renamePanelTitle)
            {
                SetPanelTitleFromFolderPath(folderPath);
            }

            FileList.Items.Clear();
            _baseItemPaths.Clear();
            _detailsDefaultOrderPaths.Clear();
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();

            EnsureParentNavigationItemState();

            RefreshDetailsHeader();
            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateDropZoneVisibility();
            UpdateEmptyRecycleBinButtonVisibility();
            StartFolderLoad(folderPath, loadCts);
            ScheduleBackgroundFolderListingWarmup();

            if (saveSettings)
            {
                MainWindow.SaveSettings();
            }

            return true;
        }

        public void LoadList(IEnumerable<string> items, bool saveSettings = true)
        {
            _contentViewGeneration++;
            CancelPendingFolderLoad();
            CancelPendingFolderSearchIndex();
            CancelPendingRecycleBinLoad();
            StopFolderWatchers();
            StopRecycleBinWatchers();
            ResetSearchState(clearSearchBox: true);
            PanelType = PanelKind.List;
            currentFolderPath = "";
            _useLightweightItemVisuals = false;
            FileList.Items.Clear();
            PinnedItems.Clear();
            _baseItemPaths.Clear();
            _detailsDefaultOrderPaths.Clear();
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();

            foreach (var item in items)
            {
                AddFileToList(item, true);
            }

            RefreshDetailsHeader();
            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateDropZoneVisibility();
            UpdateEmptyRecycleBinButtonVisibility();

            if (saveSettings)
            {
                MainWindow.SaveSettings();
            }
        }

        public void ClearPanelItems()
        {
            _contentViewGeneration++;
            CancelPendingFolderLoad();
            CancelPendingRecycleBinLoad();
            StopFolderWatchers();
            ResetSearchState(clearSearchBox: true);
            PanelType = PanelKind.None;
            currentFolderPath = "";
            _useLightweightItemVisuals = false;
            PinnedItems.Clear();
            FileList.Items.Clear();
            _baseItemPaths.Clear();
            _detailsDefaultOrderPaths.Clear();
            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();
            RefreshDetailsHeader();
            UpdateDropZoneVisibility();
        }

        private async Task RunFolderLoadAsync(string folderPath, CancellationTokenSource cts)
        {
            var token = cts.Token;

            try
            {
                List<string>? indexedEntries = TryGetIndexedVisibleFolderEntries(folderPath);
                List<string> entries = indexedEntries ??
                    await Task.Run(() => EnumerateVisibleFolderEntries(folderPath, token));
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (!IsFolderLoadRequestCurrent(cts, folderPath) || entries.Count == 0)
                {
                    return;
                }

                bool useLightweightVisuals = entries.Count >= FolderLightweightVisualThreshold;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (IsFolderLoadRequestCurrent(cts, folderPath))
                    {
                        _useLightweightItemVisuals = useLightweightVisuals;
                        _detailsDefaultOrderPaths.Clear();
                        _detailsDefaultOrderPaths.AddRange(entries);
                    }
                }, System.Windows.Threading.DispatcherPriority.Send, token);

                int uiBatchSize = GetFolderLoadBatchSize();
                for (int start = 0; start < entries.Count; start += uiBatchSize)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

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

                        bool isPhotoMode = string.Equals(
                            NormalizeViewMode(viewMode),
                            ViewModePhotos,
                            StringComparison.OrdinalIgnoreCase);
                        string activeFilter = SearchBox?.Text?.Trim() ?? string.Empty;
                        IReadOnlyList<string> activeTerms = GetSearchTerms(activeFilter);
                        foreach (string entryPath in batch)
                        {
                            if (_baseItemPaths.Contains(entryPath))
                            {
                                continue;
                            }

                            string displayName = GetDisplayNameForPath(entryPath);
                            if (string.IsNullOrWhiteSpace(displayName))
                            {
                                displayName = entryPath;
                            }

                            var listItem = CreateFileListBoxItem(
                                displayName,
                                entryPath,
                                isBackButton: false,
                                _currentAppearance);

                            if (!MatchesSearchTerms(displayName, activeTerms))
                            {
                                listItem.Visibility = Visibility.Collapsed;
                            }

                            FileList.Items.Add(listItem);
                            _baseItemPaths.Add(entryPath);
                        }

                        if (isPhotoMode)
                        {
                            // Keep collage layout progressive while folder items stream in.
                            UpdateWrapPanelWidth();
                        }
                    }, System.Windows.Threading.DispatcherPriority.ContextIdle);

                    if (start + uiBatchSize < entries.Count)
                    {
                        await Task.Delay(FolderUiBatchDelayMs);
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
                    UpdateEmptyRecycleBinButtonVisibility();

                    string currentSearch = SearchBox?.Text ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(currentSearch))
                    {
                        BeginSearch(currentSearch);
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
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
                if (ReferenceEquals(_runningFolderLoadCts, cts))
                {
                    _runningFolderLoadCts = null;
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
            bool completed = false;
            try
            {
                foreach (string directoryPath in Directory.EnumerateDirectories(folderPath))
                {
                    if (token.IsCancellationRequested)
                    {
                        return entries;
                    }

                    if (ShouldIndexPath(directoryPath))
                    {
                        entries.Add(directoryPath);
                    }
                }

                foreach (string filePath in Directory.EnumerateFiles(folderPath))
                {
                    if (token.IsCancellationRequested)
                    {
                        return entries;
                    }

                    if (ShouldIndexPath(filePath))
                    {
                        entries.Add(filePath);
                    }
                }

                completed = true;
            }
            catch
            {
            }

            if (completed && !token.IsCancellationRequested)
            {
                FolderListingCache.Store(folderPath, entries);
            }

            return entries
                .Where(ShouldShowPath)
                .ToList();
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

            ListBoxItem item = CreateFileListBoxItem(
                displayName,
                filePath,
                isBackButton: false,
                _currentAppearance);
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

        private void OpenPanelItemPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (PanelType == PanelKind.RecycleBin)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
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

        private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_renameEditBox != null)
            {
                e.Handled = true;
                return;
            }

            if (openItemsOnSingleClick)
            {
                return;
            }

            var clickedItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (clickedItem?.Tag is string path && !string.IsNullOrWhiteSpace(path))
            {
                OpenPanelItemPath(path);
                e.Handled = true;
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

        private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _ = Dispatcher.BeginInvoke(
                new Action(CollapseCompactSearchIfPossible),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void RefreshActiveSearch()
        {
            if (_isClosed) return;
            UpdateSearchIndexStatus();
            string filter = SearchBox?.Text ?? string.Empty;
            if (filter.Trim().Length < SearchMinCharsForDeepLookup) return;
            if (_searchCts != null)
            {
                // Let a lookup finish even when the index publishes more entries.
                _searchRefreshPending = true;
                return;
            }
            BeginSearch(filter);
        }

        private void BeginSearch(string rawFilter)
        {
            if (_isClosed) return;
            _searchRefreshPending = false;
            UpdateSearchIndexStatus();
            if (string.IsNullOrWhiteSpace(rawFilter) || rawFilter.Trim().Length < SearchMinCharsForDeepLookup)
            {
                CancelPendingFolderSearchIndex();
            }
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
                string filter = rawFilter.Trim();
                bool localChanged = await ApplyLocalSearchFilterAsync(filter, cts, token);
                token.ThrowIfCancellationRequested();
                if (!IsSearchRequestCurrent(cts)) return;
                if (localChanged) QueueWrapPanelWidthUpdate();

                if (string.IsNullOrWhiteSpace(filter) ||
                    filter.Length < SearchMinCharsForDeepLookup ||
                    PanelType != PanelKind.Folder ||
                    string.IsNullOrWhiteSpace(currentFolderPath))
                {
                    if (RemoveInjectedSearchItems()) QueueWrapPanelWidthUpdate();
                    return;
                }

                string root = currentFolderPath;
                UpdateSearchIndexStatus();
                List<string> results = await GetIndexedMatchesAsync(root, filter, token);

                token.ThrowIfCancellationRequested();
                if (!IsSearchRequestCurrent(cts))
                {
                    return;
                }

                await SynchronizeInjectedSearchResultsAsync(results, cts, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Search failed for panel '{PanelId}': {ex}");
            }
            finally
            {
                bool shouldApplyDeferredSort = ReferenceEquals(_searchCts, cts) && _deferSortUntilSearchComplete;
                if (shouldApplyDeferredSort)
                {
                    _deferSortUntilSearchComplete = false;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsSearchRequestCurrent(cts)) return;
                        SortCurrentFolderItemsInPlace();
                        RefreshParentNavigationItemVisual();
                        _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }

                bool isCurrent = IsSearchRequestCurrent(cts);
                if (isCurrent) _searchCts = null;
                cts.Dispose();
                if (isCurrent && _searchRefreshPending) RefreshActiveSearch();
            }
        }

        private void ResetSearchState(bool clearSearchBox, bool removeInjectedItems = true)
        {
            SearchIndexStatus.Visibility = Visibility.Collapsed;
            CancelPendingFolderSearchIndex();
            var pendingSearchCts = _searchCts;
            _searchCts = null;
            pendingSearchCts?.Cancel();
            _searchRefreshPending = false;
            _deferSortUntilSearchComplete = false;
            _isSearchExpandedFromCompactButton = false;
            if (removeInjectedItems)
            {
                RemoveInjectedSearchItems();
            }
            else
            {
                // The caller is about to clear FileList in one operation. Avoid removing a
                // potentially large injected result set item-by-item on the dispatcher.
                _searchInjectedItems.Clear();
                _searchInjectedPaths.Clear();
            }

            if (!clearSearchBox || SearchBox == null || string.IsNullOrEmpty(SearchBox.Text))
            {
                ApplySearchVisibility(animate: false);
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

            ApplySearchVisibility(animate: false);
        }

        private void RestoreUnfilteredPanelItems()
        {
            RemoveInjectedSearchItems();
            foreach (var item in FileList.Items.OfType<ListBoxItem>())
            {
                item.Visibility = IsParentNavigationItem(item)
                    ? (ShouldShowParentNavigationListItem() ? Visibility.Visible : Visibility.Collapsed)
                    : Visibility.Visible;
            }

            _ = Dispatcher.BeginInvoke(new Action(UpdateWrapPanelWidth), System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task<bool> ApplyLocalSearchFilterAsync(string filter, CancellationTokenSource cts, CancellationToken token)
        {
            IReadOnlyList<string> terms = GetSearchTerms(filter);
            var items = await Dispatcher.InvokeAsync(() =>
            {
                var injected = _searchInjectedItems.ToHashSet();
                return FileList.Items
                    .OfType<ListBoxItem>()
                    .Where(item => !injected.Contains(item))
                    .Select(item => (
                        Item: item,
                        IsParentNavigationItem: IsParentNavigationItem(item),
                        CandidateText: GetSearchCandidateText(item),
                        Visibility: item.Visibility))
                    .ToList();
            }, System.Windows.Threading.DispatcherPriority.Background, token);

            token.ThrowIfCancellationRequested();
            if (!IsSearchRequestCurrent(cts) || items.Count == 0) return false;
            bool showParent = ShouldShowParentNavigationListItem();
            var changes = await Task.Run(() =>
            {
                var changed = new List<(ListBoxItem Item, Visibility Target)>();
                foreach (var entry in items)
                {
                    token.ThrowIfCancellationRequested();
                    bool visible = entry.IsParentNavigationItem
                        ? showParent
                        : MatchesSearchTerms(entry.CandidateText, terms);
                    Visibility target = visible ? Visibility.Visible : Visibility.Collapsed;
                    if (entry.Visibility != target) changed.Add((entry.Item, target));
                }
                return changed;
            }, token);

            bool uiChanged = false;
            for (int start = 0; start < changes.Count; start += SearchFilterBatchSize)
            {
                token.ThrowIfCancellationRequested();
                int end = Math.Min(changes.Count, start + SearchFilterBatchSize);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSearchRequestCurrent(cts)) return;
                    for (int i = start; i < end; i++)
                    {
                        var change = changes[i];
                        if (change.Item.Visibility == change.Target) continue;
                        change.Item.Visibility = change.Target;
                        uiChanged = true;
                    }
                }, System.Windows.Threading.DispatcherPriority.Background, token);
            }
            return uiChanged;
        }

        private bool RemoveInjectedSearchItems()
        {
            if (_searchInjectedItems.Count == 0)
            {
                _searchInjectedPaths.Clear();
                return false;
            }

            foreach (var item in _searchInjectedItems)
            {
                FileList.Items.Remove(item);
            }

            _searchInjectedItems.Clear();
            _searchInjectedPaths.Clear();
            return true;
        }

        private async Task SynchronizeInjectedSearchResultsAsync(IEnumerable<string> results, CancellationTokenSource cts, CancellationToken token)
        {
            bool showHidden = showHiddenItems;
            bool showExtensions = showFileExtensions;
            var basePaths = _baseItemPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = await Task.Run(() =>
            {
                var found = new List<(string Path, string DisplayName)>();
                foreach (string path in results.Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(SearchResultLimit))
                {
                    token.ThrowIfCancellationRequested();
                    if (basePaths.Contains(path)) continue;
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.System) != 0 ||
                            (!showHidden && (attributes & FileAttributes.Hidden) != 0)) continue;
                        string name = GetPathLeafName(path);
                        if (!showExtensions && (attributes & FileAttributes.Directory) == 0)
                        {
                            string withoutExtension = Path.GetFileNameWithoutExtension(name);
                            if (!string.IsNullOrWhiteSpace(withoutExtension)) name = withoutExtension;
                        }
                        found.Add((path, name));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return found;
            }, token);

            token.ThrowIfCancellationRequested();
            if (!IsSearchRequestCurrent(cts)) return;
            var desiredPaths = candidates.Select(candidate => candidate.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = _searchInjectedItems.Where(item => item.Tag is not string path ||
                !desiredPaths.Contains(path) || _baseItemPaths.Contains(path)).ToArray();
            var added = candidates.Where(candidate => !_searchInjectedPaths.Contains(candidate.Path)).ToList();
            bool changed = removed.Length > 0;

            foreach (var item in removed)
            {
                FileList.Items.Remove(item);
                _searchInjectedItems.Remove(item);
                if (item.Tag is string path) _searchInjectedPaths.Remove(path);
            }

            var candidatesByPath = candidates.ToDictionary(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var item in _searchInjectedItems)
            {
                string oldPath = (string)item.Tag;
                var candidate = candidatesByPath[oldPath];
                if (string.Equals(oldPath, candidate.Path, StringComparison.Ordinal) &&
                    string.Equals(GetSearchCandidateText(item), candidate.DisplayName, StringComparison.Ordinal)) continue;
                item.Tag = candidate.Path;
                item.Content = CreateListBoxItem(candidate.DisplayName, candidate.Path, isBackButton: false, _currentAppearance);
                ApplyListItemContainerSpacing(item);
                _searchInjectedPaths.Remove(oldPath);
                _searchInjectedPaths.Add(candidate.Path);
                changed = true;
            }

            for (int start = 0; start < added.Count; start += SearchResultBatchSize)
            {
                token.ThrowIfCancellationRequested();
                var batch = added
                    .Skip(start)
                    .Take(SearchResultBatchSize)
                    .ToArray();

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSearchRequestCurrent(cts))
                    {
                        return;
                    }

                    foreach (var candidate in batch)
                    {
                        string foundPath = candidate.Path;
                        if (_baseItemPaths.Contains(foundPath) || _searchInjectedPaths.Contains(foundPath))
                        {
                            continue;
                        }

                        var listItem = CreateFileListBoxItem(
                            candidate.DisplayName,
                            foundPath,
                            isBackButton: false,
                            _currentAppearance);

                        FileList.Items.Add(listItem);
                        _searchInjectedPaths.Add(foundPath);
                        _searchInjectedItems.Add(listItem);
                        changed = true;
                    }
                }, System.Windows.Threading.DispatcherPriority.Background, token);
            }

            token.ThrowIfCancellationRequested();
            if (!IsSearchRequestCurrent(cts)) return;
            var injectedByPath = _searchInjectedItems.ToDictionary(item => (string)item.Tag, StringComparer.OrdinalIgnoreCase);
            var desiredInjected = candidates.Where(candidate => injectedByPath.ContainsKey(candidate.Path))
                .Select(candidate => injectedByPath[candidate.Path]).ToList();
            bool orderChanged = !_searchInjectedItems.SequenceEqual(desiredInjected);
            if (orderChanged)
            {
                _searchInjectedItems.Clear();
                _searchInjectedItems.AddRange(desiredInjected);
                if (!_detailsSortActive)
                {
                    var injected = desiredInjected.ToHashSet();
                    var desiredOrder = FileList.Items.OfType<ListBoxItem>().Where(item => !injected.Contains(item)).ToList();
                    desiredOrder.AddRange(desiredInjected);
                    var selectedPaths = FileList.SelectedItems.OfType<ListBoxItem>().Select(item => item.Tag)
                        .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    ApplyFileListOrderInPlace(desiredOrder, selectedPaths);
                }
            }
            if (changed || orderChanged)
            {
                SortCurrentFolderItemsInPlace();
                QueueWrapPanelWidthUpdate();
            }
        }

        private string GetSearchCandidateText(ListBoxItem item)
        {
            if (TryGetItemNameLabel(item, out var labelText) &&
                !string.IsNullOrWhiteSpace(labelText.Text))
            {
                return labelText.Text;
            }

            if (item.Tag is string path && !string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    string displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    return string.IsNullOrWhiteSpace(displayName) ? path : displayName;
                }
                catch
                {
                    return path;
                }
            }

            return string.Empty;
        }

    }
}
