using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace DesktopPlus.Tests;

public sealed class FolderSearchIndexTests
{
    [Fact]
    public async Task TraversalStaysInsideSelectedFolderAndPreservesNestedSearchPaths()
    {
        using var folder = new TempDir();
        string root = folder.Dir("selected");
        string nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        string file = Path.Combine(nested, "report.txt");
        File.WriteAllText(file, "report");
        folder.File("outside.txt");
        string hidden = Path.Combine(root, "hidden.txt");
        File.WriteAllText(hidden, "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        string systemDirectory = Directory.CreateDirectory(Path.Combine(root, "protected")).FullName;
        File.WriteAllText(Path.Combine(systemDirectory, "excluded.txt"), "excluded");
        File.SetAttributes(systemDirectory, FileAttributes.System);

        try
        {
            var entries = new List<FolderSearchIndexEntry>();
            await foreach (var entry in FolderSearchTraversal.EnumerateAsync(root, CancellationToken.None))
            {
                entries.Add(entry);
            }

            Assert.Equal(3, entries.Count);
            Assert.Contains(entries, entry => entry.Path == nested && entry.IsDirectory && entry.Depth == 1);
            var nestedFile = Assert.Single(entries, entry => entry.Path == file);
            Assert.Equal(Path.Combine("nested", "report.txt"), nestedFile.RelativePath);
            Assert.Equal(2, nestedFile.Depth);
            Assert.Contains(entries, entry => entry.Path == hidden);
            Assert.DoesNotContain(entries, entry => entry.Path.StartsWith(systemDirectory, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.SetAttributes(systemDirectory, FileAttributes.Directory);
            File.SetAttributes(hidden, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task TraversalPausesEvenForEmptyDirectoryTrees()
    {
        using var folder = new TempDir();
        for (int i = 0; i < 70; i++) folder.Dir($"empty-{i}");

        var elapsed = Stopwatch.StartNew();
        int count = 0;
        await foreach (var _ in FolderSearchTraversal.EnumerateAsync(folder.Path, CancellationToken.None)) count++;

        Assert.Equal(70, count);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(40), "Empty folders must also yield disk time.");
    }

    [Fact]
    public async Task TraversalStopsWhenCancelledWithoutReturningPartialSuccess()
    {
        using var folder = new TempDir();
        folder.File("one.txt");
        folder.File("two.txt");
        using var cts = new CancellationTokenSource();
        await using var entries = FolderSearchTraversal.EnumerateAsync(folder.Path, cts.Token).GetAsyncEnumerator();

        Assert.True(await entries.MoveNextAsync());
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await entries.MoveNextAsync());
    }

}
