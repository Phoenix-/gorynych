using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex.Protocol;
using static Gorynych.Adapters.Codex.CodexMap;

namespace Gorynych.Adapters.Codex;

/// <summary>A Codex thread as the adapter knows it in this app-server process.</summary>
sealed class CodexThread(string id, CodexSettings settings)
{
    public string Id { get; } = id;
    public volatile bool Loaded = true;

    /// <summary>The thread's settings, and those changed since its last turn, which go with the next one.</summary>
    public CodexSettings Settings = settings;
    public CodexSettings? Pending;

    /// <summary>The running turn: its ID and its end.</summary>
    public string? TurnId;
    public TaskCompletionSource<Turn>? Turn;

    readonly Lock gate = new();
    readonly HashSet<string> streamed = [];
    readonly Dictionary<string, ThreadItem> tools = [];

    /// <summary>Notes that an item's text came as deltas, so its completed text is not sent again; true the first time.</summary>
    public bool Streamed(string itemId)
    {
        lock (gate) return streamed.Add(itemId);
    }

    public bool WasStreamed(string itemId)
    {
        lock (gate) return streamed.Contains(itemId);
    }

    public void Started(ThreadItem item, string id)
    {
        lock (gate) tools[id] = item;
    }

    public ThreadItem? Tool(string id)
    {
        lock (gate) return tools.GetValueOrDefault(id);
    }

    /// <summary>The MCP tool call of this server and tool started last, for its approval.</summary>
    public string? LastMcpCall(string server, string tool)
    {
        lock (gate) return tools.Values.OfType<McpToolCallThreadItem>().LastOrDefault(m => m.Server == server && m.Tool == tool)?.Id;
    }

    public void NewTurn()
    {
        lock (gate) { streamed.Clear(); tools.Clear(); }
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
    readonly ConcurrentDictionary<string, CodexThread> threads = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, CancellationTokenSource> asking = new(StringComparer.Ordinal);
    readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    List<Model> models = [];
    HashSet<string> userServers = [];
    string version = "";
    int disposed;

    public CodexAgent(CodexAdapterOptions options, JsonRpcConnection rpc, AgentInstance appServer)
    {
        this.options = options;
        this.rpc = rpc;
        this.appServer = appServer;
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
                return AcpJson.ToNode(new ResumeSessionResponse { ConfigOptions = ConfigOptions(t.Settings, models) });
            }
            case AcpMethods.Agent.SessionLoad:
            {
                var p = request.ParamsAs<LoadSessionRequest>();
                var t = await OpenAsync(p.SessionId, p.McpServers, p.SystemPrompt, ct);
                await ReplayAsync(p.SessionId, ct);
                return AcpJson.ToNode(new LoadSessionResponse { ConfigOptions = ConfigOptions(t.Settings, models) });
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
                return await PromptAsync(request.ParamsAs<PromptRequest>(), ct);
            case AcpMethods.Agent.SessionSetConfigOption:
                return SetConfigOption(request.ParamsAs<SetSessionConfigOptionRequest>());
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

    async Task OnNotificationAsync(string method, JsonNode? @params)
    {
        if (method == AcpMethods.Agent.SessionCancel && (string?)@params?["sessionId"] is { } id && threads.TryGetValue(id, out var t))
            await InterruptAsync(t);
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

    /// <summary>App-server's refusals to load a thread, in words a person and an asking agent can act on.</summary>
    static JsonRpcException Explain(JsonRpcException e, string id)
    {
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
        return AcpJson.ToNode(new ListSessionsResponse
        {
            Sessions = listed.Data.Select(th => new SessionInfo
            {
                SessionId = th.Id,
                Cwd = th.Cwd,
                Title = string.IsNullOrEmpty(th.Name) ? null : th.Name,
                UpdatedAt = Iso(th.UpdatedAt),
            }).ToList(),
            NextCursor = listed.NextCursor,
        });
    }

    /// <summary>Closing stops the thread's turn (ACP), then unloads it from app-server.</summary>
    async Task CloseAsync(string id, CancellationToken ct)
    {
        if (!threads.TryGetValue(id, out var t) || !t.Loaded) return;
        await InterruptAsync(t);
        await app.SendRequestAsync(CodexMethods.Client.ThreadUnsubscribe, ToNode(new ThreadUnsubscribeParams { ThreadId = id },
            CodexJsonContext.Default.ThreadUnsubscribeParams), ct);
        t.Loaded = false;
    }

    async Task DeleteAsync(string id, CancellationToken ct)
    {
        if (threads.TryGetValue(id, out var t)) await InterruptAsync(t);
        try
        {
            await app.SendRequestAsync(CodexMethods.Client.ThreadDelete, ToNode(new ThreadDeleteParams { ThreadId = id },
                CodexJsonContext.Default.ThreadDeleteParams), ct);
        }
        catch (JsonRpcException e)
        {
            throw Explain(e, id);
        }
        threads.TryRemove(id, out _);
    }

    async Task<JsonNode?> PromptAsync(PromptRequest p, CancellationToken ct)
    {
        if (!threads.TryGetValue(p.SessionId, out var t) || !t.Loaded)
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Codex has not loaded session {p.SessionId}; resume it first.");
        if (t.Turn is not null) throw new JsonRpcException(JsonRpcException.InvalidRequest, "A turn of this session is still running.");
        var input = p.Prompt.Select(InputOf).OfType<UserInput>().ToList();
        if (input.Count == 0) throw new JsonRpcException(JsonRpcException.InvalidParams, "The prompt has no text.");
        var start = new TurnStartParams
        {
            ThreadId = t.Id,
            Input = input,
            // The ID the service journals the text under: Codex keeps it with the message (clientId).
            ClientUserMessageId = (string?)p.Meta?[GorynychProtocol.MetaMessageId],
        };
        var changed = t.Pending;
        if (changed is not null)
        {
            start.ApprovalPolicy = JsonValue.Create(changed.ApprovalPolicy);
            start.SandboxPolicy = SandboxOf(changed.Sandbox);
            start.ApprovalsReviewer = ApprovalsReviewer.Create(changed.Reviewer);
            start.Model = changed.Model;
            start.Effort = changed.Effort;
        }
        var done = new TaskCompletionSource<Turn>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.NewTurn();
        t.Turn = done;
        try
        {
            var started = From(await app.SendRequestAsync(CodexMethods.Client.TurnStart, ToNode(start, CodexJsonContext.Default.TurnStartParams), ct),
                CodexJsonContext.Default.TurnStartResponse);
            t.TurnId ??= started.Turn.Id;
            if (changed is not null)
            {
                t.Settings = changed; // Codex keeps a turn's overrides for the turns after it
                t.Pending = null;
            }
            await using var stop = ct.Register(() => _ = InterruptAsync(t));
            return AcpJson.ToNode(new PromptResponse { StopReason = StopOf(await done.Task) });
        }
        finally
        {
            t.Turn = null;
            t.TurnId = null;
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

    async Task InterruptAsync(CodexThread t)
    {
        if (t.TurnId is not { } turnId || t.Turn is null) return;
        try
        {
            await app.SendRequestAsync(CodexMethods.Client.TurnInterrupt, ToNode(new TurnInterruptParams { ThreadId = t.Id, TurnId = turnId },
                CodexJsonContext.Default.TurnInterruptParams));
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            Log($"codex {t.Id}: interrupt: {e.Message}");
        }
    }

    JsonNode? SetConfigOption(SetSessionConfigOptionRequest p)
    {
        if (p is not SetSessionConfigOptionRequestValueId { Value: var value })
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Codex's options are selects.");
        if (!threads.TryGetValue(p.SessionId, out var t)) throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Unknown session {p.SessionId}");
        var s = t.Pending ?? t.Settings;
        s = p.ConfigId switch
        {
            "mode" when value == "custom" => s,
            "mode" => Modes.FirstOrDefault(m => m.Id == value) is { Id: not null } m
                ? s with { ApprovalPolicy = m.Policy, Sandbox = m.Sandbox, Reviewer = m.Reviewer }
                : throw new JsonRpcException(JsonRpcException.InvalidParams, $"No mode {value}"),
            "model" => s with
            {
                Model = value,
                Effort = models.FirstOrDefault(x => x.Id == value) is { } model && model.SupportedReasoningEfforts.All(e => e.ReasoningEffort != s.Effort)
                    ? model.DefaultReasoningEffort : s.Effort,
            },
            "reasoning_effort" => s with { Effort = value },
            _ => throw new JsonRpcException(JsonRpcException.InvalidParams, $"No option {p.ConfigId}"),
        };
        t.Pending = s == t.Settings ? null : s;
        return AcpJson.ToNode(new SetSessionConfigOptionResponse { ConfigOptions = ConfigOptions(s, models) });
    }

    /// <summary>
    /// The thread's MCP servers as a Codex config override: the request's (the service's
    /// <c>gorynych</c> server), plus the user's own <c>gorynych-outside</c> turned off for this
    /// thread when the user's Codex config has it. Overriding a server the config does not have
    /// fails the thread, so that one is checked first.
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
                if (Thread(n.ThreadId) is { } t) t.TurnId = n.Turn.Id;
                break;
            }
            case CodexMethods.Notification.TurnCompleted:
            {
                var n = From(p, ctx.TurnCompletedNotification);
                Thread(n.ThreadId)?.Turn?.TrySetResult(n.Turn);
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
                        if (ToolCallDoneOf(n.Item) is { } done) await Update(n.ThreadId, done);
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
                t.Settings = SettingsOf(s.Model, s.Effort, s.ApprovalPolicy, s.SandboxPolicy, s.ApprovalsReviewer);
                await Update(n.ThreadId, new ConfigOptionUpdate { ConfigOptions = ConfigOptions(t.Pending ?? t.Settings, models) });
                break;
            }
            case CodexMethods.Notification.ThreadStatusChanged:
            {
                var n = From(p, ctx.ThreadStatusChangedNotification);
                if (n.Status is NotLoadedThreadStatus && Thread(n.ThreadId) is { } t) t.Loaded = false;
                break;
            }
            case CodexMethods.Notification.ThreadClosed:
            {
                var n = From(p, ctx.ThreadClosedNotification);
                if (Thread(n.ThreadId) is { } t) t.Loaded = false;
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
            return request.Method == CodexMethods.Server.McpServerElicitationRequest || request.Method == CodexMethods.Server.ItemToolRequestUserInput
                ? new JsonObject { ["action"] = "cancel", ["answers"] = new JsonObject() }
                : new JsonObject { ["decision"] = "cancel" };
        }
        finally
        {
            asking.TryRemove(key, out _);
        }
    }

    async Task<string?> AskAsync(string threadId, ToolCallUpdate call, List<PermissionOption> options, CancellationToken ct)
    {
        var answer = await rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission,
            new RequestPermissionRequest { SessionId = threadId, ToolCall = call, Options = options }, ct);
        return answer.Outcome is SelectedPermissionOutcome s ? s.OptionId : null;
    }

    async Task<JsonNode?> CommandApprovalAsync(JsonNode? @params, CancellationToken ct)
    {
        var p = From(@params, CodexJsonContext.Default.CommandExecutionRequestApprovalParams);
        var options = DecisionOptions(PlainDecisions(@params?["availableDecisions"] as JsonArray));
        var started = Thread(p.ThreadId)?.Tool(p.ItemId) is CommandExecutionThreadItem c ? ToolCallOf(c, done: false) : null;
        var chosen = await AskAsync(p.ThreadId, new ToolCallUpdate
        {
            ToolCallId = p.ItemId,
            Title = started?.Title ?? p.Command ?? "Run a command",
            Kind = started?.Kind ?? ToolKind.Execute,
            Status = ToolCallStatus.Pending,
            RawInput = new JsonObject { ["command"] = p.Command, ["cwd"] = p.Cwd, ["reason"] = p.Reason },
        }, options, ct);
        return new JsonObject { ["decision"] = chosen ?? (options.Any(o => o.OptionId == "cancel") ? "cancel" : "decline") };
    }

    async Task<JsonNode?> FileChangeApprovalAsync(JsonNode? @params, CancellationToken ct)
    {
        var p = From(@params, CodexJsonContext.Default.FileChangeRequestApprovalParams);
        var options = DecisionOptions(["accept", "acceptForSession", "decline", "cancel"]);
        var change = Thread(p.ThreadId)?.Tool(p.ItemId) as FileChangeThreadItem;
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
            : new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" };
    }

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
            var chosen = await AskAsync(threadId, new ToolCallUpdate
            {
                ToolCallId = Thread(threadId)?.LastMcpCall(server, tool) ?? $"mcp-{server}-{tool}",
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
                ElicitationAcceptAction a => new JsonObject { ["action"] = "accept", ["content"] = AcpJson.ToNode(a.Content ?? []) },
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
            t.Turn?.TrySetException(new JsonRpcException(JsonRpcException.InternalError, "Codex's app-server stopped during the turn."));
        foreach (var cts in asking.Values)
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        await rpc.DisposeAsync();
        await app.DisposeAsync();
        try { await appServer.DisposeAsync(); }
        catch (Exception e) { Log($"codex: stopping app-server: {e.Message}"); }
        exited.TrySetResult();
    }
}
