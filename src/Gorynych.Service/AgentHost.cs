using System.Reflection;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

/// <summary>
/// Keeps one adapter running and initialized. Starts it on first use and again after it
/// exits; remembers which sessions are live in the current instance, because a restarted
/// agent has to resume or load a session before it can take a prompt.
/// </summary>
public sealed class AgentHost(IAgentAdapter adapter, Conductor conductor) : IAsyncDisposable
{
    readonly SemaphoreSlim startGate = new(1, 1);
    readonly HashSet<string> live = new(StringComparer.Ordinal);
    readonly Lock liveGate = new();
    AgentInstance? instance;
    JsonRpcConnection? connection;

    public string Name => adapter.Name;
    public string DisplayName => adapter.DisplayName;

    /// <summary>The adapter's <c>initialize</c> result, once it has started.</summary>
    public InitializeResponse? Info { get; private set; }

    public bool IsRunning => connection is { IsClosed: false };

    /// <summary>The agent's connection when it runs, or null: never starts it (unlike <see cref="GetAsync"/>).</summary>
    public JsonRpcConnection? Running => connection is { IsClosed: false } running ? running : null;

    public static string Version =>
        typeof(AgentHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    static InitializeRequest InitializeParams() => new()
    {
        ProtocolVersion = AcpMethods.ProtocolVersion,
        ClientCapabilities = new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
            Terminal = false,
            // The service relays forms to a client that declared them.
            Elicitation = new ElicitationCapabilities { Form = new ElicitationFormCapabilities() },
        },
        ClientInfo = new Implementation { Name = "gorynych", Title = "Gorynych", Version = Version },
    };

    public async Task<JsonRpcConnection> GetAsync(CancellationToken ct)
    {
        if (connection is { IsClosed: false } running) return running;
        await startGate.WaitAsync(ct);
        try
        {
            if (connection is { IsClosed: false } again) return again;
            if (conductor.Hosts is { } hosts) return await HostedAsync(hosts, ct);
            ServiceLog.Info($"{Name}: starting");
            var started = await adapter.StartAsync(ct);
            var rpc = new JsonRpcConnection(started.Input, started.Output, Name)
            {
                RequestHandler = (req, token) => conductor.OnAgentRequestAsync(this, req, token),
                NotificationHandler = (method, p) => conductor.OnAgentNotificationAsync(this, method, p),
                Log = m => ServiceLog.Warn(m),
            };
            rpc.Start();
            InitializeResponse init;
            try
            {
                init = await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, InitializeParams(), ct);
            }
            catch
            {
                await rpc.DisposeAsync();
                await started.DisposeAsync();
                throw;
            }
            ServiceLog.Info($"{Name}: initialized {init.AgentInfo?.Name} {init.AgentInfo?.Version}");
            var previous = instance;
            lock (liveGate) live.Clear();
            instance = started;
            Info = init;
            connection = rpc;
            _ = rpc.Completion.ContinueWith(_ => OnClosed(rpc), TaskScheduler.Default);
            if (previous is not null)
                try { await previous.DisposeAsync(); } catch (Exception e) { ServiceLog.Warn($"{Name}: old instance: {e.Message}"); }
            return rpc;
        }
        finally
        {
            startGate.Release();
        }
    }

    // ---------------------------------------------------------------- in a host

    readonly Lock attachGate = new();
    TaskCompletionSource attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly HashSet<string> goneHosts = new(StringComparer.Ordinal);

    /// <summary>The ID of the host that runs the adapter now, if any.</summary>
    public string? HostId { get; private set; }

    /// <summary>How long a request for the agent waits for its host to connect.</summary>
    public static TimeSpan AttachTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The adapter runs in a host: one that runs already is waited for (a host comes back to a new
    /// conductor on its own), else one is started; under <see cref="startGate"/>.
    /// </summary>
    async Task<JsonRpcConnection> HostedAsync(Gorynych.Service.Hosts.IHostLauncher hosts, CancellationToken ct)
    {
        Task waiting;
        lock (attachGate) waiting = attached.Task;
        if (connection is { IsClosed: false } ready) return ready;
        if (hostFailure is { } failed && Environment.TickCount64 < failed.RetryAt)
            throw HostFailed($"{failed.Why} Gorynych starts it again in {(failed.RetryAt - Environment.TickCount64 + 999) / 1000} seconds.");
        var alive = hosts.IsAlive(Name);
        if (!alive) hosts.Launch(Name);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await Task.WhenAny(waiting, Task.Delay(HostCheckInterval, ct)) != waiting)
        {
            ct.ThrowIfCancellationRequested();
            // One that ran and runs no more exited before it connected: its log says why.
            if (hosts.IsAlive(Name)) alive = true;
            else if (alive) Fail($"The {DisplayName} host exited before it connected to Gorynych; its log is in Gorynych's logs folder (host-{Name}-*.log).");
            if (waited.Elapsed >= AttachTimeout)
                Fail($"The {DisplayName} host did not connect to Gorynych within {AttachTimeout.TotalSeconds:0} seconds; its log is in Gorynych's logs folder " +
                    $"(host-{Name}-*.log). If its process (Gorynych.Service host --adapter {Name}) hangs, stopping it lets Gorynych start another.");
        }
        hostFailure = null;
        return connection ?? throw new IOException($"{Name}: its host went as it connected");
    }

    /// <summary>
    /// The host could not be had: the requests that come in the next while fail at once with the same
    /// words, rather than each wait for a host that fails again; the while doubles up to 5 minutes.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    void Fail(string why)
    {
        var backoff = hostFailure is { } before ? TimeSpan.FromTicks(Math.Min(before.Backoff.Ticks * 2, MaxHostBackoff.Ticks)) : HostBackoff;
        hostFailure = new HostFailure(why, Environment.TickCount64 + (long)backoff.TotalMilliseconds, backoff);
        ServiceLog.Warn($"{Name}: {why}");
        throw HostFailed(why);
    }

    static JsonRpcException HostFailed(string why) => new(JsonRpcException.InternalError, why);

    sealed record HostFailure(string Why, long RetryAt, TimeSpan Backoff);

    HostFailure? hostFailure; // under startGate

    /// <summary>How often a request that waits for the host looks whether it still runs.</summary>
    internal static TimeSpan HostCheckInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long after a host could not be had the next one is not started; it doubles up to <see cref="MaxHostBackoff"/>.</summary>
    internal static TimeSpan HostBackoff { get; set; } = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan MaxHostBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A host of this adapter connected and proved itself (<see cref="Conductor.ServeHostAsync"/>):
    /// the conductor initializes it as it would the adapter (a host that comes back answers from what
    /// its agent said the first time), and if it comes back, takes its report (open sessions, running
    /// turns), adopts its turns, and has it send what the journal lacks after each session's cursor.
    /// Then requests for the agent go to it. False when it is refused: a second host, or one this
    /// conductor already saw go.
    /// </summary>
    internal async Task<bool> AttachAsync(JsonRpcConnection rpc, string hostId, CancellationToken ct)
    {
        // The host before closed, and that is not handled yet: it is now, so a host that comes back
        // after its link broke is known as gone.
        if (connection is { IsClosed: true } closed) OnClosed(closed);
        bool gone;
        lock (attachGate)
        {
            if (connection is { IsClosed: false })
            {
                ServiceLog.Warn($"{Name}: a second host ({hostId}) connected while {HostId} serves; refused");
                return false;
            }
            gone = goneHosts.Contains(hostId);
        }
        if (gone)
        {
            // This conductor ended its turns when it went: they cannot be taken back now.
            ServiceLog.Warn($"{Name}: host {hostId} came back after this service saw it go; stopped");
            try { await rpc.SendRequestAsync(GorynychProtocol.MethodHostStop, new JsonObject(), ct).WaitAsync(TimeSpan.FromSeconds(5), ct); }
            catch (Exception e) when (e is IOException or JsonRpcException or TimeoutException) { }
            return false;
        }
        rpc.RequestHandler = (req, token) => conductor.OnAgentRequestAsync(this, req, token);
        rpc.NotificationHandler = (method, p) => conductor.OnAgentNotificationAsync(this, method, p);
        var init = await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, InitializeParams(), ct);
        var reattached = init.Meta?[GorynychProtocol.MetaHost]?["reattached"] is JsonValue r && r.TryGetValue<bool>(out var back) && back;
        JsonObject? report = null;
        if (reattached) report = await rpc.SendRequestAsync(GorynychProtocol.MethodHostState, new JsonObject(), ct) as JsonObject;
        var sessions = Strings(report?["sessions"]);
        lock (liveGate)
        {
            live.Clear();
            foreach (var s in sessions) live.Add(s);
        }
        Info = init;
        HostId = hostId;
        JsonNode? replayed;
        try
        {
            if (report is not null) await conductor.AdoptAsync(this, report, ct);
            var after = conductor.HostCursors(hostId, sessions.Concat(Strings(report?["buffered"])).Distinct(StringComparer.Ordinal));
            replayed = await rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = after }, ct);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // This service exits (ct): the host lives on with its turns, for the next one, which takes
            // them back; they are kept at the exit (Conductor.DisposeAsync). Otherwise it went in the
            // middle of its handshake, with the turns it brought back: they end as a
            // host's that dies do, and the host is stopped if it comes again.
            ServiceLog.Warn($"{Name}: host {hostId} went before it was taken back; its turns end");
            lock (attachGate) goneHosts.Add(hostId);
            lock (liveGate) live.Clear();
            conductor.OnAgentStopped(this);
            throw;
        }
        foreach (var trimmed in Strings(replayed?["trimmed"])) conductor.HostTrimmed(this, trimmed);
        // The turns a service before left to a host that no longer has them (it died, or exited
        // alone) never ended anywhere: they end here, cancelled.
        await conductor.SettleHostPromptsAsync(this, reattached ? report : null);
        TaskCompletionSource signal;
        lock (attachGate)
        {
            connection = rpc;
            signal = attached;
        }
        _ = rpc.Completion.ContinueWith(_ => OnClosed(rpc), TaskScheduler.Default);
        ServiceLog.Info($"{Name}: host {hostId} {(reattached ? "came back" : "started")}: {init.AgentInfo?.Name} {init.AgentInfo?.Version}, " +
            $"{sessions.Count} sessions open, {(report?["turns"] as JsonArray)?.Count ?? 0} turns running, {(int?)replayed?["replayed"] ?? 0} updates sent again");
        signal.TrySetResult();
        return true;
    }

    static List<string> Strings(JsonNode? array) =>
        (array as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();

    /// <summary>The agent's connection closed: handled once, by whichever of its callers comes first.</summary>
    void OnClosed(JsonRpcConnection rpc)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref connection, null, rpc), rpc)) return;
        ServiceLog.Warn($"{Name}: connection closed");
        lock (attachGate)
        {
            if (HostId is { } gone && conductor.Hosts is not null) goneHosts.Add(gone);
            attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        lock (liveGate) live.Clear();
        conductor.OnAgentStopped(this);
    }

    public bool IsLive(string sessionId)
    {
        lock (liveGate) return live.Contains(sessionId);
    }

    public void MarkLive(string sessionId)
    {
        lock (liveGate) live.Add(sessionId);
    }

    public void MarkClosed(string sessionId)
    {
        lock (liveGate) live.Remove(sessionId);
    }

    public bool CanList => Info?.AgentCapabilities?.SessionCapabilities?.List is not null;
    public bool CanResume => Info?.AgentCapabilities?.SessionCapabilities?.Resume is not null;
    public bool CanLoad => Info?.AgentCapabilities?.LoadSession == true;
    public bool CanClose => Info?.AgentCapabilities?.SessionCapabilities?.Close is not null;
    public bool CanDelete => Info?.AgentCapabilities?.SessionCapabilities?.Delete is not null;

    /// <summary>The agent takes <c>systemPrompt</c> in append mode on new, load and resume.</summary>
    public bool TakesSystemPrompt => Info?.AgentCapabilities?.SessionCapabilities?.SystemPrompt is not null;

    /// <summary>The adapter sets native titles (<c>_gorynych/set_title</c>).</summary>
    public bool CanSetTitle => Flag(GorynychProtocol.CapSetTitle);

    /// <summary>The adapter tells when a row of its list changes (<c>_gorynych/sessions_changed</c>).</summary>
    public bool TellsListChanges => Flag(GorynychProtocol.MetaSessionsChanged);

    /// <summary>The adapter tells which sessions another app has open (<c>_gorynych/open_elsewhere</c>).</summary>
    public bool TellsOpenElsewhere => Flag(GorynychProtocol.CapOpenElsewhere);

    /// <summary>The adapter replays its own history after a mark on <c>session/resume</c> (<c>gorynych/replayFrom</c>).</summary>
    public bool ReplaysFrom => Flag(GorynychProtocol.CapReplayFrom);

    /// <summary>The agent keeps nothing durable before a session's first prompt.</summary>
    public bool DraftsNotDurable => Flag(GorynychProtocol.CapDraftsNotDurable);

    /// <summary>The idle time the adapter asks for its sessions (at most a day), or null for the service's own.</summary>
    public TimeSpan? UnloadAfter =>
        Info?.AgentCapabilities?.Meta?[GorynychProtocol.CapUnloadAfter] is JsonValue v && v.TryGetValue<double>(out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, MaxUnloadAfter.TotalSeconds))
            : null;

    static readonly TimeSpan MaxUnloadAfter = TimeSpan.FromDays(1);

    bool Flag(string key) => Info?.AgentCapabilities?.Meta?[key] is JsonValue v && v.TryGetValue<bool>(out var on) && on;

    public JsonObject Describe() => new()
    {
        ["name"] = Name,
        ["displayName"] = DisplayName,
        ["running"] = IsRunning,
        ["host"] = HostId,
        ["agentInfo"] = Info?.AgentInfo is { } a ? AcpJson.ToNode(a) : null,
    };

    /// <summary>
    /// Stops the adapter. One in a host is stopped only when the service shuts down
    /// (<see cref="Conductor.StopHostsOnExit"/>); for a restart its host keeps its agents and comes
    /// back to the next conductor.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var rpc = Interlocked.Exchange(ref connection, null);
        var running = Interlocked.Exchange(ref instance, null);
        if (rpc is not null && conductor.Hosts is not null && conductor.StopHostsOnExit)
            try { await rpc.SendRequestAsync(GorynychProtocol.MethodHostStop, new JsonObject()).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is IOException or JsonRpcException or TimeoutException) { ServiceLog.Warn($"{Name}: the host did not take its stop: {e.Message}"); }
        if (rpc is not null) await rpc.DisposeAsync();
        if (running is not null) await running.DisposeAsync();
    }
}
