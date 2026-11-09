using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gorynych.Adapters.Codex;

/// <summary>
/// Which other Codex app holds a thread open (docs/adapters.md, "One writer"). Codex lets one
/// process write a thread at a time, with an OS lock on
/// <c>&lt;CODEX_HOME&gt;/thread-writer-locks/&lt;thread id&gt;.lock</c> (codex-rs
/// <c>rollout/src/writer_lock.rs</c>), and app-server has no API that says who holds it. On Windows
/// the file system says which processes have the file open (<c>FileProcessIdsUsingFileInformation</c>);
/// the file is opened for its attributes only, with full sharing, so no lock is taken and nothing is
/// created or deleted. Only a Codex process counts as a holder, and only one that can be read:
/// anything else that has the file open (an antivirus, an indexer, a backup, a preview) is ignored.
/// A heuristic on Codex's insides: any failure means "not known" and marks nothing, and outside
/// Windows nothing is known; app-server then decides.
/// </summary>
/// <param name="locksDirectory">Codex's <c>thread-writer-locks</c> folder; null knows nothing.</param>
/// <param name="ownProcesses">Processes whose locks do not count, nor their descendants': this adapter's own app-server.</param>
internal sealed partial class CodexThreadHolders(string? locksDirectory, Func<IReadOnlyCollection<int>> ownProcesses)
{
    /// <summary><c>$CODEX_HOME/thread-writer-locks</c>, or <c>~/.codex/thread-writer-locks</c>.</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        "thread-writer-locks");

    /// <summary>
    /// The threads among <paramref name="threadIds"/> that another process holds, each with who holds
    /// it ("Codex Desktop", "VS Code", "a terminal codex (pid N)"). The folder is read once.
    /// </summary>
    public IReadOnlyDictionary<string, string> Find(IEnumerable<string> threadIds)
    {
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows() || locksDirectory is null) return held;
        try
        {
            if (!Directory.Exists(locksDirectory)) return held;
            var ids = threadIds.Where(IsThreadId).Distinct(StringComparer.Ordinal).ToList();
            // A page of a list: the folder is read once; one thread being opened: its file is looked for.
            var locks = ids.Count > 4
                ? Directory.EnumerateFiles(locksDirectory, "*.lock").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var own = ownProcesses();
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var id in ids)
            {
                var path = Path.Combine(locksDirectory, id + ".lock");
                if (locks is not null ? !locks.Contains(id) : !File.Exists(path)) continue;
                if (Windows.ProcessesUsing(path) is not { } pids) continue;
                foreach (var pid in pids)
                {
                    if (pid == Environment.ProcessId || own.Contains(pid)) continue;
                    // A process that cannot be read (gone, its ID reused, elevated) is not taken for anything.
                    if (Windows.Read(pid) is not { Image: { } image } process) continue;
                    // A wrapper's child (a .cmd, a shim) holds the lock for the adapter's own app-server.
                    if (Windows.DescendsFrom(pid, process, own)) continue;
                    var parentImage = Windows.Parent(process)?.Image;
                    if (Classify(pid, image, parentImage, localAppData) is not { } holder) continue;
                    held[id] = holder;
                    break;
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Not known: what was found so far stands, the rest is not marked.
        }
        return held;
    }

    /// <summary>The one thread's holder, or null when it is free or not known.</summary>
    public string? Find(string threadId) => Find([threadId]).GetValueOrDefault(threadId);

    /// <summary>A thread ID as a file name may hold it: letters, digits and dashes (Codex's are UUIDs).</summary>
    static bool IsThreadId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>
    /// Who a holding process is, or null when it is not Codex. Codex Desktop: any image under
    /// <c>%LOCALAPPDATA%\OpenAI\Codex\</c> or in the OpenAI.Codex package, or a <c>codex*.exe</c>
    /// started by ChatGPT.exe. VS Code: a <c>codex*.exe</c> in a VS Code extensions folder or started by
    /// Code.exe. Otherwise a <c>codex*.exe</c> is a codex in a terminal, by its process ID; any other
    /// image, or none (the process could not be read), is not a holder.
    /// </summary>
    /// <remarks>
    /// It reads Windows image paths, the same on any OS it runs on (the tests run on Linux too): it
    /// splits them at <c>\</c> and <c>/</c> itself and compares them ignoring case, never through <see cref="Path"/>.
    /// </remarks>
    internal static string? Classify(int pid, string? image, string? parentImage, string localAppData)
    {
        if (image is null) return null;
        var path = Slashed(image);
        var name = FileName(path);
        var codex = name.StartsWith("codex", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var parent = parentImage is null ? null : FileName(Slashed(parentImage));
        if (path.StartsWith(Slashed(localAppData).TrimEnd('\\') + @"\OpenAI\Codex\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\WindowsApps\OpenAI.Codex", StringComparison.OrdinalIgnoreCase)
            || (codex && string.Equals(parent, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)))
            return "Codex Desktop";
        if (!codex) return null;
        if (path.Contains(@"\.vscode\extensions\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\.vscode-insiders\extensions\", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, "Code.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, "Code - Insiders.exe", StringComparison.OrdinalIgnoreCase))
            return "VS Code";
        return $"a terminal codex (pid {pid})";
    }

    /// <summary>A Windows path with its separators as backslashes.</summary>
    static string Slashed(string path) => path.Replace('/', '\\');

    /// <summary>The last part of a backslashed path.</summary>
    static string FileName(string path) => path[(path.LastIndexOf('\\') + 1)..];

    static unsafe partial class Windows
    {
        const uint FileReadAttributes = 0x80;
        const uint ShareAll = 0x7; // read | write | delete: nothing the holder does is blocked
        const uint OpenExisting = 3;
        const int FileProcessIdsUsingFileInformation = 47;
        const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        const uint ProcessQueryLimitedInformation = 0x1000;
        const int ProcessBasicInformation = 0;

        /// <summary>The processes that have the file open, or null when that cannot be told.</summary>
        public static int[]? ProcessesUsing(string path)
        {
            using var file = CreateFileW(path, FileReadAttributes, ShareAll, 0, OpenExisting, 0, 0);
            if (file.IsInvalid) return null;
            foreach (var size in new[] { 4096, 65536 })
            {
                var buffer = new byte[size];
                fixed (byte* p = buffer)
                {
                    var status = NtQueryInformationFile(file, out _, p, (uint)size, FileProcessIdsUsingFileInformation);
                    if (status == StatusInfoLengthMismatch) continue;
                    if (status != 0) return null;
                    // FILE_PROCESS_IDS_USING_FILE_INFORMATION: a ULONG count, then ULONG_PTR IDs, pointer-aligned.
                    var count = *(uint*)p;
                    var max = (size - sizeof(nuint)) / sizeof(nuint);
                    if (count > max) return null;
                    var ids = new int[count];
                    for (var i = 0; i < count; i++) ids[i] = (int)((nuint*)(p + sizeof(nuint)))[i];
                    return ids;
                }
            }
            return null;
        }

        /// <summary>One process as far as it can be read: its image, its parent's ID, when it started.</summary>
        public readonly record struct ProcessInfo(int Pid, string? Image, int ParentPid, long Created);

        /// <summary>The process, or null when it cannot be opened (gone, elevated, another user's).</summary>
        public static ProcessInfo? Read(int pid)
        {
            var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (process == 0) return null;
            try
            {
                if (!GetProcessTimes(process, out var created, out _, out _, out _)) return null;
                ProcessBasicInfo info;
                var parentPid = NtQueryInformationProcess(process, ProcessBasicInformation, &info, (uint)sizeof(ProcessBasicInfo), out _) == 0
                    ? (int)info.InheritedFromUniqueProcessId : 0;
                return new ProcessInfo(pid, ImagePath(process), parentPid, created);
            }
            finally
            {
                CloseHandle(process);
            }
        }

        /// <summary>The process's parent, unless it is gone or its ID now names a younger process.</summary>
        public static ProcessInfo? Parent(ProcessInfo child) =>
            child.ParentPid > 0 && Read(child.ParentPid) is { } parent && parent.Created <= child.Created ? parent : null;

        /// <summary>Whether one of <paramref name="ancestors"/> is above the process, its parents' chain followed while it holds.</summary>
        public static bool DescendsFrom(int pid, ProcessInfo process, IReadOnlyCollection<int> ancestors)
        {
            if (ancestors.Count == 0) return false;
            var current = process;
            for (var depth = 0; depth < 16; depth++)
            {
                if (Parent(current) is not { } parent) return false;
                if (ancestors.Contains(parent.Pid)) return true;
                if (parent.Pid == pid) return false;
                current = parent;
            }
            return false;
        }

        static string? ImagePath(nint process)
        {
            var buffer = stackalloc char[1024];
            var length = 1024u;
            return QueryFullProcessImageNameW(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessBasicInfo
        {
            public nint ExitStatus;
            public nint PebBaseAddress;
            public nint AffinityMask;
            public nint BasePriority;
            public nint UniqueProcessId;
            public nint InheritedFromUniqueProcessId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoStatusBlock
        {
            public nint Status;
            public nuint Information;
        }

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock io, void* information, uint length, int informationClass);

        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationProcess(nint process, int informationClass, void* information, uint length, out uint returned);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
