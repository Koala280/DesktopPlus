using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class DesktopAutoSorterTests
{
    private static DesktopSortRuleState AllFiles(string target = "Files") => new()
    {
        CatchAll = true,
        TargetPanelName = target
    };

    [Fact]
    public async Task RunAsync_ReturnsWhileBackupIsStillRunning()
    {
        using var desktop = new TempDir();
        using var storage = new TempDir();
        string source = desktop.File("document.txt");
        var backupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBackup = new ManualResetEventSlim();
        Task<DesktopSortResult> sort = DesktopAutoSorter.RunAsync(new[] { desktop.Path }, new[] { AllFiles() },
            storage.Path, _ =>
            {
                backupStarted.SetResult();
                if (!releaseBackup.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Test backup was not released.");
                }
            });
        try
        {
            await backupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(sort.IsCompleted);
            Assert.True(File.Exists(source));
        }
        finally
        {
            releaseBackup.Set();
            await sort.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, (await sort).MovedCount);
    }

    [Fact]
    public async Task RunAsync_BackupFailureLeavesDesktopUntouched()
    {
        using var desktop = new TempDir();
        using var storage = new TempDir();
        string source = desktop.File("keep.txt", "original contents");
        await Assert.ThrowsAsync<IOException>(() => DesktopAutoSorter.RunAsync(new[] { desktop.Path },
            new[] { AllFiles() }, storage.Path, _ => throw new IOException("Backup unavailable")));

        Assert.Equal("original contents", File.ReadAllText(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(storage.Path));
    }

    [Fact]
    public async Task RunAsync_UsesBackedUpPlanAndRuleSnapshotWithUniqueNames()
    {
        using var desktop = new TempDir();
        using var commonDesktop = new TempDir();
        using var storage = new TempDir();
        string source = desktop.File("photo.PNG", "first");
        commonDesktop.File("photo.PNG", "second");
        desktop.File("desktop.ini");
        string target = storage.Dir("Photos");
        File.WriteAllText(Path.Combine(target, "photo.PNG"), "existing");
        var imageRule = new DesktopSortRuleState
        {
            TargetPanelName = "Photos",
            Extensions = new List<string> { ".png" }
        };
        DesktopSortMovedItem[]? plannedMoves = null;
        var result = await DesktopAutoSorter.RunAsync(new[] { desktop.Path, commonDesktop.Path },
            new[] { imageRule, AllFiles() }, storage.Path, moves =>
            {
                plannedMoves = moves.ToArray();
                Assert.True(File.Exists(source));
                desktop.File("arrived-during-backup.png");
                imageRule.TargetPanelName = "Changed";
                imageRule.Extensions.Clear();
            });

        Assert.Equal(2, result.MovedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.NotNull(plannedMoves);
        Assert.Equal(2, plannedMoves.Length);
        Assert.Equal(2, plannedMoves.Select(move => move.TargetPath).Distinct().Count());
        Assert.All(plannedMoves, move => Assert.True(File.Exists(move.TargetPath)));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "photo.PNG")));
        Assert.True(File.Exists(desktop.Combine("arrived-during-backup.png")));
        Assert.True(File.Exists(desktop.Combine("desktop.ini")));
        Assert.False(Directory.Exists(storage.Combine("Changed")));
    }

    [Fact]
    public async Task RunAsync_MovesLargeFolderWithoutReadingItsContentsAndContinuesAfterErrors()
    {
        using var desktop = new TempDir();
        using var storage = new TempDir();
        string folder = desktop.Dir("Large folder");
        string child = Path.Combine(folder, "large.bin");
        using (var stream = File.Create(child))
        {
            stream.SetLength(128 * 1024 * 1024);
        }
        string disappearing = desktop.File("disappearing.txt");
        desktop.File("keep-moving.txt");
        var folderRule = new DesktopSortRuleState { MatchFolders = true, TargetPanelName = "Folders" };
        var result = await DesktopAutoSorter.RunAsync(new[] { desktop.Path }, new[] { folderRule, AllFiles() },
            storage.Path, moves =>
            {
                Assert.Equal(3, moves.Count);
                File.Delete(disappearing);
            });

        Assert.Equal(2, result.MovedCount);
        Assert.Equal(1, result.ErrorCount);
        string movedChild = storage.Combine("Folders", "Large folder", "large.bin");
        Assert.Equal(128 * 1024 * 1024, new FileInfo(movedChild).Length);
        Assert.True(File.Exists(storage.Combine("Files", "keep-moving.txt")));
    }

    [Fact]
    public void AutoSortBackup_ContainsOnlySettingsAndMoveHistory()
    {
        using var data = new TempDir();
        string settings = data.File("settings.json", "{\"Language\":\"de\"}");
        string source = data.File("large-desktop-file.bin");
        using var lockedSource = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        lockedSource.SetLength(128 * 1024 * 1024);
        string archivePath = data.Combine("auto-sort.zip");
        var moves = new[] { new DesktopSortMovedItem { SourcePath = source, TargetPath = data.Combine("sorted.bin") } };

        MainWindow.WriteDesktopAutoSortBackupArchive(archivePath, settings, moves, "Before sort", "Move history");

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Equal(3, archive.Entries.Count);
        Assert.True(new FileInfo(archivePath).Length < 16 * 1024);
        Assert.NotNull(archive.GetEntry("user-data/roaming/DesktopPlus_Settings.json"));
        using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        using var manifest = JsonDocument.Parse(reader.ReadToEnd());
        Assert.False(manifest.RootElement.GetProperty("CapturedAutoSortStorage").GetBoolean());
        Assert.Empty(manifest.RootElement.GetProperty("DesktopItems").EnumerateArray());
        Assert.Equal(source, manifest.RootElement.GetProperty("DesktopMoves")[0].GetProperty("SourcePath").GetString());
    }

    [Fact]
    public async Task MoveHistoryRestore_MovesBackFoldersAndPreservesExistingDesktopFiles()
    {
        using var data = new TempDir();
        string desktop = data.Dir("desktop");
        string storage = data.Dir("storage");
        string folder = Directory.CreateDirectory(Path.Combine(storage, "folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "contents.txt"), "folder contents");
        string sortedFile = Path.Combine(storage, "file.txt");
        File.WriteAllText(sortedFile, "sorted contents");
        string existing = Path.Combine(desktop, "file.txt");
        File.WriteAllText(existing, "new desktop contents");
        var moves = new[]
        {
            new DesktopSortMovedItem { SourcePath = Path.Combine(desktop, "folder"), TargetPath = folder },
            new DesktopSortMovedItem { SourcePath = existing, TargetPath = sortedFile },
            new DesktopSortMovedItem { SourcePath = Path.Combine(desktop, "failed.txt"), TargetPath = Path.Combine(storage, "missing.txt") }
        };
        await RunRestoreScriptAsync(desktop, storage, moves);

        Assert.Equal("folder contents", File.ReadAllText(Path.Combine(desktop, "folder", "contents.txt")));
        Assert.Equal("new desktop contents", File.ReadAllText(existing));
        Assert.Equal("sorted contents", File.ReadAllText(sortedFile));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task MoveHistoryRestore_RejectsTargetsOutsideSortStorage()
    {
        using var data = new TempDir();
        string desktop = data.Dir("desktop");
        string storage = data.Dir("storage");
        string unrelatedFile = data.File("unrelated.txt", "must stay here");
        var moves = new[] { new DesktopSortMovedItem { SourcePath = Path.Combine(desktop, "file.txt"), TargetPath = unrelatedFile } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunRestoreScriptAsync(desktop, storage, moves));
        Assert.Equal("must stay here", File.ReadAllText(unrelatedFile));
    }

    private static async Task RunRestoreScriptAsync(string desktop, string storage, DesktopSortMovedItem[] moves)
    {
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string manifest = JsonSerializer.Serialize(new { DesktopMoves = moves });
        string script = "$ErrorActionPreference = 'Stop'\n" +
            "$targetAutoSortDir = " + Quote(storage) + "\n$desktopRoots = @(" + Quote(desktop) + ")\n" +
            "$manifest = " + Quote(manifest) + " | ConvertFrom-Json\n" +
            "function Ensure-Directory([string]$path) { [IO.Directory]::CreateDirectory($path) | Out-Null }\n" +
            MainWindow.BuildDesktopMoveRestoreScript();
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await output;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await error);
        }
    }
}
