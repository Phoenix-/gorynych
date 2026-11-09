using System.Diagnostics;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Adapters.Codex;

public sealed class CodexAdapterOptions
{
    /// <summary>
    /// The <c>codex</c> executable. When null it is found on PATH; on Windows the native
    /// <c>codex.exe</c> that npm's <c>codex.cmd</c> shim starts is preferred.
    /// </summary>
    public string? Executable { get; init; }

    /// <summary>
    /// Connects to an app-server instead of starting <c>codex app-server</c>: the streams of one
    /// app-server connection. For tests (a fake app-server).
    /// </summary>
    public Func<CancellationToken, Task<AgentInstance>>? Launch { get; init; }

    /// <summary>
    /// How long a Codex session stays loaded once it is idle with no client
    /// (<c>agentCapabilities._meta["gorynych/unloadAfter"]</c>). A loaded thread cannot be written by
    /// Codex's own apps, and loading one again costs about two seconds (docs/adapters.md).
    /// </summary>
    public TimeSpan UnloadAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long closing or deleting a thread waits for its interrupted turn to end before going on.
    /// A turn still running then ends as cancelled: once unsubscribed, Codex sends nothing more of it.
    /// </summary>
    public TimeSpan TurnEndWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The name of the MCP server the user registers for agents outside Gorynych; turned off in Gorynych's threads.</summary>
    public string OutsideServerName { get; init; } = "gorynych-outside";

    /// <summary>Gorynych's version, for <c>clientInfo</c>.</summary>
    public string ClientVersion { get; init; } = "0";

    /// <summary>
    /// Which of some threads another Codex app holds, and who: <see cref="CodexThreadHolders"/> on
    /// Codex's own lock folder by default. For tests.
    /// </summary>
    internal Func<IEnumerable<string>, IReadOnlyDictionary<string, string>>? FindHolders { get; init; }

    /// <summary>Codex's lock folder, watched for threads changing hands; <c>CODEX_HOME</c>'s by default. For tests.</summary>
    internal string? LocksDirectory { get; init; }

    /// <summary>How long a burst of lock-folder changes is let settle before the holders are found again.</summary>
    internal TimeSpan LockSettle { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>How often the threads last seen held are looked at again, for a holder that left no event.</summary>
    internal TimeSpan LockRecheck { get; init; } = TimeSpan.FromSeconds(60);

    public Action<string>? Log { get; init; }
}

/// <summary>
/// The Codex adapter: an in-process ACP agent that drives one <c>codex app-server</c>, the service's
/// child, over its JSON-RPC (docs/adapters.md, "Codex").
/// </summary>
public sealed class CodexAdapter(CodexAdapterOptions options) : IAgentAdapter
{
    public string Name => "codex";
    public string DisplayName => "Codex";

    public async Task<AgentInstance> StartAsync(CancellationToken ct)
    {
        int? appServerPid = null;
        AgentInstance appServer;
        if (options.Launch is { } launch) appServer = await launch(ct);
        else (appServer, appServerPid) = StartAppServer();
        var link = new DuplexLink();
        var (agentIn, agentOut) = link.AgentEnd;
        // The app-server's own locks are the threads Gorynych has open: they do not count.
        var locks = options.LocksDirectory ?? CodexThreadHolders.DefaultDirectory();
        var holders = options.FindHolders ?? new CodexThreadHolders(locks, () => appServerPid is { } pid ? [pid] : []).Find;
        var agent = new CodexAgent(options, new JsonRpcConnection(agentIn, agentOut, "codex-adapter"), appServer, holders, locks);
        try
        {
            await agent.StartAsync(ct);
        }
        catch
        {
            await agent.DisposeAsync();
            throw;
        }
        var (clientIn, clientOut) = link.ClientEnd;
        return new AgentInstance(clientIn, clientOut, agent.Exited, agent.DisposeAsync);
    }

    (AgentInstance AppServer, int Pid) StartAppServer()
    {
        var exe = options.Executable ?? FindCodex() ?? throw new InvalidOperationException("codex was not found on PATH");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        psi.ArgumentList.Add("app-server");
        var process = Process.Start(psi) ?? throw new InvalidOperationException("codex app-server did not start");
        options.Log?.Invoke($"codex: started {exe} app-server, pid {process.Id}");
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
                options.Log?.Invoke("codex: " + line);
        }, CancellationToken.None);
        var exited = process.WaitForExitAsync(CancellationToken.None);
        return (new AgentInstance(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, exited, async () =>
        {
            try { process.StandardInput.Close(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            if (await Task.WhenAny(exited, Task.Delay(3000)) != exited)
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
        }), process.Id);
    }

    /// <summary>
    /// <c>codex</c> on PATH. On Windows npm installs a <c>codex.cmd</c> shim that starts node, which
    /// starts the native <c>codex.exe</c>; that one is used directly when it is where npm puts it.
    /// </summary>
    static string? FindCodex()
    {
        var found = ExecutableLocator.Find("codex");
        if (found is null || !OperatingSystem.IsWindows() || found.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return found;
        var npm = Path.GetDirectoryName(found)!;
        foreach (var (package, triple) in new[] { ("codex-win32-x64", "x86_64-pc-windows-msvc"), ("codex-win32-arm64", "aarch64-pc-windows-msvc") })
        {
            var native = Path.Combine(npm, "node_modules", "@openai", "codex", "node_modules", "@openai", package, "vendor", triple, "bin", "codex.exe");
            if (File.Exists(native)) return native;
        }
        return found;
    }
}
