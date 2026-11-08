// Compiled into both Gorynych.Service (for tests) and Gorynych.Outside, which uses it.
using System.Runtime.InteropServices;

namespace Gorynych.Outside;

/// <summary>
/// Whether a process runs inside Gorynych: some ancestor of it runs a Gorynych service. Every
/// agent descends from the service and starts its MCP servers as its own children, so the
/// <c>gorynych-outside</c> server that an agent inside Gorynych reads from the user's own
/// configuration finds the service among its ancestors (docs/service-protocol.md, "Hiding from
/// sessions inside Gorynych").
/// </summary>
internal static partial class Ancestry
{
    /// <summary>The image name of the service: the apphost and the Native AOT build alike.</summary>
    public const string ServiceImage = "Gorynych.Service.exe";

    /// <summary>One process as the walk sees it.</summary>
    public readonly record struct ProcessEntry(int ParentId, string Image, DateTime? Created);

    /// <summary>The verdict, and the ancestors visited (image and ID), nearest first, for the log.</summary>
    public readonly record struct Verdict(bool Inside, IReadOnlyList<string> Chain, string Why);

    /// <summary>Walks the current process's ancestors. Builds everywhere; only Windows can walk yet.</summary>
    public static Verdict OfCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            return new Verdict(false, [], "the parent walk exists only on Windows yet, so every caller counts as outside");
        var processes = Windows.Snapshot();
        return Walk(Environment.ProcessId, pid => processes.TryGetValue(pid, out var p) ? p with { Created = Windows.CreationTime(pid) } : null);
    }

    /// <summary>
    /// Walks up from <paramref name="self"/>. It stops at a parent that no longer exists, and at
    /// one created after its child, which means its ID was reused. Any ancestor running a
    /// Gorynych service makes the process inside, whichever service it is.
    /// </summary>
    public static Verdict Walk(int self, Func<int, ProcessEntry?> lookup)
    {
        var chain = new List<string>();
        if (lookup(self) is not { } current) return new Verdict(false, chain, "the process itself is not in the snapshot");
        var seen = new HashSet<int> { self };
        for (var depth = 0; depth < 64; depth++)
        {
            var parentId = current.ParentId;
            if (parentId <= 0 || !seen.Add(parentId)) return new Verdict(false, chain, "the chain ended");
            if (lookup(parentId) is not { } parent) return new Verdict(false, chain, $"parent {parentId} no longer exists");
            if (parent.Created is { } born && current.Created is { } childBorn && born > childBorn)
                return new Verdict(false, chain, $"parent {parentId} is younger than its child, so its ID was reused");
            chain.Add($"{parent.Image} {parentId}");
            if (string.Equals(parent.Image, ServiceImage, StringComparison.OrdinalIgnoreCase))
                return new Verdict(true, chain, $"{parent.Image} {parentId} is an ancestor");
            current = parent;
        }
        return new Verdict(false, chain, "the chain is too long");
    }

    static unsafe partial class Windows
    {
        public static Dictionary<int, ProcessEntry> Snapshot()
        {
            var result = new Dictionary<int, ProcessEntry>();
            var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snapshot == -1) return result;
            try
            {
                var entry = new ProcessEntry32 { dwSize = (uint)sizeof(ProcessEntry32) };
                for (var ok = Process32FirstW(snapshot, &entry); ok; ok = Process32NextW(snapshot, &entry))
                    result[(int)entry.th32ProcessID] = new ProcessEntry((int)entry.th32ParentProcessID, new string(entry.szExeFile), null);
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return result;
        }

        public static DateTime? CreationTime(int pid)
        {
            var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
            if (handle == 0) return null;
            try
            {
                return GetProcessTimes(handle, out var created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessEntry32
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
            public fixed char szExeFile[260];
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32FirstW(nint snapshot, ProcessEntry32* entry);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32NextW(nint snapshot, ProcessEntry32* entry);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
