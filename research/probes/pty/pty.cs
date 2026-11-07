#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
// Probe: a minimal ConPTY host. Runs the command line from args[0] (a file) in a pseudo console,
// logs raw output to args[1], and accepts keystrokes on POST http://127.0.0.1:47813/keys.
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

var cmdline = File.ReadAllText(args[0]).Trim();
var rawLog = args[1];
var cwd = args.Length > 2 ? args[2] : Environment.CurrentDirectory;

if (!Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Exception("CreatePipe in");
if (!Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Exception("CreatePipe out");
var hr = Native.CreatePseudoConsole(new Native.COORD { X = 120, Y = 40 }, inRead, outWrite, 0, out var hPC);
if (hr != 0) throw new Exception($"CreatePseudoConsole 0x{hr:X}");
inRead.Dispose();
outWrite.Dispose();

var size = IntPtr.Zero;
Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
var attrs = Marshal.AllocHGlobal(size);
if (!Native.InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Exception("InitializeProcThreadAttributeList");
if (!Native.UpdateProcThreadAttribute(attrs, 0, (IntPtr)0x00020016, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
    throw new Exception("UpdateProcThreadAttribute");
var si = new Native.STARTUPINFOEX();
si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
si.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES with null handles: do not inherit ours
si.lpAttributeList = attrs;
if (!Native.CreateProcessW(null, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, cwd, ref si, out var pi))
    throw new Exception($"CreateProcess {Marshal.GetLastWin32Error()}");
File.AppendAllText(rawLog, "");

var input = new FileStream(inWrite, FileAccess.Write);
var output = new FileStream(outRead, FileAccess.Read);
var gate = new object();
_ = Task.Run(() =>
{
    var buf = new byte[8192];
    int n;
    using var raw = new FileStream(rawLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    while ((n = output.Read(buf, 0, buf.Length)) > 0) { raw.Write(buf, 0, n); raw.Flush(); }
});

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
var app = builder.Build();
app.MapPost("/keys", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var bytes = ms.ToArray();
    lock (gate) { input.Write(bytes, 0, bytes.Length); input.Flush(); }
    return $"sent {bytes.Length} bytes";
});
app.MapPost("/kill", (IHostApplicationLifetime life) => { Native.ClosePseudoConsole(hPC); life.StopApplication(); return "killed"; });
_ = Task.Run(() => { Native.WaitForSingleObject(pi.hProcess, 0xFFFFFFFF); app.Lifetime.StopApplication(); });
app.Run("http://127.0.0.1:47813");

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CreatePipe(out SafeFileHandle r, out SafeFileHandle w, IntPtr sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] public static extern void ClosePseudoConsole(IntPtr hPC);
}
