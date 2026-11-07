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

    public Conductor(ConductorOptions options, IEnumerable<IAgentAdapter> adapters)
    {
        this.options = options;
        Directory.CreateDirectory(options.DataDirectory);
        journalDirectory = Path.Combine(options.DataDirectory, "journal");
        Directory.CreateDirectory(journalDirectory);
        store = new SessionStore(Path.Combine(options.DataDirectory, "sessions.json"));
        foreach (var a in adapters) agents[a.Name] = new AgentHost(a, this);
        DefaultAgent = options.DefaultAgent ?? agents.Keys.FirstOrDefault() ?? throw new ArgumentException("no adapters");
    }

    public string DefaultAgent { get; }
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
            foreach (var feed in feeds.Values) feed.Detach(client);
            await client.Rpc.DisposeAsync();
            ServiceLog.Info($"{name}: disconnected");
        }
    }

    internal SessionFeed Feed(string sessionId) =>
        feeds.GetOrAdd(sessionId, id => new SessionFeed(id, new SessionJournal(Path.Combine(journalDirectory, SessionJournal.FileNameFor(id)), options.JournalMaxEntries)));

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
        if (sessionId is null) return;
        var feed = Feed(sessionId);
        if (method == AcpMethods.Client.SessionUpdate)
        {
            if (feed.SuppressReplay > 0) return; // the agent is replaying history we already journal
            if (@params!["update"] is not JsonObject update) return;
            if (feed.IsRepeatedTitle(update)) return;
            await feed.PublishAsync(update, except: null, () => Remember(host, sessionId, update));
            return;
        }
        foreach (var c in feed.Clients())
            await feed.SendAsync(c, method, @params!.DeepClone());
    }

    void Remember(AgentHost host, string sessionId, JsonObject update)
    {
        var kind = (string?)update["sessionUpdate"];
        store.Update(sessionId, host.Name, r =>
        {
            r.UpdatedAt = DateTimeOffset.UtcNow;
            if (kind == "session_info_update" && (string?)update["title"] is { Length: > 0 } title)
            {
                r.Title = title;
                r.TitleSource = "agent";
            }
        });
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
        var feed = Feed(sessionId);
        while (true)
        {
            var client = await feed.WaitForClientAsync(c => c.Accepts(request.Method), ct);
            try
            {
                return await client.Rpc.SendRequestAsync(request.Method, request.Params?.DeepClone(), ct);
            }
            catch (Exception e) when (e is IOException or TimeoutException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                feed.Detach(client);
            }
        }
    }

    // ---------------------------------------------------------------- for clients

    internal async Task<ListSessionsResponse> ListAsync(ListSessionsRequest request, CancellationToken ct)
    {
        var found = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
        foreach (var host in agents.Values)
        {
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
                        var record = store.Update(s.SessionId, host.Name, rec =>
                        {
                            var discovered = rec.Cwd.Length == 0;
                            rec.Cwd = s.Cwd;
                            if (s.Title is { Length: > 0 } t) { rec.Title = t; rec.TitleSource = "agent"; }
                            if (ParseTime(s.UpdatedAt) is { } at && (discovered || at > rec.UpdatedAt)) rec.UpdatedAt = at;
                        });
                        found[s.SessionId] = Describe(record, s);
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
            if (found.ContainsKey(r.SessionId)) continue;
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
        return new SessionInfo
        {
            SessionId = r.SessionId,
            Cwd = native?.Cwd ?? r.Cwd,
            AdditionalDirectories = native?.AdditionalDirectories,
            Title = native?.Title is { Length: > 0 } t ? t : r.Title,
            UpdatedAt = r.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            Meta = meta,
        };
    }

    internal async Task<NewSessionResponse> NewSessionAsync(ClientConnection client, NewSessionRequest request, CancellationToken ct)
    {
        var host = Agent((string?)request.Meta?[GorynychProtocol.MetaAgent]);
        var rpc = await host.GetAsync(ct);
        var token = NewToken();
        request.McpServers = WithPeers(request.McpServers, token);
        var response = await rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, request, ct);
        BindToken(token, response.SessionId);
        host.MarkLive(response.SessionId);
        store.Update(response.SessionId, host.Name, r => r.Cwd = request.Cwd);
        Feed(response.SessionId).Attach(client);
        response.Meta ??= [];
        response.Meta[GorynychProtocol.MetaAgent] = host.Name;
        ServiceLog.Info($"{host.Name}: new session {response.SessionId} in {request.Cwd}");
        return response;
    }

    /// <summary>Resolves a session ID the client names, refreshing from the agents if needed.</summary>
    internal async Task<SessionRecord> FindAsync(string sessionId, CancellationToken ct)
    {
        if (store.Get(sessionId) is { } known) return known;
        await ListAsync(new ListSessionsRequest(), ct);
        return store.Get(sessionId) ?? throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Unknown session {sessionId}");
    }

    /// <summary>
    /// Makes sure the agent has the session open in its current instance. Prefers
    /// <c>session/resume</c>; falls back to <c>session/load</c> with the agent's replay
    /// suppressed, since the history comes from the service's own journal.
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
            return (rpc, await OpenAsync(host, rpc, record, ct));
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
                AcpJson.ToNode(new ResumeSessionRequest { SessionId = record.SessionId, Cwd = record.Cwd, McpServers = PeersFor(record.SessionId) }), ct);
        }
        else if (host.CanLoad)
        {
            var feed = Feed(record.SessionId);
            Interlocked.Increment(ref feed.SuppressReplay);
            try
            {
                opened = await rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
                    AcpJson.ToNode(new LoadSessionRequest { SessionId = record.SessionId, Cwd = record.Cwd, McpServers = PeersFor(record.SessionId) }), ct);
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
        return opened as JsonObject;
    }

    internal async Task<JsonNode?> LoadAsync(ClientConnection client, LoadSessionRequest request, CancellationToken ct)
    {
        var record = await FindAsync(request.SessionId, ct);
        var host = HostOf(record);
        var feed = Feed(record.SessionId);
        JsonObject? opened = null;
        if (feed.Journal.Count == 0 && !host.IsLive(record.SessionId))
        {
            // History the service has never seen: let the agent replay it, and journal it.
            var rpc = await host.GetAsync(ct);
            if (host.CanLoad)
            {
                feed.Attach(client);
                opened = await rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
                    AcpJson.ToNode(new LoadSessionRequest { SessionId = record.SessionId, Cwd = record.Cwd, McpServers = PeersFor(record.SessionId) }), ct) as JsonObject;
                host.MarkLive(record.SessionId);
                return opened ?? [];
            }
        }
        try
        {
            (_, opened) = await EnsureLiveAsync(record, ct);
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            // The history is still worth showing even if the agent cannot take the session now.
            ServiceLog.Warn($"{host.Name}: cannot reopen {record.SessionId}: {e.Message}");
        }
        await feed.ReplayAndAttachAsync(client);
        return opened ?? [];
    }

    internal async Task<JsonNode?> ResumeAsync(ClientConnection client, ResumeSessionRequest request, CancellationToken ct)
    {
        var record = await FindAsync(request.SessionId, ct);
        var (_, opened) = await EnsureLiveAsync(record, ct);
        var feed = Feed(record.SessionId);
        feed.Attach(client);
        return opened ?? [];
    }

    internal async Task<JsonNode?> PromptAsync(ClientConnection client, JsonRpcRequest request, CancellationToken ct)
    {
        var prompt = request.ParamsAs<PromptRequest>();
        var record = await FindAsync(prompt.SessionId, ct);
        var (rpc, _) = await EnsureLiveAsync(record, ct);
        var feed = Feed(record.SessionId);
        feed.Attach(client);
        if (client.CancelledAfter(record.SessionId, request.Sequence)) return Cancelled();
        // Journal the user's message, show it to the session's other clients, and use the
        // first prompt as the title until the harness provides one.
        foreach (var block in prompt.Prompt)
        {
            var update = AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk { Content = block })!.AsObject();
            await feed.PublishAsync(update, except: client, () => store.Update(record.SessionId, record.Agent, r => r.UpdatedAt = DateTimeOffset.UtcNow));
        }
        if (record.Title is null && FirstText(prompt.Prompt) is { } text)
        {
            var title = Shorten(text);
            store.Update(record.SessionId, record.Agent, r => { r.Title ??= title; r.TitleSource ??= "prompt"; });
            var info = AcpJson.ToNode<SessionUpdate>(new SessionInfoUpdate { Title = title })!.AsObject();
            if (!feed.IsRepeatedTitle(info)) await feed.PublishAsync(info, except: null, () => { });
        }
        // A turn outlives the client that started it: closing the GUI must not stop the agent.
        // Stopping a turn is an explicit session/cancel, which must reach the agent after the
        // prompt, so both go through the session's forward gate.
        Task<JsonNode?> response;
        await feed.ForwardGate.WaitAsync(CancellationToken.None);
        try
        {
            if (client.CancelledAfter(record.SessionId, request.Sequence)) return Cancelled();
            response = await rpc.StartRequestAsync(request.Method, request.Params?.DeepClone(), CancellationToken.None);
        }
        finally
        {
            feed.ForwardGate.Release();
        }
        return await response;
    }

    static JsonNode? Cancelled() => AcpJson.ToNode(new PromptResponse { StopReason = StopReason.Cancelled });

    internal async Task<JsonNode?> ForwardAsync(JsonRpcRequest request, string sessionId, CancellationToken ct)
    {
        var record = await FindAsync(sessionId, ct);
        var (rpc, _) = await EnsureLiveAsync(record, ct);
        return await rpc.SendRequestAsync(request.Method, request.Params?.DeepClone(), ct);
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

    internal async Task<JsonNode?> CloseAsync(ClientConnection client, string sessionId, JsonRpcRequest request, CancellationToken ct)
    {
        Feed(sessionId).Detach(client);
        var record = await FindAsync(sessionId, ct);
        var host = HostOf(record);
        if (!host.IsRunning || !host.IsLive(sessionId)) return new JsonObject();
        if (!host.CanClose) return new JsonObject();
        var rpc = await host.GetAsync(ct);
        var result = await rpc.SendRequestAsync(request.Method, request.Params?.DeepClone(), ct);
        host.MarkClosed(sessionId);
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
            if (from == target) throw new JsonRpcException(JsonRpcException.InvalidParams, "A session cannot ask itself.");
            sender = $"peer_{store.Get(from)?.Agent ?? "agent"}";
        }
        var record = await FindAsync(target, ct);
        var (rpc, _) = await EnsureLiveAsync(record, ct);
        ServiceLog.Info($"opinion: {from ?? "client"} asks {record.Agent} {target}");
        var forwarded = new JsonObject { ["sessionId"] = target, ["text"] = text, ["sender"] = sender ?? "peer_agent" };
        if (from is not null) forwarded["fromSessionId"] = from;
        try
        {
            return await rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, forwarded, ct);
        }
        catch (JsonRpcException e) when (e.Code == JsonRpcException.MethodNotFound)
        {
            throw new JsonRpcException(JsonRpcException.MethodNotFound, $"{record.Agent} sessions cannot be asked for opinions yet.");
        }
    }

    /// <summary><c>_gorynych/peers</c>: the sessions the session behind <c>token</c> can ask.</summary>
    internal JsonObject Peers(JsonRpcRequest request)
    {
        var token = (string?)request.Params?["token"];
        var self = token is not null && peerTokens.TryGetValue(token, out var sid) ? sid : null;
        var sessions = new JsonArray();
        foreach (var r in store.All().Where(r => r.SessionId != self).OrderByDescending(r => r.UpdatedAt))
        {
            var live = agents.TryGetValue(r.Agent, out var host) && host.IsLive(r.SessionId);
            sessions.Add((JsonNode)new JsonObject
            {
                ["session_id"] = r.SessionId,
                ["agent"] = r.Agent,
                ["title"] = r.Title,
                ["cwd"] = r.Cwd,
                ["open"] = live,
                ["updated_at"] = r.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            });
        }
        return new JsonObject { ["sessions"] = sessions };
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
        return line.Length <= 80 ? line : line[..79].TrimEnd() + "…";
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
        foreach (var host in agents.Values)
            try { await host.DisposeAsync(); }
            catch (Exception e) { ServiceLog.Warn($"{host.Name}: stop failed: {e.Message}"); }
        shutdown.Dispose();
    }
}
