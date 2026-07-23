using System;
using System.Linq;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class FolderListingCacheTests
{
    [Fact]
    public void StoreAndInvalidate_ControlsCachedSnapshot()
    {
        using var folder = new TempDir();
        string[] paths =
        {
            System.IO.Path.Combine(folder.Path, "one.png"),
            System.IO.Path.Combine(folder.Path, "two.png")
        };

        FolderListingCache.Store(folder.Path, paths);

        Assert.True(FolderListingCache.TryGet(folder.Path, out var cached));
        Assert.Equal(paths, cached);

        FolderListingCache.Invalidate(folder.Path);
        Assert.False(FolderListingCache.TryGet(folder.Path, out _));
    }

    [Fact]
    public void Store_DoesNotCacheOversizedFolderListing()
    {
        using var folder = new TempDir();
        string[] paths = Enumerable.Range(0, 20_001)
            .Select(index => System.IO.Path.Combine(folder.Path, $"{index:D5}.png"))
            .ToArray();

        FolderListingCache.Store(folder.Path, paths);

        Assert.False(FolderListingCache.TryGet(folder.Path, out _));
    }

    [Fact]
    public void ApplyFileSystemChange_UpdatesOnlyChangedEntries()
    {
        using var folder = new TempDir();
        string keptPath = folder.File("keep.png");
        string removedPath = folder.File("remove.png");
        FolderListingCache.Store(folder.Path, new[] { keptPath, removedPath });

        System.IO.File.Delete(removedPath);
        string addedPath = folder.File("added.png");
        Assert.True(FolderListingCache.ApplyFileSystemChange(
            folder.Path,
            removedPath,
            addedPath));

        Assert.True(FolderListingCache.TryGet(folder.Path, out var cached));
        Assert.Contains(keptPath, cached);
        Assert.Contains(addedPath, cached);
        Assert.DoesNotContain(removedPath, cached);
    }

    [Fact]
    public void ApplyFileSystemChange_RenamesCachedEntry()
    {
        using var folder = new TempDir();
        string oldPath = folder.File("old.png");
        FolderListingCache.Store(folder.Path, new[] { oldPath });

        string newPath = System.IO.Path.Combine(folder.Path, "new.png");
        System.IO.File.Move(oldPath, newPath);
        Assert.True(FolderListingCache.ApplyFileSystemChange(
            folder.Path,
            oldPath,
            newPath));

        Assert.True(FolderListingCache.TryGet(folder.Path, out var cached));
        Assert.Equal(new[] { newPath }, cached);
    }
}
