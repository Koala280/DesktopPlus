using System;
using System.Linq;
using DesktopPlus.Companion;
using Xunit;

namespace DesktopPlus.Tests;

public class CompanionFileServiceTests
{
    [Fact]
    public void ListDirectory_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(CompanionFileService.ListDirectory(null));
        Assert.Null(CompanionFileService.ListDirectory("   "));
    }

    [Fact]
    public void ListDirectory_Missing_ReturnsNull()
    {
        Assert.Null(CompanionFileService.ListDirectory(@"C:\nope_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void ListDirectory_OrdersDirectoriesFirstThenAlphabetically()
    {
        using var t = new TempDir();
        t.File("banana.txt");
        t.File("apple.txt");
        t.Dir("zeta");
        t.Dir("alpha");

        var listing = CompanionFileService.ListDirectory(t.Path);

        Assert.NotNull(listing);
        Assert.Equal(t.Path, listing!.Path);
        Assert.Equal(new[] { "alpha", "zeta", "apple.txt", "banana.txt" },
                     listing.Entries.Select(e => e.Name).ToArray());
        Assert.True(listing.Entries[0].IsDir);
        Assert.False(listing.Entries[^1].IsDir);
    }

    [Fact]
    public void ListDirectory_ReportsSizeAndLowercaseExtension()
    {
        using var t = new TempDir();
        t.File("data.TXT", "hello");   // 5 bytes

        var listing = CompanionFileService.ListDirectory(t.Path);
        var entry = listing!.Entries.Single();

        Assert.False(entry.IsDir);
        Assert.Equal(5, entry.Size);
        Assert.Equal("txt", entry.Ext);
    }

    [Fact]
    public void ListDirectory_SetsParentOfSubfolder()
    {
        using var t = new TempDir();
        string sub = t.Dir("child");

        var listing = CompanionFileService.ListDirectory(sub);

        Assert.NotNull(listing);
        Assert.Equal(t.Path, listing!.Parent);
    }

    [Fact]
    public void GetDrives_IncludesReadySystemDrive()
    {
        var drives = CompanionFileService.GetDrives();

        Assert.NotEmpty(drives);
        Assert.Contains(drives, d => d.Ready && d.Path.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase));
    }
}
