using System.Collections.Concurrent;
using System.Globalization;
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

    /// <summary>How Claude Code is driven: headless (<c>claude -p</c>, the default) or the TUI in a pseudo console.</summary>
    public ClaudeTransport Transport { get; init; } = ClaudeTransport.Headless;

    /// <summary>The <c>claude</c> executable; found on PATH when null.</summary>
    public string? Executable { get; init; }

    /// <summary>Passed as <c>--model</c> when set (for example <c>haiku</c>), unless the person picks a model.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Passed as <c>--permission-mode</c> unless the person picks a mode. <c>default</c> makes Claude
    /// ask, which becomes an ACP approval; a global <c>defaultMode: auto</c> would otherwise decide
    /// by itself. Never Full access (<c>bypassPermissions</c>): that is only the person's own pick, and
    /// is taken as <c>default</c> here.
    /// </summary>
    public string PermissionMode { get; init; } = "default";

    /// <summary>
    /// The command that runs the <c>gorynych</c> server for the live-note path: the TUI's development
    /// channel, or the reply route of a headless session; null for none.
    /// </summary>
    public IReadOnlyList<string>? ChannelServerCommand { get; init; }

    /// <summary>
    /// TUI only: whether Claude may be started with development channels, which makes Claude Code
    /// show a notice the adapter confirms on each launch. <see cref="ChannelConsent.Ask"/> asks the
    /// person once and keeps the answer in <c>channels.json</c> of <see cref="DataDirectory"/>.
    /// </summary>
    public ChannelConsent Channels { get; init; } = ChannelConsent.Ask;

    /// <summary>How long a launch may take: the TUI's first prompt, or a headless <c>initialize</c>.</summary>
    public int StartupTimeoutSeconds { get; init; } = 90;

    public int PasteEnterDelayMs { get; init; } = 400;

    /// <summary>Headless: how long a closed Claude may take to exit before its process tree is killed.</summary>
    public int CloseTimeoutMs { get; init; } = 5000;

    /// <summary>Headless: how long after an interrupt Claude may take to end the turn before its process is stopped.</summary>
    public int InterruptGraceMs { get; init; } = 5000;

    /// <summary>Headless: how long Claude may stay silent with messages waiting and no turn running before they count as dropped.</summary>
    public int StallMs { get; init; } = 30_000;

    /// <summary>Headless, for tests: awaited before a cancel's interrupt takes the input.</summary>
    public Func<Task>? BeforeInterrupt { get; init; }

    /// <summary>Claude's <c>projects</c> folder, where transcripts are; null for Claude's own default. For tests.</summary>
    public string? ProjectsDirectory { get; init; }

    /// <summary>Claude's state file that records trusted folders; null for Claude's own default. For tests.</summary>
    public string? ClaudeStatePath { get; init; }

    /// <summary>Keeps the raw terminal output of each TUI launch in the session folder, for diagnosis.</summary>
    public bool KeepTerminalLog { get; init; } = true;

    /// <summary>
    /// Headless: with no kept model catalog, fetch one at start from a Claude launched only for its
    /// <c>initialize</c> (no prompt, no session saved), so the selectors show before any session runs.
    /// </summary>
    public bool FetchCatalog { get; init; } = true;

    public Action<string>? Log { get; init; }
}

/// <summary>
/// The ACP agent face of the Claude adapter. Runs in the service process and talks ACP over
/// an in-memory link, so moving it into its own process later means only wrapping it.
/// </summary>
internal sealed class ClaudeAgent : IAsyncDisposable
{
    readonly ClaudeAdapterOptions options;
    readonly JsonRpcConnection rpc;
    readonly ClaudeStateStore state;
    readonly ConcurrentDictionary<string, IClaudeSession> sessions = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, long> cancelledAt = new(StringComparer.Ordinal);
    readonly ChannelConsentStore consent;
    readonly ClaudeTrust trust;
    readonly ClaudeCatalogStore catalogStore;
    readonly CancellationTokenSource lifetime = new();
    Task? fetching;
    readonly ConcurrentDictionary<string, Lazy<Task>> trustQuestions = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock consentGate = new();
    string? consentAsking; // the session whose development-channels question is open
    HookServer? hooks;

    public ClaudeAgent(ClaudeAdapterOptions options, JsonRpcConnection rpc)
    {
        this.options = options;
        this.rpc = rpc;
        state = new ClaudeStateStore(Path.Combine(options.DataDirectory, "sessions.json"), options.Log);
        consent = new ChannelConsentStore(Path.Combine(options.DataDirectory, "channels.json"), options.Log);
        trust = new ClaudeTrust(Path.Combine(options.DataDirectory, "trusted.json"), options.ClaudeStatePath, options.Log);
        catalogStore = new ClaudeCatalogStore(Path.Combine(options.DataDirectory, "catalog.json"), options.Log);
        Catalog = catalogStore.Load();
        Channel = options.ChannelServerCommand is { Count: > 0 } command ? new ChannelHost(command) : null;
        rpc.RequestHandler = HandleAsync;
        rpc.NotificationHandler = OnNotificationAsync;
        rpc.Log = Log;
    }

    public ChannelHost? Channel { get; }

    bool Headless => options.Transport == ClaudeTransport.Headless;

    /// <summary>
    /// Claude's model catalog: kept from an earlier run (<c>catalog.json</c>), fetched at start, or from
    /// the latest headless launch's <c>initialize</c>; empty until one of them brings it.
    /// </summary>
    public IReadOnlyList<ClaudeModel> Catalog { get; private set; } = [];

    public async Task StartAsync()
    {
        hooks = await HookServer.StartAsync(OnHttpAsync);
        if (Channel is not null) Channel.BaseUrl = hooks.BaseUrl;
        Log($"claude adapter ({(Headless ? "headless" : "TUI")}): loopback server at {hooks.BaseUrl}");
        rpc.Start();
        // In the background: neither the service's start nor session/new waits for it.
        if (Headless && Catalog.Count == 0 && options.FetchCatalog) fetching = Task.Run(() => FetchCatalogAsync(lifetime.Token));
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
                    AgentInfo = new Implementation
                    {
                        Name = "claude-code",
                        Title = Headless ? "Claude Code" : "Claude Code (interactive)",
                        Version = "gorynych-adapter",
                    },
                    AgentCapabilities = new AgentCapabilities
                    {
                        LoadSession = false,
                        PromptCapabilities = new PromptCapabilities { Image = Headless, Audio = false, EmbeddedContext = false },
                        SessionCapabilities = new SessionCapabilities
                        {
                            List = new SessionListCapabilities(),
                            Resume = new SessionResumeCapabilities(),
                            Close = new SessionCloseCapabilities(),
                            // append only: --append-system-prompt adds to Claude Code's own prompt
                            SystemPrompt = new SessionSystemPromptCapabilities(),
                        },
                        Meta = new JsonObject
                        {
                            // It replays a session's transcript after the last entry the service saw (TranscriptHistory).
                            [GorynychProtocol.CapReplayFrom] = true,
                            // Headless sessions write Claude's own title through rename_session.
                            [GorynychProtocol.CapSetTitle] = Headless,
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
                var session = NewSession(id, cwd, resume: false);
                session.McpServers = p.McpServers;
                session.SystemPrompt = systemPrompt;
                sessions[id] = session;
                return AcpJson.ToNode(new NewSessionResponse { SessionId = id, ConfigOptions = ConfigOptionsOf(session) });
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
                var replayed = p.Meta?[GorynychProtocol.MetaReplayFrom] is JsonObject from ? await ReplayAsync(session, from) : null;
                return AcpJson.ToNode(new ResumeSessionResponse
                {
                    ConfigOptions = ConfigOptionsOf(session),
                    Meta = replayed is null ? null : new JsonObject { [GorynychProtocol.MetaReplay] = replayed },
                });
            }
            case AcpMethods.Agent.SessionPrompt:
            {
                var p = request.ParamsAs<PromptRequest>();
                var session = Session(p.SessionId, null);
                if (!StreamJson.HasContent(p.Prompt)) throw new JsonRpcException(JsonRpcException.InvalidParams, "The prompt has no text.");
                var sequence = request.Sequence;
                state.Update(session.Id, session.Cwd, r => KeepSettings(session, r));
                var stop = await session.PromptAsync(p.Prompt,
                    () => cancelledAt.TryGetValue(session.Id, out var at) && at > sequence, ct);
                state.Update(session.Id, session.Cwd, r => r.UpdatedAt = DateTimeOffset.UtcNow);
                return AcpJson.ToNode(new PromptResponse { StopReason = stop, Meta = await MarkMetaAsync(session) });
            }
            case AcpMethods.Agent.SessionSetConfigOption:
            {
                var p = request.ParamsAs<SetSessionConfigOptionRequest>();
                if (Session(p.SessionId, null) is not HeadlessSession headless)
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, "Claude's TUI sessions have no options.");
                if (p is not SetSessionConfigOptionRequestValueId { Value: var value })
                    throw new JsonRpcException(JsonRpcException.InvalidParams, "Claude's options are selects.");
                var configOptions = await headless.SetOptionAsync(p.ConfigId, value, ct);
                // A draft is recorded at its first prompt, which keeps its choices then.
                if (state.Get(headless.Id) is not null) state.Update(headless.Id, headless.Cwd, r => KeepSettings(headless, r));
                return AcpJson.ToNode(new SetSessionConfigOptionResponse { ConfigOptions = configOptions });
            }
            case GorynychProtocol.MethodSetTitle:
            {
                var id = (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
                var title = (string?)request.Params?["title"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "title is required");
                if (Session(id, null) is not HeadlessSession headless)
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, "Claude's TUI sessions cannot be renamed by Gorynych.");
                await headless.SetTitleAsync(title, ct);
                return new JsonObject();
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

    /// <summary>Where Claude keeps the session's transcript.</summary>
    public string TranscriptOf(IClaudeSession session) => TranscriptTitles.ExpectedPath(session.Cwd, session.Id, options.ProjectsDirectory);

    /// <summary>
    /// <c>_meta</c> with the transcript's mark (<c>gorynych/historyMark</c>) at the end of a turn: the
    /// service's journal now holds the conversation up to there. Taken once the transcript settles
    /// (<see cref="TranscriptHistory.SettledMarkAsync"/>). Null when there is no transcript; it never
    /// fails the turn.
    /// </summary>
    public async Task<JsonObject?> MarkMetaAsync(IClaudeSession session)
    {
        try
        {
            var mark = await TranscriptHistory.SettledMarkAsync(TranscriptOf(session));
            return mark is null ? null : new JsonObject { [GorynychProtocol.MetaHistoryMark] = mark };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"claude {session.Id}: the transcript's mark could not be taken: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Replays the session's conversation after the service's mark from Claude's transcript
    /// (docs/adapter-contract.md, §4), or all of it. Answers <c>gorynych/replay</c>, or null when
    /// the transcript cannot be read (the service then leaves its journal as it is).
    /// </summary>
    async Task<JsonObject?> ReplayAsync(IClaudeSession session, JsonObject from)
    {
        var mark = (string?)from["type"] == GorynychProtocol.ReplayAfterMark ? (string?)from["mark"] : null;
        var path = TranscriptOf(session);
        var replay = await TranscriptHistory.ReadAsync(path, mark, update => rpc.SendUpdateAsync(session.Id, update), m => Log($"claude {session.Id}: {m}"));
        if (replay is null)
        {
            Log($"claude {session.Id}: no readable transcript at {path}; the service's journal is not caught up");
            return null;
        }
        Log($"claude {session.Id}: {replay.Sent} updates replayed {(replay.FromMark ? "after the mark" : "from the start")} ({replay.BytesRead} bytes read)");
        return new JsonObject { ["from"] = replay.FromMark ? "mark" : "start", ["mark"] = replay.Mark };
    }

    IClaudeSession NewSession(string id, string cwd, bool resume)
    {
        if (Headless)
        {
            var headless = new HeadlessSession(this, options, id, cwd, resume);
            if (state.Get(id) is { } known)
            {
                headless.Settings.Model = known.Model;
                headless.Settings.Effort = known.Effort;
                headless.Settings.Mode = known.Mode;
            }
            return headless;
        }
        if (!OperatingSystem.IsWindows())
            throw new JsonRpcException(JsonRpcException.InternalError, "Claude's TUI transport needs ConPTY, which only Windows has.");
        return new ClaudeSession(this, options, id, cwd, resume);
    }

    /// <summary>Keeps a headless session's choices in its record, so they outlive its process and the service.</summary>
    static void KeepSettings(IClaudeSession session, ClaudeSessionRecord record)
    {
        if (session is not HeadlessSession headless) return;
        record.Model = headless.Settings.Model;
        record.Effort = headless.Settings.Effort;
        record.Mode = headless.Settings.Mode;
    }

    List<SessionConfigOption>? ConfigOptionsOf(IClaudeSession session) =>
        session is HeadlessSession headless ? ClaudeOptions.ConfigOptions(headless.Settings, Catalog) : null;

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
    /// <c>_gorynych/opinion</c>: another agent asks this session for an opinion on the live-note path,
    /// the development channel (TUI) or Claude's input (headless); the result carries Claude's <c>reply</c>.
    /// </summary>
    async Task<JsonNode?> OpinionAsync(JsonRpcRequest request, CancellationToken ct)
    {
        // Without a channel the service falls back to asking in an ordinary turn.
        if (Channel is null) throw new JsonRpcException(JsonRpcException.MethodNotFound, "Claude sessions run without a live-note path.");
        var id = (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
        var text = (string?)request.Params?["text"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "text is required");
        var session = Session(id, null);
        if (!session.IsRunning) throw new JsonRpcException(JsonRpcException.InvalidRequest, "The Claude session is not running; prompt it first.");
        if (session is HeadlessSession headless)
        {
            var (messageId, reply) = Channel.OpenRequest();
            try
            {
                var meta = (request.Params?["_meta"] as JsonObject)?.DeepClone().AsObject() ?? [];
                meta[GorynychProtocol.MetaOpinion] = messageId;
                var dropped = await headless.DeliverAsync(LiveOpinionText(text, messageId), meta, ct);
                if (await Task.WhenAny(reply, dropped).WaitAsync(ct) != reply)
                    // Cancelled with the client's turn, or lost with the process: the service asks in a turn instead.
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, "Claude did not take the request on its live-note path.");
                return new JsonObject { ["text"] = await reply };
            }
            finally { Channel.Forget(messageId); }
        }
        if (!OperatingSystem.IsWindows() || session is not ClaudeSession { ChannelLoaded: true })
            throw new JsonRpcException(JsonRpcException.MethodNotFound, "This Claude session runs without a channel.");
        var answer = await Channel.AskAsync(id, text, request.Params?["_meta"] as JsonObject, ct);
        return new JsonObject { ["text"] = answer };
    }

    /// <summary>The service's text unchanged, with how to answer it after it.</summary>
    internal static string LiveOpinionText(string text, string messageId) =>
        $"{text}\n\nAnswer with the reply tool of the gorynych MCP server, passing message_id \"{messageId}\" " +
        "(reply answers this request; ask_agent would start a new question instead). Take no other action for it.";

    /// <summary>
    /// <c>_gorynych/note</c>: something for a running session that needs no answer (a child's
    /// report), on the live-note path. Without one, or with Claude not running, the service
    /// delivers it as a turn instead.
    /// </summary>
    async Task<JsonNode?> NoteAsync(JsonRpcRequest request, CancellationToken ct)
    {
        if (Channel is null) throw new JsonRpcException(JsonRpcException.MethodNotFound, "Claude sessions run without a live-note path.");
        var id = (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
        var text = (string?)request.Params?["text"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "text is required");
        var session = Session(id, null);
        if (!session.IsRunning) throw new JsonRpcException(JsonRpcException.InvalidRequest, "The Claude session is not running.");
        if (session is HeadlessSession headless)
        {
            var meta = (request.Params?["_meta"] as JsonObject)?.DeepClone().AsObject() ?? [];
            var dropped = await headless.DeliverAsync(text, meta, ct);
            _ = dropped.ContinueWith(_ => Log($"claude {id}: a report was not taken (cancelled with a turn, or Claude exited)"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            return new JsonObject();
        }
        if (!OperatingSystem.IsWindows() || session is not ClaudeSession { ChannelLoaded: true })
            throw new JsonRpcException(JsonRpcException.MethodNotFound, "This Claude session runs without a channel.");
        await Channel.NotifyAsync(id, text, request.Params?["_meta"] as JsonObject, ct);
        return new JsonObject();
    }

    IClaudeSession Session(string id, string? cwd)
    {
        if (sessions.TryGetValue(id, out var live)) return live;
        var known = state.Get(id);
        var dir = known?.Cwd ?? cwd;
        if (dir is null || !File.Exists(TranscriptTitles.ExpectedPath(dir, id, options.ProjectsDirectory)) && known is null)
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Unknown Claude session {id}");
        var session = sessions.GetOrAdd(id, _ => NewSession(id, RequireDirectory(dir), resume: true));
        if (known is null) state.Update(id, dir, _ => { });
        return session;
    }

    public Task EmitAsync(string sessionId, SessionUpdate update) => rpc.SendUpdateAsync(sessionId, update);

    // ---------------------------------------------------------------- headless: catalog and trust

    /// <summary>
    /// A headless <c>initialize</c> answered (a session's launch, or the fetch at start). A catalog that is
    /// new or changed is kept and shown to every headless session at once, so open threads get their
    /// selectors without being reopened; otherwise only the launching session hears its options.
    /// </summary>
    public async Task OnInitializeAsync(HeadlessSession? session, JsonObject initialize)
    {
        var changed = false;
        if (initialize["models"] is JsonArray models && ClaudeOptions.Catalog(initialize) is { Count: > 0 } catalog && catalogStore.Save(models))
        {
            Catalog = catalog;
            changed = true;
        }
        if (session is not null) await OnSettingsReportedAsync(session);
        if (!changed) return;
        // The other sessions hear it apart from the launch, which neither waits for them nor fails with them.
        var others = sessions.Values.OfType<HeadlessSession>().Where(s => s != session).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var open in others) await OnSettingsReportedAsync(open);
        });
    }

    /// <summary>
    /// Fetches the catalog without a session: Claude launched as every headless session is (the same
    /// flags, the same cleaned environment, the same plan check), in the adapter's own folder, with
    /// no prompt and nothing saved (<c>--no-session-persistence</c>), asked only <c>initialize</c>. It
    /// loads none of the person's settings files (<c>--setting-sources local</c>, and the folder has
    /// none) and no MCP server (<c>--strict-mcp-config</c> with an empty <c>--mcp-config</c>), so none of
    /// their hooks or servers runs for it. Stopping the adapter stops it.
    /// </summary>
    async Task FetchCatalogAsync(CancellationToken ct)
    {
        try
        {
            var exe = options.Executable ?? ExecutableLocator.Find("claude");
            if (exe is null)
            {
                Log("claude: not on PATH, so no model catalog yet");
                return;
            }
            var folder = Path.Combine(options.DataDirectory, "catalog");
            Directory.CreateDirectory(folder);
            var settingsPath = Path.Combine(folder, "settings.json");
            File.WriteAllText(settingsPath, StreamJson.Settings().ToJsonString(AcpJson.TextOptions));
            var id = Guid.NewGuid().ToString();
            var mcpPath = Path.Combine(folder, "mcp.json");
            File.WriteAllText(mcpPath, """{"mcpServers":{}}""");
            var args = new List<string>(StreamJson.BaseArgs)
            {
                "--settings", settingsPath, "--setting-sources", "local", "--strict-mcp-config", "--mcp-config", mcpPath,
                "--no-session-persistence", "--session-id", id,
            };
            args.AddRange(ClaudeOptions.LaunchArgs(new ClaudeSettings(), options.PermissionMode, options.Model));
            Log($"claude: fetching the model catalog in {folder} (session {id}, not kept)");
            await using var claude = ClaudeProcess.Start(exe, args, folder, ClaudeEnvironment.Clean(headless: true), Path.Combine(folder, "stderr.log"),
                _ => Task.CompletedTask, Log);
            claude.BeginReading();
            try
            {
                var init = await claude.ControlAsync(new JsonObject { ["subtype"] = "initialize" }, TimeSpan.FromSeconds(options.StartupTimeoutSeconds), ct);
                if (StreamJson.PlanProblem(init) is { } problem)
                {
                    Log($"claude: no model catalog fetched: {problem}");
                    return;
                }
                ct.ThrowIfCancellationRequested();
                await OnInitializeAsync(null, init);
                Log($"claude: fetched the model catalog ({Catalog.Count} models) from Claude Code {(string?)init["claude_code_version"]}");
            }
            finally
            {
                await claude.StopAsync(TimeSpan.FromMilliseconds(options.CloseTimeoutMs));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log("claude: the model catalog fetch stopped with the adapter");
        }
        catch (Exception e)
        {
            Log($"claude: could not fetch the model catalog: {e.Message}");
        }
    }

    /// <summary>Claude reported its model or mode: the session's options may have changed.</summary>
    public async Task OnSettingsReportedAsync(HeadlessSession session)
    {
        // News, not a step of anything: a session that is gone or not announced yet fails nothing else.
        try { await EmitAsync(session.Id, new ConfigOptionUpdate { ConfigOptions = ClaudeOptions.ConfigOptions(session.Settings, Catalog) }); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log($"claude {session.Id}: its options were not sent: {e.Message}");
        }
    }

    /// <summary>
    /// Before the first headless launch in a folder: Claude's own trust flag, or Gorynych's kept
    /// allow, or the person's answer now (one question per folder at a time). A deny fails the prompt.
    /// </summary>
    public async Task EnsureTrustedAsync(string sessionId, string cwd, CancellationToken ct)
    {
        if (trust.IsTrusted(cwd)) return;
        var asking = trustQuestions.GetOrAdd(cwd, _ => new Lazy<Task>(() => AskTrustAsync(sessionId, cwd)));
        // The question is forgotten once answered, not when one of its waiters gives up.
        _ = asking.Value.ContinueWith(_ => trustQuestions.TryRemove(new KeyValuePair<string, Lazy<Task>>(cwd, asking)), TaskScheduler.Default);
        await asking.Value.WaitAsync(ct);
    }

    async Task AskTrustAsync(string sessionId, string cwd)
    {
        if (trust.IsTrusted(cwd)) return;
        Log($"claude {sessionId}: asking the person whether to trust {cwd}");
        RequestPermissionResponse response;
        try { response = await RequestPermissionAsync(ClaudeHooks.TrustFolder(sessionId, cwd), CancellationToken.None); }
        catch (Exception e) when (e is IOException or JsonRpcException)
        {
            throw new JsonRpcException(JsonRpcException.InternalError, $"Gorynych could not ask whether to trust {cwd}: {e.Message}");
        }
        if (!ClaudeHooks.IsAllowed(response.Outcome))
            throw new JsonRpcException(JsonRpcException.InvalidRequest, $"Claude does not start in {cwd}: the folder was not trusted.");
        trust.Remember(cwd);
    }

    // ---------------------------------------------------------------- TUI: development channels

    /// <summary>
    /// Whether this launch of the session loads the channel. Never without a channel server; the
    /// answer given up front or kept from before when there is one; otherwise the person is asked,
    /// on this session. One question at a time, and it holds up no other session: while it is
    /// open, other launches go without the channel and ask nothing. A question left unanswered
    /// (dismissed, or no client could take it) runs this launch without the channel and is asked
    /// again at a later one. Cancelling <paramref name="ct"/> withdraws the question.
    /// </summary>
    public async Task<bool> UseChannelAsync(string sessionId, CancellationToken ct)
    {
        if (Channel is null) return false;
        if (options.Channels != ChannelConsent.Ask) return options.Channels == ChannelConsent.Accept;
        lock (consentGate)
        {
            if (consent.Answer is { } known) return known;
            if (consentAsking is { } other)
            {
                Log($"claude {sessionId}: starts without the channel while the development-channels question on {other} is open");
                return false;
            }
            consentAsking = sessionId;
        }
        try
        {
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
            catch (OperationCanceledException)
            {
                Log($"claude {sessionId}: the development-channels question was withdrawn");
                await CloseChannelQuestionAsync(sessionId, ToolCallStatus.Failed,
                    "Withdrawn: the prompt was cancelled. Gorynych asks again at the next launch.");
                throw;
            }
            if (accepted is { } answer)
            {
                // Saved before the question counts as closed, so a launch that finds none open finds the answer.
                consent.Save(answer, sessionId);
                Log($"claude {sessionId}: development channels {(answer ? "allowed" : "declined")} by the person; kept in {consent.Path}");
            }
            await CloseChannelQuestionAsync(sessionId, accepted == true ? ToolCallStatus.Completed : ToolCallStatus.Failed,
                accepted switch
                {
                    true => "Allowed. Gorynych starts Claude with development channels and confirms the notice on each launch.",
                    false => "Not allowed. Claude runs without the channel; other agents' messages reach it as turns.",
                    null => "Not answered. This launch runs without the channel; Gorynych asks again at the next one.",
                });
            return accepted ?? false;
        }
        finally
        {
            lock (consentGate) consentAsking = null;
        }
    }

    /// <summary>Ends the <c>development-channels</c> tool call the question opened; best effort.</summary>
    async Task CloseChannelQuestionAsync(string sessionId, ToolCallStatus status, string text)
    {
        try { await EmitAsync(sessionId, ClaudeHooks.ToolCallStatusUpdate(ClaudeHooks.ChannelConsentToolCallId, status, text)); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or JsonRpcException)
        {
            Log($"claude {sessionId}: the development-channels tool call was not closed: {e.Message}");
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

    // ---------------------------------------------------------------- HTTP from Claude and the gorynych server

    async Task<JsonNode?> OnHttpAsync(string kind, string sessionId, string token, JsonObject? body, CancellationToken ct)
    {
        if (!sessions.TryGetValue(sessionId, out var session) || !TokenMatches(session.Token, token)) return null;
        switch (kind)
        {
            case "hook" when body is not null && OperatingSystem.IsWindows() && session is ClaudeSession tui:
                return await tui.OnHookAsync(body, ct);
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
        lifetime.Cancel();
        if (fetching is not null) await fetching; // it never throws
        foreach (var s in sessions.Values)
            try { await s.DisposeAsync(); } catch (Exception e) { Log($"claude {s.Id}: stop failed: {e.Message}"); }
        sessions.Clear();
        if (hooks is not null) await hooks.DisposeAsync();
        await rpc.DisposeAsync();
    }
}

/// <summary>The Claude Code adapter: an in-process ACP agent driving <c>claude</c> sessions, headless or in the TUI.</summary>
public sealed class ClaudeAdapter(ClaudeAdapterOptions options) : IAgentAdapter
{
    public string Name => "claude";
    public string DisplayName => "Claude Code";

    public async Task<AgentInstance> StartAsync(CancellationToken ct)
    {
        if (options.Transport == ClaudeTransport.Tui && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Claude's TUI transport needs ConPTY; a Unix PTY host is not written yet. Use the headless transport.");
        Directory.CreateDirectory(options.DataDirectory);
        var link = new DuplexLink();
        var (agentIn, agentOut) = link.AgentEnd;
        var agent = new ClaudeAgent(options, new JsonRpcConnection(agentIn, agentOut, "claude-adapter"));
        await agent.StartAsync();
        var (clientIn, clientOut) = link.ClientEnd;
        var exited = new TaskCompletionSource();
        return new AgentInstance(clientIn, clientOut, exited.Task, async () =>
        {
            await agent.DisposeAsync();
            exited.TrySetResult();
        });
    }
}
