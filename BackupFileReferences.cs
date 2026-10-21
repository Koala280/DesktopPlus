using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopPlus;

internal sealed class BackupFileReference
{
    public string Path { get; set; } = "";
    public string FileId { get; set; } = "";
    public long CreationTimeUtcTicks { get; set; }
    public bool IsDirectory { get; set; }
}

internal static class BackupFileReferences
{
    // A volume and file ID survive renames/moves. Creation time also guards against reused IDs after deletion.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
        IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    internal static string GetFileId(string path)
    {
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
        return !handle.IsInvalid && GetFileInformationByHandle(handle, out var info)
            ? $"{info.VolumeSerial:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}" : "";
    }

    internal static List<BackupFileReference> Capture(IReadOnlyList<string> desktopRoots, string storageRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] Entries(string directory)
        {
            try { return Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (IOException) { return Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
        void AddItems(string directory)
        {
            // Never descend into user folders or read file contents.
            foreach (string path in Entries(directory)) paths.Add(path);
        }
        foreach (string root in desktopRoots) AddItems(root);
        if (Directory.Exists(storageRoot))
        {
            foreach (string category in Entries(storageRoot))
            {
                try
                {
                    if (!Directory.Exists(category)) { paths.Add(category); continue; }
                    if ((File.GetAttributes(category) & FileAttributes.ReparsePoint) != 0) continue;
                    if (string.Equals(System.IO.Path.GetFileName(category), DesktopAutoSorter.DuplicateShortcutsFolderName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string duplicateCategory in Entries(category).Where(Directory.Exists))
                        {
                            if ((File.GetAttributes(duplicateCategory) & FileAttributes.ReparsePoint) == 0) AddItems(duplicateCategory);
                        }
                    }
                    else AddItems(category);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        var references = new List<BackupFileReference>();
        foreach (string path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                if (!info.Exists) continue;
                references.Add(new BackupFileReference
                {
                    Path = info.FullName,
                    FileId = GetFileId(path),
                    CreationTimeUtcTicks = info.CreationTimeUtc.Ticks,
                    IsDirectory = info is DirectoryInfo
                });
            }
            catch (IOException) { } // An item can disappear while taking the snapshot.
            catch (UnauthorizedAccessException) { }
        }
        return references;
    }

    // The restore helper runs after DesktopPlus exits, under Windows PowerShell/.NET Framework.
    internal const string RestoreIdentityType = """
        using System;
        using System.IO;
        using System.Runtime.InteropServices;
        using Microsoft.Win32.SafeHandles;
        public static class DesktopPlusBackupIdentity {
          [StructLayout(LayoutKind.Sequential)] private struct Info {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
          }
          [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
          private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
          [DllImport("kernel32.dll", SetLastError=true)]
          private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
          public static string GetId(string path) {
            using (var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero)) {
              Info info;
              return !handle.IsInvalid && GetFileInformationByHandle(handle, out info)
                ? string.Format("{0:X8}:{1:X8}{2:X8}", info.Volume, info.IndexHigh, info.IndexLow) : "";
            }
          }
        }
        """;
}
