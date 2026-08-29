using System.Diagnostics;
using System.Text.RegularExpressions;
using CleanSpace.Models;

namespace CleanSpace.Services;

public sealed record ShellActionResult(bool Success, string MessageKey)
{
    public static ShellActionResult Ok() => new(true, "status.ready");
    public static ShellActionResult Fail(string key) => new(false, key);
}

public static class ShellService
{
    public static ShellActionResult TryOpenFile(string path)
    {
        if (!File.Exists(path)) return ShellActionResult.Fail("shell.missing");
        try
        {
            return Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }) is null
                ? ShellActionResult.Fail("shell.open_failed") : ShellActionResult.Ok();
        }
        catch { return ShellActionResult.Fail("shell.open_failed"); }
    }

    public static ShellActionResult TryLocateFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return ShellActionResult.Fail("shell.missing");
        try
        {
            return Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }) is null
                ? ShellActionResult.Fail("shell.locate_failed") : ShellActionResult.Ok();
        }
        catch { return ShellActionResult.Fail("shell.locate_failed"); }
    }

    public static ShellActionResult TryOpenInstalledApps()
    {
        try
        {
            return Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }) is null
                ? ShellActionResult.Fail("apps.open_settings_failed") : ShellActionResult.Ok();
        }
        catch { return ShellActionResult.Fail("apps.open_settings_failed"); }
    }

    public static ShellActionResult TryLaunchUninstaller(InstalledApp app)
    {
        if (string.IsNullOrWhiteSpace(app.UninstallCommand)) return TryOpenInstalledApps();
        var command = Environment.ExpandEnvironmentVariables(app.UninstallCommand.Trim());
        if (IsSilentUninstallCommand(command)) return TryOpenInstalledApps();

        var (file, args) = SplitCommand(command);
        if (string.IsNullOrWhiteSpace(file)) return TryOpenInstalledApps();
        try
        {
            return Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }) is null
                ? ShellActionResult.Fail("apps.uninstall_failed") : ShellActionResult.Ok();
        }
        catch { return ShellActionResult.Fail("apps.uninstall_failed"); }
    }

    private static (string File, string Args) SplitCommand(string command)
    {
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? (command[1..end], command[(end + 1)..].Trim()) : ("", "");
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0) return (command[..(exe + 4)].Trim(), command[(exe + 4)..].Trim());
        var firstSpace = command.IndexOf(' ');
        return firstSpace > 0 ? (command[..firstSpace], command[(firstSpace + 1)..]) : (command, "");
    }

    public static bool IsSilentUninstallCommand(string command) =>
        Regex.IsMatch(command, @"(?:^|\s)(?:/quiet|/silent|/verysilent|/qn|/q|/s|-s|-silent|--silent)(?:\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
