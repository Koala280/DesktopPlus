using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopPlus.Companion
{
    internal sealed class CompanionDirectoryListing
    {
        public string Path { get; set; } = "";
        public string? Parent { get; set; }
        public string Name { get; set; } = "";
        public List<CompanionEntry> Entries { get; set; } = new();
    }

    internal sealed class CompanionEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsDir { get; set; }
        public long Size { get; set; }
        public string? Modified { get; set; }
        public string Ext { get; set; } = "";
    }

    internal sealed class CompanionDriveInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "";
        public bool Ready { get; set; }
    }

    /// <summary>
    /// Read-only filesystem access for the companion. Pure I/O (runs off the UI thread).
    /// Paths are canonicalized; per-entry failures (access denied, etc.) are skipped rather
    /// than aborting the whole listing, and nothing throws across the API boundary.
    /// </summary>
    internal static class CompanionFileService
    {
        public static CompanionDirectoryListing? ListDirectory(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return null;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            catch
            {
                return null;
            }

            DirectoryInfo directory;
            try
            {
                directory = new DirectoryInfo(fullPath);
                if (!directory.Exists)
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }

            var listing = new CompanionDirectoryListing
            {
                Path = directory.FullName,
                Parent = directory.Parent?.FullName,
                Name = string.IsNullOrEmpty(directory.Name) ? directory.FullName : directory.Name
            };

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = directory.EnumerateFileSystemInfos();
            }
            catch
            {
                // Directory exists but is not enumerable (permissions): return it empty.
                return listing;
            }

            var entries = new List<CompanionEntry>();
            foreach (var info in children)
            {
                try
                {
                    bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new CompanionEntry
                    {
                        Name = info.Name,
                        Path = info.FullName,
                        IsDir = isDir,
                        Size = isDir ? 0 : SafeLength(info),
                        Modified = SafeModified(info),
                        Ext = isDir ? "" : info.Extension.TrimStart('.').ToLowerInvariant()
                    });
                }
                catch
                {
                    // Skip entries we can't stat.
                }
            }

            listing.Entries = entries
                .OrderByDescending(e => e.IsDir)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return listing;
        }

        /// <summary>
        /// Resolves a List-panel's pinned paths into entries. Missing/unreadable items are skipped.
        /// Folders sort first, then alphabetically — matching directory listings.
        /// </summary>
        public static List<CompanionEntry> BuildEntries(IEnumerable<string>? paths)
        {
            var entries = new List<CompanionEntry>();
            if (paths == null)
            {
                return entries;
            }

            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                try
                {
                    string full = Path.GetFullPath(raw);
                    FileSystemInfo info;
                    bool isDir = Directory.Exists(full);
                    if (isDir)
                    {
                        info = new DirectoryInfo(full);
                    }
                    else if (File.Exists(full))
                    {
                        info = new FileInfo(full);
                    }
                    else
                    {
                        continue;   // pinned item no longer on disk
                    }

                    entries.Add(new CompanionEntry
                    {
                        Name = string.IsNullOrEmpty(info.Name) ? full : info.Name,
                        Path = info.FullName,
                        IsDir = isDir,
                        Size = isDir ? 0 : SafeLength(info),
                        Modified = SafeModified(info),
                        Ext = isDir ? "" : info.Extension.TrimStart('.').ToLowerInvariant()
                    });
                }
                catch
                {
                    // Skip anything we can't canonicalize or stat.
                }
            }

            return entries
                .OrderByDescending(e => e.IsDir)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<CompanionDriveInfo> GetDrives()
        {
            var result = new List<CompanionDriveInfo>();
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch
            {
                return result;
            }

            foreach (var drive in drives)
            {
                try
                {
                    bool ready = drive.IsReady;
                    result.Add(new CompanionDriveInfo
                    {
                        Name = drive.Name.TrimEnd('\\'),
                        Path = drive.RootDirectory.FullName,
                        Label = ready && !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.VolumeLabel : "",
                        Type = drive.DriveType.ToString(),
                        Ready = ready
                    });
                }
                catch
                {
                }
            }

            return result;
        }

        private static long SafeLength(FileSystemInfo info)
        {
            try
            {
                return info is FileInfo file ? file.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string? SafeModified(FileSystemInfo info)
        {
            try
            {
                return info.LastWriteTimeUtc.ToString("o");
            }
            catch
            {
                return null;
            }
        }
    }
}
