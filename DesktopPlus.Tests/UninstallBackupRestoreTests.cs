using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class UninstallBackupRestoreTests
{
    [Fact]
    public void UninstallSafetyBackup_IncludesDesktopItemsThatWillBeOverwritten()
    {
        using var data = new RestoreFixture();
        data.Write("desktop/file.txt", "current desktop contents");
        string archivePath = data.CreateArchive(new
        {
            DesktopItems = new[] { new
            {
                OriginalPath = data.PathFor("desktop/file.txt"),
                ArchiveRelativePath = "user-data/desktop-items/0000/file.txt"
            } }
        }, new Dictionary<string, string> { ["user-data/desktop-items/0000/file.txt"] = "old contents" });

        Assert.Equal(new[] { data.PathFor("desktop/file.txt") },
            MainWindow.ReadDesktopItemsForUninstallSafety(archivePath, new[] { data.PathFor("desktop") }));
    }

    [Fact]
    public void UninstallSafetyBackup_RejectsDesktopItemsOutsideDesktop()
    {
        using var data = new RestoreFixture();
        string archivePath = data.CreateArchive(new
        {
            DesktopItems = new[] { new { OriginalPath = data.PathFor("unrelated/file.txt") } }
        }, new Dictionary<string, string>());

        Assert.Throws<InvalidDataException>(() => MainWindow.ReadDesktopItemsForUninstallSafety(
            archivePath, new[] { data.PathFor("desktop") }));
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
        Assert.False(File.Exists(data.PathFor("storage/old.txt")));
        Assert.Equal("restored desktop file", data.Read("desktop/folder/restored.txt"));
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
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
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
