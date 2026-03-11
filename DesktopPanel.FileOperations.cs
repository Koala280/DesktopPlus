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
        private const int GlobalSearchIndexMaxRoots = 32;
        private const int PersistedSearchIndexMagic = 0x44505349;
        private const int PersistedSearchIndexVersion = 2;
        private const int PersistedSearchIndexMaxFiles = 64;
        private const int FolderUiBatchSizeDefault = 8;
        private const int FolderUiBatchSizePhotos = 3;
        private const int FolderUiBatchDelayMs = 1;
        private const int FolderLightweightVisualThreshold = 700;
        private static readonly TimeSpan GlobalSearchIndexRetention = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan PersistedSearchIndexRetention = TimeSpan.FromDays(21);
        private static readonly object GlobalFolderSearchIndexLock = new object();
        private static readonly string GlobalSearchIndexCacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPlus",
            "SearchIndex");
        private static readonly Dictionary<string, GlobalFolderSearchIndexState> GlobalFolderSearchIndices =
            new Dictionary<string, GlobalFolderSearchIndexState>(StringComparer.OrdinalIgnoreCase);

        private sealed class FolderSearchIndexEntry
        {
            public string Path { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string RelativePath { get; init; } = string.Empty;
            public bool IsDirectory { get; init; }
            public int Depth { get; init; }
        }

        private sealed class FolderSearchMatch
        {
            public string Path { get; init; } = string.Empty;
            public string SortName { get; init; } = string.Empty;
            public int Depth { get; init; }
            public int Score { get; init; }
        }

        private sealed class GlobalFolderSearchIndexState
        {
            public string RootPath { get; init; } = string.Empty;
            public List<FolderSearchIndexEntry> Entries { get; set; } = new List<FolderSearchIndexEntry>();
            public bool IsComplete { get; set; }
            public bool IsDirty { get; set; }
            public bool RequiresRefresh { get; set; }
            public CancellationTokenSource? BuildCts { get; set; }
            public DateTime LastBuildUtc { get; set; }
            public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
        }

        private sealed class PersistedFolderSearchIndexSnapshot
        {
            public DateTime BuiltUtc { get; init; }
            public List<FolderSearchIndexEntry> Entries { get; init; } = new List<FolderSearchIndexEntry>();
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

        private void CancelPendingFolderSearchIndex()
        {
            // The search index is global to the app now and intentionally survives
            // panel switches/closes so later searches stay warm.
        }

        private static string NormalizeFolderSearchIndexRoot(string folderPath)
        {
            try
            {
                return Path.GetFullPath(folderPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static string BuildRelativeSearchPath(string rootPath, string entryPath, string? fallbackName = null)
        {
            string safeFallback = !string.IsNullOrWhiteSpace(fallbackName)
                ? fallbackName
                : GetPathLeafName(entryPath);

            try
            {
                string relativePath = Path.GetRelativePath(rootPath, entryPath)
                    .Trim()
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.IsNullOrWhiteSpace(relativePath) ||
                    string.Equals(relativePath, ".", StringComparison.Ordinal))
                {
                    return safeFallback;
                }

                return relativePath;
            }
            catch
            {
                return safeFallback;
            }
        }

        private static void TrimGlobalFolderSearchIndexCache(string? preferredRoot = null)
        {
            lock (GlobalFolderSearchIndexLock)
            {
                DateTime cutoff = DateTime.UtcNow - GlobalSearchIndexRetention;

                foreach (var staleRoot in GlobalFolderSearchIndices
                    .Where(pair =>
                        !string.Equals(pair.Key, preferredRoot, StringComparison.OrdinalIgnoreCase) &&
                        pair.Value.BuildCts == null &&
                        pair.Value.LastAccessUtc < cutoff)
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    GlobalFolderSearchIndices.Remove(staleRoot);
                }

                while (GlobalFolderSearchIndices.Count > GlobalSearchIndexMaxRoots)
                {
                    var candidate = GlobalFolderSearchIndices
                        .Where(pair =>
                            !string.Equals(pair.Key, preferredRoot, StringComparison.OrdinalIgnoreCase) &&
                            pair.Value.BuildCts == null)
                        .OrderBy(pair => pair.Value.LastAccessUtc)
                        .FirstOrDefault();

                    if (string.IsNullOrWhiteSpace(candidate.Key))
                    {
                        break;
                    }

                    GlobalFolderSearchIndices.Remove(candidate.Key);
                }
            }
        }

        private static string GetFolderSearchIndexCachePath(string folderPath)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(folderPath));
            string fileName = $"{Convert.ToHexString(hash)}.bin";
            return Path.Combine(GlobalSearchIndexCacheDirectory, fileName);
        }

        private static void DeletePersistedFolderSearchIndex(string folderPath)
        {
            try
            {
                string cachePath = GetFolderSearchIndexCachePath(folderPath);
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            catch
            {
            }
        }

        private static PersistedFolderSearchIndexSnapshot? TryLoadPersistedFolderSearchIndex(string folderPath)
        {
            try
            {
                string cachePath = GetFolderSearchIndexCachePath(folderPath);
                if (!File.Exists(cachePath))
                {
                    return null;
                }

                using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

                if (reader.ReadInt32() != PersistedSearchIndexMagic)
                {
                    return null;
                }

                int persistedVersion = reader.ReadInt32();
                if (persistedVersion != 1 &&
                    persistedVersion != PersistedSearchIndexVersion)
                {
                    return null;
                }

                string storedRoot = reader.ReadString();
                if (!string.Equals(storedRoot, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                long builtTicks = reader.ReadInt64();
                int entryCount = reader.ReadInt32();
                if (entryCount < 0 || entryCount > 2_000_000)
                {
                    return null;
                }

                var entries = new List<FolderSearchIndexEntry>(entryCount);
                for (int i = 0; i < entryCount; i++)
                {
                    string path = reader.ReadString();
                    string name = reader.ReadString();
                    bool isDirectory = reader.ReadBoolean();
                    int depth = reader.ReadInt32();

                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    entries.Add(new FolderSearchIndexEntry
                    {
                        Path = path,
                        Name = string.IsNullOrWhiteSpace(name) ? GetPathLeafName(path) : name,
                        RelativePath = persistedVersion >= 2
                            ? reader.ReadString()
                            : BuildRelativeSearchPath(storedRoot, path, name),
                        IsDirectory = isDirectory,
                        Depth = Math.Max(0, depth)
                    });
                }

                DateTime builtUtc;
                try
                {
                    builtUtc = new DateTime(builtTicks, DateTimeKind.Utc);
                }
                catch
                {
                    builtUtc = DateTime.UtcNow;
                }

                return new PersistedFolderSearchIndexSnapshot
                {
                    BuiltUtc = builtUtc,
                    Entries = entries
                };
            }
            catch
            {
                return null;
            }
        }

        private static void PersistFolderSearchIndex(
            string folderPath,
            IReadOnlyList<FolderSearchIndexEntry> entries,
            DateTime builtUtc)
        {
            try
            {
                Directory.CreateDirectory(GlobalSearchIndexCacheDirectory);

                string cachePath = GetFolderSearchIndexCachePath(folderPath);
                string tempPath = cachePath + ".tmp";

                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
                {
                    writer.Write(PersistedSearchIndexMagic);
                    writer.Write(PersistedSearchIndexVersion);
                    writer.Write(folderPath);
                    writer.Write(builtUtc.Ticks);
                    writer.Write(entries.Count);

                    foreach (var entry in entries)
                    {
                        writer.Write(entry.Path ?? string.Empty);
                        writer.Write(entry.Name ?? string.Empty);
                        writer.Write(entry.IsDirectory);
                        writer.Write(entry.Depth);
                        writer.Write(entry.RelativePath ?? string.Empty);
                    }
                }

                File.Move(tempPath, cachePath, overwrite: true);
                TrimPersistedFolderSearchIndexCache(cachePath);
            }
            catch
            {
            }
        }

        private static void TrimPersistedFolderSearchIndexCache(string? preferredCachePath = null)
        {
            try
            {
                if (!Directory.Exists(GlobalSearchIndexCacheDirectory))
                {
                    return;
                }

                DateTime cutoff = DateTime.UtcNow - PersistedSearchIndexRetention;
                var cacheFiles = new DirectoryInfo(GlobalSearchIndexCacheDirectory)
                    .EnumerateFiles("*.bin", SearchOption.TopDirectoryOnly)
                    .ToList();

                foreach (var staleFile in cacheFiles
                    .Where(file =>
                        !string.Equals(file.FullName, preferredCachePath, StringComparison.OrdinalIgnoreCase) &&
                        file.LastWriteTimeUtc < cutoff)
                    .ToList())
                {
                    try
                    {
                        staleFile.Delete();
                    }
                    catch
                    {
                    }
                }

                cacheFiles = new DirectoryInfo(GlobalSearchIndexCacheDirectory)
                    .EnumerateFiles("*.bin", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToList();

                for (int i = PersistedSearchIndexMaxFiles; i < cacheFiles.Count; i++)
                {
                    FileInfo file = cacheFiles[i];
                    if (string.Equals(file.FullName, preferredCachePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private void InvalidateFolderSearchIndex(
            string folderPath,
            bool rebuildInBackground = true,
            bool rerunActiveSearch = false)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            string normalizedRoot = NormalizeFolderSearchIndexRoot(folderPath);
            CancellationTokenSource? buildToCancel = null;

            lock (GlobalFolderSearchIndexLock)
            {
                if (GlobalFolderSearchIndices.TryGetValue(normalizedRoot, out var state))
                {
                    buildToCancel = state.BuildCts;
                    state.BuildCts = null;
                    state.IsDirty = true;
                    state.IsComplete = false;
                    state.LastAccessUtc = DateTime.UtcNow;
                }
            }

            buildToCancel?.Cancel();

            if (rebuildInBackground)
            {
                EnsureFolderSearchIndexBuild(normalizedRoot);
            }
            else if (!Directory.Exists(normalizedRoot))
            {
                DeletePersistedFolderSearchIndex(normalizedRoot);
            }

            if (rerunActiveSearch)
            {
                RerunSearchForPanelsBoundToFolder(normalizedRoot);
            }
        }

        private void RerunSearchForPanelsBoundToFolder(string folderPath)
        {
            if (System.Windows.Application.Current == null)
            {
                return;
            }

            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var panel in System.Windows.Application.Current.Windows.OfType<DesktopPanel>())
                {
                    if (panel.PanelType != PanelKind.Folder ||
                        string.IsNullOrWhiteSpace(panel.currentFolderPath) ||
                        !string.Equals(
                            NormalizeFolderSearchIndexRoot(panel.currentFolderPath),
                            folderPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string activeSearch = panel.SearchBox?.Text ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(activeSearch))
                    {
                        panel.BeginSearch(activeSearch);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void EnsureFolderSearchIndexBuild(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            string normalizedRoot = NormalizeFolderSearchIndexRoot(folderPath);
            PersistedFolderSearchIndexSnapshot? persistedSnapshot = null;
            CancellationTokenSource? buildCts = null;
            bool shouldLoadPersistedSnapshot;

            lock (GlobalFolderSearchIndexLock)
            {
                shouldLoadPersistedSnapshot = !GlobalFolderSearchIndices.ContainsKey(normalizedRoot);
            }

            if (shouldLoadPersistedSnapshot)
            {
                persistedSnapshot = TryLoadPersistedFolderSearchIndex(normalizedRoot);
            }

            lock (GlobalFolderSearchIndexLock)
            {
                TrimGlobalFolderSearchIndexCache(normalizedRoot);

                if (!GlobalFolderSearchIndices.TryGetValue(normalizedRoot, out var state))
                {
                    state = new GlobalFolderSearchIndexState
                    {
                        RootPath = normalizedRoot
                    };

                    if (persistedSnapshot != null)
                    {
                        state.Entries = persistedSnapshot.Entries;
                        state.IsComplete = true;
                        state.IsDirty = false;
                        state.RequiresRefresh = true;
                        state.LastBuildUtc = persistedSnapshot.BuiltUtc;
                    }

                    GlobalFolderSearchIndices[normalizedRoot] = state;
                }

                state.LastAccessUtc = DateTime.UtcNow;
                if (state.BuildCts != null ||
                    (state.IsComplete && !state.IsDirty && !state.RequiresRefresh))
                {
                    return;
                }

                buildCts = new CancellationTokenSource();
                state.BuildCts = buildCts;
                state.RequiresRefresh = false;
            }

            _ = Task.Run(() => BuildFolderSearchIndexAsync(normalizedRoot, buildCts!), buildCts!.Token);
        }

        private async Task BuildFolderSearchIndexAsync(string folderPath, CancellationTokenSource cts)
        {
            var token = cts.Token;
            var builtEntries = new List<FolderSearchIndexEntry>();
            DateTime completedUtc;

            try
            {
                foreach (var entry in EnumerateRecursiveSearchEntries(folderPath, token))
                {
                    token.ThrowIfCancellationRequested();
                    builtEntries.Add(entry);
                }

                completedUtc = DateTime.UtcNow;
                bool shouldNotify = false;
                lock (GlobalFolderSearchIndexLock)
                {
                    if (!GlobalFolderSearchIndices.TryGetValue(folderPath, out var state) ||
                        !ReferenceEquals(state.BuildCts, cts))
                    {
                        return;
                    }

                    state.Entries = builtEntries;
                    state.IsComplete = true;
                    state.IsDirty = false;
                    state.RequiresRefresh = false;
                    state.BuildCts = null;
                    state.LastBuildUtc = completedUtc;
                    state.LastAccessUtc = completedUtc;
                    shouldNotify = true;
                }

                if (!shouldNotify)
                {
                    return;
                }

                PersistFolderSearchIndex(folderPath, builtEntries, completedUtc);
                await Dispatcher.InvokeAsync(() => RerunSearchForPanelsBoundToFolder(folderPath),
                    System.Windows.Threading.DispatcherPriority.Background,
                    token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Search index build failed for '{folderPath}': {ex}");

                lock (GlobalFolderSearchIndexLock)
                {
                    if (GlobalFolderSearchIndices.TryGetValue(folderPath, out var state) &&
                        ReferenceEquals(state.BuildCts, cts))
                    {
                        state.RequiresRefresh = true;
                        state.LastAccessUtc = DateTime.UtcNow;
                    }
                }
            }
            finally
            {
                lock (GlobalFolderSearchIndexLock)
                {
                    if (GlobalFolderSearchIndices.TryGetValue(folderPath, out var state) &&
                        ReferenceEquals(state.BuildCts, cts))
                    {
                        state.BuildCts = null;
                    }
                }

                cts.Dispose();
            }
        }

        private (List<FolderSearchIndexEntry> Entries, bool HasSnapshot, bool IsComplete) GetFolderSearchIndexSnapshot(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return (new List<FolderSearchIndexEntry>(), false, false);
            }

            string normalizedRoot = NormalizeFolderSearchIndexRoot(folderPath);

            lock (GlobalFolderSearchIndexLock)
            {
                if (!GlobalFolderSearchIndices.TryGetValue(normalizedRoot, out var state))
                {
                    return (new List<FolderSearchIndexEntry>(), false, false);
                }

                state.LastAccessUtc = DateTime.UtcNow;
                bool hasUsableSnapshot = !state.IsDirty &&
                    (state.Entries.Count > 0 || state.IsComplete);

                return (
                    new List<FolderSearchIndexEntry>(state.Entries),
                    hasUsableSnapshot,
                    hasUsableSnapshot && state.IsComplete);
            }
        }

        private IEnumerable<FolderSearchIndexEntry> EnumerateRecursiveSearchEntries(string root, CancellationToken token)
        {
            var pendingDirectories = new Queue<(string Path, int Depth)>();
            pendingDirectories.Enqueue((root, 0));

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            while (pendingDirectories.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (currentDirectory, depth) = pendingDirectories.Dequeue();

                IEnumerable<string> childDirectories;
                try
                {
                    childDirectories = Directory.EnumerateDirectories(currentDirectory, "*", options);
                }
                catch
                {
                    childDirectories = Array.Empty<string>();
                }

                foreach (string directoryPath in childDirectories)
                {
                    token.ThrowIfCancellationRequested();
                    pendingDirectories.Enqueue((directoryPath, depth + 1));
                    if (!ShouldIndexPath(directoryPath))
                    {
                        continue;
                    }

                    yield return new FolderSearchIndexEntry
                    {
                        Path = directoryPath,
                        Name = GetPathLeafName(directoryPath),
                        RelativePath = BuildRelativeSearchPath(root, directoryPath),
                        IsDirectory = true,
                        Depth = depth + 1
                    };
                }

                IEnumerable<string> childFiles;
                try
                {
                    childFiles = Directory.EnumerateFiles(currentDirectory, "*", options);
                }
                catch
                {
                    childFiles = Array.Empty<string>();
                }

                foreach (string filePath in childFiles)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ShouldIndexPath(filePath))
                    {
                        continue;
                    }

                    yield return new FolderSearchIndexEntry
                    {
                        Path = filePath,
                        Name = GetPathLeafName(filePath),
                        RelativePath = BuildRelativeSearchPath(root, filePath),
                        IsDirectory = false,
                        Depth = depth + 1
                    };
                }
            }
        }

        private string GetSearchDisplayName(FolderSearchIndexEntry entry)
        {
            if (entry.IsDirectory || showFileExtensions)
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

        private FolderSearchMatch? TryCreateSearchMatch(FolderSearchIndexEntry entry, string filter)
        {
            IReadOnlyList<string> terms = GetSearchTerms(filter);
            if (terms.Count == 0)
            {
                return null;
            }

            string displayName = GetSearchDisplayName(entry);
            string fullName = entry.Name;
            string relativePath = string.IsNullOrWhiteSpace(entry.RelativePath)
                ? BuildRelativeSearchPath(currentFolderPath, entry.Path, entry.Name)
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

        private List<string> GetIndexedMatches(string root, string filter, out bool hasSnapshot, out bool isComplete)
        {
            var snapshot = GetFolderSearchIndexSnapshot(root);
            hasSnapshot = snapshot.HasSnapshot;
            isComplete = snapshot.IsComplete;
            if (!snapshot.HasSnapshot)
            {
                return new List<string>();
            }

            return FinalizeSearchMatches(
                snapshot.Entries
                    .Select(entry => TryCreateSearchMatch(entry, filter))
                    .Where(match => match != null)
                    .Cast<FolderSearchMatch>());
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

        public void LoadFolder(string folderPath, bool saveSettings = true, bool renamePanelTitle = false)
        {
            if (!Directory.Exists(folderPath)) return;

            var loadCts = BeginFolderLoad();
            CancelPendingFolderSearchIndex();
            CancelPendingRecycleBinLoad();
            StopRecycleBinWatchers();
            ResetSearchState(clearSearchBox: true);
            PanelType = PanelKind.Folder;
            currentFolderPath = folderPath;
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
            EnsureFolderSearchIndexBuild(folderPath);
            _ = RunFolderLoadAsync(folderPath, loadCts);

            if (saveSettings)
            {
                MainWindow.SaveSettings();
            }
        }

        public void LoadList(IEnumerable<string> items, bool saveSettings = true)
        {
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
                List<string> entries = await Task.Run(() => EnumerateVisibleFolderEntries(folderPath, token));
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
                        bool hasFilter = !string.IsNullOrWhiteSpace(activeFilter);
                        foreach (string entryPath in batch)
                        {
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

                            if (hasFilter &&
                                displayName.IndexOf(activeFilter, StringComparison.OrdinalIgnoreCase) < 0)
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
                    if (token.IsCancellationRequested)
                    {
                        return entries;
                    }

                    if (ShouldShowPath(directoryPath))
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

                    if (ShouldShowPath(filePath))
                    {
                        entries.Add(filePath);
                    }
                }
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
                string filter = rawFilter.Trim();
                await ApplyLocalSearchFilterAsync(filter, cts, token);

                // Debounce only the expensive deep lookup. Local filtering should react immediately.
                await Task.Delay(260, token);

                if (!IsSearchRequestCurrent(cts) ||
                    string.IsNullOrWhiteSpace(filter) ||
                    filter.Length < SearchMinCharsForDeepLookup ||
                    PanelType != PanelKind.Folder ||
                    string.IsNullOrWhiteSpace(currentFolderPath) ||
                    !Directory.Exists(currentFolderPath))
                {
                    return;
                }

                EnsureFolderSearchIndexBuild(currentFolderPath);

                List<string> results = await Task.Run(() =>
                {
                    List<string> indexedMatches = GetIndexedMatches(currentFolderPath, filter, out bool hasSnapshot, out bool isComplete);
                    if (hasSnapshot && (indexedMatches.Count > 0 || isComplete))
                    {
                        return indexedMatches;
                    }

                    return EnumerateMatches(currentFolderPath, filter, token);
                }, token);

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
            catch (Exception ex)
            {
                Debug.WriteLine($"Search failed for panel '{PanelId}': {ex}");
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
            _isSearchExpandedFromCompactButton = false;
            RemoveInjectedSearchItems();

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

        private async Task ApplyLocalSearchFilterAsync(string filter, CancellationTokenSource cts, CancellationToken token)
        {
            IReadOnlyList<string> terms = GetSearchTerms(filter);
            List<(ListBoxItem Item, bool IsParentNavigationItem, string CandidateText)> items = await Dispatcher.InvokeAsync(() =>
            {
                if (!IsSearchRequestCurrent(cts))
                {
                    return new List<(ListBoxItem Item, bool IsParentNavigationItem, string CandidateText)>();
                }

                RemoveInjectedSearchItems();
                return FileList.Items
                    .OfType<ListBoxItem>()
                    .Select(item => (
                        Item: item,
                        IsParentNavigationItem: IsParentNavigationItem(item),
                        CandidateText: GetSearchCandidateText(item)))
                    .ToList();
            }, System.Windows.Threading.DispatcherPriority.Send, token);

            if (!IsSearchRequestCurrent(cts) || items.Count == 0)
            {
                return;
            }

            bool showAll = terms.Count == 0;
            if (showAll)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSearchRequestCurrent(cts))
                    {
                        return;
                    }

                    foreach (var entry in items)
                    {
                        bool isVisible = entry.IsParentNavigationItem
                            ? ShouldShowParentNavigationListItem()
                            : true;
                        var target = isVisible ? Visibility.Visible : Visibility.Collapsed;
                        if (entry.Item.Visibility != target)
                        {
                            entry.Item.Visibility = target;
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Send, token);
            }
            else
            {
                List<ListBoxItem> matchingItems = items
                    .Where(entry => !entry.IsParentNavigationItem &&
                        MatchesSearchTerms(entry.CandidateText, terms))
                    .Select(entry => entry.Item)
                    .ToList();

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsSearchRequestCurrent(cts))
                    {
                        return;
                    }

                    foreach (var entry in items)
                    {
                        Visibility target = entry.IsParentNavigationItem && ShouldShowParentNavigationListItem()
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                        if (entry.Item.Visibility != target)
                        {
                            entry.Item.Visibility = target;
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Send, token);

                for (int start = 0; start < matchingItems.Count; start += SearchFilterBatchSize)
                {
                    token.ThrowIfCancellationRequested();
                    int end = Math.Min(matchingItems.Count, start + SearchFilterBatchSize);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsSearchRequestCurrent(cts))
                        {
                            return;
                        }

                        for (int i = start; i < end; i++)
                        {
                            var item = matchingItems[i];
                            if (item.Visibility != Visibility.Visible)
                            {
                                item.Visibility = Visibility.Visible;
                            }
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background, token);

                    if (end < matchingItems.Count)
                    {
                        await Task.Delay(1, token);
                    }
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

                        var listItem = CreateFileListBoxItem(
                            displayName,
                            foundPath,
                            isBackButton: false,
                            _currentAppearance);

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

        private List<string> EnumerateMatches(string root, string filter, CancellationToken token)
        {
            var matches = new List<FolderSearchMatch>(SearchResultLimit * 2);
            try
            {
                foreach (var entry in EnumerateRecursiveSearchEntries(root, token))
                {
                    token.ThrowIfCancellationRequested();
                    FolderSearchMatch? match = TryCreateSearchMatch(entry, filter);
                    if (match == null)
                    {
                        continue;
                    }

                    matches.Add(match);
                    if (matches.Count >= SearchResultLimit * 4)
                    {
                        matches = matches
                            .OrderBy(item => item.Score)
                            .ThenBy(item => item.Depth)
                            .ThenBy(item => item.SortName, StringComparer.OrdinalIgnoreCase)
                            .Take(SearchResultLimit * 2)
                            .ToList();
                    }
                }
            }
            catch
            {
            }

            return FinalizeSearchMatches(matches);
        }
    }
}
