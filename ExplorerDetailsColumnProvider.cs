using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopPlus
{
    internal static class ExplorerDetailsColumnProvider
    {
        private const string MetadataPrefix = "explorer:";
        private const int MaxColumnScanCount = 512;
        private const int EmptyColumnStopThreshold = 32;
        private const int ValueCacheLimit = 4096;
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, List<ExplorerDetailsColumnInfo>> AvailableColumnsCache =
            new Dictionary<string, List<ExplorerDetailsColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, int>> FolderColumnIndexCache =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ValueCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> ValueCacheOrder = new Queue<string>();

        internal sealed class ExplorerDetailsColumnInfo
        {
            public ExplorerDetailsColumnInfo(string key, string label)
            {
                Key = key;
                Label = label;
            }

            public string Key { get; }
            public string Label { get; }
        }

        public static bool IsExplorerMetadataKey(string? key)
        {
            return !string.IsNullOrWhiteSpace(key) &&
                key.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase) &&
                key.Length > MetadataPrefix.Length;
        }

        public static string NormalizeMetadataKey(string? key)
        {
            if (!IsExplorerMetadataKey(key))
            {
                return string.Empty;
            }

            string label = GetDisplayLabel(key);
            return string.IsNullOrWhiteSpace(label)
                ? string.Empty
                : CreateMetadataKey(label);
        }

        public static string CreateMetadataKey(string? label)
        {
            string normalizedLabel = (label ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalizedLabel)
                ? string.Empty
                : MetadataPrefix + normalizedLabel;
        }

        public static string GetDisplayLabel(string? key)
        {
            if (!IsExplorerMetadataKey(key))
            {
                return string.Empty;
            }

            return key![MetadataPrefix.Length..].Trim();
        }

        public static IReadOnlyList<ExplorerDetailsColumnInfo> GetAvailableColumns(string? folderPath)
        {
            string normalizedFolderPath = NormalizePath(folderPath);
            if (string.IsNullOrWhiteSpace(normalizedFolderPath) || !Directory.Exists(normalizedFolderPath))
            {
                return Array.Empty<ExplorerDetailsColumnInfo>();
            }

            lock (CacheLock)
            {
                if (AvailableColumnsCache.TryGetValue(normalizedFolderPath, out List<ExplorerDetailsColumnInfo>? cachedColumns))
                {
                    return cachedColumns;
                }
            }

            Dictionary<string, int> columnIndexes = BuildColumnIndexMap(normalizedFolderPath);
            var columns = columnIndexes.Keys
                .Select(label => new ExplorerDetailsColumnInfo(CreateMetadataKey(label), label))
                .OrderBy(info => info.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            lock (CacheLock)
            {
                AvailableColumnsCache[normalizedFolderPath] = columns;
                if (!FolderColumnIndexCache.ContainsKey(normalizedFolderPath))
                {
                    FolderColumnIndexCache[normalizedFolderPath] = columnIndexes;
                }
            }

            return columns;
        }

        public static string GetValue(string? path, string? metadataKey)
        {
            string normalizedPath = NormalizePath(path);
            string label = GetDisplayLabel(metadataKey);
            if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(label))
            {
                return string.Empty;
            }

            string cacheKey = $"{normalizedPath}|{label}";
            lock (CacheLock)
            {
                if (ValueCache.TryGetValue(cacheKey, out string? cachedValue))
                {
                    return cachedValue;
                }
            }

            string value = ReadValueInternal(normalizedPath, label);
            lock (CacheLock)
            {
                ValueCache[cacheKey] = value;
                ValueCacheOrder.Enqueue(cacheKey);
                while (ValueCacheOrder.Count > ValueCacheLimit)
                {
                    string staleKey = ValueCacheOrder.Dequeue();
                    ValueCache.Remove(staleKey);
                }
            }

            return value;
        }

        public static void InvalidatePath(string? path)
        {
            string normalizedPath = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return;
            }

            string? parentPath = TryGetParentFolderPath(normalizedPath);

            lock (CacheLock)
            {
                foreach (string key in ValueCache.Keys
                    .Where(key => key.StartsWith(normalizedPath + "|", StringComparison.OrdinalIgnoreCase))
                    .ToList())
                {
                    ValueCache.Remove(key);
                }

                if (!string.IsNullOrWhiteSpace(parentPath))
                {
                    AvailableColumnsCache.Remove(parentPath);
                    FolderColumnIndexCache.Remove(parentPath);
                }
            }
        }

        private static string ReadValueInternal(string path, string label)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return string.Empty;
            }

            string? parentPath = TryGetParentFolderPath(path);
            string itemName = GetItemParseName(path);
            if (string.IsNullOrWhiteSpace(parentPath) ||
                string.IsNullOrWhiteSpace(itemName) ||
                !Directory.Exists(parentPath))
            {
                return string.Empty;
            }

            object? shellApp = null;
            object? folder = null;
            object? item = null;

            try
            {
                shellApp = CreateShellApplication();
                if (shellApp == null)
                {
                    return string.Empty;
                }

                folder = InvokeComMethod(shellApp, "NameSpace", parentPath);
                if (folder == null)
                {
                    return string.Empty;
                }

                int columnIndex = ResolveColumnIndex(folder, parentPath, label);
                if (columnIndex < 0)
                {
                    return string.Empty;
                }

                item = InvokeComMethod(folder, "ParseName", itemName);
                if (item == null)
                {
                    return string.Empty;
                }

                return NormalizeValue(ReadString(InvokeComMethod(folder, "GetDetailsOf", item, columnIndex)));
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                ReleaseComObject(item);
                ReleaseComObject(folder);
                ReleaseComObject(shellApp);
            }
        }

        private static int ResolveColumnIndex(object folder, string folderPath, string label)
        {
            lock (CacheLock)
            {
                if (FolderColumnIndexCache.TryGetValue(folderPath, out Dictionary<string, int>? cachedIndexes) &&
                    cachedIndexes.TryGetValue(label, out int cachedIndex))
                {
                    return cachedIndex;
                }
            }

            Dictionary<string, int> indexes = BuildColumnIndexMap(folderPath, folder);
            lock (CacheLock)
            {
                FolderColumnIndexCache[folderPath] = indexes;
            }

            return indexes.TryGetValue(label, out int index) ? index : -1;
        }

        private static Dictionary<string, int> BuildColumnIndexMap(string folderPath, object? existingFolder = null)
        {
            var indexes = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            object? shellApp = null;
            object? folder = existingFolder;

            try
            {
                if (folder == null)
                {
                    shellApp = CreateShellApplication();
                    if (shellApp == null)
                    {
                        return indexes;
                    }

                    folder = InvokeComMethod(shellApp, "NameSpace", folderPath);
                    if (folder == null)
                    {
                        return indexes;
                    }
                }

                int emptyCount = 0;
                for (int index = 0; index < MaxColumnScanCount && emptyCount < EmptyColumnStopThreshold; index++)
                {
                    string label = NormalizeValue(ReadString(InvokeComMethod(folder, "GetDetailsOf", null, index)));
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        emptyCount++;
                        continue;
                    }

                    emptyCount = 0;
                    if (!indexes.ContainsKey(label))
                    {
                        indexes[label] = index;
                    }
                }
            }
            catch
            {
                return indexes;
            }
            finally
            {
                if (existingFolder == null)
                {
                    ReleaseComObject(folder);
                }

                ReleaseComObject(shellApp);
            }

            return indexes;
        }

        private static object? CreateShellApplication()
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            return shellType == null ? null : Activator.CreateInstance(shellType);
        }

        private static object? InvokeComMethod(object target, string methodName, params object?[] args)
        {
            return target.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod,
                binder: null,
                target: target,
                args: args);
        }

        private static string ReadString(object? value)
        {
            return value?.ToString() ?? string.Empty;
        }

        private static string NormalizeValue(string value)
        {
            return (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path.Trim();
            }
        }

        private static string? TryGetParentFolderPath(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    return Directory.GetParent(path)?.FullName;
                }

                return Path.GetDirectoryName(path);
            }
            catch
            {
                return null;
            }
        }

        private static string GetItemParseName(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    return new DirectoryInfo(path).Name;
                }

                return Path.GetFileName(path);
            }
            catch
            {
                return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }

        private static void ReleaseComObject(object? value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
    }
}
