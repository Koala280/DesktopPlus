using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace DesktopPlus
{
    public partial class MainWindow : Window
    {
        private const string DesktopAutoSortRootFolderName = "DesktopPlus Organized";
        private const string AutoSortStorageFolderName = "AutoSortStorage";
        private const string RuleFolders = "builtin:folders";
        private const string RuleImages = "builtin:images";
        private const string RuleVideos = "builtin:videos";
        private const string RuleAudio = "builtin:audio";
        private const string RuleDocuments = "builtin:documents";
        private const string RuleArchives = "builtin:archives";
        private const string RuleInstallers = "builtin:installers";
        private const string RuleShortcuts = "builtin:shortcuts";
        private const string RuleCode = "builtin:code";
        private const string RuleOthers = "builtin:others";

        private DesktopAutoSortSettings _desktopAutoSort = new DesktopAutoSortSettings();
        private readonly List<FileSystemWatcher> _desktopAutoSortWatchers = new List<FileSystemWatcher>();
        private DispatcherTimer? _desktopAutoSortDebounceTimer;
        private bool _desktopAutoSortInProgress;
        private bool _suspendDesktopAutoSortHandlers;
        private string _desktopAutoSortStatusMessage = "";
        private int _newAutoSortPanelIndex;
        private readonly List<Rect> _autoSortOccupiedRects = new List<Rect>();
        private readonly Dictionary<System.Windows.Controls.TextBox, AutoSortSuggestionPopupState> _autoSortTargetSuggestionPopups = new Dictionary<System.Windows.Controls.TextBox, AutoSortSuggestionPopupState>();

        private sealed class DesktopSortTemplate
        {
            public string RuleId { get; init; } = "";
            public string NameResourceKey { get; init; } = "";
            public string DefaultPanelResourceKey { get; init; } = "";
            public bool MatchFolders { get; init; }
            public bool CatchAll { get; init; }
            public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();
        }

        private sealed class DesktopSortResult
        {
            public int MovedCount { get; set; }
            public int ErrorCount { get; set; }
            public int SkippedCount { get; set; }
            public Dictionary<string, List<DesktopSortMovedItem>> TargetPanels { get; } = new Dictionary<string, List<DesktopSortMovedItem>>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class DesktopSortMovedItem
        {
            public string SourcePath { get; init; } = "";
            public string TargetPath { get; init; } = "";
        }

        private sealed class AutoSortTargetMatch
        {
            public DesktopPanel? OpenPanel { get; init; }
            public int OpenTabIndex { get; init; } = -1;
            public WindowData? SavedPanel { get; init; }
            public int SavedTabIndex { get; init; } = -1;

            public bool HasOpenPanel => OpenPanel != null;
            public bool HasSavedPanel => SavedPanel != null;
        }

        private sealed class AutoSortSuggestionPopupState
        {
            public System.Windows.Controls.Primitives.Popup Popup { get; init; } = null!;
            public System.Windows.Controls.StackPanel ItemsHost { get; init; } = null!;
            public System.Windows.Controls.Border Container { get; init; } = null!;
        }

        private static readonly IReadOnlyList<DesktopSortTemplate> DesktopSortTemplates = new List<DesktopSortTemplate>
        {
            new DesktopSortTemplate
            {
                RuleId = RuleFolders,
                NameResourceKey = "Loc.AutoSortRuleFolders",
                DefaultPanelResourceKey = "Loc.AutoSortPanelFolders",
                MatchFolders = true
            },
            new DesktopSortTemplate
            {
                RuleId = RuleImages,
                NameResourceKey = "Loc.AutoSortRuleImages",
                DefaultPanelResourceKey = "Loc.AutoSortPanelImages",
                Extensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".svg", ".tif", ".tiff", ".raw", ".cr2", ".nef", ".arw" }
            },
            new DesktopSortTemplate
            {
                RuleId = RuleVideos,
                NameResourceKey = "Loc.AutoSortRuleVideos",
                DefaultPanelResourceKey = "Loc.AutoSortPanelVideos",
                Extensions = new[] { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".m4v", ".flv" }
            },
            new DesktopSortTemplate
            {
                RuleId = RuleAudio,
                NameResourceKey = "Loc.AutoSortRuleAudio",
                DefaultPanelResourceKey = "Loc.AutoSortPanelAudio",
                Extensions = new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma" }
            },
            new DesktopSortTemplate
            {
                RuleId = RuleDocuments,
                NameResourceKey = "Loc.AutoSortRuleDocuments",
                DefaultPanelResourceKey = "Loc.AutoSortPanelDocuments",
                Extensions = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".csv", ".odt", ".ods", ".md" }
            },
            new DesktopSortTemplate
            {
                RuleId = RuleArchives,
                NameResourceKey = "Loc.AutoSortRuleArchives",
                DefaultPanelResourceKey = "Loc.AutoSortPanelArchives",
                Extensions = new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz" }
            },
            new DesktopSortTemplate
            {
                RuleId = RuleInstallers,
                NameResourceKey = "Loc.AutoSortRuleInstallers",
                DefaultPanelResourceKey = "Loc.AutoSortPanelInstallers",
                Extensions = new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".bat", ".cmd" }
            },
            new DesktopSortTemplate
            {
                RuleId = RuleShortcuts,
                NameResourceKey = "Loc.AutoSortRuleShortcuts",
                DefaultPanelResourceKey = "Loc.AutoSortPanelShortcuts",
                Extensions = new[] { ".lnk", ".url" }
            },
            new DesktopSortTemplate
            {
                RuleId = RuleCode,
                NameResourceKey = "Loc.AutoSortRuleCode",
                DefaultPanelResourceKey = "Loc.AutoSortPanelCode",
                Extensions = new[] { ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".cpp", ".c", ".h", ".hpp", ".json", ".xml", ".yaml", ".yml", ".html", ".css", ".ps1", ".sql" }
            },
            new DesktopSortTemplate
            {
                RuleId = RuleOthers,
                NameResourceKey = "Loc.AutoSortRuleOthers",
                DefaultPanelResourceKey = "Loc.AutoSortPanelOthers",
                CatchAll = true
            }
        };

        private static bool IsBuiltInRuleId(string ruleId)
        {
            return DesktopSortTemplates.Any(t => string.Equals(t.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
        }

        private static string EnsureCustomRuleId(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !IsBuiltInRuleId(value))
            {
                return value;
            }

            return $"custom:{Guid.NewGuid():N}";
        }

        private static List<string> ParseExtensions(string? input)
        {
            var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(input))
            {
                return new List<string>();
            }

            char[] separators = { ',', ';', ' ', '\t', '\r', '\n' };
            var tokens = input.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                string ext = token.Trim();
                if (string.IsNullOrWhiteSpace(ext))
                {
                    continue;
                }

                if (!ext.StartsWith(".", StringComparison.Ordinal))
                {
                    ext = "." + ext;
                }

                ext = ext.ToLowerInvariant();
                if (ext.Length <= 1)
                {
                    continue;
                }

                if (ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    ext.Contains('\\') ||
                    ext.Contains('/'))
                {
                    continue;
                }

                list.Add(ext);
            }

            return list.OrderBy(x => x).ToList();
        }

        private static List<string> NormalizeExtensions(IEnumerable<string>? values)
        {
            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values == null) return new List<string>();

            foreach (var value in values)
            {
                foreach (var ext in ParseExtensions(value))
                {
                    normalized.Add(ext);
                }
            }

            return normalized.OrderBy(x => x).ToList();
        }

        private IReadOnlyList<string> GetDesktopDirectoryPaths()
        {
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>(2);
            string[] candidates =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop")
            };

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string normalized;
                try
                {
                    normalized = Path.GetFullPath(candidate)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    continue;
                }

                if (!Directory.Exists(normalized))
                {
                    continue;
                }

                if (unique.Add(normalized))
                {
                    paths.Add(normalized);
                }
            }

            return paths;
        }

        private string GetDesktopAutoSortStorageRootPath()
        {
            string localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppDataPath, "DesktopPlus", AutoSortStorageFolderName);
        }

        private static string SanitizePanelFolderName(string? input)
        {
            string value = string.IsNullOrWhiteSpace(input) ? "Sorted" : input.Trim();
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray())
                .Trim()
                .TrimEnd('.');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "Sorted";
            }

            return sanitized;
        }

        private static string GetUniqueDestinationPath(string directory, string fileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            string candidate = Path.Combine(directory, fileName);
            int counter = 1;

            while (File.Exists(candidate) || Directory.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{baseName}_{counter++}{extension}");
            }

            return candidate;
        }

        private static bool IsPathInside(string candidatePath, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
            {
                return false;
            }

            try
            {
                string normalizedCandidate = Path.GetFullPath(candidatePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedRoot = Path.GetFullPath(rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsIgnoredDesktopEntry(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            string name = Path.GetFileName(path);
            if (string.Equals(name, DesktopAutoSortRootFolderName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "thumbs.db", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                var attr = File.GetAttributes(path);
                bool isDirectory = (attr & FileAttributes.Directory) != 0;
                bool isSystem = (attr & FileAttributes.System) != 0;
                bool isReparsePoint = (attr & FileAttributes.ReparsePoint) != 0;

                // Skip potentially unsafe desktop folder links (junctions/symlinks),
                // but allow files (including cloud placeholders and shortcuts).
                if (isDirectory && isReparsePoint)
                {
                    return true;
                }

                // Keep system folders out of sorting, but allow files so .lnk/.url
                // and similar entries are still routed by extension rules.
                if (isDirectory && isSystem)
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }

            return false;
        }

        private void NormalizeDesktopAutoSortSettings()
        {
            _desktopAutoSort ??= new DesktopAutoSortSettings();
            _desktopAutoSort.Rules ??= new List<DesktopSortRuleState>();

            var existingBuiltIns = _desktopAutoSort.Rules
                .Where(r => r != null && (r.IsBuiltIn || IsBuiltInRuleId(r.RuleId)))
                .GroupBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            var mergedRules = new List<DesktopSortRuleState>();

            foreach (var template in DesktopSortTemplates)
            {
                existingBuiltIns.TryGetValue(template.RuleId, out var existing);
                string targetPanel = existing?.TargetPanelName ?? "";
                if (string.IsNullOrWhiteSpace(targetPanel))
                {
                    targetPanel = GetString(template.DefaultPanelResourceKey);
                }

                mergedRules.Add(new DesktopSortRuleState
                {
                    RuleId = template.RuleId,
                    IsBuiltIn = true,
                    Enabled = existing?.Enabled ?? true,
                    MatchFolders = template.MatchFolders,
                    CatchAll = template.CatchAll,
                    RuleName = GetString(template.NameResourceKey),
                    TargetPanelName = targetPanel,
                    Extensions = NormalizeExtensions(template.Extensions)
                });
            }

            var customRules = _desktopAutoSort.Rules
                .Where(r => r != null && !IsBuiltInRuleId(r.RuleId) && !r.IsBuiltIn)
                .Select(r =>
                {
                    var normalizedExt = NormalizeExtensions(r.Extensions);
                    if (normalizedExt.Count == 0)
                    {
                        return null;
                    }

                    return new DesktopSortRuleState
                    {
                        RuleId = EnsureCustomRuleId(r.RuleId),
                        IsBuiltIn = false,
                        Enabled = r.Enabled,
                        MatchFolders = false,
                        CatchAll = false,
                        RuleName = string.IsNullOrWhiteSpace(r.RuleName)
                            ? string.Format(GetString("Loc.AutoSortCustomRuleNameFormat"), string.Join(", ", normalizedExt))
                            : r.RuleName,
                        TargetPanelName = string.IsNullOrWhiteSpace(r.TargetPanelName) ? GetString("Loc.AutoSortPanelOthers") : r.TargetPanelName,
                        Extensions = normalizedExt
                    };
                })
                .Where(r => r != null)
                .Cast<DesktopSortRuleState>()
                .ToList();

            mergedRules.AddRange(customRules);
            _desktopAutoSort.Rules = mergedRules;
            RefreshDesktopAutoSortRuleViews();
        }

        private void RefreshDesktopAutoSortRuleViews()
        {
            if (_desktopAutoSort?.Rules == null)
            {
                return;
            }

            foreach (var rule in _desktopAutoSort.Rules)
            {
                if (rule == null) continue;

                if (rule.IsBuiltIn && IsBuiltInRuleId(rule.RuleId))
                {
                    var template = DesktopSortTemplates.FirstOrDefault(t =>
                        string.Equals(t.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase));
                    rule.DisplayName = template != null ? GetString(template.NameResourceKey) : rule.RuleName;
                }
                else
                {
                    rule.DisplayName = string.IsNullOrWhiteSpace(rule.RuleName)
                        ? string.Format(GetString("Loc.AutoSortCustomRuleNameFormat"), string.Join(", ", rule.Extensions))
                        : rule.RuleName;
                }

                if (rule.MatchFolders)
                {
                    rule.ExtensionsSummary = GetString("Loc.AutoSortExtensionsFolders");
                }
                else if (rule.CatchAll)
                {
                    rule.ExtensionsSummary = GetString("Loc.AutoSortExtensionsCatchAll");
                }
                else
                {
                    rule.ExtensionsSummary = string.Join(", ", NormalizeExtensions(rule.Extensions));
                }
            }

            if (AutoSortBuiltInRulesList != null)
            {
                AutoSortBuiltInRulesList.ItemsSource = null;
                AutoSortBuiltInRulesList.ItemsSource = _desktopAutoSort.Rules
                    .Where(r => r.IsBuiltIn)
                    .ToList();
            }

            if (AutoSortCustomRulesList != null)
            {
                AutoSortCustomRulesList.ItemsSource = null;
                AutoSortCustomRulesList.ItemsSource = _desktopAutoSort.Rules
                    .Where(r => !r.IsBuiltIn)
                    .OrderBy(r => r.DisplayName)
                    .ToList();
            }
        }

        private void ApplyDesktopAutoSortSettingsToUi()
        {
            _suspendDesktopAutoSortHandlers = true;

            if (AutoSortToggle != null)
            {
                AutoSortToggle.IsChecked = _desktopAutoSort.AutoSortEnabled;
            }

            RefreshDesktopAutoSortRuleViews();

            _suspendDesktopAutoSortHandlers = false;
            if (string.IsNullOrWhiteSpace(_desktopAutoSortStatusMessage))
            {
                _desktopAutoSortStatusMessage = _desktopAutoSort.AutoSortEnabled
                    ? GetString("Loc.AutoSortStatusEnabled")
                    : GetString("Loc.AutoSortStatusDisabled");
            }
            SetDesktopAutoSortStatus(_desktopAutoSortStatusMessage);
        }

        private void SetDesktopAutoSortStatus(string message)
        {
            _desktopAutoSortStatusMessage = message;
            if (AutoSortStatusText != null)
            {
                AutoSortStatusText.Text = message;
            }
        }

        private void ConfigureDesktopAutoSortWatcher()
        {
            StopDesktopAutoSortWatcher();

            if (!_desktopAutoSort.AutoSortEnabled)
            {
                return;
            }

            var desktopPaths = GetDesktopDirectoryPaths();
            if (desktopPaths.Count == 0)
            {
                return;
            }

            _desktopAutoSortDebounceTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _desktopAutoSortDebounceTimer.Tick -= DesktopAutoSortDebounceTimer_Tick;
            _desktopAutoSortDebounceTimer.Tick += DesktopAutoSortDebounceTimer_Tick;

            foreach (string desktopPath in desktopPaths)
            {
                try
                {
                    var watcher = new FileSystemWatcher(desktopPath)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime
                    };
                    watcher.Created += DesktopAutoSortWatcher_Changed;
                    watcher.Renamed += DesktopAutoSortWatcher_Changed;
                    watcher.EnableRaisingEvents = true;
                    _desktopAutoSortWatchers.Add(watcher);
                }
                catch
                {
                    // Ignore watcher setup failures for individual desktop roots.
                }
            }
        }

        private void StopDesktopAutoSortWatcher()
        {
            if (_desktopAutoSortDebounceTimer != null)
            {
                _desktopAutoSortDebounceTimer.Stop();
            }

            foreach (var watcher in _desktopAutoSortWatchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= DesktopAutoSortWatcher_Changed;
                    watcher.Renamed -= DesktopAutoSortWatcher_Changed;
                    watcher.Dispose();
                }
                catch
                {
                }
            }

            _desktopAutoSortWatchers.Clear();
        }

        private void DesktopAutoSortWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (_desktopAutoSortInProgress || !_desktopAutoSort.AutoSortEnabled)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_desktopAutoSortInProgress || !_desktopAutoSort.AutoSortEnabled)
                {
                    return;
                }

                _desktopAutoSortDebounceTimer?.Stop();
                _desktopAutoSortDebounceTimer?.Start();
            }));
        }

        private void DesktopAutoSortDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _desktopAutoSortDebounceTimer?.Stop();
            RunDesktopSort(showResultMessage: false);
        }

        private DesktopSortRuleState? ResolveRuleForPath(IEnumerable<DesktopSortRuleState> activeRules, string path, bool isDirectory)
        {
            if (isDirectory)
            {
                return activeRules.FirstOrDefault(r => r.MatchFolders);
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = "";
            }

            var custom = activeRules.FirstOrDefault(r =>
                !r.IsBuiltIn &&
                !r.MatchFolders &&
                !r.CatchAll &&
                r.Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase)));
            if (custom != null)
            {
                return custom;
            }

            var builtIn = activeRules.FirstOrDefault(r =>
                r.IsBuiltIn &&
                !r.MatchFolders &&
                !r.CatchAll &&
                r.Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase)));
            if (builtIn != null)
            {
                return builtIn;
            }

            return activeRules.FirstOrDefault(r => r.CatchAll);
        }

        private string ResolveTargetPanelName(DesktopSortRuleState rule)
        {
            if (!string.IsNullOrWhiteSpace(rule.TargetPanelName))
            {
                return rule.TargetPanelName.Trim();
            }

            if (rule.IsBuiltIn && IsBuiltInRuleId(rule.RuleId))
            {
                var template = DesktopSortTemplates.FirstOrDefault(t =>
                    string.Equals(t.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase));
                if (template != null)
                {
                    return GetString(template.DefaultPanelResourceKey);
                }
            }

            return GetString("Loc.AutoSortPanelOthers");
        }

        private static bool MatchesAutoSortPanelName(string expectedPanelName, string? title)
        {
            if (string.IsNullOrWhiteSpace(expectedPanelName))
            {
                return false;
            }

            string expected = expectedPanelName.Trim();
            if (!string.IsNullOrWhiteSpace(title) &&
                string.Equals(title.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static int FindMatchingTabIndex(IReadOnlyList<PanelTabData>? tabs, string expectedTargetName)
        {
            if (tabs == null || tabs.Count == 0 || string.IsNullOrWhiteSpace(expectedTargetName))
            {
                return -1;
            }

            for (int i = 0; i < tabs.Count; i++)
            {
                if (MatchesAutoSortPanelName(expectedTargetName, tabs[i]?.TabName))
                {
                    return i;
                }
            }

            return -1;
        }

        private AutoSortTargetMatch? ResolveAutoSortTargetMatch(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
            {
                return null;
            }

            string normalizedTarget = targetName.Trim();
            var openPanels = System.Windows.Application.Current.Windows
                .OfType<DesktopPanel>()
                .Where(IsUserPanel)
                .ToList();

            foreach (var panel in openPanels)
            {
                int tabIndex = panel.FindTabIndexByName(normalizedTarget);
                if (tabIndex >= 0)
                {
                    return new AutoSortTargetMatch
                    {
                        OpenPanel = panel,
                        OpenTabIndex = tabIndex
                    };
                }
            }

            foreach (var panel in openPanels)
            {
                if (MatchesAutoSortPanelName(normalizedTarget, panel.PanelTitle?.Text ?? panel.Title))
                {
                    return new AutoSortTargetMatch
                    {
                        OpenPanel = panel
                    };
                }
            }

            var openPanelKeys = new HashSet<string>(
                openPanels.Select(GetPanelKey),
                StringComparer.OrdinalIgnoreCase);

            foreach (var saved in savedWindows)
            {
                if (saved == null || IsInternalPreviewWindowData(saved))
                {
                    continue;
                }

                NormalizeWindowData(saved);
                if (openPanelKeys.Contains(GetPanelKey(saved)))
                {
                    continue;
                }

                int tabIndex = FindMatchingTabIndex(saved.Tabs, normalizedTarget);
                if (tabIndex >= 0)
                {
                    return new AutoSortTargetMatch
                    {
                        SavedPanel = saved,
                        SavedTabIndex = tabIndex
                    };
                }
            }

            foreach (var saved in savedWindows)
            {
                if (saved == null || IsInternalPreviewWindowData(saved))
                {
                    continue;
                }

                NormalizeWindowData(saved);
                if (openPanelKeys.Contains(GetPanelKey(saved)))
                {
                    continue;
                }

                if (MatchesAutoSortPanelName(normalizedTarget, saved.PanelTitle))
                {
                    return new AutoSortTargetMatch
                    {
                        SavedPanel = saved
                    };
                }
            }

            return null;
        }

        private static List<string> MergeDistinctPaths(IEnumerable<string>? existing, IEnumerable<string> additions)
        {
            var merged = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (existing != null)
            {
                foreach (string path in existing.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    if (seen.Add(path))
                    {
                        merged.Add(path);
                    }
                }
            }

            if (additions != null)
            {
                foreach (string path in additions.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    if (seen.Add(path))
                    {
                        merged.Add(path);
                    }
                }
            }

            return merged;
        }

        private DesktopSortResult SortDesktopOnce()
        {
            var result = new DesktopSortResult();
            var desktopPaths = GetDesktopDirectoryPaths();
            if (desktopPaths.Count == 0)
            {
                return result;
            }

            string storageRootPath = GetDesktopAutoSortStorageRootPath();
            Directory.CreateDirectory(storageRootPath);

            var activeRules = _desktopAutoSort.Rules
                .Where(r => r.Enabled)
                .ToList();

            if (!activeRules.Any())
            {
                return result;
            }

            var resolvedTargetFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string desktopPath in desktopPaths)
            {
                IEnumerable<string> entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(desktopPath).ToList();
                }
                catch
                {
                    result.ErrorCount++;
                    continue;
                }

                foreach (string entry in entries)
                {
                    if (IsIgnoredDesktopEntry(entry))
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    bool isDirectory = Directory.Exists(entry);
                    var rule = ResolveRuleForPath(activeRules, entry, isDirectory);
                    if (rule == null)
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    string panelName = ResolveTargetPanelName(rule);
                    if (!resolvedTargetFolders.TryGetValue(panelName, out string? targetFolderPath))
                    {
                        targetFolderPath = Path.Combine(storageRootPath, SanitizePanelFolderName(panelName));

                        try
                        {
                            Directory.CreateDirectory(targetFolderPath);
                        }
                        catch
                        {
                            result.ErrorCount++;
                            continue;
                        }

                        resolvedTargetFolders[panelName] = targetFolderPath;
                    }

                    string name = Path.GetFileName(entry);
                    string targetPath = GetUniqueDestinationPath(targetFolderPath, name);

                    try
                    {
                        if (isDirectory)
                        {
                            Directory.Move(entry, targetPath);
                        }
                        else
                        {
                            File.Move(entry, targetPath);
                        }

                        result.MovedCount++;
                        if (!result.TargetPanels.TryGetValue(panelName, out List<DesktopSortMovedItem>? movedItems))
                        {
                            movedItems = new List<DesktopSortMovedItem>();
                            result.TargetPanels[panelName] = movedItems;
                        }

                        movedItems.Add(new DesktopSortMovedItem
                        {
                            SourcePath = entry,
                            TargetPath = targetPath
                        });
                    }
                    catch
                    {
                        result.ErrorCount++;
                    }
                }
            }

            return result;
        }

        private static Rect BuildRectForWindow(double left, double top, double width, double height)
        {
            const double minWidth = 120;
            const double minHeight = 80;
            double resolvedWidth = double.IsNaN(width) || width <= 0 ? minWidth : width;
            double resolvedHeight = double.IsNaN(height) || height <= 0 ? minHeight : height;
            return new Rect(left, top, resolvedWidth, resolvedHeight);
        }

        private static bool RectOverlapsWithPadding(Rect candidate, Rect occupied)
        {
            const double overlapPadding = 8;
            var expanded = new Rect(
                occupied.Left - overlapPadding,
                occupied.Top - overlapPadding,
                occupied.Width + (overlapPadding * 2),
                occupied.Height + (overlapPadding * 2));
            return expanded.IntersectsWith(candidate);
        }

        private void CaptureAutoSortOccupiedRects()
        {
            _autoSortOccupiedRects.Clear();

            var visiblePanels = System.Windows.Application.Current.Windows
                .OfType<DesktopPanel>()
                .Where(panel => IsUserPanel(panel) && panel.IsVisible)
                .ToList();

            foreach (var panel in visiblePanels)
            {
                double width = panel.ActualWidth > 0 ? panel.ActualWidth : panel.Width;
                double height = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
                _autoSortOccupiedRects.Add(BuildRectForWindow(panel.Left, panel.Top, width, height));
            }
        }

        private (double Left, double Top) FindFreeAutoSortPanelPosition(
            int preferredIndex,
            double panelWidth,
            double panelHeight,
            double margin,
            double gap)
        {
            Rect workArea = SystemParameters.WorkArea;

            int rowsPerColumn = Math.Max(1, (int)((workArea.Height - (margin * 2) + gap) / (panelHeight + gap)));
            int columns = Math.Max(1, (int)((workArea.Width - (margin * 2) + gap) / (panelWidth + gap)));
            int slotCount = Math.Max(1, rowsPerColumn * columns);

            (double Left, double Top) GetSlotPosition(int slotIndex)
            {
                int normalizedSlot = ((slotIndex % slotCount) + slotCount) % slotCount;
                int column = normalizedSlot / rowsPerColumn;
                int row = normalizedSlot % rowsPerColumn;

                double left = workArea.Right - margin - panelWidth - (column * (panelWidth + gap));
                if (left < workArea.Left + margin)
                {
                    left = workArea.Left + margin;
                }

                double top = workArea.Top + margin + (row * (panelHeight + gap));
                if (top + panelHeight > workArea.Bottom - margin)
                {
                    top = Math.Max(workArea.Top + margin, workArea.Bottom - margin - panelHeight);
                }

                return (left, top);
            }

            for (int offset = 0; offset < slotCount; offset++)
            {
                var candidate = GetSlotPosition(preferredIndex + offset);
                Rect candidateRect = BuildRectForWindow(candidate.Left, candidate.Top, panelWidth, panelHeight);
                bool intersects = _autoSortOccupiedRects.Any(occupied => RectOverlapsWithPadding(candidateRect, occupied));
                if (!intersects)
                {
                    _autoSortOccupiedRects.Add(candidateRect);
                    return candidate;
                }
            }

            var fallback = GetSlotPosition(preferredIndex);
            double offsetStep = 28;
            double shiftedLeft = fallback.Left + ((_newAutoSortPanelIndex % 6) * offsetStep);
            double shiftedTop = fallback.Top + ((_newAutoSortPanelIndex % 6) * offsetStep);
            shiftedLeft = Math.Min(
                Math.Max(workArea.Left + margin, shiftedLeft),
                Math.Max(workArea.Left + margin, workArea.Right - margin - panelWidth));
            shiftedTop = Math.Min(
                Math.Max(workArea.Top + margin, shiftedTop),
                Math.Max(workArea.Top + margin, workArea.Bottom - margin - panelHeight));

            var fallbackRect = BuildRectForWindow(shiftedLeft, shiftedTop, panelWidth, panelHeight);
            _autoSortOccupiedRects.Add(fallbackRect);
            return (shiftedLeft, shiftedTop);
        }

        private WindowData CreateAutoSortPanelData(string panelName, IEnumerable<string> initialItems, int index)
        {
            const double panelWidth = 420;
            const double panelHeight = 360;
            const double margin = 24;
            const double gap = 12;

            var position = FindFreeAutoSortPanelPosition(index, panelWidth, panelHeight, margin, gap);

            return new WindowData
            {
                PanelId = GeneratePanelId(),
                PanelType = PanelKind.List.ToString(),
                FolderPath = "",
                DefaultFolderPath = "",
                Left = position.Left,
                Top = position.Top,
                Width = panelWidth,
                Height = panelHeight,
                ExpandedHeight = panelHeight,
                PanelTitle = panelName,
                PresetName = DefaultPresetName,
                IsCollapsed = false,
                IsHidden = false,
                ShowHidden = false,
                ShowFileExtensions = true,
                ShowSettingsButton = true,
                ExpandOnHover = false,
                OpenFoldersExternally = false,
                MovementMode = "titlebar",
                SearchVisibilityMode = DesktopPanel.SearchVisibilityAlways,
                PinnedItems = MergeDistinctPaths(Array.Empty<string>(), initialItems)
            };
        }

        private void EnsureAutoSortPanel(string panelName, IReadOnlyList<DesktopSortMovedItem> movedItems)
        {
            var movedPaths = movedItems
                .Select(item => item.TargetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (movedPaths.Count == 0)
            {
                return;
            }

            var targetMatch = ResolveAutoSortTargetMatch(panelName);

            if (targetMatch != null && targetMatch.HasOpenPanel)
            {
                var openPanel = targetMatch.OpenPanel!;
                if (targetMatch.OpenTabIndex >= 0)
                {
                    openPanel.AppendItemsToTab(targetMatch.OpenTabIndex, movedPaths, animateEntries: true);
                    return;
                }

                openPanel.AppendItemsToList(movedPaths, animateEntries: true);
                openPanel.defaultFolderPath = "";
                openPanel.currentFolderPath = "";
                openPanel.PanelType = PanelKind.List;
                if (!string.IsNullOrWhiteSpace(panelName))
                {
                    openPanel.Title = panelName;
                    openPanel.PanelTitle.Text = panelName;
                }
                return;
            }

            if (targetMatch != null && targetMatch.HasSavedPanel)
            {
                var savedPanel = targetMatch.SavedPanel!;
                NormalizeWindowData(savedPanel);
                savedPanel.IsHidden = false;

                if (targetMatch.SavedTabIndex >= 0 &&
                    savedPanel.Tabs != null &&
                    targetMatch.SavedTabIndex < savedPanel.Tabs.Count)
                {
                    var targetTab = savedPanel.Tabs[targetMatch.SavedTabIndex];
                    targetTab.PinnedItems = MergeDistinctPaths(targetTab.PinnedItems, movedPaths);
                    targetTab.PanelType = PanelKind.List.ToString();
                    targetTab.FolderPath = "";
                    targetTab.DefaultFolderPath = "";
                }
                else
                {
                    savedPanel.PanelType = PanelKind.List.ToString();
                    savedPanel.FolderPath = "";
                    savedPanel.DefaultFolderPath = "";
                    savedPanel.PinnedItems = MergeDistinctPaths(savedPanel.PinnedItems, movedPaths);
                    savedPanel.PanelTitle = panelName;
                }

                DesktopPanel openedPanel = OpenPanelFromData(savedPanel);
                if (targetMatch.SavedTabIndex >= 0)
                {
                    if (openedPanel.ActiveTabIndex == targetMatch.SavedTabIndex)
                    {
                        openedPanel.AnimateListItemsForPaths(movedPaths);
                    }
                }
                else
                {
                    openedPanel.AnimateListItemsForPaths(movedPaths);
                }

                _autoSortOccupiedRects.Add(BuildRectForWindow(
                    openedPanel.Left,
                    openedPanel.Top,
                    openedPanel.ActualWidth > 0 ? openedPanel.ActualWidth : openedPanel.Width,
                    openedPanel.ActualHeight > 0 ? openedPanel.ActualHeight : openedPanel.Height));
                return;
            }

            var data = CreateAutoSortPanelData(panelName, movedPaths, _newAutoSortPanelIndex++);
            DesktopPanel panel = OpenPanelFromData(data);
            panel.AnimateListItemsForPaths(movedPaths);
        }

        private void EnsureAutoSortPanels(Dictionary<string, List<DesktopSortMovedItem>> targetPanels)
        {
            _newAutoSortPanelIndex = 0;
            CaptureAutoSortOccupiedRects();
            foreach (var pair in targetPanels.OrderBy(p => p.Key))
            {
                EnsureAutoSortPanel(pair.Key, pair.Value);
            }
        }

        private void RunDesktopSort(bool showResultMessage)
        {
            if (_desktopAutoSortInProgress)
            {
                return;
            }

            _desktopAutoSortInProgress = true;
            try
            {
                var result = SortDesktopOnce();
                if (result.MovedCount > 0)
                {
                    EnsureAutoSortPanels(result.TargetPanels);
                    SaveSettings();
                    NotifyPanelsChanged();
                }

                string statusText;
                if (result.MovedCount == 0 && result.ErrorCount == 0)
                {
                    statusText = GetString("Loc.AutoSortMsgSortNoItems");
                }
                else if (result.ErrorCount > 0)
                {
                    statusText = string.Format(GetString("Loc.AutoSortMsgSortDoneWithErrors"), result.MovedCount, result.ErrorCount);
                }
                else
                {
                    statusText = string.Format(GetString("Loc.AutoSortMsgSortDone"), result.MovedCount);
                }

                SetDesktopAutoSortStatus(statusText);

                if (showResultMessage)
                {
                    MessageBoxImage icon = result.ErrorCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
                    System.Windows.MessageBox.Show(
                        statusText,
                        GetString("Loc.MsgInfo"),
                        MessageBoxButton.OK,
                        icon);
                }
            }
            catch (Exception ex)
            {
                string message = string.Format(GetString("Loc.AutoSortMsgSortFailed"), ex.Message);
                SetDesktopAutoSortStatus(message);

                if (showResultMessage)
                {
                    System.Windows.MessageBox.Show(
                        message,
                        GetString("Loc.MsgError"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                _desktopAutoSortInProgress = false;
            }
        }

        private DesktopSortRuleState? FindDesktopSortRuleById(string? ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                return null;
            }

            return _desktopAutoSort.Rules.FirstOrDefault(r =>
                string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
        }

        private static void AddAutoSortSuggestion(HashSet<string> set, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string normalized = value.Trim();
            if (normalized.Length == 0)
            {
                return;
            }

            set.Add(normalized);
        }

        private List<string> GetAutoSortTargetNameSuggestions()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var panel in System.Windows.Application.Current.Windows.OfType<DesktopPanel>().Where(IsUserPanel))
            {
                AddAutoSortSuggestion(names, panel.PanelTitle?.Text);
                AddAutoSortSuggestion(names, panel.Title);
                foreach (var tab in panel.Tabs)
                {
                    AddAutoSortSuggestion(names, tab.TabName);
                }
            }

            foreach (var saved in savedWindows)
            {
                if (saved == null || IsInternalPreviewWindowData(saved))
                {
                    continue;
                }

                NormalizeWindowData(saved);
                AddAutoSortSuggestion(names, saved.PanelTitle);
                if (saved.Tabs == null)
                {
                    continue;
                }

                foreach (var tab in saved.Tabs)
                {
                    AddAutoSortSuggestion(names, tab.TabName);
                }
            }

            return names
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<string> FilterAutoSortTargetSuggestions(IEnumerable<string> suggestions, string currentText)
        {
            string input = (currentText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return suggestions.Take(10).ToList();
            }

            var startsWith = new List<string>();
            var contains = new List<string>();

            foreach (string suggestion in suggestions)
            {
                if (string.Equals(suggestion, input, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (suggestion.StartsWith(input, StringComparison.CurrentCultureIgnoreCase))
                {
                    startsWith.Add(suggestion);
                }
                else if (suggestion.IndexOf(input, StringComparison.CurrentCultureIgnoreCase) >= 0)
                {
                    contains.Add(suggestion);
                }
            }

            return startsWith.Concat(contains).Take(10).ToList();
        }

        private void ApplyAutoSortSuggestion(System.Windows.Controls.TextBox textBox, string suggestion)
        {
            if (string.IsNullOrWhiteSpace(suggestion))
            {
                return;
            }

            textBox.Text = suggestion;
            textBox.CaretIndex = textBox.Text.Length;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.Focus();
                CloseAutoSortSuggestionMenu(textBox);
            }), DispatcherPriority.Input);
        }

        private AutoSortSuggestionPopupState GetAutoSortSuggestionPopupState(System.Windows.Controls.TextBox textBox)
        {
            if (_autoSortTargetSuggestionPopups.TryGetValue(textBox, out AutoSortSuggestionPopupState? existing))
            {
                return existing;
            }

            var textBrush = TryFindResource("TextPrimary") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            var popupBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(26, 34, 48));
            var popupBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 83, 109));

            var itemsHost = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical
            };

            var scrollViewer = new System.Windows.Controls.ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                CanContentScroll = true,
                Content = itemsHost
            };

            var container = new System.Windows.Controls.Border
            {
                Background = popupBackground,
                BorderBrush = popupBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(5),
                Margin = new Thickness(0, 6, 0, 0),
                MinWidth = Math.Max(180, textBox.ActualWidth),
                Child = scrollViewer,
                SnapsToDevicePixels = true
            };
            if (TryFindResource("InputBackground") is System.Windows.Media.Brush inputBackground)
            {
                container.Background = inputBackground;
            }
            if (TryFindResource("InputBorder") is System.Windows.Media.Brush inputBorder)
            {
                container.BorderBrush = inputBorder;
            }

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = textBox,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                AllowsTransparency = true,
                StaysOpen = true,
                Child = container
            };

            var state = new AutoSortSuggestionPopupState
            {
                Popup = popup,
                ItemsHost = itemsHost,
                Container = container
            };

            textBox.SizeChanged += (_, __) =>
            {
                state.Container.MinWidth = Math.Max(180, textBox.ActualWidth);
            };

            _autoSortTargetSuggestionPopups[textBox] = state;
            return state;
        }

        private void CloseAutoSortSuggestionMenu(System.Windows.Controls.TextBox textBox)
        {
            if (_autoSortTargetSuggestionPopups.TryGetValue(textBox, out AutoSortSuggestionPopupState? state))
            {
                state.Popup.IsOpen = false;
                state.ItemsHost.Children.Clear();
            }
        }

        private void ShowAutoSortTargetSuggestions(System.Windows.Controls.TextBox textBox, bool forceOpen)
        {
            var allSuggestions = GetAutoSortTargetNameSuggestions();
            var filtered = FilterAutoSortTargetSuggestions(allSuggestions, textBox.Text ?? "");
            if (filtered.Count == 0)
            {
                CloseAutoSortSuggestionMenu(textBox);
                return;
            }

            var textBrush = TryFindResource("TextPrimary") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            var hoverBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 48, 58));

            AutoSortSuggestionPopupState popupState = GetAutoSortSuggestionPopupState(textBox);
            popupState.ItemsHost.Children.Clear();

            foreach (string suggestion in filtered)
            {
                var itemBorder = new System.Windows.Controls.Border
                {
                    Background = System.Windows.Media.Brushes.Transparent,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 1, 0, 1),
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                var itemText = new System.Windows.Controls.TextBlock
                {
                    Text = suggestion,
                    Foreground = textBrush,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                itemBorder.Child = itemText;
                itemBorder.MouseEnter += (_, __) => itemBorder.Background = hoverBrush;
                itemBorder.MouseLeave += (_, __) => itemBorder.Background = System.Windows.Media.Brushes.Transparent;

                string capturedSuggestion = suggestion;
                itemBorder.MouseLeftButtonDown += (_, __) => ApplyAutoSortSuggestion(textBox, capturedSuggestion);
                popupState.ItemsHost.Children.Add(itemBorder);
            }

            if (forceOpen || textBox.IsKeyboardFocusWithin)
            {
                popupState.Popup.IsOpen = true;
            }
        }

        private void AutoSortTargetTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox textBox)
            {
                return;
            }

            textBox.Unloaded -= AutoSortTargetTextBox_Unloaded;
            textBox.Unloaded += AutoSortTargetTextBox_Unloaded;
        }

        private void AutoSortTargetTextBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox textBox)
            {
                return;
            }

            CloseAutoSortSuggestionMenu(textBox);
            _autoSortTargetSuggestionPopups.Remove(textBox);
        }

        private void AutoSortTargetTextBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox textBox)
            {
                return;
            }

            ShowAutoSortTargetSuggestions(textBox, forceOpen: true);
        }

        private void AutoSortTargetTextBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox textBox)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_autoSortTargetSuggestionPopups.TryGetValue(textBox, out AutoSortSuggestionPopupState? state))
                {
                    if (!textBox.IsKeyboardFocusWithin && !state.Container.IsMouseOver)
                    {
                        state.Popup.IsOpen = false;
                    }
                }
            }), DispatcherPriority.Background);
        }

        private void AutoSortTargetTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox textBox)
            {
                return;
            }

            if (e.Key == System.Windows.Input.Key.Down)
            {
                ShowAutoSortTargetSuggestions(textBox, forceOpen: true);
                e.Handled = true;
                return;
            }

            if (e.Key == System.Windows.Input.Key.Escape)
            {
                CloseAutoSortSuggestionMenu(textBox);
                e.Handled = true;
            }
        }

        private void AutoSortToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendDesktopAutoSortHandlers)
            {
                return;
            }

            _desktopAutoSort.AutoSortEnabled = AutoSortToggle?.IsChecked == true;
            ConfigureDesktopAutoSortWatcher();
            SetDesktopAutoSortStatus(_desktopAutoSort.AutoSortEnabled
                ? GetString("Loc.AutoSortStatusEnabled")
                : GetString("Loc.AutoSortStatusDisabled"));
            SaveSettings();
        }

        private void SortDesktopNow_Click(object sender, RoutedEventArgs e)
        {
            RunDesktopSort(showResultMessage: true);
        }

        private void ResetAutoSortRules_Click(object sender, RoutedEventArgs e)
        {
            _desktopAutoSort.Rules = new List<DesktopSortRuleState>();
            NormalizeDesktopAutoSortSettings();
            ApplyDesktopAutoSortSettingsToUi();
            SetDesktopAutoSortStatus(_desktopAutoSort.AutoSortEnabled
                ? GetString("Loc.AutoSortStatusEnabled")
                : GetString("Loc.AutoSortStatusDisabled"));
            SaveSettings();
        }

        private void AutoSortRuleEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendDesktopAutoSortHandlers)
            {
                return;
            }

            if (sender is not System.Windows.Controls.CheckBox checkBox)
            {
                return;
            }

            string ruleId = checkBox.Tag?.ToString() ?? "";
            var rule = FindDesktopSortRuleById(ruleId);
            if (rule == null)
            {
                return;
            }

            rule.Enabled = checkBox.IsChecked == true;
            SaveSettings();
        }

        private void AutoSortRuleTarget_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_suspendDesktopAutoSortHandlers)
            {
                return;
            }

            if (sender is not System.Windows.Controls.TextBox textBox)
            {
                return;
            }

            string ruleId = textBox.Tag?.ToString() ?? "";
            var rule = FindDesktopSortRuleById(ruleId);
            if (rule == null)
            {
                return;
            }

            rule.TargetPanelName = textBox.Text ?? "";
            ShowAutoSortTargetSuggestions(textBox, forceOpen: false);
            SaveSettings();
        }

        private void AutoSortCustomTargetInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox textBox)
            {
                return;
            }

            ShowAutoSortTargetSuggestions(textBox, forceOpen: false);
        }

        private void AddCustomSortRule_Click(object sender, RoutedEventArgs e)
        {
            string extensionInput = AutoSortCustomExtensionsInput?.Text ?? "";
            string panelTarget = AutoSortCustomTargetInput?.Text ?? "";
            var extensions = ParseExtensions(extensionInput);
            if (extensions.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    GetString("Loc.AutoSortMsgInvalidExtensions"),
                    GetString("Loc.MsgInfo"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(panelTarget))
            {
                panelTarget = GetString("Loc.AutoSortPanelOthers");
            }

            bool duplicate = _desktopAutoSort.Rules.Any(r =>
                !r.IsBuiltIn &&
                string.Equals(r.TargetPanelName?.Trim(), panelTarget.Trim(), StringComparison.OrdinalIgnoreCase) &&
                NormalizeExtensions(r.Extensions).SequenceEqual(extensions, StringComparer.OrdinalIgnoreCase));
            if (duplicate)
            {
                return;
            }

            _desktopAutoSort.Rules.Add(new DesktopSortRuleState
            {
                RuleId = EnsureCustomRuleId(null),
                IsBuiltIn = false,
                Enabled = true,
                MatchFolders = false,
                CatchAll = false,
                RuleName = string.Format(GetString("Loc.AutoSortCustomRuleNameFormat"), string.Join(", ", extensions)),
                TargetPanelName = panelTarget.Trim(),
                Extensions = extensions
            });

            RefreshDesktopAutoSortRuleViews();
            if (AutoSortCustomExtensionsInput != null)
            {
                AutoSortCustomExtensionsInput.Text = "";
            }
            if (AutoSortCustomTargetInput != null)
            {
                AutoSortCustomTargetInput.Text = "";
            }

            SetDesktopAutoSortStatus(GetString("Loc.AutoSortMsgRuleAdded"));
            SaveSettings();
        }

        private void RemoveCustomSortRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            string ruleId = element.Tag?.ToString() ?? "";
            var rule = FindDesktopSortRuleById(ruleId);
            if (rule == null || rule.IsBuiltIn)
            {
                return;
            }

            _desktopAutoSort.Rules.Remove(rule);
            RefreshDesktopAutoSortRuleViews();
            SetDesktopAutoSortStatus(GetString("Loc.AutoSortMsgRuleRemoved"));
            SaveSettings();
        }
    }
}
