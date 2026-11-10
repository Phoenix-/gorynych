using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service.Hosts;

namespace Gorynych.Service;

/// <summary>
/// One running instance of an adapter as the conductor has it: the adapter in the service's own
/// process, or an adapter host (docs/service-protocol.md, "Adapter hosts"), with the generation it
/// runs and the sessions open in it.
/// </summary>
public sealed class HostLink
{
    internal HostLink(JsonRpcConnection rpc, InitializeResponse info)
    {
        Rpc = rpc;
        Info = info;
    }

    public JsonRpcConnection Rpc { get; }

    /// <summary>Its <c>initialize</c> answer.</summary>
    public InitializeResponse Info { get; }

    /// <summary>The host's ID; null for the adapter in the service's own process.</summary>
    public string? HostId { get; init; }

    /// <summary>Which host it is of its adapter: the adapter-wide one of a generation, or one for a session (Claude).</summary>
    public string Slot { get; init; } = "";

    /// <summary>What it runs, as its hello said; null when it did not say (a host of an earlier version).</summary>
    public HostGeneration? Generation { get; init; }

    /// <summary>Of the generation the conductor would start now: new sessions open in it. A stale one only keeps its own until they move.</summary>
    public bool Current { get; init; }

    /// <summary>A host for one session (Claude), not the adapter's.</summary>
    public bool ForSession { get; init; }

    public int ProcessId { get; init; }

    internal readonly HashSet<string> Sessions = new(StringComparer.Ordinal); // under the AgentHost's linksGate
    internal int Opening; // opens on their way to it; under linksGate
    internal bool Stopping; // under linksGate

    public bool CanClose => Info.AgentCapabilities?.SessionCapabilities?.Close is not null;
}

/// <summary>What a host said in its hello (<see cref="Conductor.ServeHostAsync"/>).</summary>
internal sealed record HostHello(string HostId, string Slot, HostGeneration? Generation, int ProcessId);

/// <summary>
/// Keeps an adapter running and initialized, and knows where each of its sessions is open. Without
/// hosts the adapter runs in this process, started on first use and again after it exits. With
/// hosts it has one or more: the adapter-wide host of the current generation, hosts of earlier
/// generations whose sessions have not moved yet, and for an adapter with a host per session
/// (Claude) one host for each open session (docs/service-protocol.md, "Updating adapters").
/// A restarted agent, or a new host, has to resume or load a session before it can take a prompt.
/// </summary>
public sealed class AgentHost(IAgentAdapter adapter, Conductor conductor) : IAsyncDisposable
{
    readonly SemaphoreSlim startGate = new(1, 1);
    readonly Lock linksGate = new();
    readonly List<HostLink> links = [];                                                          // under linksGate
    readonly Dictionary<string, HostLink> route = new(StringComparer.Ordinal);                     // session -> where it is open; under linksGate
    readonly Dictionary<string, TaskCompletionSource<HostLink>> arriving = new(StringComparer.Ordinal); // slot -> its attach; under linksGate
    readonly HashSet<string> goneHosts = new(StringComparer.Ordinal);                             // under linksGate
    AgentInstance? instance;

    public string Name => adapter.Name;
    public string DisplayName => adapter.DisplayName;

    IHostLauncher? Hosts => conductor.Hosts;

    /// <summary>Each session in a host of its own (Claude), rather than all in the adapter's.</summary>
    bool PerSession => Hosts is not null && conductor.HostPerSession(Name);

    HostGeneration? Expected => conductor.ExpectedGeneration(Name);

    /// <summary>The slot of the adapter-wide host of the current generation; empty when the conductor tells no generations.</summary>
    string GeneralSlot => Expected?.Key ?? "";

    HostLink? GeneralLocked() => links.LastOrDefault(l => l.Current && !l.ForSession && !l.Stopping && !l.Rpc.IsClosed);

    HostLink? General
    {
        get { lock (linksGate) return GeneralLocked(); }
    }

    /// <summary>The adapter's <c>initialize</c> result, once an instance of the current generation runs (or any, before).</summary>
    public InitializeResponse? Info
    {
        get
        {
            lock (linksGate) return (GeneralLocked() ?? links.LastOrDefault(l => l.Current) ?? links.LastOrDefault())?.Info;
        }
    }

    public bool IsRunning
    {
        get { lock (linksGate) return links.Any(l => !l.Rpc.IsClosed); }
    }

    /// <summary>The adapter-wide connection when it runs, or null: never starts it (unlike <see cref="GetAsync"/>).</summary>
    public JsonRpcConnection? Running => General?.Rpc;

    /// <summary>The ID of the adapter-wide host of the current generation, if one is connected.</summary>
    public string? HostId => General?.HostId;

    /// <summary>Its instances now.</summary>
    public IReadOnlyList<HostLink> Links
    {
        get { lock (linksGate) return links.ToList(); }
    }

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

    /// <summary>The adapter-wide connection, for a list or what no session has open: started, or waited for.</summary>
    public async Task<JsonRpcConnection> GetAsync(CancellationToken ct) => (await GeneralLinkAsync(ct)).Rpc;

    async Task<HostLink> GeneralLinkAsync(CancellationToken ct)
    {
        if (General is { } ready) return ready;
        await startGate.WaitAsync(ct);
        try
        {
            if (General is { } again) return again;
            if (Hosts is { } hosts) return await HostedAsync(hosts, GeneralSlot, ct);
            return await StartInProcessAsync(ct);
        }
        finally
        {
            startGate.Release();
        }
    }

    async Task<HostLink> StartInProcessAsync(CancellationToken ct)
    {
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
        var link = new HostLink(rpc, init) { Current = true };
        var previous = instance;
        List<HostLink> old;
        lock (linksGate)
        {
            old = [.. links];
            links.Clear();
            route.Clear();
            links.Add(link);
        }
        instance = started;
        _ = rpc.Completion.ContinueWith(_ => OnClosed(link), TaskScheduler.Default);
        if (previous is not null)
            try { await previous.DisposeAsync(); } catch (Exception e) { ServiceLog.Warn($"{Name}: old instance: {e.Message}"); }
        return link;
    }

    /// <summary>
    /// Where a session is opened or created: the adapter-wide instance, or with a host per session a
    /// new host of its own. It counts as an open on its way, so the host is not stopped as empty, until
    /// <see cref="DoneOpening"/>.
    /// </summary>
    internal async Task<JsonRpcConnection> OpenTargetAsync(CancellationToken ct)
    {
        HostLink link;
        if (PerSession)
            link = await HostedAsync(Hosts!, $"{GeneralSlot}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}", ct);
        else
        {
            link = await GeneralLinkAsync(ct);
            lock (linksGate) link.Opening++;
        }
        return link.Rpc;
    }

    /// <summary>An open on <paramref name="rpc"/> (<see cref="OpenTargetAsync"/>) is over: marked live there, or failed.</summary>
    internal void DoneOpening(JsonRpcConnection rpc)
    {
        lock (linksGate)
            if (links.FirstOrDefault(l => l.Rpc == rpc) is { Opening: > 0 } link) link.Opening--;
        Reap();
    }

    /// <summary>The connection a session's request goes to: where it is open, else the adapter-wide one.</summary>
    internal async Task<JsonRpcConnection> SessionRpcAsync(string sessionId, CancellationToken ct) =>
        RouteOf(sessionId)?.Rpc ?? await GetAsync(ct);

    // ---------------------------------------------------------------- hosts

    /// <summary>How long a request for the agent waits for its host to connect.</summary>
    public static TimeSpan AttachTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The host of <paramref name="slot"/>: one that runs already is waited for (a host comes back to
    /// a new conductor on its own), else one is started.
    /// </summary>
    async Task<HostLink> HostedAsync(IHostLauncher hosts, string slot, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            HandleClosed();
            Task<HostLink> arrival;
            lock (linksGate)
            {
                if (links.FirstOrDefault(l => l.Slot == slot && !l.Stopping && !l.Rpc.IsClosed) is { } ready)
                {
                    hostFailure = null;
                    return ready;
                }
                if (hostFailure is { } failed && Environment.TickCount64 < failed.RetryAt)
                    throw HostFailed($"{failed.Why} Gorynych starts it again in {(failed.RetryAt - Environment.TickCount64 + 999) / 1000} seconds.");
                if (!arriving.TryGetValue(slot, out var waiting))
                    arriving[slot] = waiting = new TaskCompletionSource<HostLink>(TaskCreationOptions.RunContinuationsAsynchronously);
                arrival = waiting.Task;
            }
            var startedHere = !hosts.IsAlive(Name, slot);
            if (startedHere) hosts.Launch(Name, slot);
            var gone = false;
            while (await Task.WhenAny(arrival, Task.Delay(HostCheckInterval, ct)) != arrival)
            {
                ct.ThrowIfCancellationRequested();
                if (!hosts.IsAlive(Name, slot))
                {
                    // The one started for this request exited before it connected: its log says why.
                    // One that ran before (it went, or was stopped as it came back) makes way for the next.
                    if (startedHere)
                        Fail(slot, $"The {DisplayName} host exited before it connected to Gorynych{(hosts.WhyExited(Name, slot) is { } why ? $": {why}" : "")}; " +
                            $"its log is in Gorynych's logs folder (host-{Name}-*.log).");
                    gone = true;
                    break;
                }
                if (waited.Elapsed >= AttachTimeout)
                    Fail(slot, $"The {DisplayName} host did not connect to Gorynych within {AttachTimeout.TotalSeconds:0} seconds; its log is in Gorynych's logs folder " +
                        $"(host-{Name}-*.log). If its process (Gorynych.Service host --adapter {Name}) hangs, stopping it lets Gorynych start another.");
            }
            if (!gone && arrival.IsCompletedSuccessfully && arrival.Result is { Stopping: false } got && !got.Rpc.IsClosed)
            {
                lock (linksGate) hostFailure = null;
                return got;
            }
            // It went as it connected (it died, or its link broke): the next one, in the same wait.
            if (attempt >= 3 || waited.Elapsed >= AttachTimeout)
                Fail(slot, $"The {DisplayName} host went each time it connected to Gorynych; its log is in Gorynych's logs folder (host-{Name}-*.log).");
            ServiceLog.Warn($"{Name}: its host went as it connected; waiting for the next one");
        }
    }

    /// <summary>
    /// The host could not be had: the requests that come in the next while fail at once with the same
    /// words, rather than each wait for a host that fails again; the while doubles up to 5 minutes.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    void Fail(string slot, string why)
    {
        lock (linksGate)
        {
            var backoff = hostFailure is { } before ? TimeSpan.FromTicks(Math.Min(before.Backoff.Ticks * 2, MaxHostBackoff.Ticks)) : HostBackoff;
            hostFailure = new HostFailure(why, Environment.TickCount64 + (long)backoff.TotalMilliseconds, backoff);
            arriving.Remove(slot); // a host that comes after all is no one's open on its way
        }
        ServiceLog.Warn($"{Name}: {why}");
        throw HostFailed(why);
    }

    static JsonRpcException HostFailed(string why) => new(JsonRpcException.InternalError, why);

    sealed record HostFailure(string Why, long RetryAt, TimeSpan Backoff);

    HostFailure? hostFailure; // under linksGate

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
    /// Then its sessions' requests go to it, and, if it is of the current generation, new ones. False
    /// when it is refused: a second host of the same slot, or one this conductor already saw go.
    /// </summary>
    internal async Task<bool> AttachAsync(JsonRpcConnection rpc, HostHello hello, CancellationToken ct)
    {
        // A host before that closed and is not handled yet is now, so a host that comes back after its
        // link broke is known as gone.
        HandleClosed();
        bool gone;
        HostLink? serving;
        lock (linksGate)
        {
            gone = goneHosts.Contains(hello.HostId);
            serving = links.FirstOrDefault(l => l.Slot == hello.Slot && !l.Rpc.IsClosed);
        }
        if (serving is not null)
        {
            ServiceLog.Warn($"{Name}: a second host ({hello.HostId}) connected for {SlotName(hello.Slot)} while {serving.HostId} serves it; refused");
            return false;
        }
        if (gone)
        {
            // This conductor ended its turns when it went: they cannot be taken back now.
            ServiceLog.Warn($"{Name}: host {hello.HostId} came back after this service saw it go; stopped");
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
        var expected = Expected;
        var link = new HostLink(rpc, init)
        {
            HostId = hello.HostId, Slot = hello.Slot, Generation = hello.Generation, ProcessId = hello.ProcessId,
            Current = expected is null || expected.Same(hello.Generation),
            ForSession = hello.Slot.Contains('.'),
        };
        lock (linksGate)
        {
            links.Add(link);
            foreach (var s in sessions)
                if (route.TryAdd(s, link)) link.Sessions.Add(s);
                else ServiceLog.Warn($"{Name}: host {hello.HostId} has {s} open, which host {route[s].HostId} has too; it goes on there");
            // An open waits for it: it is not stopped as empty before the session is opened in it.
            if (link.ForSession && arriving.ContainsKey(link.Slot)) link.Opening++;
        }
        JsonNode? replayed;
        try
        {
            if (report is not null) await conductor.AdoptAsync(this, link, report, ct);
            var after = conductor.HostCursors(hello.HostId, sessions.Concat(Strings(report?["buffered"])).Distinct(StringComparer.Ordinal));
            replayed = await rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = after }, ct);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // This service exits (ct): the host lives on with its turns, for the next one, which takes
            // them back; they are kept at the exit (Conductor.DisposeAsync). Otherwise it went in the
            // middle of its handshake, with the turns it brought back: they end as a host's that dies
            // do, and the host is stopped if it comes again.
            ServiceLog.Warn($"{Name}: host {hello.HostId} went before it was taken back; its turns end");
            var lost = Forget(link);
            conductor.OnAgentStopped(this, link, lost);
            throw;
        }
        foreach (var trimmed in Strings(replayed?["trimmed"])) conductor.HostTrimmed(this, trimmed);
        // The turns a service before left to a host that no longer has them (it died, or exited
        // alone) never ended anywhere: they end here, cancelled.
        await conductor.SettleAsync(this, link, reattached ? report : null);
        TaskCompletionSource<HostLink>? arrival;
        lock (linksGate) arriving.Remove(link.Slot, out arrival);
        _ = rpc.Completion.ContinueWith(_ => OnClosed(link), TaskScheduler.Default);
        ServiceLog.Info($"{Name}: host {hello.HostId} ({SlotName(link.Slot)}, {(link.Current ? "current" : "stale")}, {hello.Generation?.ToString() ?? "no generation"}) " +
            $"{(reattached ? "came back" : "started")}: {init.AgentInfo?.Name} {init.AgentInfo?.Version}, {sessions.Count} sessions open, " +
            $"{(report?["turns"] as JsonArray)?.Count ?? 0} turns running, {(int?)replayed?["replayed"] ?? 0} updates sent again");
        arrival?.TrySetResult(link);
        conductor.HostAttached(this, link);
        return true;
    }

    static string SlotName(string slot) => slot.Length == 0 ? "the adapter" : slot.Contains('.') ? $"a session's host {slot}" : $"the adapter's host {slot}";

    static List<string> Strings(JsonNode? array) =>
        (array as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();

    /// <summary>Forgets an instance: its sessions are open nowhere now. Returns them.</summary>
    List<string> Forget(HostLink link)
    {
        lock (linksGate)
        {
            if (!links.Remove(link)) return [];
            if (link.HostId is { } id && Hosts is not null) goneHosts.Add(id);
            var lost = link.Sessions.ToList();
            foreach (var s in lost)
                if (route.TryGetValue(s, out var on) && on == link) route.Remove(s);
            link.Sessions.Clear();
            return lost;
        }
    }

    /// <summary>An instance's connection closed: handled once, by whichever of its callers comes first.</summary>
    void OnClosed(HostLink link)
    {
        bool known;
        lock (linksGate) known = links.Contains(link);
        if (!known) return;
        var stopping = link.Stopping;
        var lost = Forget(link);
        if (!stopping) ServiceLog.Warn($"{Name}: {(link.HostId is { } id ? $"host {id}" : "its agent")}: connection closed");
        conductor.OnAgentStopped(this, link, lost);
    }

    /// <summary>Instances whose connection closed while their close is not handled yet.</summary>
    void HandleClosed()
    {
        foreach (var link in Links.Where(l => l.Rpc.IsClosed)) OnClosed(link);
    }

    public bool IsLive(string sessionId)
    {
        lock (linksGate) return route.ContainsKey(sessionId);
    }

    /// <summary>The sessions open in an instance now.</summary>
    internal string[] SessionsOf(HostLink link)
    {
        lock (linksGate) return [.. link.Sessions];
    }

    /// <summary>The instance the session is open in, if any.</summary>
    internal HostLink? RouteOf(string sessionId)
    {
        lock (linksGate) return route.GetValueOrDefault(sessionId);
    }

    /// <summary>The session is open in a host of an earlier generation: it moves once it is idle.</summary>
    internal bool IsStale(string sessionId) => RouteOf(sessionId) is { Current: false };

    /// <summary>The session is open now in the instance of <paramref name="rpc"/>.</summary>
    public void MarkLive(string sessionId, JsonRpcConnection rpc)
    {
        lock (linksGate)
        {
            if (links.FirstOrDefault(l => l.Rpc == rpc) is not { } link) return; // it went meanwhile
            if (route.TryGetValue(sessionId, out var was) && was != link) was.Sessions.Remove(sessionId);
            route[sessionId] = link;
            link.Sessions.Add(sessionId);
        }
    }

    public void MarkClosed(string sessionId)
    {
        lock (linksGate)
            if (route.Remove(sessionId, out var link)) link.Sessions.Remove(sessionId);
        Reap();
    }

    /// <summary>
    /// Stops the hosts left with nothing: a stale one whose sessions all moved, and a host per session
    /// whose session closed (or that was started for an open that did not happen).
    /// </summary>
    internal void Reap()
    {
        if (Hosts is null) return;
        List<HostLink> idle;
        lock (linksGate)
        {
            idle = links.Where(l => l.HostId is not null && !l.Stopping && !l.Rpc.IsClosed && l.Sessions.Count == 0 && l.Opening == 0
                && (l.ForSession || !l.Current)).ToList();
            foreach (var l in idle) l.Stopping = true;
        }
        foreach (var link in idle) _ = StopAsync(link, link.Current ? "its session closed" : "its sessions moved to a host of the current generation");
    }

    /// <summary>Stops a host the conductor has no more use for: it stops its agent and exits.</summary>
    internal async Task StopAsync(HostLink link, string why)
    {
        ServiceLog.Info($"{Name}: host {link.HostId} ({SlotName(link.Slot)}) stopped: {why}");
        try { await link.Rpc.SendRequestAsync(GorynychProtocol.MethodHostStop, new JsonObject()).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is IOException or JsonRpcException or TimeoutException) { }
        await link.Rpc.DisposeAsync();
    }

    /// <summary>
    /// A session that a host of the service before had open (<see cref="SessionRecord.HostSlot"/>), or
    /// any session while a host of an earlier version that runs the whole adapter is about: that host,
    /// if it still runs and has not come back yet, is waited for, so the session is never opened in two.
    /// </summary>
    internal async Task AwaitHostOfAsync(SessionRecord record, CancellationToken ct)
    {
        if (Hosts is not { } hosts || IsLive(record.SessionId)) return;
        HashSet<string> slots = [""];
        if (record.HostSlot is { } slot) slots.Add(slot);
        foreach (var s in slots)
        {
            Task<HostLink> arrival;
            lock (linksGate)
            {
                if (links.Any(l => l.Slot == s && !l.Rpc.IsClosed)) continue;
                if (!arriving.TryGetValue(s, out var waiting))
                    arriving[s] = waiting = new TaskCompletionSource<HostLink>(TaskCreationOptions.RunContinuationsAsynchronously);
                arrival = waiting.Task;
            }
            var waited = Stopwatch.StartNew();
            while (hosts.IsAlive(Name, s) && !arrival.IsCompleted && waited.Elapsed < AttachTimeout)
                await Task.WhenAny(arrival, Task.Delay(HostCheckInterval, ct));
            ct.ThrowIfCancellationRequested();
            if (!arrival.IsCompleted && hosts.IsAlive(Name, s))
                ServiceLog.Warn($"{Name} {record.SessionId}: the host from before ({SlotName(s)}) runs but did not come back within {AttachTimeout.TotalSeconds:0} s");
        }
        if (record.HostOf is not null && !IsLive(record.SessionId) && !HostIsAround(record))
            await conductor.SettleGoneAsync(this, record);
    }

    /// <summary>Whether the host a record names (<see cref="SessionRecord.HostOf"/>) is connected, or runs and may come.</summary>
    internal bool HostIsAround(SessionRecord record)
    {
        if (Hosts is not { } hosts || record.HostOf is not { } hostId) return false;
        var slot = record.HostSlot ?? "";
        lock (linksGate)
        {
            if (links.Any(l => l.HostId == hostId)) return true;
            if (links.Any(l => l.Slot == slot && !l.Rpc.IsClosed)) return false; // another host has its slot now
        }
        return hosts.IsAlive(Name, slot);
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

    /// <summary>Its instances for <c>_gorynych/status</c>: generation, sessions and, as the conductor sees them, running turns.</summary>
    public JsonObject Describe(Func<string, bool>? turnRuns = null)
    {
        turnRuns ??= _ => false;
        var hosts = new JsonArray();
        foreach (var link in Links.Where(l => l.HostId is not null))
        {
            string[] sessions;
            lock (linksGate) sessions = [.. link.Sessions];
            hosts.Add(new JsonObject
            {
                ["hostId"] = link.HostId, ["slot"] = link.Slot, ["current"] = link.Current, ["forSession"] = link.ForSession,
                ["build"] = link.Generation?.Build, ["options"] = new JsonArray((link.Generation?.Options ?? []).Select(o => (JsonNode)o).ToArray()),
                ["processId"] = link.ProcessId, ["sessions"] = new JsonArray(sessions.Select(s => (JsonNode)s).ToArray()),
                ["turns"] = sessions.Count(turnRuns), ["stopping"] = link.Stopping,
            });
        }
        return new JsonObject
        {
            ["name"] = Name,
            ["displayName"] = DisplayName,
            ["running"] = IsRunning,
            ["host"] = General?.HostId,
            ["generation"] = Expected?.ToString(),
            ["hosts"] = hosts,
            ["agentInfo"] = Info?.AgentInfo is { } a ? AcpJson.ToNode(a) : null,
        };
    }

    /// <summary>
    /// Stops the adapter. A host is stopped only when the service shuts down
    /// (<see cref="Conductor.StopHostsOnExit"/>); for a restart the hosts keep their agents and come
    /// back to the next conductor.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<HostLink> all;
        lock (linksGate)
        {
            all = [.. links];
            links.Clear();
            route.Clear();
        }
        var running = Interlocked.Exchange(ref instance, null);
        foreach (var link in all)
        {
            if (link.HostId is not null && conductor.StopHostsOnExit)
                try { await link.Rpc.SendRequestAsync(GorynychProtocol.MethodHostStop, new JsonObject()).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception e) when (e is IOException or JsonRpcException or TimeoutException) { ServiceLog.Warn($"{Name}: host {link.HostId} did not take its stop: {e.Message}"); }
            await link.Rpc.DisposeAsync();
        }
        if (running is not null) await running.DisposeAsync();
    }
}
