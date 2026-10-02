using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace DesktopPlus;

internal sealed class FolderSearchIndexEntry
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public int Depth { get; init; }
}

/// <summary>Bounds disk work even on fast directories and on trees of empty folders.</summary>
internal sealed class SearchIndexIoThrottle
{
    private const int MaxOperationsPerSlice = 64;
    private static readonly TimeSpan ActiveSlice = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(40);
    private long _sliceStarted = Stopwatch.GetTimestamp();
    private int _operations;

    internal async ValueTask WaitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (++_operations < MaxOperationsPerSlice &&
            Stopwatch.GetElapsedTime(_sliceStarted) < ActiveSlice)
        {
            return;
        }

        await Task.Delay(Pause, token).ConfigureAwait(false);
        _operations = 0;
        _sliceStarted = Stopwatch.GetTimestamp();
    }
}

internal static class FolderSearchTraversal
{
    internal static async IAsyncEnumerable<FolderSearchIndexEntry> EnumerateAsync(
        string root,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var entry in EnumerateWithCheckpointsAsync(root, token).ConfigureAwait(false))
            if (!string.IsNullOrEmpty(entry.Path)) yield return entry;
    }

    internal static async IAsyncEnumerable<FolderSearchIndexEntry> EnumerateWithCheckpointsAsync(
        string root, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool skipRoot;
        try { skipRoot = (File.GetAttributes(root) & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0; }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        if (skipRoot) yield break;
        var pendingDirectories = new Queue<(string Path, int Depth)>();
        pendingDirectories.Enqueue((root, 0));
        var throttle = new SearchIndexIoThrottle();
        var checkpoint = Stopwatch.StartNew();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        while (pendingDirectories.Count > 0)
        {
            await throttle.WaitAsync(token).ConfigureAwait(false);
            if (checkpoint.Elapsed >= TimeSpan.FromMilliseconds(800))
            {
                // Empty directories also give the central worker a chance to switch jobs.
                checkpoint.Restart();
                yield return new FolderSearchIndexEntry();
            }
            var (currentDirectory, depth) = pendingDirectories.Dequeue();
            IEnumerator<FileSystemInfo> children;
            try
            {
                // Reuse enumeration metadata instead of traversing twice and then
                // reading attributes separately for every file and directory.
                children = new DirectoryInfo(currentDirectory)
                    .EnumerateFileSystemInfos("*", options).GetEnumerator();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            using (children)
            {
                while (true)
                {
                    await throttle.WaitAsync(token).ConfigureAwait(false);
                    FileSystemInfo child;
                    try
                    {
                        if (!children.MoveNext()) break;
                        child = children.Current;
                    }
                    catch (IOException) { break; }
                    catch (UnauthorizedAccessException) { break; }

                    bool isDirectory = child is DirectoryInfo;
                    if (isDirectory)
                    {
                        pendingDirectories.Enqueue((child.FullName, depth + 1));
                    }

                    yield return new FolderSearchIndexEntry
                    {
                        Path = child.FullName,
                        Name = child.Name,
                        RelativePath = Path.GetRelativePath(root, child.FullName),
                        IsDirectory = isDirectory,
                        Depth = depth + 1
                    };
                }
            }
        }
    }
}
