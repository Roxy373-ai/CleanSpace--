using CleanSpace.Models;
using Microsoft.Win32;

namespace CleanSpace.Services;

public sealed class InstalledAppService
{
    private static readonly (RegistryHive Hive, RegistryView View)[] RegistryTargets =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
        (RegistryHive.CurrentUser, RegistryView.Registry32)
    ];

    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        var result = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in RegistryTargets)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.View);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var subName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(subName);
                    try
                    {
                    var name = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name) || Convert.ToInt32(key?.GetValue("SystemComponent") ?? 0) == 1) continue;
                    var app = new InstalledApp
                    {
                        Name = name.Trim(),
                        Publisher = key?.GetValue("Publisher") as string ?? "",
                        Version = key?.GetValue("DisplayVersion") as string ?? "",
                        InstallLocation = key?.GetValue("InstallLocation") as string ?? "",
                        UninstallCommand = key?.GetValue("UninstallString") as string ?? "",
                        IconPath = key?.GetValue("DisplayIcon") as string ?? "",
                        Icon = AppIconService.Load(key?.GetValue("DisplayIcon") as string, key?.GetValue("InstallLocation") as string),
                        EstimatedSize = Convert.ToInt64(key?.GetValue("EstimatedSize") ?? 0) * 1024
                    };
                    result.TryAdd($"{app.Name}|{app.Version}|{app.Publisher}", app);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return result.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
