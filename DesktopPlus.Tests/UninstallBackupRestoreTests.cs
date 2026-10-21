using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class UninstallBackupRestoreTests
{
    [Theory]
    [InlineData("manual")]
    [InlineData("update")]
    [InlineData("critical")]
    [InlineData("auto-sort")]
    public void ReferenceBackups_NeverReadOrCopyUserFileContents(string kind)
    {
        using var data = new RestoreFixture();
        data.Write("settings.json", "{\"Language\":\"de\"}");
        data.Write("desktop/folder/large.bin", "contents");
        data.Write("storage/Files/stored.bin", "contents");
        using var lockedDesktopFile = new FileStream(data.PathFor("desktop/folder/large.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var lockedStoredFile = new FileStream(data.PathFor("storage/Files/stored.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        lockedDesktopFile.SetLength(128 * 1024 * 1024);
        lockedStoredFile.SetLength(128 * 1024 * 1024);

        string path = data.CreateReferenceArchive(kind);

        using var archive = ZipFile.OpenRead(path);
        Assert.Equal(3, archive.Entries.Count);
        Assert.True(new FileInfo(path).Length < 16 * 1024);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.True(manifest.RootElement.GetProperty("UsesFileReferences").GetBoolean());
        Assert.Equal(2, manifest.RootElement.GetProperty("FileReferences").GetArrayLength());
        Assert.Empty(manifest.RootElement.GetProperty("DesktopItems").EnumerateArray());
        Assert.False(manifest.RootElement.GetProperty("CapturedAutoSortStorage").GetBoolean());
    }

    [Fact]
    public async Task ReferenceRestore_ContinuesAfterDeletionConflictsExternalMovesAndLockedFiles()
    {
        using var data = new RestoreFixture();
        data.Write("settings.json", "settings before sorting");
        data.Write("desktop/folder/child.txt", "folder contents");
        foreach (string name in new[] { "renamed", "deleted", "conflict", "external", "locked" })
            data.Write("desktop/" + name + ".txt", name);
        string archive = data.CreateReferenceArchive();
        data.Write("settings.json", "settings after sorting");
        Directory.CreateDirectory(data.PathFor("storage/Files"));
        Directory.Move(data.PathFor("desktop/folder"), data.PathFor("storage/Files/renamed-folder"));
        File.Move(data.PathFor("desktop/renamed.txt"), data.PathFor("storage/Files/new-name.txt"));
        File.Delete(data.PathFor("desktop/deleted.txt"));
        File.Move(data.PathFor("desktop/conflict.txt"), data.PathFor("storage/Files/conflict.txt"));
        data.Write("desktop/conflict.txt", "new unrelated file");
        File.Move(data.PathFor("desktop/external.txt"), data.PathFor("external.txt"));
        File.Move(data.PathFor("desktop/locked.txt"), data.PathFor("storage/Files/locked.txt"));
        using var locked = new FileStream(data.PathFor("storage/Files/locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await data.RestoreAsync(archive);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal("settings before sorting", data.Read("settings.json"));
        Assert.Equal("folder contents", data.Read("desktop/folder/child.txt"));
        Assert.Equal("renamed", data.Read("desktop/renamed.txt"));
        Assert.False(File.Exists(data.PathFor("desktop/deleted.txt")));
        Assert.Equal("new unrelated file", data.Read("desktop/conflict.txt"));
        Assert.Equal("conflict", data.Read("storage/Files/conflict.txt"));
        Assert.Equal("external", data.Read("external.txt"));
        Assert.True(File.Exists(data.PathFor("storage/Files/locked.txt")));
        string report = data.Read("DesktopPlus-last-restore.txt");
        Assert.Contains("Skipped items: 4", report);
        Assert.Contains("deleted.txt", report);
        Assert.Contains("conflict.txt", report);
        Assert.Contains("external.txt", report);
        Assert.Contains("locked.txt", report);
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task ReferenceRestore_DoesNotSubstituteAnUnrelatedFileWithTheSameName()
    {
        using var data = new RestoreFixture();
        data.Write("desktop/file.txt", "original");
        string archive = data.CreateReferenceArchive();
        // Keep the original ID allocated so a replacement cannot reuse it in this test.
        File.Move(data.PathFor("desktop/file.txt"), data.PathFor("outside.txt"));
        data.Write("storage/Files/file.txt", "unrelated replacement");

        var result = await data.RestoreAsync(archive);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.False(File.Exists(data.PathFor("desktop/file.txt")));
        Assert.Equal("unrelated replacement", data.Read("storage/Files/file.txt"));
        Assert.Equal("original", data.Read("outside.txt"));
        Assert.Contains("Skipped items: 1", data.Read("DesktopPlus-last-restore.txt"));
    }

    [Fact]
    public async Task ReferenceRestore_PreservesEditsAndHandlesUnicodeAndLiteralPathCharacters()
    {
        using var data = new RestoreFixture();
        const string original = "desktop/Überblick [1] O'Brien $.txt";
        data.Write(original, "original contents");
        string archive = data.CreateReferenceArchive();
        Directory.CreateDirectory(data.PathFor("storage/Files [1]"));
        string moved = data.PathFor("storage/Files [1]/renamed.txt");
        File.Move(data.PathFor(original), moved);
        File.WriteAllText(moved, "edited contents after backup");

        var result = await data.RestoreAsync(archive);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal("edited contents after backup", data.Read(original));
        Assert.Contains("Skipped items: 0", data.Read("DesktopPlus-last-restore.txt"));
    }

    [Fact]
    public async Task ReferenceRestore_IsIndependentAndCanRestoreInEitherDirectionRepeatedly()
    {
        using var data = new RestoreFixture();
        data.Write("desktop/file.txt", "original");
        string desktopArchive = data.CreateReferenceArchive();
        Directory.CreateDirectory(data.PathFor("storage/Files"));
        File.Move(data.PathFor("desktop/file.txt"), data.PathFor("storage/Files/sorted.txt"));
        string sortedArchive = data.CreateReferenceArchive();
        string extraArchive = data.CreateReferenceArchive();
        File.Delete(extraArchive);

        var first = await data.RestoreAsync(desktopArchive);
        Assert.True(first.ExitCode == 0, first.Error);
        Assert.Equal("original", data.Read("desktop/file.txt"));
        var second = await data.RestoreAsync(sortedArchive);
        Assert.True(second.ExitCode == 0, second.Error);
        Assert.Equal("original", data.Read("storage/Files/sorted.txt"));
        var third = await data.RestoreAsync(sortedArchive);
        Assert.True(third.ExitCode == 0, third.Error);
        Assert.Equal("original", data.Read("storage/Files/sorted.txt"));
        Assert.Contains("Skipped items: 0", data.Read("DesktopPlus-last-restore.txt"));
    }

    [Fact]
    public async Task ReferenceRestore_RejectsDestinationsOutsideManagedDirectories()
    {
        using var data = new RestoreFixture();
        data.Write("settings.json", "current settings");
        string archive = data.CreateArchive(new
        {
            UsesFileReferences = true,
            FileReferences = new[] { new { Path = data.PathFor("unrelated/file.txt"), FileId = "fake" } }
        }, new Dictionary<string, string> { ["user-data/roaming/DesktopPlus_Settings.json"] = "old settings" });

        var result = await data.RestoreAsync(archive);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Invalid file reference", result.Error);
        Assert.Equal("current settings", data.Read("settings.json"));
    }

    [Fact]
    public async Task RestoreBeforeUninstall_RestoresUserDataAndPreservesInstalledFiles()
    {
        using var data = new RestoreFixture();
        data.Write("install/DesktopPlus.exe", "installed application");
        data.Write("install/unins000.dat", "uninstall data");
        data.Write("settings.json", "current settings");
        data.Write("languages/old.json", "old language");
        data.Write("storage/old.txt", "old storage");
        data.Write("desktop/folder/old.txt", "old desktop snapshot");
        string archivePath = data.CreateArchive(new
        {
            CapturedAutoSortStorage = true,
            DesktopItems = new[] { new
            {
                OriginalPath = data.PathFor("desktop/folder"),
                ArchiveRelativePath = "user-data/desktop-items/0000/folder"
            } }
        }, new Dictionary<string, string>
        {
            ["app/DesktopPlus.exe"] = "backed-up application",
            ["user-data/roaming/DesktopPlus_Settings.json"] = "restored settings",
            ["user-data/roaming/DesktopPlus/Languages/new.json"] = "restored language",
            ["user-data/local/AutoSortStorage/new.txt"] = "restored storage",
            ["user-data/desktop-items/0000/folder/restored.txt"] = "restored desktop file"
        });

        var result = await data.RestoreAsync(archivePath);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal("installed application", data.Read("install/DesktopPlus.exe"));
        Assert.Equal("uninstall data", data.Read("install/unins000.dat"));
        Assert.Equal("restored settings", data.Read("settings.json"));
        Assert.Equal("restored language", data.Read("languages/new.json"));
        Assert.False(File.Exists(data.PathFor("languages/old.json")));
        Assert.Equal("restored storage", data.Read("storage/new.txt"));
        Assert.Equal("old storage", data.Read("storage/old.txt"));
        Assert.Equal("old desktop snapshot", data.Read("desktop/folder/old.txt"));
        Assert.False(File.Exists(data.PathFor("desktop/folder/restored.txt")));
        Assert.True(File.Exists(archivePath));
        Assert.False(Directory.Exists(data.PathFor("restore-work")));
    }

    [Fact]
    public async Task RestoreBeforeUninstall_ReversesAutoSortMovesAndKeepsDesktopConflicts()
    {
        using var data = new RestoreFixture();
        data.Write("storage/folder/contents.txt", "moved folder");
        data.Write("storage/file.txt", "sorted file");
        data.Write("desktop/file.txt", "new desktop file");
        string archivePath = data.CreateArchive(new
        {
            DesktopMoves = new[]
            {
                new { SourcePath = data.PathFor("desktop/folder"), TargetPath = data.PathFor("storage/folder") },
                new { SourcePath = data.PathFor("desktop/file.txt"), TargetPath = data.PathFor("storage/file.txt") }
            }
        }, new Dictionary<string, string>
        {
            ["user-data/roaming/DesktopPlus_Settings.json"] = "settings before sort"
        });

        var result = await data.RestoreAsync(archivePath);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal("moved folder", data.Read("desktop/folder/contents.txt"));
        Assert.False(Directory.Exists(data.PathFor("storage/folder")));
        Assert.Equal("new desktop file", data.Read("desktop/file.txt"));
        Assert.Equal("sorted file", data.Read("storage/file.txt"));
        Assert.Equal("settings before sort", data.Read("settings.json"));
        Assert.True(File.Exists(archivePath));
    }

    [Theory]
    [InlineData("unrelated/file.txt", "user-data/desktop-items/0000/file.txt")]
    [InlineData("desktop/file.txt", "user-data/desktop-items/../../../app/file.txt")]
    public async Task RestoreBeforeUninstall_RejectsDesktopPathsOutsideExpectedDirectories(
        string originalPath, string archiveRelativePath)
    {
        using var data = new RestoreFixture();
        data.Write("unrelated/file.txt", "unrelated data");
        data.Write("desktop/file.txt", "desktop data");
        string archivePath = data.CreateArchive(new
        {
            DesktopItems = new[] { new
            {
                OriginalPath = data.PathFor(originalPath),
                ArchiveRelativePath = archiveRelativePath
            } }
        }, new Dictionary<string, string>
        {
            ["user-data/desktop-items/0000/file.txt"] = "snapshot",
            ["app/file.txt"] = "outside desktop snapshot"
        });

        var result = await data.RestoreAsync(archivePath);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Invalid desktop item", result.Error);
        Assert.Equal("unrelated data", data.Read("unrelated/file.txt"));
        Assert.Equal("desktop data", data.Read("desktop/file.txt"));
        Assert.True(File.Exists(archivePath));
        Assert.False(Directory.Exists(data.PathFor("restore-work")));
    }

    private sealed class RestoreFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DesktopPlus-uninstall-tests-" + Guid.NewGuid().ToString("N"));

        public RestoreFixture()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(PathFor("desktop"));
        }

        public string PathFor(string relativePath) => Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        public string Read(string relativePath) => File.ReadAllText(PathFor(relativePath));

        public void Write(string relativePath, string contents)
        {
            string path = PathFor(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public string CreateArchive(object manifest, Dictionary<string, string> entries)
        {
            string path = PathFor("backup.zip");
            using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
            entries["manifest.json"] = JsonSerializer.Serialize(manifest);
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entry.Key).Open());
                writer.Write(entry.Value);
            }
            return path;
        }

        public string CreateReferenceArchive(string kind = "manual")
        {
            string path = PathFor("DesktopPlus-backup-" + kind + "-" + Guid.NewGuid().ToString("N") + ".zip");
            MainWindow.WriteReferenceBackupArchive(path, PathFor("settings.json"), PathFor("languages"),
                new[] { PathFor("desktop") }, PathFor("storage"), kind, "test", "test");
            return path;
        }

        public async Task<(int ExitCode, string Error)> RestoreAsync(string archivePath)
        {
            string script = MainWindow.BuildBackupRestoreScript(
                new UpdateBackupInfo { ArchivePath = archivePath }, Array.Empty<string>(), restoreBeforeUninstall: true);
            Assert.DoesNotContain("Start-Process", script);
            // Redirect every destination to this fixture before executing the production restore body.
            string assignments = string.Join(Environment.NewLine, new[]
            {
                Assign("targetInstallDir", "install"), Assign("settingsPath", "settings.json"),
                Assign("targetLanguagesDir", "languages"), Assign("targetAutoSortDir", "storage"),
                Assign("pendingInfoPath", "pending.json"), Assign("restoreRoot", "restore-work"),
                Assign("extractPath", "restore-work/extract"),
                "$desktopRoots = @('" + PathFor("desktop").Replace("'", "''") + "')"
            });
            string marker = "try {" + Environment.NewLine + "  Ensure-Directory $restoreRoot";
            Assert.Contains(marker, script);
            script = script.Replace(marker, assignments + Environment.NewLine + marker);
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            string encoded = MainWindow.EncodeBackupRestoreScript(script);
            Assert.True(encoded.Length < 30_000, "Restore helper exceeds the Windows command-line limit.");
            start.ArgumentList.Add(encoded);
            using Process process = Process.Start(start)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await output;
            return (process.ExitCode, await error);
        }

        private string Assign(string variable, string relativePath) =>
            "$" + variable + " = '" + PathFor(relativePath).Replace("'", "''") + "'";

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
