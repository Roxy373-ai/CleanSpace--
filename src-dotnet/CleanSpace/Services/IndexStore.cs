using System.Text;
using CleanSpace.Models;

namespace CleanSpace.Services;

public sealed record IndexSummary(long FileCount, long TotalBytes, long SafeBytes, int LargeFileCount, DateTime CreatedUtc);

public sealed class IndexStore
{
    private const string Magic = "CleanSpaceIndex";
    private const int FormatVersion = 2;
    private readonly string _indexPath;

    public IndexStore()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CLEANSPACE_DATA_DIR");
        var root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "허준영", "CleanSpace")
            : overrideRoot;
        Directory.CreateDirectory(root);
        _indexPath = Path.Combine(root, "scan-index.v2.bin");
    }

    public bool Exists => File.Exists(_indexPath);
    public IndexWriter BeginWrite() => new(_indexPath);

    public IndexSummary? ReadSummary()
    {
        try { using var reader = OpenReader(); return ReadHeader(reader); }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    public IEnumerable<FileRecord> EnumerateRecords()
    {
        using var reader = OpenReader();
        var summary = ReadHeader(reader);
        for (long i = 0; i < summary.FileCount; i++) yield return ReadRecord(reader);
        if (reader.ReadString() != "Complete") throw new InvalidDataException("Incomplete CleanSpace index.");
    }

    public Task<IReadOnlyList<FileRecord>> QueryLargestAsync(int limit, string? contains = null,
        bool mediaOnly = false, CancellationToken token = default) => Task.Run<IReadOnlyList<FileRecord>>(() =>
    {
        var queue = new PriorityQueue<FileRecord, long>();
        foreach (var record in EnumerateRecords())
        {
            token.ThrowIfCancellationRequested();
            if (mediaOnly && !record.IsMedia) continue;
            if (!string.IsNullOrWhiteSpace(contains) && !record.FullPath.Contains(contains, StringComparison.CurrentCultureIgnoreCase)) continue;
            queue.Enqueue(record, record.Size);
            if (queue.Count > limit) queue.Dequeue();
        }
        return queue.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Size).ToArray();
    }, token);

    public Task<IReadOnlyList<FileRecord>> QueryByRiskAsync(RiskLevel risk, int limit = 100_000,
        CancellationToken token = default) => Task.Run<IReadOnlyList<FileRecord>>(() =>
    {
        var result = new List<FileRecord>();
        foreach (var record in EnumerateRecords())
        {
            token.ThrowIfCancellationRequested();
            if (record.Risk != risk) continue;
            result.Add(record);
            if (result.Count >= limit) break;
        }
        return result.OrderByDescending(x => x.Size).ToArray();
    }, token);

    private BinaryReader OpenReader() => new(new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read,
        1024 * 1024, FileOptions.SequentialScan), Encoding.UTF8, leaveOpen: false);

    private static IndexSummary ReadHeader(BinaryReader reader)
    {
        if (reader.ReadString() != Magic || reader.ReadInt32() != FormatVersion)
            throw new InvalidDataException("Unsupported CleanSpace index.");
        var ticks = reader.ReadInt64();
        var count = reader.ReadInt64();
        var total = reader.ReadInt64();
        var safe = reader.ReadInt64();
        var large = reader.ReadInt32();
        if (count < 0 || count > 10_000_000) throw new InvalidDataException("Invalid record count.");
        return new(count, total, safe, large, new DateTime(ticks, DateTimeKind.Utc));
    }

    private static FileRecord ReadRecord(BinaryReader reader)
    {
        var path = reader.ReadString();
        return new FileRecord
        {
            FullPath = path,
            Size = reader.ReadInt64(),
            ModifiedUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
            Risk = (RiskLevel)reader.ReadByte(),
            ReasonKey = reader.ReadString(),
            Drive = Path.GetPathRoot(path)?.TrimEnd('\\') ?? ""
        };
    }

    public sealed class IndexWriter : IDisposable
    {
        private readonly string _target;
        private readonly string _temp;
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private bool _completed;
        private long _count;
        private long _totalBytes;
        private long _safeBytes;
        private int _largeFiles;

        internal IndexWriter(string target)
        {
            _target = target;
            _temp = target + ".tmp";
            _stream = new FileStream(_temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
            WriteHeader(0, 0, 0, 0);
        }

        public void Append(IReadOnlyList<FileRecord> records)
        {
            if (_completed) throw new InvalidOperationException("Index is already complete.");
            foreach (var record in records)
            {
                _writer.Write(record.FullPath); _writer.Write(record.Size); _writer.Write(record.ModifiedUtc.Ticks);
                _writer.Write((byte)record.Risk); _writer.Write(record.ReasonKey);
                _count++; _totalBytes += record.Size;
                if (record.Risk == RiskLevel.Safe) _safeBytes += record.Size;
                if (record.Size >= 1024L * 1024 * 1024) _largeFiles++;
            }
        }

        public IndexSummary Complete()
        {
            if (_completed) throw new InvalidOperationException("Index is already complete.");
            _writer.Write("Complete"); _writer.Flush();
            _stream.Position = 0; WriteHeader(_count, _totalBytes, _safeBytes, _largeFiles);
            _writer.Flush(); _stream.Flush(true); _completed = true;
            _writer.Dispose(); _stream.Dispose();
            File.Move(_temp, _target, true);
            return new(_count, _totalBytes, _safeBytes, _largeFiles, DateTime.UtcNow);
        }

        private void WriteHeader(long count, long total, long safe, int large)
        {
            _writer.Write(Magic); _writer.Write(FormatVersion); _writer.Write(DateTime.UtcNow.Ticks);
            _writer.Write(count); _writer.Write(total); _writer.Write(safe); _writer.Write(large);
        }

        public void Dispose()
        {
            if (_completed) return;
            _writer.Dispose(); _stream.Dispose();
            try { File.Delete(_temp); } catch { }
        }
    }
}
