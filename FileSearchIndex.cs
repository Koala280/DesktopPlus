using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPlus
{
    public static class FileSearchIndex
    {
        private static readonly object SyncRoot = new object();
        private static List<string> _sortedPaths = new List<string>();
        private static Task? _buildTask;
        private static bool _isReady;
        private static bool _isBuilding;

        public static bool IsReady => _isReady;
        public static bool IsBuilding => _isBuilding;

        public static void EnsureStarted()
        {
            lock (SyncRoot)
            {
                if (_isReady) return;
                if (_buildTask != null && !_buildTask.IsCompleted) return;
                _buildTask = Task.Run(BuildIndex);
            }
        }

        public static Task<List<string>> SearchAsync(string rootFolderPath, string searchTerm, int limit, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(rootFolderPath) ||
                string.IsNullOrWhiteSpace(searchTerm) ||
                limit <= 0)
            {
                return Task.FromResult(new List<string>());
            }

            EnsureStarted();
            return Task.Run(() => SearchCore(rootFolderPath, searchTerm, limit, token), token);
        }

        private static void BuildIndex()
        {
            _isBuilding = true;

            try
            {
                var indexedPaths = BuildPathList(CancellationToken.None);
                indexedPaths.Sort(StringComparer.OrdinalIgnoreCase);

                lock (SyncRoot)
                {
                    _sortedPaths = indexedPaths;
                    _isReady = true;
                }
            }
            catch
            {
                lock (SyncRoot)
                {
                    _sortedPaths = new List<string>();
                    _isReady = false;
                }
            }
            finally
            {
                _isBuilding = false;
            }
        }

        private static List<string> SearchCore(string rootFolderPath, string searchTerm, int limit, CancellationToken token)
        {
            string rootPrefix;
            try
            {
                rootPrefix = NormalizeRootPrefix(rootFolderPath);
            }
            catch
            {
                return new List<string>();
            }

            string needle = searchTerm.Trim();
            if (needle.Length == 0)
            {
                return new List<string>();
            }

            List<string> snapshot;
            lock (SyncRoot)
            {
                snapshot = _sortedPaths;
            }

            if (snapshot.Count == 0)
            {
                return new List<string>();
            }

            int startIndex = LowerBound(snapshot, rootPrefix);
            var results = new List<string>(Math.Min(limit, 64));

            for (int i = startIndex; i < snapshot.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                string path = snapshot[i];
                if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                string name = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                results.Add(path);
                if (results.Count >= limit)
                {
                    break;
                }
            }

            return results;
        }

        private static int LowerBound(List<string> sortedValues, string key)
        {
            int left = 0;
            int right = sortedValues.Count;

            while (left < right)
            {
                int middle = left + ((right - left) / 2);
                if (StringComparer.OrdinalIgnoreCase.Compare(sortedValues[middle], key) < 0)
                {
                    left = middle + 1;
                }
                else
                {
                    right = middle;
                }
            }

            return left;
        }

        private static string NormalizeRootPrefix(string rootFolderPath)
        {
            string full = Path.GetFullPath(rootFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full + Path.DirectorySeparatorChar;
        }

        private static List<string> BuildPathList(CancellationToken token)
        {
            var indexedPaths = new List<string>(250000);
            foreach (var drive in DriveInfo.GetDrives())
            {
                token.ThrowIfCancellationRequested();
                if (!IsSearchableDrive(drive))
                {
                    continue;
                }

                IndexDirectoryTree(drive.RootDirectory.FullName, indexedPaths, token);
            }

            return indexedPaths;
        }

        private static bool IsSearchableDrive(DriveInfo drive)
        {
            if (!drive.IsReady) return false;

            return drive.DriveType == DriveType.Fixed ||
                   drive.DriveType == DriveType.Removable;
        }

        private static void IndexDirectoryTree(string rootDirectory, List<string> sink, CancellationToken token)
        {
            var pending = new Stack<string>();
            pending.Push(rootDirectory);

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string currentDirectory = pending.Pop();

                IEnumerable<string> subDirectories;
                try
                {
                    subDirectories = Directory.EnumerateDirectories(currentDirectory, "*", options);
                }
                catch
                {
                    continue;
                }

                foreach (var subDirectory in subDirectories)
                {
                    token.ThrowIfCancellationRequested();
                    if (ShouldSkipDirectory(subDirectory))
                    {
                        continue;
                    }

                    sink.Add(subDirectory);
                    pending.Push(subDirectory);
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(currentDirectory, "*", options);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    sink.Add(file);
                }
            }
        }

        private static bool ShouldSkipDirectory(string path)
        {
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(name, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase);
        }
    }
}
