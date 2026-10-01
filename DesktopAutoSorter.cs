using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DesktopPlus;

internal enum DesktopSortPhase
{
    CreatingBackup,
    MovingItems
}

internal sealed class DesktopSortMovedItem
{
    public string SourcePath { get; init; } = "";
    public string TargetPath { get; init; } = "";
}

internal sealed class DesktopSortResult
{
    public int MovedCount { get; set; }
    public int DuplicateShortcutCount { get; set; }
    public int ErrorCount { get; set; }
    public int SkippedCount { get; set; }
    public Dictionary<string, List<DesktopSortMovedItem>> TargetPanels { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class DesktopAutoSorter
{
    internal const string DuplicateShortcutsFolderName = ".DuplicateShortcuts";
    private sealed record PlannedItem(string PanelName, bool IsDirectory, DesktopSortMovedItem Move, string? CanonicalPath = null);

    public static Task<DesktopSortResult> RunAsync(
        IReadOnlyList<string> desktopPaths,
        IEnumerable<DesktopSortRuleState> rules,
        string storageRootPath,
        Action<IReadOnlyCollection<DesktopSortMovedItem>> createBackup,
        IProgress<DesktopSortPhase>? progress = null)
    {
        // The UI can change rules while the worker is running. Use an independent snapshot.
        var activeRules = rules.Where(rule => rule.Enabled).Select(rule => new DesktopSortRuleState
        {
            IsBuiltIn = rule.IsBuiltIn,
            MatchFolders = rule.MatchFolders,
            CatchAll = rule.CatchAll,
            TargetPanelName = rule.TargetPanelName,
            Extensions = rule.Extensions.ToList()
        }).ToArray();
        var roots = desktopPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return Task.Run(() =>
        {
            var result = new DesktopSortResult();
            if (activeRules.Length == 0)
            {
                return result;
            }

            var plan = new List<PlannedItem>();
            var reservedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var shortcutIndexes = new Dictionary<string, Dictionary<ShortcutIdentity, string>>(StringComparer.OrdinalIgnoreCase);

            void PlanDuplicate(string sourcePath, string panelName, string canonicalPath)
            {
                string duplicateFolder = Path.Combine(storageRootPath, DuplicateShortcutsFolderName, SanitizeFolderName(panelName));
                string duplicatePath = GetUniqueDestinationPath(duplicateFolder, Path.GetFileName(sourcePath), reservedTargets);
                reservedTargets.Add(duplicatePath);
                plan.Add(new PlannedItem(panelName, false, new DesktopSortMovedItem
                {
                    SourcePath = sourcePath,
                    TargetPath = duplicatePath
                }, canonicalPath));
            }

            Dictionary<ShortcutIdentity, string> GetShortcutIndex(string panelName, string targetFolder)
            {
                if (shortcutIndexes.TryGetValue(targetFolder, out var index)) return index;
                index = new Dictionary<ShortcutIdentity, string>();
                shortcutIndexes[targetFolder] = index;
                if (!Directory.Exists(targetFolder)) return index;
                try
                {
                    // Prefer the unsuffixed name when cleaning duplicates left by earlier sorts.
                    foreach (string existingPath in Directory.EnumerateFiles(targetFolder)
                        .Where(IsShortcutPath)
                        .OrderBy(path => Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"_\d+$"))
                        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        var identity = ShortcutIdentity.TryRead(existingPath);
                        if (identity == null) continue;
                        if (index.TryGetValue(identity, out string? canonicalPath))
                        {
                            PlanDuplicate(existingPath, panelName, canonicalPath);
                        }
                        else
                        {
                            index[identity] = existingPath;
                        }
                    }
                }
                catch
                {
                    result.ErrorCount++;
                }
                return index;
            }

            // A second Sort now also repairs existing duplicates, even with an empty desktop.
            foreach (var rule in activeRules)
            {
                string panelName = rule.TargetPanelName.Trim();
                GetShortcutIndex(panelName, Path.Combine(storageRootPath, SanitizeFolderName(panelName)));
            }

            foreach (string desktopPath in roots)
            {
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(desktopPath))
                    {
                        if (IsIgnoredEntry(entry))
                        {
                            result.SkippedCount++;
                            continue;
                        }

                        bool isDirectory = Directory.Exists(entry);
                        var rule = ResolveRule(activeRules, entry, isDirectory);
                        if (rule == null)
                        {
                            result.SkippedCount++;
                            continue;
                        }

                        string panelName = rule.TargetPanelName.Trim();
                        string targetFolder = Path.Combine(storageRootPath, SanitizeFolderName(panelName));
                        var shortcutIndex = GetShortcutIndex(panelName, targetFolder);
                        var shortcutIdentity = isDirectory ? null : ShortcutIdentity.TryRead(entry);
                        if (shortcutIdentity != null && shortcutIndex.TryGetValue(shortcutIdentity, out string? canonicalPath))
                        {
                            PlanDuplicate(entry, panelName, canonicalPath);
                            continue;
                        }
                        string targetPath = GetUniqueDestinationPath(targetFolder, Path.GetFileName(entry), reservedTargets);
                        reservedTargets.Add(targetPath);
                        plan.Add(new PlannedItem(panelName, isDirectory, new DesktopSortMovedItem
                        {
                            SourcePath = entry,
                            TargetPath = targetPath
                        }));
                        if (shortcutIdentity != null) shortcutIndex[shortcutIdentity] = targetPath;
                    }
                }
                catch
                {
                    result.ErrorCount++;
                }
            }

            if (plan.Count == 0)
            {
                return result;
            }

            // Persist the exact move plan before touching desktop items. Never copy their contents.
            progress?.Report(DesktopSortPhase.CreatingBackup);
            createBackup(plan.Select(item => item.Move).ToArray());
            progress?.Report(DesktopSortPhase.MovingItems);

            var preparedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan)
            {
                try
                {
                    if (item.CanonicalPath != null)
                    {
                        var canonicalIdentity = ShortcutIdentity.TryRead(item.CanonicalPath);
                        if (canonicalIdentity == null || canonicalIdentity != ShortcutIdentity.TryRead(item.Move.SourcePath))
                        {
                            // Keep the extra link available if the original move failed or its launch settings changed.
                            result.ErrorCount++;
                            continue;
                        }
                    }
                    string targetFolder = Path.GetDirectoryName(item.Move.TargetPath)!;
                    if (!preparedFolders.Contains(targetFolder))
                    {
                        Directory.CreateDirectory(targetFolder);
                        preparedFolders.Add(targetFolder);
                    }
                    if (item.IsDirectory)
                    {
                        Directory.Move(item.Move.SourcePath, item.Move.TargetPath);
                    }
                    else
                    {
                        ShortcutFileTransfer.MoveFile(item.Move.SourcePath, item.Move.TargetPath);
                    }

                    if (item.CanonicalPath == null) result.MovedCount++;
                    else result.DuplicateShortcutCount++;
                    if (!result.TargetPanels.TryGetValue(item.PanelName, out var movedItems))
                    {
                        movedItems = new List<DesktopSortMovedItem>();
                        result.TargetPanels[item.PanelName] = movedItems;
                    }
                    // Remap pinned references to the visible canonical link, not the saved duplicate.
                    movedItems.Add(item.CanonicalPath == null ? item.Move : new DesktopSortMovedItem
                    {
                        SourcePath = item.Move.SourcePath,
                        TargetPath = item.CanonicalPath
                    });
                }
                catch
                {
                    result.ErrorCount++;
                }
            }

            return result;
        });
    }

    private static DesktopSortRuleState? ResolveRule(IEnumerable<DesktopSortRuleState> rules, string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return rules.FirstOrDefault(rule => rule.MatchFolders);
        }

        string extension = Path.GetExtension(path);
        bool Matches(DesktopSortRuleState rule) => !rule.MatchFolders && !rule.CatchAll &&
            rule.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        return rules.FirstOrDefault(rule => !rule.IsBuiltIn && Matches(rule)) ??
            rules.FirstOrDefault(rule => rule.IsBuiltIn && Matches(rule)) ??
            rules.FirstOrDefault(rule => rule.CatchAll);
    }

    private static bool IsIgnoredEntry(string path)
    {
        string name = Path.GetFileName(path);
        if (string.Equals(name, "DesktopPlus Organized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "thumbs.db", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 &&
                (attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsShortcutPath(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFolderName(string input)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new string(input.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray())
            .Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Sorted" : sanitized;
    }

    private static string GetUniqueDestinationPath(string folder, string name, ISet<string> reservedTargets)
    {
        string baseName = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        string candidate = Path.Combine(folder, name);
        int counter = 1;
        while (reservedTargets.Contains(candidate) || File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{baseName}_{counter++}{extension}");
        }
        return candidate;
    }
}
