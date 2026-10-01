using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class ShortcutDeduplicationTests
{
    private static DesktopSortRuleState ShortcutRule() => new()
    {
        TargetPanelName = "Shortcuts",
        Extensions = new List<string> { ".lnk", ".url" }
    };

    [Fact]
    public async Task Sort_MergesUserAndPublicDesktopLinksWithoutLosingTheirFiles()
    {
        using var desktop = new TempDir();
        using var publicDesktop = new TempDir();
        using var storage = new TempDir();
        using var application = new TempDir();
        string target = application.File("application.exe");
        string first = desktop.Combine("Application.lnk");
        string duplicate = publicDesktop.Combine("Application.lnk");
        WriteShortcut(first, target, workingDirectory: application.Path);
        WriteShortcut(duplicate, target, workingDirectory: application.Path, description: "Different shortcut metadata");
        DesktopSortMovedItem[]? movePlan = null;

        var result = await DesktopAutoSorter.RunAsync(new[] { desktop.Path, publicDesktop.Path },
            new[] { ShortcutRule() }, storage.Path, moves => movePlan = moves.ToArray());

        Assert.Equal(1, result.MovedCount);
        Assert.Equal(1, result.DuplicateShortcutCount);
        Assert.Equal(0, result.ErrorCount);
        string canonical = storage.Combine("Shortcuts", "Application.lnk");
        string savedDuplicate = storage.Combine(DesktopAutoSorter.DuplicateShortcutsFolderName, "Shortcuts", "Application.lnk");
        Assert.True(File.Exists(canonical));
        Assert.True(File.Exists(savedDuplicate));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(duplicate));
        Assert.Equal(ShortcutIdentity.TryRead(canonical), ShortcutIdentity.TryRead(savedDuplicate));
        Assert.All(result.TargetPanels["Shortcuts"], item => Assert.Equal(canonical, item.TargetPath));
        Assert.Contains(movePlan!, item => item.SourcePath == duplicate && item.TargetPath == savedDuplicate);

        var secondRun = await DesktopAutoSorter.RunAsync(new[] { desktop.Path, publicDesktop.Path },
            new[] { ShortcutRule() }, storage.Path, _ => Assert.Fail("Nothing changed; no further backup should be needed."));
        Assert.Equal(0, secondRun.MovedCount);
        Assert.Equal(0, secondRun.DuplicateShortcutCount);
    }

    [Fact]
    public async Task Sort_RepairsExistingNumberedUrlCopiesWithAnEmptyDesktop()
    {
        using var desktop = new TempDir();
        using var storage = new TempDir();
        string shortcuts = storage.Dir("Shortcuts");
        string duplicate = Path.Combine(shortcuts, "Game_1.url");
        WriteUrl(duplicate, "steam://rungameid/123");
        string canonical = Path.Combine(shortcuts, "Game.url");
        WriteUrl(canonical, "steam://rungameid/123", "IconIndex=9\n");
        WriteUrl(Path.Combine(shortcuts, "Another Game.url"), "steam://rungameid/456");
        DesktopSortMovedItem[]? movePlan = null;

        var result = await DesktopAutoSorter.RunAsync(new[] { desktop.Path }, new[] { ShortcutRule() },
            storage.Path, moves => movePlan = moves.ToArray());

        Assert.Equal(0, result.MovedCount);
        Assert.Equal(1, result.DuplicateShortcutCount);
        Assert.Equal(2, Directory.GetFiles(shortcuts).Length);
        Assert.True(File.Exists(canonical));
        Assert.Single(movePlan!);
        Assert.Equal(duplicate, movePlan![0].SourcePath);
        Assert.True(File.Exists(movePlan[0].TargetPath));
        Assert.Equal(canonical, Assert.Single(result.TargetPanels["Shortcuts"]).TargetPath);
    }

    [Theory]
    [InlineData("--profile=one", "--profile=two", false, 1, 1)]
    [InlineData("--profile=One", "--profile=one", false, 1, 1)]
    [InlineData("", "", true, 1, 1)]
    [InlineData("", "", false, 1, 3)]
    public async Task Sort_KeepsDifferentLaunchBehaviorSeparate(string firstArguments, string secondArguments,
        bool differentWorkingDirectory, int firstWindowStyle, int secondWindowStyle)
    {
        using var desktop = new TempDir();
        using var publicDesktop = new TempDir();
        using var storage = new TempDir();
        using var application = new TempDir();
        string target = application.File("application.exe");
        string alternateWorkingDirectory = application.Dir("alternate");
        WriteShortcut(desktop.Combine("Application.lnk"), target, firstArguments, application.Path, windowStyle: firstWindowStyle);
        WriteShortcut(publicDesktop.Combine("Application.lnk"), target, secondArguments,
            differentWorkingDirectory ? alternateWorkingDirectory : application.Path, windowStyle: secondWindowStyle);

        var result = await DesktopAutoSorter.RunAsync(new[] { desktop.Path, publicDesktop.Path },
            new[] { ShortcutRule() }, storage.Path, _ => { });

        Assert.Equal(2, result.MovedCount);
        Assert.Equal(0, result.DuplicateShortcutCount);
        string[] links = Directory.GetFiles(storage.Combine("Shortcuts"));
        Assert.Equal(2, links.Length);
        Assert.NotEqual(ShortcutIdentity.TryRead(links[0]), ShortcutIdentity.TryRead(links[1]));
    }

    [Fact]
    public async Task Sort_BackupFailureDoesNotTouchExistingDuplicates()
    {
        using var desktop = new TempDir();
        using var storage = new TempDir();
        string shortcuts = storage.Dir("Shortcuts");
        string canonical = Path.Combine(shortcuts, "Game.url");
        string duplicate = Path.Combine(shortcuts, "Game_1.url");
        WriteUrl(canonical, "steam://rungameid/123");
        WriteUrl(duplicate, "steam://rungameid/123");
        await Assert.ThrowsAsync<IOException>(() => DesktopAutoSorter.RunAsync(new[] { desktop.Path },
            new[] { ShortcutRule() }, storage.Path, _ => throw new IOException("Backup failed")));
        Assert.True(File.Exists(canonical));
        Assert.True(File.Exists(duplicate));
        Assert.False(Directory.Exists(storage.Combine(DesktopAutoSorter.DuplicateShortcutsFolderName)));
    }

    [Fact]
    public async Task Sort_FailedCanonicalMoveLeavesOtherDesktopLinkAvailable()
    {
        using var desktop = new TempDir();
        using var publicDesktop = new TempDir();
        using var storage = new TempDir();
        string first = desktop.Combine("Game.url");
        string duplicate = publicDesktop.Combine("Game.url");
        WriteUrl(first, "steam://rungameid/123");
        WriteUrl(duplicate, "steam://rungameid/123");

        var result = await DesktopAutoSorter.RunAsync(new[] { desktop.Path, publicDesktop.Path },
            new[] { ShortcutRule() }, storage.Path, _ => File.Delete(first));

        Assert.Equal(0, result.MovedCount);
        Assert.Equal(0, result.DuplicateShortcutCount);
        Assert.Equal(2, result.ErrorCount);
        Assert.True(File.Exists(duplicate));
    }

    [Fact]
    public async Task Sort_KeepsTheSameShortcutInDifferentCategories()
    {
        using var desktop = new TempDir();
        using var storage = new TempDir();
        string games = storage.Dir("Games");
        WriteUrl(Path.Combine(games, "Game.url"), "steam://rungameid/123");
        WriteUrl(desktop.Combine("Game.url"), "steam://rungameid/123");
        var result = await DesktopAutoSorter.RunAsync(new[] { desktop.Path }, new[] { ShortcutRule() }, storage.Path, _ => { });
        Assert.Equal(1, result.MovedCount);
        Assert.Equal(0, result.DuplicateShortcutCount);
        Assert.True(File.Exists(Path.Combine(games, "Game.url")));
        Assert.True(File.Exists(storage.Combine("Shortcuts", "Game.url")));
    }

    [Fact]
    public async Task Sort_DoesNotHideAUniqueLinkWhenCanonicalLaunchSettingsChangeDuringBackup()
    {
        using var desktop = new TempDir();
        using var storage = new TempDir();
        string shortcuts = storage.Dir("Shortcuts");
        string canonical = Path.Combine(shortcuts, "Game.url");
        string duplicate = desktop.Combine("Game.url");
        WriteUrl(canonical, "steam://rungameid/123");
        WriteUrl(duplicate, "steam://rungameid/123");

        var result = await DesktopAutoSorter.RunAsync(new[] { desktop.Path }, new[] { ShortcutRule() }, storage.Path,
            _ => WriteUrl(canonical, "steam://rungameid/456"));

        Assert.Equal(0, result.DuplicateShortcutCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.True(File.Exists(duplicate));
        Assert.False(Directory.Exists(storage.Combine(DesktopAutoSorter.DuplicateShortcutsFolderName)));
    }

    [Fact]
    public async Task Sort_UnreadableShortcutsAreNeverMerged()
    {
        using var desktop = new TempDir();
        using var publicDesktop = new TempDir();
        using var storage = new TempDir();
        desktop.File("broken.lnk", "not a shortcut");
        publicDesktop.File("broken.lnk", "not a shortcut");
        var result = await DesktopAutoSorter.RunAsync(new[] { desktop.Path, publicDesktop.Path },
            new[] { ShortcutRule() }, storage.Path, _ => { });
        Assert.Equal(2, result.MovedCount);
        Assert.Equal(0, result.DuplicateShortcutCount);
        Assert.Equal(2, Directory.GetFiles(storage.Combine("Shortcuts")).Length);
    }

    private static void WriteUrl(string path, string url, string extraMetadata = "") =>
        File.WriteAllText(path, "[InternetShortcut]\nURL=" + url + "\n" + extraMetadata);

    private static void WriteShortcut(string path, string target, string arguments = "", string workingDirectory = "",
        string description = "", int windowStyle = 1)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        object shell = Activator.CreateInstance(shellType)!;
        object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path })!;
        try
        {
            void Set(string name, object value) => shortcut.GetType().InvokeMember(name, BindingFlags.SetProperty, null, shortcut, new[] { value });
            Set("TargetPath", target);
            Set("Arguments", arguments);
            Set("WorkingDirectory", workingDirectory);
            Set("Description", description);
            Set("WindowStyle", windowStyle);
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
