using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// One Claude Code session driven headless: the unmodified <c>claude</c> in its documented
/// non-interactive mode (<c>claude -p</c>, stream-json in and out), one process for the session's
/// life, started with per-launch flags only. Prompts and live notes are user messages on stdin;
/// the stream feeds ACP; approvals are <c>can_use_tool</c> control requests (docs/adapters.md).
/// </summary>
internal sealed class HeadlessSession : IClaudeSession
{
    readonly ClaudeAgent agent;
    readonly ClaudeAdapterOptions options;
    readonly SemaphoreSlim promptGate = new(1, 1);
    readonly SemaphoreSlim launchGate = new(1, 1);
    readonly Lock stateGate = new();
    readonly CancellationTokenSource lifetime = new();
    ClaudeProcess? process;
    TranscriptWatcher? transcript;
    bool resume;
    string? pendingTitle;

    // The turn Claude runs, under stateGate. One `result` ends each turn; a message written while
    // a turn runs joins it unless its replay comes only after the result, which makes it the next.
    bool busy;
    TaskCompletionSource idle = Completed();
    TaskCompletionSource cancelSignal = NewSignal();
    Turn? turn;
    bool ownTurn;   // a turn the client's prompt did not start: a live note's, or one Claude began itself
    bool ownMarked; // its start was marked with gorynych/agentTurn
    string? planError; // an auth or billing error of this turn's assistant messages
    readonly Dictionary<string, Note> notes = new(StringComparer.Ordinal);
    readonly HashSet<string> unconsumed = new(StringComparer.Ordinal); // written, not replayed yet

    // Stream state, used only from the reader's handler.
    string? messageId;
    readonly HashSet<string> streamed = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> toolNames = new(StringComparer.Ordinal);

    readonly ConcurrentDictionary<string, CancellationTokenSource> asks = new(StringComparer.Ordinal);
    int permissionCount;

    public HeadlessSession(ClaudeAgent agent, ClaudeAdapterOptions options, string id, string cwd, bool resume)
    {
        this.agent = agent;
        this.options = options;
        Id = id;
        Cwd = cwd;
        this.resume = resume;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }

    public string Id { get; }
    public string Cwd { get; }
    public string Token { get; }
    public List<McpServer> McpServers { get; set; } = [];
    public string? SystemPrompt { get; set; }

    /// <summary>The person's choices for this session and what Claude reported last.</summary>
    public ClaudeSettings Settings { get; } = new();

    public bool IsRunning => process is { Exited.IsCompleted: false };

    string Folder => Path.Combine(options.DataDirectory, "sessions", Id);

    // ---------------------------------------------------------------- prompts

    public async Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> prompt, Func<bool> cancelled, CancellationToken ct)
    {
        await promptGate.WaitAsync(ct);
        Turn? current = null;
        try
        {
            if (cancelled()) return StopReason.Cancelled;
            // One turn at a time: wait while one runs (a live note's, or one Claude began itself).
            while (true)
            {
                Task wake;
                lock (stateGate)
                {
                    if (!busy) break;
                    wake = Task.WhenAny(idle.Task, cancelSignal.Task);
                }
                await wake.WaitAsync(ct);
                if (cancelled()) return StopReason.Cancelled;
            }
            var claude = await EnsureRunningAsync(ct);
            if (cancelled()) return StopReason.Cancelled;
            current = new Turn();
            var uuid = Guid.NewGuid().ToString();
            lock (stateGate)
            {
                busy = true;
                idle = NewSignal();
                turn = current;
                ownTurn = false;
                ownMarked = false;
                unconsumed.Add(uuid);
            }
            await claude.WriteAsync(StreamJson.UserMessage(uuid, StreamJson.Content(prompt)), ct);
            await using var registration = ct.Register(() => current.TryComplete(StopReason.Cancelled));
            if (await Task.WhenAny(current.Task, claude.Exited) != current.Task)
            {
                // The result may still be on its way when the process exits right after it.
                await Task.WhenAny(current.Task, Task.Delay(1500));
                if (!current.Task.IsCompleted)
                    throw new JsonRpcException(JsonRpcException.InternalError,
                        $"Claude exited with code {claude.Exited.Result} before the turn ended. {StderrNote(claude)}");
            }
            return await current.Task;
        }
        finally
        {
            lock (stateGate) if (ReferenceEquals(turn, current)) turn = null;
            promptGate.Release();
        }
    }

    public void Cancel()
    {
        Turn? current;
        lock (stateGate)
        {
            current = turn;
            if (current is not null) current.CancelRequested = true;
            var wake = cancelSignal;
            cancelSignal = NewSignal();
            wake.TrySetResult();
        }
        if (current is null || current.Task.IsCompleted) return;
        if (process is { Exited.IsCompleted: false } claude) _ = InterruptAsync(claude, current);
        else current.TryComplete(StopReason.Cancelled);
    }

    async Task InterruptAsync(ClaudeProcess claude, Turn current)
    {
        try { await claude.ControlAsync(new JsonObject { ["subtype"] = "interrupt" }, TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is IOException or TimeoutException or ClaudeControlException or ObjectDisposedException)
        {
            agent.Log($"claude {Id}: interrupt failed: {e.Message}");
        }
        // The turn ends with its result; should none come, it ends here.
        if (await Task.WhenAny(current.Task, Task.Delay(options.InterruptGraceMs)) == current.Task) return;
        agent.Log($"claude {Id}: no result after the interrupt; the turn ends as cancelled");
        current.TryComplete(StopReason.Cancelled);
        lock (stateGate)
        {
            if (!ReferenceEquals(turn, current) && turn is not null) return;
            busy = false;
            ownTurn = false;
            unconsumed.Clear();
            idle.TrySetResult();
        }
    }

    // ---------------------------------------------------------------- live notes

    /// <summary>
    /// Writes a message for the service (an opinion request or a report) into the running Claude:
    /// it joins a running turn after the current tool, or starts a turn of Claude's own. When
    /// Claude replays it, its <paramref name="meta"/> goes on the user message, with
    /// <c>gorynych/origin: "live"</c>. Not running: <c>-32600</c>, so the service asks in a turn.
    /// </summary>
    public async Task DeliverAsync(string text, JsonObject meta, CancellationToken ct)
    {
        if (process is not { Exited.IsCompleted: false } claude)
            throw new JsonRpcException(JsonRpcException.InvalidRequest, "The Claude session is not running.");
        var uuid = Guid.NewGuid().ToString();
        lock (stateGate)
        {
            notes[uuid] = new Note(text, meta);
            unconsumed.Add(uuid);
            if (!busy)
            {
                busy = true;
                idle = NewSignal();
                ownTurn = true;
                ownMarked = false;
            }
        }
        try { await claude.WriteAsync(StreamJson.UserMessage(uuid, JsonValue.Create(text)!), ct); }
        catch
        {
            lock (stateGate)
            {
                notes.Remove(uuid);
                unconsumed.Remove(uuid);
            }
            throw;
        }
    }

    // ---------------------------------------------------------------- options and title

    public async Task<List<SessionConfigOption>> SetOptionAsync(string configId, string value, CancellationToken ct)
    {
        var request = ClaudeOptions.Change(Settings, agent.Catalog, configId, value);
        if (process is { Exited.IsCompleted: false } claude)
        {
            try { await claude.ControlAsync(request, TimeSpan.FromSeconds(15), ct); }
            catch (Exception e) when (e is ClaudeControlException or IOException or TimeoutException)
            {
                throw new JsonRpcException(JsonRpcException.InternalError, $"Claude did not take the change: {e.Message}");
            }
        }
        return ClaudeOptions.ConfigOptions(Settings, agent.Catalog);
    }

    /// <summary>Writes Claude's own title (<c>custom-title</c>) through <c>rename_session</c>; waits for the next launch when Claude is not running.</summary>
    public async Task SetTitleAsync(string title, CancellationToken ct)
    {
        if (process is { Exited.IsCompleted: false } claude) await RenameAsync(claude, title, ct);
        else lock (stateGate) pendingTitle = title;
    }

    async Task RenameAsync(ClaudeProcess claude, string title, CancellationToken ct)
    {
        try
        {
            await claude.ControlAsync(new JsonObject { ["subtype"] = "rename_session", ["title"] = title, ["source"] = "host", ["session_id"] = Id },
                TimeSpan.FromSeconds(15), ct);
        }
        catch (Exception e) when (e is ClaudeControlException or IOException or TimeoutException)
        {
            agent.Log($"claude {Id}: rename failed: {e.Message}");
            lock (stateGate) pendingTitle ??= title;
        }
    }

    // ---------------------------------------------------------------- launch

    async Task<ClaudeProcess> EnsureRunningAsync(CancellationToken ct)
    {
        await launchGate.WaitAsync(ct);
        try
        {
            if (process is { Exited.IsCompleted: false } running) return running;
            await agent.EnsureTrustedAsync(Id, Cwd, ct);
            var claude = Launch();
            JsonObject? init = null;
            try { init = await claude.ControlAsync(new JsonObject { ["subtype"] = "initialize" }, TimeSpan.FromSeconds(options.StartupTimeoutSeconds), ct); }
            catch (Exception e) when (e is ClaudeControlException or TimeoutException or IOException)
            {
                // initialize only brings the catalog; the session works without it.
                agent.Log($"claude {Id}: initialize failed: {e.Message}");
                if (claude.Exited.IsCompleted)
                    throw new JsonRpcException(JsonRpcException.InternalError,
                        $"Claude exited with code {claude.Exited.Result} as it started. {StderrNote(claude)}");
            }
            if (StreamJson.SignedOut(init))
            {
                // Never run on without the person's login: should `-p` become bare by default, it
                // would read no OAuth login (nor hooks, CLAUDE.md or the user's MCP servers).
                await claude.StopAsync(TimeSpan.FromMilliseconds(options.CloseTimeoutMs));
                throw new JsonRpcException(JsonRpcException.InternalError,
                    "Claude Code started without your login (its account shows no plan and no token). Sign in with `claude` in a " +
                    "terminal; if you are signed in, `claude -p` may now run bare by default, which ignores the login, so start the " +
                    "Gorynych service with --claude-transport tui.");
            }
            if (StreamJson.ApiKeySource(init) is { } apiKey)
            {
                // An API key wins over the login in `claude -p`: the session would be billed outside the plan.
                await claude.StopAsync(TimeSpan.FromMilliseconds(options.CloseTimeoutMs));
                throw new JsonRpcException(JsonRpcException.InternalError,
                    $"Claude Code would run on {apiKey}, billed outside your plan. Gorynych runs Claude on your own plan login only: " +
                    $"remove {apiKey} from the environment the Gorynych service starts with.");
            }
            if (init is not null)
            {
                Settings.ReportedMode = (string?)init["current_permission_mode"] ?? Settings.ReportedMode;
                agent.Log($"claude {Id}: Claude Code {(string?)init["claude_code_version"]}, {(string?)init["account"]?["subscriptionType"] ?? "no plan"}");
                await agent.OnCatalogAsync(this, ClaudeOptions.Catalog(init));
            }
            string? title;
            lock (stateGate) { title = pendingTitle; pendingTitle = null; }
            if (title is not null) await RenameAsync(claude, title, ct);
            return claude;
        }
        finally { launchGate.Release(); }
    }

    ClaudeProcess Launch()
    {
        var exe = options.Executable ?? ExecutableLocator.Find("claude")
            ?? throw new JsonRpcException(JsonRpcException.InternalError, "claude was not found on PATH");
        Directory.CreateDirectory(Folder);
        var settingsPath = Path.Combine(Folder, "settings.json");
        File.WriteAllText(settingsPath, StreamJson.Settings().ToJsonString(AcpJson.TextOptions));

        var args = new List<string>(StreamJson.BaseArgs) { "--settings", settingsPath };
        args.AddRange(ClaudeOptions.LaunchArgs(Settings, options.PermissionMode, options.Model));
        var mcpPath = Path.Combine(Folder, "mcp.json");
        var channel = agent.Channel;
        if (channel is not null)
        {
            File.WriteAllText(mcpPath, channel.McpConfig(Id, Token, McpServers, live: false).ToJsonString(AcpJson.TextOptions));
            args.AddRange(["--mcp-config", mcpPath]);
        }
        else if (McpServers.Count > 0)
        {
            File.WriteAllText(mcpPath, Claude.McpServers.Config(McpServers).ToJsonString(AcpJson.TextOptions));
            args.AddRange(["--mcp-config", mcpPath]);
        }
        var appended = string.Join("\n\n", new[] { channel is null ? null : ChannelHost.LiveSystemPrompt, SystemPrompt }
            .Where(t => !string.IsNullOrEmpty(t)));
        if (appended.Length > 0) args.AddRange(["--append-system-prompt", appended]);
        args.AddRange(resume ? ["--resume", Id] : ["--session-id", Id]);

        var previous = process;
        process = null;
        if (previous is not null) _ = previous.DisposeAsync().AsTask();
        agent.Log($"claude {Id}: launching headless in {Cwd} ({(resume ? "resume" : "new")})");
        var claude = ClaudeProcess.Start(exe, args, Cwd, ClaudeEnvironment.Clean(), Path.Combine(Folder, "stderr.log"),
            m => OnMessageAsync(m), agent.Log);
        process = claude;
        _ = claude.Exited.ContinueWith(t => OnExited(claude, t.Result), TaskScheduler.Default);
        transcript ??= new TranscriptWatcher(TranscriptTitles.ExpectedPath(Cwd, Id, options.ProjectsDirectory), title => _ = agent.OnTitleAsync(Id, title));
        return claude;
    }

    void OnExited(ClaudeProcess claude, int code)
    {
        agent.Log($"claude {Id}: exited with code {code}");
        bool own;
        List<CancellationTokenSource> pendingAsks;
        lock (stateGate)
        {
            if (!ReferenceEquals(process, claude)) return;
            own = ownTurn && ownMarked;
            busy = false;
            ownTurn = false;
            ownMarked = false;
            unconsumed.Clear();
            notes.Clear();
            idle.TrySetResult();
            pendingAsks = asks.Values.ToList();
        }
        foreach (var cts in pendingAsks) cts.Cancel();
        if (own) _ = MarkOwnTurnAsync("ended");
    }

    // ---------------------------------------------------------------- the stream

    async Task OnMessageAsync(JsonObject m)
    {
        switch (StreamJson.Type(m))
        {
            case "stream_event":
            {
                if (StreamJson.IsSubagent(m)) return;
                if (StreamJson.MessageStartId(m) is { } started)
                {
                    messageId = started;
                    return;
                }
                if (StreamJson.Delta(m, messageId) is not { } delta) return;
                await TurnActivityAsync();
                if (delta is SessionUpdateAgentMessageChunk && messageId is not null) streamed.Add(messageId);
                await EmitAsync(delta);
                return;
            }
            case "assistant":
            {
                if (StreamJson.PlanError(m) is { } planError) lock (stateGate) this.planError = planError;
                if (StreamJson.IsSubagent(m)) return;
                await TurnActivityAsync();
                var id = StreamJson.AssistantId(m);
                // Partial messages normally carry the text; a message streamed without them still shows.
                if ((id is null || !streamed.Contains(id)) && StreamJson.AssistantText(m) is { Length: > 0 } text)
                {
                    if (id is not null) streamed.Add(id);
                    await EmitAsync(ClaudeHooks.TextChunk(text, id ?? Guid.NewGuid().ToString()));
                }
                foreach (var (call, tool, _) in StreamJson.ToolUses(m))
                {
                    toolNames[call.ToolCallId] = tool;
                    await EmitAsync(call);
                }
                return;
            }
            case "user":
            {
                if (StreamJson.IsSubagent(m)) return;
                if (StreamJson.IsReplay(m))
                {
                    await OnReplayAsync(m);
                    return;
                }
                foreach (var update in StreamJson.ToolResults(m, id => toolNames.GetValueOrDefault(id)))
                    await EmitAsync(update);
                return;
            }
            case "system" when StreamJson.Subtype(m) == "init":
            {
                var model = (string?)m["model"];
                var mode = (string?)m["permissionMode"];
                if (model == Settings.ReportedModel && mode == Settings.ReportedMode) return;
                Settings.ReportedModel = model;
                Settings.ReportedMode = mode;
                await agent.OnSettingsReportedAsync(this);
                return;
            }
            case "result":
                await OnResultAsync(m);
                return;
            case "control_request":
                _ = OnControlRequestAsync(m);
                return;
            case "control_cancel_request":
                if ((string?)m["request_id"] is { } withdrawn && asks.TryRemove(withdrawn, out var cts)) cts.Cancel();
                return;
        }
    }

    /// <summary>Output while no turn was counted: a turn Claude began itself (a background task ended, say).</summary>
    async Task TurnActivityAsync()
    {
        bool mark;
        lock (stateGate)
        {
            if (!busy)
            {
                busy = true;
                idle = NewSignal();
                ownTurn = true;
                ownMarked = false;
            }
            mark = ownTurn && !ownMarked;
            if (mark) ownMarked = true;
        }
        if (mark) await MarkOwnTurnAsync("started");
    }

    async Task OnReplayAsync(JsonObject m)
    {
        var uuid = (string?)m["uuid"] ?? "";
        Note? note;
        bool mark = false;
        lock (stateGate)
        {
            unconsumed.Remove(uuid);
            if (notes.Remove(uuid, out note))
            {
                if (!busy)
                {
                    busy = true;
                    idle = NewSignal();
                    ownTurn = true;
                    ownMarked = false;
                }
                mark = ownTurn && !ownMarked;
                if (mark) ownMarked = true;
            }
        }
        resume = true; // the conversation now exists in Claude's own store
        if (note is null) return; // the client's own prompt: ACP does not echo it
        var meta = note.Meta.DeepClone().AsObject();
        meta[GorynychProtocol.MetaOrigin] = "live";
        if (mark) meta[GorynychProtocol.MetaAgentTurn] = "started";
        await EmitAsync(new SessionUpdateUserMessageChunk { Content = new TextContent { Text = note.Text }, MessageId = uuid, Meta = meta });
    }

    async Task OnResultAsync(JsonObject m)
    {
        Turn? current;
        bool own, next;
        string? plan;
        lock (stateGate)
        {
            current = turn;
            own = ownTurn && ownMarked;
            // A message written during this turn that it did not take starts the next one.
            next = unconsumed.Count > 0;
            busy = next;
            ownTurn = next;
            ownMarked = false;
            plan = planError;
            planError = null;
        }
        var error = StreamJson.ResultError(m);
        if (error is not null && current is not { CancelRequested: true })
        {
            // An auth or billing error is never left as a quiet note: the client's prompt fails with the way back.
            var note = plan is null ? $"[Claude stopped with an error: {error}]" : $"[Claude cannot run on your plan ({plan}): {error}] {StreamJson.TuiHint}";
            await EmitAsync(ClaudeHooks.TextChunk("\n" + note, Guid.NewGuid().ToString()));
            if (plan is not null) agent.Log($"claude {Id}: {note}");
        }
        if (own) await MarkOwnTurnAsync("ended");
        await agent.TouchAsync(Id);
        if (current is not null)
        {
            if (plan is not null && error is not null && !current.CancelRequested)
                current.TryFail(new JsonRpcException(JsonRpcException.InternalError, $"Claude cannot run on your plan ({plan}): {error} {StreamJson.TuiHint}"));
            else current.TryComplete(StreamJson.StopReasonOf(m, current.CancelRequested));
        }
        lock (stateGate) if (!next) idle.TrySetResult();
    }

    Task MarkOwnTurnAsync(string mark) => EmitAsync(new SessionInfoUpdate
    {
        UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        Meta = new JsonObject { [GorynychProtocol.MetaAgentTurn] = mark },
    });

    async Task EmitAsync(SessionUpdate update)
    {
        try { await agent.EmitAsync(Id, update); }
        catch (IOException) { }
    }

    // ---------------------------------------------------------------- control requests from Claude

    async Task OnControlRequestAsync(JsonObject m)
    {
        var requestId = (string?)m["request_id"] ?? "";
        var request = m["request"] as JsonObject ?? [];
        var claude = process;
        if (claude is null) return;
        try
        {
            switch ((string?)request["subtype"])
            {
                case "can_use_tool":
                    await PermissionAsync(claude, requestId, request);
                    break;
                case "elicitation":
                    // MCP servers' forms are not passed on yet; Claude takes a decline as "no".
                    agent.Log($"claude {Id}: declined an elicitation from {(string?)request["mcp_server_name"]}");
                    await claude.RespondAsync(requestId, new JsonObject { ["action"] = "decline" });
                    break;
                default:
                    await claude.RespondErrorAsync(requestId, $"Gorynych does not handle {(string?)request["subtype"]}");
                    break;
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            agent.Log($"claude {Id}: could not answer {(string?)request["subtype"]}: {e.Message}");
        }
    }

    async Task PermissionAsync(ClaudeProcess claude, string requestId, JsonObject request)
    {
        var tool = (string?)request["tool_name"] ?? "";
        var input = request["input"] as JsonObject ?? [];
        // Answering an opinion request, looking who is there and asking to spawn (the service asks
        // the person itself) need no approval; asking another agent does (docs/service-protocol.md).
        if (ChannelHost.PreAllowedTools.Contains(tool))
        {
            await claude.RespondAsync(requestId, StreamJson.Allow(input));
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        asks[requestId] = cts;
        var toolCallId = (string?)request["tool_use_id"] ?? $"permission-{Interlocked.Increment(ref permissionCount)}";
        try
        {
            JsonObject answer;
            if (tool == "AskUserQuestion")
            {
                if (!agent.ClientHasForms)
                    answer = StreamJson.Deny("The user's client cannot show questions; ask in plain text instead.");
                else
                {
                    var response = await agent.ElicitAsync(ClaudeHooks.QuestionForm(Id, toolCallId, input), cts.Token);
                    answer = ClaudeHooks.AnsweredInput(input, response) is { } answered
                        ? StreamJson.Allow(answered)
                        : StreamJson.Deny("The user chose not to answer.");
                }
            }
            else
            {
                var hookLike = new JsonObject { ["tool_name"] = tool, ["tool_input"] = input.DeepClone() };
                var decision = await agent.RequestPermissionAsync(ClaudeHooks.Permission(Id, toolCallId, hookLike), cts.Token);
                if (ClaudeHooks.IsAllowed(decision.Outcome)) answer = StreamJson.Allow(input);
                else
                {
                    await EmitAsync(ClaudeHooks.ToolCallStatusUpdate(toolCallId, ToolCallStatus.Failed, "Denied in Gorynych."));
                    answer = StreamJson.Deny("The user denied this tool call in Gorynych.");
                }
            }
            await claude.RespondAsync(requestId, answer);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            agent.Log($"claude {Id}: the approval of {tool} was withdrawn");
        }
        catch (Exception e) when (e is IOException or JsonRpcException)
        {
            agent.Log($"claude {Id}: approval failed: {e.Message}");
            try { await claude.RespondAsync(requestId, StreamJson.Deny("Gorynych could not reach the user for approval.")); }
            catch (IOException) { }
        }
        finally { asks.TryRemove(requestId, out _); }
    }

    static string StderrNote(ClaudeProcess claude) =>
        claude.StderrTail is { Length: > 0 } tail ? "Claude said: " + (tail.Length <= 600 ? tail : "…" + tail[^600..]) : "";

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static TaskCompletionSource Completed()
    {
        var done = NewSignal();
        done.TrySetResult();
        return done;
    }

    public async ValueTask DisposeAsync()
    {
        lock (stateGate) turn?.TryComplete(StopReason.Cancelled);
        lifetime.Cancel();
        if (transcript is not null) await transcript.DisposeAsync();
        var running = process;
        process = null;
        if (running is not null) await running.StopAsync(TimeSpan.FromMilliseconds(options.CloseTimeoutMs));
        if (running is not null) await running.DisposeAsync();
    }

    /// <summary>A message for the service written into Claude, waiting for its replay.</summary>
    sealed record Note(string Text, JsonObject Meta);

    /// <summary>One prompt turn sent by the ACP client.</summary>
    sealed class Turn
    {
        readonly TaskCompletionSource<StopReason> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancelRequested { get; set; }
        public Task<StopReason> Task => done.Task;
        public void TryComplete(StopReason reason) => done.TrySetResult(reason);
        public void TryFail(Exception e) => done.TrySetException(e);
    }
}
