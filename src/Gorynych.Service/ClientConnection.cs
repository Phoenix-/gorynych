using System.Text.Json.Nodes;
using System.Threading.Channels;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

/// <summary>One connected client (a GUI): the service is the ACP agent on this connection.</summary>
internal sealed class ClientConnection
{
    readonly Conductor conductor;

    public ClientConnection(Conductor conductor, JsonRpcConnection rpc)
    {
        this.conductor = conductor;
        Rpc = rpc;
        rpc.RequestHandler = HandleAsync;
        rpc.NotificationHandler = OnNotificationAsync;
        rpc.Log = m => ServiceLog.Warn(m);
        _ = Task.Run(SendNoticesAsync);
        _ = rpc.Completion.ContinueWith(_ => notices.Writer.TryComplete(), TaskScheduler.Default);
    }

    readonly Channel<(string Method, JsonNode? Params)> notices =
        Channel.CreateUnbounded<(string, JsonNode?)>(new UnboundedChannelOptions { SingleReader = true });

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> cancelledAt = new(StringComparer.Ordinal);

    public JsonRpcConnection Rpc { get; }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> held = new(StringComparer.Ordinal);

    /// <summary>
    /// The sessions this client attached to and has not closed. When it goes away it lets go of
    /// each, also of one it was detached from on the way, because a request or an update to it
    /// failed before the service saw it go.
    /// </summary>
    public ICollection<string> Held => held.Keys;

    public void Hold(string sessionId) => held[sessionId] = 0;

    /// <summary>The client closed the session with <c>session/close</c>.</summary>
    public void Release(string sessionId) => held.TryRemove(sessionId, out _);

    /// <summary>True when the client sent <c>session/cancel</c> for the session after the message at <paramref name="sequence"/>.</summary>
    public bool CancelledAfter(string sessionId, long sequence) => cancelledAt.TryGetValue(sessionId, out var at) && at > sequence;

    /// <summary>The client's <c>initialize</c> params.</summary>
    public InitializeRequest? Init { get; private set; }

    /// <summary>
    /// The agent this connection is scoped to by <c>initialize</c>'s <c>_meta["gorynych/agent"]</c>
    /// (the stdio bridge sets it for plain ACP clients): new sessions use it, lists show only it.
    /// </summary>
    public string? AgentScope => (string?)Init?.Meta?[GorynychProtocol.MetaAgent];

    /// <summary>
    /// The client is on the resumable link (<c>initialize</c> <c>_meta["gorynych/link"]</c>): when it
    /// goes, it may come back and resume its sessions, drafts too.
    /// </summary>
    public bool OnLink => Init?.Meta?[GorynychProtocol.CapLink] is JsonValue v && v.TryGetValue<int>(out var version) && version >= 1;

    /// <summary>Whether this client can take <paramref name="method"/> requests from an agent.</summary>
    public bool Accepts(string method) => method switch
    {
        AcpMethods.Client.ElicitationCreate => Init?.ClientCapabilities?.Elicitation?.Form is not null,
        _ => true,
    };

    async Task<JsonNode?> HandleAsync(JsonRpcRequest request, CancellationToken ct)
    {
        switch (request.Method)
        {
            case AcpMethods.Agent.Initialize:
                Init = request.ParamsAs<InitializeRequest>();
                if (AgentScope is { } scope && !conductor.Agents.ContainsKey(scope))
                    throw new JsonRpcException(JsonRpcException.InvalidParams, $"Unknown agent '{scope}'. Known: {string.Join(", ", conductor.Agents.Keys)}");
                // A client that comes during a restart learns of it as the others did.
                if (conductor.Draining)
                    request.AfterResult(() => { Notify(GorynychProtocol.MethodRestarting, new JsonObject()); return Task.CompletedTask; });
                return AcpJson.ToNode(Initialize());
            case AcpMethods.Agent.Authenticate:
            case AcpMethods.Agent.Logout:
                return new JsonObject();
            case AcpMethods.Agent.SessionList:
                return AcpJson.ToNode(await conductor.ListAsync(request.ParamsAs<ListSessionsRequest>(), ct, AgentScope));
            case AcpMethods.Agent.SessionNew:
                return AcpJson.ToNode(await conductor.NewSessionAsync(this, request.ParamsAs<NewSessionRequest>(), request.AfterResult, ct));
            case AcpMethods.Agent.SessionLoad:
                return await conductor.LoadAsync(this, request.ParamsAs<LoadSessionRequest>(), ct);
            case AcpMethods.Agent.SessionResume:
                return await conductor.ResumeAsync(this, request.ParamsAs<ResumeSessionRequest>(), ct);
            case AcpMethods.Agent.SessionPrompt:
                return await conductor.PromptAsync(this, request, ct);
            case AcpMethods.Agent.SessionClose:
                return await conductor.CloseAsync(this, RequireSession(request), ct);
            case AcpMethods.Agent.SessionDelete:
                return await conductor.DeleteAsync(RequireSession(request), ct);
            case GorynychProtocol.MethodStatus:
                return conductor.Status();
            case GorynychProtocol.MethodOpinion:
                return await conductor.OpinionAsync(request, ct);
            case GorynychProtocol.MethodPeers:
                return await conductor.PeersAsync(request, ct);
            case GorynychProtocol.MethodReply:
                return conductor.Reply(request);
            case GorynychProtocol.MethodSpawn:
                return await conductor.SpawnAsync(request, ct);
            case GorynychProtocol.MethodArchive:
                return await conductor.ArchiveAsync(request, ct);
            case GorynychProtocol.MethodRead:
                return await conductor.ReadMethodAsync(request, ct);
            case GorynychProtocol.MethodMcpMessage:
                return await conductor.Outside.HandleAsync(this, request, ct);
            case GorynychProtocol.MethodShutdown:
                ServiceLog.Info($"{Rpc.Name}: asked the service to exit");
                _ = Task.Delay(200).ContinueWith(_ => conductor.RequestShutdown(), TaskScheduler.Default);
                return new JsonObject();
            case GorynychProtocol.MethodRestart:
                ServiceLog.Info($"{Rpc.Name}: asked the service to restart");
                return await conductor.RestartAsync(request);
            default:
                // Anything else about a session (modes, config options) goes to its agent.
                if ((string?)request.Params?["sessionId"] is { } sid)
                    return await conductor.ForwardAsync(request, sid, ct);
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {request.Method}");
        }
    }

    async Task OnNotificationAsync(string method, JsonNode? @params)
    {
        if ((string?)@params?["sessionId"] is not { } sid) return;
        if (method == AcpMethods.Agent.SessionCancel) cancelledAt[sid] = Rpc.NotificationSequence;
        try { await conductor.ForwardNotificationAsync(method, @params, sid); }
            catch (Exception e) when (e is IOException or JsonRpcException) { ServiceLog.Warn($"{method} for {sid}: {e.Message}"); }
    }

    InitializeResponse Initialize() => new()
    {
        ProtocolVersion = (ushort)Math.Min((int)(Init?.ProtocolVersion ?? AcpMethods.ProtocolVersion), AcpMethods.ProtocolVersion),
        AgentInfo = new Implementation
        {
            Name = "gorynych",
            Title = AgentScope is { } scope && conductor.Agents.TryGetValue(scope, out var host) ? $"Gorynych: {host.DisplayName}" : "Gorynych",
            Version = AgentHost.Version,
        },
        AgentCapabilities = new AgentCapabilities
        {
            LoadSession = true,
            PromptCapabilities = new PromptCapabilities { Image = false, Audio = false, EmbeddedContext = false },
            SessionCapabilities = new SessionCapabilities
            {
                List = new SessionListCapabilities(),
                Resume = new SessionResumeCapabilities(),
                Close = new SessionCloseCapabilities(),
                Delete = new SessionDeleteCapabilities(),
            },
            // ACP's extensibility rules advertise custom capabilities in the capability object's _meta.
            Meta = Extensions(),
        },
        AuthMethods = [],
        Meta = ResultMeta(),
    };

    /// <summary>The service's extension flags, advertised in <c>agentCapabilities._meta</c>.</summary>
    static JsonObject Extensions()
    {
        var flags = DeprecatedFlags();
        flags[GorynychProtocol.CapLink] = GorynychProtocol.LinkVersion;
        return flags;
    }

    /// <summary>The flags that were in the result's <c>_meta</c> before they moved.</summary>
    static JsonObject DeprecatedFlags() => new()
    {
        [GorynychProtocol.MetaSessionsChanged] = true,
        [GorynychProtocol.MetaArchive] = true,
    };

    /// <summary>The <c>initialize</c> result's <c>_meta</c>: data about the service, not flags.</summary>
    JsonObject ResultMeta()
    {
        var meta = new JsonObject
        {
            [GorynychProtocol.MetaServiceId] = conductor.ServiceId,
            [GorynychProtocol.MetaAgents] = new JsonArray(conductor.Agents.Values
                .Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["displayName"] = a.DisplayName }).ToArray()),
        };
        // Deprecated: the flags' old place, kept for one release for clients built before they moved.
        foreach (var (key, value) in DeprecatedFlags()) meta[key] = value?.DeepClone();
        return meta;
    }

    static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Queues a notification that belongs to no session, such as <c>_gorynych/sessions_changed</c>.
    /// Never blocks: each client has its own queue and sender, so a client that is slow or broken
    /// delays and stops only its own notices, which keep their order.
    /// </summary>
    public void Notify(string method, JsonNode? @params) => notices.Writer.TryWrite((method, @params));

    async Task SendNoticesAsync()
    {
        await foreach (var (method, @params) in notices.Reader.ReadAllAsync())
        {
            if (Rpc.IsClosed) continue; // drain without sending
            try
            {
                await Rpc.SendNotificationAsync(method, @params).WaitAsync(NotifyTimeout);
            }
            catch (TimeoutException)
            {
                // A client that stops reading for 15 seconds is disconnected, as for session updates.
                ServiceLog.Warn($"{Rpc.Name}: not reading; disconnecting it");
                _ = Rpc.DisposeAsync().AsTask();
            }
            catch (Exception e)
            {
                if (e is not (IOException or OperationCanceledException or ObjectDisposedException))
                    ServiceLog.Warn($"{Rpc.Name}: {method} not sent: {e.Message}");
            }
        }
    }

    static string RequireSession(JsonRpcRequest request) =>
        (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
}
