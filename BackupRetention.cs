using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DesktopPlus;

internal static class BackupRetention
{
    internal const int MaximumBackups = 5;
    internal static readonly object SyncRoot = new();

    internal static void Maintain(string directory, params string?[] protectedArchives)
    {
        lock (SyncRoot)
        {
            if (!Directory.Exists(directory)) return;

            var protectedPaths = protectedArchives.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var archives = Directory.EnumerateFiles(directory, "DesktopPlus-backup-*.zip", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase).ToList();
            int count = archives.Count;
            foreach (var archive in archives.AsEnumerable().Reverse().Where(file => !protectedPaths.Contains(file.FullName)))
            {
                if (count <= MaximumBackups) break;
                if (TryDelete(() => archive.Delete(), archive.FullName)) count--;
            }
        }
    }

    internal static void CleanAbandonedWorkDirectories(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            // Older versions copied user files here. Only remove recognizable, abandoned work directories.
            DateTime cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string path in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                bool knownPrefix = name.StartsWith("staging-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("restore-", StringComparison.OrdinalIgnoreCase);
                if (!knownPrefix || name.Length != 40 || !Guid.TryParseExact(name.Substring(8), "N", out _) ||
                    !string.Equals(Directory.GetParent(Path.GetFullPath(path))?.FullName, root, StringComparison.OrdinalIgnoreCase) ||
                    Directory.GetLastWriteTimeUtc(path) >= cutoff ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                TryDelete(() => DeleteAbandonedCopy(path, Path.GetFullPath(path)), path);
            }
            foreach (string path in Directory.EnumerateFiles(directory, ".pending-*.zip", SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) TryDelete(() => File.Delete(path), path);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Could not clean abandoned backup work directories: {ex.Message}"); }
    }

    private static void DeleteAbandonedCopy(string path, string allowedRoot)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(fullPath, allowedRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Backup cleanup path is outside the staging directory.");
        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            // Never follow a copied junction/symlink into original user data.
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(fullPath)) DeleteAbandonedCopy(child, allowedRoot);
                File.SetAttributes(fullPath, attributes & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(fullPath);
        }
        else
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0) File.SetAttributes(fullPath, attributes & ~FileAttributes.ReadOnly);
            File.Delete(fullPath);
        }
    }

    private static bool TryDelete(Action delete, string path)
    {
        try { delete(); return true; }
        catch (Exception ex) { Debug.WriteLine($"Could not remove backup artifact '{path}': {ex.Message}"); return false; }
    }
}
