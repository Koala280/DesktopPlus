using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopPlus;

// Compare launch behavior, not .lnk bytes, which also contain timestamps and other metadata.
internal sealed record ShortcutIdentity(
    string Kind, string Target, string Arguments, string WorkingDirectory, int WindowStyle, string Hotkey)
{
    public static ShortcutIdentity? TryRead(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadInternetShortcut(path);
        }
        if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null || !File.Exists(path)) return null;
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            if (shortcut == null) return null;

            object? Read(string name) => shortcut.GetType().InvokeMember(name, BindingFlags.GetProperty, null, shortcut, null);
            string target = Read("TargetPath") as string ?? "";
            if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return null;
            string workingDirectory = Read("WorkingDirectory") as string ?? "";
            if (!string.IsNullOrWhiteSpace(workingDirectory) && !Path.IsPathFullyQualified(workingDirectory)) return null;
            return new ShortcutIdentity("lnk", NormalizeWindowsPath(target),
                Read("Arguments") as string ?? "", NormalizeWindowsPath(workingDirectory),
                Convert.ToInt32(Read("WindowStyle")), (Read("Hotkey") as string ?? "").ToUpperInvariant());
        }
        catch
        {
            // Unknown/broken links must remain separate rather than risk hiding a different launch command.
            return null;
        }
        finally
        {
            Release(shortcut);
            Release(shell);
        }
    }

    private static string NormalizeWindowsPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (Path.IsPathFullyQualified(value)) value = Path.GetFullPath(value);
        return value.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    }

    private static ShortcutIdentity? TryReadInternetShortcut(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return null;
            bool inShortcutSection = false;
            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inShortcutSection = string.Equals(line, "[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inShortcutSection) continue;
                int separator = line.IndexOf('=');
                if (separator < 0 || !string.Equals(line[..separator].Trim(), "URL", StringComparison.OrdinalIgnoreCase)) continue;
                string target = line[(separator + 1)..].Trim();
                return Uri.TryCreate(target, UriKind.Absolute, out _)
                    ? new ShortcutIdentity("url", target, "", "", 0, "")
                    : null;
            }
        }
        catch
        {
        }
        return null;
    }

    private static void Release(object? value)
    {
        if (value == null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }
}
