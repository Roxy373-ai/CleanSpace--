using CleanSpace.Models;
using Microsoft.Win32;
using System.Text;

namespace CleanSpace.Services;

public sealed class AppResidualService
{
    public IReadOnlyList<AppResidual> Find(InstalledApp app)
    {
        var results = new Dictionary<string, AppResidual>(StringComparer.OrdinalIgnoreCase);
        AddDirectory(results, app.InstallLocation, "residual.install_location");

        var appName = NormalizeName(app.Name);
        var publisher = NormalizeName(app.Publisher);
        foreach (var root in CandidateRoots())
        {
            AddExactChild(results, root, appName, "residual.app_data");
            if (publisher.Length > 0)
            {
                var publisherDirectory = FindExactChild(root, publisher);
                if (publisherDirectory is not null)
                    AddExactChild(results, publisherDirectory, appName, "residual.publisher_data");
            }
        }

        AddRegistry(results, RegistryHive.CurrentUser, RegistryView.Registry64, $@"Software\{app.Name}");
        if (string.IsNullOrWhiteSpace(app.Publisher) == false)
            AddRegistry(results, RegistryHive.CurrentUser, RegistryView.Registry64, $@"Software\{app.Publisher}\{app.Name}");

        return results.Values.OrderBy(x => x.CanRecycle ? 0 : 1).ThenBy(x => x.Location, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public bool IsStillInstalled(InstalledApp app, IReadOnlyList<InstalledApp> installed) =>
        installed.Any(candidate =>
            candidate.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase) &&
            candidate.Publisher.Equals(app.Publisher, StringComparison.OrdinalIgnoreCase) &&
            candidate.Version.Equals(app.Version, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> CandidateRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    }

    private static void AddExactChild(Dictionary<string, AppResidual> results, string root, string expected, string sourceKey)
    {
        var path = FindExactChild(root, expected);
        if (path is not null) AddDirectory(results, path, sourceKey);
    }

    private static string? FindExactChild(string root, string expected)
    {
        if (expected.Length < 3 || Directory.Exists(root) == false) return null;
        try
        {
            return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => NormalizeName(Path.GetFileName(path)).Equals(expected, StringComparison.Ordinal));
        }
        catch { return null; }
    }

    private static void AddDirectory(Dictionary<string, AppResidual> results, string? path, string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))));
            if (Directory.Exists(full) == false || IsBroadOrUnsafe(full)) return;
            if ((new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0) return;
            results.TryAdd("dir|" + full, new AppResidual
            {
                Location = full,
                SourceKey = sourceKey,
                RiskKey = "residual.risk_confirm",
                CanRecycle = true
            });
        }
        catch { }
    }

    private static bool IsBroadOrUnsafe(string path)
    {
        var roots = new[]
        {
            Path.GetPathRoot(path),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        return roots.Where(value => string.IsNullOrWhiteSpace(value) == false)
            .Any(value => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value!))
                .Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddRegistry(Dictionary<string, AppResidual> results, RegistryHive hive, RegistryView view, string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            if (key is null) return;
            var location = $@"{hive}\{subKey}";
            results.TryAdd("reg|" + location, new AppResidual
            {
                Location = location,
                SourceKey = "residual.registry",
                RiskKey = "residual.risk_registry",
                CanRecycle = false
            });
        }
        catch { }
    }

    internal static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }
}
