using System.IO;
using System.Diagnostics;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class BackupRetentionTests
{
    [Fact]
    public void Maintain_KeepsFiveAcrossAllKindsAndProtectsSelectedRestore()
    {
        using var data = new TempDir();
        string[] kinds = { "manual", "1.6.5-before-1.6.8", "auto-sort", "critical", "manual", "update", "critical", "manual" };
        var paths = kinds.Select((kind, i) =>
        {
            string path = data.File($"DesktopPlus-backup-{kind}-{i}.zip");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i - 10));
            return path;
        }).ToArray();
        string unrelated = data.File("personal.zip");

        BackupRetention.Maintain(data.Path, paths[0], paths[^1]);

        Assert.Equal(5, Directory.GetFiles(data.Path, "DesktopPlus-backup-*.zip").Length);
        Assert.True(File.Exists(paths[0]));
        Assert.True(File.Exists(paths[^1]));
        Assert.True(File.Exists(unrelated));
        Assert.False(File.Exists(paths[1]));
        Assert.False(File.Exists(paths[2]));
        Assert.False(File.Exists(paths[3]));
    }

    [Fact]
    public void Maintain_LockedOldArchiveDoesNotPreventEnforcingLimit()
    {
        using var data = new TempDir();
        var paths = Enumerable.Range(0, 7).Select(i =>
        {
            string path = data.File($"DesktopPlus-backup-manual-{i}.zip");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i - 10));
            return path;
        }).ToArray();
        using var locked = new FileStream(paths[0], FileMode.Open, FileAccess.Read, FileShare.Read);

        BackupRetention.Maintain(data.Path);

        Assert.Equal(5, Directory.GetFiles(data.Path, "DesktopPlus-backup-*.zip").Length);
        Assert.True(File.Exists(paths[0]));
        Assert.True(File.Exists(paths[^1]));
        Assert.False(File.Exists(paths[1]));
        Assert.False(File.Exists(paths[2]));
    }

    [Fact]
    public void Cleanup_OnlyRemovesAbandonedRecognizableWorkDirectories()
    {
        using var data = new TempDir();
        string abandoned = data.Dir("staging-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(abandoned, "old-copy.txt"), "old staging contents");
        File.SetAttributes(Path.Combine(abandoned, "old-copy.txt"), FileAttributes.ReadOnly);
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));
        string active = data.Dir("staging-" + Guid.NewGuid().ToString("N"));
        string unknown = data.Dir("staging-my-files");
        Directory.SetLastWriteTimeUtc(unknown, DateTime.UtcNow.AddDays(-2));
        string storage = data.Dir("AutoSortStorage");
        string archive = data.File("DesktopPlus-backup-manual-keep.zip");

        BackupRetention.CleanAbandonedWorkDirectories(data.Path);

        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(unknown));
        Assert.True(Directory.Exists(storage));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void LoadingExistingHistory_EnforcesGlobalLimitAndKeepsDamagedArchivesDeletable()
    {
        using var data = new TempDir();
        foreach (int i in Enumerable.Range(0, 8))
        {
            string path = data.File($"DesktopPlus-backup-manual-{i}.zip", "not a ZIP file");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i - 10));
        }

        var backups = MainWindow.LoadAvailableUpdateBackups(data.Path);

        Assert.Equal(5, backups.Count);
        Assert.Equal(5, Directory.GetFiles(data.Path).Length);
        Assert.All(backups, backup => Assert.False(backup.CanRestore));
        File.Delete(backups[0].ArchivePath);
        Assert.Equal(4, MainWindow.LoadAvailableUpdateBackups(data.Path).Count);
    }

    [Fact]
    public void Cleanup_DoesNotFollowJunctionsIntoOriginalUserData()
    {
        using var backups = new TempDir();
        using var originals = new TempDir();
        string original = originals.File("original.txt", "must be kept");
        string staging = backups.Dir("staging-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", Path.Combine(staging, "linked-folder"), originals.Path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        Directory.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddDays(-2));

        BackupRetention.CleanAbandonedWorkDirectories(backups.Path);

        Assert.False(Directory.Exists(staging));
        Assert.Equal("must be kept", File.ReadAllText(original));
        Assert.True(Directory.Exists(originals.Path));
    }
}
