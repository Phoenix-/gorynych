using System.Text.Json.Nodes;
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
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> cancelledAt = new(StringComparer.Ordinal);

    public JsonRpcConnection Rpc { get; }

    /// <summary>True when the client sent <c>session/cancel</c> for the session after the message at <paramref name="sequence"/>.</summary>
    public bool CancelledAfter(string sessionId, long sequence) => cancelledAt.TryGetValue(sessionId, out var at) && at > sequence;

    /// <summary>The client's <c>initialize</c> params.</summary>
    public InitializeRequest? Init { get; private set; }

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
                return AcpJson.ToNode(Initialize());
            case AcpMethods.Agent.Authenticate:
            case AcpMethods.Agent.Logout:
                return new JsonObject();
            case AcpMethods.Agent.SessionList:
                return AcpJson.ToNode(await conductor.ListAsync(request.ParamsAs<ListSessionsRequest>(), ct));
            case AcpMethods.Agent.SessionNew:
                return AcpJson.ToNode(await conductor.NewSessionAsync(this, request.ParamsAs<NewSessionRequest>(), ct));
            case AcpMethods.Agent.SessionLoad:
                return await conductor.LoadAsync(this, request.ParamsAs<LoadSessionRequest>(), ct);
            case AcpMethods.Agent.SessionResume:
                return await conductor.ResumeAsync(this, request.ParamsAs<ResumeSessionRequest>(), ct);
            case AcpMethods.Agent.SessionPrompt:
                return await conductor.PromptAsync(this, request, ct);
            case AcpMethods.Agent.SessionClose:
                return await conductor.CloseAsync(this, RequireSession(request), request, ct);
            case GorynychProtocol.MethodStatus:
                return conductor.Status();
            case GorynychProtocol.MethodOpinion:
                return await conductor.OpinionAsync(request, ct);
            case GorynychProtocol.MethodPeers:
                return conductor.Peers(request);
            case GorynychProtocol.MethodReply:
                return conductor.Reply(request);
            case GorynychProtocol.MethodShutdown:
                ServiceLog.Info($"{Rpc.Name}: asked the service to exit");
                _ = Task.Delay(200).ContinueWith(_ => conductor.RequestShutdown(), TaskScheduler.Default);
                return new JsonObject();
            default:
                // Anything else about a session (modes, config options, delete) goes to its agent.
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
        AgentInfo = new Implementation { Name = "gorynych", Title = "Gorynych", Version = AgentHost.Version },
        AgentCapabilities = new AgentCapabilities
        {
            LoadSession = true,
            PromptCapabilities = new PromptCapabilities { Image = false, Audio = false, EmbeddedContext = false },
            SessionCapabilities = new SessionCapabilities
            {
                List = new SessionListCapabilities(),
                Resume = new SessionResumeCapabilities(),
                Close = new SessionCloseCapabilities(),
            },
        },
        AuthMethods = [],
        Meta = new JsonObject
        {
            [GorynychProtocol.MetaServiceId] = conductor.ServiceId,
            [GorynychProtocol.MetaAgents] = new JsonArray(conductor.Agents.Values
                .Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["displayName"] = a.DisplayName }).ToArray()),
        },
    };

    static string RequireSession(JsonRpcRequest request) =>
        (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
}
