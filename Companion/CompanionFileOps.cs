using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DesktopPlus.Companion
{
    internal sealed class CompanionOpResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Path { get; set; }

        public static CompanionOpResult Fail(string error) => new() { Ok = false, Error = error };
        public static CompanionOpResult Success(string? path = null) => new() { Ok = true, Path = path };
    }

    /// <summary>
    /// Mutating filesystem operations for the companion (create/rename/move/copy/delete/open).
    /// Pure I/O, runs off the UI thread. Every path is required to be fully-qualified and is
    /// canonicalized before use; names are validated against separators and invalid characters.
    /// Deletes go to the recycle bin unless <c>permanent</c> is set. Nothing throws across the
    /// API boundary — failures come back as <see cref="CompanionOpResult"/> with a message.
    /// </summary>
    internal static class CompanionFileOps
    {
        public static CompanionOpResult CreateFolder(string? dir, string? name)
        {
            if (!TryCanonicalizeDirectory(dir, out string fullDir, out var dirErr))
            {
                return dirErr!;
            }
            if (!IsValidName(name))
            {
                return CompanionOpResult.Fail("Invalid folder name.");
            }

            string target = Path.Combine(fullDir, name!);
            try
            {
                if (Directory.Exists(target) || File.Exists(target))
                {
                    return CompanionOpResult.Fail("An item with that name already exists.");
                }
                Directory.CreateDirectory(target);
                return CompanionOpResult.Success(target);
            }
            catch (Exception ex)
            {
                return CompanionOpResult.Fail(Describe(ex));
            }
        }

        public static CompanionOpResult Rename(string? path, string? newName)
        {
            if (!TryCanonicalizeExisting(path, out string full, out bool isDir, out var err))
            {
                return err!;
            }
            if (!IsValidName(newName))
            {
                return CompanionOpResult.Fail("Invalid name.");
            }

            string? parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent))
            {
                return CompanionOpResult.Fail("This item can't be renamed.");
            }

            string target = Path.Combine(parent, newName!);
            if (string.Equals(target, full, StringComparison.OrdinalIgnoreCase))
            {
                return CompanionOpResult.Success(full);
            }

            try
            {
                if (Directory.Exists(target) || File.Exists(target))
                {
                    return CompanionOpResult.Fail("An item with that name already exists.");
                }
                if (isDir)
                {
                    Directory.Move(full, target);
                }
                else
                {
                    ShortcutFileTransfer.MoveFile(full, target);
                }
                return CompanionOpResult.Success(target);
            }
            catch (Exception ex)
            {
                return CompanionOpResult.Fail(Describe(ex));
            }
        }

        public static CompanionOpResult Delete(IReadOnlyList<string>? paths, bool permanent)
        {
            if (paths == null || paths.Count == 0)
            {
                return CompanionOpResult.Fail("Nothing to delete.");
            }

            var resolved = new List<(string full, bool isDir)>();
            foreach (var p in paths)
            {
                if (!TryCanonicalizeExisting(p, out string full, out bool isDir, out var err))
                {
                    return err!;
                }
                resolved.Add((full, isDir));
            }

            try
            {
                if (permanent)
                {
                    foreach (var (full, isDir) in resolved)
                    {
                        if (isDir)
                        {
                            Directory.Delete(full, recursive: true);
                        }
                        else
                        {
                            File.Delete(full);
                        }
                    }
                }
                else
                {
                    int rc = RecycleToBin(resolved.Select(r => r.full));
                    if (rc != 0)
                    {
                        return CompanionOpResult.Fail("Couldn't move to the recycle bin.");
                    }
                }
                return CompanionOpResult.Success();
            }
            catch (Exception ex)
            {
                return CompanionOpResult.Fail(Describe(ex));
            }
        }

        public static CompanionOpResult Transfer(IReadOnlyList<string>? paths, string? destDir, bool move)
        {
            if (paths == null || paths.Count == 0)
            {
                return CompanionOpResult.Fail("Nothing to " + (move ? "move." : "copy."));
            }
            if (!TryCanonicalizeDirectory(destDir, out string fullDest, out var destErr))
            {
                return destErr!;
            }

            foreach (var p in paths)
            {
                if (!TryCanonicalizeExisting(p, out string full, out bool isDir, out var err))
                {
                    return err!;
                }

                string? sourceParent = Path.GetDirectoryName(full);
                if (isDir && IsSameOrInside(fullDest, full))
                {
                    return CompanionOpResult.Fail("Can't " + (move ? "move" : "copy") + " a folder into itself.");
                }
                if (move && string.Equals(sourceParent, fullDest, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // already there
                }

                string target = UniqueTarget(fullDest, Path.GetFileName(full), isDir);
                try
                {
                    if (isDir)
                    {
                        if (move)
                        {
                            MoveDirectory(full, target);
                        }
                        else
                        {
                            CopyDirectory(full, target);
                        }
                    }
                    else if (move)
                    {
                        ShortcutFileTransfer.MoveFile(full, target);
                    }
                    else
                    {
                        ShortcutFileTransfer.CopyFile(full, target, overwrite: false);
                    }
                }
                catch (Exception ex)
                {
                    return CompanionOpResult.Fail(Describe(ex));
                }
            }

            return CompanionOpResult.Success(fullDest);
        }

        public static CompanionOpResult OpenOnPc(string? path)
        {
            if (!TryCanonicalizeExisting(path, out string full, out _, out var err))
            {
                return err!;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true });
                return CompanionOpResult.Success(full);
            }
            catch (Exception ex)
            {
                return CompanionOpResult.Fail(Describe(ex));
            }
        }

        // ---- helpers -------------------------------------------------------

        private static bool IsValidName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
            {
                return false;
            }
            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0)
            {
                return false;
            }
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static bool TryCanonicalizeDirectory(string? raw, out string fullDir, out CompanionOpResult? error)
        {
            fullDir = "";
            error = null;
            if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
            {
                error = CompanionOpResult.Fail("Invalid folder path.");
                return false;
            }
            try
            {
                fullDir = Path.GetFullPath(raw);
            }
            catch
            {
                error = CompanionOpResult.Fail("Invalid folder path.");
                return false;
            }
            if (!Directory.Exists(fullDir))
            {
                error = CompanionOpResult.Fail("That folder no longer exists.");
                return false;
            }
            return true;
        }

        private static bool TryCanonicalizeExisting(string? raw, out string full, out bool isDir, out CompanionOpResult? error)
        {
            full = "";
            isDir = false;
            error = null;
            if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
            {
                error = CompanionOpResult.Fail("Invalid path.");
                return false;
            }
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch
            {
                error = CompanionOpResult.Fail("Invalid path.");
                return false;
            }

            if (Directory.Exists(full))
            {
                isDir = true;
                return true;
            }
            if (File.Exists(full))
            {
                isDir = false;
                return true;
            }
            error = CompanionOpResult.Fail("That item no longer exists.");
            return false;
        }

        private static bool IsSameOrInside(string candidate, string root)
        {
            string a = candidate.TrimEnd('\\', '/');
            string b = root.TrimEnd('\\', '/');
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string UniqueTarget(string destDir, string name, bool isDir)
        {
            string candidate = Path.Combine(destDir, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }

            string baseName = isDir ? name : Path.GetFileNameWithoutExtension(name);
            string ext = isDir ? "" : Path.GetExtension(name);
            for (int i = 2; i < 10000; i++)
            {
                candidate = Path.Combine(destDir, $"{baseName} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            return Path.Combine(destDir, $"{baseName} ({Guid.NewGuid():N}){ext}");
        }

        private static void MoveDirectory(string source, string target)
        {
            try
            {
                Directory.Move(source, target);
            }
            catch (IOException)
            {
                // Cross-volume move: Directory.Move can't span volumes — copy then remove.
                CopyDirectory(source, target);
                Directory.Delete(source, recursive: true);
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
            foreach (string file in Directory.GetFiles(source))
            {
                ShortcutFileTransfer.CopyFile(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
            }
        }

        private static string Describe(Exception ex)
        {
            return ex switch
            {
                UnauthorizedAccessException => "Access denied.",
                DirectoryNotFoundException => "The folder no longer exists.",
                FileNotFoundException => "The file no longer exists.",
                PathTooLongException => "The path is too long.",
                IOException io => string.IsNullOrWhiteSpace(io.Message) ? "The item is in use or unavailable." : io.Message,
                _ => "The operation failed."
            };
        }

        // ---- recycle bin (silent, with undo) -------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;

        private static int RecycleToBin(IEnumerable<string> paths)
        {
            // pFrom is a double-null-terminated list of fully-qualified paths.
            string from = string.Join('\0', paths) + "\0\0";
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = unchecked((ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI))
            };
            return SHFileOperation(ref op);
        }
    }
}
