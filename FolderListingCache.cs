using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPlus
{
    /// <summary>
    /// Small, bounded cache for the direct children of panel folders.
    /// This is intentionally separate from the recursive search index: opening a
    /// panel only needs one directory level and should not wait for a whole tree.
    /// </summary>
    internal static class FolderListingCache
    {
        private const int MaxCachedRoots = 64;
        private const int MaxEntriesPerRoot = 20_000;
        private const long MaxBytesPerRoot = 8L * 1024L * 1024L;
        private const long CacheBudgetBytes = 24L * 1024L * 1024L;
        private const int MaxConcurrentWarmups = 2;

        private sealed class CacheEntry
        {
            public string[] Paths { get; set; } = Array.Empty<string>();
            public DateTime RootLastWriteUtc { get; set; }
            public long EstimatedBytes { get; set; }
            public long LastAccessSequence { get; set; }
        }

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CacheEntry> Entries =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Task> PendingWarmups =
            new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim WarmupSemaphore =
            new SemaphoreSlim(MaxConcurrentWarmups, MaxConcurrentWarmups);
        private static long _cacheBytes;
        private static long _accessSequence;

        internal static bool TryGet(string folderPath, out IReadOnlyList<string> paths)
        {
            paths = Array.Empty<string>();
            string normalizedPath = Normalize(folderPath);
            CacheEntry? entry;

            lock (CacheLock)
            {
                if (!Entries.TryGetValue(normalizedPath, out entry))
                {
                    return false;
                }
            }

            DateTime currentLastWriteUtc = GetLastWriteTimeUtc(normalizedPath);
            if (currentLastWriteUtc == DateTime.MinValue ||
                currentLastWriteUtc != entry.RootLastWriteUtc)
            {
                Invalidate(normalizedPath);
                return false;
            }

            lock (CacheLock)
            {
                if (!Entries.TryGetValue(normalizedPath, out entry))
                {
                    return false;
                }

                entry.LastAccessSequence = ++_accessSequence;
                paths = entry.Paths;
                return true;
            }
        }

        internal static void Store(string folderPath, IReadOnlyCollection<string> paths)
        {
            if (paths == null ||
                paths.Count > MaxEntriesPerRoot ||
                string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            string normalizedPath = Normalize(folderPath);
            DateTime rootLastWriteUtc = GetLastWriteTimeUtc(normalizedPath);
            if (rootLastWriteUtc == DateTime.MinValue)
            {
                return;
            }

            string[] snapshot = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            long estimatedBytes = EstimateBytes(snapshot);
            if (estimatedBytes > MaxBytesPerRoot)
            {
                return;
            }

            lock (CacheLock)
            {
                RemoveCore(normalizedPath);
                Entries[normalizedPath] = new CacheEntry
                {
                    Paths = snapshot,
                    RootLastWriteUtc = rootLastWriteUtc,
                    EstimatedBytes = estimatedBytes,
                    LastAccessSequence = ++_accessSequence
                };
                _cacheBytes += estimatedBytes;
                TrimCore(normalizedPath);
            }
        }

        internal static void Invalidate(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            lock (CacheLock)
            {
                RemoveCore(Normalize(folderPath));
            }
        }

        internal static bool ApplyFileSystemChange(
            string folderPath,
            string? oldPath,
            string? newPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            string normalizedRoot = Normalize(folderPath);
            lock (CacheLock)
            {
                if (!Entries.TryGetValue(normalizedRoot, out CacheEntry? entry))
                {
                    return false;
                }

                bool removesDirectChild = IsDirectChild(normalizedRoot, oldPath);
                bool touchesDirectChild = IsDirectChild(normalizedRoot, newPath);
                bool alreadyContainsNewPath = touchesDirectChild &&
                    entry.Paths.Any(path =>
                        string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase));
                if ((!removesDirectChild && !touchesDirectChild) ||
                    (!removesDirectChild &&
                     alreadyContainsNewPath &&
                     PathExistsAndCanBeCached(newPath!)))
                {
                    DateTime currentLastWriteUtc = GetLastWriteTimeUtc(normalizedRoot);
                    if (currentLastWriteUtc == DateTime.MinValue)
                    {
                        RemoveCore(normalizedRoot);
                        return false;
                    }

                    entry.RootLastWriteUtc = currentLastWriteUtc;
                    entry.LastAccessSequence = ++_accessSequence;
                    return true;
                }

                var updatedPaths = entry.Paths.ToList();
                if (removesDirectChild)
                {
                    updatedPaths.RemoveAll(path =>
                        string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase));
                }

                if (touchesDirectChild)
                {
                    if (PathExistsAndCanBeCached(newPath!))
                    {
                        bool alreadyPresent = updatedPaths.Any(path =>
                            string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyPresent)
                        {
                            if (Directory.Exists(newPath))
                            {
                                int firstFileIndex = updatedPaths.FindIndex(path => !Directory.Exists(path));
                                if (firstFileIndex >= 0)
                                {
                                    updatedPaths.Insert(firstFileIndex, newPath!);
                                }
                                else
                                {
                                    updatedPaths.Add(newPath!);
                                }
                            }
                            else
                            {
                                updatedPaths.Add(newPath!);
                            }
                        }
                    }
                    else
                    {
                        updatedPaths.RemoveAll(path =>
                            string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase));
                    }
                }

                string[] snapshot = updatedPaths.ToArray();
                long estimatedBytes = EstimateBytes(snapshot);
                DateTime rootLastWriteUtc = GetLastWriteTimeUtc(normalizedRoot);
                if (snapshot.Length > MaxEntriesPerRoot ||
                    estimatedBytes > MaxBytesPerRoot ||
                    rootLastWriteUtc == DateTime.MinValue)
                {
                    RemoveCore(normalizedRoot);
                    return false;
                }

                _cacheBytes = Math.Max(0, _cacheBytes - entry.EstimatedBytes);
                entry.Paths = snapshot;
                entry.RootLastWriteUtc = rootLastWriteUtc;
                entry.EstimatedBytes = estimatedBytes;
                entry.LastAccessSequence = ++_accessSequence;
                _cacheBytes += estimatedBytes;
                TrimCore(normalizedRoot);
                return true;
            }
        }

        internal static Task WarmAsync(IEnumerable<string> folderPaths, CancellationToken token = default)
        {
            string[] roots = folderPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.WhenAll(roots.Select(root => GetOrCreateWarmupTask(root, token)));
        }

        private static Task GetOrCreateWarmupTask(string folderPath, CancellationToken token)
        {
            if (TryGet(folderPath, out _))
            {
                return Task.CompletedTask;
            }

            lock (CacheLock)
            {
                if (PendingWarmups.TryGetValue(folderPath, out Task? pending))
                {
                    return pending.WaitAsync(token);
                }

                Task warmup = Task.Run(() => WarmOneAsync(folderPath));
                PendingWarmups[folderPath] = warmup;
                _ = warmup.ContinueWith(
                    _ =>
                    {
                        lock (CacheLock)
                        {
                            PendingWarmups.Remove(folderPath);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return warmup.WaitAsync(token);
            }
        }

        private static async Task WarmOneAsync(string folderPath)
        {
            await WarmupSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (TryGet(folderPath, out _) || !Directory.Exists(folderPath))
                {
                    return;
                }

                var paths = new List<string>(Math.Min(512, MaxEntriesPerRoot));
                if (!TryEnumerateChildren(folderPath, paths))
                {
                    return;
                }

                Store(folderPath, paths);
            }
            finally
            {
                WarmupSemaphore.Release();
            }
        }

        private static bool TryEnumerateChildren(string folderPath, List<string> paths)
        {
            try
            {
                long estimatedBytes = 64;
                foreach (string directoryPath in Directory.EnumerateDirectories(folderPath))
                {
                    long pathBytes = 32L + directoryPath.Length * sizeof(char);
                    if (paths.Count >= MaxEntriesPerRoot || estimatedBytes + pathBytes > MaxBytesPerRoot)
                    {
                        return false;
                    }

                    if (ShouldCachePath(directoryPath))
                    {
                        paths.Add(directoryPath);
                        estimatedBytes += pathBytes;
                    }
                }

                foreach (string filePath in Directory.EnumerateFiles(folderPath))
                {
                    long pathBytes = 32L + filePath.Length * sizeof(char);
                    if (paths.Count >= MaxEntriesPerRoot || estimatedBytes + pathBytes > MaxBytesPerRoot)
                    {
                        return false;
                    }

                    if (ShouldCachePath(filePath))
                    {
                        paths.Add(filePath);
                        estimatedBytes += pathBytes;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldCachePath(string path)
        {
            try
            {
                return !File.GetAttributes(path).HasFlag(FileAttributes.System);
            }
            catch
            {
                return true;
            }
        }

        private static bool PathExistsAndCanBeCached(string path)
        {
            try
            {
                return (Directory.Exists(path) || File.Exists(path)) && ShouldCachePath(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDirectChild(string rootPath, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                string normalizedParent = Path.TrimEndingDirectorySeparator(
                    Path.GetDirectoryName(Normalize(path)) ?? string.Empty);
                return string.Equals(normalizedParent, rootPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        private static long EstimateBytes(IEnumerable<string> paths)
        {
            long total = 64;
            foreach (string path in paths)
            {
                total += 32L + (path?.Length ?? 0) * sizeof(char);
            }

            return total;
        }

        private static void TrimCore(string preferredRoot)
        {
            while (Entries.Count > MaxCachedRoots || _cacheBytes > CacheBudgetBytes)
            {
                var candidate = Entries
                    .Where(pair => !string.Equals(pair.Key, preferredRoot, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(pair => pair.Value.LastAccessSequence)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(candidate.Key))
                {
                    break;
                }

                RemoveCore(candidate.Key);
            }
        }

        private static void RemoveCore(string folderPath)
        {
            if (!Entries.Remove(folderPath, out CacheEntry? removed))
            {
                return;
            }

            _cacheBytes = Math.Max(0, _cacheBytes - removed.EstimatedBytes);
        }

        private static string Normalize(string folderPath)
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            }
            catch
            {
                return Path.TrimEndingDirectorySeparator(folderPath);
            }
        }

        private static DateTime GetLastWriteTimeUtc(string folderPath)
        {
            try
            {
                return Directory.Exists(folderPath)
                    ? Directory.GetLastWriteTimeUtc(folderPath)
                    : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
