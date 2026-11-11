using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
    readonly ConcurrentDictionary<ClientConnection, Lazy<Outbox>> outboxes = new();
    readonly ConcurrentDictionary<string, long> unknown = new(StringComparer.Ordinal); // IDs no remote had, and when that was found
    readonly CancellationTokenSource stopping = new();
    readonly SemaphoreSlim applying = new(1, 1);
    string? applied; // the remotes.json content the machines follow
    bool disposed; // under applying
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
    public void Start() => _ = Task.Run(WatchAsync);

    /// <summary>Reads the file now and then for as long as the service runs: nothing in it, or in reading it, ends that.</summary>
    async Task WatchAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await ApplyAsync();
                Prune();
            }
            catch (Exception e) when (!stopping.IsCancellationRequested)
            {
                ServiceLog.Warn($"remotes: {e.GetType().Name}: {e.Message}; the remotes stay as they were, and the file is read again");
            }
            try { await Task.Delay(watchEvery, stopping.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Takes <c>remotes.json</c> as it is now: new remotes connect, removed ones go, changed ones start again.</summary>
    internal async Task ApplyAsync()
    {
        string content;
        try { content = File.Exists(file) ? await ReadSharedAsync(file) : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // being written: the next look takes it
        if (content == applied) return;
        await applying.WaitAsync();
        try
        {
            if (disposed || content == applied) return;
            List<RemoteEntry> entries;
            try { entries = RemotesFile.Read(file); }
            catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
            {
                if (applied is not null || content.Length > 0) ServiceLog.Warn($"remotes: {e.Message}; the remotes stay as they were");
                applied = content;
                return;
            }
            applied = content;
            var going = new List<Task>();
            foreach (var (name, machine) in machines)
            {
                var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (entry is not null && entry.Name == machine.Name && entry.SameLaunch(machine.Entry)) continue;
                machines.TryRemove(name, out _);
                ServiceLog.Info($"remotes: {name} {(entry is null ? "removed" : "changed")}");
                Gone(machine, entry is null ? $"{name} is no longer a remote of this service" : machine.NotConnected);
                going.Add(machine.DisposeAsync().AsTask());
            }
            await Task.WhenAll(going); // in parallel: each may take its link's while to end
            if (going.Count > 0) OnAgentsChanged();
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

    /// <summary>Reads the file without keeping the <c>remote</c> command, or a person's editor, from replacing it meanwhile.</summary>
    static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
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

    string announcedAgents = ""; // the remote agents the clients were told of last

    /// <summary>
    /// The agents a remote offers may have changed (it connected, dropped, went from remotes.json, or came
    /// back as another build): the clients are told the new list, once per change.
    /// </summary>
    internal void OnAgentsChanged()
    {
        var now = string.Join(", ", Agents.Select(a => $"{a.Name}={a.DisplayName}"));
        if (Interlocked.Exchange(ref announcedAgents, now) != now) conductor.AnnounceAgents();
    }

    /// <summary>Whether the session is a remote one this hub knows (from a list, a new session, a change).</summary>
    public bool Owns(string sessionId) => routes.ContainsKey(sessionId);

    /// <summary>Whether the session is a remote one: known, or found now in the lists of the connected remotes.</summary>
    public async ValueTask<bool> HasAsync(string sessionId, CancellationToken ct) =>
        routes.ContainsKey(sessionId) || (!machines.IsEmpty && await FindAsync(sessionId, ct) is not null);

    /// <summary>The agent of a remote session, as this service names it; null for one it does not know.</summary>
    public string? AgentOf(string sessionId) => routes.TryGetValue(sessionId, out var r) ? $"{r.Agent}@{r.Machine}" : null;

    /// <summary>How many of the hub's clients have the remote session open; for tests.</summary>
    internal int ClientsOf(string sessionId) => open.TryGetValue(sessionId, out var os) ? CountOf(os) : 0;

    static int CountOf(OpenSession os)
    {
        lock (os.Gate) return os.Clients.Count;
    }

    /// <summary>Whether the hub follows the remote session at all; for tests.</summary>
    internal bool Follows(string sessionId) => open.ContainsKey(sessionId);

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
            bounded.CancelAfter(options.ListWait);
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A machine that answers but is slow (or hung) holds up no list: its sessions are left out this time.
            ServiceLog.Warn($"remote {machine.Name}: its session/list did not come within {options.ListWait.TotalSeconds:0.#} s; its sessions are left out of this list");
            return (machine, []);
        }
        catch (Exception e) when (e is IOException or JsonRpcException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            ServiceLog.Warn($"remote {machine.Name}: session/list failed: {e.Message}");
        }
        return (machine, sessions);
    }

    /// <summary>Finds which remote has a session it does not know yet (this service restarted, say): from their lists, archived ones too.</summary>
    async Task<Route?> FindAsync(string sessionId, CancellationToken ct)
    {
        if (routes.TryGetValue(sessionId, out var known)) return known;
        // An ID no remote had a moment ago (deleted, a typo) is not looked for in every list again at once.
        if (unknown.TryGetValue(sessionId, out var at) && Environment.TickCount64 - at < options.UnknownFor.TotalMilliseconds) return null;
        await ListAsync(new ListSessionsRequest(), scope: null, includeArchived: true, isLocal: conductor.IsLocalSession, ct);
        if (routes.TryGetValue(sessionId, out var found)) return found;
        unknown[sessionId] = Environment.TickCount64;
        return null;
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
            os.Attach(client, Interlocked.Increment(ref order), position: null, held: true);
        }
        afterAnswer(() =>
        {
            ReleaseHeld(os, client);
            return Task.CompletedTask;
        });
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
        Interlocked.Increment(ref os.Pending); // not dropped while this open waits for one before it, or a close
        try
        {
            await os.Opening.WaitAsync(ct);
        }
        catch
        {
            Interlocked.Decrement(ref os.Pending);
            throw;
        }
        try
        {
            // Where the client is: a load wants all of it, a resume after a position what follows it, a
            // plain resume nothing of the history (only what comes after the answer).
            var from = @params["_meta"]?[GorynychProtocol.MetaReplayFrom];
            var plain = method == AcpMethods.Agent.SessionResume && from is null;
            long? position = method == AcpMethods.Agent.SessionResume && (string?)from?["type"] == GorynychProtocol.ReplayAfterMark
                && long.TryParse((string?)from?["mark"], out var mark) ? mark : null;
            Attachment attachment;
            Attachment? before;
            lock (os.Gate)
            {
                before = os.Clients.TryGetValue(client, out var had) ? had.Copy() : null;
                attachment = os.Attach(client, Interlocked.Increment(ref order), position, held: false, plain: plain);
            }
            JsonObject answer;
            try
            {
                answer = await machine.RequestAsync(method, ForRemote(@params), ct) as JsonObject ?? [];
            }
            catch
            {
                // A client that had it open keeps it as it was; one that came for it lets it go, as one that went.
                if (before is not null)
                    lock (os.Gate) attachment.Restore(before);
                else LetGo(os, client, close: false);
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
            // Its replay first, then its answer, as from a local session.
            await FlushedAsync(client).WaitAsync(ct);
            return WithMachine(answer, machine.Name, agent: null);
        }
        finally
        {
            os.Opening.Release();
            Interlocked.Decrement(ref os.Pending);
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
        var answer = await machine.RequestAsync(AcpMethods.Agent.SessionPrompt, @params.DeepClone(), CancellationToken.None).WaitAsync(ct);
        await FlushedAsync(client).WaitAsync(ct); // the turn's updates before its answer
        return answer;
    }

    /// <summary>Any other request about a remote session (modes, options, delete, archive, read): forwarded, failing at once while its machine is unreachable.</summary>
    public async Task<JsonNode?> ForwardAsync(ClientConnection? client, string method, JsonObject @params, string sessionId, CancellationToken ct)
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
        if (client is not null) await FlushedAsync(client).WaitAsync(ct); // an update it caused (an option's) before its answer
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
        if (outboxes.TryRemove(client, out var outbox) && outbox.IsValueCreated) outbox.Value.Close();
    }

    void LetGo(OpenSession os, ClientConnection client, bool close)
    {
        bool last;
        lock (os.Gate)
        {
            if (!os.Detach(client)) return;
            last = !os.HasClients;
        }
        if (last) _ = Task.Run(() => ReleaseAsync(os, close));
    }

    /// <summary>
    /// Attached only while open: the hub's last client of the remote session let it go, and so does the hub
    /// there. Under <see cref="OpenSession.Opening"/>, after an open on its way and before the next, and its
    /// answer is waited for: the remote never gets a close behind an open that came after it.
    /// </summary>
    async Task ReleaseAsync(OpenSession os, bool close)
    {
        await os.Opening.WaitAsync();
        try
        {
            lock (os.Gate)
            {
                if (os.HasClients || !os.OnRemote) return; // a client came for it meanwhile, or nothing is open there
                os.OnRemote = false;
            }
            if (!machines.TryGetValue(os.Machine, out var machine)) return;
            var method = close ? AcpMethods.Agent.SessionClose : machine.Takes(GorynychProtocol.CapDetach) ? GorynychProtocol.MethodDetach : null;
            if (method is null)
            {
                // An older remote has no detach, and a close would cancel the person's turn there.
                ServiceLog.Info($"remote {machine.Name}: {os.SessionId} stays attached: it takes no {GorynychProtocol.MethodDetach}");
                lock (os.Gate) os.OnRemote = true;
                return;
            }
            try
            {
                await machine.SendAsync(method, new JsonObject { ["sessionId"] = os.SessionId }).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception e) when (e is IOException or JsonRpcException or TimeoutException or OperationCanceledException)
            {
                ServiceLog.Info($"remote {machine.Name}: {method} for {os.SessionId}: {e.Message}");
            }
        }
        finally
        {
            os.Opening.Release();
            Forget(os);
        }
    }

    /// <summary>
    /// Drops what came for sessions no client here took within a minute: a session's updates that came
    /// before its <c>session/new</c> answer, or after the hub let it go.
    /// </summary>
    void Prune()
    {
        var old = Environment.TickCount64 - 60_000;
        foreach (var os in open.Values)
            if (Volatile.Read(ref os.Touched) < old) Forget(os);
    }

    /// <summary>Drops a session nobody has open from what the hub follows.</summary>
    void Forget(OpenSession os)
    {
        lock (os.Gate)
            if (os.HasClients || os.OnRemote || os.Waiting > 0 || Volatile.Read(ref os.Pending) > 0) return;
        open.TryRemove(new KeyValuePair<string, OpenSession>(os.SessionId, os));
    }

    // ---------------------------------------------------------------- from the remote

    /// <summary>A notification from a remote: a session's update, to its clients here; a change of a list, said again under this service's names.</summary>
    internal Task OnRemoteNotificationAsync(RemoteMachine machine, string method, JsonNode? @params)
    {
        // Never waits for a client: each has its outbox, so a slow one holds up nothing from the remote.
        if (method == AcpMethods.Client.SessionUpdate && (string?)@params?["sessionId"] is { } sessionId && @params["update"] is JsonObject update)
            RouteUpdate(machine, sessionId, (JsonObject)@params);
        else if (method == GorynychProtocol.MethodSessionsChanged && @params is JsonObject change)
            ListChanged(machine, change);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether what a machine sends about a session is its owner's: not about a local session's ID, nor
    /// about a session routed to another machine (the same ID on two, as a copied Claude transcript has).
    /// Anything else is dropped, and logged once.
    /// </summary>
    bool FromOwner(RemoteMachine machine, string sessionId)
    {
        var other = conductor.IsLocalSession(sessionId) ? "a local session"
            : routes.TryGetValue(sessionId, out var route) && route.Machine != machine.Name ? $"a session on {route.Machine}"
            : open.TryGetValue(sessionId, out var os) && os.Machine != machine.Name ? $"a session on {os.Machine}"
            : null;
        if (other is null) return true;
        if (collisions.TryAdd($"{machine.Name}/{sessionId}/sent", 0))
            ServiceLog.Warn($"remote {machine.Name}: what it sends about session {sessionId} is dropped: here that ID is {other}'s");
        return false;
    }

    void RouteUpdate(RemoteMachine machine, string sessionId, JsonObject @params)
    {
        if (!FromOwner(machine, sessionId)) return;
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
        lock (os.Gate)
        {
            os.Touch();
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
                else OutboxOf(client).Add((JsonObject)@params.DeepClone());
            }
        }
    }

    void ListChanged(RemoteMachine machine, JsonObject change)
    {
        unknown.Clear(); // a session came or went: what no remote had may be there now
        var meta = change["_meta"] as JsonObject;
        if (meta?[GorynychProtocol.MetaMachine] is not null) return; // one hop
        if ((string?)change["sessionId"] is not { } sessionId || !FromOwner(machine, sessionId)) return;
        var remoteAgent = (string?)meta?[GorynychProtocol.MetaAgent] ?? (routes.TryGetValue(sessionId, out var known) ? known.Agent : null);
        var kind = (string?)change["change"];
        if (kind == "removed") routes.TryRemove(sessionId, out _); // its own: FromOwner said so
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
        if (!FromOwner(machine, sessionId))
            throw new JsonRpcException(JsonRpcException.InvalidRequest, $"The hub has no session {sessionId} of {machine.Name} open: here that ID is another session's.");
        var os = Session(sessionId, machine.Name);
        Interlocked.Increment(ref os.Waiting); // not pruned while a question waits for a client
        try
        {
            return await AskClientAsync(os, request, ct);
        }
        finally
        {
            Interlocked.Decrement(ref os.Waiting);
        }
    }

    async Task<JsonNode?> AskClientAsync(OpenSession os, JsonRpcRequest request, CancellationToken ct)
    {
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
                // What the agent did before it asked (the tool call) reaches the client first.
                await FlushedAsync(client).WaitAsync(ct);
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
            if (was != !up) Tell(os, machine, notice: true);
        }
        OnAgentsChanged();
    }

    /// <summary>
    /// The machine's link is up, its first or one that started over: the sessions the hub's clients have
    /// open there are resumed after the last update the hub took, and what came meanwhile reaches the clients
    /// that lack it, before they learn they may write again.
    /// </summary>
    internal void OnConnected(RemoteMachine machine) => _ = Task.Run(async () =>
    {
        unknown.Clear();
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
            Tell(os, machine, notice: true, reason: why);
        }
    }

    /// <summary>Tells the session's clients here whether they may write to it: the remote's own state, or read-only while its machine is unreachable.</summary>
    void Tell(OpenSession os, RemoteMachine machine, bool notice, string? reason = null)
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
            OutboxOf(client).Add((JsonObject)update.DeepClone());
            if (notice && news && client.TakesNotices)
            {
                var said = state is null
                    ? new Notice { Severity = NoticeSeverity.Info, Title = "You can write here again", Description = $"{machine.Name} is connected again." }
                    : new Notice { Severity = NoticeSeverity.Warning, Title = "Read-only for now", Description = state.Reason };
                OutboxOf(client).Add(new JsonObject { ["sessionId"] = os.SessionId, ["update"] = AcpJson.ToNode<SessionUpdate>(said) });
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    OpenSession Session(string sessionId, string machine) => open.GetOrAdd(sessionId, id => new OpenSession(id, machine));

    /// <summary>A client's updates held for its <c>session/new</c> answer go now, in order, ahead of any that follow.</summary>
    void ReleaseHeld(OpenSession os, ClientConnection client)
    {
        lock (os.Gate)
        {
            if (!os.Clients.TryGetValue(client, out var a) || a.Held is not { } held) return;
            foreach (var update in held) OutboxOf(client).Add((JsonObject)update.DeepClone());
            a.Held = null;
        }
        os.ClientCame();
    }

    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    Outbox OutboxOf(ClientConnection client) => outboxes.GetOrAdd(client, c => new Lazy<Outbox>(() => new Outbox(this, c))).Value;

    /// <summary>Resolves once what was queued for the client so far is sent (or the client is gone).</summary>
    Task FlushedAsync(ClientConnection client) => outboxes.TryGetValue(client, out var outbox) && outbox.IsValueCreated ? outbox.Value.Flushed() : Task.CompletedTask;

    /// <summary>
    /// What goes to one client of the hub about remote sessions, sent in order by a sender of its own: a client
    /// that is slow to read holds up only itself, never a remote's connection or the hub's other clients. One
    /// that does not read for 15 seconds is disconnected, as the service does with its own sessions' updates.
    /// </summary>
    sealed class Outbox
    {
        readonly Channel<(JsonObject? Update, TaskCompletionSource? Sent)> queue =
            Channel.CreateUnbounded<(JsonObject?, TaskCompletionSource?)>(new UnboundedChannelOptions { SingleReader = true });

        public Outbox(RemoteHub hub, ClientConnection client) => _ = Task.Run(() => SendAsync(hub, client));

        public void Add(JsonObject update) => queue.Writer.TryWrite((update, null));

        public Task Flushed()
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.Writer.TryWrite((null, sent))) sent.TrySetResult();
            return sent.Task;
        }

        public void Close() => queue.Writer.TryComplete();

        async Task SendAsync(RemoteHub hub, ClientConnection client)
        {
            var broken = false;
            await foreach (var (update, sent) in queue.Reader.ReadAllAsync())
            {
                if (sent is not null)
                {
                    sent.TrySetResult();
                    continue;
                }
                if (broken || client.Rpc.IsClosed) continue;
                try
                {
                    await client.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, update).WaitAsync(SendTimeout);
                }
                catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
                {
                    broken = true;
                    if (e is TimeoutException)
                    {
                        ServiceLog.Warn($"{client.Rpc.Name}: not reading; disconnecting it");
                        _ = client.Rpc.DisposeAsync().AsTask();
                    }
                    hub.ClientGone(client);
                }
            }
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
        // After a change being applied, and before any other: no machine starts once this has begun.
        await applying.WaitAsync();
        try
        {
            disposed = true;
            var all = machines.Values.ToList();
            machines.Clear();
            await Task.WhenAll(all.Select(m => m.DisposeAsync().AsTask()));
        }
        finally
        {
            applying.Release();
        }
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

        public Attachment Copy() => new() { Position = Position, Plain = Plain, Held = Held, Order = Order };

        /// <summary>Back to what it was before an open that failed.</summary>
        public void Restore(Attachment before)
        {
            Position = before.Position;
            Plain = before.Plain;
            Held = before.Held;
            Order = before.Order;
        }
    }

    /// <summary>A remote session the hub's clients have open, or had, while the hub follows it.</summary>
    sealed class OpenSession(string sessionId, string machine)
    {
        public string SessionId { get; } = sessionId;
        public string Machine { get; } = machine;
        /// <summary>When a client, an update or a question last had to do with it.</summary>
        public long Touched = Environment.TickCount64;

        /// <summary>Questions of its agent waiting for a client: it is not dropped meanwhile.</summary>
        public int Waiting;

        /// <summary>Opens on their way, waiting for <see cref="Opening"/>: it is not dropped meanwhile.</summary>
        public int Pending;

        public void Touch() => Volatile.Write(ref Touched, Environment.TickCount64);
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
            Touch();
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
        public bool Detach(ClientConnection client)
        {
            Touch();
            return Clients.Remove(client);
        }

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
