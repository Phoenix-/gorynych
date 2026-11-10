using System.Security.Cryptography;
using System.Text;
using Gorynych.Outside;

namespace Gorynych.Service.Hosts;

/// <summary>Starts the adapter hosts of a conductor that runs its adapters in hosts.</summary>
public interface IHostLauncher
{
    /// <summary>Whether a host of the adapter runs, connected or on its way to a conductor.</summary>
    bool IsAlive(string adapter);

    /// <summary>Starts a host of the adapter, which then dials the conductor.</summary>
    void Launch(string adapter);
}

/// <summary>
/// Hosts as processes of this executable (<c>Gorynych.Service host --adapter …</c>), detached from
/// the service's process and job as the service is from its bridge, so they outlive it.
/// </summary>
public sealed class ProcessHostLauncher(string exe, string pipe, string dataDirectory, IReadOnlyList<string> adapterArguments) : IHostLauncher
{
    public bool IsAlive(string adapter) => HostNames.IsHeld(pipe, adapter);

    public void Launch(string adapter)
    {
        List<string> args = ["host", "--adapter", adapter, "--pipe", pipe, "--data", dataDirectory, .. adapterArguments];
        ServiceLog.Info($"{adapter}: starting its host ({exe} {string.Join(' ', args)})");
        if (!DetachedProcess.Start(exe, args))
            ServiceLog.Warn($"{adapter}: the host stays in the service's job object, which forbids breaking away; a restart may stop it");
    }
}

/// <summary>The names hosts and their conductor share: the host pipe, each host's mutex, the token.</summary>
public static class HostNames
{
    /// <summary>The pipe a conductor takes its hosts on, next to its clients' pipe.</summary>
    public static string Pipe(string servicePipe) => servicePipe + ".hosts";

    /// <summary>One host per adapter and pipe, across logon sessions, as the service's own mutex.</summary>
    public static string Mutex(string servicePipe, string adapter) => $"Gorynych.Host.{servicePipe}.{adapter}";

    public static NamedWaitHandleOptions MutexOptions => new() { CurrentUserOnly = true, CurrentSessionOnly = false };

    /// <summary>Whether a host of the adapter holds its mutex: it runs.</summary>
    public static bool IsHeld(string servicePipe, string adapter)
    {
        if (!System.Threading.Mutex.TryOpenExisting(Mutex(servicePipe, adapter), MutexOptions, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>The token file in the service's data folder, as only the user can read it.</summary>
    public static string TokenPath(string dataDirectory) => Path.Combine(dataDirectory, "host.token");

    /// <summary>The conductor's token for its hosts, made once and kept: a host started by an earlier run proves itself with it.</summary>
    public static string EnsureToken(string dataDirectory)
    {
        var path = TokenPath(dataDirectory);
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Trim() is { Length: >= 32 } kept) return kept;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Gorynych.Acp.Hosting.AtomicFile.WriteAllText(path, token, Encoding.UTF8);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return token;
    }

    /// <summary>The token as a host reads it; null while there is none (the conductor writes it before it starts a host).</summary>
    public static string? ReadToken(string dataDirectory)
    {
        var path = TokenPath(dataDirectory);
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null; }
        catch (IOException) { return null; }
    }

    /// <summary>Compares two tokens in constant time.</summary>
    public static bool SameToken(string? a, string? b) =>
        a is not null && b is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
