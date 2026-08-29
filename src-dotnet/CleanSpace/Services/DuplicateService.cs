using System.Collections.Concurrent;
using System.Security.Cryptography;
using CleanSpace.Models;

namespace CleanSpace.Services;

public sealed record DuplicateProgress(int Completed, int Total, string CurrentPath);

public sealed class DuplicateService
{
    public async Task<IReadOnlyList<DuplicateGroup>> FindExactAsync(
        IEnumerable<FileRecord> source, IProgress<DuplicateProgress>? progress, CancellationToken token)
    {
        var firstBySize = new Dictionary<long, FileRecord>();
        var duplicateSizes = new Dictionary<long, List<FileRecord>>();
        foreach (var file in source)
        {
            token.ThrowIfCancellationRequested();
            if (file.Size < 1024 * 1024 || !File.Exists(file.FullPath)) continue;
            if (duplicateSizes.TryGetValue(file.Size, out var group)) group.Add(file);
            else if (firstBySize.Remove(file.Size, out var first)) duplicateSizes[file.Size] = [first, file];
            else firstBySize[file.Size] = file;
        }
        var candidates = duplicateSizes.Values.SelectMany(x => x).ToArray();
        var partial = new ConcurrentDictionary<string, ConcurrentBag<FileRecord>>(StringComparer.Ordinal);
        var done = 0;

        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
            async (file, ct) =>
            {
                try
                {
                    var key = $"{file.Size}:{await HashPartialAsync(file.FullPath, ct)}";
                    partial.GetOrAdd(key, _ => []).Add(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                var current = Interlocked.Increment(ref done);
                if (current % 20 == 0) progress?.Report(new(current, candidates.Length, file.FullPath));
            });

        var fullCandidates = partial.Values.Where(g => g.Count > 1).SelectMany(g => g).ToArray();
        var exact = new ConcurrentDictionary<string, ConcurrentBag<FileRecord>>(StringComparer.Ordinal);
        done = 0;
        await Parallel.ForEachAsync(fullCandidates,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
            async (file, ct) =>
            {
                try
                {
                    var hash = await HashFullAsync(file.FullPath, ct);
                    exact.GetOrAdd($"{file.Size}:{hash}", _ => []).Add(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                var current = Interlocked.Increment(ref done);
                if (current % 10 == 0) progress?.Report(new(current, fullCandidates.Length, file.FullPath));
            });

        return exact.Where(x => x.Value.Count > 1)
            .Select(x => new DuplicateGroup { Hash = x.Key, Files = x.Value.OrderBy(f => f.FullPath).ToArray() })
            .OrderByDescending(x => x.ReclaimableBytes).ToArray();
    }

    private static async Task<string> HashPartialAsync(string path, CancellationToken token)
    {
        const int chunk = 64 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            chunk, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[chunk];
        var read = await stream.ReadAsync(buffer, token);
        hash.AppendData(buffer, 0, read);
        if (stream.Length > chunk)
        {
            stream.Seek(Math.Max(chunk, stream.Length - chunk), SeekOrigin.Begin);
            read = await stream.ReadAsync(buffer, token);
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> HashFullAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash);
    }
}
