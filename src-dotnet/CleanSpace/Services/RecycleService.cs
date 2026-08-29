using System.Runtime.InteropServices;
using System.Text;

namespace CleanSpace.Services;

public sealed record LockingProcessInfo(int ProcessId, string ApplicationName, string ServiceName, bool Restartable);
public sealed record RecycleAttempt(bool Success, bool Missing, int ErrorCode, IReadOnlyList<LockingProcessInfo> LockingProcesses);
public sealed record CloseRecycleRestartResult(bool ShutdownSucceeded, bool RestartRequested, IReadOnlyDictionary<string, RecycleAttempt> Attempts);

public sealed class RecycleService
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const int ErrorMoreData = 234;

    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "dwm", "explorer"
    };

    public RecycleAttempt TryMoveToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return new(true, true, 0, []);
        var operation = new ShFileOpStruct
        {
            WFunc = FoDelete,
            PFrom = path + '\0' + '\0',
            FFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi
        };
        var code = SHFileOperation(ref operation);
        if (code == 0 && !operation.AnyOperationsAborted) return new(true, false, 0, []);
        Thread.Sleep(120);
        return new(false, false, code, GetLockingProcesses([path]));
    }

    public IReadOnlyDictionary<string, RecycleAttempt> TryMoveToRecycleBinBatch(IReadOnlyList<string> paths)
    {
        var results = new Dictionary<string, RecycleAttempt>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in paths.Where(x => File.Exists(x) || Directory.Exists(x)).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(128))
        {
            var operation = new ShFileOpStruct
            {
                WFunc = FoDelete,
                PFrom = string.Join('\0', chunk) + "\0\0",
                FFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi
            };
            var code = SHFileOperation(ref operation);
            if (code == 0 && !operation.AnyOperationsAborted)
            {
                foreach (var path in chunk) results[path] = new(true, false, 0, []);
                continue;
            }
            // A failed chunk is uncommon; fall back to per-file silent checks so locked items are identified.
            foreach (var path in chunk) results[path] = TryMoveToRecycleBin(path);
        }
        foreach (var path in paths.Where(x => !results.ContainsKey(x)))
            results[path] = new(true, true, 0, []);
        return results;
    }

    public IReadOnlyList<LockingProcessInfo> GetLockingProcesses(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];
        var key = new StringBuilder(33);
        var start = RmStartSession(out var session, 0, key);
        if (start != 0) return [];
        try
        {
            var files = paths.Where(x => File.Exists(x) || Directory.Exists(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var register = files.Length == 0 ? -1 : RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null);
            if (files.Length == 0 || register != 0) return [];
            uint needed = 0, count = 0, reasons = 0;
            var code = RmGetList(session, out needed, ref count, null, ref reasons);
            if (code != ErrorMoreData || needed == 0) return [];
            var info = new RmProcessInfo[needed];
            count = needed;
            code = RmGetList(session, out needed, ref count, info, ref reasons);
            if (code != 0) return [];
            return info.Take((int)count).Select(x => new LockingProcessInfo(
                x.Process.ProcessId, x.ApplicationName ?? "", x.ServiceShortName ?? "", x.Restartable)).ToArray();
        }
        finally { RmEndSession(session); }
    }

    public bool CanCloseApplications(IEnumerable<LockingProcessInfo> processes) =>
        processes.Any() && processes.All(x => x.ProcessId != Environment.ProcessId &&
            !ProtectedProcesses.Contains(SafeProcessName(x.ProcessId)));

    public CloseRecycleRestartResult CloseRecycleAndRestart(IReadOnlyList<string> paths, bool force)
    {
        var locks = GetLockingProcesses(paths);
        if (!CanCloseApplications(locks)) return new(false, false, new Dictionary<string, RecycleAttempt>());
        var key = new StringBuilder(33);
        if (RmStartSession(out var session, 0, key) != 0) return new(false, false, new Dictionary<string, RecycleAttempt>());
        try
        {
            var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0 || RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0)
                return new(false, false, new Dictionary<string, RecycleAttempt>());
            var shutdownSucceeded = RmShutdown(session, force ? 1u : 0u, null) == 0;
            if (!shutdownSucceeded) return new(false, false, new Dictionary<string, RecycleAttempt>());
            Thread.Sleep(800);
            var attempts = files.ToDictionary(x => x, TryMoveToRecycleBin, StringComparer.OrdinalIgnoreCase);
            var restartRequested = RmRestart(session, 0, null) == 0;
            return new(true, restartRequested, attempts);
        }
        finally { RmEndSession(session); }
    }

    private static string SafeProcessName(int processId)
    {
        try { return System.Diagnostics.Process.GetProcessById(processId).ProcessName; }
        catch { return ""; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint WFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string PFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? PTo;
        public ushort FFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess { public int ProcessId; public NativeFileTime ProcessStartTime; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint LowDateTime; public uint HighDateTime; }
    private enum RmAppType { UnknownApp, MainWindow, OtherWindow, Service, Explorer, Console, Critical = 1000 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public RmAppType ApplicationType;
        public uint AppStatus;
        public uint TerminalSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    private delegate void RmWriteStatusCallback(uint percentComplete);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref ShFileOpStruct operation);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] string[] fileNames, uint applicationCount, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] RmUniqueProcess[]? applications, uint serviceCount, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] string[]? serviceNames);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmGetList(uint sessionHandle, out uint processInfoNeeded, ref uint processInfo, [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] RmProcessInfo[]? affectedApps, ref uint rebootReasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmShutdown(uint sessionHandle, uint actionFlags, RmWriteStatusCallback? statusCallback);
    [DllImport("rstrtmgr.dll")] private static extern int RmRestart(uint sessionHandle, uint restartFlags, RmWriteStatusCallback? statusCallback);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint sessionHandle);
}
