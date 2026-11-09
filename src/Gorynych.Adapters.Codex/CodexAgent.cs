using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex.Protocol;
using static Gorynych.Adapters.Codex.CodexMap;

namespace Gorynych.Adapters.Codex;

/// <summary>
/// A Codex thread as the adapter knows it in this app-server process. The ACP side and
/// app-server's notifications and requests touch it from different tasks, so its state is under
/// one lock; the private helpers expect it held.
/// </summary>
sealed class CodexThread
{
    readonly Lock gate = new();

    CodexSettings settings;
    CodexChange? pending;
    readonly Dictionary<string, SandboxPolicy> sandboxes = new(StringComparer.Ordinal);

    TaskCompletionSource<Turn>? turn;
    string? turnId;
    bool stopAsked;
    long cancelledAt;

    readonly HashSet<string> streamed = [];
    readonly Dictionary<string, ThreadItem> tools = [];
    readonly HashSet<string> claimed = [];
    TaskCompletionSource itemStarted = Signal();

    public CodexThread(string id, CodexSettings settings)
    {
        Id = id;
        this.settings = settings;
        Remember(settings.Sandbox);
    }

    public string Id { get; }
    public volatile bool Loaded = true;

    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------------------------------------------------------------- settings

    /// <summary>The thread's settings as Codex last reported them.</summary>
    public CodexSettings Settings
    {
        get { lock (gate) return settings; }
    }

    /// <summary>What a client changed since the last turn, which goes with the next one.</summary>
    public CodexChange? Pending
    {
        get { lock (gate) return pending; }
    }

    /// <summary>The settings as the client sees them: Codex's, with the pending change.</summary>
    public CodexSettings Effective
    {
        get { lock (gate) return Apply(settings, pending); }
    }

    public void Reported(CodexSettings s)
    {
        lock (gate)
        {
            settings = s;
            Remember(s.Sandbox);
        }
    }

    /// <summary>
    /// A client's change, edited from the settings it sees and the pending change; what the
    /// thread's own settings already are is left out of it. Returns the settings it now sees.
    /// </summary>
    public CodexSettings Change(Func<CodexSettings, CodexChange, CodexChange> edit)
    {
        lock (gate)
        {
            var next = edit(Apply(settings, pending), pending ?? new CodexChange());
            if (next.Model == settings.Model) next = next with { Model = null };
            if (next.Effort == settings.Effort) next = next with { Effort = null };
            if (next.Mode is { } m && ModeOf(settings) == m.Id) next = next with { Mode = null };
            pending = next.IsEmpty ? null : next;
            return Apply(settings, pending);
        }
    }

    /// <summary>A turn carrying this change started: Codex keeps a turn's overrides for the turns after it.</summary>
    public void Applied(CodexChange change)
    {
        lock (gate)
        {
            settings = Apply(settings, change);
            Remember(settings.Sandbox);
            if (ReferenceEquals(pending, change)) pending = null;
        }
    }

    /// <summary>Codex refused a turn carrying this change: it is dropped, so it cannot fail every turn after.</summary>
    public void Refused(CodexChange change)
    {
        lock (gate)
            if (ReferenceEquals(pending, change)) pending = null;
    }

    /// <summary>
    /// The sandbox a mode sends: the thread's own when it is of that type, so its writable roots
    /// and network access stay; else one this thread had before; else Codex's default.
    /// </summary>
    public SandboxPolicy? SandboxFor(string type)
    {
        lock (gate) return SandboxForCore(type);
    }

    SandboxPolicy? SandboxForCore(string type) =>
        settings.Sandbox?.Tag == type ? settings.Sandbox : sandboxes.GetValueOrDefault(type) ?? SandboxOf(type);

    CodexSettings Apply(CodexSettings s, CodexChange? c) => c is null ? s : s with
    {
        Model = c.Model ?? s.Model,
        Effort = c.Effort ?? s.Effort,
        ApprovalPolicy = c.Mode is { } m ? JsonValue.Create(m.Policy) : s.ApprovalPolicy,
        Sandbox = c.Mode is { } mode ? SandboxForCore(mode.Sandbox) : s.Sandbox,
        Reviewer = c.Mode?.Reviewer ?? s.Reviewer,
    };

    void Remember(SandboxPolicy? sandbox)
    {
        if (sandbox?.Tag is { } tag) sandboxes[tag] = sandbox;
    }

    // ---------------------------------------------------------------- the running turn

    public TaskCompletionSource<Turn>? Turn
    {
        get { lock (gate) return turn; }
    }

    /// <summary>
    /// Starts the turn of a prompt the ACP connection received as message <paramref name="sequence"/>;
    /// null while another runs. A <c>session/cancel</c> received after the prompt but handled before
    /// this (requests run apart from notifications) stops the turn as soon as Codex names it.
    /// </summary>
    public TaskCompletionSource<Turn>? Begin(long sequence)
    {
        lock (gate)
        {
            if (turn is not null) return null;
            turn = new TaskCompletionSource<Turn>(TaskCreationOptions.RunContinuationsAsynchronously);
            turnId = null;
            stopAsked = cancelledAt > sequence;
            streamed.Clear();
            tools.Clear();
            claimed.Clear();
            return turn;
        }
    }

    /// <summary>Codex named the running turn (<c>turn/started</c> or <c>turn/start</c>'s answer); true when a stop was asked before.</summary>
    public bool Named(string id)
    {
        lock (gate)
        {
            if (turn is null || turnId is not null) return false;
            turnId = id;
            return stopAsked;
        }
    }

    /// <summary>
    /// A stop is asked (a cancel the ACP connection received as message <paramref name="sequence"/>,
    /// a close, a delete). Returns whether a turn runs, and its ID to interrupt now; with no ID yet
    /// it is interrupted when Codex names it.
    /// </summary>
    public (bool Running, string? TurnId) Stop(long sequence = 0)
    {
        lock (gate)
        {
            cancelledAt = Math.Max(cancelledAt, sequence);
            if (turn is null) return (false, null);
            stopAsked = true;
            return (true, turnId);
        }
    }

    public void Completed(Turn ended)
    {
        TaskCompletionSource<Turn>? done;
        lock (gate) done = turnId is null || turnId == ended.Id ? turn : null;
        done?.TrySetResult(ended);
    }

    /// <summary>
    /// Ends the running turn as interrupted: the thread was unloaded, closed or deleted, and no
    /// <c>turn/completed</c> is waited for. Returns the ID of the turn it ended, so that a late
    /// completion of it is not taken for a later turn's.
    /// </summary>
    public string? Abandon()
    {
        TaskCompletionSource<Turn>? done;
        string? id;
        lock (gate) (done, id) = (turn, turnId);
        return done?.TrySetResult(new Turn { Id = id ?? "", Status = TurnStatus.Interrupted }) == true ? id : null;
    }

    public void Fail(Exception e)
    {
        TaskCompletionSource<Turn>? done;
        lock (gate) done = turn;
        done?.TrySetException(e);
    }

    public void End(TaskCompletionSource<Turn> done)
    {
        lock (gate)
        {
            if (turn != done) return;
            turn = null;
            turnId = null;
            stopAsked = false;
        }
    }

    // ---------------------------------------------------------------- items

    /// <summary>Notes that an item's text came as deltas, so its completed text is not sent again; true the first time.</summary>
    public bool Streamed(string itemId)
    {
        lock (gate) return streamed.Add(itemId);
    }

    public bool WasStreamed(string itemId)
    {
        lock (gate) return streamed.Contains(itemId);
    }

    /// <summary>A tool-like item started; its approval may ask about it.</summary>
    public void Started(ThreadItem item, string id)
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            tools[id] = item;
            (signal, itemStarted) = (itemStarted, Signal());
        }
        signal.TrySetResult();
    }

    public void Finished(string id)
    {
        lock (gate) tools.Remove(id);
    }

    /// <summary>A started item, waiting up to <paramref name="wait"/> for it: app-server's requests are handled apart from its notifications, so its approval can come first.</summary>
    public Task<ThreadItem?> ToolAsync(string id, TimeSpan wait, CancellationToken ct) =>
        WaitAsync(() => tools.GetValueOrDefault(id), wait, ct);

    /// <summary>The running MCP call of this server and tool that no approval has named yet, waiting for it as <see cref="ToolAsync"/> does.</summary>
    public Task<string?> McpCallAsync(string server, string tool, TimeSpan wait, CancellationToken ct) => WaitAsync(() =>
    {
        var call = tools.Values.OfType<McpToolCallThreadItem>().FirstOrDefault(m => m.Server == server && m.Tool == tool && !claimed.Contains(m.Id));
        if (call is not null) claimed.Add(call.Id);
        return call?.Id;
    }, wait, ct);

    async Task<T?> WaitAsync<T>(Func<T?> find, TimeSpan wait, CancellationToken ct) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        while (true)
        {
            Task signal;
            lock (gate)
            {
                if (find() is { } found) return found;
                signal = itemStarted.Task;
            }
            try
            {
                await signal.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// The ACP agent face of the Codex adapter. It runs in the service process and speaks ACP over
/// an in-memory link to the service, and the app-server's JSON-RPC to one <c>codex app-server</c>.
/// </summary>
sealed class CodexAgent : IAsyncDisposable
{
    /// <summary>Notifications the adapter has no use for, which app-server then does not send.</summary>
    static readonly List<string> Unused =
    [
        "hook/started", "hook/completed", "account/updated", "account/rateLimits/updated", "remoteControl/status/changed",
        "turn/moderationMetadata", "thread/readState/changed", "mcpServer/startupStatus/updated", "skills/changed",
        "fs/changed", "app/list/updated", "item/commandExecution/outputDelta", "item/fileChange/outputDelta", "turn/diff/updated",
    ];

    readonly CodexAdapterOptions options;
    readonly JsonRpcConnection rpc;
    readonly AgentInstance appServer;
    readonly JsonRpcConnection app;

    /// <summary>Which of some threads another Codex app holds, and who (<see cref="CodexThreadHolders"/>).</summary>
    readonly Func<IEnumerable<string>, IReadOnlyDictionary<string, string>> holders;
    readonly string locksDirectory;

    /// <summary>Tells the service when another Codex app takes a thread or lets it go; started with the adapter.</summary>
    CodexLockWatcher? lockWatcher;
    readonly ConcurrentDictionary<string, CodexThread> threads = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, CancellationTokenSource> asking = new(StringComparer.Ordinal);

    /// <summary>Turns ended here before Codex said so (a close, delete or unload): their late <c>turn/completed</c> is dropped.</summary>
    readonly ConcurrentDictionary<string, byte> abandoned = new(StringComparer.Ordinal);
    readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    List<Model> models = [];
    HashSet<string> userServers = [];
    string version = "";
    int disposed;

    public CodexAgent(CodexAdapterOptions options, JsonRpcConnection rpc, AgentInstance appServer,
        Func<IEnumerable<string>, IReadOnlyDictionary<string, string>> holders, string locksDirectory)
    {
        this.options = options;
        this.rpc = rpc;
        this.appServer = appServer;
        this.holders = holders;
        this.locksDirectory = locksDirectory;
        app = new JsonRpcConnection(appServer.Input, appServer.Output, "codex-app-server")
        {
            RequestHandler = OnServerRequestAsync,
            NotificationHandler = OnServerNotificationAsync,
            Log = Log,
        };
        rpc.RequestHandler = HandleAsync;
        rpc.NotificationHandler = OnNotificationAsync;
        rpc.Log = Log;
    }

    /// <summary>Completes when the adapter stops, also when its app-server goes away.</summary>
    public Task Exited => exited.Task;

    void Log(string message) => options.Log?.Invoke(message);

    public async Task StartAsync(CancellationToken ct)
    {
        app.Start();
        _ = app.Completion.ContinueWith(_ =>
        {
            Log("codex: the app-server connection closed");
            _ = DisposeAsync().AsTask();
        }, TaskScheduler.Default);
        _ = appServer.Exited.ContinueWith(_ => _ = DisposeAsync().AsTask(), TaskScheduler.Default);
        var init = From(await app.SendRequestAsync(CodexMethods.Client.Initialize, ToNode(new InitializeParams
        {
            ClientInfo = new ClientInfo { Name = "gorynych", Title = "Gorynych", Version = options.ClientVersion },
            Capabilities = new InitializeCapabilities { OptOutNotificationMethods = Unused },
        }, CodexJsonContext.Default.InitializeParams), ct), CodexJsonContext.Default.InitializeResponse);
        await app.SendNotificationAsync("initialized", null);
        version = init.UserAgent;
        Log($"codex: app-server {version}");
        try
        {
            models = From(await app.SendRequestAsync(CodexMethods.Client.ModelList, ToNode(new ModelListParams(), CodexJsonContext.Default.ModelListParams), ct),
                CodexJsonContext.Default.ModelListResponse).Data;
            var config = await app.SendRequestAsync(CodexMethods.Client.ConfigRead, new JsonObject(), ct);
            userServers = (config?["config"]?["mcp_servers"] as JsonObject)?.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch (JsonRpcException e)
        {
            Log($"codex: models or config not read: {e.Message}");
        }
        rpc.Start();
        // A thread changing hands changes its row in the service's list (gorynych/openElsewhere).
        lockWatcher = new CodexLockWatcher(locksDirectory, holders,
            id => rpc.SendNotificationAsync(GorynychProtocol.MethodSessionsChanged, new JsonObject { ["sessionId"] = id, ["change"] = "updated" }),
            options.LockSettle, options.LockRecheck, Log);
    }

    // ---------------------------------------------------------------- ACP from the service

    async Task<JsonNode?> HandleAsync(JsonRpcRequest request, CancellationToken ct)
    {
        switch (request.Method)
        {
            case AcpMethods.Agent.Initialize:
                return AcpJson.ToNode(new Gorynych.Acp.InitializeResponse
                {
                    ProtocolVersion = AcpMethods.ProtocolVersion,
                    AgentInfo = new Implementation { Name = "codex", Title = "Codex", Version = version },
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
                            // append only: developerInstructions add to Codex's own instructions
                            SystemPrompt = new SessionSystemPromptCapabilities(),
                        },
                        Meta = new JsonObject
                        {
                            [GorynychProtocol.CapSetTitle] = true,
                            [GorynychProtocol.CapDraftsNotDurable] = true,
                            [GorynychProtocol.CapUnloadAfter] = options.UnloadAfter.TotalSeconds,
                            [GorynychProtocol.CapOpenElsewhere] = true,
                            // It tells when a thread's list row changes: another Codex app took it or let it go.
                            [GorynychProtocol.MetaSessionsChanged] = true,
                        },
                    },
                    AuthMethods = [],
                });
            case AcpMethods.Agent.SessionNew:
                return await NewAsync(request.ParamsAs<NewSessionRequest>(), ct);
            case AcpMethods.Agent.SessionResume:
            {
                var p = request.ParamsAs<ResumeSessionRequest>();
                var t = await OpenAsync(p.SessionId, p.McpServers ?? [], p.SystemPrompt, ct);
                return AcpJson.ToNode(new ResumeSessionResponse { ConfigOptions = ConfigOptions(t.Effective, models) });
            }
            case AcpMethods.Agent.SessionLoad:
            {
                var p = request.ParamsAs<LoadSessionRequest>();
                var t = await OpenAsync(p.SessionId, p.McpServers, p.SystemPrompt, ct);
                await ReplayAsync(p.SessionId, ct);
                return AcpJson.ToNode(new LoadSessionResponse { ConfigOptions = ConfigOptions(t.Effective, models) });
            }
            case AcpMethods.Agent.SessionList:
                return await ListAsync(request.ParamsAs<ListSessionsRequest>(), ct);
            case AcpMethods.Agent.SessionClose:
                await CloseAsync(RequireId(request), ct);
                return new JsonObject();
            case AcpMethods.Agent.SessionDelete:
                await DeleteAsync(RequireId(request), ct);
                return new JsonObject();
            case AcpMethods.Agent.SessionPrompt:
                return await PromptAsync(request, ct);
            case AcpMethods.Agent.SessionSetConfigOption:
                return SetConfigOption(request.ParamsAs<SetSessionConfigOptionRequest>());
            case GorynychProtocol.MethodOpenElsewhere:
            {
                var ids = (request.Params?["sessionIds"] as JsonArray ?? []).Select(i => (string?)i).OfType<string>().ToList();
                var sessions = new JsonObject();
                foreach (var (id, holder) in holders(ids)) sessions[id] = holder;
                return new JsonObject { ["sessions"] = sessions };
            }
            case GorynychProtocol.MethodSetTitle:
            {
                var title = (string?)request.Params?["title"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "title is required");
                await app.SendRequestAsync(CodexMethods.Client.ThreadNameSet, ToNode(new ThreadSetNameParams { ThreadId = RequireId(request), Name = title },
                    CodexJsonContext.Default.ThreadSetNameParams), ct);
                return new JsonObject();
            }
            default:
                // No live-note path (_gorynych/opinion, _gorynych/note): the service asks in a turn.
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {request.Method}");
        }
    }

    Task OnNotificationAsync(string method, JsonNode? @params)
    {
        // Notifications are handled in order of arrival, prompts on their own tasks: the cancel's
        // place in the stream tells which prompts it reaches (CodexThread.Begin).
        var sequence = rpc.NotificationSequence;
        // The stop is noted at once; the interrupt is not waited for, so the read loop goes on.
        if (method == AcpMethods.Agent.SessionCancel && (string?)@params?["sessionId"] is { } id && threads.TryGetValue(id, out var t))
            _ = StopAsync(t, sequence);
        return Task.CompletedTask;
    }

    static string RequireId(JsonRpcRequest request) =>
        (string?)request.Params?["sessionId"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");

    async Task<JsonNode?> NewAsync(NewSessionRequest p, CancellationToken ct)
    {
        var started = From(await app.SendRequestAsync(CodexMethods.Client.ThreadStart, ToNode(new ThreadStartParams
        {
            Cwd = p.Cwd,
            Config = McpConfig(p.McpServers),
            DeveloperInstructions = Instructions(p.SystemPrompt),
        }, CodexJsonContext.Default.ThreadStartParams), ct), CodexJsonContext.Default.ThreadStartResponse);
        var t = new CodexThread(started.Thread.Id,
            SettingsOf(started.Model, started.ReasoningEffort, started.ApprovalPolicy, started.Sandbox, started.ApprovalsReviewer));
        threads[t.Id] = t;
        Log($"codex: new thread {t.Id} in {started.Cwd}");
        return AcpJson.ToNode(new NewSessionResponse { SessionId = t.Id, ConfigOptions = ConfigOptions(t.Settings, models) });
    }

    /// <summary>
    /// Loads a thread into this app-server (<c>thread/resume</c>), with the MCP servers of this
    /// request: Codex keeps them only while the thread is loaded. A thread already loaded stays as it is.
    /// </summary>
    async Task<CodexThread> OpenAsync(string id, List<McpServer> servers, SystemPrompt? systemPrompt, CancellationToken ct)
    {
        var instructions = Instructions(systemPrompt);
        if (threads.TryGetValue(id, out var known) && known.Loaded) return known;
        // App-server would refuse it anyway (one writer); said at once, naming the app that has it.
        var held = holders([id]);
        lockWatcher?.Saw([id], held);
        if (held.GetValueOrDefault(id) is { } holder) throw OpenElsewhere(holder);
        ThreadResumeResponse resumed;
        try
        {
            resumed = From(await app.SendRequestAsync(CodexMethods.Client.ThreadResume, ToNode(new ThreadResumeParams
            {
                ThreadId = id,
                ExcludeTurns = true,
                Config = McpConfig(servers),
                // Codex keeps the instructions given when the thread was created; these do not replace them.
                DeveloperInstructions = instructions,
            }, CodexJsonContext.Default.ThreadResumeParams), ct), CodexJsonContext.Default.ThreadResumeResponse);
        }
        catch (JsonRpcException e)
        {
            throw Explain(e, id);
        }
        var t = new CodexThread(id, SettingsOf(resumed.Model, resumed.ReasoningEffort, resumed.ApprovalPolicy, resumed.Sandbox, resumed.ApprovalsReviewer));
        threads[id] = t;
        return t;
    }

    /// <summary>
    /// A thread another Codex app holds: refused with that app's name in the message, and no
    /// <c>data</c> (Zed shows it as raw JSON); clients have the holder from the list's <c>_meta</c>, the
    /// service from <c>_gorynych/open_elsewhere</c>.
    /// </summary>
    static JsonRpcException OpenElsewhere(string holder) =>
        new(JsonRpcException.InvalidRequest, $"This Codex thread is open in {holder}; close it there, then try again.");

    /// <summary>App-server's refusals to load a thread, in words a person and an asking agent can act on.</summary>
    static JsonRpcException Explain(JsonRpcException e, string id)
    {
        // The lock was taken after the check, or the holder could not be told (outside Windows).
        if (e.Message.Contains("active writer", StringComparison.Ordinal))
            return new JsonRpcException(e.Code,
                "This Codex thread is open in another Codex app (Codex Desktop, the CLI or VS Code); close it there, then try again.");
        if (e.Message.Contains("is archived", StringComparison.Ordinal))
            return new JsonRpcException(e.Code, $"This Codex thread is archived in Codex. Unarchive it there (codex unarchive {id}), then try again.");
        if (e.Message.Contains("no rollout found", StringComparison.Ordinal) || e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return new JsonRpcException(JsonRpcException.ResourceNotFound, $"Codex has no saved thread {id}: it never had a turn, or it was deleted.");
        return e;
    }

    /// <summary>Sends a thread's history from Codex, for a session the service has not journaled (made in Codex's own apps).</summary>
    async Task ReplayAsync(string id, CancellationToken ct)
    {
        string? cursor = null;
        for (var page = 0; page < 1000; page++)
        {
            var items = From(await app.SendRequestAsync(CodexMethods.Client.ThreadItemsList, ToNode(new ThreadItemsListParams
            {
                ThreadId = id,
                Limit = 100,
                Cursor = cursor is null ? null : JsonValue.Create(cursor),
            }, CodexJsonContext.Default.ThreadItemsListParams), ct), CodexJsonContext.Default.ThreadItemsListResponse);
            foreach (var entry in items.Data)
                if (ReplayOf(entry.Item) is { } update) await rpc.SendUpdateAsync(id, update);
            cursor = items.NextCursor;
            if (cursor is null) return;
        }
    }

    static SessionUpdate? ReplayOf(ThreadItem item) => item switch
    {
        UserMessageThreadItem u => new SessionUpdateUserMessageChunk { Content = new TextContent { Text = UserText(u) }, MessageId = u.ClientId ?? u.Id },
        AgentMessageThreadItem a when a.Text.Length > 0 => new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = a.Text }, MessageId = a.Id },
        ReasoningThreadItem r when ReasoningText(r) is { Length: > 0 } text =>
            new SessionUpdateAgentThoughtChunk { Content = new TextContent { Text = text }, MessageId = r.Id },
        _ => ToolCallOf(item, done: true),
    };

    async Task<JsonNode?> ListAsync(ListSessionsRequest p, CancellationToken ct)
    {
        var listed = From(await app.SendRequestAsync(CodexMethods.Client.ThreadList, ToNode(new ThreadListParams
        {
            Cwd = p.Cwd is null ? null : JsonValue.Create(p.Cwd),
            Cursor = p.Cursor,
            Limit = 50,
            Archived = false,
            SortKey = ThreadSortKey.UpdatedAt,
            UseStateDbOnly = true,
        }, CodexJsonContext.Default.ThreadListParams), ct), CodexJsonContext.Default.ThreadListResponse);
        // Read once for the page: the threads another Codex app has open, which this app-server cannot load.
        var held = holders(listed.Data.Select(th => th.Id));
        lockWatcher?.Saw(listed.Data.Select(th => th.Id), held);
        return AcpJson.ToNode(new ListSessionsResponse
        {
            Sessions = listed.Data.Select(th => new SessionInfo
            {
                SessionId = th.Id,
                Cwd = th.Cwd,
                Title = string.IsNullOrEmpty(th.Name) ? null : th.Name,
                UpdatedAt = Iso(th.UpdatedAt),
                Meta = held.TryGetValue(th.Id, out var holder) ? new JsonObject { [GorynychProtocol.MetaOpenElsewhere] = holder } : null,
            }).ToList(),
            NextCursor = listed.NextCursor,
        });
    }

    /// <summary>
    /// Closing stops the thread's turn (ACP), gives it a moment to end, then unloads the thread
    /// from app-server. A turn that has not ended by then ends as cancelled: after unsubscribing,
    /// no <c>turn/completed</c> comes for it.
    /// </summary>
    async Task CloseAsync(string id, CancellationToken ct)
    {
        if (!threads.TryGetValue(id, out var t) || !t.Loaded) return;
        await StopTurnAsync(t);
        try
        {
            await app.SendRequestAsync(CodexMethods.Client.ThreadUnsubscribe, ToNode(new ThreadUnsubscribeParams { ThreadId = id },
                CodexJsonContext.Default.ThreadUnsubscribeParams), ct);
        }
        finally
        {
            // Also when unsubscribing fails (app-server going away): the turn has had its time.
            t.Loaded = false;
            Abandon(t);
        }
    }

    async Task DeleteAsync(string id, CancellationToken ct)
    {
        threads.TryGetValue(id, out var t);
        if (t is not null) await StopTurnAsync(t);
        try
        {
            await app.SendRequestAsync(CodexMethods.Client.ThreadDelete, ToNode(new ThreadDeleteParams { ThreadId = id },
                CodexJsonContext.Default.ThreadDeleteParams), ct);
            threads.TryRemove(id, out _);
        }
        catch (JsonRpcException e)
        {
            throw Explain(e, id);
        }
        finally
        {
            if (t is not null)
            {
                t.Loaded = false;
                Abandon(t);
            }
        }
    }

    /// <summary>
    /// Stops the running turn and waits for it to end, all within <see cref="CodexAdapterOptions.TurnEndWait"/>:
    /// the interrupt is sent and not waited for, the turn's end is.
    /// </summary>
    async Task StopTurnAsync(CodexThread t)
    {
        _ = StopAsync(t);
        await TurnEndAsync(t);
    }

    /// <summary>Waits a little for a stopped turn to end (<see cref="CodexAdapterOptions.TurnEndWait"/>).</summary>
    async Task TurnEndAsync(CodexThread t)
    {
        if (t.Turn is not { } running) return;
        try
        {
            await running.Task.WaitAsync(options.TurnEndWait);
        }
        catch (TimeoutException)
        {
            Log($"codex {t.Id}: the stopped turn did not end in {options.TurnEndWait.TotalSeconds:0.#} s; it ends as cancelled");
        }
        catch (Exception e) when (e is JsonRpcException or OperationCanceledException)
        {
            // it ended, failed
        }
    }

    async Task<JsonNode?> PromptAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var p = request.ParamsAs<PromptRequest>();
        if (!threads.TryGetValue(p.SessionId, out var t) || !t.Loaded)
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Codex has not loaded session {p.SessionId}; resume it first.");
        var input = p.Prompt.Select(InputOf).OfType<UserInput>().ToList();
        if (input.Count == 0) throw new JsonRpcException(JsonRpcException.InvalidParams, "The prompt has no text.");
        var done = t.Begin(request.Sequence) ?? throw new JsonRpcException(JsonRpcException.InvalidRequest, "A turn of this session is still running.");
        try
        {
            // A cancel during turn/start is applied once Codex names the turn; turn/start itself is
            // always waited for, so no turn runs on that the adapter does not know.
            await using var stop = ct.Register(() => _ = StopAsync(t));
            var change = t.Pending;
            var start = new TurnStartParams
            {
                ThreadId = t.Id,
                Input = input,
                // The ID the service journals the text under: Codex keeps it with the message (clientId).
                ClientUserMessageId = (string?)p.Meta?[GorynychProtocol.MetaMessageId],
                // Only what the client changed: the rest stays as the thread has it.
                Model = change?.Model,
                Effort = change?.Effort,
            };
            if (change?.Mode is { } mode)
            {
                start.ApprovalPolicy = JsonValue.Create(mode.Policy);
                start.SandboxPolicy = t.SandboxFor(mode.Sandbox);
                start.ApprovalsReviewer = ApprovalsReviewer.Create(mode.Reviewer);
            }
            TurnStartResponse started;
            try
            {
                started = From(await app.SendRequestAsync(CodexMethods.Client.TurnStart, ToNode(start, CodexJsonContext.Default.TurnStartParams), CancellationToken.None),
                    CodexJsonContext.Default.TurnStartResponse);
            }
            catch (JsonRpcException e) when (change is not null)
            {
                t.Refused(change);
                await Update(t.Id, new ConfigOptionUpdate { ConfigOptions = ConfigOptions(t.Effective, models) });
                throw new JsonRpcException(e.Code, $"{e.Message} (Codex did not start the turn; the settings changed since the last turn were reset.)");
            }
            if (change is not null) t.Applied(change);
            Named(t, started.Turn.Id);
            return AcpJson.ToNode(new PromptResponse { StopReason = StopOf(await done.Task) });
        }
        finally
        {
            t.End(done);
        }
    }

    static UserInput? InputOf(ContentBlock block) => block switch
    {
        TextContent text => new TextUserInput { Text = text.Text },
        ResourceLink link => new TextUserInput { Text = $"@{link.Uri}" },
        _ => null,
    };

    static StopReason StopOf(Turn turn) => turn.Status.Value switch
    {
        "completed" => StopReason.EndTurn,
        "interrupted" => StopReason.Cancelled,
        _ when turn.Error?.CodexErrorInfo?.ToJsonString().Contains("contextWindowExceeded", StringComparison.OrdinalIgnoreCase) == true => StopReason.MaxTokens,
        _ => throw new JsonRpcException(JsonRpcException.InternalError, turn.Error?.Message is { Length: > 0 } m ? m : "Codex failed the turn."),
    };

    /// <summary>
    /// Stops the thread's running turn: interrupts it now when Codex has named it, else as soon as
    /// it does. <paramref name="sequence"/> is the place of a <c>session/cancel</c> in the ACP stream.
    /// </summary>
    async Task StopAsync(CodexThread t, long sequence = 0)
    {
        var (running, turnId) = t.Stop(sequence);
        if (turnId is not null) await InterruptAsync(t.Id, turnId);
        else if (running) Log($"codex {t.Id}: stop asked before Codex named the turn; it is interrupted once named");
    }

    /// <summary>Codex named the running turn; interrupts it when a stop came first. Never waits: app-server's notifications call it.</summary>
    void Named(CodexThread t, string turnId)
    {
        if (t.Named(turnId)) _ = InterruptAsync(t.Id, turnId);
    }

    /// <summary>Sends <c>turn/interrupt</c>, waiting for Codex's answer no longer than <see cref="CodexAdapterOptions.TurnEndWait"/>.</summary>
    async Task InterruptAsync(string threadId, string turnId)
    {
        try
        {
            using var limit = new CancellationTokenSource(options.TurnEndWait);
            await app.SendRequestAsync(CodexMethods.Client.TurnInterrupt, ToNode(new TurnInterruptParams { ThreadId = threadId, TurnId = turnId },
                CodexJsonContext.Default.TurnInterruptParams), limit.Token);
        }
        catch (Exception e) when (e is JsonRpcException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            Log($"codex {threadId}: interrupt: {e.Message}");
        }
    }

    /// <summary>
    /// A client's choice. It is held for the next turn and sent alone: a model or effort sends
    /// only that, a mode its approval policy, sandbox and reviewer. Values are checked against
    /// what Codex offers, so a held change cannot fail the turns after it.
    /// </summary>
    JsonNode? SetConfigOption(SetSessionConfigOptionRequest p)
    {
        if (p is not SetSessionConfigOptionRequestValueId { Value: var value })
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Codex's options are selects.");
        if (!threads.TryGetValue(p.SessionId, out var t)) throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Unknown session {p.SessionId}");
        var seen = t.Change((s, change) => p.ConfigId switch
        {
            // back to the thread's own settings
            "mode" when value == "custom" => change with { Mode = null },
            "mode" => change with { Mode = Modes.FirstOrDefault(m => m.Id == value) ?? throw new JsonRpcException(JsonRpcException.InvalidParams, $"No mode {value}") },
            "model" => change with
            {
                Model = models.Count == 0 || models.Any(m => m.Id == value) || value == s.Model
                    ? value : throw new JsonRpcException(JsonRpcException.InvalidParams, $"Codex has no model {value}"),
                Effort = models.FirstOrDefault(x => x.Id == value) is { } model && model.SupportedReasoningEfforts.All(e => e.ReasoningEffort != s.Effort)
                    ? model.DefaultReasoningEffort : change.Effort,
            },
            "reasoning_effort" => change with
            {
                Effort = value == s.Effort || models.FirstOrDefault(m => m.Id == s.Model) is not { } current
                    || current.SupportedReasoningEfforts.Any(e => e.ReasoningEffort == value)
                    ? value : throw new JsonRpcException(JsonRpcException.InvalidParams, $"{s.Model} has no reasoning effort {value}"),
            },
            _ => throw new JsonRpcException(JsonRpcException.InvalidParams, $"No option {p.ConfigId}"),
        });
        return AcpJson.ToNode(new SetSessionConfigOptionResponse { ConfigOptions = ConfigOptions(seen, models) });
    }

    /// <summary>The service's own MCP server, with the gorynych tools.</summary>
    const string PeersServerName = "gorynych";

    /// <summary>
    /// The thread's MCP servers as a Codex config override: the request's (the service's
    /// <c>gorynych</c> server, whose tools Codex runs without asking: docs/service-protocol.md,
    /// "Approvals"), plus the user's own <c>gorynych-outside</c> turned off for this thread when the
    /// user's Codex config has it. Overriding a server the config does not have fails the thread,
    /// so that one is checked first.
    /// </summary>
    JsonObject? McpConfig(List<McpServer> servers)
    {
        var mcp = new JsonObject();
        foreach (var server in servers)
            switch (server)
            {
                case McpServerStdio s:
                    var stdio = new JsonObject { ["command"] = s.Command, ["args"] = new JsonArray(s.Args.Select(a => (JsonNode?)a).ToArray()) };
                    if (s.Env.Count > 0) stdio["env"] = new JsonObject(s.Env.Select(e => KeyValuePair.Create(e.Name, (JsonNode?)e.Value)));
                    // The service asks the person itself where it must (a spawn, a session asking one in full access).
                    if (s.Name == PeersServerName) stdio["default_tools_approval_mode"] = "approve";
                    mcp[s.Name] = stdio;
                    break;
                case McpServerHttp h:
                    var http = new JsonObject { ["url"] = h.Url };
                    if (h.Headers.Count > 0) http["http_headers"] = new JsonObject(h.Headers.Select(x => KeyValuePair.Create(x.Name, (JsonNode?)x.Value)));
                    mcp[h.Name] = http;
                    break;
                default:
                    Log($"codex: MCP server {server.Tag} transport not supported; left out");
                    break;
            }
        if (userServers.Contains(options.OutsideServerName) && !mcp.ContainsKey(options.OutsideServerName))
            mcp[options.OutsideServerName] = new JsonObject { ["enabled"] = false };
        return mcp.Count == 0 ? null : new JsonObject { ["mcp_servers"] = mcp };
    }

    /// <summary>A <c>systemPrompt</c> as developer instructions; only <c>append</c> with text blocks (docs/adapter-contract.md, §6.2).</summary>
    static string? Instructions(SystemPrompt? prompt)
    {
        if (prompt is null) return null;
        if (prompt.Mode != SystemPromptMode.Append)
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"Codex takes systemPrompt only in append mode, not {prompt.Mode}.");
        if (prompt.Content.Count == 0 || prompt.Content.Any(b => b is not TextContent))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Codex takes systemPrompt as text blocks only.");
        return string.Join("\n\n", prompt.Content.OfType<TextContent>().Select(t => t.Text));
    }

    // ---------------------------------------------------------------- app-server's notifications

    CodexThread? Thread(string id) => threads.GetValueOrDefault(id);

    /// <summary>
    /// Codex says it unloaded a thread (<c>thread/closed</c>, status <c>notLoaded</c>). Such a note
    /// can come late, for an unload before the thread was opened again, so it is checked against
    /// <c>thread/loaded/list</c> first; only the instance it was about is marked. Never waits:
    /// app-server's notifications call it.
    /// </summary>
    void Unloading(CodexThread t, string what) => _ = Task.Run(async () =>
    {
        try
        {
            using var limit = new CancellationTokenSource(options.TurnEndWait);
            var loaded = await app.SendRequestAsync(CodexMethods.Client.ThreadLoadedList, new JsonObject(), limit.Token);
            if ((loaded?["data"] as JsonArray)?.Any(x => (string?)x == t.Id) == true)
            {
                Log($"codex {t.Id}: {what}, but Codex still has the thread loaded; ignored");
                return;
            }
        }
        catch (Exception e) when (e is JsonRpcException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            Log($"codex {t.Id}: {what}; loaded threads not read ({e.Message}), so taken as unloaded");
        }
        t.Loaded = false;
        if (t.Turn is not null) Log($"codex {t.Id}: Codex unloaded the thread during a turn; the turn ends as cancelled");
        Abandon(t);
    });

    /// <summary>Ends a thread's running turn here (<see cref="CodexThread.Abandon"/>) and notes it, as instances of a thread come and go.</summary>
    void Abandon(CodexThread t)
    {
        if (t.Abandon() is { Length: > 0 } turnId) abandoned[turnId] = 0;
    }

    Task Update(string threadId, SessionUpdate update) => rpc.SendUpdateAsync(threadId, update);

    async Task OnServerNotificationAsync(string method, JsonNode? p)
    {
        try
        {
            await OnServerNotificationCoreAsync(method, p);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
        {
            Log($"codex: {method}: {e.Message}");
        }
    }

    async Task OnServerNotificationCoreAsync(string method, JsonNode? p)
    {
        var ctx = CodexJsonContext.Default;
        switch (method)
        {
            case CodexMethods.Notification.TurnStarted:
            {
                var n = From(p, ctx.TurnStartedNotification);
                if (Thread(n.ThreadId) is { } t) Named(t, n.Turn.Id);
                break;
            }
            case CodexMethods.Notification.TurnCompleted:
            {
                var n = From(p, ctx.TurnCompletedNotification);
                if (abandoned.TryRemove(n.Turn.Id, out _)) break; // ended here already; not a later turn's end
                Thread(n.ThreadId)?.Completed(n.Turn);
                break;
            }
            case CodexMethods.Notification.ItemStarted:
            {
                var n = From(p, ctx.ItemStartedNotification);
                if (ToolCallOf(n.Item, done: false) is { } call)
                {
                    Thread(n.ThreadId)?.Started(n.Item, call.ToolCallId);
                    await Update(n.ThreadId, call);
                }
                break;
            }
            case CodexMethods.Notification.ItemCompleted:
            {
                var n = From(p, ctx.ItemCompletedNotification);
                var t = Thread(n.ThreadId);
                switch (n.Item)
                {
                    case UserMessageThreadItem:
                        break; // the client's own prompt: ACP does not echo it, and only the service starts turns here
                    case AgentMessageThreadItem a when a.Text.Length > 0 && t?.WasStreamed(a.Id) != true:
                        await Update(n.ThreadId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = a.Text }, MessageId = a.Id });
                        break;
                    case ReasoningThreadItem r when t?.WasStreamed(r.Id) != true && ReasoningText(r) is { Length: > 0 } text:
                        await Update(n.ThreadId, new SessionUpdateAgentThoughtChunk { Content = new TextContent { Text = text }, MessageId = r.Id });
                        break;
                    default:
                        if (ToolCallDoneOf(n.Item) is { } done)
                        {
                            t?.Finished(done.ToolCallId);
                            await Update(n.ThreadId, done);
                        }
                        break;
                }
                break;
            }
            case CodexMethods.Notification.ItemAgentMessageDelta:
            {
                var n = From(p, ctx.AgentMessageDeltaNotification);
                Thread(n.ThreadId)?.Streamed(n.ItemId);
                await Update(n.ThreadId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = n.Delta }, MessageId = n.ItemId });
                break;
            }
            case CodexMethods.Notification.ItemReasoningSummaryTextDelta:
            {
                var n = From(p, ctx.ReasoningSummaryTextDeltaNotification);
                Thread(n.ThreadId)?.Streamed(n.ItemId);
                await Update(n.ThreadId, new SessionUpdateAgentThoughtChunk { Content = new TextContent { Text = n.Delta }, MessageId = n.ItemId });
                break;
            }
            case CodexMethods.Notification.ItemReasoningTextDelta:
            {
                var n = From(p, ctx.ReasoningTextDeltaNotification);
                Thread(n.ThreadId)?.Streamed(n.ItemId);
                await Update(n.ThreadId, new SessionUpdateAgentThoughtChunk { Content = new TextContent { Text = n.Delta }, MessageId = n.ItemId });
                break;
            }
            case CodexMethods.Notification.ItemReasoningSummaryPartAdded:
            {
                var n = From(p, ctx.ReasoningSummaryPartAddedNotification);
                if (n.SummaryIndex > 0)
                    await Update(n.ThreadId, new SessionUpdateAgentThoughtChunk { Content = new TextContent { Text = "\n\n" }, MessageId = n.ItemId });
                break;
            }
            case CodexMethods.Notification.TurnPlanUpdated:
            {
                var n = From(p, ctx.TurnPlanUpdatedNotification);
                await Update(n.ThreadId, new Plan
                {
                    Entries = n.Plan.Select(s => new PlanEntry
                    {
                        Content = s.Step,
                        Priority = PlanEntryPriority.Medium,
                        Status = s.Status.Value switch
                        {
                            "completed" => PlanEntryStatus.Completed,
                            "inProgress" => PlanEntryStatus.InProgress,
                            _ => PlanEntryStatus.Pending,
                        },
                    }).ToList(),
                });
                break;
            }
            case CodexMethods.Notification.ThreadTokenUsageUpdated:
            {
                var n = From(p, ctx.ThreadTokenUsageUpdatedNotification);
                if (n.TokenUsage.ModelContextWindow is { } size && size > 0)
                    await Update(n.ThreadId, new UsageUpdate
                    {
                        Used = (ulong)Math.Max(0, n.TokenUsage.Last.InputTokens + n.TokenUsage.Last.OutputTokens),
                        Size = (ulong)size,
                    });
                break;
            }
            case CodexMethods.Notification.ThreadNameUpdated:
            {
                var n = From(p, ctx.ThreadNameUpdatedNotification);
                if (!string.IsNullOrEmpty(n.ThreadName))
                    await Update(n.ThreadId, new SessionInfoUpdate
                    {
                        Title = n.ThreadName,
                        UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    });
                break;
            }
            case CodexMethods.Notification.ThreadSettingsUpdated:
            {
                var n = From(p, ctx.ThreadSettingsUpdatedNotification);
                if (Thread(n.ThreadId) is not { } t) break;
                var s = n.ThreadSettings;
                t.Reported(SettingsOf(s.Model, s.Effort, s.ApprovalPolicy, s.SandboxPolicy, s.ApprovalsReviewer));
                await Update(n.ThreadId, new ConfigOptionUpdate { ConfigOptions = ConfigOptions(t.Effective, models) });
                break;
            }
            case CodexMethods.Notification.ThreadStatusChanged:
            {
                // Codex unloaded the thread: a turn still running ends, as no turn/completed comes for it.
                var n = From(p, ctx.ThreadStatusChangedNotification);
                if (n.Status is NotLoadedThreadStatus && Thread(n.ThreadId) is { Loaded: true } t) Unloading(t, "status notLoaded");
                break;
            }
            case CodexMethods.Notification.ThreadClosed:
            {
                var n = From(p, ctx.ThreadClosedNotification);
                if (Thread(n.ThreadId) is { Loaded: true } t) Unloading(t, "thread/closed");
                break;
            }
            case CodexMethods.Notification.ServerRequestResolved:
            {
                // Codex settled a request of its own accord (an interrupt): the question is withdrawn.
                var n = From(p, ctx.ServerRequestResolvedNotification);
                if (asking.TryRemove(n.RequestId.ToJsonString(), out var cts)) cts.Cancel();
                break;
            }
            case CodexMethods.Notification.Error:
            {
                var n = From(p, ctx.ErrorNotification);
                Log($"codex {n.ThreadId}: {n.Error.Message}{(n.WillRetry ? " (retrying)" : "")}");
                break;
            }
        }
    }

    // ---------------------------------------------------------------- app-server's requests

    async Task<JsonNode?> OnServerRequestAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var key = request.Id.ToJsonString();
        using var resolved = CancellationTokenSource.CreateLinkedTokenSource(ct);
        asking[key] = resolved;
        try
        {
            return request.Method switch
            {
                CodexMethods.Server.ItemCommandExecutionRequestApproval => await CommandApprovalAsync(request.Params, resolved.Token),
                CodexMethods.Server.ItemFileChangeRequestApproval => await FileChangeApprovalAsync(request.Params, resolved.Token),
                CodexMethods.Server.ItemPermissionsRequestApproval => await PermissionsApprovalAsync(request.Params, resolved.Token),
                CodexMethods.Server.McpServerElicitationRequest => await ElicitationAsync(request.Params, resolved.Token),
                CodexMethods.Server.ItemToolRequestUserInput => await UserInputAsync(request.Params, resolved.Token),
                _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Gorynych does not handle {request.Method}"),
            };
        }
        catch (OperationCanceledException) when (resolved.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Withdrawn: each request's own "nothing granted".
            return request.Method switch
            {
                CodexMethods.Server.McpServerElicitationRequest => new JsonObject { ["action"] = "cancel" },
                CodexMethods.Server.ItemToolRequestUserInput => new JsonObject { ["answers"] = new JsonObject() },
                CodexMethods.Server.ItemPermissionsRequestApproval => NoPermissions(),
                _ => new JsonObject { ["decision"] = "cancel" },
            };
        }
        finally
        {
            asking.TryRemove(key, out _);
        }
    }

    /// <summary>How long an approval waits for the item it is about (<see cref="CodexThread.ToolAsync"/>).</summary>
    static readonly TimeSpan ItemWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Asks the client. Returns the option chosen, null when the client cancelled, and the first
    /// "reject once" option for an option that was not offered.
    /// </summary>
    async Task<string?> AskAsync(string threadId, ToolCallUpdate call, List<PermissionOption> options, CancellationToken ct)
    {
        var answer = await rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission,
            new RequestPermissionRequest { SessionId = threadId, ToolCall = call, Options = options }, ct);
        if (answer.Outcome is not SelectedPermissionOutcome s) return null;
        if (options.Any(o => o.OptionId == s.OptionId)) return s.OptionId;
        var reject = options.FirstOrDefault(o => o.Kind == PermissionOptionKind.RejectOnce)?.OptionId;
        Log($"codex {threadId}: the client chose {s.OptionId}, which was not offered; answered {reject ?? "as cancelled"}");
        return reject;
    }

    async Task<JsonNode?> CommandApprovalAsync(JsonNode? @params, CancellationToken ct)
    {
        var p = From(@params, CodexJsonContext.Default.CommandExecutionRequestApprovalParams);
        var options = DecisionOptions(PlainDecisions(@params?["availableDecisions"] as JsonArray));
        // The request carries the command as Codex parsed it, as its item does; the item only when it does not.
        var item = p.CommandActions is null && Thread(p.ThreadId) is { } t ? await t.ToolAsync(p.ItemId, ItemWait, ct) as CommandExecutionThreadItem : null;
        var command = p.Command ?? item?.Command;
        var actions = p.CommandActions ?? item?.CommandActions ?? [];
        var chosen = await AskAsync(p.ThreadId, new ToolCallUpdate
        {
            ToolCallId = p.ItemId,
            Title = command is null && actions.Count == 0 ? "Run a command" : CommandTitle(command ?? "", actions),
            Kind = CommandKind(actions),
            Status = ToolCallStatus.Pending,
            RawInput = new JsonObject { ["command"] = command, ["cwd"] = p.Cwd, ["reason"] = p.Reason },
            Locations = p.Cwd is { Length: > 0 } cwd ? [new ToolCallLocation { Path = cwd }] : null,
        }, options, ct);
        return new JsonObject { ["decision"] = chosen ?? (options.Any(o => o.OptionId == "cancel") ? "cancel" : "decline") };
    }

    async Task<JsonNode?> FileChangeApprovalAsync(JsonNode? @params, CancellationToken ct)
    {
        var p = From(@params, CodexJsonContext.Default.FileChangeRequestApprovalParams);
        var options = DecisionOptions(["accept", "acceptForSession", "decline", "cancel"]);
        // The request names only the item, which says what changes.
        var change = Thread(p.ThreadId) is { } t ? await t.ToolAsync(p.ItemId, ItemWait, ct) as FileChangeThreadItem : null;
        var chosen = await AskAsync(p.ThreadId, new ToolCallUpdate
        {
            ToolCallId = p.ItemId,
            Title = change is null ? "Edit files" : FileChangeTitle(change),
            Kind = ToolKind.Edit,
            Status = ToolCallStatus.Pending,
            Locations = change?.Changes.Select(ch => new ToolCallLocation { Path = ch.Path }).ToList(),
            RawInput = new JsonObject { ["reason"] = p.Reason, ["grantRoot"] = p.GrantRoot },
        }, options, ct);
        return new JsonObject { ["decision"] = chosen ?? "cancel" };
    }

    async Task<JsonNode?> PermissionsApprovalAsync(JsonNode? @params, CancellationToken ct)
    {
        var p = From(@params, CodexJsonContext.Default.PermissionsRequestApprovalParams);
        var chosen = await AskAsync(p.ThreadId, new ToolCallUpdate
        {
            ToolCallId = p.ItemId,
            Title = p.Reason is { Length: > 0 } reason ? $"Allow more access: {reason}" : "Allow more access",
            Kind = ToolKind.Other,
            Status = ToolCallStatus.Pending,
            RawInput = @params?["permissions"]?.DeepClone(),
        },
        [
            Option("turn", "Allow for this turn", PermissionOptionKind.AllowOnce),
            Option("session", "Allow for this session", PermissionOptionKind.AllowAlways),
            Option("deny", "Deny", PermissionOptionKind.RejectOnce),
        ], ct);
        return chosen is "turn" or "session"
            ? new JsonObject { ["permissions"] = @params?["permissions"]?.DeepClone() ?? new JsonObject(), ["scope"] = chosen }
            : NoPermissions();
    }

    /// <summary>A permissions request's answer that grants nothing.</summary>
    static JsonObject NoPermissions() => new() { ["permissions"] = new JsonObject(), ["scope"] = "turn" };

    /// <summary>
    /// An MCP elicitation: Codex's approval of an MCP tool call (<c>codex_approval_kind:
    /// mcp_tool_call</c>) becomes an approval, offered once or for the session but never
    /// <c>persist: always</c>, which Codex saves beyond the session; an MCP server's own form
    /// becomes an ACP form; a URL elicitation is declined.
    /// </summary>
    async Task<JsonNode?> ElicitationAsync(JsonNode? @params, CancellationToken ct)
    {
        var mode = (string?)@params?["mode"];
        var threadId = (string?)@params?["threadId"] ?? "";
        var meta = @params?["_meta"] as JsonObject;
        if (mode == "form" && (string?)meta?["codex_approval_kind"] == "mcp_tool_call")
        {
            var server = (string?)@params?["serverName"] ?? "mcp";
            var tool = (string?)meta["tool_name"] ?? "tool";
            var persist = (meta["persist"] as JsonArray)?.Select(x => (string?)x).ToHashSet() ?? [];
            var options = new List<PermissionOption> { Option("accept", "Allow", PermissionOptionKind.AllowOnce) };
            if (persist.Contains("session")) options.Add(Option("session", "Allow for this session", PermissionOptionKind.AllowAlways));
            options.Add(Option("decline", "Deny", PermissionOptionKind.RejectOnce));
            var call = Thread(threadId) is { } t ? await t.McpCallAsync(server, tool, ItemWait, ct) : null;
            var chosen = await AskAsync(threadId, new ToolCallUpdate
            {
                ToolCallId = call ?? $"mcp-{server}-{tool}",
                Title = McpTitle(server, tool),
                Kind = ToolKind.Other,
                Status = ToolCallStatus.Pending,
                RawInput = meta["tool_params"]?.DeepClone(),
            }, options, ct);
            return chosen switch
            {
                "accept" => new JsonObject { ["action"] = "accept", ["content"] = new JsonObject() },
                "session" => new JsonObject { ["action"] = "accept", ["content"] = new JsonObject(), ["_meta"] = new JsonObject { ["persist"] = "session" } },
                _ => new JsonObject { ["action"] = "decline" },
            };
        }
        if (mode == "form" && AcpJson.FromNode<ElicitationSchema>(@params?["requestedSchema"]) is { } schema)
        {
            var answer = await rpc.RequestAsync<CreateElicitationRequest, CreateElicitationResponse>(AcpMethods.Client.ElicitationCreate,
                new ElicitationFormMode { SessionId = threadId, Message = (string?)@params?["message"] ?? "", RequestedSchema = schema }, ct);
            return answer switch
            {
                ElicitationAcceptAction a => new JsonObject
                {
                    ["action"] = "accept",
                    ["content"] = new JsonObject((a.Content ?? []).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))),
                },
                CreateElicitationResponseDecline => new JsonObject { ["action"] = "decline" },
                _ => new JsonObject { ["action"] = "cancel" },
            };
        }
        Log($"codex {threadId}: declined an MCP {mode} elicitation from {(string?)@params?["serverName"]}");
        return new JsonObject { ["action"] = "decline" };
    }

    /// <summary>Codex's <c>request_user_input</c> tool: one ACP form, a string per question (a choice where it has options).</summary>
    async Task<JsonNode?> UserInputAsync(JsonNode? @params, CancellationToken ct)
    {
        var p = From(@params, CodexJsonContext.Default.ToolRequestUserInputParams);
        var properties = new Dictionary<string, ElicitationPropertySchema?>();
        foreach (var q in p.Questions)
            properties[q.Id] = q.Options is { Count: > 0 } options && q.IsOther != true
                ? new StringPropertySchema
                {
                    Title = q.Header, Description = q.Question,
                    OneOf = options.Select(o => new EnumOption { Const = o.Label, Title = o.Label, Description = o.Description }).ToList(),
                }
                : new StringPropertySchema { Title = q.Header, Description = q.Question };
        var answer = await rpc.RequestAsync<CreateElicitationRequest, CreateElicitationResponse>(AcpMethods.Client.ElicitationCreate, new ElicitationFormMode
        {
            SessionId = p.ThreadId,
            ToolCallId = p.ItemId,
            Message = p.Questions.Count == 1 ? p.Questions[0].Question : "Codex asks you some questions",
            RequestedSchema = new ElicitationSchema { Type = ElicitationSchemaType.Object, Properties = properties, Required = properties.Keys.ToList() },
        }, ct);
        var answers = new JsonObject();
        if (answer is ElicitationAcceptAction { Content: { } content })
            foreach (var (id, value) in content)
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                    answers[id] = new JsonObject { ["answers"] = new JsonArray(s) };
        return new JsonObject { ["answers"] = answers };
    }

    // ---------------------------------------------------------------- lifetime

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var t in threads.Values)
            t.Fail(new JsonRpcException(JsonRpcException.InternalError, "Codex's app-server stopped during the turn."));
        foreach (var cts in asking.Values)
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        if (lockWatcher is { } watching) await watching.DisposeAsync();
        await rpc.DisposeAsync();
        await app.DisposeAsync();
        try { await appServer.DisposeAsync(); }
        catch (Exception e) { Log($"codex: stopping app-server: {e.Message}"); }
        exited.TrySetResult();
    }
}
