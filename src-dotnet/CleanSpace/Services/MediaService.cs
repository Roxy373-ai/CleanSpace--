using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanSpace.Models;

namespace CleanSpace.Services;

public sealed class MediaService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    public bool IsImage(FileRecord file) => ImageExtensions.Contains(file.Extension);
 
    public ImageSource? LoadPreview(string path, int decodeWidth = 320)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    public async Task<bool> CheckAsync(FileRecord file, CancellationToken token)
    {
        if (ImageExtensions.Contains(file.Extension))
            return await Task.Run(() => LoadPreview(file.FullPath, 64) is not null, token);

        var ffprobe = FindFfprobe();
        if (ffprobe is null) return true;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffprobe,
                $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{file.FullPath}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }
        };
        try
        {
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            return false;
        }
    }
    public async Task<IReadOnlyList<DuplicateGroup>> FindSimilarImagesAsync(
        IEnumerable<FileRecord> source, IProgress<DuplicateProgress>? progress, CancellationToken token)
    {
        var candidates = source.Where(IsImage).Where(file => file.Size >= 8 * 1024 && File.Exists(file.FullPath)).ToArray();
        var signatures = new ConcurrentBag<SimilarSignature>();
        var completed = 0;
        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
            (file, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var signature = TryCreateSignature(file);
                if (signature is not null) signatures.Add(signature);
                var done = Interlocked.Increment(ref completed);
                if (done % 10 == 0 || done == candidates.Length)
                    progress?.Report(new(done, candidates.Length, file.FullPath));
                return ValueTask.CompletedTask;
            });

        var items = signatures.ToArray();
        var parent = Enumerable.Range(0, items.Length).ToArray();
        int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }
        void Union(int left, int right)
        {
            left = Find(left); right = Find(right);
            if (left != right) parent[right] = left;
        }

        foreach (var bucket in Enumerable.Range(0, items.Length).GroupBy(index => items[index].AspectBucket))
        {
            var indexes = bucket.ToArray();
            for (var left = 0; left < indexes.Length; left++)
            for (var right = left + 1; right < indexes.Length; right++)
            {
                token.ThrowIfCancellationRequested();
                var a = items[indexes[left]];
                var b = items[indexes[right]];
                if (Math.Abs(a.AverageLuma - b.AverageLuma) <= 12 &&
                    BitOperations.PopCount(a.Hash ^ b.Hash) <= 6)
                    Union(indexes[left], indexes[right]);
            }
        }

        return Enumerable.Range(0, items.Length)
            .GroupBy(Find)
            .Select(group => group.Select(index => items[index].File).OrderBy(file => file.FullPath).ToArray())
            .Where(group => group.Length > 1)
            .Select((group, index) => new DuplicateGroup { Hash = $"similar:{index}", Files = group })
            .OrderByDescending(group => group.ReclaimableBytes)
            .ToArray();
    }

    private static SimilarSignature? TryCreateSignature(FileRecord file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.UriSource = new Uri(file.FullPath, UriKind.Absolute);
            image.EndInit();
            if (image.PixelWidth == 0 || image.PixelHeight == 0) return null;

            var resized = new TransformedBitmap(image,
                new ScaleTransform(9d / image.PixelWidth, 8d / image.PixelHeight));
            var gray = new FormatConvertedBitmap(resized, PixelFormats.Gray8, null, 0);
            var pixels = new byte[9 * 8];
            gray.CopyPixels(pixels, 9, 0);
            ulong hash = 0;
            var bit = 0;
            long total = 0;
            for (var row = 0; row < 8; row++)
            {
                for (var column = 0; column < 9; column++) total += pixels[row * 9 + column];
                for (var column = 0; column < 8; column++)
                {
                    if (pixels[row * 9 + column] >= pixels[row * 9 + column + 1])
                        hash |= 1UL << bit;
                    bit++;
                }
            }

            var aspect = (int)Math.Round((double)image.PixelWidth / image.PixelHeight * 20);
            return new SimilarSignature(file, hash, (byte)(total / pixels.Length), aspect);
        }
        catch
        {
            return null;
        }
    }

    private sealed record SimilarSignature(FileRecord File, ulong Hash, byte AverageLuma, int AspectBucket);

    private static string? FindFfprobe()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
        if (File.Exists(beside)) return beside;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Select(x => Path.Combine(x, "ffprobe.exe")).FirstOrDefault(File.Exists);
    }
}
