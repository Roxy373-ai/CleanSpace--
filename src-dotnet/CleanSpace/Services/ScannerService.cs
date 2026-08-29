using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using CleanSpace.Models;

namespace CleanSpace.Services;

public sealed record ScanUpdate(IReadOnlyList<FileRecord> Batch, long FileCount, long TotalBytes,
                                string CurrentPath, int ErrorCount, TimeSpan Elapsed, bool Finished, bool Cancelled);

public sealed class ScannerService : IDisposable
{
    private readonly RiskService _risk;
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public bool IsPaused => IsRunning && !_pauseGate.IsSet;

    public ScannerService(RiskService risk) => _risk = risk;

    public Task ScanAsync(IEnumerable<string> roots, IProgress<ScanUpdate> progress)
    {
        if (IsRunning) throw new InvalidOperationException("A scan is already running.");
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _pauseGate.Set();
        return Task.Run(() => ScanCore(roots, progress, _cts.Token));
    }

    public void TogglePause()
    {
        if (!IsRunning) return;
        if (_pauseGate.IsSet) _pauseGate.Reset(); else _pauseGate.Set();
    }

    public void Cancel()
    {
        _pauseGate.Set();
        _cts?.Cancel();
    }

    private void ScanCore(IEnumerable<string> roots, IProgress<ScanUpdate> progress, CancellationToken token)
    {
        var batch = new List<FileRecord>(256);
        long count = 0, bytes = 0;
        var errors = 0;
        var current = "";
        var timer = Stopwatch.StartNew();
        var lastReport = Stopwatch.StartNew();
        var cancelled = false;

        try
        {
            var stack = new Stack<string>(roots.Where(Directory.Exists).Reverse());
            while (stack.Count > 0)
            {
                var root = stack.Pop();
                _pauseGate.Wait(token);
                token.ThrowIfCancellationRequested();
                try
                {
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip = 0
                    };
                    var entries = new FileSystemEnumerable<EntryData>(root, (ref FileSystemEntry entry) =>
                    {
                        var path = entry.ToFullPath();
                        var attributes = entry.Attributes;
                        return new EntryData(path, entry.IsDirectory,
                            (attributes & FileAttributes.ReparsePoint) != 0,
                            entry.IsDirectory ? 0 : entry.Length,
                            entry.LastWriteTimeUtc.UtcDateTime);
                    }, options);

                    foreach (var entry in entries)
                    {
                        _pauseGate.Wait(token);
                        token.ThrowIfCancellationRequested();
                        if (entry.IsReparsePoint) continue;
                        if (entry.IsDirectory) { stack.Push(entry.Path); continue; }
                        var classification = _risk.Classify(entry.Path, entry.Length, entry.ModifiedUtc);
                        var file = new FileRecord
                        {
                            FullPath = entry.Path,
                            Size = entry.Length,
                            ModifiedUtc = entry.ModifiedUtc,
                            Drive = Path.GetPathRoot(entry.Path)?.TrimEnd('\\') ?? "",
                            Risk = classification.Risk,
                            ReasonKey = classification.ReasonKey
                        };
                        batch.Add(file);
                        count++;
                        bytes += file.Size;
                        current = file.FullPath;
                        if (batch.Count >= 256 || lastReport.ElapsedMilliseconds >= 150)
                        {
                            progress.Report(new(batch.ToArray(), count, bytes, current, errors, timer.Elapsed, false, false));
                            batch.Clear();
                            lastReport.Restart();
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { errors++; }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            if (batch.Count > 0)
                progress.Report(new(batch.ToArray(), count, bytes, current, errors, timer.Elapsed, false, cancelled));
            progress.Report(new(Array.Empty<FileRecord>(), count, bytes, current, errors, timer.Elapsed, true, cancelled));
            IsRunning = false;
            _pauseGate.Set();
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _pauseGate.Dispose();
    }

    private sealed record EntryData(string Path, bool IsDirectory, bool IsReparsePoint, long Length, DateTime ModifiedUtc);
}
