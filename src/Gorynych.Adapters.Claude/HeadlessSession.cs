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
/// life, started with per-launch flags only. Prompts and live notes are user messages on stdin,
/// each stamped with a <c>uuid</c>; the stream feeds ACP; approvals are <c>can_use_tool</c>
/// control requests (docs/adapters.md).
/// </summary>
/// <remarks>
/// Claude runs one turn at a time. A turn starts with the replay of the first message it takes,
/// or with output Claude produces on its own, and ends with one <c>result</c>. A message written
/// while a turn runs joins it (its replay comes before the result) or starts the next one. So a
/// client's prompt ends with the result of the turn that replayed its <c>uuid</c>, never with
/// another's.
/// </remarks>
internal sealed class HeadlessSession : IClaudeSession
{
    readonly ClaudeAgent agent;
    readonly ClaudeAdapterOptions options;
    readonly SemaphoreSlim promptGate = new(1, 1);
    readonly SemaphoreSlim launchGate = new(1, 1);
    readonly SemaphoreSlim inputGate = new(1, 1); // orders writes against interrupts
    readonly Lock stateGate = new();
    readonly CancellationTokenSource lifetime = new();
    ClaudeProcess? process;
    TranscriptWatcher? transcript;
    bool resume;
    bool disposed;
    string? pendingTitle;

    // Under stateGate.
    bool active;            // a turn runs in Claude
    bool activeOwn;         // ... that the client's prompt did not start (a live note's, or Claude's own)
    bool activeMarked;      // ... and its start was marked with gorynych/agentTurn
    readonly HashSet<string> taken = new(StringComparer.Ordinal);   // uuids the running turn replayed
    readonly List<string> pending = [];                              // written, not replayed yet
    readonly Dictionary<string, Note> notes = new(StringComparer.Ordinal);
    Turn? turn;                                                      // the client's prompt
    TaskCompletionSource idle = Completed();                         // no turn and nothing pending
    TaskCompletionSource cancelSignal = NewSignal();
    long generation;
    long lastOutput;
    string? planError;      // an auth or billing error of the running turn
    string? lastResultError;

    // Used only from the reader's handler.
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

    bool Busy => active || pending.Count > 0;

    /// <summary>Keeps <see cref="idle"/> in step with <see cref="Busy"/>; under stateGate.</summary>
    void UpdateIdle()
    {
        if (!Busy) idle.TrySetResult();
        else if (idle.Task.IsCompleted) idle = NewSignal();
    }

    // ---------------------------------------------------------------- prompts

    public async Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> prompt, Func<bool> cancelled, CancellationToken ct)
    {
        await promptGate.WaitAsync(ct);
        var current = new Turn(Guid.NewGuid().ToString());
        try
        {
            if (cancelled()) return StopReason.Cancelled;
            var claude = await EnsureRunningAsync(ct);
            // One turn at a time: wait until Claude is idle, and take the input in the same step.
            while (true)
            {
                Task wake;
                lock (stateGate)
                {
                    if (!Busy)
                    {
                        turn = current;
                        pending.Add(current.Uuid);
                        UpdateIdle();
                        break;
                    }
                    wake = Task.WhenAny(idle.Task, cancelSignal.Task);
                }
                await wake.WaitAsync(ct);
                if (cancelled()) return StopReason.Cancelled;
            }
            var written = false;
            await inputGate.WaitAsync(ct);
            try
            {
                lock (stateGate)
                {
                    if (current.CancelRequested || cancelled()) return StopReason.Cancelled;
                    current.Written = true;
                }
                lock (stateGate) lastOutput = Environment.TickCount64;
                try
                {
                    await claude.WriteAsync(StreamJson.UserMessage(current.Uuid, StreamJson.Content(prompt)), ct);
                    written = true;
                }
                catch (IOException) when (current.Task.IsCompleted)
                {
                    // Claude took the message and was stopped at once (off the plan): that is the news, not the closed pipe.
                    written = true;
                }
                catch (IOException e)
                {
                    throw new JsonRpcException(JsonRpcException.InternalError, $"Claude did not take the prompt: {e.Message} {StderrNote(claude)}");
                }
            }
            finally
            {
                inputGate.Release();
                if (!written) Withdraw(current);
            }
            ScheduleStallCheck();
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

    /// <summary>Takes back a prompt that was never written.</summary>
    void Withdraw(Turn current)
    {
        lock (stateGate)
        {
            pending.Remove(current.Uuid);
            if (ReferenceEquals(turn, current)) turn = null;
            UpdateIdle();
        }
    }

    public void Cancel()
    {
        Turn? current;
        bool written;
        lock (stateGate)
        {
            current = turn;
            if (current is not null) current.CancelRequested = true;
            written = current?.Written == true;
            var wake = cancelSignal;
            cancelSignal = NewSignal();
            wake.TrySetResult();
        }
        // Not written yet: the prompt sees the cancel and writes nothing.
        if (current is null || !written || current.Task.IsCompleted) return;
        _ = InterruptAsync(current);
    }

    /// <summary>Withdraws the approvals still open; Claude does not wait for them once its turn is over.</summary>
    void WithdrawAsks()
    {
        foreach (var id in asks.Keys.ToList())
            if (asks.TryRemove(id, out var cts))
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Stops the client's turn: <c>interrupt</c> with <c>cancel_queued</c>, after the prompt's own
    /// write (the input gate orders them), so a prompt Claude has not started yet is dropped too.
    /// The turn ends with its result; without one, Claude's process is stopped, and the next prompt
    /// resumes the conversation in a new one.
    /// </summary>
    async Task InterruptAsync(Turn current)
    {
        if (options.BeforeInterrupt is { } hook) await hook();
        if (process is not { Exited.IsCompleted: false } claude)
        {
            current.TryComplete(StopReason.Cancelled);
            return;
        }
        await inputGate.WaitAsync();
        try
        {
            // The turn may have ended while this waited; an interrupt now would stop the next prompt.
            lock (stateGate)
                if (!ReferenceEquals(turn, current) || current.Task.IsCompleted) return;
            var response = await claude.ControlAsync(new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = true }, TimeSpan.FromSeconds(5));
            // Probed with 2.1.302: {still_queued: [], cancelled: [uuids it dropped from the queue]}.
            foreach (var uuid in (response["cancelled"] as JsonArray)?.Select(n => (string?)n).OfType<string>() ?? [])
                Drop(uuid, error: null);
        }
        catch (Exception e) when (e is IOException or TimeoutException or ClaudeControlException or ObjectDisposedException)
        {
            agent.Log($"claude {Id}: interrupt failed: {e.Message}");
        }
        finally { inputGate.Release(); }
        WithdrawAsks();
        if (await Task.WhenAny(current.Task, Task.Delay(options.InterruptGraceMs)) == current.Task) return;
        current.TryComplete(StopReason.Cancelled);
        Abandon(claude, "no result after the interrupt; the next prompt resumes the session in a new process");
    }

    /// <summary>
    /// A written message Claude will not take: cancelled with the interrupt, never replayed, or lost
    /// with the process. A live note's waiter learns of it; the client's prompt ends with
    /// <paramref name="error"/>, or as cancelled without one.
    /// </summary>
    void Drop(string uuid, Exception? error)
    {
        Note? note;
        Turn? current;
        lock (stateGate)
        {
            pending.Remove(uuid);
            notes.Remove(uuid, out note);
            current = turn?.Uuid == uuid ? turn : null;
            UpdateIdle();
        }
        note?.Dropped.TrySetResult();
        if (current is null) return;
        if (error is null) current.TryComplete(StopReason.Cancelled);
        else current.TryFail(error);
    }

    /// <summary>
    /// When messages wait with no turn running and Claude stays silent for
    /// <see cref="ClaudeAdapterOptions.StallMs"/>, Claude is not going to take them: they are dropped,
    /// so nothing waits on them for ever.
    /// </summary>
    void ScheduleStallCheck()
    {
        _ = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try { await Task.Delay(options.StallMs, lifetime.Token); }
                catch (OperationCanceledException) { return; }
                List<string> stale;
                lock (stateGate)
                {
                    if (active || pending.Count == 0) return;
                    if (Environment.TickCount64 - lastOutput < options.StallMs) continue;
                    stale = pending.ToList();
                }
                var reason = lastResultError is { } error ? $" Claude's last error: {error}" : "";
                agent.Log($"claude {Id}: Claude took {stale.Count} message(s) in no turn; dropping them");
                foreach (var uuid in stale)
                    Drop(uuid, new JsonRpcException(JsonRpcException.InternalError, $"Claude did not take the prompt.{reason}"));
                return;
            }
        });
    }

    // ---------------------------------------------------------------- live notes

    /// <summary>
    /// Writes a message for the service (an opinion request or a report) into the running Claude:
    /// it joins a running turn after the current tool, or starts a turn of Claude's own. When
    /// Claude replays it, its <paramref name="meta"/> goes on the user message, with
    /// <c>gorynych/origin: "live"</c>. Not running: <c>-32600</c>, so the service asks in a turn.
    /// The returned task completes when Claude will not take the message after all.
    /// </summary>
    public async Task<Task> DeliverAsync(string text, JsonObject meta, CancellationToken ct)
    {
        if (process is not { Exited.IsCompleted: false } claude)
            throw new JsonRpcException(JsonRpcException.InvalidRequest, "The Claude session is not running.");
        var note = new Note(Guid.NewGuid().ToString(), text, meta);
        await inputGate.WaitAsync(ct);
        try
        {
            lock (stateGate)
            {
                notes[note.Uuid] = note;
                pending.Add(note.Uuid);
                lastOutput = Environment.TickCount64;
                UpdateIdle();
            }
            try { await claude.WriteAsync(StreamJson.UserMessage(note.Uuid, JsonValue.Create(text)!), ct); }
            catch
            {
                lock (stateGate)
                {
                    notes.Remove(note.Uuid);
                    pending.Remove(note.Uuid);
                    UpdateIdle();
                }
                throw;
            }
        }
        finally { inputGate.Release(); }
        ScheduleStallCheck();
        return note.Dropped.Task;
    }

    // ---------------------------------------------------------------- options and title

    public async Task<List<SessionConfigOption>> SetOptionAsync(string configId, string value, CancellationToken ct)
    {
        // Changed on a copy, and kept only once a running Claude took it.
        var next = Settings.Clone();
        var request = ClaudeOptions.Change(next, agent.Catalog, configId, value);
        if (process is { Exited.IsCompleted: false } claude)
        {
            try { await claude.ControlAsync(request, TimeSpan.FromSeconds(15), ct); }
            catch (Exception e) when (e is ClaudeControlException or IOException or TimeoutException)
            {
                throw new JsonRpcException(JsonRpcException.InternalError, $"Claude did not take the change: {e.Message}");
            }
        }
        Settings.CopyChoicesFrom(next);
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
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is { } previous)
            {
                if (!previous.Exited.IsCompleted) return previous;
                OnExited(previous, previous.Exited.Result); // whichever runs first resets the state
            }
            await agent.EnsureTrustedAsync(Id, Cwd, ct);
            var claude = Launch();
            try
            {
                JsonObject init;
                try { init = await claude.ControlAsync(new JsonObject { ["subtype"] = "initialize" }, TimeSpan.FromSeconds(options.StartupTimeoutSeconds), ct); }
                catch (Exception e) when (e is ClaudeControlException or TimeoutException or IOException)
                {
                    throw new JsonRpcException(JsonRpcException.InternalError,
                        $"Claude Code did not start: {e.Message}. {StderrNote(claude)}");
                }
                // Never run on without the person's plan login (docs/adapters.md, "Failing loudly").
                if (StreamJson.PlanProblem(init) is { } problem)
                    throw new JsonRpcException(JsonRpcException.InternalError, problem);
                Settings.ReportedMode = (string?)init["current_permission_mode"] ?? Settings.ReportedMode;
                agent.Log($"claude {Id}: Claude Code {(string?)init["claude_code_version"]}, {(string?)init["account"]?["subscriptionType"]}");
                await agent.OnCatalogAsync(this, ClaudeOptions.Catalog(init));
                string? title;
                lock (stateGate) { title = pendingTitle; pendingTitle = null; }
                if (title is not null) await RenameAsync(claude, title, ct);
                return claude;
            }
            catch
            {
                // Nothing runs that was not checked: a launch that failed, timed out or was cancelled is stopped.
                lock (stateGate) if (ReferenceEquals(process, claude)) process = null;
                await claude.StopAsync(TimeSpan.FromMilliseconds(options.CloseTimeoutMs));
                await claude.DisposeAsync();
                throw;
            }
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

        agent.Log($"claude {Id}: launching headless in {Cwd} ({(resume ? "resume" : "new")})");
        ClaudeProcess? started = null;
        // Lines of a process the session gave up (abandoned, or replaced) are dropped: they must not start turns or answer approvals.
        var claude = ClaudeProcess.Start(exe, args, Cwd, ClaudeEnvironment.Clean(headless: true), Path.Combine(Folder, "stderr.log"),
            m => IsCurrent(started!) ? OnMessageAsync(started!, m) : Task.CompletedTask, agent.Log);
        started = claude;
        lock (stateGate)
        {
            process = claude;
            active = activeOwn = activeMarked = false;
            taken.Clear();
            pending.Clear();
            notes.Clear();
            UpdateIdle();
        }
        _ = claude.Exited.ContinueWith(t => OnExited(claude, t.Result), TaskScheduler.Default);
        claude.BeginReading();
        transcript ??= new TranscriptWatcher(TranscriptTitles.ExpectedPath(Cwd, Id, options.ProjectsDirectory), title => _ = agent.OnTitleAsync(Id, title));
        return claude;
    }

    /// <summary>
    /// Gives up a process now, before it has exited: the session is reset at once, so the next
    /// prompt starts a new process instead of writing into one that is going away.
    /// </summary>
    void Abandon(ClaudeProcess claude, string why)
    {
        OnExited(claude, -1, why);
        claude.Kill();
    }

    /// <summary>Claude's process ended: nothing it was given will run, and the session is idle. Runs once per process.</summary>
    void OnExited(ClaudeProcess claude, int code, string? why = null)
    {
        bool own;
        List<Note> lost;
        List<CancellationTokenSource> withdrawn;
        lock (stateGate)
        {
            if (!ReferenceEquals(process, claude)) return;
            process = null;
            own = active && activeOwn && activeMarked;
            active = activeOwn = activeMarked = false;
            taken.Clear();
            pending.Clear();
            lost = notes.Values.ToList();
            notes.Clear();
            withdrawn = asks.Values.ToList();
            UpdateIdle();
        }
        agent.Log(why is null ? $"claude {Id}: exited with code {code}" : $"claude {Id}: stopped: {why}");
        foreach (var note in lost) note.Dropped.TrySetResult();
        foreach (var cts in withdrawn)
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        if (own) _ = MarkOwnTurnAsync("ended");
    }

    // ---------------------------------------------------------------- the stream

    bool IsCurrent(ClaudeProcess claude)
    {
        lock (stateGate) return ReferenceEquals(process, claude);
    }

    async Task OnMessageAsync(ClaudeProcess claude, JsonObject m)
    {
        lock (stateGate) lastOutput = Environment.TickCount64;
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
                if (StreamJson.PlanError(m) is { } plan) lock (stateGate) planError = plan;
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
                if (StreamJson.ApiKeySourceOfInit(m) is { } apiKey)
                {
                    StopOffThePlan(StreamJson.ApiKeyProblem(apiKey));
                    return;
                }
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
                _ = OnControlRequestAsync(claude, m);
                return;
            case "control_cancel_request":
                if ((string?)m["request_id"] is { } withdrawn && asks.TryRemove(withdrawn, out var cts))
                    try { cts.Cancel(); }
                    catch (ObjectDisposedException) { }
                return;
        }
    }

    /// <summary>A turn about to run on something other than the plan: the client's prompt fails, and Claude is stopped before it calls the API.</summary>
    void StopOffThePlan(string problem)
    {
        Turn? current;
        ClaudeProcess? claude;
        lock (stateGate) { current = turn; claude = process; }
        current?.TryFail(new JsonRpcException(JsonRpcException.InternalError, problem));
        if (claude is not null) Abandon(claude, problem);
    }

    /// <summary>Starts a turn in the state when none runs; under stateGate.</summary>
    void BeginTurn(bool own)
    {
        active = true;
        activeOwn = own;
        activeMarked = false;
        taken.Clear();
        generation++;
        UpdateIdle();
    }

    /// <summary>Output while no turn was counted: a turn Claude began itself (a background task ended, say).</summary>
    async Task TurnActivityAsync()
    {
        bool mark;
        lock (stateGate)
        {
            if (!active) BeginTurn(own: true);
            mark = activeOwn && !activeMarked;
            if (mark) activeMarked = true;
        }
        if (mark) await MarkOwnTurnAsync("started");
    }

    async Task OnReplayAsync(JsonObject m)
    {
        var uuid = (string?)m["uuid"] ?? "";
        Note? note;
        var mark = false;
        lock (stateGate)
        {
            pending.Remove(uuid);
            notes.Remove(uuid, out note);
            if (!active) BeginTurn(own: turn?.Uuid != uuid);
            taken.Add(uuid);
            if (note is not null)
            {
                mark = activeOwn && !activeMarked;
                if (mark) activeMarked = true;
            }
            UpdateIdle();
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
        bool own, waiting;
        string? plan;
        lock (stateGate)
        {
            // Only the turn that took the client's prompt ends it.
            current = turn is { } t && taken.Contains(t.Uuid) ? t : null;
            own = active && activeOwn && activeMarked;
            active = activeOwn = activeMarked = false;
            taken.Clear();
            generation++;
            plan = planError;
            planError = null;
            waiting = pending.Count > 0;
            UpdateIdle();
        }
        streamed.Clear();
        toolNames.Clear();
        WithdrawAsks(); // the turn is over: no approval of it is still awaited
        var error = StreamJson.ResultError(m);
        lock (stateGate) lastResultError = error;
        try
        {
            if (error is not null && current is not { CancelRequested: true })
            {
                // An auth or billing error is never left as a quiet note: the client's prompt fails with the way back.
                var note = plan is null ? $"[Claude stopped with an error: {error}]" : $"[Claude cannot run on your plan ({plan}): {error}] {StreamJson.TuiHint}";
                await EmitAsync(ClaudeHooks.TextChunk("\n" + note, Guid.NewGuid().ToString()));
                if (plan is not null) agent.Log($"claude {Id}: {note}");
            }
            if (own) await MarkOwnTurnAsync("ended");
            await agent.TouchAsync(Id);
        }
        finally
        {
            // Whatever the feed did, the prompt ends here.
            if (current is not null)
            {
                if (plan is not null && error is not null && !current.CancelRequested)
                    current.TryFail(new JsonRpcException(JsonRpcException.InternalError, $"Claude cannot run on your plan ({plan}): {error} {StreamJson.TuiHint}"));
                else current.TryComplete(StreamJson.StopReasonOf(m, current.CancelRequested));
            }
            if (waiting) ScheduleStallCheck();
        }
    }

    Task MarkOwnTurnAsync(string mark) => EmitAsync(new SessionInfoUpdate
    {
        UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        Meta = new JsonObject { [GorynychProtocol.MetaAgentTurn] = mark },
    });

    /// <summary>Sends an update to the client; a client that is gone never breaks the turn bookkeeping.</summary>
    async Task EmitAsync(SessionUpdate update)
    {
        try { await agent.EmitAsync(Id, update); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or JsonRpcException or OperationCanceledException or InvalidOperationException)
        {
            agent.Log($"claude {Id}: an update was not delivered: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- control requests from Claude

    async Task OnControlRequestAsync(ClaudeProcess claude, JsonObject m)
    {
        var requestId = (string?)m["request_id"] ?? "";
        var request = m["request"] as JsonObject ?? [];
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
        catch (Exception e)
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
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        asks[requestId] = cts;
        var toolCallId = (string?)request["tool_use_id"] ?? $"permission-{Interlocked.Increment(ref permissionCount)}";
        JsonObject answer;
        try
        {
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
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            agent.Log($"claude {Id}: the approval of {tool} was withdrawn");
            return; // Claude withdrew it, or Claude is gone: nobody waits for an answer
        }
        catch (Exception e)
        {
            // Claude must always get an answer, or its turn waits for ever.
            agent.Log($"claude {Id}: approval failed: {e.Message}");
            answer = StreamJson.Deny("Gorynych could not reach the user for approval.");
        }
        finally
        {
            asks.TryRemove(requestId, out _);
            cts.Dispose();
        }
        await claude.RespondAsync(requestId, answer);
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
        // No launch may start after this; one under way finishes first.
        var gated = await launchGate.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            disposed = true;
            lock (stateGate) turn?.TryComplete(StopReason.Cancelled);
            lifetime.Cancel();
            if (transcript is not null) await transcript.DisposeAsync();
            ClaudeProcess? running;
            lock (stateGate) running = process;
            if (running is not null)
            {
                await running.StopAsync(TimeSpan.FromMilliseconds(options.CloseTimeoutMs));
                await running.DisposeAsync();
            }
        }
        finally
        {
            if (gated) launchGate.Release();
        }
    }

    /// <summary>A message for the service written into Claude, waiting for its replay.</summary>
    sealed record Note(string Uuid, string Text, JsonObject Meta)
    {
        public TaskCompletionSource Dropped { get; } = NewSignal();
    }

    /// <summary>One prompt turn sent by the ACP client.</summary>
    sealed class Turn(string uuid)
    {
        readonly TaskCompletionSource<StopReason> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Uuid { get; } = uuid;
        public bool Written { get; set; }
        public bool CancelRequested { get; set; }
        public Task<StopReason> Task => done.Task;
        public void TryComplete(StopReason reason) => done.TrySetResult(reason);
        public void TryFail(Exception e) => done.TrySetException(e);
    }
}
