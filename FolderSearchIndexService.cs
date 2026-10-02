using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace DesktopPlus;

internal enum SearchIndexChangeKind { Created, Deleted, Renamed, Changed }
internal sealed record FolderSearchIndexStatus(bool IsPreparing, bool IsRefreshing, bool HasCompleteIndex,
    int EntryCount, long MemoryBytes, int Loads, int Scans, int Writes);

/// <summary>
/// Application-owned indexes. Cooperative one-second scan slices let cache loads
/// and filesystem changes run ahead of background traversal on the same I/O queue.
/// </summary>
internal sealed class FolderSearchIndexService : IDisposable
{
    internal static FolderSearchIndexService Shared { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopPlus", "SearchIndex"),
        SearchIndexWorkQueue.Shared);

    private readonly object _sync = new();
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly FolderSearchIndexStore _store;
    private readonly SearchIndexWorkQueue _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly long _memoryBudget;
    private readonly long _memoryPerRoot;
    private readonly TimeSpan _scanDelay;
    private readonly TimeSpan _persistInterval;
    private readonly TimeSpan _refreshDelay;
    private readonly bool _watchersEnabled;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<FolderSearchIndexEntry>> _traverse;
    private long _access;
    private bool _disposed;

    internal event Action<string>? IndexChanged;

    internal FolderSearchIndexService(string cacheDirectory, SearchIndexWorkQueue? queue = null,
        long memoryBudget = 64L * 1024 * 1024, long memoryPerRoot = 24L * 1024 * 1024,
        TimeSpan? scanDelay = null, TimeSpan? persistInterval = null, TimeSpan? refreshDelay = null,
        bool watchersEnabled = true,
        Func<string, CancellationToken, IAsyncEnumerable<FolderSearchIndexEntry>>? traverse = null)
    {
        _store = new FolderSearchIndexStore(cacheDirectory);
        _queue = queue ?? new SearchIndexWorkQueue();
        _memoryBudget = memoryBudget;
        _memoryPerRoot = memoryPerRoot;
        _scanDelay = scanDelay ?? TimeSpan.FromSeconds(5);
        _persistInterval = persistInterval ?? TimeSpan.FromSeconds(30);
        _refreshDelay = refreshDelay ?? TimeSpan.FromMilliseconds(1500);
        _watchersEnabled = watchersEnabled;
        _traverse = traverse ?? FolderSearchTraversal.EnumerateWithCheckpointsAsync;
    }

    private sealed class Change
    {
        internal long Sequence;
        internal SearchIndexChangeKind Kind;
        internal string Path = "";
        internal string? OldPath;
        internal bool IsDirectory;
        internal FolderSearchIndexEntry? Entry;
        internal bool Processed;
        internal bool ReplaceSubtree;
        internal bool RenamedDuringFullScan;
    }

    private sealed record Source(string Path, int Count, long Sequence, bool Complete, string Scope);
    private sealed class RootState
    {
        internal required string Root;
        internal required CancellationTokenSource Lifetime;
        internal readonly Dictionary<string, FolderSearchIndexEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<Change> Changes = new();
        internal readonly List<Source> Sources = new();
        internal readonly HashSet<string> TemporaryFiles = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, long> Subtrees = new(StringComparer.OrdinalIgnoreCase);
        internal FileSystemWatcher? ContentWatcher;
        internal FileSystemWatcher? ParentWatcher;
        internal Scan? Scan;
        internal Hydration? Hydration;
        internal bool Preferred;
        internal bool LoadPending = true;
        internal bool ScanScheduled;
        internal bool NeedsFullScan;
        internal bool HasCompleteIndex;
        internal bool MemoryComplete = true;
        internal bool PersistScheduled;
        internal bool PersistRunning;
        internal bool NeedsPersist;
        internal bool ProcessChangesQueued;
        internal bool ResetContentWatcher;
        internal bool ReconcileAfterQuiet;
        internal long ChangeBytes;
        internal long MemoryBytes;
        internal long MemoryEpoch;
        internal long LastAccess;
        internal long Sequence;
        internal long Revision;
        internal DateTime LastPersistUtc = DateTime.MinValue;
        internal DateTime FullScanDueUtc;
        internal DateTime RootLastWriteUtc;
        internal int EntryCount;
        internal int Loads;
        internal int Scans;
        internal int Writes;
    }

    private sealed class Scan
    {
        internal required IAsyncEnumerator<FolderSearchIndexEntry> Reader;
        internal required FolderSearchIndexStore.Writer Writer;
        internal required string ScannedRoot;
        internal long Sequence;
        internal bool Full;
        internal bool Merge;
    }

    private sealed class Hydration
    {
        internal required IAsyncEnumerator<FolderSearchIndexEntry> Reader;
        internal required Source Source;
        internal long MemoryEpoch;
        internal bool AllCached = true;
    }

    internal void Configure(IEnumerable<string> folders, IEnumerable<string>? preferredFolders = null)
    {
        var normalized = folders.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var preferred = (preferredFolders ?? Array.Empty<string>()).Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<RootState>();
        var removed = new List<RootState>();
        lock (_sync)
        {
            if (_disposed) return;
            var keep = normalized.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string root in _roots.Keys.Where(root => !keep.Contains(root)).ToArray())
            {
                RootState state = _roots[root];
                state.Lifetime.Cancel();
                _roots.Remove(root);
                removed.Add(state);
            }
            foreach (string root in normalized.OrderBy(root => preferred.Contains(root) ? 0 : 1))
            {
                if (!_roots.TryGetValue(root, out var state))
                {
                    state = new RootState
                    {
                        Root = root, Lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token),
                        FullScanDueUtc = DateTime.UtcNow + _scanDelay, NeedsFullScan = true
                    };
                    _roots.Add(root, state);
                    added.Add(state);
                }
                state.Preferred = preferred.Contains(root);
            }
        }
        foreach (var state in removed) QueueCleanup(state);
        foreach (var state in added) Post(state, () => LoadAsync(state), state.Preferred ? 0 : 1);
    }

    internal bool Contains(string root)
    {
        lock (_sync) return _roots.ContainsKey(Normalize(root));
    }

    internal FolderSearchIndexStatus GetStatus(string root)
    {
        lock (_sync)
        {
            if (!_roots.TryGetValue(Normalize(root), out var state))
                return new(false, false, false, 0, 0, 0, 0, 0);
            bool pending = state.LoadPending || state.NeedsFullScan || state.ScanScheduled || state.Scan != null || state.Subtrees.Count > 0;
            return new(pending && !state.HasCompleteIndex, pending && state.HasCompleteIndex,
                state.HasCompleteIndex, state.EntryCount, state.MemoryBytes + state.ChangeBytes, state.Loads, state.Scans, state.Writes);
        }
    }

    internal IReadOnlyList<string>? GetDirectChildren(string root)
    {
        lock (_sync)
        {
            if (!_roots.TryGetValue(Normalize(root), out var state) || !state.HasCompleteIndex || !state.MemoryComplete)
                return null;
            return state.Entries.Values.Where(entry => entry.Depth == 1)
                .OrderByDescending(entry => entry.IsDirectory).Select(entry => entry.Path).ToArray();
        }
    }

    private async Task LoadAsync(RootState state)
    {
        state.Lifetime.Token.ThrowIfCancellationRequested();
        ConfigureWatchers(state);
        string path = _store.CachePath(state.Root);
        if (File.Exists(path))
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    var header = FolderSearchIndexStore.ReadHeader(reader, state.Root);
                    lock (_sync)
                    {
                        state.RootLastWriteUtc = header.RootLastWriteUtc;
                        state.LastPersistUtc = header.BuiltUtc;
                        state.Sources.Add(new Source(path, header.Count, 0, true, state.Root));
                        state.HasCompleteIndex = true;
                        state.EntryCount = header.Count;
                        state.Loads++;
                        state.MemoryComplete = false;
                        state.Revision++;
                    }
                }
                Notify(state); // The complete persisted source is searchable before RAM hydration.
                await BeginHydrationAsync(state, state.Sources[0]).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Debug.WriteLine($"Cannot load search index '{state.Root}': {exception.Message}");
                lock (_sync)
                {
                    ClearMemory(state);
                    state.Sources.Clear();
                    state.HasCompleteIndex = false;
                    state.MemoryComplete = true;
                }
            }
        }
        lock (_sync) state.LoadPending = false;
        Notify(state);
        ScheduleScan(state);
    }

    private async Task BeginHydrationAsync(RootState state, Source source)
    {
        lock (_sync)
        {
            state.LoadPending = true;
            state.MemoryComplete = false;
            state.Hydration = new Hydration
            {
                Reader = _store.ReadAsync(source.Path, state.Root, token: state.Lifetime.Token).GetAsyncEnumerator(),
                Source = source, MemoryEpoch = state.MemoryEpoch
            };
        }
        await HydrateChunkAsync(state).ConfigureAwait(false);
    }

    private async Task HydrateChunkAsync(RootState state)
    {
        Hydration? hydration;
        lock (_sync) hydration = state.Hydration;
        if (hydration == null) return;
        bool finished = false;
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (elapsed.Elapsed < TimeSpan.FromMilliseconds(800))
            {
                state.Lifetime.Token.ThrowIfCancellationRequested();
                if (!await hydration.Reader.MoveNextAsync().ConfigureAwait(false)) { finished = true; break; }
                lock (_sync)
                {
                    var current = Transform(hydration.Reader.Current, state.Root, state.Changes, hydration.Source.Sequence);
                    hydration.AllCached &= hydration.MemoryEpoch == state.MemoryEpoch &&
                        (current == null || CacheEntry(state, current));
                }
                // The complete source remains on disk; do not read its remainder merely to exceed the RAM cap.
                if (!hydration.AllCached) { finished = true; break; }
            }
            if (!finished)
            {
                Post(state, () => HydrateChunkAsync(state), state.Preferred ? 0 : 1);
                return;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"Cannot hydrate search index '{state.Root}': {exception.Message}");
            lock (_sync)
            {
                state.Sources.Remove(hydration.Source);
                ClearMemory(state);
                state.HasCompleteIndex = false;
                state.NeedsFullScan = true;
            }
            hydration.AllCached = false;
        }
        await hydration.Reader.DisposeAsync().ConfigureAwait(false);
        lock (_sync)
        {
            hydration.AllCached &= hydration.MemoryEpoch == state.MemoryEpoch;
            foreach (var change in state.Changes.Where(change => change.Entry != null))
            {
                var current = Transform(change.Entry!, state.Root, state.Changes, change.Sequence);
                if (current != null) hydration.AllCached &= CacheEntry(state, current);
            }
            state.MemoryComplete = hydration.AllCached;
            state.Hydration = null;
            state.LoadPending = false;
        }
        Notify(state);
        ScheduleScan(state);
        SchedulePersist(state);
    }

    private void ScheduleScan(RootState state)
    {
        DateTime due;
        lock (_sync)
        {
            if (!IsCurrent(state) || state.LoadPending || state.ScanScheduled || state.Scan != null || state.PersistRunning)
                return;
            if (!state.NeedsFullScan && state.Subtrees.Count == 0) return;
            state.ScanScheduled = true;
            due = state.NeedsFullScan ? state.FullScanDueUtc : DateTime.UtcNow;
        }
        _ = DelayPostAsync(state, due, () => StartScanAsync(state), state.Preferred ? 2 : 3);
    }

    private async Task StartScanAsync(RootState state)
    {
        bool waitAgain;
        lock (_sync)
        {
            state.ScanScheduled = false;
            if (state.Scan != null || state.PersistRunning || state.LoadPending || !IsCurrent(state)) return;
            waitAgain = state.NeedsFullScan && state.FullScanDueUtc > DateTime.UtcNow;
        }
        if (waitAgain) { ScheduleScan(state); return; }
        if (!Directory.Exists(state.Root)) return;
        ConfigureWatchers(state);
        string subtree;
        long sequence;
        bool full;
        lock (_sync)
        {
            full = state.NeedsFullScan;
            if (full)
            {
                state.NeedsFullScan = false;
                state.ReconcileAfterQuiet = false;
                subtree = state.Root;
                sequence = state.Sequence;
                state.Scans++;
                foreach (string covered in state.Subtrees.Where(pair => pair.Value <= sequence).Select(pair => pair.Key).ToArray())
                    state.Subtrees.Remove(covered);
            }
            else
            {
                if (state.Subtrees.Count == 0) return;
                var target = state.Subtrees.First();
                subtree = target.Key;
                sequence = target.Value;
                state.Subtrees.Remove(subtree);
            }
            state.ScanScheduled = true; // Keep preparation observable while the reader/writer are opened.
        }
        var writer = _store.CreateWriter(state.Root);
        var scan = new Scan
        {
            Reader = _traverse(subtree, state.Lifetime.Token).GetAsyncEnumerator(),
            Writer = writer, ScannedRoot = subtree, Sequence = sequence, Full = full
        };
        if (!full)
        {
            var entry = ReadEntry(state.Root, subtree);
            if (entry != null) await writer.AddAsync(entry, state.Lifetime.Token).ConfigureAwait(false);
        }
        lock (_sync)
        {
            state.Scan = scan;
            state.ScanScheduled = false;
            state.TemporaryFiles.Add(writer.Path);
        }
        await ScanChunkAsync(state).ConfigureAwait(false);
    }

    private async Task ScanChunkAsync(RootState state)
    {
        Scan? scan;
        lock (_sync) scan = state.Scan;
        if (scan == null) return;
        var elapsed = Stopwatch.StartNew();
        bool finished = false;
        try
        {
            while (elapsed.Elapsed < TimeSpan.FromMilliseconds(800))
            {
                state.Lifetime.Token.ThrowIfCancellationRequested();
                if (!await scan.Reader.MoveNextAsync().ConfigureAwait(false)) { finished = true; break; }
                FolderSearchIndexEntry entry = scan.Reader.Current;
                if (string.IsNullOrEmpty(entry.Path)) continue; // Cooperative traversal checkpoint.
                if (!scan.Full && !scan.Merge) entry = WithPath(state.Root, entry, entry.Path);
                await scan.Writer.AddAsync(entry, state.Lifetime.Token).ConfigureAwait(false);
                if (!scan.Merge)
                {
                    lock (_sync)
                    {
                        var current = Transform(entry, state.Root, state.Changes, scan.Sequence);
                        if (current != null && !(scan.Full && state.HasCompleteIndex)) CacheEntry(state, current);
                    }
                }
            }
            scan.Writer.Publish();
            lock (_sync)
            {
                state.Sources.RemoveAll(source => source.Path == scan.Writer.Path);
                // A complete old index stays usable throughout a full refresh.
                if (!scan.Merge && (!scan.Full || !state.HasCompleteIndex))
                    state.Sources.Add(new Source(scan.Writer.Path, scan.Writer.Count, scan.Sequence, false, scan.ScannedRoot));
                state.EntryCount = Math.Max(state.EntryCount, scan.Writer.Count);
                state.Revision++;
            }
            Notify(state);
            if (!finished)
            {
                Post(state, () => ScanChunkAsync(state), state.Preferred ? 2 : 3);
                return;
            }
            await scan.Reader.DisposeAsync().ConfigureAwait(false);
            scan.Writer.Dispose();
            if (scan.Merge)
            {
                await FinishMergeAsync(state, scan).ConfigureAwait(false);
                return;
            }
            bool hydrateRefresh;
            Source completed = new(scan.Writer.Path, scan.Writer.Count, scan.Sequence, true, scan.ScannedRoot);
            DateTime rootWriteUtc = FolderSearchIndexStore.GetRootWriteUtc(state.Root);
            lock (_sync)
            {
                hydrateRefresh = scan.Full && state.HasCompleteIndex;
                if (scan.Full)
                {
                    if (hydrateRefresh) ClearMemory(state);
                    state.Sources.Clear();
                    state.HasCompleteIndex = true;
                    state.EntryCount = scan.Writer.Count;
                }
                else state.Sources.RemoveAll(source => source.Path == scan.Writer.Path);
                state.Sources.Add(completed);
                state.Scan = null;
                state.NeedsPersist = true;
                state.RootLastWriteUtc = rootWriteUtc;
            }
            if (hydrateRefresh)
            {
                await BeginHydrationAsync(state, completed).ConfigureAwait(false);
                return;
            }
            ScheduleScan(state);
            SchedulePersist(state);
            Notify(state);
        }
        catch
        {
            await scan.Reader.DisposeAsync().ConfigureAwait(false);
            scan.Writer.Dispose();
            lock (_sync)
            {
                state.Sources.RemoveAll(source => source.Path == scan.Writer.Path);
                state.Scan = null;
                state.PersistRunning = false;
            }
            try { File.Delete(scan.Writer.Path); } catch (IOException) { }
            throw;
        }
    }

    internal void NotifyChange(string root, SearchIndexChangeKind kind, string path, string? oldPath = null)
    {
        if (kind == SearchIndexChangeKind.Changed) return;
        RootState? state;
        Change change;
        bool queueChanges;
        lock (_sync)
        {
            if (!_roots.TryGetValue(Normalize(root), out state) || !IsCurrent(state)) return;
            path = Normalize(path);
            if (!FolderSearchIndexStore.IsDescendant(state.Root, path))
            {
                if (kind != SearchIndexChangeKind.Renamed || oldPath == null ||
                    !FolderSearchIndexStore.IsDescendant(state.Root, Normalize(oldPath))) return;
                path = Normalize(oldPath);
                oldPath = null;
                kind = SearchIndexChangeKind.Deleted;
            }
            change = new Change { Sequence = ++state.Sequence, Kind = kind, Path = path,
                OldPath = oldPath == null ? null : Normalize(oldPath),
                RenamedDuringFullScan = kind == SearchIndexChangeKind.Renamed && state.Scan?.Full == true };
            state.Changes.Add(change);
            if (state.ReconcileAfterQuiet) state.FullScanDueUtc = DateTime.UtcNow + _refreshDelay;
            state.ChangeBytes += EstimateChangeBytes(change);
            // Deletes and renames are immediately visible without filesystem I/O.
            if (kind is SearchIndexChangeKind.Deleted or SearchIndexChangeKind.Renamed)
                ApplyMemoryChange(state, change);
            state.NeedsPersist = true;
            state.Revision++;
            if (state.Changes.Count > 2048 || state.ChangeBytes > _memoryPerRoot ||
                _roots.Values.Sum(root => root.ChangeBytes) > _memoryBudget)
            {
                // An overflowing notification buffer is a lost-event condition.
                // Bound it and reconcile once the event stream becomes quiet.
                state.Changes.Clear();
                state.ChangeBytes = 0;
                state.NeedsFullScan = true;
                state.ReconcileAfterQuiet = true;
                state.FullScanDueUtc = DateTime.UtcNow + _refreshDelay;
                state.MemoryComplete = false;
            }
            TrimMemoryForChanges(state);
            queueChanges = !state.ProcessChangesQueued;
            state.ProcessChangesQueued = true;
        }
        Notify(state);
        if (queueChanges) Post(state, () => ProcessChangesAsync(state), 0);
    }

    private async Task ProcessChangesAsync(RootState state)
    {
        Change[] batch;
        lock (_sync) batch = state.Changes.Where(change => !change.Processed).Take(64).ToArray();
        var throttle = new SearchIndexIoThrottle();
        foreach (var change in batch)
        {
            await throttle.WaitAsync(state.Lifetime.Token).ConfigureAwait(false);
            ProcessChange(state, change);
        }
        bool more;
        lock (_sync)
        {
            more = state.Changes.Any(change => !change.Processed);
            state.ProcessChangesQueued = more;
        }
        if (more) Post(state, () => ProcessChangesAsync(state), 0);
        ScheduleScan(state);
        SchedulePersist(state);
        Notify(state);
    }

    private void ProcessChange(RootState state, Change change)
    {
        var entry = change.Kind == SearchIndexChangeKind.Deleted ? null : ReadEntry(state.Root, change.Path);
        lock (_sync)
        {
            change.Processed = true;
            change.Entry = entry;
            change.IsDirectory = entry?.IsDirectory == true;
            if (entry == null && change.Kind is SearchIndexChangeKind.Created or SearchIndexChangeKind.Renamed)
                ApplyMemoryChange(state, new Change { Kind = SearchIndexChangeKind.Deleted, Path = change.Path });
            if (entry != null)
            {
                CacheEntry(state, entry);
                if (change.Kind == SearchIndexChangeKind.Created && entry.IsDirectory)
                {
                    state.Subtrees[entry.Path] = change.Sequence;
                    state.MemoryComplete = false;
                }
                else if (change.Kind == SearchIndexChangeKind.Renamed && entry.IsDirectory)
                {
                    bool needsSubtree = change.OldPath == null ||
                        !FolderSearchIndexStore.IsDescendant(state.Root, change.OldPath) ||
                        state.Scan?.Full == true || change.RenamedDuringFullScan ||
                        state.Subtrees.Keys.Any(path => SameOrDescendant(change.OldPath, path)) ||
                        state.Changes.Any(earlier => earlier.Sequence < change.Sequence &&
                            earlier.Kind == SearchIndexChangeKind.Created && earlier.Path == change.OldPath);
                    if (needsSubtree)
                    {
                        foreach (string old in state.Subtrees.Keys.Where(path => change.OldPath != null &&
                            SameOrDescendant(change.OldPath, path)).ToArray()) state.Subtrees.Remove(old);
                        change.ReplaceSubtree = true;
                        state.Subtrees[entry.Path] = change.Sequence;
                        state.MemoryComplete = false;
                    }
                }
            }
            state.Revision++;
        }
    }

    private void ApplyMemoryChange(RootState state, Change change)
    {
        string affected = change.Kind == SearchIndexChangeKind.Renamed ? change.OldPath ?? change.Path : change.Path;
        foreach (var entry in state.Entries.Values.Where(entry => SameOrDescendant(affected, entry.Path)).ToArray())
        {
            RemoveEntry(state, entry.Path);
            if (change.Kind == SearchIndexChangeKind.Renamed)
                CacheEntry(state, WithPath(state.Root, entry, change.Path + entry.Path[affected.Length..]));
        }
    }

    internal void RequestRefresh(string root)
    {
        RootState? state;
        lock (_sync)
        {
            if (!_roots.TryGetValue(Normalize(root), out state) || !IsCurrent(state)) return;
            state.NeedsFullScan = true;
            state.ReconcileAfterQuiet = true;
            state.FullScanDueUtc = DateTime.UtcNow + _refreshDelay;
            state.ResetContentWatcher = true;
        }
        ScheduleScan(state);
    }

    private void SchedulePersist(RootState state)
    {
        DateTime due;
        lock (_sync)
        {
            if (!IsCurrent(state) || !state.NeedsPersist || state.LoadPending || state.NeedsFullScan || state.ProcessChangesQueued || state.Scan != null ||
                state.ScanScheduled || state.Subtrees.Count > 0 || state.PersistRunning || state.PersistScheduled) return;
            state.PersistScheduled = true;
            due = state.LastPersistUtc + _persistInterval;
        }
        _ = DelayPostAsync(state, due, () => StartMergeAsync(state), 3);
    }

    private async Task StartMergeAsync(RootState state)
    {
        Source[] sources;
        Change[] changes;
        long sequence;
        lock (_sync)
        {
            state.PersistScheduled = false;
            if (state.Scan != null || state.LoadPending || state.NeedsFullScan || state.ProcessChangesQueued ||
                state.Changes.Any(change => !change.Processed) || state.ScanScheduled || state.Subtrees.Count > 0 || !state.NeedsPersist) return;
            if (state.Sources.Count == 0 && !state.HasCompleteIndex) return;
            state.PersistRunning = true;
            sequence = state.Sequence;
            sources = state.Sources.ToArray();
            changes = state.Changes.Where(change => change.Sequence <= sequence).ToArray();
        }
        // An unchanged complete scan is already a throttled temporary index.
        // Publish it directly rather than reading and writing the entire index twice.
        if (sources.Length == 1 && sources[0].Complete &&
            string.Equals(sources[0].Scope, state.Root, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sources[0].Path, _store.CachePath(state.Root), StringComparison.OrdinalIgnoreCase) &&
            changes.All(change => change.Sequence <= sources[0].Sequence))
        {
            await CommitSourceAsync(state, sources[0]).ConfigureAwait(false);
            return;
        }
        var writer = _store.CreateWriter(state.Root);
        var merge = new Scan
        {
            Reader = ReadSnapshotAsync(state.Root, sources, changes, state.Lifetime.Token, true).GetAsyncEnumerator(),
            Writer = writer, ScannedRoot = state.Root, Sequence = sequence, Merge = true
        };
        lock (_sync)
        {
            state.Scan = merge;
            state.TemporaryFiles.Add(writer.Path);
        }
        await ScanChunkAsync(state).ConfigureAwait(false);
    }

    private async Task FinishMergeAsync(RootState state, Scan merge)
    {
        await CommitSourceAsync(state, new Source(merge.Writer.Path, merge.Writer.Count, merge.Sequence, true, state.Root))
            .ConfigureAwait(false);
    }

    private async Task CommitSourceAsync(RootState state, Source completed)
    {
        state.Lifetime.Token.ThrowIfCancellationRequested();
        string final = _store.CachePath(state.Root);
        FolderSearchIndexStore.RefreshCommitTimestamp(completed.Path);
        File.Move(completed.Path, final, overwrite: true);
        string[] obsolete;
        lock (_sync)
        {
            state.Sources.Clear();
            state.Sources.Add(new Source(final, completed.Count, completed.Sequence, true, state.Root));
            state.Changes.RemoveAll(change => change.Sequence <= completed.Sequence);
            state.ChangeBytes = state.Changes.Sum(EstimateChangeBytes);
            state.NeedsPersist = state.Changes.Count > 0;
            state.LastPersistUtc = DateTime.UtcNow;
            state.HasCompleteIndex = true;
            state.EntryCount = completed.Count;
            state.Writes++;
            state.Scan = null;
            state.PersistRunning = false;
            ClearMemory(state);
            state.Revision++;
            obsolete = state.TemporaryFiles.Where(path => path != completed.Path).ToArray();
            state.TemporaryFiles.Clear();
        }
        foreach (string path in obsolete) { try { File.Delete(path); } catch (IOException) { } }
        IReadOnlySet<string> configured;
        lock (_sync) configured = _roots.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _store.Trim(configured);
        Notify(state);
        await BeginHydrationAsync(state, new Source(final, completed.Count, completed.Sequence, true, state.Root))
            .ConfigureAwait(false);
    }

    internal async IAsyncEnumerable<FolderSearchIndexEntry> SearchEntriesAsync(string root,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        root = Normalize(root);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            FolderSearchIndexEntry[]? memory;
            Source[] sources;
            Change[] changes;
            lock (_sync)
            {
                if (!_roots.TryGetValue(root, out var state)) yield break;
                state.LastAccess = ++_access;
                memory = state.MemoryComplete ? state.Entries.Values.ToArray() : null;
                sources = state.Sources.ToArray();
                changes = state.Changes.ToArray();
            }
            if (memory != null)
            {
                foreach (var entry in memory) { token.ThrowIfCancellationRequested(); yield return entry; }
                yield break;
            }
            await using var reader = ReadSnapshotAsync(root, sources, changes, token, false).GetAsyncEnumerator();
            bool retry = false;
            while (true)
            {
                FolderSearchIndexEntry entry;
                try
                {
                    if (!await reader.MoveNextAsync().ConfigureAwait(false)) break;
                    entry = reader.Current;
                }
                catch (IOException) { retry = true; break; }
                yield return entry;
            }
            if (!retry) yield break; // An atomic replacement can retire an old temporary source.
        }
    }

    private async IAsyncEnumerable<FolderSearchIndexEntry> ReadSnapshotAsync(string root, Source[] sources,
        Change[] changes, [EnumeratorCancellation] CancellationToken token, bool background)
    {
        foreach (var source in sources)
        {
            await foreach (var entry in _store.ReadAsync(source.Path, root,
                source.Complete ? null : source.Count, background, token))
            {
                var current = Transform(entry, root, changes, source.Sequence);
                if (current != null) yield return current;
            }
        }
        foreach (var change in changes)
        {
            if (change.Kind is not (SearchIndexChangeKind.Created or SearchIndexChangeKind.Renamed) || change.Entry == null) continue;
            if (sources.Any(source => source.Complete && source.Sequence >= change.Sequence &&
                SameOrDescendant(source.Scope, change.Path))) continue;
            var current = Transform(change.Entry, root, changes, change.Sequence);
            if (current != null) yield return current;
        }
    }

    private static FolderSearchIndexEntry? Transform(FolderSearchIndexEntry entry, string root,
        IEnumerable<Change> changes, long afterSequence)
    {
        foreach (var change in changes)
        {
            if (change.Sequence <= afterSequence) continue;
            if (change.Kind == SearchIndexChangeKind.Renamed && change.OldPath != null &&
                SameOrDescendant(change.OldPath, entry.Path))
                entry = WithPath(root, entry, change.Path + entry.Path[change.OldPath.Length..]);
            // Parsed rename metadata is yielded once as an addition, including files
            // moved in from outside the root or renamed before their scan reached them.
            if (change.Kind == SearchIndexChangeKind.Renamed && change.Entry != null &&
                string.Equals(change.Path, entry.Path, StringComparison.OrdinalIgnoreCase)) return null;
            if (change.ReplaceSubtree && SameOrDescendant(change.Path, entry.Path)) return null;
            if (change.Processed && change.Entry == null && change.Kind == SearchIndexChangeKind.Renamed &&
                SameOrDescendant(change.Path, entry.Path)) return null;
            if (change.Kind is SearchIndexChangeKind.Deleted or SearchIndexChangeKind.Created &&
                     SameOrDescendant(change.Path, entry.Path)) return null;
        }
        return entry;
    }

    private bool CacheEntry(RootState state, FolderSearchIndexEntry entry)
    {
        RemoveEntry(state, entry.Path);
        long bytes = EstimateBytes(entry);
        if (state.MemoryBytes + state.ChangeBytes + bytes > _memoryPerRoot)
        {
            state.MemoryComplete = false;
            return false;
        }
        long total = _roots.Values.Sum(root => root.MemoryBytes + root.ChangeBytes);
        while (total + bytes > _memoryBudget ||
               (state.Entries.Count == 0 && _roots.Values.Count(root => root.Entries.Count > 0) >= 32))
        {
            RootState? victim = _roots.Values.Where(root => root != state && root.Entries.Count > 0)
                .OrderBy(root => root.LastAccess).FirstOrDefault();
            if (victim == null) { state.MemoryComplete = false; return false; }
            total -= victim.MemoryBytes;
            ClearMemory(victim);
        }
        state.Entries[entry.Path] = entry;
        state.MemoryBytes += bytes;
        state.LastAccess = ++_access;
        return true;
    }

    private static void RemoveEntry(RootState state, string path)
    {
        if (state.Entries.Remove(path, out var old)) state.MemoryBytes -= EstimateBytes(old);
    }

    private static void ClearMemory(RootState state)
    {
        state.Entries.Clear();
        state.MemoryBytes = 0;
        state.MemoryComplete = false;
        state.MemoryEpoch++;
    }

    private void TrimMemoryForChanges(RootState state)
    {
        while (state.MemoryBytes + state.ChangeBytes > _memoryPerRoot ||
               _roots.Values.Sum(root => root.MemoryBytes + root.ChangeBytes) > _memoryBudget)
        {
            RootState? victim = state.MemoryBytes + state.ChangeBytes > _memoryPerRoot ? state :
                _roots.Values.Where(root => root.MemoryBytes > 0).OrderBy(root => root.LastAccess).FirstOrDefault();
            if (victim == null || victim.MemoryBytes == 0) break;
            ClearMemory(victim);
        }
    }

    private static long EstimateChangeBytes(Change change) =>
        192L + (change.Path.Length * 2L + (change.OldPath?.Length ?? 0)) * sizeof(char);

    private static long EstimateBytes(FolderSearchIndexEntry entry) =>
        128L + (entry.Path.Length + entry.Name.Length + entry.RelativePath.Length) * sizeof(char);
    internal static string Normalize(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private static bool SameOrDescendant(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) || FolderSearchIndexStore.IsDescendant(root, path);

    private static FolderSearchIndexEntry WithPath(string root, FolderSearchIndexEntry entry, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return new FolderSearchIndexEntry { Path = path, Name = Path.GetFileName(path), IsDirectory = entry.IsDirectory,
            RelativePath = relative, Depth = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length };
    }

    private static FolderSearchIndexEntry? ReadEntry(string root, string path)
    {
        try
        {
            if ((File.GetAttributes(root) & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return null;
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return null;
            string? ancestor = Path.GetDirectoryName(path);
            while (ancestor != null && !string.Equals(ancestor, root, StringComparison.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(ancestor) & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return null;
                ancestor = Path.GetDirectoryName(ancestor);
            }
            return WithPath(root, new FolderSearchIndexEntry { IsDirectory = attributes.HasFlag(FileAttributes.Directory) }, path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private bool IsCurrent(RootState state) => !_disposed && !state.Lifetime.IsCancellationRequested &&
        _roots.TryGetValue(state.Root, out var current) && ReferenceEquals(state, current);

    private void Post(RootState state, Func<Task> action, int priority)
    {
        _ = ObserveAsync(_queue.RunAsync(action, state.Lifetime.Token, priority), state);
    }
    private async Task ObserveAsync(Task task, RootState state)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Debug.WriteLine($"Search index job failed for '{state.Root}': {exception}"); }
    }

    private async Task DelayPostAsync(RootState state, DateTime due, Func<Task> action, int priority)
    {
        try
        {
            TimeSpan delay = due - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, state.Lifetime.Token).ConfigureAwait(false);
            Post(state, action, priority);
        }
        catch (OperationCanceledException) { }
    }

    private void Notify(RootState state)
    {
        if (!state.Lifetime.IsCancellationRequested) IndexChanged?.Invoke(state.Root);
    }

    private void ConfigureWatchers(RootState state)
    {
        if (!_watchersEnabled) return;
        bool reset;
        lock (_sync) { reset = state.ResetContentWatcher; state.ResetContentWatcher = false; }
        if (reset)
        {
            state.ContentWatcher?.Dispose();
            state.ContentWatcher = null;
        }
        if (state.ContentWatcher == null && Directory.Exists(state.Root))
        {
            try
            {
                var watcher = new FileSystemWatcher(state.Root)
                {
                    IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                watcher.Created += (_, e) => NotifyChange(state.Root, SearchIndexChangeKind.Created, e.FullPath);
                watcher.Deleted += (_, e) => NotifyChange(state.Root, SearchIndexChangeKind.Deleted, e.FullPath);
                watcher.Renamed += (_, e) => NotifyChange(state.Root, SearchIndexChangeKind.Renamed, e.FullPath, e.OldFullPath);
                watcher.Error += (_, _) => RequestRefresh(state.Root);
                watcher.EnableRaisingEvents = true;
                state.ContentWatcher = watcher;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(state.Root));
        if (state.ParentWatcher == null && parent != null && Directory.Exists(parent))
        {
            try
            {
                var watcher = new FileSystemWatcher(parent) { NotifyFilter = NotifyFilters.DirectoryName };
                void ParentChanged(object sender, FileSystemEventArgs e)
                {
                    if (string.Equals(e.FullPath, state.Root, StringComparison.OrdinalIgnoreCase) ||
                        e is RenamedEventArgs renamed && string.Equals(renamed.OldFullPath, state.Root, StringComparison.OrdinalIgnoreCase))
                        RequestRefresh(state.Root);
                }
                watcher.Created += ParentChanged;
                watcher.Deleted += ParentChanged;
                watcher.Renamed += ParentChanged;
                watcher.EnableRaisingEvents = true;
                state.ParentWatcher = watcher;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void QueueCleanup(RootState state)
    {
        _ = _queue.RunAsync(async () =>
        {
            state.ContentWatcher?.Dispose();
            state.ParentWatcher?.Dispose();
            if (state.Hydration != null) await state.Hydration.Reader.DisposeAsync().ConfigureAwait(false);
            if (state.Scan != null)
            {
                await state.Scan.Reader.DisposeAsync().ConfigureAwait(false);
                state.Scan.Writer.Dispose();
            }
            foreach (string path in state.TemporaryFiles) { try { File.Delete(path); } catch (IOException) { } }
        }, priority: 0);
    }

    public void Dispose()
    {
        RootState[] states;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            states = _roots.Values.ToArray();
            _roots.Clear();
        }
        foreach (var state in states) QueueCleanup(state);
    }
}
