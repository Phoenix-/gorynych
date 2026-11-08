using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Adapters.Claude;

public sealed class ClaudeAdapterOptions
{
    /// <summary>Where the adapter keeps per-session launch files and its session list.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>The <c>claude</c> executable; found on PATH when null.</summary>
    public string? Executable { get; init; }

    /// <summary>Passed as <c>--model</c> when set (for example <c>haiku</c>).</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Passed as <c>--permission-mode</c>. <c>default</c> makes Claude ask, which the
    /// PermissionRequest hook turns into an ACP approval; a global <c>defaultMode: auto</c>
    /// would otherwise decide by itself.
    /// </summary>
    public string PermissionMode { get; init; } = "default";

    /// <summary>The command that runs the channel server, or null to start Claude without a channel.</summary>
    public IReadOnlyList<string>? ChannelServerCommand { get; init; }

    /// <summary>
    /// Whether Claude may be started with development channels, which makes Claude Code show a
    /// notice the adapter confirms on each launch. <see cref="ChannelConsent.Ask"/> asks the person
    /// once and keeps the answer in <c>channels.json</c> of <see cref="DataDirectory"/>.
    /// </summary>
    public ChannelConsent Channels { get; init; } = ChannelConsent.Ask;

    public int StartupTimeoutSeconds { get; init; } = 90;
    public int PasteEnterDelayMs { get; init; } = 400;

    /// <summary>Keeps the raw terminal output of each launch in the session folder, for diagnosis.</summary>
    public bool KeepTerminalLog { get; init; } = true;

    public Action<string>? Log { get; init; }
}

/// <summary>
/// The ACP agent face of the Claude adapter. Runs in the service process and talks ACP over
/// an in-memory link, so moving it into its own process later means only wrapping it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ClaudeAgent : IAsyncDisposable
{
    readonly ClaudeAdapterOptions options;
    readonly JsonRpcConnection rpc;
    readonly ClaudeStateStore state;
    readonly ConcurrentDictionary<string, ClaudeSession> sessions = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, long> cancelledAt = new(StringComparer.Ordinal);
    readonly ChannelConsentStore consent;
    readonly SemaphoreSlim consentGate = new(1, 1);
    HookServer? hooks;

    public ClaudeAgent(ClaudeAdapterOptions options, JsonRpcConnection rpc)
    {
        this.options = options;
        this.rpc = rpc;
        state = new ClaudeStateStore(Path.Combine(options.DataDirectory, "sessions.json"), options.Log);
        consent = new ChannelConsentStore(Path.Combine(options.DataDirectory, "channels.json"), options.Log);
        Channel = options.ChannelServerCommand is { Count: > 0 } command ? new ChannelHost(command) : null;
        rpc.RequestHandler = HandleAsync;
        rpc.NotificationHandler = OnNotificationAsync;
        rpc.Log = Log;
    }

    public ChannelHost? Channel { get; }

    public async Task StartAsync()
    {
        hooks = await HookServer.StartAsync(OnHttpAsync);
        if (Channel is not null) Channel.BaseUrl = hooks.BaseUrl;
        Log($"claude adapter: hooks at {hooks.BaseUrl}");
        rpc.Start();
    }

    public string HookUrl(string sessionId, string token) => $"{hooks!.BaseUrl}/hook/{sessionId}/{token}";

    public void Log(string message) => options.Log?.Invoke(message);

    // ---------------------------------------------------------------- ACP

    async Task<JsonNode?> HandleAsync(JsonRpcRequest request, CancellationToken ct)
    {
        switch (request.Method)
        {
            case AcpMethods.Agent.Initialize:
                ClientHasForms = request.ParamsAs<InitializeRequest>().ClientCapabilities?.Elicitation?.Form is not null;
                return AcpJson.ToNode(new InitializeResponse
                {
                    ProtocolVersion = AcpMethods.ProtocolVersion,
                    AgentInfo = new Implementation { Name = "claude-code", Title = "Claude Code (interactive)", Version = "gorynych-adapter" },
                    AgentCapabilities = new AgentCapabilities
                    {
                        LoadSession = false,
                        PromptCapabilities = new PromptCapabilities { Image = false, Audio = false, EmbeddedContext = false },
                        SessionCapabilities = new SessionCapabilities
                        {
                            List = new SessionListCapabilities(),
                            Resume = new SessionResumeCapabilities(),
                            Close = new SessionCloseCapabilities(),
                            // append only: --append-system-prompt adds to Claude Code's own prompt
                            SystemPrompt = new SessionSystemPromptCapabilities(),
                        },
                    },
                    AuthMethods = [],
                });
            case AcpMethods.Agent.SessionNew:
            {
                var p = request.ParamsAs<NewSessionRequest>();
                var cwd = RequireDirectory(p.Cwd);
                var systemPrompt = ClaudeHooks.SystemPromptText(p.SystemPrompt);
                var id = Guid.NewGuid().ToString();
                // Recorded at the first prompt: a session nobody prompts never reaches the list.
                sessions[id] = new ClaudeSession(this, options, id, cwd, resume: false) { McpServers = p.McpServers, SystemPrompt = systemPrompt };
                return AcpJson.ToNode(new NewSessionResponse { SessionId = id });
            }
            case AcpMethods.Agent.SessionList:
            {
                var p = request.ParamsAs<ListSessionsRequest>();
                var list = state.All()
                    .Where(r => p.Cwd is null || SamePath(r.Cwd, p.Cwd))
                    .OrderByDescending(r => r.UpdatedAt)
                    .Select(r => new SessionInfo
                    {
                        SessionId = r.SessionId,
                        Cwd = r.Cwd,
                        Title = r.Title,
                        UpdatedAt = r.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
                    }).ToList();
                return AcpJson.ToNode(new ListSessionsResponse { Sessions = list });
            }
            case AcpMethods.Agent.SessionResume:
            {
                var p = request.ParamsAs<ResumeSessionRequest>();
                var systemPrompt = ClaudeHooks.SystemPromptText(p.SystemPrompt);
                var session = Session(p.SessionId, p.Cwd);
                if (p.McpServers is { } servers) session.McpServers = servers;
                session.SystemPrompt = systemPrompt; // it applies per request: none now means none
                return AcpJson.ToNode(new ResumeSessionResponse());
            }
            case AcpMethods.Agent.SessionPrompt:
            {
                var p = request.ParamsAs<PromptRequest>();
                var session = Session(p.SessionId, null);
                var text = string.Join("\n\n", p.Prompt.Select(BlockText).Where(t => t.Length > 0));
                if (text.Length == 0) throw new JsonRpcException(JsonRpcException.InvalidParams, "The prompt has no text.");
                var sequence = request.Sequence;
                state.Update(session.Id, session.Cwd, _ => { });
                var stop = await session.PromptAsync(text,
                    () => cancelledAt.TryGetValue(session.Id, out var at) && at > sequence, ct);
                state.Update(session.Id, session.Cwd, r => r.UpdatedAt = DateTimeOffset.UtcNow);
                return AcpJson.ToNode(new PromptResponse { StopReason = stop });
            }
            case AcpMethods.Agent.SessionClose:
            {
                var id = (string?)request.Params?["sessionId"] ?? "";
                if (sessions.TryRemove(id, out var s)) await s.DisposeAsync();
                return new JsonObject();
            }
            case GorynychProtocol.MethodOpinion:
                return await OpinionAsync(request, ct);
            case GorynychProtocol.MethodNote:
                return await NoteAsync(request, ct);
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {request.Method}");
        }
    }

    Task OnNotificationAsync(string method, JsonNode? @params)
    {
        if (method == AcpMethods.Agent.SessionCancel && (string?)@params?["sessionId"] is { } id)
        {
            cancelledAt[id] = rpc.NotificationSequence;
            if (sessions.TryGetValue(id, out var s)) s.Cancel();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>_gorynych/opinion</c>: another agent asks this session for an opinion through the channel;
    /// the result carries Claude's <c>reply</c>.
    /// </summary>
    async Task<JsonNode?> OpinionAsync(JsonRpcRequest request, CancellationToken ct)
    {
        // Without a channel the service falls back to asking in an ordinary turn.
        if (Channel is null) throw new JsonRpcException(JsonRpcException.MethodNotFound, "Claude sessions run without a channel.");
        var id = (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
        var text = (string?)request.Params?["text"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "text is required");
        var session = Session(id, null);
        if (!session.IsRunning) throw new JsonRpcException(JsonRpcException.InvalidRequest, "The Claude session is not running; prompt it first.");
        if (!session.ChannelLoaded) throw new JsonRpcException(JsonRpcException.MethodNotFound, "This Claude session runs without a channel.");
        var reply = await Channel.AskAsync(id, text, request.Params?["_meta"] as JsonObject, ct);
        return new JsonObject { ["text"] = reply };
    }

    /// <summary>
    /// <c>_gorynych/note</c>: something for a running session that needs no answer (a child's
    /// report), pushed through the channel. Without a channel, or with Claude not running, the
    /// service delivers it as a turn instead.
    /// </summary>
    async Task<JsonNode?> NoteAsync(JsonRpcRequest request, CancellationToken ct)
    {
        if (Channel is null) throw new JsonRpcException(JsonRpcException.MethodNotFound, "Claude sessions run without a channel.");
        var id = (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
        var text = (string?)request.Params?["text"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "text is required");
        var session = Session(id, null);
        if (!session.IsRunning) throw new JsonRpcException(JsonRpcException.InvalidRequest, "The Claude session is not running.");
        if (!session.ChannelLoaded) throw new JsonRpcException(JsonRpcException.MethodNotFound, "This Claude session runs without a channel.");
        await Channel.NotifyAsync(id, text, request.Params?["_meta"] as JsonObject, ct);
        return new JsonObject();
    }

    ClaudeSession Session(string id, string? cwd)
    {
        if (sessions.TryGetValue(id, out var live)) return live;
        var known = state.Get(id);
        var dir = known?.Cwd ?? cwd;
        if (dir is null || !File.Exists(TranscriptTitles.ExpectedPath(dir, id)) && known is null)
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Unknown Claude session {id}");
        var session = sessions.GetOrAdd(id, _ => new ClaudeSession(this, options, id, RequireDirectory(dir), resume: true));
        if (known is null) state.Update(id, dir, _ => { });
        return session;
    }

    public Task EmitAsync(string sessionId, SessionUpdate update) => rpc.SendUpdateAsync(sessionId, update);

    /// <summary>
    /// Whether this launch of the session loads the channel. Never without a channel server; the
    /// answer given up front or kept from before when there is one; otherwise the person is asked
    /// once, on this session, while other sessions' launches wait for that answer. A question
    /// left unanswered (dismissed, or no client could take it) runs this launch without the
    /// channel and is asked again at the next one.
    /// </summary>
    public async Task<bool> UseChannelAsync(string sessionId, CancellationToken ct)
    {
        if (Channel is null) return false;
        if (options.Channels != ChannelConsent.Ask) return options.Channels == ChannelConsent.Accept;
        if (consent.Answer is { } known) return known;
        await consentGate.WaitAsync(ct);
        try
        {
            if (consent.Answer is { } decided) return decided;
            Log($"claude {sessionId}: asking the person about development channels");
            bool? accepted;
            try
            {
                var response = await RequestPermissionAsync(ClaudeHooks.ChannelConsentQuestion(sessionId), ct);
                accepted = response.Outcome is SelectedPermissionOutcome ? ClaudeHooks.IsAllowed(response.Outcome) : null;
            }
            catch (Exception e) when (e is IOException or JsonRpcException)
            {
                Log($"claude {sessionId}: the development-channels question failed: {e.Message}");
                accepted = null;
            }
            if (accepted is { } answer)
            {
                consent.Save(answer, sessionId);
                Log($"claude {sessionId}: development channels {(answer ? "allowed" : "declined")} by the person; kept in {consent.Path}");
            }
            try
            {
                await EmitAsync(sessionId, ClaudeHooks.ToolCallStatusUpdate(ClaudeHooks.ChannelConsentToolCallId,
                    accepted == true ? ToolCallStatus.Completed : ToolCallStatus.Failed,
                    accepted switch
                    {
                        true => "Allowed. Gorynych starts Claude with development channels and confirms the notice on each launch.",
                        false => "Not allowed. Claude runs without the channel; other agents' messages reach it as turns.",
                        null => "Not answered. This launch runs without the channel; Gorynych asks again at the next one.",
                    }));
            }
            catch (IOException) { }
            return accepted ?? false;
        }
        finally
        {
            consentGate.Release();
        }
    }

    public Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct) =>
        rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission, request, ct);

    /// <summary>True when the ACP client declared form elicitation in <c>initialize</c>.</summary>
    public bool ClientHasForms { get; private set; }

    public Task<CreateElicitationResponse> ElicitAsync(CreateElicitationRequest request, CancellationToken ct) =>
        rpc.RequestAsync<CreateElicitationRequest, CreateElicitationResponse>(AcpMethods.Client.ElicitationCreate, request, ct);

    public async Task OnTitleAsync(string sessionId, string title)
    {
        var record = state.Get(sessionId);
        if (record?.Title == title) return;
        state.Update(sessionId, record?.Cwd ?? "", r => { r.Title = title; r.UpdatedAt = DateTimeOffset.UtcNow; });
        try
        {
            await EmitAsync(sessionId, new SessionInfoUpdate
            {
                Title = title,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });
        }
        catch (IOException) { }
    }

    public Task TouchAsync(string sessionId)
    {
        if (state.Get(sessionId) is { } r) state.Update(sessionId, r.Cwd, x => x.UpdatedAt = DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- HTTP from Claude and the channel server

    async Task<JsonNode?> OnHttpAsync(string kind, string sessionId, string token, JsonObject? body, CancellationToken ct)
    {
        if (!sessions.TryGetValue(sessionId, out var session) || !TokenMatches(session.Token, token)) return null;
        switch (kind)
        {
            case "hook" when body is not null:
                return await session.OnHookAsync(body, ct);
            case "channel-next" when Channel is not null:
                return await Channel.NextAsync(sessionId, ct);
            case "channel-reply" when Channel is not null:
                return Channel.Reply(body);
            default:
                return null;
        }
    }

    static bool TokenMatches(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    static string BlockText(ContentBlock block) => block switch
    {
        TextContent t => t.Text,
        ResourceLink l => $"@{l.Uri}",
        _ => "",
    };

    static string RequireDirectory(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd) || !Directory.Exists(cwd))
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"cwd must be an existing absolute directory: '{cwd}'");
        return Path.GetFullPath(cwd);
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        foreach (var s in sessions.Values)
            try { await s.DisposeAsync(); } catch (Exception e) { Log($"claude {s.Id}: stop failed: {e.Message}"); }
        sessions.Clear();
        if (hooks is not null) await hooks.DisposeAsync();
        await rpc.DisposeAsync();
    }
}

/// <summary>The Claude Code adapter: an in-process ACP agent driving interactive <c>claude</c> sessions.</summary>
public sealed class ClaudeAdapter(ClaudeAdapterOptions options) : IAgentAdapter
{
    public string Name => "claude";
    public string DisplayName => "Claude Code";

    public async Task<AgentInstance> StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Claude adapter needs ConPTY; a Unix PTY host is not written yet.");
        Directory.CreateDirectory(options.DataDirectory);
        var link = new DuplexLink();
        var (agentIn, agentOut) = link.AgentEnd;
        var agent = new ClaudeAgent(options, new JsonRpcConnection(agentIn, agentOut, "claude-adapter"));
        await agent.StartAsync();
        var (clientIn, clientOut) = link.ClientEnd;
        var exited = new TaskCompletionSource();
        return new AgentInstance(clientIn, clientOut, exited.Task, async () =>
        {
            if (OperatingSystem.IsWindows()) await agent.DisposeAsync();
            exited.TrySetResult();
        });
    }
}
