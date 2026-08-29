using CleanSpace.Models;
using CleanSpace.Services;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args.Length > 1 && args[0] == "--hold")
{
    var lockPath = args[1];
    var marker = args.Length > 3 && args[2] == "--marker" ? args[3] : "";
    if (!string.IsNullOrWhiteSpace(marker)) File.AppendAllText(marker, $"started {Environment.ProcessId}{Environment.NewLine}");
    var restartPrefix = string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
        ? $"\"{Assembly.GetExecutingAssembly().Location}\" " : "";
    var restartArguments = $"{restartPrefix}--hold \"{lockPath}\" --marker \"{marker}\"";
    NativeRestart.RegisterApplicationRestart(restartArguments, 0);
    try
    {
        using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("LOCKED"); Console.Out.Flush();
        await Task.Delay(TimeSpan.FromMinutes(2));
    }
    catch (FileNotFoundException) { }
    return 0;
}

if (args.Length > 0 && args[0] == "--benchmark")
{
    var roots = args.Skip(1).Where(Directory.Exists).ToArray();
    if (roots.Length == 0) roots = [@"C:\", @"D:\"];
    var riskForBenchmark = new RiskService();
    using var scannerForBenchmark = new ScannerService(riskForBenchmark);
    var benchmarkIndex = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLEANSPACE_DATA_DIR")) ? null : new IndexStore();
    using var benchmarkWriter = benchmarkIndex?.BeginWrite();
    ScanUpdate? finalBenchmark = null;
    long lastPrinted = 0;
    var benchmarkProgress = new InlineProgress<ScanUpdate>(update =>
    {
        if (update.Batch.Count > 0) benchmarkWriter?.Append(update.Batch);
        if (update.FileCount - lastPrinted >= 100_000 || update.Finished)
        {
            Console.WriteLine($"PROGRESS files={update.FileCount} bytes={update.TotalBytes} errors={update.ErrorCount} elapsed={update.Elapsed.TotalSeconds:0.000}");
            lastPrinted = update.FileCount;
        }
        if (update.Finished) finalBenchmark = update;
    });
    var process = Process.GetCurrentProcess();
    var cpuStart = process.TotalProcessorTime;
    var wall = Stopwatch.StartNew();
    await scannerForBenchmark.ScanAsync(roots, benchmarkProgress);
    wall.Stop(); process.Refresh();
    var indexSummary = benchmarkWriter?.Complete();
    Console.WriteLine($"BENCHMARK files={finalBenchmark?.FileCount ?? 0} bytes={finalBenchmark?.TotalBytes ?? 0} errors={finalBenchmark?.ErrorCount ?? 0} wall_seconds={wall.Elapsed.TotalSeconds:0.000} cpu_seconds={(process.TotalProcessorTime-cpuStart).TotalSeconds:0.000} peak_working_set={process.PeakWorkingSet64} indexed={indexSummary?.FileCount ?? 0}");
    return 0;
}

if (args.Length > 0 && args[0] == "--analyze-index")
{
    var index = new IndexStore();
    var summary = index.ReadSummary() ?? throw new InvalidDataException("No valid CleanSpace index.");
    long safeCount = 0, cautionCount = 0, blockedCount = 0, safeBytes = 0;
    var timer = Stopwatch.StartNew();
    foreach (var file in index.EnumerateRecords())
    {
        switch (file.Risk)
        {
            case RiskLevel.Safe: safeCount++; safeBytes += file.Size; break;
            case RiskLevel.Caution: cautionCount++; break;
            default: blockedCount++; break;
        }
    }
    Console.WriteLine($"INDEX files={summary.FileCount} bytes={summary.TotalBytes} safe_files={safeCount} safe_bytes={safeBytes} caution_files={cautionCount} blocked_files={blockedCount} read_seconds={timer.Elapsed.TotalSeconds:0.000}");
    if (args.Contains("--duplicates"))
    {
        var service = new DuplicateService();
        var last = 0;
        var progress = new InlineProgress<DuplicateProgress>(x =>
        {
            if (x.Completed - last >= 1000) { Console.WriteLine($"HASH {x.Completed}/{x.Total}"); last = x.Completed; }
        });
        timer.Restart();
        var groups = await service.FindExactAsync(index.EnumerateRecords(), progress, CancellationToken.None);
        Console.WriteLine($"DUPLICATES groups={groups.Count} files={groups.Sum(x => x.Files.Count)} reclaimable_bytes={groups.Sum(x => x.ReclaimableBytes)} seconds={timer.Elapsed.TotalSeconds:0.000}");
    }
    return 0;
}

var failures = new List<string>();
var passed = 0;

void Assert(bool condition, string name)
{
    if (condition) { passed++; Console.WriteLine($"PASS {name}"); }
    else { failures.Add(name); Console.WriteLine($"FAIL {name}"); }
}

var root = Path.Combine(Path.GetTempPath(), "CleanSpace-SelfTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var same = Enumerable.Repeat((byte)0x5A, 2 * 1024 * 1024).ToArray();
    var different = Enumerable.Repeat((byte)0x29, 2 * 1024 * 1024).ToArray();
    var a = Path.Combine(root, "中文-相同.bin");
    var b = Path.Combine(root, "한국어-같음.bin");
    var c = Path.Combine(root, "different.bin");
    await File.WriteAllBytesAsync(a, same); await File.WriteAllBytesAsync(b, same); await File.WriteAllBytesAsync(c, different);

    var risk = new RiskService();
    var safe = risk.Classify(a, same.Length);
    Assert(safe.Risk == RiskLevel.Safe, "strict temp path is safe cache");
    var protectedFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll");
    Assert(risk.Classify(protectedFile, 1024).Risk == RiskLevel.Blocked, "Windows system file is blocked");

    var scanner = new ScannerService(risk);
    ScanUpdate? final = null;
    var scanned = new List<FileRecord>();
    var progress = new InlineProgress<ScanUpdate>(x => { scanned.AddRange(x.Batch); if (x.Finished) final = x; });
    await scanner.ScanAsync([root], progress);
    Assert(final is { Finished: true, Cancelled: false }, "scanner finishes");
    Assert(scanned.Count == 3, "scanner returns all files");
    Assert(scanned.All(x => x.FullPath.Contains("CleanSpace-SelfTest-")), "scanner preserves full paths");

    var duplicateService = new DuplicateService();
    var groups = await duplicateService.FindExactAsync(scanned, null, CancellationToken.None);
    Assert(groups.Count == 1 && groups[0].Files.Count == 2, "exact duplicate grouping");
    Assert(groups[0].ReclaimableBytes == same.Length, "duplicate reclaimable size");

    var similarPng = Path.Combine(root, "similar-a.png");
    var similarBmp = Path.Combine(root, "similar-b.bmp");
    var differentPng = Path.Combine(root, "different-image.png");
    WritePatternImage(similarPng, 0, useBmp: false);
    WritePatternImage(similarBmp, 0, useBmp: true);
    WritePatternImage(differentPng, 100, useBmp: false);
    var imageRecords = new[] { similarPng, similarBmp, differentPng }.Select(path => new FileRecord
    {
        FullPath = path,
        Size = new FileInfo(path).Length,
        ModifiedUtc = File.GetLastWriteTimeUtc(path),
        Drive = Path.GetPathRoot(path) ?? "",
        Risk = RiskLevel.Caution,
        ReasonKey = "reason.personal"
    }).ToArray();
    var similarGroups = await new MediaService().FindSimilarImagesAsync(imageRecords, null, CancellationToken.None);
    Assert(similarGroups.Count == 1 &&
           similarGroups[0].Files.Select(file => file.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
               .SetEquals([similarPng, similarBmp]),
        "perceptual image matching groups equivalent encodings without the different image");

    var dataRoot = Path.Combine(root, "index-data");
    Environment.SetEnvironmentVariable("CLEANSPACE_DATA_DIR", dataRoot);
    var index = new IndexStore();
    using (var writer = index.BeginWrite()) { writer.Append(scanned); writer.Complete(); }
    var loaded = index.EnumerateRecords().ToArray();
    Assert(loaded.Length == scanned.Count, "binary index round trip");
    Assert(loaded.Any(x => x.FullPath == b && x.Size == same.Length), "binary index preserves Unicode path and size");

    var changed = scanned[0];
    await File.AppendAllTextAsync(changed.FullPath, "changed");
    Assert(!risk.IsStillAllowed(changed), "changed file fails pre-delete validation");

    var localizer = new Localizer();
    var oldDownload = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "old-file.zip");
    Assert(risk.Classify(oldDownload, 1024, DateTime.UtcNow.AddDays(-91)) == (RiskLevel.Caution, "reason.old_download"),
        "old download requires confirmation");
    var updateDownload = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download", "update.bin");
    Assert(risk.Classify(updateDownload, 1024) == (RiskLevel.Caution, "reason.update"),
        "update download is never auto-selected");
    var firefoxCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mozilla", "Firefox", "Profiles", "profile.default", "cache2", "entries", "cache-file");
    Assert(risk.Classify(firefoxCache, 1024).Risk == RiskLevel.Safe, "Firefox cache is recognized");
    Assert(ShellService.TryOpenFile(Path.Combine(root, "missing.file")).Success == false,
        "missing file open returns an error instead of throwing");
    Assert(ShellService.IsSilentUninstallCommand(@"uninstall.exe /S"),
        "short silent uninstall switch is detected");
    Assert(ShellService.IsSilentUninstallCommand(@"msiexec.exe /x {GUID} /qn"),
        "MSI silent uninstall switch is detected");
    Assert(!ShellService.IsSilentUninstallCommand(@"uninstall.exe /interactive"),
        "interactive uninstall command is not misclassified");

    var drives = new DriveService();
    Assert(Path.IsPathRooted(drives.SystemRoot), "system drive comes from Windows environment");
    Assert(drives.GetSystemScanRoots().All(Directory.Exists), "system scan contains only available roots");
    Assert(drives.GetAllScanRoots().All(Directory.Exists), "all-drive scan contains only available roots");

    var icon = AppIconService.Load(Environment.ProcessPath, null);
    Assert(icon is not null, "installed-app icon extraction returns a Windows icon");

    var residualDirectory = Path.Combine(root, "Sample App");
    Directory.CreateDirectory(residualDirectory);
    var removedApp = new InstalledApp
    {
        Name = "Sample App",
        Publisher = "Sample Publisher",
        Version = "1.0",
        InstallLocation = residualDirectory
    };
    var residualService = new AppResidualService();
    var residuals = residualService.Find(removedApp);
    Assert(residuals.Any(x => x.Location == residualDirectory && x.CanRecycle),
        "recorded install location is offered as a confirm-only residual");
    var broadApp = new InstalledApp
    {
        Name = "Unsafe",
        InstallLocation = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    };
    Assert(residualService.Find(broadApp).All(x => x.Location != broadApp.InstallLocation),
        "broad user and system roots are never offered as residuals");
    Assert(residualService.IsStillInstalled(removedApp, [removedApp]),

        "residual scan waits until the exact app is no longer installed");
    localizer.SetLocale(LocaleCode.KoKr);
    Assert(localizer["risk.safe"].Contains("삭제"), "Korean translation");
    Assert(localizer["apps.confirm_uninstall"] != "apps.confirm_uninstall", "new Korean uninstall prompt exists");
    Assert(localizer["cleanup.operation_failed"] != "cleanup.operation_failed", "new Korean cleanup failure text exists");
    Assert(!localizer["app.title"].Contains('—') && !localizer["app.title"].Contains('–'),
        "Korean title has no AI-style dash");
    localizer.SetLocale(LocaleCode.ZhCn);
    Assert(localizer["risk.safe"].Contains("安全"), "Chinese translation");

    var lockFile = Path.Combine(root, "locked-test.tmp");
    var restartMarker = Path.Combine(root, "restart-marker.txt");
    await File.WriteAllTextAsync(lockFile, "locked content");
    var childPrefix = string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
        ? $"\"{Assembly.GetExecutingAssembly().Location}\" " : "";
    var childStart = new ProcessStartInfo(Environment.ProcessPath!, $"{childPrefix}--hold \"{lockFile}\" --marker \"{restartMarker}\"")
    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
    using var child = Process.Start(childStart)!;
    var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
    Assert(ready == "LOCKED", "lock helper starts");
    var recycle = new RecycleService();
    var blockedAttempt = recycle.TryMoveToRecycleBin(lockFile);
    Assert(!blockedAttempt.Success, "locked recycle fails without a Windows blocking dialog");
    Assert(blockedAttempt.LockingProcesses.Any(x => x.ProcessId == child.Id), "locking process is identified");
    var forced = recycle.CloseRecycleAndRestart([lockFile], force: true);
    Assert(forced.ShutdownSucceeded, "force close uses Restart Manager");
    Assert(forced.Attempts.TryGetValue(lockFile, out var forcedAttempt) && forcedAttempt.Success, "locked test file moves to Recycle Bin after force close");
    Assert(!File.Exists(lockFile), "forced cleanup does not leave the locked file in place");
    var restartDeadline = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < restartDeadline && (!File.Exists(restartMarker) || File.ReadAllLines(restartMarker).Length < 2)) await Task.Delay(200);
    Assert(File.Exists(restartMarker) && File.ReadAllLines(restartMarker).Length >= 2, "Restart Manager automatically reopens registered application");
}
finally
{
    Environment.SetEnvironmentVariable("CLEANSPACE_DATA_DIR", null);
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine($"RESULT passed={passed} failed={failures.Count}");
return failures.Count == 0 ? 0 : 1;

static void WritePatternImage(string path, int offset, bool useBmp)
{
    const int width = 128;
    const int height = 128;
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var value = (byte)Math.Min(255, (((x * 1973) ^ (y * 9277) ^ (x * y * 31)) & 127) + offset);
        var index = (y * width + x) * 4;
        pixels[index] = value;
        pixels[index + 1] = (byte)Math.Min(255, value + 20);
        pixels[index + 2] = (byte)Math.Max(0, value - 10);
        pixels[index + 3] = 255;
    }

    var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
    bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
    BitmapEncoder encoder = useBmp ? new BmpBitmapEncoder() : new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

file sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

file static class NativeRestart
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegisterApplicationRestart(string commandLineArgs, int flags);
}
