using System.Diagnostics;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;
using Xunit.Abstractions;

namespace DesktopPlus.Tests;

[Collection("Search indexing")]
public sealed class FolderSearchIndexServiceTests
{
    private readonly ITestOutputHelper _output;
    public FolderSearchIndexServiceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PreparesInactiveRootsAndSharesDuplicateFoldersWithoutSearch()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string active = data.Dir("active");
        string inactive = data.Dir("inactive");
        File.WriteAllText(Path.Combine(inactive, "ready.txt"), "ready");
        using var service = NewService(cache.Path);

        service.Configure(new[] { inactive, active, inactive.ToUpperInvariant() }, new[] { active });
        await WaitFor(() => service.GetStatus(inactive).Writes == 1 && service.GetStatus(active).Writes == 1);

        Assert.Equal(1, service.GetStatus(inactive).Scans);
        Assert.Equal(1, service.GetStatus(active).Scans);
        Assert.Contains(await Read(service, inactive), entry => entry.Name == "ready.txt");
    }

    [Fact]
    public async Task CachedResultsRemainAvailableDuringBackgroundRefresh()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string file = data.File("needle.txt");
        using (var initial = NewService(cache.Path))
        {
            initial.Configure(new[] { data.Path });
            await WaitFor(() => initial.GetStatus(data.Path).Writes == 1);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<FolderSearchIndexEntry> Blocked(string root, [EnumeratorCancellation] CancellationToken token)
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(token);
            await foreach (var entry in FolderSearchTraversal.EnumerateAsync(root, token)) yield return entry;
        }
        using var refreshed = NewService(cache.Path, traverse: Blocked);
        refreshed.Configure(new[] { data.Path });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(refreshed.GetStatus(data.Path).IsRefreshing);
            Assert.Contains(await Read(refreshed, data.Path), entry => entry.Path == file);
            Assert.Equal(1, refreshed.GetStatus(data.Path).Loads);
        }
        finally { resume.TrySetResult(); }
        await WaitFor(() => !refreshed.GetStatus(data.Path).IsRefreshing);
    }

    [Fact]
    public async Task ChangesDuringInitialBuildAreReplayedWithoutLosingDeletesOrRenames()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string deleted = data.File("deleted.txt");
        string renamed = data.File("old.txt");
        string newName = data.Combine("renamed.txt");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<FolderSearchIndexEntry> Blocked(string root, [EnumeratorCancellation] CancellationToken token)
        {
            yield return Entry(root, deleted);
            yield return Entry(root, renamed);
            entered.TrySetResult();
            await resume.Task.WaitAsync(token);
        }
        using var service = NewService(cache.Path, traverse: Blocked);
        service.Configure(new[] { data.Path });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.Delete(deleted);
        File.Move(renamed, newName);
        string created = data.File("created.txt");
        service.NotifyChange(data.Path, SearchIndexChangeKind.Deleted, deleted);
        service.NotifyChange(data.Path, SearchIndexChangeKind.Renamed, newName, renamed);
        service.NotifyChange(data.Path, SearchIndexChangeKind.Created, created);
        resume.TrySetResult();
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);

        var found = await Read(service, data.Path);
        Assert.Contains(found, entry => entry.Path == newName);
        Assert.Contains(found, entry => entry.Path == created);
        Assert.DoesNotContain(found, entry => entry.Path == deleted || entry.Path == renamed);
        Assert.Equal(1, service.GetStatus(data.Path).Scans);
        using var reloaded = NewService(cache.Path, scanDelay: TimeSpan.FromDays(1));
        reloaded.Configure(new[] { data.Path });
        await WaitFor(() => reloaded.GetStatus(data.Path).Loads == 1);
        Assert.Equal(found.Select(entry => entry.Path).Order(), (await Read(reloaded, data.Path)).Select(entry => entry.Path).Order());
    }

    [Fact]
    public async Task NewSubtreesAreIndexedWithoutRescanningTheRoot()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        data.File("original.txt");
        using var service = NewService(cache.Path);
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);
        string directory = data.Dir("arriving");
        string child = Path.Combine(directory, "nested.txt");
        File.WriteAllText(child, "nested");
        service.NotifyChange(data.Path, SearchIndexChangeKind.Created, directory);
        await WaitFor(() => service.GetStatus(data.Path).Writes == 2);

        Assert.Equal(1, service.GetStatus(data.Path).Scans);
        Assert.Contains(await Read(service, data.Path), entry => entry.Path == child && entry.Depth == 2);
    }

    [Fact]
    public async Task DirectoryRenamedDuringBuildIncludesChildrenTheOldTraversalDidNotReach()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string old = data.Dir("old-folder"), renamed = data.Combine("renamed-folder");
        File.WriteAllText(Path.Combine(old, "unvisited.txt"), "x");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<FolderSearchIndexEntry> Traverse(string root, [EnumeratorCancellation] CancellationToken token)
        {
            if (root == data.Path)
            {
                yield return new FolderSearchIndexEntry { Path = old, Name = "old-folder", RelativePath = "old-folder",
                    Depth = 1, IsDirectory = true };
                entered.SetResult();
                await resume.Task.WaitAsync(token);
                yield break; // The queued old path disappears before the traversal opens it.
            }
            await foreach (var entry in FolderSearchTraversal.EnumerateAsync(root, token)) yield return entry;
        }
        using var service = NewService(cache.Path, traverse: Traverse);
        service.Configure(new[] { data.Path });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Directory.Move(old, renamed);
        service.NotifyChange(data.Path, SearchIndexChangeKind.Renamed, renamed, old);
        resume.SetResult();
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);
        var found = await Read(service, data.Path);
        Assert.Equal(2, found.Count);
        Assert.Single(found, entry => entry.Path == Path.Combine(renamed, "unvisited.txt") && entry.Depth == 2);
        Assert.DoesNotContain(found, entry => entry.Path.StartsWith(old, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, service.GetStatus(data.Path).Scans);
    }

    [Fact]
    public async Task ContentChangesDoNotRebuildOrRewriteTheNameIndex()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string file = data.File("document.txt");
        var queue = new SearchIndexWorkQueue();
        using var service = NewService(cache.Path, queue);
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);
        File.WriteAllText(file, "different contents");
        service.NotifyChange(data.Path, SearchIndexChangeKind.Changed, file);
        await queue.RunAsync(() => Task.CompletedTask, priority: int.MaxValue);

        Assert.Equal(1, service.GetStatus(data.Path).Scans);
        Assert.Equal(1, service.GetStatus(data.Path).Writes);
    }

    [Fact]
    public async Task LargeCompleteIndexesStaySearchableWithinMemoryBudgetWithoutAnotherTraversal()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        for (int i = 0; i < 200; i++) data.File($"{i:D4}.txt");
        int traversals = 0;
        async IAsyncEnumerable<FolderSearchIndexEntry> Traverse(string root, [EnumeratorCancellation] CancellationToken token)
        {
            Interlocked.Increment(ref traversals);
            await foreach (var entry in FolderSearchTraversal.EnumerateAsync(root, token)) yield return entry;
        }
        using var service = NewService(cache.Path, memoryBudget: 1024, memoryPerRoot: 512, traverse: Traverse);
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);
        var found = await Read(service, data.Path);

        Assert.Equal(200, found.Count);
        Assert.Contains(found, entry => entry.Name == "0199.txt");
        Assert.True(service.GetStatus(data.Path).MemoryBytes <= 512);
        Assert.Equal(1, traversals);
        Assert.Equal(200, (await Read(service, data.Path)).Count);
        Assert.Equal(1, traversals);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadsExistingCacheVersionsBeforeStartingTraversal(int version)
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string file = data.File("legacy.txt");
        var store = new FolderSearchIndexStore(cache.Path);
        using (var stream = File.Create(store.CachePath(data.Path)))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(FolderSearchIndexStore.Magic);
            writer.Write(version);
            writer.Write(data.Path);
            writer.Write(DateTime.UtcNow.AddMinutes(-1).Ticks);
            if (version >= 3) writer.Write(Directory.GetLastWriteTimeUtc(data.Path).Ticks);
            writer.Write(1);
            writer.Write(file);
            writer.Write("legacy.txt");
            writer.Write(false);
            writer.Write(1);
            if (version >= 2) writer.Write("legacy.txt");
        }
        using var service = NewService(cache.Path, scanDelay: TimeSpan.FromDays(1));
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Loads == 1);

        Assert.Equal(file, Assert.Single(await Read(service, data.Path)).Path);
        Assert.Equal(0, service.GetStatus(data.Path).Scans);
    }

    [Fact]
    public async Task DiskBackedChangesHandleRenamesDeletesAndFilesMovedIntoTheRoot()
    {
        using var data = new TempDir();
        using var outside = new TempDir();
        using var cache = new TempDir();
        for (int i = 0; i < 200; i++) data.File($"file-{i:D4}.txt");
        using var service = NewService(cache.Path, memoryBudget: 16384, memoryPerRoot: 8192,
            persistInterval: TimeSpan.FromMilliseconds(300));
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);
        string old = data.Combine("file-0199.txt"), renamed = data.Combine("renamed.txt");
        File.Move(old, renamed);
        service.NotifyChange(data.Path, SearchIndexChangeKind.Renamed, renamed, old);
        string removed = data.Combine("file-0198.txt");
        File.Delete(removed);
        service.NotifyChange(data.Path, SearchIndexChangeKind.Deleted, removed);
        string incoming = outside.File("incoming.txt"), moved = data.Combine("incoming.txt");
        File.Move(incoming, moved);
        service.NotifyChange(data.Path, SearchIndexChangeKind.Renamed, moved, incoming);
        await WaitFor(() => service.GetStatus(data.Path).Writes == 2);
        var found = await Read(service, data.Path);
        Assert.Equal(200, found.Count);
        Assert.Single(found, entry => entry.Path == renamed);
        Assert.Single(found, entry => entry.Path == moved);
        Assert.DoesNotContain(found, entry => entry.Path == old || entry.Path == removed);
        Assert.True(service.GetStatus(data.Path).MemoryBytes <= 8192);
        Assert.Equal(1, service.GetStatus(data.Path).Scans);
    }

    [Fact]
    public async Task FullRefreshRemovesMissedDeletionsFromTheMemoryIndex()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string deleted = data.File("gone.txt");
        using var service = NewService(cache.Path, persistInterval: TimeSpan.FromDays(1));
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1 && service.GetDirectChildren(data.Path)?.Count == 1);
        File.Delete(deleted); // Simulate a missed event rather than notifying the deletion.
        service.RequestRefresh(data.Path);
        await WaitFor(() => service.GetStatus(data.Path).Scans == 2 && !service.GetStatus(data.Path).IsRefreshing);
        Assert.Empty(await Read(service, data.Path));
        Assert.Equal(1, service.GetStatus(data.Path).Writes); // Fresh results precede the next checkpoint.
    }

    [Fact]
    public async Task NativeWatcherUpdatesInactiveRootsAndRecoversAfterRootRecreation()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string root = data.Dir("inactive");
        using var service = new FolderSearchIndexService(cache.Path, scanDelay: TimeSpan.Zero,
            persistInterval: TimeSpan.FromDays(1), refreshDelay: TimeSpan.FromMilliseconds(100));
        service.Configure(new[] { root });
        await WaitFor(() => service.GetStatus(root).Writes == 1);
        string created = Path.Combine(root, "observed.txt");
        File.WriteAllText(created, "x");
        await WaitFor(() => service.GetDirectChildren(root)?.Contains(created) == true);
        string renamed = Path.Combine(root, "renamed.txt");
        File.Move(created, renamed);
        await WaitFor(() => service.GetDirectChildren(root)?.Contains(renamed) == true);
        Assert.Equal(1, service.GetStatus(root).Scans);
        Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        await WaitFor(() => service.GetStatus(root).Scans == 2 && !service.GetStatus(root).IsRefreshing);
        string afterRecreation = Path.Combine(root, "after.txt");
        File.WriteAllText(afterRecreation, "x");
        await WaitFor(() => service.GetDirectChildren(root)?.Contains(afterRecreation) == true);
        Assert.DoesNotContain(await Read(service, root), entry => entry.Path == renamed);
    }

    [Fact]
    public async Task InitialBuildPublishesSearchablePartialResultsWithinOneSecond()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        async IAsyncEnumerable<FolderSearchIndexEntry> Slow(string root, [EnumeratorCancellation] CancellationToken token)
        {
            for (int i = 0; i < 20; i++)
            {
                yield return Entry(root, Path.Combine(root, $"partial-{i}.txt"));
                await Task.Delay(70, token);
            }
        }
        using var service = NewService(cache.Path, traverse: Slow);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.IndexChanged += root =>
        {
            if (service.GetStatus(root).EntryCount > 0) published.TrySetResult();
        };
        service.Configure(new[] { data.Path });
        await published.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(service.GetStatus(data.Path).IsPreparing);
        Assert.Contains(await Read(service, data.Path), entry => entry.Name == "partial-0.txt");
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);
    }

    [Fact]
    public async Task PanelDisplaysCachedNestedMatchesWithoutStartingAnotherTraversal()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string nested = data.Dir("nested");
        string wanted = Path.Combine(nested, "needle.txt");
        File.WriteAllText(wanted, "x");
        using var service = NewService(cache.Path);
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1 && service.GetDirectChildren(data.Path) != null);
        RunSta(async () =>
        {
            var panel = new DesktopPanel { IsPreviewPanel = true, SearchIndexService = service,
                PanelType = PanelKind.Folder, currentFolderPath = data.Path };
            try
            {
                var injected = (HashSet<string>)typeof(DesktopPanel).GetField("_searchInjectedPaths",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                Task<List<string>> cached;
                // Block index access to prove even a RAM lookup returns control to the UI thread.
                lock (GetField<object>(service, "_sync"))
                {
                    cached = (Task<List<string>>)Invoke(panel, "GetIndexedMatchesAsync", data.Path, "needle",
                        CancellationToken.None)!;
                    Assert.False(cached.IsCompleted);
                }
                Assert.Contains(wanted, await cached);
                var elapsed = Stopwatch.StartNew();
                Invoke(panel, "BeginSearch", "needle");
                await WaitFor(() => injected.Contains(wanted));
                elapsed.Stop();
                _output.WriteLine($"Panel result visible after {elapsed.Elapsed.TotalMilliseconds:F2} ms.");
                Assert.Equal(1, service.GetStatus(data.Path).Scans);
            }
            finally { panel.Close(); }
        });
    }

    [Fact]
    public async Task IndexRefreshPreservesUnchangedResultsVisibilityAndSelection()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string localPath = data.File("needle-local.txt");
        string nested = data.Dir("nested");
        string keptPath = Path.Combine(nested, "needle-kept.txt");
        string removedPath = Path.Combine(nested, "needle-removed.txt");
        File.WriteAllText(keptPath, "x");
        File.WriteAllText(removedPath, "x");
        using var service = NewService(cache.Path);
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1 && service.GetDirectChildren(data.Path) != null);

        RunSta(async () =>
        {
            var panel = new DesktopPanel { IsPreviewPanel = true, SearchIndexService = service,
                PanelType = PanelKind.Folder, currentFolderPath = data.Path };
            try
            {
                var list = (ListBox)panel.FindName("FileList");
                var search = (TextBox)panel.FindName("SearchBox");
                var local = new ListBoxItem { Tag = localPath, Content = "needle-local" };
                var other = new ListBoxItem { Tag = data.File("other.txt"), Content = "other" };
                list.Items.Add(local);
                list.Items.Add(other);
                GetField<HashSet<string>>(panel, "_baseItemPaths").UnionWith(new[] { localPath, (string)other.Tag });
                search.Text = "needle";
                await WaitForSearch(panel);
                var kept = Assert.Single(list.Items.OfType<ListBoxItem>(), item => Equals(item.Tag, keptPath));
                kept.IsSelected = true;
                Assert.Equal(Visibility.Visible, local.Visibility);
                Assert.Equal(Visibility.Collapsed, other.Visibility);

                int collectionChanges = 0, visibilityChanges = 0;
                ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) => collectionChanges++;
                var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(ListBoxItem))!;
                EventHandler visibilityChanged = (_, _) => visibilityChanges++;
                descriptor.AddValueChanged(local, visibilityChanged);
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Invoke(panel, "RefreshActiveSearch");
                        await WaitForSearch(panel);
                    }
                    string unrelated = data.File("unrelated.txt");
                    service.NotifyChange(data.Path, SearchIndexChangeKind.Created, unrelated);
                    await WaitFor(() => service.GetDirectChildren(data.Path)?.Contains(unrelated) == true);
                    Invoke(panel, "RefreshActiveSearch");
                    await WaitForSearch(panel);

                    Assert.Equal(0, collectionChanges);
                    Assert.Equal(0, visibilityChanges);
                    Assert.True(kept.IsSelected);
                    Assert.Same(kept, Assert.Single(list.Items.OfType<ListBoxItem>(), item => Equals(item.Tag, keptPath)));

                    File.Delete(removedPath);
                    service.NotifyChange(data.Path, SearchIndexChangeKind.Deleted, removedPath);
                    string addedPath = Path.Combine(nested, "needle-znew.txt");
                    File.WriteAllText(addedPath, "x");
                    service.NotifyChange(data.Path, SearchIndexChangeKind.Created, addedPath);
                    await WaitFor(() => service.GetStatus(data.Path).Writes >= 2);
                    await WaitFor(async () => (await Read(service, data.Path)).Any(entry => entry.Path == addedPath));
                    collectionChanges = 0;
                    Invoke(panel, "RefreshActiveSearch");
                    await WaitForSearch(panel);

                    Assert.Equal(2, collectionChanges); // Only one removal and one addition.
                    Assert.DoesNotContain(list.Items.OfType<ListBoxItem>(), item => Equals(item.Tag, removedPath));
                    Assert.Contains(list.Items.OfType<ListBoxItem>(), item => Equals(item.Tag, addedPath));
                    Assert.True(kept.IsSelected);
                    Assert.Equal(0, visibilityChanges);

                    string renamedPath = Path.Combine(nested, "NEEDLE-KEPT.txt");
                    File.Move(keptPath, renamedPath);
                    service.NotifyChange(data.Path, SearchIndexChangeKind.Renamed, renamedPath, keptPath);
                    await WaitFor(async () => (await Read(service, data.Path)).Any(entry =>
                        string.Equals(entry.Path, renamedPath, StringComparison.Ordinal)));
                    collectionChanges = 0;
                    Invoke(panel, "RefreshActiveSearch");
                    await WaitForSearch(panel);
                    Assert.Equal(renamedPath, kept.Tag);
                    Assert.StartsWith("NEEDLE-KEPT", (string)Invoke(panel, "GetSearchCandidateText", kept)!);
                    Assert.True(kept.IsSelected);
                    Assert.Equal(0, collectionChanges);

                    File.Delete(renamedPath);
                    File.Delete(addedPath);
                    service.NotifyChange(data.Path, SearchIndexChangeKind.Deleted, renamedPath);
                    service.NotifyChange(data.Path, SearchIndexChangeKind.Deleted, addedPath);
                    await WaitFor(async () => !(await Read(service, data.Path)).Any(entry =>
                        entry.Path == keptPath || entry.Path == addedPath));
                    Invoke(panel, "RefreshActiveSearch");
                    await WaitForSearch(panel);
                    Assert.Empty(GetField<HashSet<string>>(panel, "_searchInjectedPaths"));
                    Assert.Equal(Visibility.Visible, local.Visibility);
                }
                finally { descriptor.RemoveValueChanged(local, visibilityChanged); }
            }
            finally { panel.Close(); }
        });
    }

    [Fact]
    public async Task IndexNotificationsDoNotCancelCurrentSearchAndRapidEditsDiscardOldResults()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        string nested = data.Dir("nested");
        File.WriteAllText(Path.Combine(nested, "needle.txt"), "x");
        using var service = NewService(cache.Path);
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1 && service.GetDirectChildren(data.Path) != null);
        RunSta(async () =>
        {
            var panel = new DesktopPanel { IsPreviewPanel = true, SearchIndexService = service,
                PanelType = PanelKind.Folder, currentFolderPath = data.Path };
            try
            {
                var search = (TextBox)panel.FindName("SearchBox");
                search.Text = "needle";
                var current = GetField<CancellationTokenSource>(panel, "_searchCts");
                for (int i = 0; i < 10; i++) Invoke(panel, "RefreshActiveSearch");
                Assert.Same(current, GetField<CancellationTokenSource>(panel, "_searchCts"));
                Assert.False(current.IsCancellationRequested);
                await WaitForSearch(panel);
                Assert.Single(GetField<HashSet<string>>(panel, "_searchInjectedPaths"));

                search.Text = "absent";
                await WaitForSearch(panel);
                Assert.Empty(GetField<HashSet<string>>(panel, "_searchInjectedPaths"));
                search.Text = "absent";
                search.Text = "needle";
                search.Clear();
                await WaitForSearch(panel);
                Assert.Empty(GetField<HashSet<string>>(panel, "_searchInjectedPaths"));
                Assert.False(GetField<bool>(panel, "_searchRefreshPending"));
            }
            finally { panel.Close(); }
        });
    }

    [Fact]
    public async Task RemovedQueuedRootsNeverStartWorkAndQueueSerializesJobs()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        var queue = new SearchIndexWorkQueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = queue.RunAsync(async () => { entered.SetResult(); await resume.Task; });
        await entered.Task;
        int traversals = 0;
        async IAsyncEnumerable<FolderSearchIndexEntry> Traverse(string root, [EnumeratorCancellation] CancellationToken token)
        {
            traversals++;
            await foreach (var entry in FolderSearchTraversal.EnumerateAsync(root, token)) yield return entry;
        }
        using var service = NewService(cache.Path, queue, traverse: Traverse);
        service.Configure(new[] { data.Path });
        service.Configure(Array.Empty<string>());
        resume.SetResult();
        await blocker;
        await queue.RunAsync(() => Task.CompletedTask, priority: int.MaxValue);
        Assert.False(service.Contains(data.Path));
        Assert.Equal(0, traversals);

        int active = 0, maxActive = 0;
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => queue.RunAsync(async () =>
        {
            maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
            await Task.Yield();
            Interlocked.Decrement(ref active);
        })));
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task ClearingSearchOrClosingPanelDoesNotCancelConfiguredFolderPreparation()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        async IAsyncEnumerable<FolderSearchIndexEntry> Traverse(string root, [EnumeratorCancellation] CancellationToken token)
        {
            observed = token;
            entered.SetResult();
            await resume.Task.WaitAsync(token);
            await foreach (var entry in FolderSearchTraversal.EnumerateAsync(root, token)) yield return entry;
        }
        using var service = NewService(cache.Path, traverse: Traverse);
        service.Configure(new[] { data.Path });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            RunSta(async () =>
            {
                var panel = new DesktopPanel { IsPreviewPanel = true, SearchIndexService = service };
                Invoke(panel, "BeginSearch", "");
                panel.Close();
                Assert.False(observed.IsCancellationRequested);
                await Task.CompletedTask;
            });
        }
        finally { resume.TrySetResult(); }
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);
    }

    [Fact]
    public async Task ApplicationShutdownCancelsActiveJobsAndRemovesTheirTemporaryFiles()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        var queue = new SearchIndexWorkQueue();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<FolderSearchIndexEntry> Blocked(string root, [EnumeratorCancellation] CancellationToken token)
        {
            entered.SetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }
        var service = NewService(cache.Path, queue, traverse: Blocked);
        service.Configure(new[] { data.Path });
        CancellationToken active = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        Assert.True(active.IsCancellationRequested);
        await queue.RunAsync(() => Task.CompletedTask, priority: int.MaxValue);
        Assert.Empty(Directory.EnumerateFiles(cache.Path));
    }

    [Fact]
    public async Task IndexWritesAreCoalescedAndRespectTheMinimumInterval()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        using var service = NewService(cache.Path, persistInterval: TimeSpan.FromMilliseconds(300));
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);
        var elapsed = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            string file = data.File($"new-{i}.txt");
            service.NotifyChange(data.Path, SearchIndexChangeKind.Created, file);
        }
        await WaitFor(() => service.GetStatus(data.Path).Writes == 2);

        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(270));
        Assert.Equal(5, (await Read(service, data.Path)).Count);
        Assert.Equal(2, service.GetStatus(data.Path).Writes);
    }

    [Fact]
    public async Task LostEventsTriggerOneRefreshAfterTheLastNotification()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        var starts = new List<long>();
        async IAsyncEnumerable<FolderSearchIndexEntry> Traverse(string root, [EnumeratorCancellation] CancellationToken token)
        {
            lock (starts) starts.Add(Stopwatch.GetTimestamp());
            await foreach (var entry in FolderSearchTraversal.EnumerateAsync(root, token)) yield return entry;
        }
        using var service = NewService(cache.Path, refreshDelay: TimeSpan.FromMilliseconds(100), traverse: Traverse);
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1);
        service.RequestRefresh(data.Path);
        await Task.Delay(35);
        service.RequestRefresh(data.Path);
        await Task.Delay(35);
        string file = data.File("arrived-during-recovery.txt");
        service.NotifyChange(data.Path, SearchIndexChangeKind.Created, file);
        long lastNotification = Stopwatch.GetTimestamp();
        await WaitFor(() => service.GetStatus(data.Path).Writes == 2);

        Assert.Equal(2, starts.Count);
        Assert.True(Stopwatch.GetElapsedTime(lastNotification, starts[1]) >= TimeSpan.FromMilliseconds(95));
    }

    [Fact]
    public async Task PreparationAndCachedLookupBenchmark()
    {
        using var data = new TempDir();
        using var cache = new TempDir();
        int files = int.TryParse(Environment.GetEnvironmentVariable("DESKTOPPLUS_INDEX_BENCHMARK_FILES"), out int configured)
            ? Math.Clamp(configured, 2048, 100000) : 2048;
        int folders = files / 4;
        for (int i = 0; i < files; i++) data.File($"report-{i:D5}.txt");
        for (int i = 0; i < folders; i++) data.Dir($"empty-{i:D5}");
        using var service = NewService(cache.Path);
        _output.WriteLine($"Index preparation started {DateTime.UtcNow:O}.");
        var build = Stopwatch.StartNew();
        service.Configure(new[] { data.Path });
        await WaitFor(() => service.GetStatus(data.Path).Writes == 1, TimeSpan.FromMinutes(3));
        build.Stop();
        _output.WriteLine($"Index preparation finished {DateTime.UtcNow:O}.");
        // Wait for cache hydration, then measure a lookup independently of initial setup.
        await WaitFor(() => service.GetDirectChildren(data.Path)?.Count == files + folders);
        var lookup = Stopwatch.StartNew();
        var entries = await Read(service, data.Path);
        var matches = entries.Where(entry => entry.Name.Contains("report-020", StringComparison.OrdinalIgnoreCase)).ToArray();
        lookup.Stop();

        _output.WriteLine($"Prepared {entries.Count} entries in {build.Elapsed.TotalMilliseconds:F0} ms; " +
            $"cached lookup {lookup.Elapsed.TotalMilliseconds:F2} ms; matches {matches.Length}; " +
            $"index bytes {new FileInfo(new FolderSearchIndexStore(cache.Path).CachePath(data.Path)).Length}; " +
            $"memory estimate {service.GetStatus(data.Path).MemoryBytes}; traversals {service.GetStatus(data.Path).Scans}.");
        Assert.Equal(files + folders, entries.Count);
        Assert.NotEmpty(matches);
        Assert.Equal(1, service.GetStatus(data.Path).Scans);
        Assert.True(build.Elapsed >= TimeSpan.FromMilliseconds(500));
    }

    private static FolderSearchIndexService NewService(string cache, SearchIndexWorkQueue? queue = null,
        long memoryBudget = 64L * 1024 * 1024, long memoryPerRoot = 24L * 1024 * 1024,
        TimeSpan? scanDelay = null, TimeSpan? persistInterval = null, TimeSpan? refreshDelay = null,
        Func<string, CancellationToken, IAsyncEnumerable<FolderSearchIndexEntry>>? traverse = null) =>
        new(cache, queue, memoryBudget, memoryPerRoot, scanDelay ?? TimeSpan.Zero,
            persistInterval ?? TimeSpan.Zero, refreshDelay ?? TimeSpan.Zero, false, traverse);

    private static FolderSearchIndexEntry Entry(string root, string path) => new()
    {
        Path = path, Name = Path.GetFileName(path), RelativePath = Path.GetRelativePath(root, path), Depth = 1
    };
    private static async Task<List<FolderSearchIndexEntry>> Read(FolderSearchIndexService service, string root)
    {
        var entries = new List<FolderSearchIndexEntry>();
        await foreach (var entry in service.SearchEntriesAsync(root)) entries.Add(entry);
        return entries;
    }
    private static async Task WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        try { while (!condition()) await Task.Delay(10, cts.Token); }
        catch (OperationCanceledException) { Assert.Fail("Index operation timed out."); }
    }
    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { while (!await condition()) await Task.Delay(10, cts.Token); }
        catch (OperationCanceledException) { Assert.Fail("Index operation timed out."); }
    }
    private static Task WaitForSearch(DesktopPanel panel) =>
        WaitFor(() => GetField<CancellationTokenSource?>(panel, "_searchCts") == null);
    private static T GetField<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static object? Invoke(DesktopPanel panel, string method, params object[] arguments) =>
        typeof(DesktopPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, arguments);
    private static void RunSta(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await test(); }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Search UI test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

[CollectionDefinition("Search indexing", DisableParallelization = true)]
public sealed class SearchIndexTestCollection { }
