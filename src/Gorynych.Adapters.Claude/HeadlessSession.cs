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
    /// <summary>The <c>--permission-mode</c> of the latest launch.</summary>
    string launchMode = "default";
    TranscriptWatcher? transcript;
    bool resume;
    bool disposed;
    string? pendingTitle;

    readonly SemaphoreSlim titleGate = new(1, 1);
    CancellationTokenSource? titleJob;
    Task? titleTask;
    readonly TaskCompletionSource<bool> titleReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool manualTitle;

    // Under stateGate.
    bool active;            // a turn runs in Claude
    bool activeOwn;         // ... that the client's prompt did not start (a live note's, or Claude's own)
    bool activeMarked;      // ... and its start was marked with gorynych/agentTurn
    bool ownOpen;           // ... its ACP turn is open across native result cycles (an accepted steer is pending)
    readonly HashSet<string> taken = new(StringComparer.Ordinal);   // uuids the running turn replayed
    readonly List<string> pending = [];                              // written, not replayed yet
    readonly Dictionary<string, Note> notes = new(StringComparer.Ordinal);
    readonly Dictionary<string, Steer> steers = new(StringComparer.Ordinal); // accepted, not replayed yet
    Turn? turn;                                                      // the client's prompt
    Turn? incoming;                                                  // its prompt is accepted, still starting
    TaskCompletionSource idle = Completed();                         // no turn and nothing pending
    TaskCompletionSource cancelSignal = NewSignal();
    long generation;
    long lastOutput;
    bool processUp;         // the current process said system/init: it is up, its MCP servers too
    string? authenticationError; // a native auth or billing error of the running turn
    string? lastResultError;

    // Used only from the reader's handler.
    string? messageId;
    readonly HashSet<string> streamed = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> toolNames = new(StringComparer.Ordinal);

    readonly ConcurrentDictionary<string, CancellationTokenSource> asks = new(StringComparer.Ordinal);
    int permissionCount;

    // Admissions of this session's prompts and steers in the ACP stream's order, before their
    // handlers run on the thread pool (docs/steering.md). A cancel after them waits for them.
    readonly Lock arrivalGate = new();
    readonly Dictionary<long, TaskCompletionSource> arrivals = [];

    public void NoteArrival(long sequence)
    {
        lock (arrivalGate) arrivals[sequence] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void AdmitArrival(long sequence)
    {
        TaskCompletionSource? signal;
        lock (arrivalGate) arrivals.Remove(sequence, out signal);
        signal?.TrySetResult();
    }

    /// <summary>Waits until every prompt or steer that arrived before <paramref name="sequence"/> has reached its admission point.</summary>
    public async Task WaitArrivalsBefore(long sequence, CancellationToken ct)
    {
        Task[] waiting;
        lock (arrivalGate) waiting = arrivals.Where(a => a.Key < sequence).Select(a => (Task)a.Value.Task).ToArray();
        if (waiting.Length > 0) await Task.WhenAll(waiting).WaitAsync(ct);
    }

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

    /// <summary>Claude's task checklist, reduced from its native task tools into ACP's plan.</summary>
    public ClaudeTaskPlan TaskPlan { get; } = new();

    /// <summary>Whether <see cref="TaskPlan"/> covers the whole native history (a replay or a lazy prime).</summary>
    public bool TasksPrimed { get; set; }

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

    public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> prompt, Func<bool> cancelled, CancellationToken ct, long arrivalSequence = 0) =>
        PromptCoreAsync(prompt, cancelled, ct, arrivalSequence, started: null);

    /// <summary>
    /// The shared extension's legacy idle start for a direct ACP caller that did not opt in: runs
    /// the message as an ordinary prompt turn, tracked like any other, and answers once it is handed
    /// to Claude. The conductor never takes this path: it always opts in.
    /// </summary>
    public async Task<JsonObject> StartIdleAsync(AcpSteering.Request request, CancellationToken ct)
    {
        var started = NewSignal();
        var run = PromptCoreAsync(request.Prompt, () => false, CancellationToken.None, 0, started);
        _ = run.ContinueWith(t =>
        {
            if (t.IsFaulted) started.TrySetException(t.Exception!.GetBaseException());
            else _ = t.Exception;
        }, TaskScheduler.Default);
        await started.Task.WaitAsync(ct);
        return AcpSteering.Result(AcpSteering.StartedNewTurn);
    }

    async Task<StopReason> PromptCoreAsync(IReadOnlyList<ContentBlock> prompt, Func<bool> cancelled, CancellationToken ct,
        long arrivalSequence, TaskCompletionSource? started)
    {
        await promptGate.WaitAsync(ct);
        var current = new Turn(Guid.NewGuid().ToString());
        lock (stateGate) incoming = current;
        AdmitArrival(arrivalSequence); // a steer behind it waits for its input write, not for this admission
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
                    // Claude took the message and failed at once: that is the news, not the closed pipe.
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
            if (written)
            {
                current.Admitted.TrySetResult();
                started?.TrySetResult();
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
            lock (stateGate)
            {
                if (ReferenceEquals(turn, current)) turn = null;
                if (ReferenceEquals(incoming, current)) incoming = null;
            }
            var notStarted = new JsonRpcException(JsonRpcException.InvalidRequest,
                "The prompt did not start, so the steering message was not delivered.");
            if (!current.Admitted.Task.IsCompleted)
                current.Admitted.TrySetException(notStarted);
            if (started is { Task.IsCompleted: false }) started.TrySetException(notStarted);
            promptGate.Release();
        }
    }

    /// <summary>
    /// Accepts a steer into the running turn (docs/steering.md, "Claude headless"): one user message
    /// on stdin with the service's <c>messageId</c> as its <c>uuid</c>, at Claude's default priority,
    /// written through the same input gate as prompts and live notes. A prompt still starting is
    /// waited for; its failure means the steer was not delivered. No turn at all answers
    /// <c>promptRequired</c>.
    /// </summary>
    public async Task<JsonObject> SteerAsync(AcpSteering.Request request, string messageId, long arrivalSequence, CancellationToken ct)
    {
        await WaitArrivalsBefore(arrivalSequence, ct);
        Turn? start;
        lock (stateGate) start = !active && incoming is { Written: false } ? incoming : null;
        if (start is not null)
        {
            try { await start.Admitted.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (JsonRpcException e)
            {
                throw new JsonRpcException(JsonRpcException.InvalidRequest, $"The prompt did not start, so the steering message was not delivered. {e.Message}");
            }
        }
        bool idle;
        lock (stateGate) idle = !active && turn is null && incoming is null;
        if (idle)
        {
            if (request.PromptRequired)
            {
                AdmitArrival(arrivalSequence);
                return AcpSteering.PromptRequiredResult();
            }
            var created = await StartIdleAsync(request, ct);
            AdmitArrival(arrivalSequence);
            return created;
        }
        ClaudeProcess? claude;
        Steer steer;
        await inputGate.WaitAsync(ct);
        try
        {
            lock (stateGate)
            {
                // Its prompt or its own turn may have ended while this waited.
                if (turn is not { Task.IsCompleted: false } && !active)
                {
                    AdmitArrival(arrivalSequence);
                    return AcpSteering.PromptRequiredResult();
                }
                claude = process is { Exited.IsCompleted: false } p ? p : null;
                if (claude is null)
                {
                    AdmitArrival(arrivalSequence);
                    return AcpSteering.PromptRequiredResult();
                }
                steer = new Steer(messageId, turn is { Task.IsCompleted: false } owner ? owner : null);
                steers[steer.Uuid] = steer;
                pending.Add(steer.Uuid);
                lastOutput = Environment.TickCount64;
                UpdateIdle();
            }
            try
            {
                await claude.WriteAsync(StreamJson.UserMessage(steer.Uuid, StreamJson.Content(request.Prompt)), CancellationToken.None);
            }
            catch
            {
                lock (stateGate)
                {
                    steers.Remove(steer.Uuid);
                    pending.Remove(steer.Uuid);
                    UpdateIdle();
                }
                throw;
            }
        }
        finally
        {
            inputGate.Release();
        }
        ScheduleStallCheck();
        AdmitArrival(arrivalSequence);
        return AcpSteering.Result(AcpSteering.Injected);
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

    public void Cancel(long arrivalSequence = 0)
    {
        Turn? current;
        bool written, run;
        long serial;
        lock (stateGate)
        {
            current = turn;
            if (current is not null) current.CancelRequested = true;
            written = current?.Written == true;
            // A turn of Claude's own has no client prompt: it is interrupted by its active state.
            run = active || written || ownOpen;
            serial = generation;
            var wake = cancelSignal;
            cancelSignal = NewSignal();
            wake.TrySetResult();
        }
        // Not written yet: the prompt sees the cancel and writes nothing.
        if (!run || current is { Task.IsCompleted: true }) return;
        _ = InterruptAfterArrivalsAsync(current, serial, arrivalSequence);
    }

    /// <summary>A cancel goes after the prompts and steers that arrived before it, so a steer before it is written first.</summary>
    async Task InterruptAfterArrivalsAsync(Turn? current, long serial, long arrivalSequence)
    {
        await WaitArrivalsBefore(arrivalSequence, CancellationToken.None);
        await InterruptAsync(current, serial);
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
    async Task InterruptAsync(Turn? current, long serial)
    {
        if (options.BeforeInterrupt is { } hook) await hook();
        if (process is not { Exited.IsCompleted: false } claude)
        {
            current?.TryComplete(StopReason.Cancelled);
            return;
        }
        await inputGate.WaitAsync();
        try
        {
            // The turn may have ended while this waited; an interrupt now would stop the next prompt.
            lock (stateGate)
            {
                if (current is not null)
                    if (!ReferenceEquals(turn, current) || current.Task.IsCompleted) return;
                if (current is null && ((!active && !ownOpen) || generation != serial)) return;
            }
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
        if (current is not null)
        {
            if (await Task.WhenAny(current.Task, Task.Delay(options.InterruptGraceMs)) == current.Task) return;
        }
        else
        {
            var ended = System.Diagnostics.Stopwatch.StartNew();
            while (ended.Elapsed < TimeSpan.FromMilliseconds(options.InterruptGraceMs))
            {
                lock (stateGate)
                    if (!active || generation != serial) return;
                await Task.Delay(50);
            }
        }
        // Given up before the turn ends, so a prompt sent as soon as the client reads the end starts a new process.
        Abandon(claude, "no result after the interrupt; the next prompt resumes the session in a new process");
        current?.TryComplete(StopReason.Cancelled);
    }

    /// <summary>
    /// A written message Claude will not take: cancelled with the interrupt, never replayed, or lost
    /// with the process. A live note's waiter learns of it; the client's prompt ends with
    /// <paramref name="error"/>, or as cancelled without one.
    /// </summary>
    void Drop(string uuid, Exception? error)
    {
        Note? note;
        Steer? steer;
        Turn? current;
        lock (stateGate)
        {
            pending.Remove(uuid);
            steers.Remove(uuid, out steer);
            notes.Remove(uuid, out note);
            current = turn?.Uuid == uuid ? turn : null;
            UpdateIdle();
        }
        note?.Dropped.TrySetResult();
        if (steer is not null)
        {
            // An accepted steer the cancel removed before Claude took it stays in Gorynych's
            // history; it only leaves this turn's pending settlement.
            if (error is not null) steer.Owner?.TryFail(error);
            else CompleteDeferredIfSettled();
            bool closeOwn;
            lock (stateGate)
            {
                closeOwn = steer.Owner is null && ownOpen && !active && !steers.Values.Any(s => s.Owner is null);
                if (closeOwn) ownOpen = false;
            }
            if (closeOwn) _ = MarkOwnTurnAsync("ended");
            return;
        }
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
                bool wasStarted;
                lock (stateGate)
                {
                    if (active || pending.Count == 0) return;
                    // Until it says system/init, which it does once its MCP servers are up, Claude is
                    // starting, not silent: that is bounded by the startup time. A startup failure
                    // must not come after the prompt was already failed for a stall.
                    var limit = processUp ? options.StallMs : Math.Max(options.StallMs, options.StartupTimeoutSeconds * 1000L);
                    if (Environment.TickCount64 - lastOutput < limit) continue;
                    stale = pending.ToList();
                    wasStarted = processUp;
                }
                var reason = lastResultError is { } error ? $" Claude's last error: {error}" : "";
                agent.Log($"claude {Id}: Claude took {stale.Count} message(s) in no turn{(wasStarted ? "" : ", and did not start")}; dropping them");
                foreach (var uuid in stale)
                    Drop(uuid, new JsonRpcException(JsonRpcException.InternalError, wasStarted
                        ? $"Claude did not take the prompt.{reason}"
                        : $"Claude did not take the prompt: it did not start within {options.StartupTimeoutSeconds} s.{reason}"));
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

    /// <summary>
    /// Applies an option. Changes take the launch gate, one at a time and never during a launch, and
    /// each works on a copy taken inside it, kept only once a running Claude took it.
    /// </summary>
    public async Task<List<SessionConfigOption>> SetOptionAsync(string configId, string value, CancellationToken ct)
    {
        // Full access needs a Claude started in it, so it waits for no turn: it is refused during one.
        var holdsPrompts = false;
        if (configId == "mode" && value == ClaudeOptions.FullAccess && ClaudeOptions.CurrentMode(Settings) != ClaudeOptions.FullAccess)
        {
            if (!await promptGate.WaitAsync(0, ct)) throw TurnRunning();
            holdsPrompts = true;
        }
        try
        {
            await launchGate.WaitAsync(ct);
            try
            {
                var next = Settings.Clone();
                var request = ClaudeOptions.Change(next, agent.Catalog, configId, value);
                var wasFull = ClaudeOptions.CurrentMode(Settings) == ClaudeOptions.FullAccess;
                var isFull = ClaudeOptions.CurrentMode(next) == ClaudeOptions.FullAccess;
                ClaudeProcess? running;
                bool busy;
                lock (stateGate)
                {
                    running = process is { Exited.IsCompleted: false } p ? p : null;
                    busy = Busy;
                }
                if (isFull && !wasFull)
                {
                    // Claude cannot enter bypassPermissions in a process started without it (only
                    // --allow-dangerously-skip-permissions would let it, never given): the next prompt starts it so.
                    if (running is not null)
                    {
                        if (busy) throw TurnRunning();
                        await RetireAsync(running, "stopped to start again in full access with the next prompt");
                    }
                }
                else if (running is not null)
                {
                    try { await running.ControlAsync(request, TimeSpan.FromSeconds(15), ct); }
                    catch (Exception e) when (e is ClaudeControlException or IOException or TimeoutException)
                    {
                        if (!(wasFull && !isFull)) throw new JsonRpcException(JsonRpcException.InternalError, $"Claude did not take the change: {e.Message}");
                        // Leaving Full access must hold before Claude's next tool call: a Claude that kept it is stopped.
                        Abandon(running, $"it did not leave full access ({e.Message})");
                    }
                }
                Settings.CopyChoicesFrom(next);
                return ClaudeOptions.ConfigOptions(Settings, agent.Catalog);
            }
            finally { launchGate.Release(); }
        }
        finally { if (holdsPrompts) promptGate.Release(); }
    }

    static JsonRpcException TurnRunning() => new(JsonRpcException.InvalidRequest, "A turn of this session is running; choose Full access once it has ended.");

    /// <summary>Lets a process go now and stops it: the session is reset at once, so the next prompt starts a new one.</summary>
    async Task RetireAsync(ClaudeProcess claude, string why)
    {
        OnExited(claude, -1, why);
        await claude.StopAsync(TimeSpan.FromMilliseconds(options.CloseTimeoutMs));
        await claude.DisposeAsync();
    }
    /// <summary>Writes Claude's own title (<c>custom-title</c>) through <c>rename_session</c>; waits for the next launch when Claude is not running.</summary>
    public async Task SetTitleAsync(string title, CancellationToken ct)
    {
        // The person's name wins, also over a generated title still in flight.
        CancellationTokenSource? before;
        lock (stateGate)
        {
            manualTitle = true;
            before = titleJob;
            titleJob = null;
        }
        try { before?.Cancel(); } catch (ObjectDisposedException) { }
        await titleGate.WaitAsync(ct);
        try
        {
            await WriteTitleAsync(title, ct);
        }
        finally
        {
            titleGate.Release();
        }
    }

    async Task<bool> WriteTitleAsync(string title, CancellationToken ct)
    {
        if (process is { Exited.IsCompleted: false } claude) return await RenameAsync(claude, title, ct);
        lock (stateGate) pendingTitle = title;
        return true;
    }

    async Task<bool> RenameAsync(ClaudeProcess claude, string title, CancellationToken ct)
    {
        try
        {
            await claude.ControlAsync(new JsonObject { ["subtype"] = "rename_session", ["title"] = title, ["source"] = "host", ["session_id"] = Id },
                TimeSpan.FromSeconds(15), ct);
            return true;
        }
        catch (Exception e) when (e is ClaudeControlException or IOException or TimeoutException)
        {
            agent.Log($"claude {Id}: rename failed: {e.Message}");
            lock (stateGate) pendingTitle ??= title;
            return false;
        }
    }

    // ---------------------------------------------------------------- generated title

    /// <summary>
    /// Starts an isolated no-tool title request for a session that still has only the service's
    /// first-prompt title. The answer is written as Claude's own <c>custom-title</c>; a person's
    /// rename meanwhile cancels it, and a request that fails leaves the provisional title.
    /// </summary>
    public Task GenerateTitleAsync(string input, string? provisional)
    {
        lock (stateGate)
        {
            if (manualTitle || disposed) return Task.CompletedTask;
            var before = titleJob;
            var job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            job.CancelAfter(options.TitleTimeout);
            titleJob = job;
            try { before?.Cancel(); } catch (ObjectDisposedException) { }
            titleTask = Task.Run(() => GenerateTitleCoreAsync(job, input, provisional));
        }
        return Task.CompletedTask;
    }

    async Task GenerateTitleCoreAsync(CancellationTokenSource job, string input, string? provisional)
    {
        try
        {
            // The service can ask before the first prompt starts. Never spend a model call
            // until the normal launch has passed its trust, native startup and permission checks.
            if (!await titleReady.Task.WaitAsync(job.Token)) return;
            await titleGate.WaitAsync(job.Token);
            try
            {
                if (job.IsCancellationRequested || IsNotCurrentTitleJob(job) || ManualTitle) return;
                // A name Claude or a person already wrote natively wins over a generated one.
                if (transcript is { Title: { Length: > 0 } }) return;
                var exe = options.Executable ?? ExecutableLocator.Find("claude");
                if (exe is null)
                {
                    agent.Log($"claude {Id}: no generated title: claude was not found on PATH");
                    return;
                }
                var model = Settings.Model ?? options.Model;
                await agent.TitleGenerationGate.WaitAsync(job.Token);
                string? title;
                try
                {
                    title = await ClaudeTitleGeneration.GenerateAsync(exe, options.DataDirectory, input, model,
                        options.TitleTimeout, options.TitleInputBytes, options.TitleMaxChars, agent.Log, job.Token);
                }
                finally { agent.TitleGenerationGate.Release(); }
                if (title is null || job.IsCancellationRequested || IsNotCurrentTitleJob(job) || ManualTitle || transcript is { Title: { Length: > 0 } })
                    return;
                if (!await WriteTitleAsync(title, job.Token)) return;
                agent.Log($"claude {Id}: generated title \"{title}\"{(provisional is null ? "" : $" (was \"{provisional}\")")}");
                await agent.OnTitleAsync(Id, title);
            }
            finally
            {
                titleGate.Release();
            }
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            agent.Log($"claude {Id}: no generated title: {e.Message}");
        }
        finally
        {
            lock (stateGate) if (ReferenceEquals(titleJob, job)) titleJob = null;
            job.Dispose();
        }
    }

    bool IsNotCurrentTitleJob(CancellationTokenSource job)
    {
        lock (stateGate) return !ReferenceEquals(titleJob, job);
    }

    bool ManualTitle
    {
        get { lock (stateGate) return manualTitle; }
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
                if (options.BeforeStartup is { } before) await before();
                JsonObject init;
                try { init = await claude.ControlAsync(new JsonObject { ["subtype"] = "initialize" }, TimeSpan.FromSeconds(options.StartupTimeoutSeconds), ct); }
                catch (Exception e) when (e is ClaudeControlException or TimeoutException or IOException)
                {
                    throw new JsonRpcException(JsonRpcException.InternalError,
                        $"Claude Code did not start: {e.Message}. {StderrNote(claude)}");
                }
                // Account metadata is informational. Claude owns authentication and reports its own failures.
                // Claude runs only in the mode it was started with: Full access exactly when the person
                // picked it (option changes wait for the launch, so the pick cannot change meanwhile).
                var started = (string?)init["current_permission_mode"];
                if (started == ClaudeOptions.FullAccess && launchMode != ClaudeOptions.FullAccess)
                    throw new JsonRpcException(JsonRpcException.InternalError,
                        $"Claude Code started in full access (bypassPermissions), which this session did not choose (it asked for {launchMode}); " +
                        "your Claude settings may set it. Gorynych does not run it so.");
                if (launchMode == ClaudeOptions.FullAccess && started is not null && started != ClaudeOptions.FullAccess)
                    throw new JsonRpcException(JsonRpcException.InternalError,
                        $"Claude Code started in {started} instead of full access (bypassPermissions); your Claude settings may turn it off " +
                        "(permissions.disableBypassPermissionsMode). Choose another mode, or allow it in Claude itself.");
                Settings.ReportedMode = (string?)init["current_permission_mode"] ?? Settings.ReportedMode;
                agent.Log($"claude {Id}: Claude Code {(string?)init["claude_code_version"]}");
                await agent.OnInitializeAsync(this, init);
                string? title;
                lock (stateGate) { title = pendingTitle; pendingTitle = null; }
                if (title is not null) await RenameAsync(claude, title, ct);
                titleReady.TrySetResult(true);
                return claude;
            }
            catch
            {
                titleReady.TrySetResult(false);
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
        launchMode = args[args.IndexOf("--permission-mode") + 1];
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
        var claude = ClaudeProcess.Start(exe, args, Cwd, ClaudeEnvironment.ForSession(), Path.Combine(Folder, "stderr.log"),
            m => IsCurrent(started!) ? OnMessageAsync(started!, m) : Task.CompletedTask, agent.Log);
        started = claude;
        lock (stateGate)
        {
            process = claude;
            processUp = false;
            active = activeOwn = activeMarked = false;
            ownOpen = false;
            taken.Clear();
            pending.Clear();
            notes.Clear();
            steers.Clear();
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
            own = ownOpen || (active && activeOwn && activeMarked);
            active = activeOwn = activeMarked = false;
            ownOpen = false;
            taken.Clear();
            pending.Clear();
            steers.Clear();
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
                if (StreamJson.AuthenticationError(m) is { } error) lock (stateGate) authenticationError = error;
                if (StreamJson.IsSubagent(m)) return;
                await TurnActivityAsync();
                var id = StreamJson.AssistantId(m);
                // Partial messages normally carry the text; a message streamed without them still shows.
                if ((id is null || !streamed.Contains(id)) && StreamJson.AssistantText(m) is { Length: > 0 } text)
                {
                    if (id is not null) streamed.Add(id);
                    await EmitAsync(ClaudeHooks.TextChunk(text, id ?? Guid.NewGuid().ToString()));
                }
                foreach (var (call, tool, input) in StreamJson.ToolUses(m))
                {
                    toolNames[call.ToolCallId] = tool;
                    var task = ClaudeTaskPlan.IsTool(tool);
                    if (task) await agent.EnsureTasksPrimedAsync(this);
                    // Without the whole native history the plan would be partial: no plan is better
                    // than one that drops tasks Claude still has.
                    if (!task || TasksPrimed) TaskPlan.ObserveToolUse(call.ToolCallId, tool, input);
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
                foreach (var detail in StreamJson.ToolResultDetails(m, id => toolNames.GetValueOrDefault(id)))
                {
                    var task = ClaudeTaskPlan.IsTool(detail.Tool);
                    if (task) await agent.EnsureTasksPrimedAsync(this);
                    await EmitAsync(StreamJson.ToolResultUpdate(detail));
                    if (task && TasksPrimed
                        && TaskPlan.ObserveToolResult(detail.ToolCallId, detail.Tool, detail.Failed, detail.Structured, detail.Text) is { } plan)
                        await EmitAsync(plan);
                }
                return;
            }
            case "system" when StreamJson.Subtype(m) == "init":
            {
                lock (stateGate) if (ReferenceEquals(process, claude)) processUp = true;
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
            mark = activeOwn && !activeMarked && !ownOpen;
            activeMarked = true;
            if (mark) ownOpen = true;
        }
        if (mark) await MarkOwnTurnAsync("started");
    }

    async Task OnReplayAsync(JsonObject m)
    {
        var uuid = (string?)m["uuid"] ?? "";
        Note? note;
        Steer? steer;
        var mark = false;
        lock (stateGate)
        {
            // Claude's record of a command it ran (set_model's "Set model to …"): no message it takes,
            // so no turn starts and no result comes. Counted as a turn, it would hold every later prompt.
            if (!pending.Contains(uuid) && StreamJson.UserText(m) is { } text && StreamJson.IsCommandRecord(text)) return;
            pending.Remove(uuid);
            steers.Remove(uuid, out steer);
            notes.Remove(uuid, out note);
            if (steer is not null)
            {
                // A steered message is part of its owning ACP turn: a client prompt's, or the
                // agent's own. It starts no new ACP turn and is never echoed as a user message.
                if (!active) BeginTurn(own: steer.Owner is null);
                taken.Add(uuid);
                if (steer.Owner is null)
                {
                    mark = activeOwn && !activeMarked && !ownOpen;
                    activeMarked = true;
                    if (mark) ownOpen = true;
                }
            }
            else
            {
                if (!active) BeginTurn(own: turn?.Uuid != uuid);
                taken.Add(uuid);
                if (note is not null)
                {
                    mark = activeOwn && !activeMarked && !ownOpen;
                    activeMarked = true;
                    if (mark) ownOpen = true;
                }
            }
            UpdateIdle();
        }
        resume = true; // the conversation now exists in Claude's own store
        if (steer is not null) return; // journaled by the conductor already: no echo
        if (note is null) return; // the client's own prompt: ACP does not echo it
        var meta = note.Meta.DeepClone().AsObject();
        meta[GorynychProtocol.MetaOrigin] = "live";
        if (mark) meta[GorynychProtocol.MetaAgentTurn] = "started";
        await EmitAsync(new SessionUpdateUserMessageChunk { Content = new TextContent { Text = note.Text }, MessageId = uuid, Meta = meta });
    }

    async Task OnResultAsync(JsonObject m)
    {
        Turn? current;
        bool own, waiting, deferred = false;
        string? authError;
        lock (stateGate)
        {
            // Only the turn that took the client's prompt, or one waiting for its accepted steers,
            // ends it. A later native cycle that took a pending steer stays part of that ACP turn.
            current = turn is { } t && (taken.Contains(t.Uuid) || t.Deferred is not null) ? t : null;
            own = active && activeOwn && activeMarked;
            active = activeOwn = activeMarked = false;
            taken.Clear();
            generation++;
            authError = authenticationError;
            authenticationError = null;
            var error = StreamJson.ResultError(m);
            var hasPending = steers.Values.Any(s => current is not null ? ReferenceEquals(s.Owner, current) : own && s.Owner is null);
            if (error is null && hasPending)
            {
                // Claude closed the native turn before taking an accepted steer; it remains part of
                // this ACP turn, and the next native cycle that takes it is waited for.
                if (current is not null) current.Deferred = StreamJson.StopReasonOf(m, current.CancelRequested);
                deferred = true;
            }
            else if (error is not null)
            {
                // A failed native turn ends the ACP turn now: accepted steers it did not take are resolved lost.
                if (own) DropSteers(null);
                else DropSteers(current);
            }
            if (!deferred && !hasPending) ownOpen = false;
            waiting = pending.Count > 0;
            UpdateIdle();
        }
        streamed.Clear();
        toolNames.Clear();
        WithdrawAsks(); // the turn is over: no approval of it is still awaited
        var errorText = StreamJson.ResultError(m);
        lock (stateGate) lastResultError = errorText;
        try
        {
            if (errorText is not null && current is not { CancelRequested: true })
            {
                // An auth or billing error is never left as a quiet note: the client's prompt fails with the way back.
                var note = authError is null ? $"[Claude stopped with an error: {errorText}]" : $"[Claude authentication or billing error ({authError}): {errorText}]";
                await EmitAsync(ClaudeHooks.TextChunk("\n" + note, Guid.NewGuid().ToString()));
                if (authError is not null) agent.Log($"claude {Id}: {note}");
            }
            bool closeOwn;
            lock (stateGate) closeOwn = own && !deferred && !steers.Values.Any(s => s.Owner is null);
            if (closeOwn) await MarkOwnTurnAsync("ended");
            await agent.TouchAsync(Id);
        }
        finally
        {
            // Whatever the feed did, the prompt ends here, unless it still waits for a later cycle.
            if (current is not null && !deferred)
            {
                var reason = StreamJson.StopReasonOf(m, current.CancelRequested);
                current.Deferred = null;
                if (authError is not null && errorText is not null && !current.CancelRequested)
                    current.TryFail(new JsonRpcException(JsonRpcException.InternalError, $"Claude authentication or billing error ({authError}): {errorText}"));
                else current.TryComplete(reason);
            }
            if (waiting) ScheduleStallCheck();
        }
    }

    /// <summary>Resolves accepted steers a failed or cancelled native turn will not take.</summary>
    void DropSteers(Turn? owner, Func<Steer, bool>? predicate = null)
    {
        List<string> removed;
        lock (stateGate)
        {
            removed = steers.Values.Where(s => predicate is null
                    ? (owner is not null ? ReferenceEquals(s.Owner, owner) : s.Owner is null)
                    : predicate(s))
                .Select(s => s.Uuid).ToList();
            foreach (var uuid in removed)
            {
                steers.Remove(uuid);
                pending.Remove(uuid);
            }
            if (removed.Count > 0) UpdateIdle();
        }
        if (removed.Count > 0) agent.Log($"claude {Id}: {removed.Count} accepted steer(s) ended with their turn before Claude took them");
    }

    /// <summary>A deferred client turn completes once no accepted steer attached to it is pending.</summary>
    void CompleteDeferredIfSettled()
    {
        Turn? done;
        StopReason reason;
        lock (stateGate)
        {
            var current = turn;
            if (current?.Deferred is not { } settled || current.Task.IsCompleted) return;
            if (steers.Values.Any(s => ReferenceEquals(s.Owner, current))) return;
            current.Deferred = null;
            done = current;
            reason = current.CancelRequested ? StopReason.Cancelled : settled;
        }
        done.TryComplete(reason);
    }

    async Task MarkOwnTurnAsync(string mark)
    {
        var meta = new JsonObject { [GorynychProtocol.MetaAgentTurn] = mark };
        // Its end carries the transcript's mark: the service's journal holds the turn now.
        if (mark == "ended" && (await agent.MarkMetaAsync(this))?[GorynychProtocol.MetaHistoryMark] is { } history) meta[GorynychProtocol.MetaHistoryMark] = history.DeepClone();
        await EmitAsync(new SessionInfoUpdate { UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Meta = meta });
    }

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
        // The gorynych tools need no approval (docs/service-protocol.md, "Approvals"): the launch's
        // allow rule covers them, but plan mode, or an ask rule, still asks for them here.
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
            Task? finishingTitle;
            lock (stateGate) finishingTitle = titleTask;
            if (finishingTitle is not null)
                try { await finishingTitle.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { agent.Log($"claude {Id}: title helper cleanup did not finish before close"); }
            if (transcript is not null) await transcript.DisposeAsync();
            ClaudeProcess? running;
            lock (stateGate) running = process;
            if (running is not null) await RetireAsync(running, "the session was closed");
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

    /// <summary>An accepted steer: written to Claude and waiting for its replay, which marks it taken.</summary>
    sealed class Steer(string uuid, Turn? owner)
    {
        public string Uuid { get; } = uuid;

        /// <summary>The client's ACP prompt it joined, or null for a turn of Claude's own.</summary>
        public Turn? Owner { get; } = owner;
    }

    /// <summary>One prompt turn sent by the ACP client.</summary>
    sealed class Turn(string uuid)
    {
        readonly TaskCompletionSource<StopReason> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Uuid { get; } = uuid;
        public bool Written { get; set; }

        /// <summary>The prompt's input write is done (or failed): a steer behind it may go in.</summary>
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancelRequested { get; set; }

        /// <summary>A native result came while accepted steers were still pending; the turn waits for them.</summary>
        public StopReason? Deferred { get; set; }

        public Task<StopReason> Task => done.Task;
        public void TryComplete(StopReason reason) => done.TrySetResult(reason);
        public void TryFail(Exception e) => done.TrySetException(e);
    }
}
