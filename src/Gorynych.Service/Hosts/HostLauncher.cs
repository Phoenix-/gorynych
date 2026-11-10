using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Gorynych.Outside;

namespace Gorynych.Service.Hosts;

/// <summary>Starts the adapter hosts of a conductor that runs its adapters in hosts.</summary>
public interface IHostLauncher
{
    /// <summary>
    /// Whether the host of the adapter's <paramref name="slot"/> runs, connected or on its way to a
    /// conductor: from its start on. A slot is a host: the adapter-wide one of a generation, or one
    /// for a session; the empty slot is a host of an earlier version, which ran the whole adapter.
    /// </summary>
    bool IsAlive(string adapter, string slot);

    /// <summary>Starts the host of the adapter's slot, which then dials the conductor.</summary>
    void Launch(string adapter, string slot);

    /// <summary>What the host this launcher started last for the slot said as it failed, if it said anything; for the error a request gets.</summary>
    string? WhyExited(string adapter, string slot) => null;
}

/// <summary>
/// Hosts as processes of this executable (<c>Gorynych.Service host --adapter …</c>), detached from
/// the service's process and job as the service is from its bridge, so they outlive it.
/// </summary>
public sealed class ProcessHostLauncher(string exe, string pipe, string dataDirectory, Func<string, IReadOnlyList<string>> adapterArguments) : IHostLauncher
{
    readonly Lock gate = new();
    readonly Dictionary<string, (System.Diagnostics.Process? Process, string Log, long From)> started = new(StringComparer.Ordinal);

    /// <summary>
    /// It holds its mutex, or it is the one this launcher started and its process still runs: from
    /// its start on, before it takes the mutex, so a host that exits at once is seen to.
    /// </summary>
    public bool IsAlive(string adapter, string slot)
    {
        if (HostNames.IsHeld(pipe, adapter, slot)) return true;
        lock (gate) return started.TryGetValue(Key(adapter, slot), out var host) && host.Process is { } process && !process.HasExited;
    }

    static string Key(string adapter, string slot) => adapter + "/" + slot;

    public void Launch(string adapter, string slot)
    {
        List<string> args = ["host", "--adapter", adapter, "--pipe", pipe, "--data", dataDirectory];
        if (slot.Length > 0) args.AddRange(["--slot", slot]);
        args.AddRange(adapterArguments(adapter));
        ServiceLog.Info($"{adapter}: starting its host ({exe} {string.Join(' ', args)})");
        var log = Path.Combine(dataDirectory, "logs", $"host-{adapter}-{DateTime.Now:yyyyMMdd}.log");
        var from = File.Exists(log) ? new FileInfo(log).Length : 0;
        if (!DetachedProcess.Start(exe, args, out var processId))
            ServiceLog.Warn($"{adapter}: the host stays in the service's job object, which forbids breaking away; a restart may stop it");
        System.Diagnostics.Process? process = null;
        try { process = System.Diagnostics.Process.GetProcessById(processId); }
        catch (ArgumentException) { } // gone already
        lock (gate)
        {
            if (started.TryGetValue(Key(adapter, slot), out var before)) before.Process?.Dispose();
            started[Key(adapter, slot)] = (process, log, from);
            // The hosts that ended: let go of their handles.
            foreach (var (key, ended) in started.Where(h => h.Value.Process is { HasExited: true }).ToList())
                if (key != Key(adapter, slot))
                {
                    ended.Process!.Dispose();
                    started.Remove(key);
                }
        }
    }

    /// <summary>The last error the host wrote in its log since it was started.</summary>
    public string? WhyExited(string adapter, string slot)
    {
        (System.Diagnostics.Process? Process, string Log, long From) host;
        lock (gate)
            if (!started.TryGetValue(Key(adapter, slot), out host)) return null;
        try
        {
            using var file = new FileStream(host.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length <= host.From) return null;
            file.Position = host.From;
            using var reader = new StreamReader(file, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var error = lines.LastOrDefault(l => l.Contains(" ERR ", StringComparison.Ordinal));
            if (error is null) return null;
            var said = error[(error.IndexOf(" ERR ", StringComparison.Ordinal) + 5)..];
            var own = $"host {adapter}: ";
            return said.StartsWith(own, StringComparison.Ordinal) ? said[own.Length..] : said;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The names hosts and their conductor share: the host pipe, each host's mutex, the token.</summary>
public static class HostNames
{
    /// <summary>The pipe a conductor takes its hosts on, next to its clients' pipe.</summary>
    public static string Pipe(string servicePipe) => servicePipe + ".hosts";

    /// <summary>
    /// One host per slot of an adapter and pipe, across logon sessions, as the service's own mutex: so
    /// no two hosts of a generation run the adapter. The empty slot is the mutex of a host of an
    /// earlier version, which ran the whole adapter.
    /// </summary>
    public static string Mutex(string servicePipe, string adapter, string slot = "") =>
        slot.Length == 0 ? $"Gorynych.Host.{servicePipe}.{adapter}" : $"Gorynych.Host.{servicePipe}.{adapter}.{slot}";

    public static NamedWaitHandleOptions MutexOptions => new() { CurrentUserOnly = true, CurrentSessionOnly = false };

    /// <summary>Whether a host of the adapter holds its mutex: it runs.</summary>
    public static bool IsHeld(string servicePipe, string adapter, string slot = "")
    {
        if (!System.Threading.Mutex.TryOpenExisting(Mutex(servicePipe, adapter, slot), MutexOptions, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>The token file in the service's data folder, which only the user can read.</summary>
    public static string TokenPath(string dataDirectory) => Path.Combine(dataDirectory, "host.token");

    /// <summary>
    /// The conductor's token for its hosts, made once and kept: a host started by an earlier run
    /// proves itself with it. Only the user may read the file, whatever the folder lets others do:
    /// on Windows its DACL allows the user alone and inherits nothing, elsewhere its mode is 0600
    /// from its creation on. A token an earlier version wrote gets those rights now. It keeps other
    /// users out; a process of the same user can read it, as it can the rest of the user's files.
    /// </summary>
    public static string EnsureToken(string dataDirectory)
    {
        var path = TokenPath(dataDirectory);
        if (File.Exists(path))
        {
            Protect(path);
            if (File.ReadAllText(path, Encoding.UTF8).Trim() is { Length: >= 32 } kept) return kept;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var tmp = path + ".tmp";
        File.Delete(tmp);
        using (var file = CreateOwnerOnly(tmp)) file.Write(Encoding.ASCII.GetBytes(token));
        File.Move(tmp, path, overwrite: true); // a move keeps the file's own rights
        return token;
    }

    static FileStream CreateOwnerOnly(string path) => OperatingSystem.IsWindows()
        ? new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, OwnerOnly())
        : new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });

    /// <summary>Gives an existing token file the rights a new one has.</summary>
    internal static void Protect(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) new FileInfo(path).SetAccessControl(OwnerOnly());
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            ServiceLog.Warn($"service: could not keep {path} to the user alone: {e.Message}");
        }
    }

    /// <summary>The user alone, with all rights; nothing inherited from the folder.</summary>
    [SupportedOSPlatform("windows")]
    internal static FileSecurity OwnerOnly()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>The token as a host reads it; null while there is none (the conductor writes it before it starts a host), or it cannot be read.</summary>
    public static string? ReadToken(string dataDirectory)
    {
        var path = TokenPath(dataDirectory);
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Compares two tokens in constant time.</summary>
    public static bool SameToken(string? a, string? b) =>
        a is not null && b is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
