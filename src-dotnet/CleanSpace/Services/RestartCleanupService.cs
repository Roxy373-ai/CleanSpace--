using System.Runtime.InteropServices;

namespace CleanSpace.Services;

public sealed record RestartScheduleResult(IReadOnlyList<string> Scheduled, IReadOnlyDictionary<string, int> Failed);

public sealed class RestartCleanupService
{
    private const uint MovefileDelayUntilReboot = 0x00000004;
    private const uint MovefileReplaceExisting = 0x00000001;

    public RestartScheduleResult ScheduleDeleteOnNextRestart(IEnumerable<string> paths)
    {
        var scheduled = new List<string>();
        var failed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) { scheduled.Add(path); continue; }
            if (MoveFileEx(path, null, MovefileDelayUntilReboot | MovefileReplaceExisting)) scheduled.Add(path);
            else failed[path] = Marshal.GetLastWin32Error();
        }
        return new(scheduled, failed);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
