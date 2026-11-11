using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service.Remotes;

/// <summary>
/// The hub (docs/design.md, "Several machines"): the services of other machines, from <c>remotes.json</c>,
/// shown as agents of this one (<c>claude@&lt;machine&gt;</c>). To each remote service the hub is one more
/// client: sessions, journals and approvals stay there, and it keeps no copy. It routes a session's
/// requests by a table (session → machine) it fills from the remotes' lists and from <c>session/new</c>,
/// and passes their updates to its own clients that have the session open, as they come: positions,
/// prompt IDs and the link's marks untouched, so a client on the resumable link resumes a remote session
/// after a restart of the hub as after one of the remote.
/// </summary>
/// <remarks>
/// One connection per machine serves all the hub's clients. A remote session is open there only while
/// one of them has it open; each gets its updates once, by their positions: a second client's load
/// replays the history on that connection, and only that client takes what it lacks. One hop: the hub
/// shows only the sessions and agents a remote has itself, never those it shows from its own remotes.
/// </remarks>
internal sealed class RemoteHub : IAsyncDisposable
{
    readonly Conductor conductor;
    readonly string file;
    readonly RemoteOptions options;
    readonly Func<RemoteEntry, Func<CancellationToken, Task<(Stream Input, Stream Output)>>?>? connectFor;
    readonly TimeSpan watchEvery;
    readonly ConcurrentDictionary<string, RemoteMachine> machines = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Route> routes = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, OpenSession> open = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, byte> collisions = new(StringComparer.Ordinal);
    readonly CancellationTokenSource stopping = new();
    readonly SemaphoreSlim applying = new(1, 1);
    string? applied; // the remotes.json content the machines follow
    long order;

    /// <summary>A remote session's machine and its agent there.</summary>
    sealed record Route(string Machine, string Agent, string? Title = null);

    public RemoteHub(Conductor conductor, string dataDirectory, RemoteOptions? options,
        Func<RemoteEntry, Func<CancellationToken, Task<(Stream Input, Stream Output)>>?>? connectFor, TimeSpan? watchEvery)
    {
        this.conductor = conductor;
        file = RemotesFile.PathIn(dataDirectory);
        this.options = options ?? new RemoteOptions();
        this.connectFor = connectFor;
        this.watchEvery = watchEvery ?? TimeSpan.FromSeconds(2);
    }

    public string OwnServiceId => conductor.ServiceId;

    /// <summary>Reads <c>remotes.json</c>, connects to every remote in it, and follows its changes.</summary>
    public void Start()
    {
        _ = ApplyAsync();
        _ = Task.Run(WatchAsync);
    }

    async Task WatchAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try { await Task.Delay(watchEvery, stopping.Token); }
            catch (OperationCanceledException) { return; }
            await ApplyAsync();
            Prune();
        }
    }

    /// <summary>Takes <c>remotes.json</c> as it is now: new remotes connect, removed ones go, changed ones start again.</summary>
    internal async Task ApplyAsync()
    {
        string content;
        try { content = File.Exists(file) ? await File.ReadAllTextAsync(file) : ""; }
        catch (IOException) { return; } // being written: the next look takes it
        if (content == applied) return;
        await applying.WaitAsync();
        try
        {
            if (content == applied) return;
            List<RemoteEntry> entries;
            try { entries = RemotesFile.Read(file); }
            catch (Exception e) when (e is FormatException or IOException)
            {
                if (applied is not null || content.Length > 0) ServiceLog.Warn($"remotes: {e.Message}; the remotes stay as they were");
                applied = content;
                return;
            }
            applied = content;
            foreach (var (name, machine) in machines)
            {
                var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (entry is not null && entry.Name == machine.Name && entry.SameLaunch(machine.Entry)) continue;
                machines.TryRemove(name, out _);
                ServiceLog.Info($"remotes: {name} {(entry is null ? "removed" : "changed")}");
                await machine.DisposeAsync();
                Gone(machine, entry is null ? $"{name} is no longer a remote of this service" : machine.NotConnected);
            }
            foreach (var entry in entries.Where(e => !machines.ContainsKey(e.Name)))
            {
                var machine = new RemoteMachine(entry, this, connectFor?.Invoke(entry), options);
                machines[entry.Name] = machine;
                ServiceLog.Info($"remotes: connecting to {entry.Name}: {entry.Shown}");
                machine.Start();
            }
        }
        finally
        {
            applying.Release();
        }
    }

    // ---------------------------------------------------------------- names

    /// <summary>The machine and its agent a remote agent's name (<c>claude@R-PC-167</c>) names; null for a local one.</summary>
    public (RemoteMachine Machine, string Agent)? ParseAgent(string? name)
    {
        if (name is null) return null;
        var at = name.LastIndexOf('@');
        if (at < 1 || at == name.Length - 1) return null;
        return machines.TryGetValue(name[(at + 1)..], out var machine) ? (machine, name[..at]) : null;
    }

    /// <summary>Whether a name is a remote agent's: one on a machine in <c>remotes.json</c>, connected or not.</summary>
    public bool IsRemoteAgent(string? name) => name?.Contains('@') == true;

    /// <summary>Whether a connection may be scoped to this name: a configured machine, whose agents may not be known yet.</summary>
    public bool KnowsScope(string name) => ParseAgent(name) is not null;

    /// <summary>The agents of the connected remotes, as this service names them.</summary>
    public IEnumerable<(string Name, string DisplayName)> Agents =>
        machines.Values.Where(m => m.Connected).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(m => m.Agents.Select(a => ($"{a.Name}@{m.Name}", $"{a.DisplayName} on {m.Name}")));

    /// <summary>Whether the session is a remote one this hub knows (from a list, a new session, a change).</summary>
    public bool Owns(string sessionId) => routes.ContainsKey(sessionId);

    /// <summary>Whether the session is a remote one: known, or found now in the lists of the connected remotes.</summary>
    public async ValueTask<bool> HasAsync(string sessionId, CancellationToken ct) =>
        routes.ContainsKey(sessionId) || (!machines.IsEmpty && await FindAsync(sessionId, ct) is not null);

    /// <summary>The agent of a remote session, as this service names it; null for one it does not know.</summary>
    public string? AgentOf(string sessionId) => routes.TryGetValue(sessionId, out var r) ? $"{r.Agent}@{r.Machine}" : null;

    /// <summary>The remotes, for <c>_gorynych/status</c>.</summary>
    public JsonArray Status() => new(machines.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => (JsonNode)new JsonObject
    {
        ["name"] = m.Name,
        ["command"] = m.Entry.Shown,
        ["connected"] = m.Connected,
        ["since"] = m.Since.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["lastError"] = m.LastError,
        ["serviceId"] = m.ServiceId,
        ["agents"] = new JsonArray(m.Agents.Select(a => (JsonNode)new JsonObject { ["name"] = $"{a.Name}@{m.Name}", ["displayName"] = a.DisplayName }).ToArray()),
        ["openSessions"] = open.Values.Count(o => o.Machine == m.Name && o.HasClients),
    }).ToArray());

    // ---------------------------------------------------------------- the list

    /// <summary>
    /// The sessions of the connected remotes a connection with <paramref name="scope"/> sees: each
    /// remote's own (one hop), its agent named <c>&lt;agent&gt;@&lt;machine&gt;</c> and its machine in
    /// <c>gorynych/machine</c>. A session whose ID is a local one's, or another remote's, is left out and logged.
    /// </summary>
    public async Task<List<SessionInfo>> ListAsync(ListSessionsRequest request, string? scope, bool includeArchived, Func<string, bool> isLocal, CancellationToken ct)
    {
        var only = scope is null ? null : ParseAgent(scope);
        if (scope is not null && only is null) return [];
        var lists = await Task.WhenAll(machines.Values.Where(m => m.Connected && (only is null || only.Value.Machine == m))
            .Select(m => ListOfAsync(m, request.Cwd, includeArchived, ct)));
        var found = new List<SessionInfo>();
        foreach (var (machine, sessions) in lists)
            foreach (var s in sessions)
            {
                var route = routes.AddOrUpdate(s.Info.SessionId, _ => new Route(machine.Name, s.Agent, s.Info.Title),
                    (_, had) => had.Machine == machine.Name ? had with { Title = s.Info.Title } : had);
                if (isLocal(s.Info.SessionId) || route.Machine != machine.Name)
                {
                    if (collisions.TryAdd($"{machine.Name}/{s.Info.SessionId}", 0))
                        ServiceLog.Warn($"remote {machine.Name}: session {s.Info.SessionId} has the ID of {(isLocal(s.Info.SessionId) ? "a local session" : $"a session on {route.Machine}")}; that one is kept, this one is not listed");
                    if (route.Machine == machine.Name) routes.TryRemove(s.Info.SessionId, out _);
                    continue;
                }
                if (only is { } o && o.Agent != s.Agent) continue;
                found.Add(s.Info);
            }
        return found;
    }

    async Task<(RemoteMachine Machine, List<(SessionInfo Info, string Agent)> Sessions)> ListOfAsync(RemoteMachine machine, string? cwd, bool includeArchived, CancellationToken ct)
    {
        var sessions = new List<(SessionInfo, string)>();
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(30));
            string? cursor = null;
            for (var page = 0; page < 100; page++)
            {
                var @params = new JsonObject();
                if (cwd is not null) @params["cwd"] = cwd;
                if (cursor is not null) @params["cursor"] = cursor;
                if (includeArchived) @params["_meta"] = new JsonObject { [GorynychProtocol.MetaIncludeArchived] = true };
                var answer = await machine.RequestAsync(AcpMethods.Agent.SessionList, @params, bounded.Token);
                foreach (var node in answer?["sessions"] as JsonArray ?? [])
                {
                    if (node is not JsonObject info || (string?)info["sessionId"] is not { Length: > 0 }) continue;
                    var meta = info["_meta"] as JsonObject;
                    if (meta?[GorynychProtocol.MetaMachine] is not null) continue; // one hop: what the remote shows from its own remotes
                    var agent = (string?)meta?[GorynychProtocol.MetaAgent] ?? machine.Agents.FirstOrDefault().Name ?? "agent";
                    var shown = (JsonObject)info.DeepClone();
                    var shownMeta = shown["_meta"] as JsonObject ?? [];
                    shownMeta[GorynychProtocol.MetaAgent] = $"{agent}@{machine.Name}";
                    shownMeta[GorynychProtocol.MetaMachine] = machine.Name;
                    shown["_meta"] = shownMeta;
                    if (open.TryGetValue((string)info["sessionId"]!, out var os) && os.Unreachable) Conductor.PutInteractivity(shownMeta, os.Flag(machine));
                    if (AcpJson.FromNode<SessionInfo>(shown) is { } parsed) sessions.Add((parsed, agent));
                }
                cursor = (string?)answer?["nextCursor"];
                if (cursor is null) break;
            }
        }
        catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            ServiceLog.Warn($"remote {machine.Name}: session/list failed: {e.Message}");
        }
        return (machine, sessions);
    }

    /// <summary>Finds which remote has a session it does not know yet (this service restarted, say): from their lists, archived ones too.</summary>
    async Task<Route?> FindAsync(string sessionId, CancellationToken ct)
    {
        if (routes.TryGetValue(sessionId, out var known)) return known;
        await ListAsync(new ListSessionsRequest(), scope: null, includeArchived: true, isLocal: conductor.IsLocalSession, ct);
        return routes.TryGetValue(sessionId, out var found) ? found : null;
    }

    async Task<(Route Route, RemoteMachine Machine)> RouteAsync(string sessionId, CancellationToken ct)
    {
        var route = await FindAsync(sessionId, ct) ?? throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Session {sessionId} not found");
        if (!machines.TryGetValue(route.Machine, out var machine))
            throw new JsonRpcException(JsonRpcException.InvalidRequest, $"{route.Machine} is no longer a remote of this service");
        if (!machine.Connected) throw machine.NotConnectedError();
        return (route, machine);
    }

    // ---------------------------------------------------------------- opening, prompting, closing

    /// <summary><c>session/new</c> on a remote agent: the remote creates the session and has the hub attached; so has this client.</summary>
    public async Task<JsonNode?> NewAsync(ClientConnection client, JsonObject @params, string agent, Action<Func<Task>> afterAnswer, CancellationToken ct)
    {
        var (machine, remoteAgent) = ParseAgent(agent) ?? throw new JsonRpcException(JsonRpcException.InvalidParams, $"Unknown agent '{agent}'");
        if (!machine.Connected) throw machine.NotConnectedError();
        var forwarded = ForRemote(@params);
        var meta = forwarded["_meta"] as JsonObject ?? [];
        meta[GorynychProtocol.MetaAgent] = remoteAgent;
        forwarded["_meta"] = meta;
        var answer = await machine.RequestAsync(AcpMethods.Agent.SessionNew, forwarded, ct) as JsonObject
            ?? throw new JsonRpcException(JsonRpcException.InternalError, $"{machine.Name} answered session/new with nothing");
        var sessionId = (string?)answer["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InternalError, $"{machine.Name} gave no sessionId");
        routes[sessionId] = new Route(machine.Name, remoteAgent);
        var os = Session(sessionId, machine.Name);
        lock (os.Gate)
        {
            os.OnRemote = true;
            // Its updates wait for its answer: a client knows the session only from that (Zed drops them until then).
            os.Attach(client, ++order, position: null, held: true);
        }
        afterAnswer(() => ReleaseHeldAsync(os, client));
        return WithMachine(answer, machine.Name, $"{remoteAgent}@{machine.Name}");
    }

    /// <summary>
    /// <c>session/load</c> or <c>session/resume</c> of a remote session: forwarded, and the client takes its
    /// updates from now on, those it lacks of the replay (by position) included. The answer passes through,
    /// with the session's machine.
    /// </summary>
    public async Task<JsonNode?> OpenAsync(ClientConnection client, string method, JsonObject @params, CancellationToken ct)
    {
        var sessionId = (string?)@params["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
        var (_, machine) = await RouteAsync(sessionId, ct);
        var os = Session(sessionId, machine.Name);
        await os.Opening.WaitAsync(ct);
        try
        {
            // Where the client is: a load wants all of it, a resume after a position what follows it, a
            // plain resume nothing of the history (only what comes after the answer).
            var from = @params["_meta"]?[GorynychProtocol.MetaReplayFrom];
            var plain = method == AcpMethods.Agent.SessionResume && from is null;
            long? position = method == AcpMethods.Agent.SessionResume && (string?)from?["type"] == GorynychProtocol.ReplayAfterMark
                && long.TryParse((string?)from?["mark"], out var mark) ? mark : null;
            Attachment attachment;
            lock (os.Gate) attachment = os.Attach(client, ++order, position, held: false, plain: plain);
            JsonObject answer;
            try
            {
                answer = await machine.RequestAsync(method, ForRemote(@params), ct) as JsonObject ?? [];
            }
            catch
            {
                lock (os.Gate) os.Detach(client);
                Forget(os);
                throw;
            }
            var meta = answer["_meta"] as JsonObject;
            lock (os.Gate)
            {
                os.OnRemote = true;
                os.RemoteState = Conductor.ReadOnlyOf(meta);
                attachment.Plain = false;
                if (long.TryParse((string?)meta?[GorynychProtocol.MetaPosition], out var at) && (attachment.Position is not { } had || at > had)) attachment.Position = at;
            }
            os.ClientCame();
            return WithMachine(answer, machine.Name, agent: null);
        }
        finally
        {
            os.Opening.Release();
        }
    }

    /// <summary>
    /// <c>session/prompt</c>, as it is: its prompt ID stays the client's, so the marks of its end that the
    /// remote journals reach the client, whose link resumes it so after a drop of this service. A client
    /// that goes does not cancel the turn, as with a local session.
    /// </summary>
    public async Task<JsonNode?> PromptAsync(ClientConnection client, JsonObject @params, CancellationToken ct)
    {
        var sessionId = (string?)@params["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
        var (_, machine) = await RouteAsync(sessionId, ct);
        if (!open.TryGetValue(sessionId, out var os) || !os.Has(client))
            await OpenAsync(client, AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = sessionId, ["cwd"] = @params["cwd"]?.DeepClone() ?? "", ["mcpServers"] = new JsonArray() }, ct);
        return await machine.RequestAsync(AcpMethods.Agent.SessionPrompt, @params.DeepClone(), CancellationToken.None).WaitAsync(ct);
    }

    /// <summary>Any other request about a remote session (modes, options, delete, archive, read): forwarded, failing at once while its machine is unreachable.</summary>
    public async Task<JsonNode?> ForwardAsync(string method, JsonObject @params, string sessionId, CancellationToken ct)
    {
        var (_, machine) = await RouteAsync(sessionId, ct);
        if (method == GorynychProtocol.MethodArchive && !machine.Takes(GorynychProtocol.MetaArchive))
            throw new JsonRpcException(JsonRpcException.MethodNotFound, $"{machine.Name} keeps no archive flag (an older Gorynych)");
        var answer = await machine.RequestAsync(method, @params.DeepClone(), ct);
        if (method == AcpMethods.Agent.SessionDelete)
        {
            routes.TryRemove(sessionId, out _);
            if (open.TryRemove(sessionId, out var gone)) gone.Close();
        }
        return answer;
    }

    /// <summary>
    /// A page of a remote session's history (the outside server's <c>read_session</c>), from its service's
    /// journal (<c>_gorynych/read</c>); null for a session that is not a remote one.
    /// </summary>
    public async Task<Conductor.SessionRead?> ReadAsync(string sessionId, string? cursor, int limit, bool thoughts, int? budget, CancellationToken ct)
    {
        if (conductor.IsLocalSession(sessionId) || !await HasAsync(sessionId, ct)) return null;
        var (route, machine) = await RouteAsync(sessionId, ct);
        var @params = new JsonObject { ["sessionId"] = sessionId, ["limit"] = limit, ["thoughts"] = thoughts };
        if (cursor is not null) @params["cursor"] = cursor;
        if (budget is not null) @params["budget"] = budget;
        var answer = await machine.RequestAsync(GorynychProtocol.MethodRead, @params, ct);
        var updates = (answer?["updates"] as JsonArray ?? []).OfType<JsonObject>().Select(u => (JsonObject)u.DeepClone()).ToList();
        var page = new SessionReader.Page(updates, (string?)answer?["nextCursor"], (int?)answer?["total"] ?? updates.Count, (int?)answer?["first"] ?? 0, (int?)answer?["count"] ?? 0);
        var record = new SessionRecord { SessionId = sessionId, Agent = $"{route.Agent}@{route.Machine}", Title = route.Title };
        return new Conductor.SessionRead(record, Live: false, page, answer?["inProgress"] is JsonValue p && p.TryGetValue<bool>(out var running) && running,
            answer?["trimmed"] is JsonValue t && t.TryGetValue<bool>(out var trimmed) && trimmed);
    }

    /// <summary>A notification about a remote session (<c>session/cancel</c>): the link sends it once the machine is back, if it is not now.</summary>
    public Task ForwardNotificationAsync(string method, JsonNode? @params, string sessionId) =>
        routes.TryGetValue(sessionId, out var route) && machines.TryGetValue(route.Machine, out var machine)
            ? machine.NotifyAsync(method, @params?.DeepClone())
            : Task.CompletedTask;

    /// <summary><c>session/close</c>: the client lets the session go; the last one closes it on the remote, as ACP's close does there.</summary>
    public JsonNode? Close(ClientConnection client, string sessionId)
    {
        if (open.TryGetValue(sessionId, out var os)) LetGo(os, client, close: true);
        return new JsonObject();
    }

    /// <summary><c>_gorynych/detach</c> of a remote session: the client lets it go without cancelling anything.</summary>
    public JsonNode? Detach(ClientConnection client, string sessionId)
    {
        if (open.TryGetValue(sessionId, out var os)) LetGo(os, client, close: false);
        return new JsonObject();
    }

    /// <summary>A client went: it lets go of its remote sessions without cancelling anything, as of a local one.</summary>
    public void ClientGone(ClientConnection client)
    {
        foreach (var os in open.Values)
            if (os.Has(client)) LetGo(os, client, close: false);
    }

    void LetGo(OpenSession os, ClientConnection client, bool close)
    {
        bool last;
        lock (os.Gate)
        {
            if (!os.Detach(client)) return;
            last = !os.HasClients && os.OnRemote;
            if (last) os.OnRemote = false;
        }
        if (last && machines.TryGetValue(os.Machine, out var machine))
        {
            // Attached only while open: the remote session goes when the hub's last client of it does.
            var @params = new JsonObject { ["sessionId"] = os.SessionId };
            if (close) machine.Post(AcpMethods.Agent.SessionClose, @params);
            else if (machine.Takes(GorynychProtocol.CapDetach)) machine.Post(GorynychProtocol.MethodDetach, @params);
            else
            {
                // An older remote has no detach, and a close would cancel the person's turn there.
                ServiceLog.Info($"remote {machine.Name}: {os.SessionId} stays attached: it takes no {GorynychProtocol.MethodDetach}");
                lock (os.Gate) os.OnRemote = true;
                return;
            }
        }
        Forget(os);
    }

    /// <summary>
    /// Drops what came for sessions no client here took within a minute: a session's updates that came
    /// before its <c>session/new</c> answer, or after the hub let it go.
    /// </summary>
    void Prune()
    {
        var old = Environment.TickCount64 - 60_000;
        foreach (var os in open.Values)
            if (os.Born < old) Forget(os);
    }

    /// <summary>Drops a session nobody has open from what the hub follows.</summary>
    void Forget(OpenSession os)
    {
        lock (os.Gate)
            if (os.HasClients || os.OnRemote) return;
        open.TryRemove(new KeyValuePair<string, OpenSession>(os.SessionId, os));
    }

    // ---------------------------------------------------------------- from the remote

    /// <summary>A notification from a remote: a session's update, to its clients here; a change of a list, said again under this service's names.</summary>
    internal async Task OnRemoteNotificationAsync(RemoteMachine machine, string method, JsonNode? @params)
    {
        if (method == AcpMethods.Client.SessionUpdate && (string?)@params?["sessionId"] is { } sessionId && @params["update"] is JsonObject update)
            await RouteUpdateAsync(machine, sessionId, (JsonObject)@params);
        else if (method == GorynychProtocol.MethodSessionsChanged && @params is JsonObject change)
            ListChanged(machine, change);
    }

    async Task RouteUpdateAsync(RemoteMachine machine, string sessionId, JsonObject @params)
    {
        var update = (JsonObject)@params["update"]!;
        var meta = update["_meta"] as JsonObject;
        var kind = (string?)update["sessionUpdate"];
        if (kind == "session_info_update")
        {
            // Its machine goes with each change of a session's state, as with its title.
            meta ??= [];
            meta[GorynychProtocol.MetaMachine] = machine.Name;
            update["_meta"] = meta;
        }
        long? position = long.TryParse((string?)meta?[GorynychProtocol.MetaPosition], out var p) ? p : null;
        var os = Session(sessionId, machine.Name);
        List<ClientConnection> to = [];
        lock (os.Gate)
        {
            if (position is { } at && (os.Seen is not { } seen || at > seen)) os.Seen = at;
            if (kind == "session_info_update" && meta?[GorynychProtocol.MetaInteractivity] is not null) os.RemoteState = Conductor.ReadOnlyOf(meta);
            if (!os.HasClients)
            {
                os.Orphan(@params);
                return;
            }
            foreach (var (client, a) in os.Clients)
            {
                if (kind == "notice" && !client.TakesNotices) continue;
                if (position is { } at2)
                {
                    if (a.Plain || (a.Position is { } has && at2 <= has)) continue; // it has it, or wants none of the history
                    a.Position = at2;
                }
                if (a.Held is { } held) held.Add(@params);
                else to.Add(client);
            }
        }
        foreach (var client in to) await SendAsync(os, client, AcpMethods.Client.SessionUpdate, @params);
    }

    void ListChanged(RemoteMachine machine, JsonObject change)
    {
        var meta = change["_meta"] as JsonObject;
        if (meta?[GorynychProtocol.MetaMachine] is not null) return; // one hop
        if ((string?)change["sessionId"] is not { } sessionId || conductor.IsLocalSession(sessionId)) return;
        var remoteAgent = (string?)meta?[GorynychProtocol.MetaAgent] ?? (routes.TryGetValue(sessionId, out var known) ? known.Agent : null);
        var kind = (string?)change["change"];
        if (kind == "removed") routes.TryRemove(sessionId, out _);
        else if (remoteAgent is not null) routes.TryAdd(sessionId, new Route(machine.Name, remoteAgent));
        var agent = remoteAgent is null ? null : $"{remoteAgent}@{machine.Name}";
        var said = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["change"] = kind,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = agent, [GorynychProtocol.MetaMachine] = machine.Name },
        };
        conductor.AnnounceRemote(said, agent);
    }

    /// <summary>
    /// A question of a remote session's agent (an approval, a form): to the client here that attached to the
    /// session last, translated for one without forms; while none has it open, it waits for one. The remote
    /// withdraws it (<c>$/cancel_request</c>), and so this request, when the hub lets the session go.
    /// </summary>
    internal async Task<JsonNode?> OnRemoteRequestAsync(RemoteMachine machine, JsonRpcRequest request, CancellationToken ct)
    {
        var sessionId = (string?)request.Params?["sessionId"]
            ?? throw new JsonRpcException(JsonRpcException.MethodNotFound, $"{request.Method} is not supported by the Gorynych hub");
        var os = Session(sessionId, machine.Name);
        while (true)
        {
            ClientConnection? client;
            Task came;
            lock (os.Gate)
            {
                client = os.Clients.Where(c => c.Value.Held is null && !c.Value.Plain && (c.Key.Accepts(request.Method) || request.Method == AcpMethods.Client.ElicitationCreate))
                    .OrderByDescending(c => c.Key.Accepts(request.Method)).ThenByDescending(c => c.Value.Order).Select(c => c.Key).FirstOrDefault();
                came = os.NextClient;
            }
            if (client is null)
            {
                await came.WaitAsync(ct);
                continue;
            }
            try
            {
                if (request.Method == AcpMethods.Client.ElicitationCreate && !client.Accepts(request.Method))
                    return await FormFallback.AskAsync(client, request, ct);
                return await client.Rpc.SendRequestAsync(request.Method, request.Params?.DeepClone(), ct);
            }
            catch (Exception e) when (e is IOException or TimeoutException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                LetGo(os, client, close: false);
            }
        }
    }

    /// <summary>The link to a remote dropped, or came back with the hub's sessions resumed: their clients learn it.</summary>
    internal void OnConnectionChanged(RemoteMachine machine, bool up)
    {
        foreach (var os in open.Values.Where(o => o.Machine == machine.Name))
        {
            bool was;
            lock (os.Gate)
            {
                was = os.Unreachable;
                os.Unreachable = !up;
            }
            if (was != !up) _ = TellAsync(os, machine, notice: true);
        }
    }

    /// <summary>
    /// The machine's link is up, its first or one that started over: the sessions the hub's clients have
    /// open there are resumed after the last update the hub took, and what came meanwhile reaches the clients
    /// that lack it, before they learn they may write again.
    /// </summary>
    internal void OnConnected(RemoteMachine machine) => _ = Task.Run(async () =>
    {
        foreach (var os in open.Values.Where(o => o.Machine == machine.Name))
        {
            long? seen;
            lock (os.Gate)
            {
                if (!os.HasClients) continue;
                seen = os.Seen;
            }
            try
            {
                var from = seen is { } at
                    ? new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = at.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    : new JsonObject { ["type"] = GorynychProtocol.ReplayFromStart };
                var answer = await machine.RequestAsync(AcpMethods.Agent.SessionResume, new JsonObject
                {
                    ["sessionId"] = os.SessionId, ["cwd"] = "", ["mcpServers"] = new JsonArray(), ["_meta"] = new JsonObject { [GorynychProtocol.MetaReplayFrom] = from },
                }, stopping.Token);
                lock (os.Gate)
                {
                    os.OnRemote = true;
                    os.RemoteState = Conductor.ReadOnlyOf(answer?["_meta"]);
                }
            }
            catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException)
            {
                ServiceLog.Warn($"remote {machine.Name}: {os.SessionId} not resumed: {e.Message}");
            }
        }
        OnConnectionChanged(machine, up: true);
    });

    /// <summary>A machine that went from <c>remotes.json</c> (or changed): its sessions here are read-only, with why.</summary>
    void Gone(RemoteMachine machine, string why)
    {
        foreach (var os in open.Values.Where(o => o.Machine == machine.Name))
        {
            lock (os.Gate) os.Unreachable = true;
            _ = TellAsync(os, machine, notice: true, reason: why);
        }
    }

    /// <summary>Tells the session's clients here whether they may write to it: the remote's own state, or read-only while its machine is unreachable.</summary>
    async Task TellAsync(OpenSession os, RemoteMachine machine, bool notice, string? reason = null)
    {
        SessionFeed.ReadOnlyState? state;
        List<ClientConnection> clients;
        lock (os.Gate)
        {
            state = os.Unreachable ? new SessionFeed.ReadOnlyState(reason ?? machine.NotConnected, null) : os.RemoteState;
            clients = os.Clients.Where(c => c.Value.Held is null).Select(c => c.Key).ToList();
        }
        var meta = Conductor.PutInteractivity(new JsonObject(), state);
        meta[GorynychProtocol.MetaMachine] = machine.Name;
        var update = new JsonObject { ["sessionId"] = os.SessionId, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = meta } };
        foreach (var client in clients)
        {
            var news = client.Tell(os.SessionId, state);
            await SendAsync(os, client, AcpMethods.Client.SessionUpdate, update);
            if (notice && news && client.TakesNotices)
            {
                var said = state is null
                    ? new Notice { Severity = NoticeSeverity.Info, Title = "You can write here again", Description = $"{machine.Name} is connected again." }
                    : new Notice { Severity = NoticeSeverity.Warning, Title = "Read-only for now", Description = state.Reason };
                await SendAsync(os, client, AcpMethods.Client.SessionUpdate,
                    new JsonObject { ["sessionId"] = os.SessionId, ["update"] = AcpJson.ToNode<SessionUpdate>(said) });
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    OpenSession Session(string sessionId, string machine) => open.GetOrAdd(sessionId, id => new OpenSession(id, machine));

    /// <summary>A client's updates held for its <c>session/new</c> answer go now, in order.</summary>
    async Task ReleaseHeldAsync(OpenSession os, ClientConnection client)
    {
        while (true)
        {
            List<JsonObject>? held;
            lock (os.Gate)
            {
                if (!os.Clients.TryGetValue(client, out var a) || a.Held is not { } list) return;
                if (list.Count == 0)
                {
                    a.Held = null;
                    break;
                }
                held = [.. list];
                list.Clear();
            }
            foreach (var update in held) await SendAsync(os, client, AcpMethods.Client.SessionUpdate, update);
        }
        os.ClientCame();
    }

    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>To one client; one that does not read for 15 seconds, or is gone, lets the session go.</summary>
    async Task SendAsync(OpenSession os, ClientConnection client, string method, JsonNode @params)
    {
        try
        {
            await client.Rpc.SendNotificationAsync(method, @params.DeepClone()).WaitAsync(SendTimeout);
        }
        catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
            LetGo(os, client, close: false);
        }
    }

    /// <summary>A client's request as the remote takes it: its MCP servers run where the client is, not there.</summary>
    static JsonObject ForRemote(JsonObject @params)
    {
        var forwarded = (JsonObject)@params.DeepClone();
        if (forwarded.ContainsKey("mcpServers")) forwarded["mcpServers"] = new JsonArray();
        return forwarded;
    }

    static JsonObject WithMachine(JsonObject answer, string machine, string? agent)
    {
        var meta = answer["_meta"] as JsonObject ?? [];
        meta[GorynychProtocol.MetaMachine] = machine;
        if (agent is not null) meta[GorynychProtocol.MetaAgent] = agent;
        answer["_meta"] = meta;
        return answer;
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        foreach (var machine in machines.Values) await machine.DisposeAsync();
    }

    /// <summary>A client of the hub attached to a remote session.</summary>
    sealed class Attachment
    {
        /// <summary>The position of the last update it has; null for none yet.</summary>
        public long? Position;

        /// <summary>A plain resume on its way: it takes none of the history the remote may send meanwhile.</summary>
        public bool Plain;

        /// <summary>Its updates wait for the answer that tells it of the session (<c>session/new</c>).</summary>
        public List<JsonObject>? Held;

        public long Order;
    }

    /// <summary>A remote session the hub's clients have open, or had, while the hub follows it.</summary>
    sealed class OpenSession(string sessionId, string machine)
    {
        public string SessionId { get; } = sessionId;
        public string Machine { get; } = machine;
        public readonly long Born = Environment.TickCount64;
        public readonly Lock Gate = new();
        public readonly SemaphoreSlim Opening = new(1, 1);
        public readonly Dictionary<ClientConnection, Attachment> Clients = [];
        readonly List<JsonObject> orphans = [];
        TaskCompletionSource came = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The hub has it open on the remote.</summary>
        public bool OnRemote;

        /// <summary>The position of the last update the hub took for it.</summary>
        public long? Seen;

        /// <summary>What the remote says of its interactivity; null for full.</summary>
        public SessionFeed.ReadOnlyState? RemoteState;

        /// <summary>Its machine cannot be reached now.</summary>
        public bool Unreachable;

        public bool HasClients => Clients.Count > 0;

        public bool Has(ClientConnection client)
        {
            lock (Gate) return Clients.ContainsKey(client);
        }

        /// <summary>The flag a list shows while its machine is unreachable.</summary>
        public SessionFeed.ReadOnlyState Flag(RemoteMachine machine) => new(machine.NotConnected, null);

        /// <summary>Under <see cref="Gate"/>.</summary>
        public Attachment Attach(ClientConnection client, long order, long? position, bool held, bool plain = false)
        {
            var a = Clients.TryGetValue(client, out var had) ? had : Clients[client] = new Attachment();
            a.Order = order;
            a.Plain = plain;
            if (had is null || position is null) a.Position = position;
            else if (a.Position is not { } p || position > p) a.Position = position;
            if (held)
            {
                a.Held = [.. orphans];
                orphans.Clear();
            }
            return a;
        }

        /// <summary>Under <see cref="Gate"/>; false when the client did not have it.</summary>
        public bool Detach(ClientConnection client) => Clients.Remove(client);

        /// <summary>Under <see cref="Gate"/>: an update that came before any client had the session (a new one's first).</summary>
        public void Orphan(JsonObject update)
        {
            if (orphans.Count < 500) orphans.Add(update);
        }

        /// <summary>Resolves when a client attaches next.</summary>
        public Task NextClient => came.Task;

        public void ClientCame()
        {
            TaskCompletionSource was;
            lock (Gate)
            {
                was = came;
                came = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            was.TrySetResult();
        }

        public void Close()
        {
            lock (Gate) Clients.Clear();
        }
    }
}
