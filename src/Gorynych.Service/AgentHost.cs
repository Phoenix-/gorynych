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

    public static string Version =>
        typeof(AgentHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public async Task<JsonRpcConnection> GetAsync(CancellationToken ct)
    {
        if (connection is { IsClosed: false } running) return running;
        await startGate.WaitAsync(ct);
        try
        {
            if (connection is { IsClosed: false } again) return again;
            ServiceLog.Info($"{Name}: starting");
            var started = await adapter.StartAsync(ct);
            var rpc = new JsonRpcConnection(started.Input, started.Output, Name)
            {
                RequestHandler = (req, token) => conductor.OnAgentRequestAsync(this, req, token),
                NotificationHandler = (method, p) => conductor.OnAgentNotificationAsync(this, method, p),
                Log = m => ServiceLog.Warn(m),
            };
            rpc.Start();
            _ = rpc.Completion.ContinueWith(_ => OnClosed(rpc), TaskScheduler.Default);
            var init = await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
            {
                ProtocolVersion = AcpMethods.ProtocolVersion,
                ClientCapabilities = new ClientCapabilities
                {
                    Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
                    Terminal = false,
                },
                ClientInfo = new Implementation { Name = "gorynych", Title = "Gorynych", Version = Version },
            }, ct);
            ServiceLog.Info($"{Name}: initialized {init.AgentInfo?.Name} {init.AgentInfo?.Version}");
            instance = started;
            Info = init;
            connection = rpc;
            lock (liveGate) live.Clear();
            return rpc;
        }
        finally
        {
            startGate.Release();
        }
    }

    void OnClosed(JsonRpcConnection rpc)
    {
        if (!ReferenceEquals(connection, rpc)) return;
        ServiceLog.Warn($"{Name}: connection closed");
        connection = null;
        lock (liveGate) live.Clear();
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

    public JsonObject Describe() => new()
    {
        ["name"] = Name,
        ["displayName"] = DisplayName,
        ["running"] = IsRunning,
        ["agentInfo"] = Info?.AgentInfo is { } a ? AcpJson.ToNode(a) : null,
    };

    public async ValueTask DisposeAsync()
    {
        var rpc = connection;
        connection = null;
        if (rpc is not null) await rpc.DisposeAsync();
        if (instance is not null) await instance.DisposeAsync();
    }
}
