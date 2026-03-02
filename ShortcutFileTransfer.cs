using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopPlus
{
    internal static class ShortcutFileTransfer
    {
        private const string LnkExtension = ".lnk";

        public static void MoveFile(string sourcePath, string targetPath)
        {
            if (!IsShortcutPath(sourcePath))
            {
                File.Move(sourcePath, targetPath);
                return;
            }

            string? resolvedTargetPath = TryReadShortcutTargetPath(sourcePath);
            File.Move(sourcePath, targetPath);
            TryWriteShortcutTargetPath(targetPath, resolvedTargetPath);
        }

        public static void CopyFile(string sourcePath, string targetPath, bool overwrite)
        {
            if (!IsShortcutPath(sourcePath))
            {
                File.Copy(sourcePath, targetPath, overwrite);
                return;
            }

            string? resolvedTargetPath = TryReadShortcutTargetPath(sourcePath);
            File.Copy(sourcePath, targetPath, overwrite);
            TryWriteShortcutTargetPath(targetPath, resolvedTargetPath);
        }

        private static bool IsShortcutPath(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, LnkExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static string? TryReadShortcutTargetPath(string shortcutPath)
        {
            if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
            {
                return null;
            }

            object? shell = null;
            object? shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return null;
                }

                shell = Activator.CreateInstance(shellType);
                if (shell == null)
                {
                    return null;
                }

                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath });
                if (shortcut == null)
                {
                    return null;
                }

                object? rawTargetPath = shortcut.GetType().InvokeMember(
                    "TargetPath",
                    BindingFlags.GetProperty,
                    null,
                    shortcut,
                    null);

                string targetPath = rawTargetPath as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    return null;
                }

                return Path.GetFullPath(targetPath);
            }
            catch
            {
                return null;
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static void TryWriteShortcutTargetPath(string shortcutPath, string? targetPath)
        {
            if (string.IsNullOrWhiteSpace(shortcutPath) ||
                string.IsNullOrWhiteSpace(targetPath) ||
                !Path.IsPathFullyQualified(targetPath) ||
                !File.Exists(shortcutPath))
            {
                return;
            }

            object? shell = null;
            object? shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return;
                }

                shell = Activator.CreateInstance(shellType);
                if (shell == null)
                {
                    return;
                }

                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath });
                if (shortcut == null)
                {
                    return;
                }

                shortcut.GetType().InvokeMember(
                    "TargetPath",
                    BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { targetPath });

                string? workingDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                {
                    shortcut.GetType().InvokeMember(
                        "WorkingDirectory",
                        BindingFlags.SetProperty,
                        null,
                        shortcut,
                        new object[] { workingDirectory });
                }

                shortcut.GetType().InvokeMember(
                    "Save",
                    BindingFlags.InvokeMethod,
                    null,
                    shortcut,
                    null);
            }
            catch
            {
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static void ReleaseComObject(object? value)
        {
            if (value == null)
            {
                return;
            }

            try
            {
                if (Marshal.IsComObject(value))
                {
                    Marshal.FinalReleaseComObject(value);
                }
            }
            catch
            {
            }
        }
    }
}
