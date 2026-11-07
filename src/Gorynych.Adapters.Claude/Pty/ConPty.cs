using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Gorynych.Adapters.Claude.Pty;

/// <summary>
/// A process running in a Windows pseudo console (ConPTY). Output must be read continuously,
/// or the child blocks; <see cref="Output"/> delivers it as raw UTF-8 bytes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class ConPty : IDisposable
{
    const uint ExtendedStartupInfoPresent = 0x00080000;
    const uint CreateUnicodeEnvironment = 0x00000400;
    const int StartfUseStdHandles = 0x00000100;
    static readonly nint ProcThreadAttributePseudoConsole = 0x00020016;

    readonly nint pseudoConsole;
    readonly SafeFileHandle process;
    readonly FileStream input;
    readonly FileStream output;
    readonly Lock writeGate = new();
    int disposed;

    ConPty(nint pseudoConsole, SafeFileHandle process, int processId, FileStream input, FileStream output)
    {
        this.pseudoConsole = pseudoConsole;
        this.process = process;
        ProcessId = processId;
        this.input = input;
        this.output = output;
        Exited = Task.Run(WaitForExit);
    }

    public int ProcessId { get; }

    /// <summary>Completes with the exit code when the process ends.</summary>
    public Task<int> Exited { get; }

    /// <summary>The console output as it arrives. Read it on one thread until it returns 0.</summary>
    public Stream Output => output;

    /// <summary>Starts <paramref name="commandLine"/> in a new pseudo console.</summary>
    public static ConPty Start(string commandLine, string workingDirectory, IDictionary<string, string> environment, short columns = 160, short rows = 50)
    {
        if (!CreatePipe(out var inRead, out var inWrite, 0, 0)) throw Error("CreatePipe");
        if (!CreatePipe(out var outRead, out var outWrite, 0, 0)) throw Error("CreatePipe");
        var hr = CreatePseudoConsole(new Coord { X = columns, Y = rows }, inRead, outWrite, 0, out var hpc);
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw Error("InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, hpc, (nint)sizeof(nint), 0, 0))
                throw Error("UpdateProcThreadAttribute");
            var si = new StartupInfoEx();
            si.StartupInfo.cb = sizeof(StartupInfoEx);
            si.StartupInfo.dwFlags = StartfUseStdHandles; // with null handles: do not hand ours to the child
            si.lpAttributeList = attributes;
            var env = EnvironmentBlock(environment);
            var cmd = (commandLine + '\0').ToCharArray();
            ProcessInformation pi;
            fixed (char* pCmd = cmd)
            fixed (char* pEnv = env)
            fixed (char* pCwd = workingDirectory)
            {
                if (!CreateProcessW(null, pCmd, 0, 0, 0, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, pEnv, pCwd, &si, &pi))
                {
                    ClosePseudoConsole(hpc);
                    throw Error("CreateProcess");
                }
            }
            CloseHandle(pi.hThread);
            return new ConPty(hpc, new SafeFileHandle(pi.hProcess, ownsHandle: true), pi.dwProcessId,
                new FileStream(inWrite, FileAccess.Write, 1), new FileStream(outRead, FileAccess.Read, 1));
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>Writes keystrokes or pasted text to the console input.</summary>
    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (writeGate)
        {
            input.Write(bytes);
            input.Flush();
        }
    }

    public void Resize(short columns, short rows) => ResizePseudoConsole(pseudoConsole, new Coord { X = columns, Y = rows });

    int WaitForExit()
    {
        WaitForSingleObject(process.DangerousGetHandle(), 0xFFFFFFFF);
        return GetExitCodeProcess(process.DangerousGetHandle(), out var code) ? (int)code : -1;
    }

    /// <summary>Ends the process: closing the pseudo console sends it a close event, then it is terminated.</summary>
    public void Kill()
    {
        if (Exited.IsCompleted) return;
        TerminateProcess(process.DangerousGetHandle(), 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ClosePseudoConsole(pseudoConsole);
        if (!Exited.Wait(2000))
        {
            Kill();
            Exited.Wait(2000);
        }
        input.Dispose();
        output.Dispose();
        // The exit watcher still uses the handle until the process is gone; never close it under it.
        if (Exited.IsCompleted) process.Dispose();
    }

    static char[] EnvironmentBlock(IDictionary<string, string> environment)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return sb.ToString().ToCharArray();
    }

    static Exception Error(string what) => new InvalidOperationException($"{what} failed: {Marshal.GetLastPInvokeError()}");

    [StructLayout(LayoutKind.Sequential)] struct Coord { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo StartupInfo; public nint lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [LibraryImport("kernel32.dll")]
    private static partial int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint pseudoConsole);

    [LibraryImport("kernel32.dll")]
    private static partial int ResizePseudoConsole(nint pseudoConsole, Coord size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint pseudoConsole);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint flags, char* environment, char* currentDirectory, StartupInfoEx* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
