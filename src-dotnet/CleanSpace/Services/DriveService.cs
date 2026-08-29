using CleanSpace.Models;

namespace CleanSpace.Services;

public sealed class DriveOption
{
    public required string RootPath { get; init; }
    public required DriveType DriveType { get; init; }
    public required string VolumeLabel { get; init; }
    public required long TotalSize { get; init; }
    public required long AvailableFreeSpace { get; init; }
    public required bool IsSystemDrive { get; init; }

    public string DisplayName
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(VolumeLabel) ? "" : $"  {VolumeLabel}";
            return $"{RootPath.TrimEnd('\\')}{label}";
        }
    }

    public string TypeKey => IsSystemDrive ? "drive.system" : DriveType == DriveType.Removable ? "drive.removable" : "drive.fixed";
    public double UsedPercent => TotalSize <= 0 ? 0 : (TotalSize - AvailableFreeSpace) * 100d / TotalSize;
    public string UsageText => $"{SizeFormatter.Format(TotalSize - AvailableFreeSpace)} / {SizeFormatter.Format(TotalSize)}";
    public string FreeText => SizeFormatter.Format(AvailableFreeSpace);
}

public sealed class DriveService
{
    public string SystemRoot { get; } = ResolveSystemRoot();

    public IReadOnlyList<DriveOption> GetAvailableDrives()
    {
        var drives = new List<DriveOption>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                drives.Add(new DriveOption
                {
                    RootPath = EnsureRoot(drive.RootDirectory.FullName),
                    DriveType = drive.DriveType,
                    VolumeLabel = drive.VolumeLabel,
                    TotalSize = drive.TotalSize,
                    AvailableFreeSpace = drive.AvailableFreeSpace,
                    IsSystemDrive = PathsEqual(drive.RootDirectory.FullName, SystemRoot)
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return drives.OrderByDescending(x => x.IsSystemDrive)
            .ThenBy(x => x.RootPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string[] GetSystemScanRoots() => GetAvailableDrives()
        .Where(x => x.IsSystemDrive).Select(x => x.RootPath).DefaultIfEmpty(SystemRoot).ToArray();

    public string[] GetAllScanRoots() => GetAvailableDrives().Select(x => x.RootPath).ToArray();

    private static string ResolveSystemRoot()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var root = Path.GetPathRoot(windows);
        if (string.IsNullOrWhiteSpace(root)) root = Environment.GetEnvironmentVariable("SystemDrive") + "\\";
        return EnsureRoot(string.IsNullOrWhiteSpace(root) ? @"C:\" : root);
    }

    private static string EnsureRoot(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(EnsureRoot(Path.GetFullPath(left)), EnsureRoot(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
