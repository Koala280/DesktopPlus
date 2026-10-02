using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace DesktopPlus;

internal sealed class FolderSearchIndexStore
{
    internal const int Magic = 0x44505349;
    internal const int Version = 3;
    private readonly string _directory;

    internal FolderSearchIndexStore(string directory) => _directory = directory;
    internal string CachePath(string root) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))) + ".bin");

    internal sealed record Header(int Version, string Root, DateTime BuiltUtc, DateTime RootLastWriteUtc, int Count);
    internal static Header ReadHeader(BinaryReader reader, string root)
    {
        if (reader.ReadInt32() != Magic) throw new InvalidDataException("Invalid search index.");
        int version = reader.ReadInt32();
        if (version < 1 || version > Version) throw new InvalidDataException("Unsupported search index.");
        string storedRoot = reader.ReadString();
        if (!string.Equals(root, storedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Wrong search index root.");
        DateTime builtUtc = new(reader.ReadInt64(), DateTimeKind.Utc);
        DateTime rootLastWriteUtc = version >= 3 ? new(reader.ReadInt64(), DateTimeKind.Utc) : DateTime.MinValue;
        int count = reader.ReadInt32();
        if (count < 0) throw new InvalidDataException("Invalid search index length.");
        return new Header(version, storedRoot, builtUtc, rootLastWriteUtc, count);
    }

    internal async IAsyncEnumerable<FolderSearchIndexEntry> ReadAsync(
        string path, string root, int? publishedCount = null, bool background = true,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        Header header = ReadHeader(reader, root);
        var throttle = new SearchIndexIoThrottle();
        long checkpoint = stream.Position;
        for (int i = 0; i < (publishedCount ?? header.Count); i++)
        {
            token.ThrowIfCancellationRequested();
            string entryPath = FolderSearchIndexService.Normalize(reader.ReadString());
            string name = reader.ReadString();
            bool directory = reader.ReadBoolean();
            int depth = reader.ReadInt32();
            string relative = header.Version >= 2 ? reader.ReadString() : Path.GetRelativePath(root, entryPath);
            if (!IsDescendant(root, entryPath)) throw new InvalidDataException("Search index entry outside root.");
            yield return new FolderSearchIndexEntry
            {
                Path = entryPath, Name = name, IsDirectory = directory,
                Depth = Math.Max(1, depth), RelativePath = relative
            };
            // Count buffered I/O blocks, not BinaryReader's in-memory field reads.
            if (background && stream.Position - checkpoint >= 65536)
            {
                await throttle.WaitAsync(token).ConfigureAwait(false);
                checkpoint = stream.Position;
            }
        }
    }

    internal Writer CreateWriter(string root)
    {
        Directory.CreateDirectory(_directory);
        return new Writer(CachePath(root) + "." + Guid.NewGuid().ToString("N") + ".tmp", root);
    }

    internal sealed class Writer : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly long _countOffset;
        private readonly long _builtOffset;
        private readonly SearchIndexIoThrottle _throttle = new();
        private long _checkpoint;
        internal string Path { get; }
        internal int Count { get; private set; }
        private bool _disposed;

        internal Writer(string path, string root)
        {
            Path = path;
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
            _writer.Write(Magic);
            _writer.Write(Version);
            _writer.Write(root);
            _builtOffset = _stream.Position;
            _writer.Write(DateTime.UtcNow.Ticks);
            _writer.Write(GetRootWriteUtc(root).Ticks);
            _countOffset = _stream.Position;
            _writer.Write(0);
        }

        internal async ValueTask AddAsync(FolderSearchIndexEntry entry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _writer.Write(entry.Path);
            _writer.Write(entry.Name);
            _writer.Write(entry.IsDirectory);
            _writer.Write(entry.Depth);
            _writer.Write(entry.RelativePath);
            Count++;
            if (_stream.Position - _checkpoint >= 65536)
            {
                await _throttle.WaitAsync(token).ConfigureAwait(false);
                _checkpoint = _stream.Position;
            }
        }

        internal void Publish()
        {
            long end = _stream.Position;
            _stream.Position = _countOffset;
            _writer.Write(Count);
            _stream.Position = end;
            _writer.Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Position = _builtOffset;
            _writer.Write(DateTime.UtcNow.Ticks);
            _writer.Dispose();
            _stream.Dispose();
        }
    }

    internal static DateTime GetRootWriteUtc(string root)
    {
        try { return Directory.GetLastWriteTimeUtc(root); }
        catch { return DateTime.MinValue; }
    }

    internal static bool IsDescendant(string root, string path) =>
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal void Commit(Writer writer, string root)
    {
        writer.Publish();
        writer.Dispose();
        File.Move(writer.Path, CachePath(root), overwrite: true);
    }

    internal static void RefreshCommitTimestamp(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadString();
        long offset = stream.Position;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = offset;
        writer.Write(DateTime.UtcNow.Ticks);
    }

    internal void Trim(IReadOnlySet<string> configuredRoots)
    {
        try
        {
            var keep = configuredRoots.Select(CachePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.bin")
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            int retained = 0;
            foreach (var file in files)
            {
                if (keep.Contains(file.FullName)) continue;
                if (++retained > 64 || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-21)) file.Delete();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
