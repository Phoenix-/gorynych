using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service.Hosts;

namespace Gorynych.Service;

public sealed class ConductorOptions
{
    public required string DataDirectory { get; init; }
    public required string ServiceId { get; init; }
    public string? DefaultAgent { get; init; }
    public int JournalMaxEntries { get; init; } = 5000;
    public int ListPageSize { get; init; } = 50;

    /// <summary>
    /// The command that runs the peers MCP server (<see cref="PeersServer"/>), given to every
    /// session in <c>mcpServers</c> with a token; null gives agents no peer tools.
    /// </summary>
    public IReadOnlyList<string>? PeerServerCommand { get; init; }

    /// <summary>
    /// How often a session's bare activity (a new <c>updatedAt</c>) may be announced with
    /// <c>_gorynych/sessions_changed</c>; the last change in an interval is announced at its end.
    /// </summary>
    public TimeSpan ActivityNoticeInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// While an agent outside Gorynych waits for an answer, how often it gets a progress
    /// notification at the latest, so its host does not take the call for idle.
    /// </summary>
    public TimeSpan OutsideProgressInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a session stays loaded in its agent once it is idle (no turn running, no operation
    /// registered) with no client attached; then it is closed there. Zero closes it at once.
    /// </summary>
    public TimeSpan UnloadAfter { get; init; } = DefaultUnloadAfter;

    /// <summary>The default of <see cref="UnloadAfter"/>: ten minutes.</summary>
    public static readonly TimeSpan DefaultUnloadAfter = TimeSpan.FromMinutes(10);

    /// <summary>The clock of <see cref="UnloadAfter"/>; tests pass one they move by hand.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// How long a draft waits for a client on the resumable link that went (its connection dropped,
    /// the service restarted) before it is discarded: longer than the link keeps trying.
    /// </summary>
    public TimeSpan ComeBackGrace { get; init; } = DefaultComeBackGrace;

    /// <summary>The default of <see cref="ComeBackGrace"/>: two minutes.</summary>
    public static readonly TimeSpan DefaultComeBackGrace = TimeSpan.FromMinutes(2);

    /// <summary>For tests: awaited when a catch-up of the journal starts, after the agent answered the open.</summary>
    internal Func<string, Task>? CatchingUp { get; init; }

    /// <summary>For tests: awaited in <c>session/new</c> once its client is attached, before the answer is written.</summary>
    internal Func<string, Task>? Answering { get; init; }

    /// <summary>
    /// Runs every adapter in an adapter host of its own (docs/service-protocol.md, "Adapter hosts"),
    /// so a restart breaks no turn; null runs them in this process, as before.
    /// </summary>
    public IHostLauncher? Hosts { get; init; }

    /// <summary>The token a host proves itself with (<c>host.token</c> in the data folder).</summary>
    public string? HostToken { get; init; }

    /// <summary>
    /// The generation of host the conductor would start now for an adapter: this executable's build
    /// and the adapter's options. A host of another is stale; its sessions move as they go idle. Null,
    /// or a null answer: every host counts as current.
    /// </summary>
    public Func<string, HostGeneration?>? HostGeneration { get; init; }

    /// <summary>The adapters that run each session in a host of its own (Claude), rather than all in one.</summary>
    public IReadOnlySet<string> SessionHosts { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>How often the conductor looks for idle sessions to move off stale hosts, besides when a session goes idle.</summary>
    public TimeSpan MoveSweep { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>For tests: awaited when a restart has waited for the running turns.</summary>
    internal Func<Task>? Drained { get; init; }

    /// <summary>For tests: awaited before a forced restart cancels a session's turn.</summary>
    internal Func<string, Task>? Cancelling { get; init; }
}

/// <summary>
/// The ACP conductor: an ACP agent with many sessions for its clients and an ACP client of
/// every adapter. It routes each session to the adapter that owns it, journals the feed,
/// replays it on <c>session/load</c> and relays the agents' requests (approvals) to a
/// client attached to the session, holding them until one attaches.
/// </summary>
public sealed class Conductor : IAsyncDisposable
{
    readonly ConductorOptions options;
    readonly Dictionary<string, AgentHost> agents = new(StringComparer.Ordinal);
    readonly SessionStore store;
    readonly string journalDirectory;
    readonly ConcurrentDictionary<string, SessionFeed> feeds = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<ClientConnection, byte> clients = new();
    readonly CancellationTokenSource shutdown = new();
    readonly ConcurrentDictionary<string, string> peerTokens = new(StringComparer.Ordinal);   // token -> session
    readonly ConcurrentDictionary<string, string> sessionTokens = new(StringComparer.Ordinal); // session -> token
    readonly ConcurrentDictionary<string, PendingOpinion> pendingOpinions = new(StringComparer.Ordinal); // message_id -> request
    readonly ConcurrentDictionary<string, byte> removed = new(StringComparer.Ordinal); // deleted in this run
    readonly List<string> startupDrafts;
    readonly ConcurrentDictionary<Task, byte> cleanups = new();
    readonly CancellationTokenSource cleanupStop = new();
    readonly ConcurrentDictionary<string, long> lastUpdateNotice = new(StringComparer.Ordinal); // session -> tick of its last "added" or "updated"
    readonly ConcurrentDictionary<string, byte> trailingUpdateNotice = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<Task, byte> backgroundTurns = new();
    readonly SemaphoreSlim archiveGate = new(1, 1); // archive changes, so their session_info_updates keep the flag's order

    long opinionCount;
    readonly string runTag = GorynychProtocol.NewRunTag(); // in message IDs: m<tag>-<n>
    long spawnCount;

    /// <summary>
    /// The gorynych tools whose approval the service answers yes for an agent that asks for MCP tools
    /// (Kimi): all of them (docs/service-protocol.md, "Approvals"). The service itself asks the person
    /// where it must: before a spawn the user did not ask for, and before a session not in full access
    /// asks one that is.
    /// </summary>
    static readonly HashSet<string> PreAllowedTools = [.. GorynychProtocol.PeerTools];

    /// <summary>The title source of a name given to <c>spawn_session</c>: harness titles do not replace it.</summary>
    const string SpawnTitle = "spawn";

    public Conductor(ConductorOptions options, IEnumerable<IAgentAdapter> adapters)
    {
        this.options = options;
        Directory.CreateDirectory(options.DataDirectory);
        journalDirectory = Path.Combine(options.DataDirectory, "journal");
        Directory.CreateDirectory(journalDirectory);
        store = new SessionStore(Path.Combine(options.DataDirectory, "sessions.json"));
        startupDrafts = store.All().Where(r => r.Draft).Select(r => r.SessionId).ToList();
        foreach (var a in adapters) agents[a.Name] = new AgentHost(a, this);
        DefaultAgent = options.DefaultAgent ?? agents.Keys.FirstOrDefault() ?? throw new ArgumentException("no adapters");
        store.Changed = OnRecordChanged;
        store.Removed = r => { if (r.Listed) AnnounceList(r.SessionId, r.Agent, "removed"); };
        store.Active = AnnounceActivity;
        Outside = new OutsideService(this, options.OutsideProgressInterval);
        // A session's peer token outlives this run: the agent's peers server (in a host) keeps it.
        foreach (var r in store.All())
            if (r.PeerToken is { Length: > 0 } token && !r.Deleted)
            {
                peerTokens[token] = r.SessionId;
                sessionTokens[r.SessionId] = token;
            }
    }

    public string DefaultAgent { get; }

    /// <summary>Starts the adapter hosts; null when the adapters run in this process.</summary>
    internal IHostLauncher? Hosts => options.Hosts;

    internal HostGeneration? ExpectedGeneration(string adapter) => options.HostGeneration?.Invoke(adapter);

    internal bool HostPerSession(string adapter) => options.SessionHosts.Contains(adapter);

    /// <summary>
    /// Whether the hosts stop with this conductor: yes when the service shuts down; no for a
    /// restart, after which they come back to the next conductor with their agents.
    /// </summary>
    public bool StopHostsOnExit { get; private set; } = true;

    // ---------------------------------------------------------------- adapter hosts

    /// <summary>
    /// A host connected to the host pipe: it must say hello first, with the token from the data
    /// folder, an adapter this service has, and a link version it takes; then its adapter's
    /// <see cref="AgentHost"/> takes it on (<see cref="AgentHost.AttachAsync"/>).
    /// </summary>
    public async Task ServeHostAsync(Stream input, Stream output, string name)
    {
        var rpc = new JsonRpcConnection(input, output, name) { Log = m => ServiceLog.Warn(m) };
        var hello = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.NotificationHandler = (method, p) =>
        {
            if (method == GorynychProtocol.MethodHostHello && p is JsonObject said) hello.TrySetResult(said);
            return Task.CompletedTask;
        };
        rpc.Start();
        try
        {
            var said = await hello.Task.WaitAsync(TimeSpan.FromSeconds(10), shutdown.Token);
            // What a process of the user's sends, before it proved anything: read as it may be.
            static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
            var adapter = Text(said["adapter"]);
            var hostId = Text(said["hostId"]);
            var link = said["link"] is JsonValue l && l.TryGetValue<int>(out var version) ? version : 0;
            var slot = Text(said["slot"]) ?? "";
            var build = Text(said["build"]);
            var hostOptions = (said["options"] as JsonArray ?? []).Select(Text).OfType<string>().ToList();
            var pid = said["pid"] is JsonValue p && p.TryGetValue<int>(out var processId) ? processId : 0;
            string? refusal = null;
            if (!HostNames.SameToken(Text(said["token"]), options.HostToken)) refusal = "its token is not this service's";
            else if (adapter is null || !agents.ContainsKey(adapter)) refusal = $"this service has no adapter '{adapter}'";
            else if (string.IsNullOrEmpty(hostId)) refusal = "it has no ID";
            else if (link < GorynychProtocol.MinHostLinkVersion || link > GorynychProtocol.HostLinkVersion)
                refusal = $"it speaks host link {link}, and this service takes {GorynychProtocol.MinHostLinkVersion} to {GorynychProtocol.HostLinkVersion}";
            else if (options.Hosts is null) refusal = "this service runs its adapters in its own process";
            if (refusal is not null)
            {
                // Stopped, not only closed: else it would try again until it is an orphan, and keep the
                // adapter's mutex, so that no host of this service's could start meanwhile.
                ServiceLog.Warn($"{name}: a host was refused, and stopped: {refusal}");
                try { await rpc.SendRequestAsync(GorynychProtocol.MethodHostStop, new JsonObject()).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception e) when (e is IOException or JsonRpcException or TimeoutException) { }
                await rpc.DisposeAsync();
                return;
            }
            var greeting = new HostHello(hostId!, slot, build is null ? null : new HostGeneration(build, hostOptions), pid);
            if (!await agents[adapter!].AttachAsync(rpc, greeting, shutdown.Token)) await rpc.DisposeAsync();
        }
        catch (Exception e)
        {
            ServiceLog.Warn($"{name}: a host did not get through its handshake: {(e is TimeoutException or OperationCanceledException or IOException or JsonRpcException ? e.Message : e.ToString())}");
            await rpc.DisposeAsync();
        }
    }

    /// <summary>
    /// The turns a host that came back has running: each holds its session as a turn would
    /// (<see cref="SessionFeed.AdoptedTurn"/>), so a new prompt waits for it, a resuming client is
    /// told it is in progress, and the end the host reports later is marked.
    /// </summary>
    internal async Task AdoptAsync(AgentHost host, HostLink link, JsonObject report, CancellationToken ct)
    {
        foreach (var turn in (report["turns"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)turn["sessionId"] is not { } sessionId || removed.ContainsKey(sessionId)) continue;
            var promptId = (string?)turn["promptId"];
            long? hostTurn = turn["turn"] is JsonValue t && t.TryGetValue<long>(out var number) ? number : null;
            var feed = Feed(sessionId);
            if (feed.Adopted is not null) continue;
            // Nothing takes the turn of a session of this adapter before its host is here; were it
            // taken all the same, the turn runs untaken, and its end is marked when it comes.
            if (!await feed.TurnGate.WaitAsync(AdoptWait, ct))
            {
                ServiceLog.Warn($"{host.Name} {sessionId}: the session's turn stayed taken for {AdoptWait.TotalSeconds:0} s; its turn in the host is not taken back");
                continue;
            }
            var key = feed.BeginPrompt(promptId);
            feed.Handed(key);
            if (!feed.Adopt(new SessionFeed.AdoptedTurn(key, promptId, host.Name, hostTurn, link.HostId)))
            {
                feed.EndPrompt(key);
                feed.TurnGate.Release();
                continue;
            }
            feed.ClientTurn = true;
            feed.EnterUse();
            // A spawned session reports the end of its turn to its parent: with the text that comes from now on.
            if (store.Get(sessionId)?.ParentSessionId is not null) feed.BeginTurnText(ReportLimit + 1);
            ServiceLog.Info($"{host.Name} {sessionId}: took back a turn running in its host{(promptId is null ? "" : $" (prompt {promptId})")}");
        }
    }

    internal static TimeSpan AdoptWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The end of an adopted turn: the session is free again.</summary>
    void EndAdopted(SessionFeed feed, SessionFeed.AdoptedTurn turn)
    {
        feed.EndPrompt(turn.Key);
        feed.ClientTurn = false;
        feed.TurnEnded();
        feed.TurnGate.Release();
        feed.ExitUse();
        IdleFromNow(feed.SessionId);
    }

    static bool SameList(List<string>? a, List<string>? b) => (a ?? []).SequenceEqual(b ?? [], StringComparer.Ordinal);

    /// <summary>
    /// After a host is taken: the sessions a service before left open in it (<see cref="SessionRecord.HostOf"/>)
    /// are settled by what it reported: the turns it does not run and whose end is not in the journal
    /// ended nowhere (they were cut off with an earlier host of theirs), and each ends cancelled, with
    /// its mark. So are those of the adapter's sessions whose host is gone for good: it runs no more,
    /// or another host has its slot now. A host that came back has sent the ends it had by now.
    /// </summary>
    /// <param name="report">What a host that came back reported; null for a new host, which runs none of them.</param>
    internal async Task SettleAsync(AgentHost host, HostLink link, JsonObject? report)
    {
        var running = (report?["turns"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(t => (string?)t["promptId"]).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var record in store.All())
        {
            if (record.Agent != host.Name || removed.ContainsKey(record.SessionId)) continue;
            if (record.HostOf is null && record.HostPrompts is not { Count: > 0 }) continue;
            // A record of an earlier version names no host: its prompts were left to the one adapter-wide host.
            var its = record.HostOf == link.HostId || (record.HostOf is null && link.Slot.Length == 0);
            if (!its && host.HostIsAround(record)) continue;
            // One of this host's keeps its host named until the host is taken (HostTaken): until then
            // requests do not see it (AgentHost.AttachAsync), and a resume of the session waits for it by
            // that name, rather than open it in a second host.
            await SettlePromptsAsync(host, record, its ? running : [], keepHost: its && record.HostOf is not null);
        }
    }

    /// <summary>
    /// A host that came back is taken: its sessions are routed to it now, so their records no longer
    /// name it (SettleAsync kept it named until now). The exit names again where each one is open.
    /// </summary>
    internal void HostTaken(AgentHost host, HostLink link)
    {
        if (link.HostId is not { } hostId) return;
        foreach (var record in store.All())
            if (record.Agent == host.Name && record.HostOf == hostId && record.HostPrompts is null && !removed.ContainsKey(record.SessionId))
                store.Update(record.SessionId, record.Agent, r =>
                {
                    if (r.HostOf != hostId) return;
                    r.HostOf = null;
                    r.HostSlot = null;
                });
    }

    /// <summary>A session's host from before is gone for good (<see cref="AgentHost.AwaitHostOfAsync"/>): its turns there ended nowhere.</summary>
    internal Task SettleGoneAsync(AgentHost host, SessionRecord record) => SettlePromptsAsync(host, record, [], keepHost: false);

    async Task SettlePromptsAsync(AgentHost host, SessionRecord record, HashSet<string> running, bool keepHost)
    {
        var feed = Feed(record.SessionId);
        foreach (var promptId in record.HostPrompts ?? [])
        {
            if (running.Contains(promptId) || feed.HasEndMark(promptId)) continue;
            ServiceLog.Warn($"{host.Name} {record.SessionId}: prompt {promptId} went on in a host that is gone; it ends cancelled");
            await MarkPromptEndAsync(feed, promptId, null, new IOException("its host went"));
        }
        if (keepHost && record.HostPrompts is null) return;
        store.Update(record.SessionId, record.Agent, r =>
        {
            r.HostPrompts = null;
            if (keepHost) return;
            r.HostOf = null;
            r.HostSlot = null;
        });
    }

    // ---------------------------------------------------------------- moving sessions off stale hosts

    int sweeping; // a sweep of stale hosts runs

    /// <summary>A host connected: if any of the adapter's is stale, its idle sessions move now, and a sweep looks again now and then.</summary>
    internal void HostAttached(AgentHost host, HostLink link)
    {
        host.Reap();
        if (host.Links.Any(l => !l.Current)) RunCleanup(() => MoveIdleAsync(host));
        if (Interlocked.Exchange(ref sweeping, 1) == 0) Track(Task.Run(SweepStaleAsync));
    }

    async Task SweepStaleAsync()
    {
        try
        {
            while (!cleanupStop.IsCancellationRequested)
            {
                await Task.Delay(options.MoveSweep, cleanupStop.Token);
                foreach (var host in agents.Values)
                    if (host.Links.Any(l => !l.Current)) await MoveIdleAsync(host);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Moves every idle session off the adapter's stale hosts, and stops those that are left with none.</summary>
    async Task MoveIdleAsync(AgentHost host)
    {
        foreach (var link in host.Links.Where(l => !l.Current))
            foreach (var sessionId in host.SessionsOf(link))
                try { await TryMoveAsync(sessionId, holdingTurn: false, cleanupStop.Token); }
                catch (Exception e) when (e is not OperationCanceledException) { ServiceLog.Warn($"{host.Name} {sessionId}: not moved: {e.Message}"); }
        host.Reap();
    }

    /// <summary>A session went idle: if it is open in a stale host, it moves now.</summary>
    void MoveIfStale(string sessionId)
    {
        if (options.Hosts is null || store.Get(sessionId) is not { } record || !agents.TryGetValue(record.Agent, out var host) || !host.IsStale(sessionId)) return;
        RunCleanup(async () =>
        {
            try { await TryMoveAsync(sessionId, holdingTurn: false, cleanupStop.Token); }
            catch (Exception e) when (e is not OperationCanceledException) { ServiceLog.Warn($"{host.Name} {sessionId}: not moved: {e.Message}"); }
        });
    }

    /// <summary>What became of a session that a move was tried for (<see cref="TryMoveAsync"/>).</summary>
    internal enum Move
    {
        /// <summary>It is open in no stale host now: nowhere (its next use opens it in a current one), or already in a current one.</summary>
        Done,
        /// <summary>Its agent runs a turn of its own in it, asks something in it, or a turn of a client's runs or waits: it moves once that ends.</summary>
        Wait,
        /// <summary>It stays where it is for now: a draft its agent cannot open elsewhere, or a host that goes only as a whole while another session of it is busy.</summary>
        Stay,
    }

    /// <summary>How long the close of a session that moves may take before the move counts as failed.</summary>
    internal static TimeSpan MoveCloseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a turn that waits for its session to move tries again, besides when its agent's turn or question ends.</summary>
    internal static TimeSpan MoveRetry { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Whether the session could open in another instance of its agent: it had a turn, or its agent
    /// keeps a draft (Kimi, DSH) or opens one afresh under its ID (the Claude adapter, <c>gorynych/draft</c>).
    /// Codex keeps nothing of a thread before its first turn (<c>gorynych/draftsNotDurable</c>): its
    /// draft can be had only in the app-server that made it.
    /// </summary>
    static bool OpensElsewhere(HostLink on, SessionFeed feed) => !on.DraftsNotDurable || feed.Journal.HasTurns;

    /// <summary>
    /// Moves a session open in a host of an earlier generation to one of the current, if it is idle
    /// there: no turn runs or waits in it (or this caller holds its turn, to run one in the new host),
    /// its agent asks nothing in it, and it could open elsewhere (<see cref="OpensElsewhere"/>). It is
    /// closed in the old host (Codex lets go of the thread's writer lock before the close answers), and
    /// the next use opens it in a host of the current generation, which catches its journal up from its
    /// mark (<see cref="OpenAsync"/>): what the journal has is not sent again, and what it lacks is
    /// added. A prompt that comes meanwhile waits for the session's gates and goes to the new host. A
    /// close that fails leaves it to its old host (<see cref="AgentHost.Pin"/>). An agent that cannot
    /// close one session has its stale host stopped as a whole instead (<see cref="TryStopWholeAsync"/>).
    /// </summary>
    internal async Task<Move> TryMoveAsync(string sessionId, bool holdingTurn, CancellationToken ct)
    {
        if (removed.ContainsKey(sessionId) || store.Get(sessionId) is not { Deleted: false } record
            || !agents.TryGetValue(record.Agent, out var host)) return Move.Stay;
        if (host.RouteOf(sessionId) is not { Current: false } stale) return Move.Done;
        var feed = Feed(sessionId);
        if (!OpensElsewhere(stale, feed)) return Move.Stay;
        if (!stale.CanClose) return await TryStopWholeAsync(host, stale, holdingTurn ? sessionId : null);
        await feed.LifeGate.WaitAsync(ct);
        var turnTaken = false;
        try
        {
            if (!holdingTurn)
            {
                if (!feed.TurnGate.Wait(0)) return Move.Wait;
                turnTaken = true;
            }
            if (feed.AgentTurnRunning || feed.Asking > 0) return Move.Wait;
            await feed.OpenGate.WaitAsync(ct);
            try
            {
                if (host.RouteOf(sessionId) is not { Current: false } on) return Move.Done;
                if (!OpensElsewhere(on, feed)) return Move.Stay;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(MoveCloseTimeout);
                if (await CloseInAgentAsync(host, on.Rpc, sessionId, ct, timeout.Token))
                    ServiceLog.Info($"{host.Name} {sessionId}: moved off host {on.HostId} ({on.Generation?.ToString() ?? "an earlier version"}); " +
                        "it opens in a host of the current generation at its next use");
                else
                    ServiceLog.Warn($"{host.Name} {sessionId}: not moved off host {on.HostId}: its close failed there; it opens there again at its next use, " +
                        "and moves once a close succeeds or that host has exited");
                return Move.Done;
            }
            finally
            {
                feed.OpenGate.Release();
            }
        }
        finally
        {
            if (turnTaken) feed.TurnGate.Release();
            feed.LifeGate.Release();
        }
    }

    /// <summary>
    /// An agent that cannot close one session (none today): its stale host goes as a whole, once none
    /// of its sessions has a turn running or waiting or its agent asking something, and each could open
    /// elsewhere. The host is marked stopping first, so nothing new goes to it; then each session's gates
    /// are taken without waiting (one that is busy calls the stop off, and a later idle moment tries
    /// again); then the host is stopped, and its sessions are open nowhere before the gates let go, so a
    /// prompt that waited for them opens its session in a host of the current generation. Drafts there go
    /// with it as any session does: each opens again where its agent keeps it or opens it afresh.
    /// </summary>
    /// <param name="turnHeldFor">The session whose turn the caller holds, for a prompt in it.</param>
    internal async Task<Move> TryStopWholeAsync(AgentHost host, HostLink link, string? turnHeldFor)
    {
        // Another stop of it is on its way, or it is gone: the session stays where it is open now.
        if (!host.BeginStop(link)) return turnHeldFor is not null && host.RouteOf(turnHeldFor) != link ? Move.Done : Move.Stay;
        var held = new List<(SessionFeed Feed, bool Turn)>();
        var stopped = false;
        try
        {
            foreach (var sessionId in host.SessionsOf(link))
            {
                var feed = Feed(sessionId);
                var mine = sessionId == turnHeldFor;
                // The session the caller holds the turn of waits for its own agent; another one that is busy keeps the host for now.
                var busy = mine ? Move.Wait : Move.Stay;
                if (!feed.LifeGate.Wait(0)) return busy;
                if (!mine && !feed.TurnGate.Wait(0))
                {
                    feed.LifeGate.Release();
                    return busy;
                }
                if (!feed.OpenGate.Wait(0))
                {
                    if (!mine) feed.TurnGate.Release();
                    feed.LifeGate.Release();
                    return busy;
                }
                held.Add((feed, !mine));
                if (feed.AgentTurnRunning || feed.Asking > 0) return busy;
                if (!OpensElsewhere(link, feed)) return Move.Stay;
            }
            await host.StopAndForgetAsync(link, "none of its sessions is busy, and its agent cannot close them one by one");
            stopped = true;
            return Move.Done;
        }
        finally
        {
            if (!stopped) host.CancelStop(link);
            foreach (var (feed, turn) in held)
            {
                feed.OpenGate.Release();
                if (turn) feed.TurnGate.Release();
                feed.LifeGate.Release();
            }
        }
    }

    /// <summary>
    /// What keeps a session in a stale host, for <c>_gorynych/status</c>: what it waits for before it
    /// moves (<see cref="TryMoveAsync"/>).
    /// </summary>
    string WhyNotMoved(HostLink link, string sessionId)
    {
        var feed = Feed(sessionId);
        if (feed.TurnRunning || feed.AgentTurnRunning) return "a turn runs in it: it moves once the turn ends";
        if (feed.Asking > 0) return "its agent asks something in it: it moves once that is answered";
        if (!OpensElsewhere(link, feed))
            return "a draft its agent cannot open in another host: it moves after its first turn there, or goes when its client lets go of it";
        if (!link.CanClose) return "its agent cannot close one session: the host goes as a whole once none of its sessions is busy";
        return "it moves at the next look";
    }

    /// <summary>
    /// With the session's turn held: the connection the turn goes to. A session open in a host of an
    /// earlier generation moves now to one of the current, so a stale host takes no new turn and
    /// empties; one whose agent runs a turn of its own or asks something is waited for, then moves; one
    /// that moved while this waited for the turn is followed. What stays in a stale host
    /// (<see cref="Move.Stay"/>) takes the turn there. A session its agent opened for reading only is
    /// opened again first (<see cref="RetakeAsync"/>), and refused while it is still read-only.
    /// </summary>
    /// <param name="forAgent">The turn is another session's message (a question, a report): a refusal is worded for an agent.</param>
    async Task<JsonRpcConnection> RpcForTurnAsync(SessionRecord record, JsonRpcConnection rpc, CancellationToken ct, bool forAgent = false)
    {
        var host = HostOf(record);
        var feed = Feed(record.SessionId);
        if (host.RouteOf(record.SessionId) is { Current: true } on && on.Rpc == rpc && !feed.OpenedReadOnly) return rpc;
        var live = (await EnsureLiveAsync(record, ct, holdingTurn: true)).Rpc;
        return feed.OpenedReadOnly ? await RetakeAsync(host, record, live, forAgent ? ReadOnlyUse.Message : ReadOnlyUse.Prompt, ct) : live;
    }

    /// <summary>What a read-only session was refused for, which its refusal's words follow.</summary>
    enum ReadOnlyUse
    {
        /// <summary>A client's prompt.</summary>
        Prompt,
        /// <summary>Another session's message: a question, a report.</summary>
        Message,
        /// <summary>A change of its settings: <c>session/set_config_option</c>, <c>session/set_mode</c>, <c>session/set_model</c>.</summary>
        Setting,
    }

    // ---------------------------------------------------------------- read-only sessions

    /// <summary>
    /// A turn for a session its agent opened for reading only: the agent is asked to open it again
    /// (<c>session/resume</c>, with the catch-up after the journal's mark), which takes it if it is
    /// free now (Codex takes the thread once the other app let it go), and brings into the journal
    /// what was made in the other app meanwhile. Still read-only, the turn is refused in words.
    /// </summary>
    async Task<JsonRpcConnection> RetakeAsync(AgentHost host, SessionRecord record, JsonRpcConnection rpc, ReadOnlyUse use, CancellationToken ct)
    {
        var feed = Feed(record.SessionId);
        await feed.OpenGate.WaitAsync(ct);
        try
        {
            if (feed.OpenedReadOnly && host.RouteOf(record.SessionId) is { } on)
            {
                await OpenAsync(host, on.Rpc, store.Get(record.SessionId) ?? record, ct);
                rpc = on.Rpc;
            }
        }
        finally
        {
            feed.OpenGate.Release();
        }
        if (feed.ReadOnly is { } state) throw ReadOnlyRefusal(record, state, use);
        return rpc;
    }

    /// <summary>
    /// The refusal of a turn or a change in a read-only session: its ID, why, and what to do, for
    /// the person or the asking agent. No <c>data</c>, which Zed would append to the message as raw
    /// JSON: the client has the flag from the session's state (load, resume and updates).
    /// </summary>
    static JsonRpcException ReadOnlyRefusal(SessionRecord record, SessionFeed.ReadOnlyState state, ReadOnlyUse use) => new(
        JsonRpcException.InvalidRequest, use switch
        {
            ReadOnlyUse.Message => $"Session {record.SessionId} is read-only for now, so it takes no message: {state.Reason} Ask again once it is free, or ask another session.",
            ReadOnlyUse.Setting => $"Session {record.SessionId} is read-only for now, so its settings cannot change: {state.Reason} Then change them again.",
            _ => $"Session {record.SessionId} is read-only for now: {state.Reason} Then send the prompt again.",
        });

    /// <summary>The read-only state a <c>_meta</c> says (<c>gorynych/interactivity</c>); null for full, also when it says nothing.</summary>
    static SessionFeed.ReadOnlyState? ReadOnlyOf(JsonNode? meta)
    {
        if (meta is not JsonObject m || TextOf(m[GorynychProtocol.MetaInteractivity]) != GorynychProtocol.InteractivityReadOnly) return null;
        return new(TextOf(m[GorynychProtocol.MetaInteractivityReason]) is { Length: > 0 } reason ? reason : "Its agent takes no prompt in it now.",
            TextOf(m[GorynychProtocol.MetaOpenElsewhere]));
    }

    static string? TextOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Says a session's interactivity in a <c>_meta</c>, in full (<c>full</c> too), with why and who holds it while it is read-only.</summary>
    static JsonObject PutInteractivity(JsonObject meta, SessionFeed.ReadOnlyState? state)
    {
        meta[GorynychProtocol.MetaInteractivity] = state is null ? GorynychProtocol.InteractivityFull : GorynychProtocol.InteractivityReadOnly;
        meta.Remove(GorynychProtocol.MetaInteractivityReason);
        meta.Remove(GorynychProtocol.MetaOpenElsewhere);
        if (state is null) return meta;
        meta[GorynychProtocol.MetaInteractivityReason] = state.Reason;
        if (state.OpenElsewhere is { } holder) meta[GorynychProtocol.MetaOpenElsewhere] = holder;
        return meta;
    }

    /// <summary>
    /// Takes the session's interactivity from what its agent said. When it changed, the attached
    /// clients are told: a <c>session_info_update</c> with the value in full, and a notice to those
    /// that show notices; neither is journaled, since it is the session's state, not its history.
    /// </summary>
    async Task SetInteractivityAsync(SessionFeed feed, SessionFeed.ReadOnlyState? state)
    {
        if (!feed.SetReadOnly(state)) return;
        ServiceLog.Info($"{feed.SessionId}: {(state is null ? "can be written to again" : $"read-only: {state.Reason}")}");
        var update = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = PutInteractivity(new JsonObject(), state) };
        await feed.PublishAsync(update, except: null, () => { }, journal: false);
        await feed.SendLiveAsync(NoticeOf(state), c => c.TakesNotices && c.Tell(feed.SessionId, state));
    }

    /// <summary>The notice that tells a person the session is read-only now, and why, or that it can be written to again.</summary>
    static JsonObject NoticeOf(SessionFeed.ReadOnlyState? state) => AcpJson.ToNode<SessionUpdate>(state is null
        ? new Notice { Severity = NoticeSeverity.Info, Title = "You can write here again", Description = "Nothing else holds this session now." }
        : new Notice { Severity = NoticeSeverity.Warning, Title = "Read-only for now", Description = state.Reason })!.AsObject();

    /// <summary>
    /// A client that opens a read-only session is told why it takes no prompt, if it shows notices,
    /// once: not again on the same connection, nor after a reconnect when its link says it shows it.
    /// </summary>
    static Task NoticeOnOpenAsync(SessionFeed feed, ClientConnection client)
    {
        // Also writable again, when its link said it shows the session read-only: a change it missed.
        var state = feed.ReadOnly;
        return client.Tell(feed.SessionId, state) && client.TakesNotices ? feed.SendLiveAsync(NoticeOf(state), c => ReferenceEquals(c, client)) : Task.CompletedTask;
    }

    /// <summary>For each session, its place in the host's stream: what the host sends again must follow it.</summary>
    internal JsonObject HostCursors(string hostId, IEnumerable<string> sessions)
    {
        var after = new JsonObject();
        foreach (var sessionId in sessions)
            if (!removed.ContainsKey(sessionId)) after[sessionId] = Feed(sessionId).HostCursor(hostId);
        return after;
    }

    /// <summary>A host kept fewer updates than the session missed: the journal lacks some, and says so.</summary>
    internal void HostTrimmed(AgentHost host, string sessionId)
    {
        ServiceLog.Warn($"{host.Name} {sessionId}: its host had let go of updates this service had not journaled; the journal is marked trimmed");
        Feed(sessionId).Journal.MarkTrimmed();
    }

    /// <summary>
    /// A host reports how a prompt ended, before its answer (<c>_gorynych/turn_ended</c>). The end is
    /// marked here, with the report's place in the host's stream, so it is in the journal exactly once:
    /// whether this service sent the prompt and its answer follows, or an earlier one sent it and the
    /// answer was lost with it. A turn taken back from the host (<see cref="AdoptAsync"/>) is over.
    /// </summary>
    async Task OnTurnEndedAsync(AgentHost host, JsonObject report)
    {
        if ((string?)report["sessionId"] is not { } sessionId || removed.ContainsKey(sessionId)) return;
        var feed = Feed(sessionId);
        var hostSeq = (string?)report["_meta"]?[GorynychProtocol.MetaHostSeq];
        if (hostSeq is not null && !feed.TakeFromHost(hostSeq)) return; // marked already
        var promptId = (string?)report["promptId"];
        var result = report["result"];
        Exception? failure = report["error"] is JsonObject error
            ? new JsonRpcException(error["code"] is JsonValue c && c.TryGetValue<int>(out var code) ? code : JsonRpcException.InternalError, (string?)error["message"] ?? "the prompt failed")
            : null;
        KeepMark(feed, result);
        if (promptId is not null) await MarkPromptEndAsync(feed, promptId, result, failure, hostSeq); // its answer, if it comes here, marks it no more
        long? hostTurn = report["turn"] is JsonValue t && t.TryGetValue<long>(out var number) ? number : null;
        if (feed.EndAdopted(hostTurn) is not { } turn) return;
        if (store.Get(sessionId)?.ParentSessionId is not null)
            ReportTurnEnd(sessionId, failure is null ? Outcome(result) : $"failed: {failure.Message}", feed.EndTurnText());
        EndAdopted(feed, turn);
        ServiceLog.Info($"{host.Name} {sessionId}: the turn it took back from its host ended{(promptId is null ? "" : $" (prompt {promptId})")}");
    }

    /// <summary>The service's side of the gorynych-outside MCP server, for agents outside Gorynych.</summary>
    internal OutsideService Outside { get; }
    public string ServiceId => options.ServiceId;
    public IReadOnlyDictionary<string, AgentHost> Agents => agents;
    public SessionStore Store => store;

    /// <summary>Cancelled when a client asks the service to exit.</summary>
    public CancellationToken ShutdownRequested => shutdown.Token;

    public void RequestShutdown()
    {
        try { shutdown.Cancel(); }
        catch (ObjectDisposedException) { } // disposed: stopped already
    }

    // ---------------------------------------------------------------- restart

    readonly Lock restartGate = new();
    readonly CancellationTokenSource drainStop = new(); // a restart with hosts: prompts waiting for a turn go to the next service
    Task<JsonObject>? restart;
    volatile bool draining;
    long restartNumber;

    /// <summary>How long a restart waits for the running turns by default.</summary>
    public static readonly TimeSpan DefaultRestartTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long the turns a forced restart cancels get to end before it exits all the same.</summary>
    internal static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long a restart with hosts waits for the prompts on their way to reach their turn (and be refused).</summary>
    internal static readonly TimeSpan HostedDrainLimit = TimeSpan.FromSeconds(10);

    /// <summary>True while a restart drains: no new turn starts (<see cref="Restarting"/>).</summary>
    public bool Draining => draining;

    /// <summary>
    /// The error for a request that would start a turn while the service restarts: not carried out,
    /// so a client on the resumable link sends it again, to the next service, or to this one should
    /// the restart be called off (<c>data["gorynych/restarting"]</c>, and <c>data["gorynych/restart"]</c>,
    /// the number of the restart, as in <c>_gorynych/restarting</c> and <c>_gorynych/restart_aborted</c>).
    /// </summary>
    internal JsonRpcException Restarting() => new(JsonRpcException.InternalError,
        "The Gorynych service is restarting; send it again in a moment.",
        new JsonObject { [GorynychProtocol.MetaRestarting] = true, [GorynychProtocol.MetaRestart] = Interlocked.Read(ref restartNumber) });

    /// <summary>The params of <c>_gorynych/restarting</c> and <c>_gorynych/restart_aborted</c>.</summary>
    JsonObject RestartNotice() => new() { ["restart"] = Interlocked.Read(ref restartNumber) };

    /// <summary>For a client that connects while a restart drains.</summary>
    internal JsonObject? RestartingNotice() => draining ? RestartNotice() : null;

    /// <summary>
    /// <c>_gorynych/restart {wait?, timeoutSeconds?, force?}</c>: the service starts no new turn and
    /// tells its clients (<c>_gorynych/restarting</c>), waits for the running turns
    /// (<see cref="DrainAsync"/>), answers, and exits. When turns still run after the wait, it
    /// calls the restart off and serves on, unless <c>force</c> says to cancel them and exit. A
    /// second request while one drains waits for the same drain.
    /// </summary>
    internal async Task<JsonNode?> RestartAsync(JsonRpcRequest request)
    {
        static bool Flag(JsonNode? node, bool otherwise) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : otherwise;
        var wait = Flag(request.Params?["wait"], true);
        var force = Flag(request.Params?["force"], false);
        var timeout = request.Params?["timeoutSeconds"] is JsonValue t && t.TryGetValue<double>(out var seconds) && double.IsFinite(seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(1).TotalSeconds))
            : DefaultRestartTimeout;
        Task<JsonObject> drain;
        lock (restartGate) drain = restart ??= Task.Run(() => DrainAsync(wait ? timeout : TimeSpan.Zero, force));
        var result = await drain;
        if (result["restarted"] is JsonValue r && r.TryGetValue<bool>(out var restarted) && restarted)
        {
            // Exit once the answer is out; and in any case soon, should the asking client be gone.
            request.AfterResult(() => { RequestShutdown(); return Task.CompletedTask; });
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => RequestShutdown(), TaskScheduler.Default);
        }
        return result.DeepClone();
    }

    /// <summary>
    /// Starts no new turn from now on, tells the clients, and waits up to <paramref name="timeout"/>
    /// for the turns that run (a client's, a service turn, one of the agent's own, a prompt waiting
    /// for one). If none is left it exits: the journals are written as each update comes, so nothing
    /// is left to flush but the overlay, which the exit writes. If some are left, it calls the
    /// restart off (<see cref="CallOff"/>), which an update must not break; with
    /// <paramref name="force"/> it cancels them, gives them <see cref="CancelGrace"/> to end, and
    /// exits all the same. Whatever goes wrong on the way, the service is left serving, not draining.
    /// </summary>
    async Task<JsonObject> DrainAsync(TimeSpan timeout, bool force)
    {
        var number = Interlocked.Increment(ref restartNumber);
        draining = true;
        var exiting = false;
        try
        {
            foreach (var c in clients.Keys) c.Notify(GorynychProtocol.MethodRestarting, RestartNotice());
            if (options.Hosts is not null)
            {
                // The turns live on in their hosts, which come back to the next conductor: nothing of
                // the agents is waited for or cancelled. Only prompts still on their way to their
                // agent are, which are refused (and go whole to the next service) once they reach
                // their turn, waiting for one included.
                ServiceLog.Info($"restart {number}: the agents run on in their hosts; refusing the prompts still on their way");
                drainStop.Cancel();
                var onTheirWay = System.Diagnostics.Stopwatch.StartNew();
                while (feeds.Values.Any(f => !f.Retired && f.PromptsOnTheirWay > 0) && onTheirWay.Elapsed < HostedDrainLimit) await Task.Delay(50);
                var inHosts = feeds.Values.Count(f => !f.Retired && f.TurnRunning);
                ServiceLog.Info($"restart {number}: exiting; {inHosts} turns go on in their hosts");
                StopHostsOnExit = false;
                exiting = true;
                return new JsonObject
                {
                    ["restarted"] = true, ["processId"] = Environment.ProcessId, ["waitedSeconds"] = Math.Round(onTheirWay.Elapsed.TotalSeconds, 1),
                    ["cancelledTurns"] = 0, ["turnsInHosts"] = inHosts,
                };
            }
            ServiceLog.Info($"restart {number}: no new turn starts; waiting up to {timeout.TotalSeconds:0.#} s for the running ones");
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (Running().Any() && waited.Elapsed < timeout) await Task.Delay(100);
            var seconds = Math.Round(waited.Elapsed.TotalSeconds, 1);
            if (options.Drained is { } drained) await drained();
            var left = Running().ToList();
            if (left.Count > 0 && !force)
            {
                ServiceLog.Info($"restart {number}: {left.Count} turns still run after {seconds} s; called off");
                return new JsonObject
                {
                    ["restarted"] = false, ["processId"] = Environment.ProcessId, ["waitedSeconds"] = seconds,
                    ["running"] = new JsonArray(left.Select(f => (JsonNode)RunningEntry(f.SessionId)).ToArray()),
                };
            }
            foreach (var feed in left)
            {
                ServiceLog.Info($"restart {number}: cancelling the turn of {feed.SessionId}");
                try
                {
                    if (options.Cancelling is { } cancelling) await cancelling(feed.SessionId);
                    await ForwardNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = feed.SessionId }, feed.SessionId)
                        .WaitAsync(CancelGrace);
                }
                catch (Exception e)
                {
                    // One turn that cannot be cancelled does not keep the others, or the exit.
                    ServiceLog.Warn($"restart {number}: the cancel of {feed.SessionId} did not reach its agent: {e.Message}");
                }
            }
            var grace = System.Diagnostics.Stopwatch.StartNew();
            while (left.Count > 0 && Running().Any() && grace.Elapsed < CancelGrace) await Task.Delay(100);
            ServiceLog.Info($"restart {number}: waited {seconds} s, cancelled {left.Count} turns; exiting");
            exiting = true;
            return new JsonObject
            {
                ["restarted"] = true, ["processId"] = Environment.ProcessId, ["waitedSeconds"] = seconds, ["cancelledTurns"] = left.Count,
            };
        }
        finally
        {
            if (!exiting) CallOff(number);
        }
    }

    /// <summary>
    /// The restart is called off (turns still run, or the drain failed): new turns start again, and
    /// the clients hear it (<c>_gorynych/restart_aborted</c>), so a client on the link sends this
    /// service what it held for the next one. A later restart drains afresh.
    /// </summary>
    void CallOff(long number)
    {
        lock (restartGate)
        {
            draining = false;
            restart = null;
        }
        foreach (var c in clients.Keys) c.Notify(GorynychProtocol.MethodRestartAborted, new JsonObject { ["restart"] = number });
    }

    /// <summary>A session a restart did not wait out: <c>{sessionId, agent, title}</c>.</summary>
    JsonObject RunningEntry(string sessionId)
    {
        var record = store.Get(sessionId);
        return new JsonObject { ["sessionId"] = sessionId, ["agent"] = record?.Agent, ["title"] = record?.Title };
    }

    IEnumerable<SessionFeed> Running() => feeds.Values.Where(f => !f.Retired && f.TurnRunning);

    /// <summary>Serves one client until it disconnects.</summary>
    public async Task ServeClientAsync(Stream input, Stream output, string name)
    {
        var client = new ClientConnection(this, new JsonRpcConnection(input, output, name));
        clients[client] = 0;
        ServiceLog.Info($"{name}: connected");
        try
        {
            client.Rpc.Start();
            await client.Rpc.Completion;
        }
        finally
        {
            clients.TryRemove(client, out _);
            foreach (var feed in feeds.Values) feed.Detach(client, notify: false);
            await client.Rpc.DisposeAsync();
            ServiceLog.Info($"{name}: disconnected");
            // A client that goes away lets go of its sessions, as session/close would: a GUI that
            // quits sends none. That includes one it was detached from while it went, by a request
            // or an update that failed first.
            foreach (var sessionId in client.Held) LetGo(sessionId, client.OnLink);
        }
    }

    /// <summary>The live side of a session; for one deleted in this run, a retired feed that keeps nothing.</summary>
    internal SessionFeed Feed(string sessionId)
    {
        if (!removed.ContainsKey(sessionId))
            return feeds.GetOrAdd(sessionId, id => new SessionFeed(id, new SessionJournal(JournalPath(id), options.JournalMaxEntries))
            {
                LastClientLeft = () => IdleFromNow(id),
                KeptHostSeen = store.Get(id)?.HostSeen,
            });
        var retired = new SessionFeed(sessionId, new SessionJournal(JournalPath(sessionId), options.JournalMaxEntries));
        retired.Retire();
        return retired;
    }

    string JournalPath(string sessionId) => Path.Combine(journalDirectory, SessionJournal.FileNameFor(sessionId));

    internal AgentHost HostOf(SessionRecord record) =>
        agents.TryGetValue(record.Agent, out var host)
            ? host
            : throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Session {record.SessionId} belongs to unknown agent {record.Agent}");

    internal AgentHost Agent(string? name)
    {
        name ??= DefaultAgent;
        return agents.TryGetValue(name, out var host)
            ? host
            : throw new JsonRpcException(JsonRpcException.InvalidParams, $"Unknown agent '{name}'. Known: {string.Join(", ", agents.Keys)}");
    }

    // ---------------------------------------------------------------- from agents

    internal async Task OnAgentNotificationAsync(AgentHost host, string method, JsonNode? @params)
    {
        if (method == GorynychProtocol.MethodSessionsChanged)
        {
            OnAgentListChanged(host, @params);
            return;
        }
        if (method == GorynychProtocol.MethodTurnEnded)
        {
            if (@params is JsonObject report) await OnTurnEndedAsync(host, report);
            return;
        }
        var sessionId = (string?)@params?["sessionId"];
        if (sessionId is null || removed.ContainsKey(sessionId)) return;
        var feed = Feed(sessionId);
        if (method == AcpMethods.Client.SessionUpdate)
        {
            if (@params!["update"] is not JsonObject update) return;
            // From a host: taken once, though a host that comes back sends what it kept again.
            if (update["_meta"]?[GorynychProtocol.MetaHostSeq] is JsonValue s && s.TryGetValue<string>(out var hostSeq) && !feed.TakeFromHost(hostSeq)) return;
            // Whether the session can be prompted now: its state, not its history, so it is not journaled.
            if (TextOf(update["sessionUpdate"]) == "session_info_update" && update["_meta"] is JsonObject said && said.ContainsKey(GorynychProtocol.MetaInteractivity))
            {
                var state = ReadOnlyOf(said);
                feed.ReportedReadOnly = state;
                // Writable again: what the other app made meanwhile first, read off this handler, which
                // the agent's replay must get through (WritableAgainAsync).
                if (state is null && feed.OpenedReadOnly && host.CatchesUp) RunCleanup(() => WritableAgainAsync(host, feed));
                else await SetInteractivityAsync(feed, state);
                return;
            }
            feed.ApplyStateUpdate(update);
            feed.Active(options.Time.GetUtcNow()); // an agent that sends something is not idle (UnloadIfIdleAsync)
            AgentTurn(feed, update);
            if (feed.CollectReplay(update)) return; // the agent's history, replayed for a catch-up (OpenAsync)
            if (SessionJournal.IsCommands(update))
            {
                await feed.PublishCommandsAsync(update); // the session's state, not its history, nor activity
                return;
            }
            KeepSpawnTitle(sessionId, update);
            // The end of a turn of the agent's own may carry the mark of its history (KeepMark).
            var mark = update["_meta"]?[GorynychProtocol.MetaHistoryMark]?.DeepClone();
            if (!feed.IsRepeatedTitle(update))
            {
                feed.TagOpinionTurn(update);
                await feed.PublishAsync(update, except: null, () => Remember(host, sessionId, update));
            }
            SetMark(feed, mark);
            return;
        }
        foreach (var c in feed.Clients())
            await feed.SendAsync(c, method, @params!.DeepClone());
    }

    /// <summary>
    /// A turn the agent runs that the service did not start (a message through Claude's channel,
    /// the person typing in its terminal), as the adapter marks it with
    /// <c>_meta["gorynych/agentTurn"]</c>: an operation on the session from <c>started</c> to
    /// <c>ended</c>, so the idle timer never unloads the session under it. A missed end keeps the
    /// session loaded until the agent stops (<see cref="OnAgentStopped"/>). Never waits for the
    /// session's gate: the agent's notifications must keep flowing while a close waits for it.
    /// </summary>
    void AgentTurn(SessionFeed feed, JsonObject update)
    {
        var mark = update["_meta"] is JsonObject meta && meta[GorynychProtocol.MetaAgentTurn] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        // The user did not start it, so a client's prompt that joins it does not make it theirs (SpawnAsync).
        if (mark == "started") feed.MarkOtherSessionText();
        if (mark == "started" && feed.BeginAgentTurn())
        {
            feed.EnterUse();
        }
        else if (mark == "ended" && feed.EndAgentTurn())
        {
            feed.TurnEnded();
            feed.ExitUse();
            IdleFromNow(feed.SessionId);
        }
    }

    /// <summary>
    /// An agent's connection closed: the turns of its own it marked are over, and so are the turns
    /// this service took over from its host, which end <c>cancelled</c>, with the mark of their end
    /// (the host died with them).
    /// </summary>
    internal void OnAgentStopped(AgentHost host, HostLink link, IReadOnlyCollection<string> sessions)
    {
        var open = sessions.ToHashSet(StringComparer.Ordinal);
        foreach (var feed in feeds.Values)
        {
            if (store.Get(feed.SessionId)?.Agent != host.Name) continue;
            // The adapter in this process runs all its sessions; a host, those open in it.
            var its = link.HostId is null || open.Contains(feed.SessionId);
            if (its && feed.EndAgentTurn())
            {
                feed.TurnEnded();
                feed.ExitUse();
            }
            if (feed.Adopted is { } adopted && (link.HostId is null || adopted.HostId == link.HostId) && feed.EndAdopted(any: true) is { } turn)
            {
                ServiceLog.Warn($"{host.Name} {feed.SessionId}: its host went in the middle of a turn it had brought back");
                Track(Task.Run(async () =>
                {
                    if (turn.PromptId is { } promptId) await MarkPromptEndAsync(feed, promptId, null, new IOException("the host went"));
                    if (store.Get(feed.SessionId)?.ParentSessionId is not null)
                        ReportTurnEnd(feed.SessionId, "failed: its host went", feed.EndTurnText());
                    EndAdopted(feed, turn);
                }));
            }
        }
    }

    void Remember(AgentHost host, string sessionId, JsonObject update)
    {
        if ((string?)update["sessionUpdate"] == "session_info_update" && (string?)update["title"] is { Length: > 0 } title)
        {
            // A title must not record a session the service does not know (yet): it would be listed and announced.
            if (store.Get(sessionId) is null) return;
            store.Update(sessionId, host.Name, r =>
            {
                r.UpdatedAt = DateTimeOffset.UtcNow;
                if (r.TitleSource == SpawnTitle) return;
                r.Title = title;
                r.TitleSource = "agent";
            });
        }
        else
        {
            store.Touch(sessionId);
        }
    }

    /// <summary>A session named by <c>spawn_session</c> keeps that name: the agent's own title is replaced with it.</summary>
    void KeepSpawnTitle(string sessionId, JsonObject update)
    {
        if ((string?)update["sessionUpdate"] != "session_info_update" || update["title"] is null) return;
        if (store.Get(sessionId) is { TitleSource: SpawnTitle, Title: { } name }) update["title"] = name;
    }

    readonly ConditionalWeakTable<JsonRpcRequest, SessionFeed> askedIn = new(); // the questions counted as they arrived, until they are over

    /// <summary>
    /// An agent's request was read off its connection (<see cref="JsonRpcConnection.RequestArrived"/>),
    /// before it is handled on the thread pool: a question in a session counts as asking from now, so a
    /// move that looks at the session after this sees it (<see cref="TryMoveAsync"/>). A host that comes
    /// back asks its questions again before it answers its replay, so they count before its sessions
    /// can move at all; one that goes before it asked them counts none.
    /// </summary>
    internal void OnAgentRequestArrived(JsonRpcRequest request)
    {
        if (request.Params is not JsonObject p || p["sessionId"] is not JsonValue v || !v.TryGetValue<string>(out var sessionId)
            || removed.ContainsKey(sessionId)) return;
        var feed = Feed(sessionId);
        feed.BeginAsking();
        askedIn.AddOrUpdate(request, feed);
    }

    /// <summary>
    /// A question counted as it arrived is over: its answer is written to the agent's connection (or
    /// could not be). Only now is the session idle: a move closes it in its host after the answer, on
    /// the same connection, so the agent has the answer before the close (<see cref="JsonRpcConnection.RequestDone"/>).
    /// </summary>
    internal void OnAgentRequestDone(JsonRpcRequest request)
    {
        if (!askedIn.TryGetValue(request, out var feed) || !askedIn.Remove(request)) return;
        feed.EndAsking();
        IdleFromNow(feed.SessionId);
    }

    /// <summary>
    /// Relays a request from an agent (an approval, an elicitation) to a client attached to
    /// its session. With no client attached it waits for one; if the client goes away before
    /// answering, the request goes to the next one.
    /// </summary>
    internal async Task<JsonNode?> OnAgentRequestAsync(AgentHost host, JsonRpcRequest request, CancellationToken ct)
    {
        var sessionId = (string?)request.Params?["sessionId"]
            ?? throw new JsonRpcException(JsonRpcException.MethodNotFound, $"{request.Method} is not supported by Gorynych");
        if (request.Method == AcpMethods.Client.SessionRequestPermission && PreAllowed(request.Params, Feed(sessionId)) is { } optionId)
        {
            ServiceLog.Info($"{host.Name} {sessionId}: allowed {(string?)request.Params?["toolCall"]?["title"] ?? (string?)request.Params?["toolCall"]?["toolCallId"]} without asking");
            return AcpJson.ToNode(new RequestPermissionResponse { Outcome = new SelectedPermissionOutcome { OptionId = optionId } });
        }
        var feed = Feed(sessionId);
        // From a host, a question outlives this service when it exits for a restart: its host asks
        // the next one, which a client's link joins to the one it shows. So the client keeps it, as
        // its connection goes with this service, rather than see it withdrawn.
        using var outlived = options.Hosts is null ? null : new CancellationTokenSource();
        await using var _ = outlived is null ? default : ct.Register(() =>
        {
            if (StopHostsOnExit) try { outlived.Cancel(); } catch (ObjectDisposedException) { }
        });
        if (outlived is not null) ct = outlived.Token;
        // A use of the session from the first wait for a client to the answer: while a client may
        // still answer, the session is not idle, so it stays loaded (IdleFromNow). Registered under
        // the session's gate, as UseAsync does, so an unload under way finishes first.
        await feed.LifeGate.WaitAsync(ct);
        try
        {
            feed.EnterUse();
        }
        finally { feed.LifeGate.Release(); }
        try
        {
            while (true)
            {
                var client = await feed.WaitForClientAsync(c => c.Accepts(request.Method), ct);
                try
                {
                    if (request.Method == AcpMethods.Client.ElicitationCreate && !client.Accepts(request.Method))
                        return await FormFallback.AskAsync(client, request, ct);
                    return await client.Rpc.SendRequestAsync(request.Method, request.Params?.DeepClone(), ct);
                }
                catch (Exception e) when (e is IOException or TimeoutException || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    feed.Detach(client);
                }
            }
        }
        finally
        {
            feed.ExitUse();
            // A question counted as it arrived goes idle once its answer is written (OnAgentRequestDone).
            if (!askedIn.TryGetValue(request, out var counted)) IdleFromNow(sessionId);
        }
    }

    // ---------------------------------------------------------------- for clients

    /// <param name="agentScope">Only this adapter's sessions (a client scoped to one agent), or all.</param>
    internal async Task<ListSessionsResponse> ListAsync(ListSessionsRequest request, CancellationToken ct, string? agentScope = null)
    {
        // Archived sessions are left out unless the client asks for them, as Codex and Kimi do.
        var includeArchived = request.Meta?[GorynychProtocol.MetaIncludeArchived] is JsonValue all && all.TryGetValue<bool>(out var include) && include;
        var found = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
        foreach (var host in agents.Values)
        {
            if (agentScope is not null && host.Name != agentScope) continue;
            try
            {
                var rpc = await host.GetAsync(ct);
                if (!host.CanList) continue;
                string? cursor = null;
                for (var page = 0; page < 20; page++)
                {
                    var r = await rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList,
                        new ListSessionsRequest { Cwd = request.Cwd, Cursor = cursor }, ct);
                    foreach (var s in r.Sessions)
                    {
                        if (removed.ContainsKey(s.SessionId) || store.Get(s.SessionId) is { Draft: true } or { Deleted: true }) continue;
                        var record = store.Update(s.SessionId, host.Name, rec =>
                        {
                            var discovered = rec.Cwd.Length == 0;
                            rec.Cwd = FolderPath.Normalize(s.Cwd);
                            if (s.Title is { Length: > 0 } t && rec.TitleSource != SpawnTitle) { rec.Title = t; rec.TitleSource = "agent"; }
                            if (ParseTime(s.UpdatedAt) is { } at && (discovered || at > rec.UpdatedAt)) rec.UpdatedAt = at;
                        });
                        if (record is not null && (includeArchived || !record.Archived)) found[s.SessionId] = Describe(record, s);
                    }
                    cursor = r.NextCursor;
                    if (cursor is null) break;
                }
            }
            catch (Exception e) when (e is IOException or JsonRpcException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ServiceLog.Warn($"{host.Name}: session/list failed: {e.Message}");
            }
        }
        foreach (var r in store.All())
        {
            if (found.ContainsKey(r.SessionId) || r.Draft || r.Deleted || (r.Archived && !includeArchived)) continue;
            if (agentScope is not null && r.Agent != agentScope) continue;
            if (request.Cwd is not null && !string.Equals(Path.GetFullPath(r.Cwd), Path.GetFullPath(request.Cwd), StringComparison.OrdinalIgnoreCase)) continue;
            if (agents.TryGetValue(r.Agent, out var host) && host.IsRunning && host.CanList) continue; // the agent no longer has it
            found[r.SessionId] = Describe(r, null);
        }
        var ordered = found.Values.OrderByDescending(s => ParseTime(s.UpdatedAt) ?? DateTimeOffset.MinValue).ToList();
        var offset = DecodeCursor(request.Cursor);
        var pageItems = ordered.Skip(offset).Take(options.ListPageSize).ToList();
        var next = offset + pageItems.Count < ordered.Count ? EncodeCursor(offset + pageItems.Count) : null;
        return new ListSessionsResponse { Sessions = pageItems, NextCursor = next };
    }

    /// <summary>
    /// A session's time in the list. The agent's own time wins, as its title does: a catch-up of the
    /// journal, or a row that changes its marks, does not move the session. But a turn the service
    /// saw end counts, should the agent note its end a moment later than the list is read.
    /// </summary>
    DateTimeOffset ListTime(SessionRecord r, SessionInfo? native)
    {
        var at = native?.UpdatedAt is { } said && ParseTime(said) is { } agentTime ? agentTime : r.UpdatedAt;
        return feeds.TryGetValue(r.SessionId, out var seen) && seen.LastTurnEnded is { } ended && ended > at ? ended : at;
    }

    SessionInfo Describe(SessionRecord r, SessionInfo? native)
    {
        var meta = native?.Meta?.DeepClone().AsObject() ?? [];
        // Open in a host of this service's (a stale one, during an update) is not open elsewhere, and
        // its interactivity is what its agent said when it opened it, or since.
        if (agents.TryGetValue(r.Agent, out var owner) && owner.IsLive(r.SessionId))
        {
            var state = feeds.TryGetValue(r.SessionId, out var feed) ? feed.ReadOnly : null;
            if (state is null)
            {
                meta.Remove(GorynychProtocol.MetaOpenElsewhere);
                meta.Remove(GorynychProtocol.MetaInteractivity);
                meta.Remove(GorynychProtocol.MetaInteractivityReason);
            }
            else PutInteractivity(meta, state);
        }
        meta[GorynychProtocol.MetaAgent] = r.Agent;
        if (r.ParentSessionId is not null) meta[GorynychProtocol.MetaParentSessionId] = r.ParentSessionId;
        if (r.Archived) meta[GorynychProtocol.MetaArchived] = true;
        return new SessionInfo
        {
            SessionId = r.SessionId,
            Cwd = FolderPath.Normalize(native?.Cwd ?? r.Cwd),
            AdditionalDirectories = native?.AdditionalDirectories,
            Title = r.TitleSource != SpawnTitle && native?.Title is { Length: > 0 } t ? t : r.Title,
            UpdatedAt = ListTime(r, native).ToString("O", CultureInfo.InvariantCulture),
            Meta = meta,
        };
    }

    /// <param name="afterAnswer">Runs an action once the answer is written to the client (<see cref="JsonRpcRequest.AfterResult"/>).</param>
    internal async Task<NewSessionResponse> NewSessionAsync(ClientConnection client, NewSessionRequest request, Action<Func<Task>> afterAnswer, CancellationToken ct)
    {
        // An explicit choice in the request wins; otherwise the agent the connection is scoped to.
        RefuseSystemPrompt(request.SystemPrompt);
        var host = Agent((string?)request.Meta?[GorynychProtocol.MetaAgent] ?? client.AgentScope);
        var rpc = await host.OpenTargetAsync(null, ct);
        var token = NewToken();
        request.McpServers = WithPeers(request.McpServers, token);
        request.SystemPrompt = SystemPromptFor(host);
        NewSessionResponse response;
        try
        {
            response = await rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, request, ct);
            BindToken(token, response.SessionId);
            host.MarkLive(response.SessionId, rpc);
        }
        finally
        {
            host.DoneOpening(rpc);
        }
        store.Update(response.SessionId, host.Name, r => { r.Cwd = FolderPath.Normalize(request.Cwd); r.Draft = true; r.PeerToken = token; });
        var feed = Feed(response.SessionId);
        feed.CaptureState(AcpJson.ToNode(response));
        // Attached now, for the agent's requests; its list of commands only once it has the answer.
        feed.AttachUnanswered(client);
        afterAnswer(() => feed.AnsweredAsync(client));
        if (options.Answering is { } hold) await hold(response.SessionId);
        response.Meta ??= [];
        response.Meta[GorynychProtocol.MetaAgent] = host.Name;
        ServiceLog.Info($"{host.Name}: new session {response.SessionId} in {request.Cwd}");
        return response;
    }

    /// <summary>Resolves a session ID the client names, refreshing from the agents if needed.</summary>
    internal async Task<SessionRecord> FindAsync(string sessionId, CancellationToken ct)
    {
        var known = store.Get(sessionId);
        if (known is { Deleted: false }) return known;
        if (known is null) await ListAsync(new ListSessionsRequest(), ct);
        return store.Get(sessionId) is { Deleted: false } found
            ? found
            : throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Unknown session {sessionId}");
    }

    /// <summary>
    /// Makes sure the agent has the session open in its current instance, its journal caught up
    /// with the agent's own history first (<see cref="OpenAsync"/>). Called under a use of the
    /// session, whose end starts its idle timer (<see cref="IdleFromNow"/>).
    /// </summary>
    /// <param name="holdingTurn">The caller holds the session's turn (<see cref="RpcForTurnAsync"/>): a session in a stale host moves now.</param>
    internal async Task<(JsonRpcConnection Rpc, JsonObject? Opened)> EnsureLiveAsync(SessionRecord record, CancellationToken ct, bool holdingTurn = false)
    {
        var host = HostOf(record);
        var feed = Feed(record.SessionId);
        var wasOpen = host.IsLive(record.SessionId);
        // In a stale host: moved first when it is idle, so the new host takes the turn. A turn about to
        // start waits for the agent's own turn and its questions there, and then moves.
        while (host.RouteOf(record.SessionId) is { Current: false } stale)
        {
            var quieter = feed.Quieter;
            var move = await TryMoveAsync(record.SessionId, holdingTurn, ct);
            if (move == Move.Done) break;
            if (move == Move.Stay || !holdingTurn) return (stale.Rpc, null);
            await Task.WhenAny(quieter, Task.Delay(MoveRetry, ct));
            ct.ThrowIfCancellationRequested();
        }
        if (host.RouteOf(record.SessionId) is { } on) return (on.Rpc, null);
        if (!wasOpen) await host.AwaitHostOfAsync(record, ct);
        var gate = feed.OpenGate;
        await gate.WaitAsync(ct);
        try
        {
            if (host.RouteOf(record.SessionId) is { } open) return (open.Rpc, null);
            var rpc = await host.OpenTargetAsync(record.SessionId, ct);
            // A host that came back with it open while this waited for a host: it is open there.
            if (host.RouteOf(record.SessionId) is { } came)
            {
                host.DoneOpening(rpc);
                return (came.Rpc, null);
            }
            try
            {
                var opened = await OpenAsync(host, rpc, record, ct);
                return (rpc, opened);
            }
            finally
            {
                host.DoneOpening(rpc);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Opens the session in its agent and, before anything runs in it, catches its journal up with the
    /// agent's own history, which may have grown outside Gorynych (<see cref="HistoryCatchUp"/>): so a
    /// turn the service starts next lands after it in the journal, as it did in the agent. The
    /// session counts as loaded (<see cref="AgentHost.IsLive"/>) only once that is done, so a prompt,
    /// a wake or another load waits for it on <see cref="SessionFeed.OpenGate"/>. An adapter that
    /// advertises <c>gorynych/replayFrom</c> replays what follows the journal's mark on
    /// <c>session/resume</c>; an agent that loads but has no marks (Kimi) replays everything on
    /// <c>session/load</c>, which the service aligns with the journal; any other just resumes. A
    /// journal without turns takes the replay as it comes (an import), so it is never held in memory.
    /// </summary>
    async Task<JsonObject?> OpenAsync(AgentHost host, JsonRpcConnection rpc, SessionRecord record, CancellationToken ct)
    {
        var feed = Feed(record.SessionId);
        JsonNode? opened;
        HistoryCatchUp.Replayed? replayed = null;
        string? newMark = null;
        (List<JsonObject> Updates, bool Dropped) history = ([], false);
        var collect = feed.Journal.HasTurns;
        if (host.CanResume && (host.ReplaysFrom || !host.CanLoad))
        {
            var mark = host.ReplaysFrom ? feed.Journal.NativeMark : null;
            var meta = new JsonObject();
            if (host.ReplaysFrom)
                meta[GorynychProtocol.MetaReplayFrom] = mark is null
                    ? new JsonObject { ["type"] = GorynychProtocol.ReplayFromStart }
                    : new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = mark };
            // No turn yet: a draft kept across a restart for a client on the resumable link, or one that
            // moved off a stale host, whose first prompt may already have ended it as one (Promote). An
            // agent that wrote nothing of it opens it afresh; one that knows it opens it as it is.
            if (record.Draft || !feed.Journal.HasTurns) meta[GorynychProtocol.MetaDraft] = true;
            var resume = new ResumeSessionRequest
            {
                SessionId = record.SessionId, Cwd = record.Cwd, McpServers = PeersFor(record.SessionId), SystemPrompt = SystemPromptFor(host),
                Meta = meta.Count > 0 ? meta : null,
            };
            if (host.ReplaysFrom && collect) feed.BeginReplay(feed.Journal.MaxEntries);
            try
            {
                opened = await rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, AcpJson.ToNode(resume), ct);
            }
            finally
            {
                if (host.ReplaysFrom && collect) history = feed.EndReplay();
            }
            if (host.ReplaysFrom)
            {
                if (opened?["_meta"]?[GorynychProtocol.MetaReplay] is JsonObject reply)
                {
                    replayed = mark is not null && (string?)reply["from"] == "mark" ? HistoryCatchUp.Replayed.AfterMark : HistoryCatchUp.Replayed.All;
                    newMark = reply["mark"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : null;
                }
                else
                {
                    // The adapter could not read the history: the journal stays as it is.
                    ServiceLog.Warn($"{host.Name} {record.SessionId}: opened without its own history; the journal is not caught up");
                }
            }
        }
        else if (host.CanLoad)
        {
            if (collect) feed.BeginReplay(feed.Journal.MaxEntries);
            try
            {
                opened = await rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
                    AcpJson.ToNode(new LoadSessionRequest
                    {
                        SessionId = record.SessionId, Cwd = record.Cwd, McpServers = PeersFor(record.SessionId), SystemPrompt = SystemPromptFor(host),
                    }), ct);
            }
            finally
            {
                if (collect) history = feed.EndReplay();
            }
            replayed = HistoryCatchUp.Replayed.All;
        }
        else
        {
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"{host.Name} cannot reopen session {record.SessionId}");
        }
        feed.CaptureState(opened);
        // An agent that can show the session but not take it now says so (gorynych/interactivity): read-only until it can.
        var readOnly = ReadOnlyOf((opened as JsonObject)?["_meta"]);
        var taken = feed.OpenedReadOnly && readOnly is null;
        feed.OpenedReadOnly = readOnly is not null;
        feed.ReportedReadOnly = readOnly;
        try
        {
            if (replayed is { } how && collect) await CatchUpAsync(host, feed, history.Updates, history.Dropped, how, newMark);
            else if (newMark is not null) SetMark(feed, newMark); // imported as it came
        }
        finally
        {
            host.MarkLive(record.SessionId, rpc);
        }
        // What the other app made meanwhile is in the journal and with the clients first; then, taken
        // after it was open for reading only, the options its agent could not say then (Codex reports
        // a thread's settings only once it loads it), and the flag.
        if (taken && (opened as JsonObject)?["configOptions"] is JsonArray { Count: > 0 } options)
            await feed.PublishAsync(new JsonObject { ["sessionUpdate"] = "config_option_update", ["configOptions"] = options.DeepClone() },
                except: null, () => { }, journal: false);
        await SetInteractivityAsync(feed, readOnly);
        return opened as JsonObject;
    }

    /// <summary>
    /// A session its agent opened for reading only can be written to again: what the other app made
    /// meanwhile is read first, without taking the session (<c>_gorynych/catch_up</c>, after the
    /// journal's mark), into the journal and to the clients, and only then is the flag cleared, so a
    /// client turns its input on with the history whole, and a prompt it sends comes after it. Under
    /// the session's open gate, which a turn's own open waits for. The flag ends as the agent said
    /// last, should the session have been taken again meanwhile.
    /// </summary>
    async Task WritableAgainAsync(AgentHost host, SessionFeed feed)
    {
        await feed.OpenGate.WaitAsync(cleanupStop.Token);
        try
        {
            if (feed.ReportedReadOnly is null && feed.OpenedReadOnly && host.RouteOf(feed.SessionId) is { } on)
                await CatchUpByReadingAsync(host, on.Rpc, feed, cleanupStop.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ServiceLog.Warn($"{host.Name} {feed.SessionId}: what was made meanwhile could not be read; it comes with the next open: {e.Message}");
        }
        finally
        {
            feed.OpenGate.Release();
        }
        await SetInteractivityAsync(feed, feed.ReportedReadOnly);
    }

    /// <summary>The session's history after the journal's mark, read by its agent without taking it, caught up into the journal as at an open.</summary>
    async Task CatchUpByReadingAsync(AgentHost host, JsonRpcConnection rpc, SessionFeed feed, CancellationToken ct)
    {
        var mark = feed.Journal.NativeMark;
        var collect = feed.Journal.HasTurns;
        var from = mark is null
            ? new JsonObject { ["type"] = GorynychProtocol.ReplayFromStart }
            : new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = mark };
        if (collect) feed.BeginReplay(feed.Journal.MaxEntries);
        (List<JsonObject> Updates, bool Dropped) history = ([], false);
        JsonNode? reply;
        try
        {
            reply = await rpc.SendRequestAsync(GorynychProtocol.MethodCatchUp,
                new JsonObject { ["sessionId"] = feed.SessionId, ["_meta"] = new JsonObject { [GorynychProtocol.MetaReplayFrom] = from } }, ct);
        }
        finally
        {
            if (collect) history = feed.EndReplay();
        }
        if (reply?["_meta"]?[GorynychProtocol.MetaReplay] is not JsonObject replay)
        {
            ServiceLog.Warn($"{host.Name} {feed.SessionId}: its agent could not read what was made meanwhile; it comes with the next open");
            return;
        }
        var how = mark is not null && TextOf(replay["from"]) == "mark" ? HistoryCatchUp.Replayed.AfterMark : HistoryCatchUp.Replayed.All;
        var newMark = TextOf(replay["mark"]);
        if (collect) await CatchUpAsync(host, feed, history.Updates, history.Dropped, how, newMark);
        else if (newMark is not null) SetMark(feed, newMark);
    }

    /// <summary>Applies what the agent replayed of its own history to the journal, then keeps its new mark. Never fails the open.</summary>
    async Task CatchUpAsync(AgentHost host, SessionFeed feed, List<JsonObject> history, bool dropped, HistoryCatchUp.Replayed replayed, string? mark)
    {
        try
        {
            if (options.CatchingUp is { } hold) await hold(feed.SessionId);
            var plan = HistoryCatchUp.Make(feed.Journal.ReadAll(), history, replayed, host.DisplayName, dropped);
            switch (plan.Kind)
            {
                case HistoryCatchUp.Kind.Append:
                    await feed.AppendHistoryAsync(plan.Updates, plan.Trimmed);
                    SetMark(feed, mark);
                    ServiceLog.Info($"{host.Name} {feed.SessionId}: {plan.Updates.Count} updates of its own history added to the journal ({plan.Why})");
                    if (plan.Trimmed)
                        ServiceLog.Warn($"{host.Name} {feed.SessionId}: its own history gained more than the journal keeps; only the last {plan.Updates.Count} updates were added, the journal is marked trimmed");
                    break;
                case HistoryCatchUp.Kind.Replace:
                    await feed.ReplaceHistoryAsync(plan.Updates, options.Time.GetUtcNow(), mark, plan.Trimmed);
                    ServiceLog.Warn($"{host.Name} {feed.SessionId}: its own history was rewritten ({plan.Why}); the journal was imported again ({plan.Updates.Count - 1} updates), the earlier one kept");
                    break;
                default:
                    SetMark(feed, mark);
                    if (replayed == HistoryCatchUp.Replayed.All) ServiceLog.Info($"{host.Name} {feed.SessionId}: nothing to add to the journal ({plan.Why})");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ServiceLog.Warn($"{host.Name} {feed.SessionId}: the journal could not be caught up: {e.Message}");
        }
    }

    internal async Task<JsonNode?> LoadAsync(ClientConnection client, LoadSessionRequest request, CancellationToken ct)
    {
        RefuseSystemPrompt(request.SystemPrompt);
        using var use = await UseAsync(request.SessionId, ct);
        var record = use.Record;
        var host = HostOf(record);
        var feed = use.Feed;
        JsonObject response;
        try
        {
            // Opening it catches the journal up with the agent's own history first (OpenAsync); a
            // session the journal has never seen is imported so.
            var (_, opened) = await EnsureLiveAsync(record, ct);
            response = opened ?? feed.State();
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            // A session the journal has never seen has its history only in an agent that loads: as before, the error is the answer.
            if (feed.Journal.Count == 0 && host.CanLoad) throw;
            // The history is still worth showing even if the agent cannot take the session now.
            ServiceLog.Warn($"{host.Name}: cannot reopen {record.SessionId}: {e.Message}");
            response = [];
        }
        var position = await feed.ReplayAndAttachAsync(client);
        await NoticeOnOpenAsync(feed, client);
        return WithLinkState(response, feed, position);
    }

    /// <summary>
    /// <c>session/resume</c>. With <c>_meta["gorynych/replayFrom"]</c> (a client on the resumable link
    /// coming back), the client first gets what the journal has after its position, or all of it
    /// from <c>{"type": "start"}</c>; a plain resume replays nothing.
    /// </summary>
    internal async Task<JsonNode?> ResumeAsync(ClientConnection client, ResumeSessionRequest request, CancellationToken ct)
    {
        RefuseSystemPrompt(request.SystemPrompt);
        var after = ReplayFrom(request.Meta?[GorynychProtocol.MetaReplayFrom]);
        // A resumable link coming back says what its client shows of the session's interactivity.
        if (request.Meta?[GorynychProtocol.MetaInteractivity] is not null) client.Knows(request.SessionId, ReadOnlyOf(request.Meta));
        using var use = await UseAsync(request.SessionId, ct);
        var (_, opened) = await EnsureLiveAsync(use.Record, ct);
        var position = await use.Feed.ResumeAndAttachAsync(client, after);
        await NoticeOnOpenAsync(use.Feed, client);
        // An already open session gets the state the agent last reported, so every client sees its switches.
        return WithLinkState(opened ?? use.Feed.State(), use.Feed, position);
    }

    /// <summary>The position a client's <c>replayFrom</c> asks to resume after; null without one.</summary>
    static long? ReplayFrom(JsonNode? from)
    {
        if (from is null) return null;
        static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return Text(from["type"]) switch
        {
            GorynychProtocol.ReplayFromStart => 0,
            GorynychProtocol.ReplayAfterMark when SessionJournal.TryParsePosition(Text(from["mark"]), out var position) => position,
            GorynychProtocol.ReplayAfterMark => throw new JsonRpcException(JsonRpcException.InvalidParams,
                $"replayFrom: '{Text(from["mark"])}' is not a position this service gave"),
            var type => throw new JsonRpcException(JsonRpcException.InvalidParams, $"replayFrom: unknown type '{type}'"),
        };
    }

    /// <summary>
    /// A load's or resume's answer with what a client on the resumable link needs: the position it
    /// is at, whether a turn runs, and which of its prompts are still in progress.
    /// </summary>
    static JsonNode WithLinkState(JsonNode? response, SessionFeed feed, long position)
    {
        var result = response as JsonObject ?? [];
        var meta = result["_meta"] as JsonObject ?? [];
        meta[GorynychProtocol.MetaPosition] = SessionJournal.FormatPosition(position);
        meta[GorynychProtocol.MetaTurn] = feed.TurnRunning ? "running" : "idle";
        meta[GorynychProtocol.MetaPrompts] = new JsonArray(feed.PromptsInProgress().Select(p => (JsonNode)p).ToArray());
        // Whether the client may prompt it, said in full: a client that showed it read-only before learns it is not.
        PutInteractivity(meta, feed.ReadOnly);
        result["_meta"] = meta;
        return result;
    }

    /// <summary>
    /// <c>session/prompt</c>. A prompt with <c>_meta["gorynych/promptId"]</c> (a client on the
    /// resumable link) leaves a mark of its turn's end in the journal, so its client finds out how it
    /// ended should its answer be lost (<see cref="MarkPromptEndAsync"/>).
    /// </summary>
    internal async Task<JsonNode?> PromptAsync(ClientConnection client, JsonRpcRequest request, CancellationToken ct)
    {
        if (Draining) throw Restarting();
        var prompt = request.ParamsAs<PromptRequest>();
        var promptId = PromptIdOf(prompt);
        using var use = await UseAsync(prompt.SessionId, ct);
        var key = use.Feed.BeginPrompt(promptId);
        // A restart that began while this prompt found its session saw nothing in progress: refused
        // all the same, so it goes to the next service rather than die with this one.
        Interlocked.MemoryBarrier();
        if (Draining)
        {
            use.Feed.EndPrompt(key);
            throw Restarting();
        }
        JsonNode? result = null;
        Exception? failure = null;
        var livesOn = false;
        try
        {
            return result = await PromptInProgressAsync(client, request, prompt, use, () => use.Feed.Handed(key), ct);
        }
        catch (IOException e) when (options.Hosts is not null && !StopHostsOnExit)
        {
            // This service exits for a restart and leaves the hosts running.
            if (!use.Feed.WasHanded(key))
            {
                failure = Restarting(); // it never reached the agent: it goes whole to the next service
                throw failure;
            }
            // The turn goes on in its host. Its client gets no answer from here: it takes this
            // service's exit for a lost connection, and the next service has the turn and marks its end.
            livesOn = true;
            failure = e;
            await Task.Delay(Timeout.Infinite, ct); // until the client's connection goes with this service
            throw;
        }
        catch (IOException e) when (options.Hosts is not null)
        {
            // Its host went with the turn (it crashed, or was killed): the turn ended cancelled, as its mark says.
            ServiceLog.Warn($"{use.Feed.SessionId}: the host went in the middle of the turn: {e.Message}");
            return result = Cancelled();
        }
        catch (Exception e)
        {
            failure = e;
            throw;
        }
        finally
        {
            // The mark first: a client resuming meanwhile finds the prompt in progress or its end in the
            // journal. Marked whether or not its client still seems there: a connection may be gone
            // before the service notices, and then the answer is lost. A prompt a restart refused
            // ended nowhere: it goes again, as it is. A turn that goes on in a host this service leaves
            // running for a restart is not over: the next service marks its end when the host reports
            // it. One its host reported the end of is marked already (OnTurnEndedAsync), and is marked no more.
            if (promptId is not null && !IsRestartingRefusal(failure) && !livesOn)
                await MarkPromptEndAsync(use.Feed, promptId, result, failure);
            use.Feed.EndPrompt(key);
        }
    }

    static bool IsRestartingRefusal(Exception? failure) =>
        failure is JsonRpcException j && j.ErrorData?[GorynychProtocol.MetaRestarting] is JsonValue v && v.TryGetValue<bool>(out var restarting) && restarting;

    static string? PromptIdOf(PromptRequest prompt) =>
        prompt.Meta?[GorynychProtocol.MetaPromptId] is JsonValue v && v.TryGetValue<string>(out var id) && id.Length > 0 ? id : null;

    /// <summary>
    /// Journals and sends the end of a prompt's turn: a <c>session_info_update</c>
    /// with <c>gorynych/agentTurn: "ended"</c>, the prompt's ID, and its stop reason or error. Never fails.
    /// A prompt whose end is marked already is marked no more: its answer after its host's report, or
    /// a report after a service ended it for a host it took for gone.
    /// </summary>
    /// <param name="hostSeq">The host's report it comes from, journaled with it so that it is marked once.</param>
    async Task MarkPromptEndAsync(SessionFeed feed, string promptId, JsonNode? result, Exception? failure, string? hostSeq = null)
    {
        if (!feed.ClaimEndMark(promptId))
        {
            ServiceLog.Info($"{feed.SessionId}: the end of prompt {promptId} is marked already");
            return;
        }
        var meta = new JsonObject { [GorynychProtocol.MetaAgentTurn] = "ended", [GorynychProtocol.MetaPromptId] = promptId };
        if (hostSeq is not null) meta[GorynychProtocol.MetaHostSeq] = hostSeq;
        if (failure is null)
            meta[GorynychProtocol.MetaStopReason] = result?["stopReason"] is JsonValue r && r.TryGetValue<string>(out var reason) ? reason : "end_turn";
        // Its client went while the session was being opened, so it never ran; or the agent went
        // under it (the service stops, the agent crashed): the turn was cut off, not refused.
        else if (failure is OperationCanceledException or IOException)
            meta[GorynychProtocol.MetaStopReason] = "cancelled";
        else
            meta[GorynychProtocol.MetaError] = new JsonObject
            {
                ["code"] = failure is JsonRpcException j ? j.Code : JsonRpcException.InternalError,
                ["message"] = failure.Message,
            };
        try
        {
            await feed.PublishAsync(AcpJson.ToNode<SessionUpdate>(new SessionInfoUpdate { Meta = meta })!.AsObject(), except: null, () => { });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"{feed.SessionId}: the end of prompt {promptId} could not be journaled: {e.Message}");
        }
    }

    async Task<JsonNode?> PromptInProgressAsync(ClientConnection client, JsonRpcRequest request, PromptRequest prompt, SessionUse use, Action handed, CancellationToken ct)
    {
        var record = Promote(use.Record);
        var (rpc, _) = await EnsureLiveAsync(record, ct);
        var feed = use.Feed;
        feed.Attach(client);
        if (client.CancelledAfter(record.SessionId, request.Sequence)) return Cancelled();
        // One turn at a time: an opinion request delivered as a turn waits for this one, and
        // this one for it.
        // With hosts, a restart does not wait for the turn before this prompt, which lives on in its
        // host: the wait ends, and the prompt goes to the next service, as one that got here first.
        try
        {
            await feed.TurnGate.WaitAsync(options.Hosts is null ? CancellationToken.None : drainStop.Token);
        }
        catch (OperationCanceledException) when (drainStop.IsCancellationRequested)
        {
            throw Restarting();
        }
        // A restart that began while this prompt waited for the turn before it: it starts no turn
        // here, which the exit might cut off, but goes whole to the next service.
        if (Draining)
        {
            feed.TurnGate.Release();
            throw Restarting();
        }
        feed.ClientTurn = true; // what a client's session/close cancels
        try
        {
            if (feed.Retired) throw NotFound(record.SessionId); // deleted while this prompt waited for the turn before it
            rpc = await RpcForTurnAsync(record, rpc, ct);
            if (record.ParentSessionId is null) return await PromptTurnAsync(client, request, prompt, record, rpc, feed, handed);
            // A spawned session reports the end of the turn to its parent.
            feed.BeginTurnText(ReportLimit + 1);
            JsonNode? result;
            try
            {
                result = await PromptTurnAsync(client, request, prompt, record, rpc, feed, handed);
            }
            catch (Exception e)
            {
                // One that goes on in a host this service leaves running for a restart: the next one reports its end.
                var hostWent = e is IOException && options.Hosts is not null;
                if (!hostWent || StopHostsOnExit) ReportTurnEnd(record.SessionId, hostWent ? "failed: its host went" : $"failed: {e.Message}", feed.EndTurnText());
                throw;
            }
            ReportTurnEnd(record.SessionId, Outcome(result), feed.EndTurnText());
            return result;
        }
        finally
        {
            feed.ClientTurn = false;
            feed.TurnEnded();
            feed.TurnGate.Release();
        }
    }

    async Task<JsonNode?> PromptTurnAsync(ClientConnection client, JsonRpcRequest request, PromptRequest prompt, SessionRecord record,
        JsonRpcConnection rpc, SessionFeed feed, Action handed)
    {
        if (client.CancelledAfter(record.SessionId, request.Sequence)) return Cancelled();
        // Journal the user's message, show it to the session's other clients, and use the
        // first prompt as the title until the harness provides one.
        var messageId = SessionFeed.NewMessageId(); // one message, however many blocks
        // The prompt's ID, if its client gave one, goes with its text: that client never gets the
        // text live, and drops it when a resume after a reconnect sends it from the journal.
        var promptId = PromptIdOf(prompt);
        foreach (var block in prompt.Prompt)
        {
            var update = AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk
            {
                Content = block, MessageId = messageId,
                Meta = promptId is null ? null : new JsonObject { [GorynychProtocol.MetaPromptId] = promptId },
            })!.AsObject();
            await feed.PublishAsync(update, except: client, () => store.Touch(record.SessionId));
        }
        await ProvisionalTitleAsync(feed, record, prompt.Prompt);
        // A turn outlives the client that started it: closing the GUI must not stop the agent.
        // Stopping a turn is an explicit session/cancel, which must reach the agent after the
        // prompt, so both go through the session's forward gate.
        Task<JsonNode?> response;
        await feed.ForwardGate.WaitAsync(CancellationToken.None);
        try
        {
            if (client.CancelledAfter(record.SessionId, request.Sequence)) return Cancelled();
            response = await rpc.StartRequestAsync(request.Method, WithMessageId(request.Params, messageId), CancellationToken.None);
            handed();
        }
        finally
        {
            feed.ForwardGate.Release();
        }
        var result = await response;
        KeepMark(feed, result);
        return result;
    }

    /// <summary>
    /// Keeps the mark a turn's result carries (<c>gorynych/historyMark</c>): the journal now holds the
    /// agent's own history up to there. Its updates came before the result, so they are journaled.
    /// </summary>
    static void KeepMark(SessionFeed feed, JsonNode? result) => SetMark(feed, result?["_meta"]?[GorynychProtocol.MetaHistoryMark]);

    static void SetMark(SessionFeed feed, JsonNode? mark)
    {
        if (mark is JsonValue v && v.TryGetValue<string>(out var text)) SetMark(feed, text);
    }

    /// <summary>Keeps a mark; a failure to write it is logged, never the end of a turn that is done.</summary>
    static void SetMark(SessionFeed feed, string? mark)
    {
        if (mark is not { Length: > 0 } || feed.Retired) return;
        try
        {
            feed.Journal.NativeMark = mark;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"{feed.SessionId}: the mark of the agent's history could not be kept: {e.Message}");
        }
    }

    /// <summary>
    /// A prompt's params with <c>_meta["gorynych/messageId"]</c>: the ID the service journals its
    /// text under, for an agent that keeps it with the message (Codex's <c>clientUserMessageId</c>).
    /// </summary>
    static JsonNode WithMessageId(JsonNode? @params, string messageId)
    {
        var copy = @params?.DeepClone() as JsonObject ?? [];
        var meta = copy["_meta"] as JsonObject ?? [];
        meta[GorynychProtocol.MetaMessageId] = messageId;
        copy["_meta"] = meta;
        return copy;
    }

    /// <summary>Uses the first prompt as the session's title until the harness provides one.</summary>
    async Task ProvisionalTitleAsync(SessionFeed feed, SessionRecord record, IEnumerable<ContentBlock> prompt)
    {
        if (record.Title is not null || FirstText(prompt) is not { } text) return;
        var title = Shorten(text);
        store.Update(record.SessionId, record.Agent, r => { r.Title ??= title; r.TitleSource ??= "prompt"; });
        var info = AcpJson.ToNode<SessionUpdate>(new SessionInfoUpdate { Title = title })!.AsObject();
        if (!feed.IsRepeatedTitle(info)) await feed.PublishAsync(info, except: null, () => { });
    }

    static JsonNode? Cancelled() => AcpJson.ToNode(new PromptResponse { StopReason = StopReason.Cancelled });

    internal async Task<JsonNode?> ForwardAsync(JsonRpcRequest request, string sessionId, CancellationToken ct)
    {
        using var use = await UseAsync(sessionId, ct);
        var (rpc, _) = await EnsureLiveAsync(use.Record, ct);
        // A change of a session its agent opened for reading only: opened again first, which takes it
        // if it is free now, and refused in the same words as a prompt while it is still read-only.
        if (use.Feed.OpenedReadOnly && request.Method is AcpMethods.Agent.SessionSetConfigOption or AcpMethods.Agent.SessionSetMode or "session/set_model")
            rpc = await RetakeAsync(HostOf(use.Record), use.Record, rpc, ReadOnlyUse.Setting, ct);
        var response = await rpc.SendRequestAsync(request.Method, request.Params?.DeepClone(), ct);
        switch (request.Method)
        {
            case AcpMethods.Agent.SessionSetConfigOption:
                use.Feed.CaptureState(response); // the full list with the new values
                break;
            case AcpMethods.Agent.SessionSetMode:
                use.Feed.SetCurrent("modes", "currentModeId", request.Params?["modeId"]);
                break;
            case "session/set_model": // unstable in ACP; agents that have it answer the same way
                use.Feed.SetCurrent("models", "currentModelId", request.Params?["modelId"]);
                break;
        }
        return response;
    }

    internal async Task ForwardNotificationAsync(string method, JsonNode? @params, string sessionId)
    {
        if (store.Get(sessionId) is not { } record) return;
        var host = HostOf(record);
        if ((host.RouteOf(sessionId)?.Rpc ?? host.Running) is not { } rpc) return;
        var feed = Feed(sessionId);
        await feed.ForwardGate.WaitAsync();
        try { await rpc.SendNotificationAsync(method, @params?.DeepClone()); }
        finally { feed.ForwardGate.Release(); }
    }

    /// <summary>
    /// <c>session/close</c>: the client lets go of the session. Another client that has it open
    /// keeps it as it is. For the last one, a draft is discarded; otherwise the session is closed
    /// in its agent after its idle timer (<see cref="StartIdle"/>), like one a client left without
    /// closing it (<see cref="LetGo"/>), and, as ACP asks of a close, a running turn a client's
    /// prompt started (any client's: this is the last one) is cancelled with <c>session/cancel</c>.
    /// A service turn (an opinion request, a report), a spawned session's first turn and a turn
    /// the agent runs on its own go on: a client's close stops no work it did not start.
    /// </summary>
    internal async Task<JsonNode?> CloseAsync(ClientConnection client, string sessionId, CancellationToken ct)
    {
        client.Release(sessionId);
        if (feeds.TryGetValue(sessionId, out var feed))
        {
            feed.Detach(client, notify: false);
            if (feed.Clients().Length > 0) return new JsonObject();
        }
        if (store.Get(sessionId) is not { Deleted: false } record) return new JsonObject();
        if (record.Draft)
        {
            await DiscardDraftAsync(sessionId, ct);
            return new JsonObject();
        }
        // The timer first: a failed cancel must not leave the session loaded. A running turn keeps it from
        // being idle, and the turn's end starts the timer.
        var unload = StartIdle(sessionId);
        if (feed is { ClientTurn: true })
        {
            ServiceLog.Info($"{record.Agent} {sessionId}: closed by its last client during a client's turn; cancelling the turn");
            try
            {
                await ForwardNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId }, sessionId);
            }
            catch (Exception e) when (e is IOException or JsonRpcException or ObjectDisposedException)
            {
                ServiceLog.Warn($"{record.Agent} {sessionId}: the cancel did not reach the agent: {e.Message}");
            }
        }
        if (unload is not null) await unload();
        return new JsonObject();
    }

    // ---------------------------------------------------------------- drafts and deletion

    /// <summary>
    /// A registered operation on a session (see <see cref="UseAsync"/>); disposing it ends the
    /// registration, then lets the conductor close the session if nothing holds it any more.
    /// </summary>
    internal sealed class SessionUse(SessionFeed feed, SessionRecord record, Action ended) : IDisposable
    {
        int done;
        public SessionFeed Feed { get; } = feed;
        public SessionRecord Record { get; } = record;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) != 0) return;
            Feed.ExitUse();
            ended();
        }
    }

    /// <summary>
    /// Resolves a session for an operation that uses it (load, resume, prompt, opinion, a
    /// forwarded request) and registers the operation, so a draft is never discarded and a session
    /// never unloaded under it. While a discard, deletion or unload of the session is under way
    /// this waits for its outcome; a session that is gone by then is not found.
    /// </summary>
    async Task<SessionUse> UseAsync(string sessionId, CancellationToken ct)
    {
        await FindAsync(sessionId, ct); // an unknown ID fails here, before a feed exists for it
        var feed = Feed(sessionId);
        await feed.LifeGate.WaitAsync(ct);
        try
        {
            var record = await FindAsync(sessionId, ct);
            if (feed.Retired) throw NotFound(sessionId);
            feed.EnterUse();
            return new SessionUse(feed, record, () => IdleFromNow(sessionId));
        }
        finally
        {
            feed.LifeGate.Release();
        }
    }

    static JsonRpcException NotFound(string sessionId) => new(JsonRpcException.ResourceNotFound, $"Unknown session {sessionId}");

    /// <summary>
    /// Ends a draft before anything reaches its agent. The mark must be on disk, since a draft
    /// left in the file is swept at the next start; if it cannot be saved the turn is refused.
    /// </summary>
    SessionRecord Promote(SessionRecord record)
    {
        if (!record.Draft) return record;
        try
        {
            return store.Update(record.SessionId, record.Agent, r => r.Draft = false, durable: true) ?? throw NotFound(record.SessionId);
        }
        catch (IOException e)
        {
            // The change stays in memory, so this run keeps the session whatever happens next.
            throw new JsonRpcException(JsonRpcException.InternalError, $"Gorynych could not save its session list, so nothing was sent: {e.Message}");
        }
    }

    /// <summary>
    /// <c>session/delete</c>. Closes the session in its agent if it is open there (which stops
    /// its turn), then deletes it natively when the agent can delete (Kimi); otherwise (Claude,
    /// whose files the service never touches) the overlay keeps a tombstone that hides it from
    /// every list. A session only the agent knows is found through its list. An unknown or
    /// already deleted session succeeds.
    /// </summary>
    internal async Task<JsonNode?> DeleteAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await FindAsync(sessionId, ct);
        }
        catch (JsonRpcException e) when (e.Code == JsonRpcException.ResourceNotFound)
        {
            return new JsonObject();
        }
        var feed = Feed(sessionId);
        await feed.LifeGate.WaitAsync(ct);
        try
        {
            if (store.Get(sessionId) is not { Deleted: false } record) return new JsonObject();
            var native = false;
            if (agents.TryGetValue(record.Agent, out var host))
            {
                try
                {
                    var rpc = await host.SessionRpcAsync(sessionId, ct);
                    await CloseInAgentAsync(host, rpc, sessionId, ct);
                    rpc = await host.SessionRpcAsync(sessionId, ct); // closed: the adapter-wide one deletes it
                    if (host.CanDelete)
                    {
                        await NativeDeleteAsync(host, rpc, record, ct);
                        native = true;
                    }
                }
                catch (Exception e) when (e is not (JsonRpcException or OperationCanceledException))
                {
                    // When in doubt, keep the session: hidden, not deleted.
                    ServiceLog.Warn($"{host.Name}: cannot reach the agent to delete {sessionId}, hiding it instead: {e.Message}");
                }
            }
            Forget(feed, record, tombstone: !native && !record.Draft, durable: true);
            ServiceLog.Info($"{record.Agent}: deleted session {sessionId}{(native ? "" : " (hidden; the harness keeps it)")}");
            return new JsonObject();
        }
        finally
        {
            feed.LifeGate.Release();
        }
    }

    enum DraftFate { Deleted, Dropped, Hidden, Kept }

    /// <summary>
    /// Deletes a draft once nothing uses it: no client attached, no operation registered, no
    /// turn in its journal. Holds the session's <see cref="SessionFeed.LifeGate"/> throughout.
    /// A native delete cannot be undone, so the agent must also show the session is empty: it
    /// must list it without a title (Kimi titles a session from its first prompt). If it cannot,
    /// the session is hidden instead. Returns true when the draft is gone from the lists; a
    /// failure leaves it marked for the sweep at the next start.
    /// </summary>
    async Task<bool> DiscardDraftAsync(string sessionId, CancellationToken ct)
    {
        if (shutdown.IsCancellationRequested || Volatile.Read(ref disposed) != 0) return false;
        if (store.Get(sessionId) is not { Draft: true }) return false;
        var feed = Feed(sessionId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, cleanupStop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await feed.LifeGate.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            // Read again under the gate: a client may have attached, prompted or loaded it meanwhile.
            if (store.Get(sessionId) is not { Draft: true, Deleted: false } record) return false;
            if (feed.Retired || feed.Clients().Length > 0 || feed.Users > 0) return false;
            var fate = feed.Journal.HasTurns ? DraftFate.Kept
                : agents.TryGetValue(record.Agent, out var host) ? await DraftFateInAgentAsync(host, record, timeout.Token)
                : DraftFate.Hidden;
            switch (fate)
            {
                case DraftFate.Kept:
                    store.Update(sessionId, record.Agent, r => r.Draft = false);
                    ServiceLog.Warn($"{record.Agent}: {sessionId} was marked a draft but has history; kept");
                    return false;
                case DraftFate.Hidden:
                    Forget(feed, record, tombstone: true);
                    ServiceLog.Info($"{record.Agent}: draft {sessionId} hidden, since its agent could not show it was empty");
                    return true;
                default:
                    Forget(feed, record, tombstone: false);
                    ServiceLog.Info($"{record.Agent}: discarded draft session {sessionId}");
                    return true;
            }
        }
        catch (Exception e)
        {
            ServiceLog.Warn($"draft {sessionId} not discarded, the next start tries again: {e.Message}");
            return false;
        }
        finally
        {
            feed.LifeGate.Release();
        }
    }

    /// <summary>Decides what happens to an unused draft in its agent, and does it there.</summary>
    static async Task<DraftFate> DraftFateInAgentAsync(AgentHost host, SessionRecord record, CancellationToken ct)
    {
        var id = record.SessionId;
        var rpc = await host.SessionRpcAsync(id, ct);
        if (!host.CanDelete || host.DraftsNotDurable)
        {
            // Nothing native to delete: the Claude adapter records a session at its first prompt, and
            // Codex writes a thread at its first turn.
            await CloseInAgentAsync(host, rpc, id, ct);
            return DraftFate.Dropped;
        }
        var native = host.CanList ? await FindNativeAsync(rpc, record, ct) : null;
        if (native is not null && !string.IsNullOrEmpty(native.Title)) return DraftFate.Kept; // the agent titled it: it had a turn
        await CloseInAgentAsync(host, rpc, id, ct);
        if (native is null) return DraftFate.Hidden;
        await NativeDeleteAsync(host, rpc, record, ct);
        return DraftFate.Deleted;
    }

    /// <summary>
    /// The agent's own list entry for the session, or null when it does not list it. Looks in
    /// the session's folder first, then in the whole list (the agent may spell the folder
    /// differently).
    /// </summary>
    static async Task<SessionInfo?> FindNativeAsync(JsonRpcConnection rpc, SessionRecord record, CancellationToken ct)
    {
        foreach (var cwd in record.Cwd.Length > 0 ? [record.Cwd, null] : new string?[] { null })
        {
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var r = await rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList,
                    new ListSessionsRequest { Cwd = cwd, Cursor = cursor }, ct);
                if (r.Sessions.FirstOrDefault(s => s.SessionId == record.SessionId) is { } found) return found;
                cursor = r.NextCursor;
                if (cursor is null) break;
            }
        }
        return null;
    }

    /// <summary>
    /// Closes the session in its agent if it is open there, which stops its turn; an agent without
    /// close gets a cancel. True when it is open nowhere now: the agent answered the close, or said it
    /// has no such session. A close that fails otherwise (an error, or no answer before
    /// <paramref name="deadline"/>) may have left it open there, or Codex's writer lock held: the
    /// session is left to that host, where it opens again, and elsewhere only once that host has gone
    /// (<see cref="AgentHost.Pin"/>).
    /// </summary>
    static async Task<bool> CloseInAgentAsync(AgentHost host, JsonRpcConnection rpc, string sessionId, CancellationToken ct, CancellationToken deadline = default)
    {
        if (host.RouteOf(sessionId) is not { } on) return true;
        rpc = on.Rpc;
        try
        {
            if (on.CanClose)
            {
                using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline);
                await rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId }, both.Token);
            }
            else await rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId });
        }
        catch (JsonRpcException e) when (e.Code != JsonRpcException.ResourceNotFound)
        {
            ServiceLog.Warn($"{host.Name} {sessionId}: its close failed in host {on.HostId?.ToString() ?? "(in this process)"}: {e.Message}");
            host.Pin(sessionId, on);
            return false;
        }
        catch (JsonRpcException) { } // it has no such session: it is open nowhere
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            ServiceLog.Warn($"{host.Name} {sessionId}: its close got no answer in host {on.HostId?.ToString() ?? "(in this process)"} within {MoveCloseTimeout.TotalSeconds:0} s");
            host.Pin(sessionId, on);
            return false;
        }
        host.MarkClosed(sessionId);
        return true;
    }

    /// <summary>
    /// <c>session/delete</c> in the agent. A session the agent no longer has counts as deleted:
    /// ACP's not-found error, or any error while the agent no longer lists it (Kimi 2.4.1
    /// answers <c>-32602 Unknown sessionId</c>).
    /// </summary>
    static async Task NativeDeleteAsync(AgentHost host, JsonRpcConnection rpc, SessionRecord record, CancellationToken ct)
    {
        try
        {
            await rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, AcpJson.ToNode(new DeleteSessionRequest { SessionId = record.SessionId }), ct);
        }
        catch (JsonRpcException e)
        {
            if (e.Code == JsonRpcException.ResourceNotFound) return;
            if (host.CanList && await FindNativeAsync(rpc, record, ct) is null) return;
            throw;
        }
    }

    /// <summary>
    /// Drops the service's state of a deleted session: wakes and detaches what waits on it,
    /// deletes its journal and peer token, and removes its record or turns it into a tombstone.
    /// Nothing that arrives for it later brings any of that back.
    /// </summary>
    void Forget(SessionFeed feed, SessionRecord record, bool tombstone, bool durable = false)
    {
        var id = record.SessionId;
        removed[id] = 0;
        feed.Retire();
        feeds.TryRemove(new KeyValuePair<string, SessionFeed>(id, feed));
        if (sessionTokens.TryRemove(id, out var token)) peerTokens.TryRemove(token, out _);
        try
        {
            feed.Journal.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"journal of {id} not deleted: {e.Message}");
        }
        if (!tombstone)
        {
            store.Remove(id);
            return;
        }
        try
        {
            store.Update(id, record.Agent, r => { r.Deleted = true; r.Draft = false; }, durable);
        }
        catch (IOException e)
        {
            throw new JsonRpcException(JsonRpcException.InternalError, $"The session is hidden for now, but Gorynych could not save that: {e.Message}");
        }
    }

    /// <summary>
    /// A client went away without <c>session/close</c> (a GUI that quits sends none). It stops
    /// nothing: a draft is discarded, as before, and any other session its last client left starts
    /// its idle timer (<see cref="IdleFromNow"/>). Runs in the background, so a disconnecting
    /// client is not held up by its agent. A client on the resumable link may come back for its
    /// draft (Zed opens one for every new thread): it waits <see cref="ConductorOptions.ComeBackGrace"/>,
    /// and a client that has it again by then keeps it.
    /// </summary>
    void LetGo(string sessionId, bool comesBack = false) => RunCleanup(async () =>
    {
        if (store.Get(sessionId) is not { Deleted: false } record) return;
        if (record.Draft)
        {
            if (comesBack)
                try { await Task.Delay(options.ComeBackGrace, options.Time, cleanupStop.Token); }
                catch (OperationCanceledException) { return; }
            await DiscardDraftAsync(sessionId, CancellationToken.None);
            return;
        }
        if (StartIdle(sessionId) is { } unload) await unload();
    });

    /// <summary>Runs cleanup work in the background, for <see cref="CleanupsIdleAsync"/>; a failure is logged.</summary>
    void RunCleanup(Func<Task> work)
    {
        var task = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception e) { ServiceLog.Warn($"cleanup: {e.Message}"); }
        });
        cleanups[task] = 0;
        _ = task.ContinueWith(t => cleanups.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>Completes when the drafts being discarded in the background are done.</summary>
    public Task CleanupsIdleAsync() => Task.WhenAll(cleanups.Keys);

    /// <summary>
    /// Deletes the drafts an earlier run left behind (a client quit without closing them). Called once
    /// at start, after <paramref name="wait"/>: a client on the resumable link that comes back after a
    /// restart resumes its drafts by then, and keeps them.
    /// </summary>
    public async Task SweepDraftsAsync(TimeSpan wait = default)
    {
        if (wait > TimeSpan.Zero)
            try { await Task.Delay(wait, options.Time, cleanupStop.Token); }
            catch (OperationCanceledException) { return; }
        foreach (var sessionId in startupDrafts)
        {
            try
            {
                await DiscardDraftAsync(sessionId, CancellationToken.None);
            }
            catch (Exception e)
            {
                ServiceLog.Warn($"draft {sessionId}: {e.Message}");
            }
        }
    }

    /// <summary>Not a draft, not deleted, and either titled (every prompted session gets a title) or with a turn in its journal.</summary>
    bool HasHistory(SessionRecord r) =>
        !r.Draft && !r.Deleted && (!string.IsNullOrEmpty(r.Title) || JournalOf(r.SessionId).HasTurns);

    SessionJournal JournalOf(string sessionId) =>
        feeds.TryGetValue(sessionId, out var feed) ? feed.Journal : new SessionJournal(JournalPath(sessionId), options.JournalMaxEntries);

    // ---------------------------------------------------------------- child sessions

    /// <summary><c>spawn_session</c>'s <c>initiation</c>: the user asked for this session in this turn.</summary>
    const string UserAsked = "user_asked";

    /// <summary><c>spawn_session</c>'s <c>initiation</c> by default: the agent proposes the session.</summary>
    const string Proposal = "proposal";

    /// <summary>
    /// <c>_gorynych/spawn</c>, from the peers MCP server's <c>spawn_session</c>. Asks the person on
    /// the calling session, unless the user asked for the session themselves (below); then creates a
    /// session in the chosen agent with the caller as its parent and starts its first prompt in the
    /// background. Returns the new session's ID at once. A spawned session is never a draft; it
    /// starts in its agent's default mode, whatever the caller's.
    /// </summary>
    internal async Task<JsonNode?> SpawnAsync(JsonRpcRequest request, CancellationToken ct)
    {
        if (Draining) throw Restarting();
        var token = (string?)request.Params?["_meta"]?[GorynychProtocol.MetaPeerToken];
        if (token is null || !peerTokens.TryGetValue(token, out var parentId))
            throw new JsonRpcException(JsonRpcException.InvalidRequest, "Only an agent's session can spawn one: unknown peer token");
        var prompt = (string?)request.Params?["prompt"];
        if (string.IsNullOrWhiteSpace(prompt)) throw new JsonRpcException(JsonRpcException.InvalidParams, "prompt is required");
        var agentName = (string?)request.Params?["agent"];
        if (agentName is null || !agents.TryGetValue(agentName, out var host))
            throw new JsonRpcException(JsonRpcException.InvalidParams,
                (agentName is null ? "agent is required." : $"No agent \"{agentName}\".") + $" Pass one of: {string.Join(", ", agents.Keys)}.");
        var title = ((string?)request.Params?["title"])?.Trim() is { Length: > 0 } named ? Shorten(named) : null;
        var report = request.Params?["report"] is not JsonValue flag || !flag.TryGetValue<bool>(out var r) || r;
        var initiation = (string?)request.Params?["initiation"] ?? Proposal;
        if (initiation is not (UserAsked or Proposal))
            throw new JsonRpcException(JsonRpcException.InvalidParams,
                $"No initiation \"{initiation}\". Pass \"{UserAsked}\" only when the user asked in this turn to start this session; otherwise \"{Proposal}\".");

        using var parentUse = await UseAsync(parentId, ct);
        var parent = parentUse.Record;
        var cwd = ResolveFolder(parent.Cwd, (string?)request.Params?["cwd"]);
        // The user's own request, in their own turn, needs no second yes; anything else is asked.
        var askedAnyway = initiation == UserAsked ? WhyAskedAnyway(parentUse.Feed) : null;
        var asks = initiation == Proposal || askedAnyway is not null;
        string? callId = null;
        if (asks)
        {
            callId = "gorynych-spawn-" + Interlocked.Increment(ref spawnCount);
            var what = $"Start a {host.DisplayName} session" + (title is null ? "" : $": {title}");
            var why = askedAnyway is null ? null : $"The agent says you asked for this; Gorynych asks you all the same, as {askedAnyway}.";
            RequestPermissionOutcome outcome;
            try
            {
                outcome = await AskToSpawnAsync(parentUse.Feed, parent, callId, what, host, cwd, prompt, title, why, ct);
            }
            catch (Exception e) when (e is OperationCanceledException or JsonRpcException)
            {
                // Withdrawn (the agent's tool call was cancelled, its peers server went away) or the caller was deleted.
                await EndSpawnCardAsync(parentUse.Feed, parent, callId, ToolCallStatus.Failed,
                    e is OperationCanceledException ? "Not started: the request was withdrawn." : $"Not started: {e.Message}");
                throw;
            }
            if (outcome is RequestPermissionOutcomeCancelled)
            {
                // The client cancelled the caller's turn (session/cancel), which answers its pending approvals so.
                await EndSpawnCardAsync(parentUse.Feed, parent, callId, ToolCallStatus.Failed, "Not started: the turn was cancelled.");
                throw new JsonRpcException(JsonRpcException.RequestCancelled, "The turn was cancelled, so the session was not started.");
            }
            if (outcome is not SelectedPermissionOutcome { OptionId: "start" })
            {
                await EndSpawnCardAsync(parentUse.Feed, parent, callId, ToolCallStatus.Failed, "Not started: the user declined.");
                throw new JsonRpcException(JsonRpcException.InvalidRequest, "The user declined to start the session." + AskedAnywayNote(askedAnyway));
            }
        }
        // The caller may have been deleted while the person decided.
        if (parentUse.Feed.Retired || store.Get(parent.SessionId) is not { Deleted: false })
            throw new JsonRpcException(JsonRpcException.InvalidRequest, "The calling session was deleted, so the session was not started.");
        string childId;
        try
        {
            childId = await CreateChildAsync(parent, host, cwd, prompt, title, report, approved: asks, ct);
        }
        catch (Exception e)
        {
            if (callId is not null) await EndSpawnCardAsync(parentUse.Feed, parent, callId, ToolCallStatus.Failed, $"Not started: {e.Message}");
            throw;
        }
        if (callId is not null) await EndSpawnCardAsync(parentUse.Feed, parent, callId, ToolCallStatus.Completed, $"Started {host.DisplayName} session {childId}.");
        ServiceLog.Info($"{parent.Agent} {parent.SessionId}: spawned {host.Name} session {childId} in {cwd}" + (asks ? "" : ", as the user asked"));
        var result = new JsonObject { ["sessionId"] = childId };
        if (askedAnyway is not null) result["note"] = AskedAnywayNote(askedAnyway).TrimStart();
        return result;
    }

    /// <summary>
    /// Why a spawn the agent says the user asked for (<c>initiation: user_asked</c>) is asked all the
    /// same, or null when it need not be: the caller's turn must have started with a client's prompt,
    /// the user's own, and no other session's text may have come into it
    /// (<see cref="SessionFeed.HasOtherSessionText"/>). Otherwise the request could come from another
    /// agent's text, whatever the agent believes.
    /// </summary>
    static string? WhyAskedAnyway(SessionFeed feed) =>
        feed.CurrentOpinion is not null ? "this turn answers another session's question"
        : !feed.ClientTurn ? "this turn did not start with a prompt from the user"
        : feed.HasOtherSessionText ? "another session's text (a question or a report) came into this turn, or the turn did not start with a prompt from the user"
        : null;

    /// <summary>What the agent is told when its <c>user_asked</c> spawn was asked all the same.</summary>
    static string AskedAnywayNote(string? why) =>
        why is null ? "" : $" The user was asked although you passed initiation user_asked, as {why}.";

    /// <summary>The spawned session's folder: the caller's by default; a relative path is taken from the caller's.</summary>
    static string ResolveFolder(string parentCwd, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return parentCwd.Length > 0
                ? FolderPath.Normalize(parentCwd)
                : throw new JsonRpcException(JsonRpcException.InvalidParams, "This session has no folder, so pass cwd: a full path to an existing folder.");
        var full = FolderPath.Normalize(Path.GetFullPath(Path.IsPathRooted(cwd) || parentCwd.Length == 0 ? cwd : Path.Combine(parentCwd, cwd)));
        if (Directory.Exists(full)) return full;
        throw new JsonRpcException(JsonRpcException.InvalidParams, $"No such folder: {full}. Pass an existing folder" +
            (parentCwd.Length > 0 ? $", or leave cwd out to work in this session's folder, {FolderPath.Normalize(parentCwd)}." : "."));
    }

    /// <summary>
    /// Shows the spawn in the caller's thread as a tool call and asks the person there, with
    /// <c>session/request_permission</c> on the calling session (allow once or reject, never
    /// always: every spawn is asked). Waits for a client if none is attached.
    /// </summary>
    Task<RequestPermissionOutcome> AskToSpawnAsync(SessionFeed feed, SessionRecord parent, string callId, string what, AgentHost host, string cwd,
        string prompt, string? title, string? why, CancellationToken ct)
    {
        var input = new JsonObject { ["agent"] = host.Name, ["cwd"] = cwd, ["prompt"] = prompt };
        if (title is not null) input["title"] = title;
        return AskPersonAsync(feed, parent, callId, what, input, $"{prompt}\n\nFolder: {cwd}" + (why is null ? "" : $"\n\n{why}"),
            new PermissionOption { OptionId = "start", Name = "Start", Kind = PermissionOptionKind.AllowOnce },
            new PermissionOption { OptionId = "decline", Name = "Don't start", Kind = PermissionOptionKind.RejectOnce }, ct);
    }

    /// <summary>
    /// Shows something the service does for a session's agent as a tool call in that session's
    /// thread, and asks the person there with <c>session/request_permission</c> (allow once or
    /// reject, never always). Waits for a client if none is attached, and asks the next one if a
    /// client goes away before answering.
    /// </summary>
    async Task<RequestPermissionOutcome> AskPersonAsync(SessionFeed feed, SessionRecord caller, string callId, string what, JsonObject input, string text,
        PermissionOption allow, PermissionOption decline, CancellationToken ct)
    {
        var card = new ToolCall
        {
            ToolCallId = callId,
            Title = what,
            Kind = ToolKind.Other,
            Status = ToolCallStatus.Pending,
            RawInput = input.DeepClone(),
            Content = [new Content { ContentValue = new TextContent { Text = text } }],
        };
        await feed.PublishAsync(AcpJson.ToNode<SessionUpdate>(card)!.AsObject(), except: null, () => store.Touch(caller.SessionId));
        var ask = new RequestPermissionRequest
        {
            SessionId = caller.SessionId,
            ToolCall = new ToolCallUpdate { ToolCallId = callId, Title = what, Kind = ToolKind.Other, Status = ToolCallStatus.Pending, RawInput = input },
            Options = [allow, decline],
        };
        while (true)
        {
            var client = await feed.WaitForClientAsync(_ => true, ct);
            try
            {
                // A withdrawn request (ct) sends the client $/cancel_request for this request.
                var answer = await client.Rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission, ask, ct);
                return answer.Outcome;
            }
            catch (Exception e) when (e is IOException or TimeoutException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                feed.Detach(client); // gone before answering: ask the next one
            }
        }
    }

    /// <summary>
    /// Whether the session runs in full access, as its agent last reported its state: the current
    /// value of its mode option (category <c>mode</c>), or its current <c>SessionMode</c>, carries
    /// <c>gorynych/fullAccess</c> in its <c>_meta</c>. A session whose agent reported no state does not.
    /// </summary>
    bool InFullAccess(string sessionId) => IsFullAccess(Feed(sessionId).State());

    internal static bool IsFullAccess(JsonObject state)
    {
        static bool Marked(JsonNode? value) => value?["_meta"]?[GorynychProtocol.MetaFullAccess] is JsonValue v && v.TryGetValue<bool>(out var on) && on;
        foreach (var option in (state["configOptions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)option["category"] != "mode" || (string?)option["type"] != "select" || (string?)option["currentValue"] is not { } current) continue;
            // Values are listed flat or in groups.
            var values = (option["options"] as JsonArray ?? []).OfType<JsonObject>()
                .SelectMany(o => o["options"] is JsonArray grouped ? grouped.OfType<JsonObject>() : [o]);
            if (values.Any(v => (string?)v["value"] == current && Marked(v))) return true;
        }
        return state["modes"] is JsonObject modes && (string?)modes["currentModeId"] is { } mode
            && (modes["availableModes"] as JsonArray ?? []).OfType<JsonObject>().Any(m => (string?)m["id"] == mode && Marked(m));
    }

    /// <summary>
    /// Before a session that is not in full access asks one that is (docs/service-protocol.md,
    /// "Approvals"): the asked agent runs anything without asking the person, so it could act on the
    /// request with rights the asking session does not have. Asks the person on the asking session,
    /// as before a spawn. Returns when allowed; throws with what the agent is told otherwise.
    /// </summary>
    async Task ConfirmAskIntoFullAccessAsync(string callerId, SessionRecord asked, string question, CancellationToken ct)
    {
        using var use = await UseAsync(callerId, ct);
        var callId = "gorynych-confirm-" + Interlocked.Increment(ref spawnCount);
        var allow = new PermissionOption { OptionId = "ask", Name = "Ask", Kind = PermissionOptionKind.AllowOnce };
        RequestPermissionOutcome outcome;
        try
        {
            outcome = await AskPersonAsync(use.Feed, use.Record, callId, $"Ask {Who(asked)} for an opinion",
                new JsonObject { ["session_id"] = asked.SessionId, ["question"] = question },
                $"{question}\n\nThat session runs in full access, so it could act on this request without asking you; this session does not.",
                allow, new PermissionOption { OptionId = "decline", Name = "Don't ask", Kind = PermissionOptionKind.RejectOnce }, ct);
        }
        catch (Exception e) when (e is OperationCanceledException or JsonRpcException)
        {
            await EndSpawnCardAsync(use.Feed, use.Record, callId, ToolCallStatus.Failed,
                e is OperationCanceledException ? "Not done: the request was withdrawn." : $"Not done: {e.Message}");
            throw;
        }
        if (outcome is RequestPermissionOutcomeCancelled)
        {
            await EndSpawnCardAsync(use.Feed, use.Record, callId, ToolCallStatus.Failed, "Not done: the turn was cancelled.");
            throw new JsonRpcException(JsonRpcException.RequestCancelled, "The turn was cancelled, so nothing was done.");
        }
        if (outcome is not SelectedPermissionOutcome selected || selected.OptionId != allow.OptionId)
        {
            await EndSpawnCardAsync(use.Feed, use.Record, callId, ToolCallStatus.Failed, "Not done: the user declined.");
            throw new JsonRpcException(JsonRpcException.InvalidRequest,
                $"The user declined to let this session ask session_id {asked.SessionId}, so it was not asked: that session runs in " +
                "full access and this one does not, so the user approves each such question. Go on without its opinion, or ask the user.");
        }
        await EndSpawnCardAsync(use.Feed, use.Record, callId, ToolCallStatus.Completed, "Allowed by the user.");
    }

    Task EndSpawnCardAsync(SessionFeed feed, SessionRecord parent, string callId, ToolCallStatus status, string text) =>
        feed.PublishAsync(AcpJson.ToNode<SessionUpdate>(new SessionUpdateToolCallUpdate
        {
            ToolCallId = callId,
            Status = status,
            Content = [new Content { ContentValue = new TextContent { Text = text } }],
        })!.AsObject(), except: null, () => store.Touch(parent.SessionId));

    /// <summary>
    /// Creates the child in its agent and starts its first turn without waiting for it. The child
    /// holds its turn gate before anyone can know it, and is a draft (never listed) until its
    /// journal has its parent: it is announced already holding its first turn, so a prompt that
    /// follows the notice runs after the spawn's. A child that fails before that is closed and
    /// deleted again, as is one whose spawn is withdrawn while its agent creates it.
    /// </summary>
    async Task<string> CreateChildAsync(SessionRecord parent, AgentHost host, string cwd, string prompt, string? title, bool report, bool approved,
        CancellationToken ct)
    {
        var rpc = await host.OpenTargetAsync(null, ct);
        var token = NewToken();
        // Not cancelled with the spawn: the agent may create the session anyway, and only its answer names it.
        // No mode is passed: the child starts in its agent's default, never in the caller's full access.
        var creating = await rpc.StartRequestAsync(AcpMethods.Agent.SessionNew,
            AcpJson.ToNode(new NewSessionRequest { Cwd = cwd, McpServers = WithPeers([], token), SystemPrompt = SystemPromptFor(host) }),
            CancellationToken.None);
        JsonNode? created;
        try
        {
            created = await creating.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            RunCleanup(async () =>
            {
                try
                {
                    if ((string?)(await creating)?["sessionId"] is { } late) await AbandonChildAsync(host, rpc, late, cwd);
                }
                finally
                {
                    host.DoneOpening(rpc);
                }
            });
            throw;
        }
        catch
        {
            host.DoneOpening(rpc);
            throw;
        }
        var id = (string?)created?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InternalError, $"{host.Name} created a session without an ID");
        var feed = Feed(id);
        await feed.TurnGate.WaitAsync(CancellationToken.None); // a new session's: free
        SessionRecord record;
        try
        {
            BindToken(token, id);
            host.MarkLive(id, rpc);
            host.DoneOpening(rpc);
            feed.CaptureState(created);
            store.Update(id, host.Name, rec =>
            {
                // A list that found the session in its agent just now has shown it already: it stays listed.
                rec.Draft = rec.Cwd.Length == 0;
                rec.Cwd = cwd;
                rec.PeerToken = token;
                rec.ParentSessionId = parent.SessionId;
                rec.ReportToParent = report;
                if (title is null) return;
                rec.Title = title;
                rec.TitleSource = SpawnTitle;
            });
            ct.ThrowIfCancellationRequested();
            var info = new JsonObject
            {
                ["sessionUpdate"] = "session_info_update",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaParentSessionId] = parent.SessionId },
            };
            if (title is not null)
            {
                info["title"] = title;
                feed.IsRepeatedTitle(new JsonObject { ["sessionUpdate"] = "session_info_update", ["title"] = title }); // so the same title is not sent again
            }
            await feed.PublishAsync(info, except: null, () => { });
            // Listed from here: announced as added, with its parent and name in the one change.
            record = store.Update(id, host.Name, rec => rec.Draft = false) ?? throw NotFound(id);
            if (title is not null && host.CanSetTitle) await SetNativeTitleAsync(host, rpc, id, title);
        }
        catch
        {
            feed.TurnGate.Release();
            await AbandonChildAsync(host, rpc, id, cwd);
            throw;
        }
        Track(Task.Run(() => SpawnedTurnAsync(feed, record, parent, prompt, approved)));
        return id;
    }

    /// <summary>
    /// Writes a name the service was given (a <c>spawn_session</c> title) as the session's native
    /// title, for an adapter that can (<c>_gorynych/set_title</c>), so the harness's own apps show it
    /// too. The overlay keeps it either way, so a failure is only logged.
    /// </summary>
    static async Task SetNativeTitleAsync(AgentHost host, JsonRpcConnection rpc, string sessionId, string title)
    {
        try
        {
            await rpc.SendRequestAsync(GorynychProtocol.MethodSetTitle, new JsonObject { ["sessionId"] = sessionId, ["title"] = title });
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            ServiceLog.Warn($"{host.Name} {sessionId}: the native title was not set: {e.Message}");
        }
    }

    /// <summary>
    /// Takes back a child whose spawn failed before its first turn: closes it in its agent, deletes
    /// it there when the agent can (it never had a turn), and forgets it. If the agent cannot be
    /// reached the child stays hidden.
    /// </summary>
    async Task AbandonChildAsync(AgentHost host, JsonRpcConnection rpc, string id, string cwd)
    {
        var record = new SessionRecord { SessionId = id, Agent = host.Name, Cwd = cwd };
        var gone = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cleanupStop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            host.MarkLive(id, rpc); // a late one was never marked
            await CloseInAgentAsync(host, rpc, id, timeout.Token);
            if (host.CanDelete) await NativeDeleteAsync(host, rpc, record, timeout.Token);
            gone = true;
        }
        catch (Exception e)
        {
            ServiceLog.Warn($"{host.Name}: the unused child {id} could not be deleted, hiding it: {e.Message}");
        }
        Forget(Feed(id), record, tombstone: !gone && host.CanDelete);
        ServiceLog.Info($"{host.Name}: dropped the child {id}, which was never started");
    }

    /// <summary>Keeps a turn or report that runs in the background, for <see cref="BackgroundTurnsIdleAsync"/>.</summary>
    void Track(Task task)
    {
        backgroundTurns[task] = 0;
        _ = task.ContinueWith(t => backgroundTurns.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>The first turn of a spawned session. Holds the session's turn gate, taken before the spawn returned.</summary>
    async Task SpawnedTurnAsync(SessionFeed feed, SessionRecord record, SessionRecord parent, string prompt, bool approved)
    {
        try
        {
            using var use = await UseAsync(record.SessionId, CancellationToken.None);
            if (feed.Retired) return;
            var (rpc, _) = await EnsureLiveAsync(use.Record, CancellationToken.None);
            // The task, then what the child needs to know about where it came from: two blocks, so
            // the task stays the first one; the journal keeps both as the one message the agent got.
            var task = new TextContent { Text = prompt };
            var context = new TextContent { Text = SpawnContext(store.Get(parent.SessionId) ?? parent, use.Record.ReportToParent, approved) };
            var meta = new JsonObject
            {
                [GorynychProtocol.MetaOrigin] = "spawn",
                [GorynychProtocol.MetaSender] = $"parent_{parent.Agent}",
                [GorynychProtocol.MetaParentSessionId] = parent.SessionId,
            };
            var shown = new TextContent { Text = $"{task.Text}\n\n{context.Text}" };
            var messageId = SessionFeed.NewMessageId();
            var asked = AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk
            {
                Content = shown, MessageId = messageId, Meta = meta,
            })!.AsObject();
            await feed.PublishAsync(asked, except: null, () => store.Touch(record.SessionId));
            await ProvisionalTitleAsync(feed, use.Record, [task]);
            feed.BeginTurnText(ReportLimit + 1);
            Task<JsonNode?> response;
            await feed.ForwardGate.WaitAsync(CancellationToken.None);
            try
            {
                response = await rpc.StartRequestAsync(AcpMethods.Agent.SessionPrompt,
                    WithMessageId(AcpJson.ToNode(new PromptRequest { SessionId = record.SessionId, Prompt = [task, context] }), messageId),
                    CancellationToken.None);
            }
            finally
            {
                feed.ForwardGate.Release();
            }
            var result = await response;
            KeepMark(feed, result);
            ReportTurnEnd(record.SessionId, Outcome(result), feed.EndTurnText());
        }
        catch (Exception e)
        {
            ServiceLog.Warn($"{record.Agent} {record.SessionId}: the spawned session's first turn failed: {e.Message}");
            // The parent holds the child's ID and may wait for news of it, so a turn that never got going is reported too.
            ReportTurnEnd(record.SessionId, $"failed: {e.Message}", feed.EndTurnText());
        }
        finally
        {
            feed.TurnEnded();
            feed.TurnGate.Release();
        }
    }

    /// <summary>Completes when the turns and reports running in the background (spawned first turns, reports to parents) are done.</summary>
    public async Task BackgroundTurnsIdleAsync()
    {
        while (backgroundTurns.Keys is { Count: > 0 } running)
        {
            await Task.WhenAll(running);
            await Task.Delay(10); // a finished one is dropped from the set just after it completes
        }
    }

    // ---------------------------------------------------------------- reports to the parent

    /// <summary>How much of a child's final message a report carries.</summary>
    const int ReportLimit = 2000;

    /// <summary>
    /// After a turn of a spawned session (its first one, or one a client prompted; not opinion
    /// or report turns): unless its spawn said otherwise, tells its parent in the background
    /// that the turn ended, with the child's ID, title and final message. A report to an archived
    /// parent is dropped (logged): it would start a turn there.
    /// </summary>
    void ReportTurnEnd(string childId, string outcome, string finalMessage)
    {
        if (store.Get(childId) is not { ParentSessionId: not null, ReportToParent: true }) return;
        Track(Task.Run(() => ReportAsync(childId, outcome, finalMessage)));
    }

    static string Outcome(JsonNode? promptResponse) => (string?)promptResponse?["stopReason"] switch
    {
        null or "end_turn" => "ended",
        "cancelled" => "was cancelled",
        var reason => $"stopped ({reason})",
    };

    /// <summary>
    /// Delivers a report like an opinion request, but expects no answer: through the parent
    /// agent's own way in (<c>_gorynych/note</c>, Claude's channel), otherwise as a marked turn
    /// of the parent session, which waits for the parent's running turn.
    /// </summary>
    async Task ReportAsync(string childId, string outcome, string finalMessage)
    {
        try
        {
            if (store.Get(childId) is not { ParentSessionId: { } parentId } child || child.Deleted) return;
            if (store.Get(parentId) is not { Deleted: false } parent) return;
            if (parent.Archived)
            {
                // Out of sight: a report would start a turn, and a channel note would wake Claude as well.
                ServiceLog.Info($"report: {childId} -> {parentId} dropped: the parent is archived");
                return;
            }
            var text = ReportText(child, outcome, finalMessage);
            var sender = $"child_{child.Agent}";
            using var use = await UseAsync(parentId, cleanupStop.Token); // its end starts the parent's idle timer
            var (rpc, _) = await EnsureLiveAsync(use.Record, cleanupStop.Token);
            // Marked before it is sent, so the agent cannot act on it first (SpawnAsync).
            var markedBefore = use.Feed.MarkOtherSessionText();
            try
            {
                await rpc.SendRequestAsync(GorynychProtocol.MethodNote, new JsonObject
                {
                    ["sessionId"] = parentId, ["text"] = text, ["sender"] = sender, ["fromSessionId"] = childId,
                    // for the user message the agent reports for it
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaSender] = sender, [GorynychProtocol.MetaFromSessionId] = childId },
                }, cleanupStop.Token);
                ServiceLog.Info($"report: {childId} -> {parentId} (note)");
                return;
            }
            catch (JsonRpcException e) when (e.Code is JsonRpcException.MethodNotFound or JsonRpcException.InvalidRequest)
            {
                // No live-note path, or the session's agent is not running (Claude): a turn reaches it
                // either way, and in the second case starts the agent just for it.
                use.Feed.UnmarkOtherSessionText(markedBefore);
            }
            await ReportTurnAsync(use, rpc, sender, childId, text);
            ServiceLog.Info($"report: {childId} -> {parentId} (turn)");
        }
        catch (Exception e)
        {
            ServiceLog.Warn($"report of {childId} to its parent not delivered: {e.Message}");
        }
    }

    /// <summary>
    /// Something that held the session let go of it: a client detached, or an operation registered
    /// on it ended (a turn, a service turn, a request of its agent waiting for a client). If it is
    /// now idle with no client, its idle timer starts again from now (<see cref="StartIdle"/>).
    /// </summary>
    void IdleFromNow(string sessionId)
    {
        if (StartIdle(sessionId) is { } unload) RunCleanup(unload);
        MoveIfStale(sessionId);
    }

    /// <summary>
    /// Starts the idle timer of a session that is loaded in its agent, idle (no turn running, no
    /// operation registered) and has no client attached; after
    /// <see cref="ConductorOptions.UnloadAfter"/> it is closed in its agent
    /// (<see cref="UnloadIfIdleAsync"/>). A client that attaches or an operation that registers
    /// stops the timer (<see cref="SessionFeed.StopIdle"/>), and the end of each starts it again,
    /// so the time counts from the later of the last client leaving and the session going idle.
    /// Nothing is cancelled. With no wait configured, returns the unload for the caller to run now.
    /// </summary>
    /// <param name="wait">How long; the configured idle time unless the timer runs again for what is left of it.</param>
    Func<Task>? StartIdle(string sessionId, TimeSpan? wait = null)
    {
        if (shutdown.IsCancellationRequested || Volatile.Read(ref disposed) != 0) return null; // the agents stop anyway
        if (!feeds.TryGetValue(sessionId, out var feed) || feed.Retired || feed.Users > 0 || feed.Clients().Length > 0) return null;
        if (store.Get(sessionId) is not { Draft: false } record || !agents.TryGetValue(record.Agent, out var host) || !host.IsLive(sessionId))
            return null; // a draft goes by its own rule (DiscardDraftAsync)
        var idle = IdleTimeOf(host);
        if (idle <= TimeSpan.Zero)
        {
            var now = feed.StopIdle(); // a timer started earlier no longer counts
            return () => UnloadIfIdleAsync(feed, now);
        }
        try
        {
            // One wait per session: starting one cancels the one before (SessionFeed.StartIdle).
            var (timer, stop) = feed.StartIdle(cleanupStop.Token);
            _ = Task.Delay(wait ?? idle, options.Time, stop).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) RunCleanup(() => UnloadIfIdleAsync(feed, timer));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            // disposed meanwhile: the agents stop anyway
        }
        return null;
    }

    /// <summary>
    /// How long an idle session of <paramref name="host"/>'s agent stays loaded: the service's
    /// <see cref="ConductorOptions.UnloadAfter"/>, or the adapter's own shorter time
    /// (<c>gorynych/unloadAfter</c>: Codex, whose loaded threads its own apps cannot write).
    /// </summary>
    TimeSpan IdleTimeOf(AgentHost host) =>
        host.UnloadAfter is { } own && own < options.UnloadAfter ? own : options.UnloadAfter;

    /// <summary>
    /// The idle timer <paramref name="timer"/> of a session ran out: the session is closed in its
    /// agent, unless the timer was stopped since (a client attached, an operation registered) or
    /// something holds it now. Checked under the session's gate, which a client's load takes too.
    /// An agent that sent an update within the idle time is not idle either, even if it marked no
    /// turn of its own (<see cref="AgentTurn"/>): the timer runs again for the rest of that time.
    /// An agent that cannot close sessions keeps it loaded.
    /// </summary>
    async Task UnloadIfIdleAsync(SessionFeed feed, long timer)
    {
        var sessionId = feed.SessionId;
        await feed.LifeGate.WaitAsync(cleanupStop.Token);
        try
        {
            if (feed.IdleTimer != timer || feed.Retired || feed.Users > 0 || feed.Clients().Length > 0) return;
            var idle = agents.TryGetValue(store.Get(sessionId)?.Agent ?? "", out var owner) ? IdleTimeOf(owner) : options.UnloadAfter;
            var quiet = options.Time.GetUtcNow() - feed.LastActivity;
            if (quiet < idle)
            {
                StartIdle(sessionId, idle - quiet);
                return;
            }
            await CloseUnheldAsync(sessionId, host => $"idle with no client for {IdleTimeOf(host)}");
        }
        finally
        {
            feed.LifeGate.Release();
        }
    }

    /// <summary>
    /// Closes, at once rather than after its idle time, a session the service loaded only to ask it
    /// something the person then declined (a question into full access), unless something holds it
    /// now: a client attached, or another operation.
    /// </summary>
    async Task UnloadNowAsync(SessionFeed feed)
    {
        try
        {
            await feed.LifeGate.WaitAsync(cleanupStop.Token);
            try
            {
                if (feed.Retired || feed.Users > 0 || feed.Clients().Length > 0) return;
                feed.StopIdle(); // a timer started when the question let go of it no longer counts
                await CloseUnheldAsync(feed.SessionId, _ => "loaded only for a question the user declined");
            }
            finally
            {
                feed.LifeGate.Release();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ServiceLog.Warn($"{feed.SessionId}: not unloaded after the declined question: {e.Message}");
        }
    }

    /// <summary>Closes a session nothing holds in its agent, if the agent can; under the session's life gate.</summary>
    async Task CloseUnheldAsync(string sessionId, Func<AgentHost, string> why)
    {
        if (store.Get(sessionId) is not { } record || !agents.TryGetValue(record.Agent, out var host)) return;
        if (host.RouteOf(sessionId) is not { CanClose: true } on) return;
        await CloseInAgentAsync(host, on.Rpc, sessionId, cleanupStop.Token);
        ServiceLog.Info($"{record.Agent} {sessionId}: unloaded from its agent, {why(host)}");
    }

    async Task ReportTurnAsync(SessionUse use, JsonRpcConnection rpc, string sender, string childId, string text)
    {
        var feed = use.Feed;
        await feed.TurnGate.WaitAsync(cleanupStop.Token);
        try
        {
            if (feed.Retired) return;
            rpc = await RpcForTurnAsync(use.Record, rpc, cleanupStop.Token, forAgent: true);
            if (store.Get(use.Record.SessionId) is { Archived: true })
            {
                ServiceLog.Info($"report: {childId} -> {use.Record.SessionId} dropped: the parent was archived while the report waited for its turn");
                return;
            }
            var meta = new JsonObject
            {
                [GorynychProtocol.MetaOrigin] = "report",
                [GorynychProtocol.MetaSender] = sender,
                [GorynychProtocol.MetaFromSessionId] = childId,
            };
            var block = new TextContent { Text = text };
            var messageId = SessionFeed.NewMessageId();
            var told = AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk
            {
                Content = block, MessageId = messageId, Meta = meta,
            })!.AsObject();
            await feed.PublishAsync(told, except: null, () => store.Touch(use.Record.SessionId));
            Task<JsonNode?> response;
            await feed.ForwardGate.WaitAsync(CancellationToken.None);
            try
            {
                response = await rpc.StartRequestAsync(AcpMethods.Agent.SessionPrompt, WithMessageId(AcpJson.ToNode(new PromptRequest
                {
                    SessionId = use.Record.SessionId,
                    Prompt = [block],
                    Meta = meta.DeepClone().AsObject(),
                }), messageId), CancellationToken.None);
            }
            finally
            {
                feed.ForwardGate.Release();
            }
            KeepMark(feed, await response);
        }
        finally
        {
            feed.TurnEnded();
            feed.TurnGate.Release();
        }
    }

    static string ReportText(SessionRecord child, string outcome, string finalMessage)
    {
        var message = finalMessage.Trim();
        if (message.Length > ReportLimit) message = Cut(message, ReportLimit) + "…";
        if (message.Length == 0) message = "(no final message)";
        return $"[Report · from {Who(child)} · your child session · its turn {outcome}]\n" +
            $"{message}\n\n" +
            "This comes from a session you started with spawn_session, not from the user. It needs no reply. " +
            $"To ask that session more, use ask_agent with session_id \"{child.SessionId}\".";
    }

    /// <summary>
    /// What a spawned session is told after its task: who started it, how its answer reaches that
    /// session (its final message, reported by itself, and what of it), and the ID to ask that
    /// session by. It follows the task, so an agent that titles a session from its first prompt
    /// (Kimi) takes the task as the title. <paramref name="approved"/>: the user approved the spawn;
    /// otherwise they asked for it in their own turn of that session.
    /// </summary>
    internal static string SpawnContext(SessionRecord parent, bool report, bool approved = true) =>
        $"[About this session · started by {Who(parent)} with spawn_session, {(approved ? "approved by the user" : "at the user's request")}]\n" +
        (report
            ? $"Answer in your final message: when your turn ends, Gorynych sends that message to session_id {parent.SessionId} as a " +
              "report by itself. Do not use reply or ask_agent for it. The report carries only that last message, your text after " +
              $"your last tool call, and is cut at {ReportLimit} characters, so put anything longer into a file and name the file " +
              "in that message. "
            : "Your turns are not reported to that session; it asks you with ask_agent when it wants to know something. ") +
        $"To ask it something, use ask_agent with session_id \"{parent.SessionId}\". The user can also prompt you here directly.";

    // ---------------------------------------------------------------- archive

    /// <summary>
    /// <c>_gorynych/archive {sessionId, archived}</c>, from a client or from the peers server's
    /// <c>archive_session</c> and <c>restore_session</c>: sets the session's archive flag in the
    /// overlay. Idempotent. Only a flag: nothing is closed, cancelled or unloaded, and nothing
    /// else ever clears it. A change is announced as <c>updated</c> at once and published to the
    /// session's clients as a <c>session_info_update</c> with the flag in <c>_meta</c>. A call
    /// with a peer token also gets <c>text</c> for the agent.
    /// </summary>
    internal async Task<JsonNode?> ArchiveAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var target = (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
        if (request.Params?["archived"] is not JsonValue flag || !flag.TryGetValue<bool>(out var archived))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "archived is required: true or false");
        string? from = null;
        if ((string?)request.Params?["_meta"]?[GorynychProtocol.MetaPeerToken] is { } token)
            from = peerTokens.TryGetValue(token, out var sid) ? sid : throw new JsonRpcException(JsonRpcException.InvalidRequest, "Unknown peer token");
        SessionUse use;
        try
        {
            use = await UseAsync(target, ct);
        }
        catch (JsonRpcException e) when (from is not null && e.Code == JsonRpcException.ResourceNotFound)
        {
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, archived
                ? $"No session \"{target}\". Pass an exact session_id from list_agents." + ArchivableHint(from)
                : $"No session \"{target}\". Pass an exact session_id from list_agents with include_archived: true." + ArchivedHint(from));
        }
        using (use)
        {
            SessionRecord record;
            bool changed;
            await archiveGate.WaitAsync(ct);
            try
            {
                record = store.Get(target) is { Deleted: false } now ? now : throw NotFound(target);
                changed = record.Archived != archived;
                if (changed)
                {
                    record = store.Update(target, record.Agent, r => r.Archived = archived) ?? throw NotFound(target);
                    var info = new JsonObject
                    {
                        ["sessionUpdate"] = "session_info_update",
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaArchived] = archived },
                    };
                    // A session the service has never journaled gets its history from its agent at its
                    // first load (LoadAsync); a journal holding only this would hide that history.
                    await use.Feed.PublishAsync(info, except: null, () => { }, journal: use.Feed.Journal.Count > 0);
                }
            }
            finally
            {
                archiveGate.Release();
            }
            if (changed) ServiceLog.Info($"{record.Agent} {target}: {(archived ? "archived" : "restored")}{(from is null ? "" : $" by {from}")}");
            var result = new JsonObject();
            if (from is not null) result["text"] = ArchiveText(record, archived, changed, self: from == target);
            return result;
        }
    }

    /// <summary>What <c>archive_session</c> and <c>restore_session</c> tell the agent: which session, and what it means now.</summary>
    static string ArchiveText(SessionRecord r, bool archived, bool changed, bool self)
    {
        var who = Who(r) + (self ? " (this session)" : "");
        return (archived, changed) switch
        {
            (true, true) => $"Archived {who}. It is now hidden from list_agents and from the user's session list; nothing in it is lost. " +
                "ask_agent still reaches it, and restore_session brings it back.",
            (true, false) => $"{who} was already archived, so nothing changed. ask_agent still reaches it, and restore_session brings it back.",
            (false, true) => $"Restored {who}. It is back in list_agents and in the user's session list.",
            (false, false) => $"{who} is not archived, so nothing changed. It is in list_agents and in the user's session list.",
        };
    }

    // ---------------------------------------------------------------- list changes

    /// <summary>
    /// Turns a change of the overlay into <c>_gorynych/sessions_changed</c>, by what clients list:
    /// a session that becomes listed (spawned, a draft's first prompt, found in an agent's list)
    /// is <c>added</c>, one that stops being listed (deleted) is <c>removed</c>, and a listed one
    /// whose title, parent or archive flag changes is <c>updated</c> at once; so is its activity,
    /// throttled. An archived session stays listed for clients that ask for archived ones, so
    /// archiving it is <c>updated</c>, not <c>removed</c>. Drafts are never announced. Runs under
    /// the store's lock, so changes are queued in their order.
    /// </summary>
    void OnRecordChanged(SessionRecord? before, SessionRecord after)
    {
        var listed = before?.Listed ?? false;
        if (!listed && after.Listed) AnnounceList(after.SessionId, after.Agent, "added");
        else if (listed && !after.Listed) AnnounceList(after.SessionId, after.Agent, "removed");
        else if (!after.Listed) return;
        else if (before!.Title != after.Title || before.ParentSessionId != after.ParentSessionId || before.Archived != after.Archived)
            AnnounceList(after.SessionId, after.Agent, "updated");
        else if (before.UpdatedAt != after.UpdatedAt) AnnounceActivity(after.SessionId, after.Agent);
    }

    /// <summary>
    /// An adapter's <c>_gorynych/sessions_changed {sessionId, change: "updated"}</c>: a row of its
    /// list changed outside any session (the Codex adapter: another Codex app took the thread or let
    /// it go, <c>gorynych/openElsewhere</c>). Passed on as <c>updated</c> for a session clients list,
    /// so they list it again; the adapter sends it only for a real change. Only from an adapter that
    /// advertises <c>gorynych/sessionsChanged</c>; other changes are not taken. It is no activity, so it
    /// does not hold back the next activity notice (<see cref="AnnounceActivity"/>).
    /// </summary>
    void OnAgentListChanged(AgentHost host, JsonNode? @params)
    {
        if (!host.TellsListChanges || (string?)@params?["sessionId"] is not { } sessionId || (string?)@params?["change"] != "updated") return;
        store.Inspect(sessionId, r => { if (r is { Listed: true } && r.Agent == host.Name) AnnounceList(sessionId, r.Agent, "updated", activity: false); });
    }

    /// <summary>
    /// Queues a notice for every client whose agent scope covers the session. Called under the
    /// store's lock only, which orders the notices; each client sends its own queue
    /// (<see cref="ClientConnection.Notify"/>), so one slow client holds up no other.
    /// </summary>
    /// <param name="activity">Counts for <see cref="AnnounceActivity"/>'s interval; a change that is not about the session's work does not.</param>
    void AnnounceList(string sessionId, string agent, string change, bool activity = true)
    {
        if (change == "removed") lastUpdateNotice.TryRemove(sessionId, out _);
        else if (activity) lastUpdateNotice[sessionId] = options.Time.GetTimestamp(); // "added" is news enough for the interval
        foreach (var client in clients.Keys)
        {
            if (client.Init is null || (client.AgentScope is not null && client.AgentScope != agent)) continue;
            client.Notify(GorynychProtocol.MethodSessionsChanged, new JsonObject { ["sessionId"] = sessionId, ["change"] = change });
        }
    }

    /// <summary>
    /// Announces a new <c>updatedAt</c> at most once per <see cref="ConductorOptions.ActivityNoticeInterval"/>
    /// per session; a change inside the interval is announced at its end. Called under the
    /// store's lock; the announcement at the end takes it again, so it never follows a <c>removed</c>.
    /// Timed by <see cref="ConductorOptions.Time"/>, like the idle timers.
    /// </summary>
    void AnnounceActivity(string sessionId, string agent)
    {
        var interval = options.ActivityNoticeInterval;
        var since = lastUpdateNotice.TryGetValue(sessionId, out var last) ? options.Time.GetElapsedTime(last) : TimeSpan.MaxValue;
        if (since >= interval)
        {
            AnnounceList(sessionId, agent, "updated");
            return;
        }
        if (!trailingUpdateNotice.TryAdd(sessionId, 0)) return;
        _ = Task.Delay(interval - since, options.Time).ContinueWith(delay =>
        {
            trailingUpdateNotice.TryRemove(sessionId, out _);
            store.Inspect(sessionId, r => { if (r is { Listed: true }) AnnounceList(sessionId, agent, "updated"); });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- reading a session

    /// <summary>A page of a session's history (<see cref="ReadAsync"/>).</summary>
    /// <param name="Live">The session's agent has it loaded now (<c>status: open</c>).</param>
    /// <param name="InProgress">A turn is running.</param>
    /// <param name="Trimmed">The journal has lost its oldest entries.</param>
    internal sealed record SessionRead(SessionRecord Record, bool Live, SessionReader.Page Page, bool InProgress, bool Trimmed);

    /// <summary>A session whose history could not be loaded from its agent.</summary>
    internal sealed class UnreadableException(SessionRecord record, Exception inner) : Exception(inner.Message, inner)
    {
        public SessionRecord Record { get; } = record;
    }

    /// <summary>
    /// Reads a page of a session's history from the service journal; reading never runs a turn. A
    /// session the service has never journaled is loaded in its agent first, which replays its
    /// history into the journal (Kimi), and unloaded again after its idle time when it was loaded only for
    /// this, as after service turns. Reading a Claude session never starts Claude: the service
    /// journals every Claude session from its first prompt, and the Claude adapter cannot load one
    /// (learning that may start the in-process adapter, which starts no claude process).
    /// </summary>
    /// <exception cref="SessionReader.UnknownCursorException">The cursor names no message of the session.</exception>
    internal async Task<SessionRead> ReadAsync(string sessionId, string? cursor, int limit, bool thoughts, int? budget, CancellationToken ct)
    {
        using var use = await UseAsync(sessionId, ct); // its end starts the idle timer of a session loaded for this
        var feed = use.Feed;
        var host = HostOf(use.Record);
        if (!feed.Journal.HasTurns && !host.IsLive(sessionId))
        {
            try { await LoadHistoryAsync(use, ct); }
            catch (Exception e) when (e is JsonRpcException or IOException) { throw new UnreadableException(use.Record, e); }
        }
        var page = SessionReader.Read(feed.Journal.ReadAll(), cursor, limit, thoughts, budget);
        return new SessionRead(store.Get(sessionId) ?? use.Record, host.IsLive(sessionId), page, feed.TurnGate.CurrentCount == 0, feed.Journal.Trimmed);
    }

    /// <summary>
    /// Loads a session the journal has no turn of in its agent, journaling the agent's replay; it
    /// is unloaded again after its idle timer, like a session woken for service turns. Does nothing
    /// when the agent cannot load sessions (Claude), or the session was loaded meanwhile.
    /// </summary>
    async Task LoadHistoryAsync(SessionUse use, CancellationToken ct)
    {
        var record = use.Record;
        var host = HostOf(record);
        await host.GetAsync(ct);
        if (!host.CanLoad) return;
        await host.AwaitHostOfAsync(record, ct);
        await use.Feed.OpenGate.WaitAsync(ct);
        try
        {
            if (host.IsLive(record.SessionId) || use.Feed.Journal.HasTurns) return;
            var rpc = await host.OpenTargetAsync(record.SessionId, ct);
            try
            {
                var opened = await rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, AcpJson.ToNode(new LoadSessionRequest
                {
                    SessionId = record.SessionId, Cwd = record.Cwd, McpServers = PeersFor(record.SessionId), SystemPrompt = SystemPromptFor(host),
                }), ct) as JsonObject;
                host.MarkLive(record.SessionId, rpc);
                use.Feed.CaptureState(opened);
                ServiceLog.Info($"{host.Name}: loaded {record.SessionId} to read its history");
            }
            finally
            {
                host.DoneOpening(rpc);
            }
        }
        finally
        {
            use.Feed.OpenGate.Release();
        }
    }

    /// <summary><c>_gorynych/read {sessionId, cursor?, limit?, thoughts?}</c> → <c>{updates, nextCursor?, total, inProgress, trimmed}</c>.</summary>
    internal async Task<JsonNode?> ReadMethodAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var sessionId = (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
        var cursor = (string?)request.Params?["cursor"];
        var limit = request.Params?["limit"] is JsonValue l && l.TryGetValue<int>(out var n) ? n : SessionReader.DefaultLimit;
        var thoughts = request.Params?["thoughts"] is JsonValue t && t.TryGetValue<bool>(out var b) && b;
        SessionRead read;
        try
        {
            read = await ReadAsync(sessionId, cursor, limit, thoughts, budget: null, ct);
        }
        catch (SessionReader.UnknownCursorException e)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"Session {sessionId} has no message \"{e.Cursor}\".");
        }
        catch (UnreadableException e)
        {
            throw e.InnerException as JsonRpcException ?? new JsonRpcException(JsonRpcException.InternalError, e.Message);
        }
        var result = new JsonObject
        {
            ["updates"] = new JsonArray(read.Page.Updates.Select(u => (JsonNode)u).ToArray()),
            ["total"] = read.Page.Total,
            ["inProgress"] = read.InProgress,
            ["trimmed"] = read.Trimmed,
        };
        if (read.Page.NextCursor is { } next) result["nextCursor"] = next;
        return result;
    }

    // ---------------------------------------------------------------- opinions between agents

    /// <summary>
    /// <c>_gorynych/opinion</c>: asks the agent of <c>sessionId</c> for an opinion. A call from
    /// the peers MCP server carries its token, which names the asking session and its agent.
    /// </summary>
    internal async Task<JsonNode?> OpinionAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var target = (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
        var text = (string?)request.Params?["text"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "text is required");
        var sender = (string?)request.Params?["sender"];
        string? from = null;
        if ((string?)request.Params?["_meta"]?[GorynychProtocol.MetaPeerToken] is { } token)
        {
            from = peerTokens.TryGetValue(token, out var sid) ? sid : throw new JsonRpcException(JsonRpcException.InvalidRequest, "Unknown peer token");
            if (from == target)
                throw new JsonRpcException(JsonRpcException.InvalidParams,
                    $"\"{target}\" is this session's own session_id; ask_agent asks another session." + AskableHint(from));
            sender = $"peer_{store.Get(from)?.Agent ?? "agent"}";
        }
        if (from is not null && Feed(from).CurrentOpinion is { } answering)
            throw new JsonRpcException(JsonRpcException.InvalidRequest,
                $"This session is answering opinion request {answering}, so it cannot ask another session during this turn. " +
                $"Send your answer with the reply tool, passing message_id \"{answering}\"; if another opinion would help, say so in the answer.");
        // The asked agent sees who asks by the asking session's real ID, which it can pass to ask_agent;
        // sender (peer_claude) is only a label for GUIs.
        var origin = from is not null && store.Get(from) is { } asking ? $"from {Who(asking)}" : $"from {sender ?? "a Gorynych client"}";
        return await AskAsync(target, text, new Asker(from, sender ?? "peer_agent", origin, Outside: null), onState: null, ct);
    }

    /// <summary>
    /// Who asks for an opinion: a session (<see cref="From"/>, named by its peer token), an agent
    /// outside Gorynych (<see cref="Outside"/>, the host object of <c>_gorynych/mcp_message</c>), or
    /// a client. <see cref="Origin"/> is how the request's first line names the asker.
    /// </summary>
    internal sealed record Asker(string? From, string Sender, string Origin, JsonObject? Outside)
    {
        /// <summary>An agent asks, so errors say how to recover.</summary>
        public bool IsAgent => From is not null || Outside is not null;
    }

    /// <summary>
    /// Asks <paramref name="target"/> for an opinion on behalf of <paramref name="asker"/>: through
    /// its agent's live-note path when it has one and runs, else in a turn, waking the session for
    /// it if needed. <paramref name="onState"/> hears each change of state (waiting for the running
    /// turn, starting the session, asked), for an outside agent's progress notifications.
    /// </summary>
    internal async Task<JsonNode?> AskAsync(string target, string text, Asker asker, Action<string>? onState, CancellationToken ct)
    {
        if (Draining) throw Restarting();
        var use = await UseAskedAsync(target, asker, ct);
        var turnHasUse = false; // an opinion turn releases the use when the turn ends, which may be after the answer
        var unload = false; // the person declined a question into a session this call loaded only for it
        try
        {
            var record = use.Record;
            JsonRpcConnection rpc;
            var woken = false; // by this call, so a failed turn is a failure to start
            try
            {
                // Not loaded in its agent: loaded now, and unloaded again after its idle timer.
                woken = !HostOf(record).IsLive(target);
                if (woken) onState?.Invoke(StartingState(target));
                (rpc, _) = await EnsureLiveAsync(record, ct);
            }
            catch (Exception e) when (asker.IsAgent && e is not OperationCanceledException)
            {
                // Held by another app (its agent says so: _gorynych/open_elsewhere): only closing it there helps.
                if ((await OpenElsewhereAsync([record], ct)).GetValueOrDefault(record.SessionId) is { } holder)
                    throw OpenElsewhereError(record, holder, asker, e);
                throw CouldNotStart(record, e, asker);
            }
            // Open for reading only, as an agent opens one another app holds: it takes no question, said at once.
            if (asker.IsAgent && use.Feed.ReadOnly is { } readOnly)
            {
                unload = woken;
                var why = new JsonRpcException(JsonRpcException.InvalidRequest, readOnly.Reason);
                throw readOnly.OpenElsewhere is { } holder ? OpenElsewhereError(record, holder, asker, why) : CouldNotStart(record, why, asker);
            }
            // Its mode now, as its agent reports it once it is loaded: for a session that was not, the
            // mode it was loaded in. An agent outside Gorynych is approved by its own host.
            var confirmed = false;
            if (asker.From is { } from && InFullAccess(target) && !InFullAccess(from))
            {
                try
                {
                    await ConfirmAskIntoFullAccessAsync(from, record, text, ct);
                }
                catch
                {
                    unload = woken;
                    throw;
                }
                confirmed = true;
            }
            ServiceLog.Info($"opinion: {asker.From ?? asker.Sender} asks {record.Agent} {target}");
            var forwarded = new JsonObject
            {
                ["sessionId"] = target,
                ["text"] = $"[Opinion request · {asker.Origin}]\n{text}",
                ["sender"] = asker.Sender,
                ["_meta"] = AskerMeta(asker), // for the user message the agent reports for it
            };
            if (asker.From is not null) forwarded["fromSessionId"] = asker.From;
            // A live-note path answers at once when its agent cannot take the request, so it counts
            // as asked only after a moment.
            using var askedLater = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var markedBefore = false;
            if (onState is not null)
                _ = Task.Delay(TimeSpan.FromMilliseconds(500), askedLater.Token)
                    .ContinueWith(t => { if (!t.IsCanceled) onState(AskedState(target)); }, TaskScheduler.Default);
            try
            {
                // An agent with its own way in (Claude's channel) answers directly.
                // Marked before it is sent, so the agent cannot act on it first (SpawnAsync).
                markedBefore = use.Feed.MarkOtherSessionText();
                var answer = await rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, forwarded, ct);
                if (record.Draft)
                {
                    // It answered, so it has history now.
                    try { Promote(record); }
                    catch (JsonRpcException e) { ServiceLog.Warn($"{record.Agent} {target}: {e.Message}"); }
                }
                return answer;
            }
            catch (JsonRpcException e) when (e.Code is JsonRpcException.MethodNotFound or JsonRpcException.InvalidRequest)
            {
                // No live-note path (Kimi), or the agent is not running (Claude refuses while its process
                // is down): ask in a turn, which in the second case starts the agent just for it.
                use.Feed.UnmarkOtherSessionText(markedBefore);
                askedLater.Cancel();
                if (e.Code == JsonRpcException.InvalidRequest)
                {
                    woken = true;
                    onState?.Invoke(StartingState(target));
                }
                turnHasUse = true;
                return await OpinionTurnAsync(use, rpc, asker, text, woken, confirmed, onState, ct);
            }
            finally
            {
                askedLater.Cancel();
            }
        }
        finally
        {
            if (!turnHasUse) use.Dispose(); // closes the session again if the service woke it for this
            if (unload) await UnloadNowAsync(use.Feed);
        }
    }

    /// <summary>Who asks, in the <c>_meta</c> keys the journal keeps on a request: the same on the live-note path as in a turn.</summary>
    static JsonObject AskerMeta(Asker asker)
    {
        var meta = new JsonObject { [GorynychProtocol.MetaSender] = asker.Sender };
        if (asker.From is not null) meta[GorynychProtocol.MetaFromSessionId] = asker.From;
        if (asker.Outside is not null) meta[GorynychProtocol.MetaOutside] = asker.Outside.DeepClone();
        return meta;
    }

    static string WaitingState(string sessionId) => $"Waiting for session_id {sessionId} to finish its running turn.";
    static string StartingState(string sessionId) => $"Starting session_id {sessionId} to answer.";
    static string AskedState(string sessionId) => $"Asked session_id {sessionId}; waiting for its answer.";

    /// <summary>For an agent that asks: the asked session could not be started (or failed at once after it was).</summary>
    /// <summary>For an agent that asks: the asked session is open in another app, which holds it.</summary>
    static JsonRpcException OpenElsewhereError(SessionRecord record, string holder, Asker asker, Exception e) =>
        new(e is JsonRpcException rpc ? rpc.Code : JsonRpcException.InvalidRequest,
            $"{Who(record)} is open in {holder}, which holds it, so it cannot be asked until it is closed there. " +
            $"Ask another session from {(asker.Outside is not null ? "list_sessions" : "list_agents")}, or ask the user to close it in {holder}.");

    static JsonRpcException CouldNotStart(SessionRecord record, Exception e, Asker asker)
    {
        var reason = e.Message.TrimEnd();
        if (!reason.EndsWith('.')) reason += ".";
        return new JsonRpcException(e is JsonRpcException rpc ? rpc.Code : JsonRpcException.InternalError,
            $"{Who(record)} could not be started to answer: {reason} " + (asker.Outside is not null
                ? "Ask another session from list_sessions, or ask the user to open that session in Gorynych and check it."
                : "Ask another session from list_agents, or ask the user to open that session and check it."));
    }

    /// <summary>The asked session; for an agent that asks, an unknown ID fails with the IDs it can pass instead.</summary>
    async Task<SessionUse> UseAskedAsync(string target, Asker asker, CancellationToken ct)
    {
        try
        {
            return await UseAsync(target, ct);
        }
        catch (JsonRpcException e) when (asker.IsAgent && e.Code == JsonRpcException.ResourceNotFound)
        {
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, asker.Outside is not null
                ? UnknownToOutside(target)
                : $"No session \"{target}\". Pass an exact session_id from list_agents." + AskableHint(asker.From!));
        }
    }

    /// <summary>What an agent outside Gorynych is told for a session ID that names no session.</summary>
    internal string UnknownToOutside(string target) =>
        $"No session \"{target}\". Pass an exact session_id from list_sessions." + Hint(" Sessions in Gorynych:",
            WithHistory().Where(c => !c.Record.Archived).Select(c => c.Record), self: "",
            "\n- and more: list_sessions shows them all", " There is no session in Gorynych now.");

    /// <summary>
    /// How a message the service puts in front of an agent names a session: its exact ID, labelled
    /// <c>session_id</c> so the agent can pass it to <c>ask_agent</c>, then its agent and title.
    /// </summary>
    internal static string Who(SessionRecord r) =>
        $"session_id {r.SessionId} ({r.Agent}" + (r.Title is { Length: > 0 } title ? $", \"{Shorten(title).Replace('"', '\'')}\"" : "") + ")";

    /// <summary>The sessions with history, open ones first, then the newest.</summary>
    IEnumerable<(SessionRecord Record, bool Live)> WithHistory() => store.All()
        .Where(HasHistory)
        .Select(r => (Record: r, Live: agents.TryGetValue(r.Agent, out var host) && host.IsLive(r.SessionId)))
        .OrderByDescending(c => c.Live).ThenByDescending(c => c.Record.UpdatedAt);

    /// <summary>
    /// The sessions <paramref name="self"/> lists with <c>list_agents</c>: the others with history,
    /// archived ones only when asked for (<c>ask_agent</c> reaches those too).
    /// </summary>
    IEnumerable<(SessionRecord Record, bool Live)> Askable(string? self, bool includeArchived = false) =>
        WithHistory().Where(c => c.Record.SessionId != self && (includeArchived || !c.Record.Archived));

    /// <summary>For a peer tool's error: the first sessions the caller can ask, so it can retry with an exact ID.</summary>
    string AskableHint(string self) => Hint(" Sessions you can ask:", Askable(self).Select(c => c.Record), self,
        "\n- and more: list_agents shows them all", " There is no other session to ask now.");

    /// <summary>For <c>archive_session</c>'s error: the sessions it can archive, the caller's own first.</summary>
    string ArchivableHint(string self) => Hint(" Sessions you can archive:",
        WithHistory().Where(c => !c.Record.Archived).OrderByDescending(c => c.Record.SessionId == self).Select(c => c.Record), self,
        "\n- and more: list_agents shows them all", " There is no session to archive now.");

    /// <summary>For <c>restore_session</c>'s error: the archived sessions.</summary>
    string ArchivedHint(string self) => Hint(" Archived sessions:", WithHistory().Where(c => c.Record.Archived).Select(c => c.Record), self,
        "\n- and more: list_agents with include_archived: true shows them all", " No session is archived now.");

    static string Hint(string label, IEnumerable<SessionRecord> candidates, string self, string more, string none)
    {
        const int Shown = 5;
        var list = candidates.Take(Shown + 1).ToList();
        if (list.Count == 0) return none;
        var hint = new StringBuilder(label);
        foreach (var r in list.Take(Shown)) hint.Append("\n- ").Append(Who(r)).Append(r.SessionId == self ? " (this session)" : "");
        if (list.Count > Shown) hint.Append(more);
        return hint.ToString();
    }

    /// <summary>
    /// Asks an agent without a live-note path (Kimi), or one that is not running (Claude), in an
    /// ordinary <c>session/prompt</c> turn, marked with the asking session and a <c>message_id</c>.
    /// The turn waits for the session's current turn, since ACP cannot inject into a running one.
    /// The answer is the agent's <c>reply</c> tool call (<c>_gorynych/reply</c>), or else the turn's
    /// own text. Takes over <paramref name="use"/> and releases it when the turn ends.
    /// </summary>
    /// <param name="woken">The service loaded or started the session for its turns, so a failure here is a failure to start.</param>
    /// <param name="confirmed">The person was already asked about this question going into full access.</param>
    async Task<JsonNode?> OpinionTurnAsync(SessionUse use, JsonRpcConnection rpc, Asker asker, string question,
        bool woken, bool confirmed, Action<string>? onState, CancellationToken ct)
    {
        var record = use.Record;
        var feed = use.Feed;
        var messageId = $"m{runTag}-{Interlocked.Increment(ref opinionCount)}";
        var pending = new PendingOpinion(record.SessionId);
        pendingOpinions[messageId] = pending;
        var turnStarted = false;
        var unload = false; // declined: the session this call woke goes again at once
        try
        {
            if (feed.TurnGate.CurrentCount == 0) onState?.Invoke(WaitingState(record.SessionId));
            await feed.TurnGate.WaitAsync(ct);
            // Either session's mode may have changed while the question waited for the turn: checked
            // again, and asked, with the turn let go meanwhile, if it was not asked before.
            if (!confirmed && asker.From is { } from && InFullAccess(record.SessionId) && !InFullAccess(from))
            {
                feed.TurnGate.Release();
                try
                {
                    await ConfirmAskIntoFullAccessAsync(from, record, question, ct);
                }
                catch
                {
                    unload = woken;
                    throw;
                }
                if (feed.TurnGate.CurrentCount == 0) onState?.Invoke(WaitingState(record.SessionId));
                await feed.TurnGate.WaitAsync(ct);
            }
            Task turnDone;
            try
            {
                if (feed.Retired) throw NotFound(record.SessionId);
                record = Promote(record);
                rpc = await RpcForTurnAsync(record, rpc, ct, forAgent: true);
                var text = OpinionPrompt(asker, messageId, question);
                var meta = new JsonObject
                {
                    [GorynychProtocol.MetaOrigin] = "peer",
                    [GorynychProtocol.MetaSender] = asker.Sender,
                    [GorynychProtocol.MetaOpinion] = messageId,
                };
                if (asker.From is not null) meta[GorynychProtocol.MetaFromSessionId] = asker.From;
                if (asker.Outside is not null) meta[GorynychProtocol.MetaOutside] = asker.Outside.DeepClone();
                var askedId = SessionFeed.NewMessageId();
                var asked = AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk
                {
                    Content = new TextContent { Text = text }, MessageId = askedId, Meta = meta,
                })!.AsObject();
                await feed.PublishAsync(asked, except: null, () => store.Touch(record.SessionId));
                feed.BeginOpinionTurn(messageId);
                Task<JsonNode?> response;
                await feed.ForwardGate.WaitAsync(CancellationToken.None);
                try
                {
                    response = await rpc.StartRequestAsync(AcpMethods.Agent.SessionPrompt, WithMessageId(AcpJson.ToNode(new PromptRequest
                    {
                        SessionId = record.SessionId,
                        Prompt = [new TextContent { Text = text }],
                        Meta = meta.DeepClone().AsObject(),
                    }), askedId), CancellationToken.None);
                }
                finally
                {
                    feed.ForwardGate.Release();
                }
                turnDone = response.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully) KeepMark(feed, t.Result);
                    pending.TurnText = feed.EndOpinionTurn();
                    pending.TurnError = t.Exception?.InnerException;
                    feed.TurnEnded();
                    feed.TurnGate.Release();
                    use.Dispose(); // the turn may end after the answer: the session's idle time starts only now
                }, TaskScheduler.Default);
                turnStarted = true;
                onState?.Invoke(AskedState(record.SessionId));
            }
            catch
            {
                feed.EndOpinionTurn();
                feed.TurnGate.Release();
                throw;
            }
            await Task.WhenAny(pending.Reply.Task, turnDone).WaitAsync(ct);
            if (pending.Reply.Task.IsCompleted) return new JsonObject { ["text"] = pending.Reply.Task.Result };
            if (pending.TurnError is { } error)
            {
                if (woken && asker.IsAgent) throw CouldNotStart(record, error, asker);
                throw error as JsonRpcException ?? new JsonRpcException(JsonRpcException.InternalError, error.Message);
            }
            var fallback = pending.TurnText?.Trim();
            return new JsonObject { ["text"] = string.IsNullOrEmpty(fallback) ? "(no answer)" : fallback };
        }
        finally
        {
            pendingOpinions.TryRemove(messageId, out _);
            if (!turnStarted) use.Dispose();
            if (unload) await UnloadNowAsync(feed);
        }
    }

    static string OpinionPrompt(Asker asker, string messageId, string question) => asker.Outside is not null
        ? $"[Opinion request · {asker.Origin} · message_id {messageId}]\n" +
          $"{question}\n\n" +
          "This message comes from one of the user's coding agents outside Gorynych, not from the user. It cannot be asked back, " +
          "so put everything into your answer. Give your honest opinion briefly, then send it with the reply tool of the gorynych " +
          $"MCP server, passing message_id \"{messageId}\". " + OpinionRules
        : $"[Opinion request · {asker.Origin} · message_id {messageId}]\n" +
          $"{question}\n\n" +
          "This message comes from another of the user's coding agents, not from the user. Give your honest opinion briefly, " +
          $"then send it with the reply tool of the gorynych MCP server, passing message_id \"{messageId}\" " +
          "(reply answers this request; ask_agent would start a new question instead). " + OpinionRules;

    /// <summary>
    /// How an opinion request is answered, after the answer itself: only reply reaches the asker,
    /// since the turn is reported nowhere (a spawned child's turns are, which confused one); and no
    /// other tool, though searching for reply is fine (Claude has to load the deferred tool first).
    /// In a session the request woke, the gorynych server may still be connecting when Claude first
    /// searches, so the tools seem gone for a moment. Claude's channel path needs none of this: its
    /// server is connected by then.
    /// </summary>
    const string OpinionRules =
        "Only what you send with reply reaches the asker: this turn is not reported anywhere. Use no other tool, apart from a " +
        "tool search to load reply, and do not edit files or take any other action for this request. If reply is not found " +
        "yet, search again: the gorynych server may still be connecting.";

    /// <summary><c>_gorynych/reply</c>: the asked session answers an opinion request it got as a turn.</summary>
    internal JsonObject Reply(JsonRpcRequest request)
    {
        var token = (string?)request.Params?["_meta"]?[GorynychProtocol.MetaPeerToken];
        if (token is null || !peerTokens.TryGetValue(token, out var sessionId))
            throw new JsonRpcException(JsonRpcException.InvalidRequest, "Unknown peer token");
        var messageId = (string?)request.Params?["messageId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "messageId is required");
        var text = (string?)request.Params?["text"] ?? "";
        var delivered = pendingOpinions.TryGetValue(messageId, out var pending) && pending.SessionId == sessionId && pending.Reply.TrySetResult(text);
        return new JsonObject { ["delivered"] = delivered };
    }

    /// <summary>
    /// The option to choose for a pre-allowed gorynych tool, or null to ask the person. A request that
    /// names its tool call by ID alone, as ACP allows, is matched by the title the call was published with.
    /// </summary>
    static string? PreAllowed(JsonNode? @params, SessionFeed feed)
    {
        if (AcpJson.FromNode<RequestPermissionRequest>(@params) is not { } request
            || (request.ToolCall.Title ?? feed.ToolTitle(request.ToolCall.ToolCallId)) is not { } title
            || !PreAllowedTools.Contains(title)) return null;
        return (request.Options.FirstOrDefault(o => o.Kind == PermissionOptionKind.AllowOnce)
            ?? request.Options.FirstOrDefault(o => o.Kind.Value.StartsWith("allow", StringComparison.Ordinal)))?.OptionId;
    }

    sealed class PendingOpinion(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public TaskCompletionSource<string> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? TurnText { get; set; }
        public Exception? TurnError { get; set; }
    }

    /// <summary>
    /// <c>_gorynych/peers {token, includeArchived?}</c>: the sessions the session behind <c>token</c>
    /// can ask; archived ones only with <c>includeArchived</c>, and then every entry says whether it is.
    /// Each entry's <c>status</c> uses the words of ACP's session-status RFD: <c>open</c> when its agent
    /// has it loaded now, <c>saved</c> when it does not (a question starts it, and it is unloaded again after its idle time).
    /// <c>caller</c> names the session behind the token as <see cref="Who"/> does, since the list
    /// leaves it out and an agent does not know its own ID otherwise. Without <c>includeArchived</c>,
    /// <c>hiddenArchived</c> counts the archived sessions the list would otherwise show (never the caller).
    /// </summary>
    internal Task<JsonObject> PeersAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var token = (string?)request.Params?["token"];
        var self = token is not null && peerTokens.TryGetValue(token, out var sid) ? sid : null;
        var includeArchived = request.Params?["includeArchived"] is JsonValue all && all.TryGetValue<bool>(out var include) && include;
        return PeerListAsync(self, includeArchived, ct);
    }

    /// <summary>
    /// The list of <see cref="PeersAsync"/>; for an agent outside Gorynych (no <paramref name="self"/>)
    /// every session with history. A session another app has open, as its running agent says now
    /// (<c>_gorynych/open_elsewhere</c>), has <c>open_elsewhere</c>: it cannot be asked until it is
    /// closed there. An agent that is not running is not started for it, and its sessions show none.
    /// </summary>
    internal async Task<JsonObject> PeerListAsync(string? self, bool includeArchived, CancellationToken ct)
    {
        var sessions = new JsonArray();
        var askable = Askable(self, includeArchived).ToList();
        var elsewhere = await OpenElsewhereAsync(askable.Select(c => c.Record), ct);
        foreach (var (r, live) in askable)
        {
            var entry = new JsonObject
            {
                ["session_id"] = r.SessionId,
                ["agent"] = r.Agent,
                ["title"] = r.Title,
                ["cwd"] = FolderPath.Normalize(r.Cwd),
                ["status"] = live ? "open" : "saved",
                ["updated_at"] = r.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
                ["parent_session_id"] = r.ParentSessionId,
            };
            if (includeArchived) entry["archived"] = r.Archived;
            if (elsewhere.TryGetValue(r.SessionId, out var holder)) entry["open_elsewhere"] = OpenElsewhereText(holder);
            sessions.Add((JsonNode)entry);
        }
        var result = new JsonObject { ["sessions"] = sessions };
        if (self is not null && store.Get(self) is { } caller)
        {
            result["caller"] = Who(caller);
            // The list leaves the caller out, so a spawned session would not find its parent there.
            if (caller.ParentSessionId is { } parentId && store.Get(parentId) is { Deleted: false } parent) result["callerParent"] = Who(parent);
        }
        if (!includeArchived) result["hiddenArchived"] = Askable(self, includeArchived: true).Count(c => c.Record.Archived);
        return result;
    }

    /// <summary>What a session list tells an agent about a session another app has open.</summary>
    internal static string OpenElsewhereText(string holder) => $"{holder} has it open; it cannot be asked until it is closed there";

    /// <summary>
    /// Which of these sessions another app has open now, as their agents say: only agents that run
    /// (none is started for a list) and take <c>_gorynych/open_elsewhere</c>. A hint, so any failure
    /// of an agent is "not known" and the list goes on. Briefly bounded, so a slow agent does not hold it up.
    /// </summary>
    async Task<Dictionary<string, string>> OpenElsewhereAsync(IEnumerable<SessionRecord> records, CancellationToken ct)
    {
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in records.GroupBy(r => r.Agent))
        {
            if (!agents.TryGetValue(group.Key, out var host) || !host.TellsOpenElsewhere || host.Running is not { } rpc) continue;
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(TimeSpan.FromSeconds(5));
                var answer = await rpc.SendRequestAsync(GorynychProtocol.MethodOpenElsewhere,
                    new JsonObject { ["sessionIds"] = new JsonArray(group.Select(r => (JsonNode)r.SessionId).ToArray()) }, bounded.Token);
                foreach (var (id, holder) in answer?["sessions"] as JsonObject ?? [])
                    // One open in another host of this service's (a stale one during an update) is not
                    // elsewhere, unless it is open there for reading only, held by another app.
                    if (holder is JsonValue v && v.TryGetValue<string>(out var name) && name.Length > 0
                        && (!host.IsLive(id) || feeds.TryGetValue(id, out var feed) && feed.ReadOnly is not null)) held[id] = name;
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                ServiceLog.Warn($"{host.Name}: could not tell which sessions are open elsewhere: {e.Message}");
            }
        }
        return held;
    }

    static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    void BindToken(string token, string sessionId)
    {
        peerTokens[token] = sessionId;
        sessionTokens[sessionId] = token;
    }

    /// <summary>
    /// The peers server for a session being opened, with its token, which the overlay keeps: the
    /// agent may hold it longer than this service runs (in a host, across a restart).
    /// </summary>
    List<McpServer> PeersFor(string sessionId)
    {
        var token = sessionTokens.GetOrAdd(sessionId, _ => NewToken());
        peerTokens[token] = sessionId;
        if (store.Get(sessionId) is { } record && record.PeerToken != token)
            store.Update(sessionId, record.Agent, r => r.PeerToken = token);
        return WithPeers([], token);
    }

    /// <summary>
    /// The service's instructions for the peer tools as an ACP <c>systemPrompt</c> in append mode,
    /// for an agent that advertises <c>sessionCapabilities.systemPrompt</c> and a session that gets
    /// the peers server; null otherwise. ACP agents need not keep it, so the service sends it on
    /// every <c>session/new</c>, <c>session/load</c> and <c>session/resume</c>.
    /// </summary>
    SystemPrompt? SystemPromptFor(AgentHost host) =>
        host.TakesSystemPrompt && options.PeerServerCommand is { Count: > 0 }
            ? new SystemPrompt { Mode = SystemPromptMode.Append, Content = [new TextContent { Text = PeersServer.SystemPrompt }] }
            : null;

    /// <summary>
    /// The service does not advertise <c>sessionCapabilities.systemPrompt</c> to its clients yet, so a
    /// client must not send one; it cannot apply one either, which ACP says to answer with an error
    /// instead of opening the session.
    /// </summary>
    static void RefuseSystemPrompt(SystemPrompt? systemPrompt)
    {
        if (systemPrompt is not null)
            throw new JsonRpcException(JsonRpcException.InvalidParams,
                "Gorynych does not take systemPrompt from clients: it does not advertise sessionCapabilities.systemPrompt.");
    }

    /// <summary>Adds the peers MCP server, unless the client already brought one with that name.</summary>
    List<McpServer> WithPeers(List<McpServer>? servers, string token)
    {
        var list = servers is null ? [] : new List<McpServer>(servers);
        if (options.PeerServerCommand is not { Count: > 0 } command) return list;
        if (list.OfType<McpServerStdio>().Any(s => s.Name == PeersServer.ServerName)) return list;
        list.Add(new McpServerStdio
        {
            Name = PeersServer.ServerName,
            Command = command[0],
            Args = [.. command.Skip(1), "--token", token],
            Env = [],
        });
        return list;
    }

    internal JsonObject Status() => new()
    {
        ["serviceId"] = options.ServiceId,
        ["processId"] = Environment.ProcessId,
        ["version"] = AgentHost.Version,
        ["dataDirectory"] = options.DataDirectory,
        ["clients"] = clients.Count,
        ["sessions"] = store.All().Count,
        ["pendingRequests"] = feeds.Values.Sum(f => f.PendingRequests),
        ["restarting"] = draining,
        ["hosts"] = options.Hosts is not null,
        ["build"] = options.Hosts is null ? null : HostGeneration.ThisBuild,
        ["agents"] = new JsonArray(agents.Values.Select(a => (JsonNode)a.Describe(s => !removed.ContainsKey(s) && Feed(s).TurnRunning, WhyNotMoved)).ToArray()),
    };

    static string? FirstText(IEnumerable<ContentBlock> blocks) =>
        blocks.OfType<TextContent>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : Cut(line, 79) + "…";
    }

    /// <summary>At most the first <paramref name="length"/> characters, without splitting a surrogate pair.</summary>
    static string Cut(string text, int length)
    {
        if (text.Length <= length) return text;
        return text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)].TrimEnd();
    }

    static DateTimeOffset? ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    static int DecodeCursor(string? cursor)
    {
        if (cursor is null) return 0;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text[2..], out var n) && n >= 0 ? n : 0;
        }
        catch (FormatException)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid cursor");
        }
    }

    static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes("o:" + offset));

    int disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cleanupStop.Cancel();
        try { await Task.WhenAll(cleanups.Keys).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) { ServiceLog.Warn($"cleanup at exit: {e.Message}"); }
        // Where each session is open, before the instances are let go of.
        var openIn = new Dictionary<string, HostLink>(StringComparer.Ordinal);
        foreach (var host in agents.Values)
            foreach (var link in host.Links.Where(l => l.HostId is not null))
                foreach (var sessionId in host.SessionsOf(link))
                    openIn[sessionId] = link;
        foreach (var host in agents.Values)
            try { await host.DisposeAsync(); }
            catch (Exception e) { ServiceLog.Warn($"{host.Name}: stop failed: {e.Message}"); }
        // What each session took from its host, journaled or not, for the next service: the hosts'
        // connections are closed, so nothing more comes (SessionFeed.HostCursor).
        // For a restart, the turns that go on in the hosts too, so the next one can end those that no
        // host brings back (SettleHostPromptsAsync).
        if (options.Hosts is not null)
            foreach (var feed in feeds.Values)
            {
                if (feed.Retired || store.Get(feed.SessionId) is not { } record) continue;
                var seen = feed.LastTakenFromHost ?? record.HostSeen;
                // Kept before and not settled in this run (no host of the adapter came), and those that
                // reached a host in this run; but none whose end is marked. A shutdown stops the hosts,
                // so its turns end there: those that left no mark are ended by the next service.
                List<string>? left = [.. (record.HostPrompts ?? []).Concat(feed.HandedPromptIds())
                    .Distinct(StringComparer.Ordinal).Where(p => !feed.HasEndMark(p))];
                if (left is { Count: 0 }) left = null;
                // And the host it is open in, which the next service waits for before it opens it anywhere else.
                var hostOf = openIn.TryGetValue(feed.SessionId, out var link) ? link.HostId : left is null ? null : record.HostOf;
                var hostSlot = link is not null ? link.Slot : left is null ? null : record.HostSlot;
                if (record.HostSeen != seen || !SameList(record.HostPrompts, left) || record.HostOf != hostOf || record.HostSlot != hostSlot)
                    store.Update(feed.SessionId, record.Agent, r => { r.HostSeen = seen; r.HostPrompts = left; r.HostOf = hostOf; r.HostSlot = hostSlot; });
            }
        store.Flush();
        shutdown.Dispose();
        cleanupStop.Dispose();
    }
}
