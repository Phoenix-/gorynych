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
/// created or deleted. A heuristic on Codex's insides: any failure means "not known" and marks
/// nothing, and outside Windows nothing is known.
/// </summary>
/// <param name="locksDirectory">Codex's <c>thread-writer-locks</c> folder; null knows nothing.</param>
/// <param name="ownProcesses">Processes whose locks do not count: this adapter's own app-server.</param>
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
                var other = pids.FirstOrDefault(p => p != Environment.ProcessId && !own.Contains(p));
                if (other == 0) continue;
                var (image, parentImage) = Windows.Describe(other);
                held[id] = Classify(other, image, parentImage, localAppData);
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
    /// Who a holding process is: Codex Desktop (its image under <c>%LOCALAPPDATA%\OpenAI\Codex\</c> or
    /// the OpenAI.Codex package, or started by ChatGPT.exe), VS Code (an extension's codex, or started by
    /// Code.exe), otherwise a codex in a terminal, by its process ID.
    /// </summary>
    internal static string Classify(int pid, string? image, string? parentImage, string localAppData)
    {
        var parent = parentImage is null ? null : Path.GetFileName(parentImage);
        if ((image is not null && (image.StartsWith(Path.Combine(localAppData, "OpenAI", "Codex") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || image.Contains(@"\WindowsApps\OpenAI.Codex", StringComparison.OrdinalIgnoreCase)))
            || string.Equals(parent, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            return "Codex Desktop";
        if ((image is not null && (image.Contains(@"\.vscode\extensions\", StringComparison.OrdinalIgnoreCase)
                || image.Contains(@"\.vscode-insiders\extensions\", StringComparison.OrdinalIgnoreCase)))
            || string.Equals(parent, "Code.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, "Code - Insiders.exe", StringComparison.OrdinalIgnoreCase))
            return "VS Code";
        return $"a terminal codex (pid {pid})";
    }

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

        /// <summary>A process's image path and its parent's, as far as they can be read.</summary>
        public static (string? Image, string? ParentImage) Describe(int pid)
        {
            var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (process == 0) return (null, null);
            try
            {
                var image = ImagePath(process);
                ProcessBasicInfo info;
                if (NtQueryInformationProcess(process, ProcessBasicInformation, &info, (uint)sizeof(ProcessBasicInfo), out _) != 0) return (image, null);
                var parent = OpenProcess(ProcessQueryLimitedInformation, false, (uint)info.InheritedFromUniqueProcessId);
                if (parent == 0) return (image, null);
                try { return (image, ImagePath(parent)); }
                finally { CloseHandle(parent); }
            }
            finally
            {
                CloseHandle(process);
            }
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

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
