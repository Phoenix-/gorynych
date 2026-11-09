using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

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
    }

    public string DefaultAgent { get; }

    /// <summary>The service's side of the gorynych-outside MCP server, for agents outside Gorynych.</summary>
    internal OutsideService Outside { get; }
    public string ServiceId => options.ServiceId;
    public IReadOnlyDictionary<string, AgentHost> Agents => agents;
    public SessionStore Store => store;

    /// <summary>Cancelled when a client asks the service to exit.</summary>
    public CancellationToken ShutdownRequested => shutdown.Token;

    public void RequestShutdown() => shutdown.Cancel();

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
            foreach (var sessionId in client.Held) LetGo(sessionId);
        }
    }

    /// <summary>The live side of a session; for one deleted in this run, a retired feed that keeps nothing.</summary>
    internal SessionFeed Feed(string sessionId)
    {
        if (!removed.ContainsKey(sessionId))
            return feeds.GetOrAdd(sessionId, id => new SessionFeed(id, new SessionJournal(JournalPath(id), options.JournalMaxEntries))
            {
                LastClientLeft = () => IdleFromNow(id),
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
        var sessionId = (string?)@params?["sessionId"];
        if (sessionId is null || removed.ContainsKey(sessionId)) return;
        var feed = Feed(sessionId);
        if (method == AcpMethods.Client.SessionUpdate)
        {
            if (@params!["update"] is not JsonObject update) return;
            feed.ApplyStateUpdate(update);
            feed.Active(options.Time.GetUtcNow()); // an agent that sends something is not idle (UnloadIfIdleAsync)
            AgentTurn(feed, update);
            if (feed.SuppressReplay > 0) return; // the agent is replaying history we already journal
            KeepSpawnTitle(sessionId, update);
            if (feed.IsRepeatedTitle(update)) return;
            feed.TagOpinionTurn(update);
            await feed.PublishAsync(update, except: null, () => Remember(host, sessionId, update));
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

    /// <summary>An agent's connection closed: the turns of its own it marked are over.</summary>
    internal void OnAgentStopped(AgentHost host)
    {
        foreach (var feed in feeds.Values)
            if (store.Get(feed.SessionId)?.Agent == host.Name && feed.EndAgentTurn())
            {
                feed.TurnEnded();
                feed.ExitUse();
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
        // A use of the session from the first wait for a client to the answer: while a client may
        // still answer, the session is not idle, so it stays loaded (IdleFromNow). Registered under
        // the session's gate, as UseAsync does, so an unload under way finishes first.
        await feed.LifeGate.WaitAsync(ct);
        try { feed.EnterUse(); }
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
            IdleFromNow(sessionId);
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

    static SessionInfo Describe(SessionRecord r, SessionInfo? native)
    {
        var meta = native?.Meta?.DeepClone().AsObject() ?? [];
        meta[GorynychProtocol.MetaAgent] = r.Agent;
        if (r.ParentSessionId is not null) meta[GorynychProtocol.MetaParentSessionId] = r.ParentSessionId;
        if (r.Archived) meta[GorynychProtocol.MetaArchived] = true;
        return new SessionInfo
        {
            SessionId = r.SessionId,
            Cwd = FolderPath.Normalize(native?.Cwd ?? r.Cwd),
            AdditionalDirectories = native?.AdditionalDirectories,
            Title = r.TitleSource != SpawnTitle && native?.Title is { Length: > 0 } t ? t : r.Title,
            UpdatedAt = r.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            Meta = meta,
        };
    }

    internal async Task<NewSessionResponse> NewSessionAsync(ClientConnection client, NewSessionRequest request, CancellationToken ct)
    {
        // An explicit choice in the request wins; otherwise the agent the connection is scoped to.
        RefuseSystemPrompt(request.SystemPrompt);
        var host = Agent((string?)request.Meta?[GorynychProtocol.MetaAgent] ?? client.AgentScope);
        var rpc = await host.GetAsync(ct);
        var token = NewToken();
        request.McpServers = WithPeers(request.McpServers, token);
        request.SystemPrompt = SystemPromptFor(host);
        var response = await rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, request, ct);
        BindToken(token, response.SessionId);
        host.MarkLive(response.SessionId);
        store.Update(response.SessionId, host.Name, r => { r.Cwd = FolderPath.Normalize(request.Cwd); r.Draft = true; });
        var feed = Feed(response.SessionId);
        feed.CaptureState(AcpJson.ToNode(response));
        feed.Attach(client);
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
    /// Makes sure the agent has the session open in its current instance. Prefers
    /// <c>session/resume</c>; falls back to <c>session/load</c> with the agent's replay
    /// suppressed, since the history comes from the service's own journal. Called under a use of
    /// the session, whose end starts its idle timer (<see cref="IdleFromNow"/>).
    /// </summary>
    internal async Task<(JsonRpcConnection Rpc, JsonObject? Opened)> EnsureLiveAsync(SessionRecord record, CancellationToken ct)
    {
        var host = HostOf(record);
        var rpc = await host.GetAsync(ct);
        if (host.IsLive(record.SessionId)) return (rpc, null);
        var gate = Feed(record.SessionId).OpenGate;
        await gate.WaitAsync(ct);
        try
        {
            rpc = await host.GetAsync(ct);
            if (host.IsLive(record.SessionId)) return (rpc, null);
            var opened = await OpenAsync(host, rpc, record, ct);
            return (rpc, opened);
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<JsonObject?> OpenAsync(AgentHost host, JsonRpcConnection rpc, SessionRecord record, CancellationToken ct)
    {
        JsonNode? opened;
        if (host.CanResume)
        {
            opened = await rpc.SendRequestAsync(AcpMethods.Agent.SessionResume,
                AcpJson.ToNode(new ResumeSessionRequest
                {
                    SessionId = record.SessionId, Cwd = record.Cwd, McpServers = PeersFor(record.SessionId), SystemPrompt = SystemPromptFor(host),
                }), ct);
        }
        else if (host.CanLoad)
        {
            var feed = Feed(record.SessionId);
            Interlocked.Increment(ref feed.SuppressReplay);
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
                Interlocked.Decrement(ref feed.SuppressReplay);
            }
        }
        else
        {
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"{host.Name} cannot reopen session {record.SessionId}");
        }
        host.MarkLive(record.SessionId);
        Feed(record.SessionId).CaptureState(opened);
        return opened as JsonObject;
    }

    internal async Task<JsonNode?> LoadAsync(ClientConnection client, LoadSessionRequest request, CancellationToken ct)
    {
        RefuseSystemPrompt(request.SystemPrompt);
        using var use = await UseAsync(request.SessionId, ct);
        var record = use.Record;
        var host = HostOf(record);
        var feed = use.Feed;
        JsonObject? opened = null;
        if (feed.Journal.Count == 0 && !host.IsLive(record.SessionId))
        {
            // History the service has never seen: let the agent replay it, and journal it.
            var rpc = await host.GetAsync(ct);
            if (host.CanLoad)
            {
                feed.Attach(client);
                opened = await rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
                    AcpJson.ToNode(new LoadSessionRequest
                    {
                        SessionId = record.SessionId, Cwd = record.Cwd, McpServers = PeersFor(record.SessionId), SystemPrompt = SystemPromptFor(host),
                    }), ct) as JsonObject;
                host.MarkLive(record.SessionId);
                feed.CaptureState(opened);
                return opened ?? [];
            }
        }
        JsonObject response;
        try
        {
            (_, opened) = await EnsureLiveAsync(record, ct);
            response = opened ?? feed.State();
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            // The history is still worth showing even if the agent cannot take the session now.
            ServiceLog.Warn($"{host.Name}: cannot reopen {record.SessionId}: {e.Message}");
            response = [];
        }
        await feed.ReplayAndAttachAsync(client);
        return response;
    }

    internal async Task<JsonNode?> ResumeAsync(ClientConnection client, ResumeSessionRequest request, CancellationToken ct)
    {
        RefuseSystemPrompt(request.SystemPrompt);
        using var use = await UseAsync(request.SessionId, ct);
        var (_, opened) = await EnsureLiveAsync(use.Record, ct);
        use.Feed.Attach(client);
        // An already open session gets the state the agent last reported, so every client sees its switches.
        return opened ?? use.Feed.State();
    }

    internal async Task<JsonNode?> PromptAsync(ClientConnection client, JsonRpcRequest request, CancellationToken ct)
    {
        var prompt = request.ParamsAs<PromptRequest>();
        using var use = await UseAsync(prompt.SessionId, ct);
        var record = Promote(use.Record);
        var (rpc, _) = await EnsureLiveAsync(record, ct);
        var feed = use.Feed;
        feed.Attach(client);
        if (client.CancelledAfter(record.SessionId, request.Sequence)) return Cancelled();
        // One turn at a time: an opinion request delivered as a turn waits for this one, and
        // this one for it.
        await feed.TurnGate.WaitAsync(CancellationToken.None);
        feed.ClientTurn = true; // what a client's session/close cancels
        try
        {
            if (feed.Retired) throw NotFound(record.SessionId); // deleted while this prompt waited for the turn before it
            if (record.ParentSessionId is null) return await PromptTurnAsync(client, request, prompt, record, rpc, feed);
            // A spawned session reports the end of the turn to its parent.
            feed.BeginTurnText(ReportLimit + 1);
            JsonNode? result;
            try
            {
                result = await PromptTurnAsync(client, request, prompt, record, rpc, feed);
            }
            catch (Exception e)
            {
                ReportTurnEnd(record.SessionId, $"failed: {e.Message}", feed.EndTurnText());
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
        JsonRpcConnection rpc, SessionFeed feed)
    {
        if (client.CancelledAfter(record.SessionId, request.Sequence)) return Cancelled();
        // Journal the user's message, show it to the session's other clients, and use the
        // first prompt as the title until the harness provides one.
        var messageId = SessionFeed.NewMessageId(); // one message, however many blocks
        foreach (var block in prompt.Prompt)
        {
            var update = AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk { Content = block, MessageId = messageId })!.AsObject();
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
        }
        finally
        {
            feed.ForwardGate.Release();
        }
        return await response;
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
        if (!host.IsRunning) return;
        var rpc = await host.GetAsync(CancellationToken.None);
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
                    var rpc = await host.GetAsync(ct);
                    await CloseInAgentAsync(host, rpc, sessionId, ct);
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
        var rpc = await host.GetAsync(ct);
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

    /// <summary>Closes the session in its agent if it is open there, which stops its turn; an agent without close gets a cancel.</summary>
    static async Task CloseInAgentAsync(AgentHost host, JsonRpcConnection rpc, string sessionId, CancellationToken ct)
    {
        if (!host.IsLive(sessionId)) return;
        try
        {
            if (host.CanClose) await rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId }, ct);
            else await rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId });
        }
        catch (JsonRpcException e)
        {
            ServiceLog.Warn($"{host.Name}: closing {sessionId}: {e.Message}");
        }
        host.MarkClosed(sessionId);
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
    /// client is not held up by its agent.
    /// </summary>
    void LetGo(string sessionId) => RunCleanup(async () =>
    {
        if (store.Get(sessionId) is not { Deleted: false } record) return;
        if (record.Draft)
        {
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

    /// <summary>Deletes the drafts an earlier run left behind (a client quit without closing them). Called once at start.</summary>
    public async Task SweepDraftsAsync()
    {
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
        var rpc = await host.GetAsync(ct);
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
                if ((string?)(await creating)?["sessionId"] is { } late) await AbandonChildAsync(host, late, cwd);
            });
            throw;
        }
        var id = (string?)created?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InternalError, $"{host.Name} created a session without an ID");
        var feed = Feed(id);
        await feed.TurnGate.WaitAsync(CancellationToken.None); // a new session's: free
        SessionRecord record;
        try
        {
            BindToken(token, id);
            host.MarkLive(id);
            feed.CaptureState(created);
            store.Update(id, host.Name, rec =>
            {
                // A list that found the session in its agent just now has shown it already: it stays listed.
                rec.Draft = rec.Cwd.Length == 0;
                rec.Cwd = cwd;
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
            await AbandonChildAsync(host, id, cwd);
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
    async Task AbandonChildAsync(AgentHost host, string id, string cwd)
    {
        var record = new SessionRecord { SessionId = id, Agent = host.Name, Cwd = cwd };
        var gone = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cleanupStop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var rpc = await host.GetAsync(timeout.Token);
            host.MarkLive(id); // a late one was never marked
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
        if (!host.IsRunning || !host.IsLive(sessionId) || !host.CanClose) return;
        var rpc = await host.GetAsync(cleanupStop.Token);
        await CloseInAgentAsync(host, rpc, sessionId, cleanupStop.Token);
        ServiceLog.Info($"{record.Agent} {sessionId}: unloaded from its agent, {why(host)}");
    }

    async Task ReportTurnAsync(SessionUse use, JsonRpcConnection rpc, string sender, string childId, string text)
    {
        var feed = use.Feed;
        await feed.TurnGate.WaitAsync(cleanupStop.Token);
        try
        {
            if (feed.Retired) return;
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
            await response;
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
    /// Queues a notice for every client whose agent scope covers the session. Called under the
    /// store's lock only, which orders the notices; each client sends its own queue
    /// (<see cref="ClientConnection.Notify"/>), so one slow client holds up no other.
    /// </summary>
    void AnnounceList(string sessionId, string agent, string change)
    {
        if (change == "removed") lastUpdateNotice.TryRemove(sessionId, out _);
        else lastUpdateNotice[sessionId] = options.Time.GetTimestamp(); // "added" is news enough for the interval
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
        var rpc = await host.GetAsync(ct);
        if (!host.CanLoad) return;
        await use.Feed.OpenGate.WaitAsync(ct);
        try
        {
            if (host.IsLive(record.SessionId) || use.Feed.Journal.HasTurns) return;
            var opened = await rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, AcpJson.ToNode(new LoadSessionRequest
            {
                SessionId = record.SessionId, Cwd = record.Cwd, McpServers = PeersFor(record.SessionId), SystemPrompt = SystemPromptFor(host),
            }), ct) as JsonObject;
            host.MarkLive(record.SessionId);
            use.Feed.CaptureState(opened);
            ServiceLog.Info($"{host.Name}: loaded {record.SessionId} to read its history");
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
                throw CouldNotStart(record, e, asker);
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
    static JsonRpcException CouldNotStart(SessionRecord record, Exception e, Asker asker)
    {
        // Another app holds it (gorynych/openElsewhere in the error's data): only closing it there helps.
        if (e is JsonRpcException { ErrorData: JsonObject data } held && (string?)data[GorynychProtocol.MetaOpenElsewhere] is { Length: > 0 } holder)
            return new JsonRpcException(held.Code,
                $"{Who(record)} is open in {holder}, which holds it, so it cannot be asked until it is closed there. " +
                $"Ask another session from {(asker.Outside is not null ? "list_sessions" : "list_agents")}, or ask the user to close it in {holder}.",
                data.DeepClone());
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
                    if (holder is JsonValue v && v.TryGetValue<string>(out var name) && name.Length > 0) held[id] = name;
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

    List<McpServer> PeersFor(string sessionId)
    {
        var token = sessionTokens.GetOrAdd(sessionId, _ => NewToken());
        peerTokens[token] = sessionId;
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
        ["agents"] = new JsonArray(agents.Values.Select(a => (JsonNode)a.Describe()).ToArray()),
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
        foreach (var host in agents.Values)
            try { await host.DisposeAsync(); }
            catch (Exception e) { ServiceLog.Warn($"{host.Name}: stop failed: {e.Message}"); }
        store.Flush();
        shutdown.Dispose();
        cleanupStop.Dispose();
    }
}
