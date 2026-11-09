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
            InitializeResponse init;
            try
            {
                init = await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
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
            }, ct);
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

    void OnClosed(JsonRpcConnection rpc)
    {
        if (!ReferenceEquals(connection, rpc)) return;
        ServiceLog.Warn($"{Name}: connection closed");
        connection = null;
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
        ["agentInfo"] = Info?.AgentInfo is { } a ? AcpJson.ToNode(a) : null,
    };

    public async ValueTask DisposeAsync()
    {
        var rpc = Interlocked.Exchange(ref connection, null);
        var running = Interlocked.Exchange(ref instance, null);
        if (rpc is not null) await rpc.DisposeAsync();
        if (running is not null) await running.DisposeAsync();
    }
}
