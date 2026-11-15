using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gorynych.Service;

/// <summary>
/// One process as status shows it: its ID, parent, image name, executable path and start time. No
/// command line and no environment, so a status report can never leak a prompt, a token or a secret.
/// </summary>
internal sealed record ProcessSnapshot(int Pid, int ParentPid, string Name, string? Path, DateTime? StartedAt);

/// <summary>
/// A bounded, best-effort process snapshot for <c>status --tree</c>. Windows uses Toolhelp for
/// parents; Linux <c>/proc</c>; macOS <c>ps</c>. Anything unavailable is left unknown rather than
/// guessed, and every call is guarded so status still works where the platform hides a process.
/// </summary>
internal static class ProcessInspector
{
    public static IReadOnlyList<ProcessSnapshot> Snapshot()
    {
        try
        {
            var parents = Parents();
            var result = new List<ProcessSnapshot>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var pid = process.Id;
                    string name = process.ProcessName;
                    string? path = null;
                    DateTime? started = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { }
                    try { started = process.StartTime.ToUniversalTime(); }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { }
                    parents.TryGetValue(pid, out var parent);
                    result.Add(new ProcessSnapshot(pid, parent, name, path, started));
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                finally
                {
                    process.Dispose();
                }
            }
            return result;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static Dictionary<int, int> Parents()
    {
        if (OperatingSystem.IsWindows()) return WindowsParents();
        if (OperatingSystem.IsLinux()) return LinuxParents();
        if (OperatingSystem.IsMacOS()) return MacParents();
        return [];
    }

    // ---------------------------------------------------------------- Windows

    static Dictionary<int, int> WindowsParents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = Windows.CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == -1) return result;
        try
        {
            var entry = new Windows.ProcessEntry32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Windows.ProcessEntry32>() };
            for (var ok = Windows.Process32FirstW(snapshot, ref entry); ok; ok = Windows.Process32NextW(snapshot, ref entry))
                result[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
        }
        finally
        {
            Windows.CloseHandle(snapshot);
        }
        return result;
    }

    static class Windows
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public nuint th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(nint handle);
    }

    // ---------------------------------------------------------------- Linux

    static Dictionary<int, int> LinuxParents()
    {
        var result = new Dictionary<int, int>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                    // pid (comm) state ppid ...: comm may hold spaces, so parse after the last ')'.
                    var close = stat.LastIndexOf(')');
                    if (close < 0 || close + 2 >= stat.Length) continue;
                    var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length > 1 && int.TryParse(fields[1], out var parent)) result[pid] = parent;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }

    // ---------------------------------------------------------------- macOS

    static Dictionary<int, int> MacParents()
    {
        var result = new Dictionary<int, int>();
        try
        {
            var info = new ProcessStartInfo("/bin/ps")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            info.ArgumentList.Add("-axo");
            info.ArgumentList.Add("pid=,ppid=");
            using var process = Process.Start(info);
            if (process is null) return result;
            var text = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } return result; }
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var parent))
                    result[pid] = parent;
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
        return result;
    }
}
