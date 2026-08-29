using CleanSpace.Models;

namespace CleanSpace.Services;

public sealed class RiskService
{
    private readonly string[] _safeRoots;
    private readonly string[] _protectedRoots;

    public RiskService()
    {
        static string Norm(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var temp = Path.GetTempPath();
        var roots = new List<string>
        {
            temp,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump"),
            Path.Combine(local, "D3DSCache"),
            Path.Combine(local, "CrashDumps"),
            Path.Combine(local, "NVIDIA", "DXCache"),
            Path.Combine(local, "NVIDIA", "GLCache"),
            Path.Combine(local, "AMD", "DxCache"),
            Path.Combine(local, "Microsoft", "Windows", "Explorer"),
            Path.Combine(local, "Microsoft", "Windows", "INetCache"),
            Path.Combine(local, "Microsoft", "Windows", "WER"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER"),
            Path.Combine(roaming, "Adobe", "Common", "Media Cache Files"),
            Path.Combine(roaming, "discord", "Cache"),
            Path.Combine(roaming, "discord", "Code Cache"),
            Path.Combine(roaming, "discord", "GPUCache"),
            Path.Combine(roaming, "Slack", "Cache"),
            Path.Combine(roaming, "Slack", "Code Cache"),
            Path.Combine(roaming, "Slack", "GPUCache"),
            Path.Combine(roaming, "Code", "Cache"),
            Path.Combine(roaming, "Code", "Code Cache"),
            Path.Combine(roaming, "Code", "GPUCache")
        };
        foreach (var browser in new[] { Path.Combine(local, "Google", "Chrome", "User Data"), Path.Combine(local, "Microsoft", "Edge", "User Data"), Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data") })
        foreach (var profile in new[] { "Default" }.Concat(Enumerable.Range(1, 20).Select(x => $"Profile {x}")))
        foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
            roots.Add(Path.Combine(browser, profile, cache));
        _safeRoots = roots.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Norm).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        _protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Norm).ToArray();
    }

    public (RiskLevel Risk, string ReasonKey) Classify(string path, long size, DateTime? modifiedUtc = null)
    {
        var full = Path.GetFullPath(path);
        if ((_safeRoots.Any(root => IsUnder(full, root)) || IsFirefoxCache(full)) && !IsSensitiveBrowserData(full))
            return (RiskLevel.Safe, SafeReason(full));

        if (IsUpdateDownload(full))
            return (RiskLevel.Caution, "reason.update");

        if (IsOldDownload(full, modifiedUtc))
            return (RiskLevel.Caution, "reason.old_download");

        if (_protectedRoots.Any(root => IsUnder(full, root)))
            return (RiskLevel.Blocked, "reason.system");

        if (IsPersonalPath(full))
            return (RiskLevel.Caution, "reason.personal");

        if (size >= 1024L * 1024 * 1024)
            return (RiskLevel.Caution, "reason.large");

        return (RiskLevel.Blocked, "reason.other");
    }

    public bool IsStillAllowed(FileRecord record)
    {
        if (!File.Exists(record.FullPath)) return false;
        var info = new FileInfo(record.FullPath);
        var current = Classify(record.FullPath, info.Length, info.LastWriteTimeUtc);
        return current.Risk == record.Risk && info.Length == record.Size &&
               Math.Abs((info.LastWriteTimeUtc - record.ModifiedUtc).TotalSeconds) < 2;
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitiveBrowserData(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("Cookies", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Login Data", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Web Data", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(@"\User Data\", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains(@"\Cache\", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains(@"\Code Cache\", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains(@"\GPUCache\", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeReason(string path)
    {
        if (path.Contains(@"\CrashDumps\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\Minidump\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\Windows\WER\", StringComparison.OrdinalIgnoreCase))
            return "reason.crash";
        if (Path.GetFileName(path).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase))
            return "reason.thumbnail";
        if (path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase))
            return "reason.temp";
        return "reason.cache";
    }

    private static bool IsFirefoxCache(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profiles = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        return IsUnder(path, profiles) && path.Contains(@"\cache2\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUpdateDownload(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return IsUnder(path, Path.Combine(windows, "SoftwareDistribution", "Download"));
    }

    private static bool IsOldDownload(string path, DateTime? modifiedUtc)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        return modifiedUtc.HasValue && IsUnder(path, downloads) && modifiedUtc.Value < DateTime.UtcNow.AddDays(-90);
    }
    private static bool IsPersonalPath(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return IsUnder(path, profile) && !path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase);
    }
}
