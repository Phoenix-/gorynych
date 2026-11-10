// Compiled into both Gorynych.Service (the stdio bridge, and the job check at start) and
// Gorynych.Outside, which start the service the same way.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Gorynych.Outside;

/// <summary>Starting the service so that it outlives whoever started it, and telling whether it may not.</summary>
internal static unsafe partial class DetachedProcess
{
    /// <summary>
    /// Starts <paramref name="exe"/> so that it outlives the caller and its host: no inherited
    /// handles (a host would otherwise wait on its stdout), no console, a new process group, and on
    /// Windows out of the caller's job object when the job allows it.
    /// </summary>
    /// <returns>False when the process had to stay in the caller's job, because the job forbids
    /// breaking away: closing the host may then kill it.</returns>
    public static bool Start(string exe, IReadOnlyList<string> args) => Start(exe, args, out _);

    /// <param name="processId">The started process's ID.</param>
    public static bool Start(string exe, IReadOnlyList<string> args, out int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} did not start");
            p.StandardInput.Close();
            processId = p.Id;
            return true;
        }
        var commandLine = Quote(exe) + " " + string.Join(' ', args.Select(Quote));
        const uint detached = 0x00000008, newGroup = 0x00000200, breakaway = 0x01000000, unicodeEnv = 0x00000400;
        if (StartWindows(commandLine, detached | newGroup | breakaway | unicodeEnv, out processId)) return true;
        if (StartWindows(commandLine, detached | newGroup | unicodeEnv, out processId)) return false;
        throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// When this process runs in a job object that kills its processes when the job closes and
    /// does not let them break away, a sentence that says so; otherwise null.
    /// </summary>
    public static string? KillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!IsProcessInJob(GetCurrentProcess(), 0, out var inJob) || !inJob) return null;
        BasicLimitInformation info;
        if (!QueryInformationJobObject(0, 2 /* JobObjectBasicLimitInformation */, &info, (uint)sizeof(BasicLimitInformation), null)) return null;
        const uint killOnClose = 0x2000, breakawayOk = 0x800, silentBreakawayOk = 0x1000;
        if ((info.LimitFlags & killOnClose) == 0) return null;
        var breakaway = (info.LimitFlags & (breakawayOk | silentBreakawayOk)) != 0;
        return "this process runs in a job object that kills its processes when the job closes" +
            (breakaway ? "" : " and lets none break away") + ", so closing whatever started it may stop it";
    }

    /// <summary>
    /// Lets go of a console that this process has to itself, as Task Scheduler gives a console
    /// program it starts in the user's session (the logon task). Its window would stay open, and
    /// closing it would stop the service with every session; a detached start has no console at
    /// all. A console shared with the terminal the person ran the service from is kept.
    /// </summary>
    /// <returns>True when it let one go.</returns>
    public static bool ReleaseOwnConsole()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var ids = stackalloc uint[2];
        return GetConsoleProcessList(ids, 2) == 1 && FreeConsole();
    }

    /// <summary>Quotes an argument the way the Microsoft C runtime splits a command line.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\r', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
            if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }

    static bool StartWindows(string commandLine, uint flags, out int processId)
    {
        var si = new StartupInfo { cb = sizeof(StartupInfo) };
        var cmd = (commandLine + '\0').ToCharArray();
        ProcessInformation pi;
        processId = 0;
        fixed (char* p = cmd)
        {
            if (!CreateProcessW(null, p, 0, 0, 0, flags, null, null, &si, &pi)) return false;
        }
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        processId = pi.dwProcessId;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint flags, char* environment, char* currentDirectory, StartupInfo* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleProcessList(uint* processIds, uint count);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(nint job, int infoClass, void* info, uint length, uint* returned);
}
