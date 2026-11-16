using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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
    TaskCompletionSource<string>? named;
    CancellationTokenSource? localCommand;
    string? turnId;
    bool stopAsked;
    long cancelledAt;

    readonly SemaphoreSlim titleGate = new(1, 1);
    CancellationTokenSource? titleJob;
    bool manualTitle;

    readonly HashSet<string> streamed = [];
    readonly Dictionary<string, ThreadItem> tools = [];
    readonly HashSet<string> claimed = [];
    TaskCompletionSource itemStarted = Signal();
    readonly Dictionary<long, TaskCompletionSource> arrivals = []; // prompts and steers, before their handlers

    public CodexThread(string id, string cwd, CodexSettings settings)
    {
        Id = id;
        Cwd = cwd;
        this.settings = settings;
        Remember(settings.Sandbox);
    }

    public string Id { get; }
    public string Cwd { get; }
    public volatile bool Loaded = true;
    readonly CancellationTokenSource backgroundStop = new();
    public Task? BackgroundTask { get; set; }
    public CancellationToken BackgroundToken => backgroundStop.Token;
    public void StopBackground() { try { backgroundStop.Cancel(); } catch (ObjectDisposedException) { } }
    public void DisposeBackground() => backgroundStop.Dispose();

    // ---------------------------------------------------------------- generated titles

    /// <summary>The gate a generated title and a manual rename take, so the later one wins.</summary>
    public SemaphoreSlim TitleGate => titleGate;

    /// <summary>The person named the thread: a generated title must no longer be written.</summary>
    public bool ManualTitle
    {
        get { lock (gate) return manualTitle; }
    }

    /// <summary>
    /// Cancels the running title generation and installs <paramref name="next"/> (null to leave none).
    /// <paramref name="manual"/> when the person is naming the thread now: later completions are stale.
    /// </summary>
    public CancellationTokenSource? ReplaceTitleJob(CancellationTokenSource? next, bool manual = false)
    {
        CancellationTokenSource? before;
        lock (gate)
        {
            before = titleJob;
            titleJob = next;
            if (manual) manualTitle = true;
        }
        try { before?.Cancel(); } catch (ObjectDisposedException) { }
        return before;
    }

    /// <summary>Whether <paramref name="job"/> is still the session's running generation.</summary>
    public bool IsTitleJob(CancellationTokenSource job)
    {
        lock (gate) return ReferenceEquals(titleJob, job);
    }

    /// <summary>Forgets <paramref name="job"/> if it is still the running one.</summary>
    public void EndTitleJob(CancellationTokenSource job)
    {
        lock (gate) if (ReferenceEquals(titleJob, job)) titleJob = null;
    }

    /// <summary>Stops any title generation, for a manual rename, a close, a delete or shutdown.</summary>
    public void CancelTitleJob(bool manual = false) => ReplaceTitleJob(null, manual);

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
            named = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            turnId = null;
            stopAsked = cancelledAt > sequence;
            streamed.Clear();
            tools.Clear();
            claimed.Clear();
            return turn;
        }
    }

    public CancellationToken LocalCancellation(CancellationToken ct)
    {
        lock (gate)
        {
            if (turn is null || localCommand is not null) throw new InvalidOperationException("No local command can start now.");
            localCommand = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (stopAsked) localCommand.Cancel();
            return localCommand.Token;
        }
    }

    /// <summary>Codex named the running turn (<c>turn/started</c> or <c>turn/start</c>'s answer); true when a stop was asked before.</summary>
    public bool Named(string id)
    {
        lock (gate)
        {
            if (turn is null || turnId is not null) return false;
            turnId = id;
            named?.TrySetResult(id);
            return stopAsked;
        }
    }

    /// <summary>The running turn's name, or a failure once it ends, is abandoned, or never was: a steer behind it never attaches to a later turn.</summary>
    public Task<string> NameOf(TaskCompletionSource<Turn> started)
    {
        lock (gate)
        {
            if (!ReferenceEquals(turn, started)) throw new InvalidOperationException("The turn already ended.");
            return named!.Task;
        }
    }

    /// <summary>A prompt or steer arrived on the ACP connection, before its handler runs on the thread pool.</summary>
    public void NoteArrival(long sequence)
    {
        lock (gate) arrivals[sequence] = Signal();
    }

    public void AdmitArrival(long sequence)
    {
        TaskCompletionSource? signal;
        lock (gate) arrivals.Remove(sequence, out signal);
        signal?.TrySetResult();
    }

    /// <summary>Waits until every prompt or steer that arrived before <paramref name="sequence"/> reached its admission point.</summary>
    public async Task WaitArrivalsBefore(long sequence, CancellationToken ct)
    {
        Task[] waiting;
        lock (gate) waiting = arrivals.Where(a => a.Key < sequence).Select(a => (Task)a.Value.Task).ToArray();
        if (waiting.Length > 0) await Task.WhenAll(waiting).WaitAsync(ct);
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
            localCommand?.Cancel();
            return (true, turnId);
        }
    }

    public void Completed(Turn ended)
    {
        TaskCompletionSource<Turn>? done;
        lock (gate)
        {
            done = turnId is null || turnId == ended.Id ? turn : null;
            if (done is not null) named?.TrySetException(new InvalidOperationException("The turn ended before Codex named it."));
        }
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
        lock (gate)
        {
            localCommand?.Cancel();
            (done, id) = (turn, turnId);
            named?.TrySetException(new InvalidOperationException("The turn was abandoned before Codex named it."));
        }
        return done?.TrySetResult(new Turn { Id = id ?? "", Status = TurnStatus.Interrupted }) == true ? id : null;
    }

    public void Fail(Exception e)
    {
        TaskCompletionSource<Turn>? done;
        lock (gate)
        {
            localCommand?.Cancel();
            done = turn;
            named?.TrySetException(e);
        }
        done?.TrySetException(e);
    }

    public void End(TaskCompletionSource<Turn> done)
    {
        lock (gate)
        {
            if (turn != done) return;
            named?.TrySetException(new InvalidOperationException("The turn ended before Codex named it."));
            localCommand?.Dispose();
            localCommand = null;
            turn = null;
            named = null;
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
    sealed record Arrival(CodexThread Thread, long Sequence);
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonRpcRequest, Arrival> arrivals = new();
    readonly ConcurrentDictionary<string, CodexThread> threads = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, CancellationTokenSource> asking = new(StringComparer.Ordinal);
    /// <summary>Threads being closed, until app-server says it unloaded them (<c>thread/closed</c>).</summary>
    readonly ConcurrentDictionary<string, TaskCompletionSource> unloading = new(StringComparer.Ordinal);

    /// <summary>The ephemeral threads of generated titles, by thread ID: their notifications never reach the service.</summary>
    readonly ConcurrentDictionary<string, TitleJob> titleHelpers = new(StringComparer.Ordinal);

    /// <summary>Every ephemeral title thread seen, kept so a late notification cannot be mistaken for a session's.</summary>
    readonly ConcurrentDictionary<string, byte> helperThreadIds = new(StringComparer.Ordinal);

    /// <summary>One generated title at a time: the requests are bounded by their own timeout.</summary>
    readonly SemaphoreSlim titleGate = new(1, 1);

    // A timed-out thread/start can still return a new thread. Keep its response so the thread
    // can be detached, and allow at most one such outstanding creation on this connection.
    readonly SemaphoreSlim titleStartGate = new(1, 1);

    /// <summary>A generated title's isolated thread and turn, and what the adapter waits for.</summary>
    sealed class TitleJob(string sessionId, CodexThread thread, CancellationTokenSource stop)
    {
        readonly Lock gate = new();
        string? text;
        bool ended;
        bool turnEnded;

        public string SessionId { get; } = sessionId;
        public CodexThread Thread { get; } = thread;
        public CancellationTokenSource Stop { get; } = stop;
        public string? HelperThreadId { get; set; }
        public string? TurnId { get; private set; }
        public TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TurnEnded { get { lock (gate) return turnEnded; } }

        public void Started(string turnId)
        {
            lock (gate) TurnId = turnId;
        }

        /// <summary>An agent message of the title turn: the last one is the structured answer.</summary>
        public bool Text(string value)
        {
            lock (gate)
            {
                if (ended) return false;
                if (Encoding.UTF8.GetByteCount(value) > 8 * 1024) return false;
                text = value;
                return true;
            }
        }

        public void Complete(TurnStatus status)
        {
            string? answer;
            lock (gate)
            {
                if (ended) return;
                ended = true;
                turnEnded = true;
                answer = text;
            }
            if (status == TurnStatus.Completed) Answer.TrySetResult(answer);
            else Answer.TrySetException(new InvalidOperationException($"the title turn ended {status}"));
        }

        public void Fail(Exception error)
        {
            lock (gate)
            {
                if (ended) return;
                ended = true;
            }
            Answer.TrySetException(error);
        }
    }

    /// <summary>Turns ended here before Codex said so (a close, delete or unload): their late <c>turn/completed</c> is dropped.</summary>
    readonly ConcurrentDictionary<string, byte> abandoned = new(StringComparer.Ordinal);
    readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    List<Model> models = [];
    HashSet<string> userServers = [];
    string version = "";
    int disposed;
    int backgroundUnsupported;

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
        rpc.RequestArrived = OnRequestArrived;
        rpc.RequestDone = OnRequestDone;
        rpc.NotificationHandler = OnNotificationAsync;
        rpc.Log = Log;
    }

    /// <summary>A prompt or steer is ordered at arrival, before its handler runs: a steer behind a prompt waits for it, and a cancel behind both waits for them.</summary>
    void OnRequestArrived(JsonRpcRequest request)
    {
        if (request.Method is not (AcpMethods.Agent.SessionPrompt or GorynychProtocol.MethodSteering)) return;
        if ((string?)request.Params?["sessionId"] is not { } id || !threads.TryGetValue(id, out var thread)) return;
        thread.NoteArrival(request.Sequence);
        arrivals.AddOrUpdate(request, new Arrival(thread, request.Sequence));
    }

    void OnRequestDone(JsonRpcRequest request)
    {
        if (arrivals.TryGetValue(request, out var arrival))
        {
            arrivals.Remove(request);
            arrival.Thread.AdmitArrival(arrival.Sequence);
        }
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
            Capabilities = new InitializeCapabilities { ExperimentalApi = true, OptOutNotificationMethods = Unused },
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
        // A thread changing hands changes its row in the service's list (gorynych/openElsewhere), and
        // one open here only for reading can be written to now, or no longer.
        lockWatcher = new CodexLockWatcher(locksDirectory, holders, ChangedHandsAsync, options.LockSettle, options.LockRecheck, Log);
    }

    /// <summary>
    /// A thread opened here without being taken, because another Codex app held it: its holder now
    /// (null: it let go, and the thread is taken at its next turn), and whether only app-server's
    /// refusal said it is held. The lock watcher cannot see such a holder (outside Windows, or one it
    /// cannot read), so its not seeing one does not mean it went: only a resume that works frees it.
    /// </summary>
    sealed record ReadOnlyThread(string? Holder, bool Refused);

    /// <summary>The threads open here for reading only (<see cref="ReadOnlyThread"/>).</summary>
    readonly ConcurrentDictionary<string, ReadOnlyThread> readOnly = new(StringComparer.Ordinal);

    async Task ChangedHandsAsync(string id, string? holder)
    {
        await rpc.SendNotificationAsync(GorynychProtocol.MethodSessionsChanged, new JsonObject { ["sessionId"] = id, ["change"] = "updated" });
        if (!readOnly.TryGetValue(id, out var before) || before.Holder == holder) return;
        if (holder is null && before.Refused) return; // a holder it never saw is not gone because it still sees none
        readOnly[id] = new ReadOnlyThread(holder, Refused: false);
        Log($"codex: thread {id}, open here for reading, is {(holder is null ? "free now" : $"held by {holder} now")}");
        await rpc.SendUpdateAsync(id, new SessionInfoUpdate { Meta = InteractivityMeta(new JsonObject(), holder) });
    }

    /// <summary>The session's interactivity, said in full (<c>full</c> too): <c>read-only</c> while another Codex app holds the thread.</summary>
    static JsonObject InteractivityMeta(JsonObject meta, string? holder)
    {
        meta[GorynychProtocol.MetaInteractivity] = holder is null ? GorynychProtocol.InteractivityFull : GorynychProtocol.InteractivityReadOnly;
        if (holder is null) return meta;
        meta[GorynychProtocol.MetaInteractivityReason] = ReadOnlyReason(holder);
        meta[GorynychProtocol.MetaOpenElsewhere] = holder;
        return meta;
    }

    static string ReadOnlyReason(string holder) => $"This Codex thread is open in {holder}. Close it there to write here.";

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
                            [GorynychProtocol.CapAutoTitle] = options.AutoTitle,
                            [GorynychProtocol.CapDraftsNotDurable] = true,
                            [GorynychProtocol.CapLocalCommands] = new JsonArray("/usage"),
                            [GorynychProtocol.CapUnloadAfter] = options.UnloadAfter.TotalSeconds,
                            [GorynychProtocol.CapOpenElsewhere] = true,
                            // It tells when a thread's list row changes: another Codex app took it or let it go.
                            [GorynychProtocol.MetaSessionsChanged] = true,
                            // It replays a thread's turns after the last one the service saw (ReplayFromAsync).
                            [GorynychProtocol.CapReplayFrom] = true,
                            // And without taking it, for a thread another Codex app held (thread/turns/list takes no writer lock).
                            [GorynychProtocol.CapCatchUp] = true,
                        },
                    },
                    AuthMethods = [],
                    // The shared steering extension, at the top level as its agreed wire shape says.
                    Meta = new JsonObject { [GorynychProtocol.MetaSteering] = new JsonObject { ["supported"] = true } },
                });
            case AcpMethods.Agent.SessionNew:
                return await NewAsync(request.ParamsAs<NewSessionRequest>(), ct);
            case AcpMethods.Agent.SessionResume:
            {
                var p = request.ParamsAs<ResumeSessionRequest>();
                var (t, holder) = await OpenOrReadAsync(p.SessionId, p.McpServers ?? [], p.SystemPrompt, ct);
                // Its history is read the same either way: thread/turns/list takes no writer lock.
                var replayed = p.Meta?[GorynychProtocol.MetaReplayFrom] is JsonObject from ? await ReplayFromAsync(p.SessionId, from, ct) : null;
                if (t is not null) await PublishCommandsAsync(p.SessionId);
                var meta = new JsonObject();
                if (replayed is not null) meta[GorynychProtocol.MetaReplay] = replayed;
                if (t is null) InteractivityMeta(meta, holder);
                return AcpJson.ToNode(new ResumeSessionResponse
                {
                    ConfigOptions = t is null ? null : ConfigOptions(t.Effective, models),
                    Meta = meta.Count > 0 ? meta : null,
                });
            }
            case AcpMethods.Agent.SessionLoad:
            {
                var p = request.ParamsAs<LoadSessionRequest>();
                var (t, holder) = await OpenOrReadAsync(p.SessionId, p.McpServers, p.SystemPrompt, ct);
                await ReplayAsync(p.SessionId, ct);
                if (t is not null) await PublishCommandsAsync(p.SessionId);
                return AcpJson.ToNode(new LoadSessionResponse
                {
                    ConfigOptions = t is null ? null : ConfigOptions(t.Effective, models),
                    Meta = t is null ? InteractivityMeta(new JsonObject(), holder) : null,
                });
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
            case GorynychProtocol.MethodSteering:
                return await SteerAsync(request, ct);
            case AcpMethods.Agent.SessionSetConfigOption:
                return SetConfigOption(request.ParamsAs<SetSessionConfigOptionRequest>());
            case GorynychProtocol.MethodCatchUp:
            {
                // Read, not loaded: the thread's turns after the mark, as a resume replays them.
                var from = request.Params?["_meta"]?[GorynychProtocol.MetaReplayFrom] as JsonObject ?? new JsonObject { ["type"] = GorynychProtocol.ReplayFromStart };
                var replayed = await ReplayFromAsync(RequireId(request), from, ct);
                return new JsonObject { ["_meta"] = replayed is null ? null : new JsonObject { [GorynychProtocol.MetaReplay] = replayed } };
            }
            case GorynychProtocol.MethodOpenElsewhere:
            {
                var ids = (request.Params?["sessionIds"] as JsonArray ?? []).Select(i => (string?)i).OfType<string>().ToList();
                var sessions = new JsonObject();
                foreach (var (id, holder) in holders(ids)) sessions[id] = holder;
                return new JsonObject { ["sessions"] = sessions };
            }
            case GorynychProtocol.MethodSetTitle:
            {
                var id = RequireId(request);
                var title = (string?)request.Params?["title"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "title is required");
                if (threads.TryGetValue(id, out var t))
                {
                    // The person's name wins, also over a generated title still in flight.
                    t.CancelTitleJob(manual: true);
                    await t.TitleGate.WaitAsync(ct);
                    try
                    {
                        t.CancelTitleJob(manual: true);
                        await app.SendRequestAsync(CodexMethods.Client.ThreadNameSet, ToNode(new ThreadSetNameParams { ThreadId = id, Name = title },
                            CodexJsonContext.Default.ThreadSetNameParams), ct);
                    }
                    finally
                    {
                        t.TitleGate.Release();
                    }
                }
                else
                {
                    await app.SendRequestAsync(CodexMethods.Client.ThreadNameSet, ToNode(new ThreadSetNameParams { ThreadId = id, Name = title },
                        CodexJsonContext.Default.ThreadSetNameParams), ct);
                }
                return new JsonObject();
            }
            case GorynychProtocol.MethodGenerateTitle:
                return StartTitleGeneration(request);
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
            _ = Task.Run(async () =>
            {
                await t.WaitArrivalsBefore(sequence, CancellationToken.None);
                await StopAsync(t, sequence);
            });
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
        var t = new CodexThread(started.Thread.Id, started.Cwd,
            SettingsOf(started.Model, started.ReasoningEffort, started.ApprovalPolicy, started.Sandbox, started.ApprovalsReviewer));
        threads[t.Id] = t;
        t.BackgroundTask = PollBackgroundAsync(t);
        Log($"codex: new thread {t.Id} in {started.Cwd}");
        await PublishCommandsAsync(t.Id);
        return AcpJson.ToNode(new NewSessionResponse { SessionId = t.Id, ConfigOptions = ConfigOptions(t.Settings, models) });
    }

    Task PublishCommandsAsync(string sessionId) => Update(sessionId, new AvailableCommandsUpdate
    {
        AvailableCommands = [new AvailableCommand { Name = "usage", Description = "Show Codex account usage limits and reset times" }],
    });

    async Task<JsonNode?> UsageAsync(CodexThread t, long sequence, CancellationToken ct)
    {
        var done = t.Begin(sequence) ?? throw new JsonRpcException(JsonRpcException.InvalidRequest, "A turn of this session is still running.");
        t.AdmitArrival(sequence);
        var status = TurnStatus.Completed;
        try
        {
            var cancel = t.LocalCancellation(ct);
            try
            {
                cancel.ThrowIfCancellationRequested();
                var response = From(await app.SendRequestAsync(CodexMethods.Client.AccountRateLimitsRead,
                    ToNode(new GetAccountRateLimitsParams(), CodexJsonContext.Default.GetAccountRateLimitsParams), cancel),
                    CodexJsonContext.Default.GetAccountRateLimitsResponse);
                cancel.ThrowIfCancellationRequested();
                await Update(t.Id, new SessionUpdateAgentMessageChunk
                {
                    Content = new TextContent { Text = CodexUsage.Format(response) },
                    Meta = new JsonObject { [GorynychProtocol.MetaLocalCommand] = true },
                });
                return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                status = TurnStatus.Interrupted;
                return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.Cancelled });
            }
        }
        finally
        {
            done.TrySetResult(new Turn { Id = "", Status = status });
            t.End(done);
        }
    }

    /// <summary>
    /// Opens a thread: loaded into this app-server when it is free (<see cref="OpenAsync"/>), else
    /// open for reading only, untaken, while another Codex app holds it (null and its holder). Its
    /// history is read without taking it, and it is taken at its first turn once it is free.
    /// </summary>
    /// <param name="again">The holder was found gone right after it was noted (<paramref name="again"/> is the one more try).</param>
    async Task<(CodexThread? Thread, string? Holder)> OpenOrReadAsync(string id, List<McpServer> servers, SystemPrompt? systemPrompt, CancellationToken ct,
        bool again = false)
    {
        try
        {
            var t = await OpenAsync(id, servers, systemPrompt, ct);
            if (readOnly.TryRemove(id, out _)) Log($"codex: thread {id}, open here for reading, is taken now");
            return (t, null);
        }
        catch (HeldElsewhereException held)
        {
            readOnly[id] = new ReadOnlyThread(held.Holder, held.Refused);
            // A holder that let go between its check and the line above: the lock watcher's word of it
            // found no thread open for reading to tell. Looked at once more, and taken if it is free.
            if (!held.Refused && !again && holders([id]).GetValueOrDefault(id) is null)
                return await OpenOrReadAsync(id, servers, systemPrompt, ct, again: true);
            Log($"codex: thread {id} is open in {held.Holder}; it opens here for reading only, untaken");
            return (null, held.Holder);
        }
    }

    /// <summary>
    /// The thread is held by another Codex app (the holder's name), so it cannot be loaded here: as
    /// the holders found say, or as only app-server's refusal does (<paramref name="refused"/>).
    /// </summary>
    sealed class HeldElsewhereException(string holder, bool refused = false) : Exception($"This Codex thread is open in {holder}; close it there, then try again.")
    {
        public string Holder { get; } = holder;
        public bool Refused { get; } = refused;
    }

    /// <summary>
    /// Loads a thread into this app-server (<c>thread/resume</c>), with the MCP servers of this
    /// request: Codex keeps them only while the thread is loaded. A thread already loaded stays as it
    /// is. One another Codex app holds fails with <see cref="HeldElsewhereException"/>.
    /// </summary>
    async Task<CodexThread> OpenAsync(string id, List<McpServer> servers, SystemPrompt? systemPrompt, CancellationToken ct)
    {
        var instructions = Instructions(systemPrompt);
        if (threads.TryGetValue(id, out var known) && known.Loaded) return known;
        // App-server would refuse it anyway (one writer); said at once, naming the app that has it.
        var held = holders([id]);
        // A session that moves off another Gorynych host of Codex after an update: that host's
        // app-server lets go of the thread as its close answers, which may lag a moment behind.
        for (var waited = Stopwatch.StartNew();
             held.GetValueOrDefault(id) is { } other && CodexThreadHolders.IsGorynychHost(other) && waited.Elapsed < options.UnloadWait;)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            held = holders([id]);
        }
        lockWatcher?.Saw([id], held);
        if (held.GetValueOrDefault(id) is { } holder) throw new HeldElsewhereException(holder);
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
        catch (JsonRpcException e) when (e.Message.Contains("active writer", StringComparison.Ordinal))
        {
            // The lock was taken after the check, or the holder could not be told (outside Windows, or
            // one it cannot read). Not given to the lock watcher, which would see it go at its next look.
            throw new HeldElsewhereException(AnotherCodexApp, refused: true);
        }
        catch (JsonRpcException e)
        {
            throw Explain(e, id);
        }
        var t = new CodexThread(id, resumed.Cwd, SettingsOf(resumed.Model, resumed.ReasoningEffort, resumed.ApprovalPolicy, resumed.Sandbox, resumed.ApprovalsReviewer));
        threads[id] = t;
        t.BackgroundTask = PollBackgroundAsync(t);
        return t;
    }

    async Task PollBackgroundAsync(CodexThread thread)
    {
        var stopped = thread.BackgroundToken;
        JsonArray? previous = null;
        var reported = false;
        var previousEmpty = false;
        try
        {
            while (!stopped.IsCancellationRequested && Volatile.Read(ref disposed) == 0 && Volatile.Read(ref backgroundUnsupported) == 0 && thread.Loaded && threads.TryGetValue(thread.Id, out var current) && ReferenceEquals(current, thread))
            {
                JsonArray? tasks = null;
                var completeEmpty = false;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    (tasks, completeEmpty) = await ReadBackgroundAsync((cursor, ct) => app.SendRequestAsync(CodexMethods.Client.ThreadBackgroundTerminalsList,
                        ToNode(new ThreadBackgroundTerminalsListParams { ThreadId = thread.Id, Cursor = cursor, Limit = 100 }, CodexJsonContext.Default.ThreadBackgroundTerminalsListParams), ct), timeout.Token);
                }
                catch (JsonRpcException e) when (e.Code == JsonRpcException.MethodNotFound)
                {
                    Interlocked.Exchange(ref backgroundUnsupported, 1);
                    Log("codex: background terminal listing is not supported by this app-server");
                }
                catch (Exception e) when (e is JsonRpcException or IOException or OperationCanceledException or InvalidOperationException)
                {
                    if (stopped.IsCancellationRequested) return;
                    Log($"codex: background terminals for {thread.Id} unavailable: {e.Message}");
                    tasks = null;
                    completeEmpty = false;
                }
                if (!thread.Loaded || !threads.TryGetValue(thread.Id, out current) || !ReferenceEquals(current, thread) || Volatile.Read(ref disposed) != 0) return;
                try
                {
                    if ((reported || tasks is not null) && (!reported || !JsonNode.DeepEquals(previous, tasks) || previousEmpty != completeEmpty))
                    {
                        previous = tasks?.DeepClone().AsArray();
                        previousEmpty = completeEmpty;
                        reported = true;
                        await Update(thread.Id, new SessionInfoUpdate { Meta = new JsonObject
                        {
                            [GorynychProtocol.MetaBackgroundKnown] = tasks,
                        } });
                    }
                    if (Volatile.Read(ref backgroundUnsupported) != 0) return;
                    await Task.Delay(TimeSpan.FromSeconds(5), stopped);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or JsonRpcException) { return; }
            }
        }
        finally
        {
            if (Volatile.Read(ref backgroundUnsupported) != 0 && previous is not null && thread.Loaded && Volatile.Read(ref disposed) == 0)
                try { await Update(thread.Id, new SessionInfoUpdate { Meta = new JsonObject { [GorynychProtocol.MetaBackgroundKnown] = null } }); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or JsonRpcException) { }
            thread.DisposeBackground();
        }
    }

    internal static async Task<(JsonArray? Tasks, bool Empty)> ReadBackgroundAsync(Func<string?, CancellationToken, Task<JsonNode?>> read, CancellationToken ct)
    {
        var found = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await read(cursor, ct);
            if (raw?["data"] is not JsonArray data) throw new InvalidDataException("missing terminal data");
            foreach (var entry in data)
            {
                if (entry is not JsonObject terminal || terminal["itemId"] is not JsonValue item || !item.TryGetValue<string>(out var itemId) || string.IsNullOrEmpty(itemId) ||
                    terminal["processId"] is not JsonValue process || !process.TryGetValue<string>(out var processId) || string.IsNullOrEmpty(processId) ||
                    terminal["command"] is not JsonValue command || !command.TryGetValue<string>(out var commandText) ||
                    terminal["cwd"] is not JsonValue cwd || !cwd.TryGetValue<string>(out _)) throw new InvalidDataException("invalid terminal");
                var id = $"{itemId.Length}:{itemId}{processId}";
                if (!found.TryAdd(id, new JsonObject { ["type"] = "_codex_terminal", ["_meta"] = new JsonObject
                { ["gorynych/nativeId"] = id, ["gorynych/itemId"] = itemId, ["gorynych/processId"] = processId, ["gorynych/description"] = commandText } }))
                    throw new InvalidDataException("duplicate terminal");
            }
            if (raw["nextCursor"] is null)
                return found.Count == 0 ? (new JsonArray(), true) : (new JsonArray(found.Values.Cast<JsonNode?>().ToArray()), false);
            if (raw["nextCursor"] is not JsonValue next || !next.TryGetValue<string>(out cursor) || string.IsNullOrEmpty(cursor) || !cursors.Add(cursor))
                throw new InvalidDataException("invalid terminal cursor");
        }
        throw new InvalidDataException("too many terminal pages");
    }

    /// <summary>The holder named when app-server refuses a thread whose holder could not be told.</summary>
    const string AnotherCodexApp = "another Codex app (Codex Desktop, the CLI or VS Code)";

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
            var turns = From(await app.SendRequestAsync(CodexMethods.Client.ThreadTurnsList, ToNode(new ThreadTurnsListParams
            {
                ThreadId = id,
                SortDirection = SortDirection.Asc,
                ItemsView = TurnItemsView.Full,
                Limit = 50,
                Cursor = cursor,
            }, CodexJsonContext.Default.ThreadTurnsListParams), ct), CodexJsonContext.Default.ThreadTurnsListResponse);
            foreach (var turn in turns.Data)
                foreach (var item in turn.Items)
                    if (ReplayOf(item) is { } update) await rpc.SendUpdateAsync(id, update);
            cursor = turns.NextCursor;
            if (cursor is null) return;
        }
    }

    /// <summary>
    /// Replays the thread's turns after the mark, a turn ID (docs/adapter-contract.md, §4). Codex's
    /// turns are listed newest first without their items until the marked one; then the newer
    /// ones are replayed oldest first, their pages read with <c>itemsView: full</c>, so
    /// nothing before the marked page is read and the replay is never held whole. When the mark is not among
    /// them (a rollback dropped it) or none was given, every turn. Answers <c>gorynych/replay</c> with
    /// the newest completed turn as the new mark (none for a thread without turns), or null when the
    /// history could not be read: the service then keeps its journal as it is.
    /// </summary>
    async Task<JsonObject?> ReplayFromAsync(string id, JsonObject from, CancellationToken ct)
    {
        var mark = (string?)from["type"] == GorynychProtocol.ReplayAfterMark ? (string?)from["mark"] : null;
        try
        {
            var newer = new HashSet<string>(StringComparer.Ordinal);
            var pages = new List<string?>();
            var found = false;
            string? newest = null;
            string? cursor = null;
            for (var page = 0; page < 100_000 && !found; page++)
            {
                pages.Add(cursor);
                var turns = From(await app.SendRequestAsync(CodexMethods.Client.ThreadTurnsList, ToNode(new ThreadTurnsListParams
                {
                    ThreadId = id,
                    SortDirection = SortDirection.Desc,
                    ItemsView = TurnItemsView.NotLoaded,
                    Limit = 50,
                    Cursor = cursor,
                }, CodexJsonContext.Default.ThreadTurnsListParams), ct), CodexJsonContext.Default.ThreadTurnsListResponse);
                foreach (var turn in turns.Data)
                {
                    // A turn still running (in another Codex app) is neither a point to resume from
                    // nor replayed: its messages are not whole yet, and the journal would keep them
                    // cut, as it takes a message it has by its ID. The next catch-up sees it whole.
                    var running = turn.Status == TurnStatus.InProgress;
                    if (!running) newest ??= turn.Id;
                    if (turn.Id == mark) { found = true; break; }
                    if (!running) newer.Add(turn.Id);
                }
                cursor = turns.NextCursor;
                if (cursor is null) break;
            }
            pages.Reverse();
            var sent = 0;
            // The item-list method exists in the schema but Codex 0.169 refuses it.
            // Re-read the selected turn pages with full items, oldest page first. Keep
            // at most one page of message bodies in memory and skip the marked/running turns.
            foreach (var pageCursor in pages)
            {
                if (newer.Count == 0) break;
                var turns = From(await app.SendRequestAsync(CodexMethods.Client.ThreadTurnsList, ToNode(new ThreadTurnsListParams
                {
                    ThreadId = id,
                    SortDirection = SortDirection.Desc,
                    ItemsView = TurnItemsView.Full,
                    Limit = 50,
                    Cursor = pageCursor,
                }, CodexJsonContext.Default.ThreadTurnsListParams), ct), CodexJsonContext.Default.ThreadTurnsListResponse);
                foreach (var turn in turns.Data.AsEnumerable().Reverse())
                {
                    if (!newer.Contains(turn.Id) || turn.Status == TurnStatus.InProgress) continue;
                    foreach (var item in turn.Items)
                        if (ReplayOf(item) is { } update)
                        {
                            await rpc.SendUpdateAsync(id, update);
                            sent++;
                        }
                }
            }
            Log($"codex: thread {id}: {newer.Count} turns ({sent} updates) replayed {(found ? "after " + mark : "from the start")}");
            return new JsonObject { ["from"] = found ? "mark" : "start", ["mark"] = newest };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"codex: thread {id}: its history could not be read, so the service's journal is not caught up: {e.Message}");
            return null;
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
                // Held by another Codex app: read-only here until it lets go (docs/service-protocol.md, "Read-only sessions").
                Meta = held.TryGetValue(th.Id, out var holder) ? InteractivityMeta(new JsonObject(), holder) : null,
            }).ToList(),
            NextCursor = listed.NextCursor,
        });
    }

    /// <summary>
    /// Closing stops the thread's turn (ACP), gives it a moment to end, then unloads the thread
    /// from app-server. A turn that has not ended by then ends as cancelled: after unsubscribing,
    /// no <c>turn/completed</c> comes for it. It returns once app-server has unloaded the thread
    /// (<c>thread/closed</c>, at once with the launch's <c>thread_unload_delay_secs=0</c>), so the
    /// thread's writer lock is free for another Codex app; it waits for that at most
    /// <see cref="CodexAdapterOptions.UnloadWait"/>, and fails when it did not come: the service
    /// then counts the thread as still held here (it opens here again, not in another host).
    /// </summary>
    async Task CloseAsync(string id, CancellationToken ct)
    {
        readOnly.TryRemove(id, out _); // open for reading only: nothing of it is loaded here
        if (!threads.TryGetValue(id, out var t)) return;
        t.CancelTitleJob();
        if (!t.Loaded) return;
        t.StopBackground();
        if (t.BackgroundTask is { } backgroundTask) await backgroundTask;
        await StopTurnAsync(t);
        var closed = unloading.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var unsubscribed = false;
        try
        {
            await app.SendRequestAsync(CodexMethods.Client.ThreadUnsubscribe, ToNode(new ThreadUnsubscribeParams { ThreadId = id },
                CodexJsonContext.Default.ThreadUnsubscribeParams), ct);
            unsubscribed = true;
        }
        finally
        {
            // Also when unsubscribing fails (app-server going away): the turn has had its time.
            t.Loaded = false;
            t.StopBackground();
            Abandon(t);
            if (!unsubscribed) unloading.TryRemove(id, out _);
        }
        var unloaded = await Task.WhenAny(closed.Task, Task.Delay(options.UnloadWait, ct)) == closed.Task;
        unloading.TryRemove(id, out _);
        if (unloaded) return;
        Log($"codex: thread {id}: app-server has not unloaded it {options.UnloadWait.TotalSeconds:0} s after the unsubscribe; its writer lock may still be held");
        throw new JsonRpcException(JsonRpcException.InternalError,
            $"Codex's app-server has not let go of thread {id} {options.UnloadWait.TotalSeconds:0} s after it was closed; its writer lock may still be held.");
    }

    async Task DeleteAsync(string id, CancellationToken ct)
    {
        readOnly.TryRemove(id, out _);
        threads.TryGetValue(id, out var t);
        if (t is not null) { t.CancelTitleJob(); t.StopBackground(); if (t.BackgroundTask is { } backgroundTask) await backgroundTask; await StopTurnAsync(t); }
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
                t.StopBackground();
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
        // Open here for reading only: a resume takes it once it is free (the service resumes it before a turn).
        if (readOnly.TryGetValue(p.SessionId, out var open))
            throw new JsonRpcException(JsonRpcException.InvalidRequest, open.Holder is not { } holder
                ? $"Codex session {p.SessionId} is open here for reading only; resume it to take it, then send the prompt again."
                : $"Codex session {p.SessionId} is read-only here: {ReadOnlyReason(holder)}");
        if (!threads.TryGetValue(p.SessionId, out var t) || !t.Loaded)
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Codex has not loaded session {p.SessionId}; resume it first.");
        if (p.Prompt is [TextContent { Text: var command }] && command.Trim() == "/usage")
            return await UsageAsync(t, request.Sequence, ct);
        var input = p.Prompt.Select(InputOf).OfType<UserInput>().ToList();
        if (input.Count == 0) throw new JsonRpcException(JsonRpcException.InvalidParams, "The prompt has no text.");
        var done = t.Begin(request.Sequence) ?? throw new JsonRpcException(JsonRpcException.InvalidRequest, "A turn of this session is still running.");
        t.AdmitArrival(request.Sequence);
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
            var reason = StopOf(await done.Task);
            // The turn is the thread's newest: the journal now holds the thread up to it.
            return AcpJson.ToNode(new PromptResponse { StopReason = reason, Meta = new JsonObject { [GorynychProtocol.MetaHistoryMark] = started.Turn.Id } });
        }
        finally
        {
            t.End(done);
        }
    }

    /// <summary>
    /// <c>_session/steering</c> on Codex: wait for the running turn to be named, then
    /// <c>turn/steer</c> with that turn as <c>expectedTurnId</c> and the service's message ID as
    /// <c>clientUserMessageId</c>. Success is queue admission. A turn that ended, or a name that
    /// never came, answers <c>promptRequired</c>; a review or compact turn is
    /// <c>turnNotSteerable</c>. Other failures stay errors: they may or may not have been accepted.
    /// </summary>
    async Task<JsonNode?> SteerAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var steering = AcpSteering.Parse(request.Params);
        if (readOnly.TryGetValue(steering.SessionId, out var open))
            throw AcpSteering.Refused("turnNotSteerable", open.Holder is not { } holder
                ? $"Codex session {steering.SessionId} is open here for reading only; it cannot take a steer."
                : $"Codex session {steering.SessionId} is read-only here: {ReadOnlyReason(holder)}");
        if (!threads.TryGetValue(steering.SessionId, out var t) || !t.Loaded) return AcpSteering.PromptRequiredResult();
        await t.WaitArrivalsBefore(request.Sequence, ct);
        var input = steering.Prompt.Select(InputOf).OfType<UserInput>().ToList();
        if (input.Count == 0) throw new JsonRpcException(JsonRpcException.InvalidParams, "The steering message has no text.");
        var running = t.Turn;
        if (running is null)
        {
            if (steering.PromptRequired)
            {
                t.AdmitArrival(request.Sequence);
                return AcpSteering.PromptRequiredResult();
            }
            var created = await StartIdleAsync(t, input, messageId: (string?)request.Params?["_meta"]?[GorynychProtocol.MetaMessageId], request.Sequence, ct);
            t.AdmitArrival(request.Sequence);
            return created;
        }
        string turnId;
        try
        {
            turnId = await t.NameOf(running).WaitAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException)
        {
            // The turn ended, was abandoned, or Codex closed the thread or the process went:
            // there is no running turn to join, and this steer never reached it.
            t.AdmitArrival(request.Sequence);
            return AcpSteering.PromptRequiredResult();
        }
        var messageId = (string?)request.Params?["_meta"]?[GorynychProtocol.MetaMessageId];
        TurnSteerResponse accepted;
        try
        {
            var response = await app.StartRequestAsync(CodexMethods.Client.TurnSteer, ToNode(new TurnSteerParams
            {
                ThreadId = t.Id,
                ExpectedTurnId = turnId,
                Input = input,
                ClientUserMessageId = messageId,
            }, CodexJsonContext.Default.TurnSteerParams), ct);
            t.AdmitArrival(request.Sequence); // written: a later cancel now goes after it
            accepted = From(await response, CodexJsonContext.Default.TurnSteerResponse);
        }
        catch (JsonRpcException e) when (IsNoActiveTurn(e) || IsExpectedTurnMismatch(e))
        {
            t.AdmitArrival(request.Sequence);
            return AcpSteering.PromptRequiredResult();
        }
        catch (JsonRpcException e) when (IsActiveTurnNotSteerable(e))
        {
            t.AdmitArrival(request.Sequence);
            throw AcpSteering.Refused("turnNotSteerable", "This Codex turn cannot take a steer (a review or a manual compact).");
        }
        if (accepted.TurnId != turnId)
            throw new JsonRpcException(JsonRpcException.InternalError,
                $"Codex accepted the steer for turn {accepted.TurnId}, not the running turn {turnId}; its outcome is unknown.");
        return AcpSteering.Result(AcpSteering.Injected);
    }

    /// <summary>
    /// The shared extension's legacy idle start for a direct ACP caller that did not opt in: one
    /// <c>turn/start</c>, tracked as the thread's running turn, answered <c>startedNewTurn</c> once
    /// Codex accepted it. The conductor always opts in, so its own fallback owns idle starts.
    /// </summary>
    async Task<JsonObject> StartIdleAsync(CodexThread t, List<UserInput> input, string? messageId, long sequence, CancellationToken ct)
    {
        var done = t.Begin(sequence) ?? throw new JsonRpcException(JsonRpcException.InvalidRequest, "A turn of this session is still running.");
        try
        {
            var started = From(await app.SendRequestAsync(CodexMethods.Client.TurnStart, ToNode(new TurnStartParams
            {
                ThreadId = t.Id,
                Input = input,
                ClientUserMessageId = messageId,
            }, CodexJsonContext.Default.TurnStartParams), ct), CodexJsonContext.Default.TurnStartResponse);
            Named(t, started.Turn.Id);
            _ = done.Task.ContinueWith(_ => t.End(done), TaskScheduler.Default);
            return AcpSteering.Result(AcpSteering.StartedNewTurn);
        }
        catch
        {
            t.Fail(new JsonRpcException(JsonRpcException.InternalError, "Codex did not start the idle turn."));
            _ = done.Task.ContinueWith(finished => _ = finished.Exception, TaskScheduler.Default);
            t.End(done);
            throw;
        }
    }

    static bool IsNoActiveTurn(JsonRpcException e) =>
        e.Code == JsonRpcException.InvalidRequest && e.Message.Contains("no active turn to steer", StringComparison.Ordinal);

    static bool IsExpectedTurnMismatch(JsonRpcException e) =>
        e.Code == JsonRpcException.InvalidRequest && e.Message.StartsWith("expected active turn id", StringComparison.Ordinal);

    static bool IsActiveTurnNotSteerable(JsonRpcException e) =>
        e.Code == JsonRpcException.InvalidRequest
        && (e.ErrorData?["codexErrorInfo"]?["activeTurnNotSteerable"] is not null
            || e.ErrorData?["codex_error_info"]?["active_turn_not_steerable"] is not null);

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

    // ---------------------------------------------------------------- generated titles

    /// <summary>
    /// Accepts a request for a generated title and starts it in the background; the service does
    /// not wait for it. Anything about the session that is already named (a spawn name, a native
    /// name, a person's rename) is left alone.
    /// </summary>
    JsonNode StartTitleGeneration(JsonRpcRequest request)
    {
        var id = RequireId(request);
        var input = (string?)request.Params?["input"] ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "input is required");
        var provisional = (string?)request.Params?["title"];
        var cwd = (string?)request.Params?["cwd"];
        if (!threads.TryGetValue(id, out var t) || !t.Loaded)
            throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Codex has not loaded session {id}; it cannot be titled.");
        if (t.ManualTitle) return new JsonObject();
        var stop = new CancellationTokenSource();
        stop.CancelAfter(options.TitleTimeout);
        var job = new TitleJob(id, t, stop);
        t.ReplaceTitleJob(stop);
        _ = Task.Run(() => GenerateTitleAsync(job, input, provisional, cwd is { Length: > 0 } ? cwd : t.Cwd));
        return new JsonObject();
    }

    async Task GenerateTitleAsync(TitleJob job, string input, string? provisional, string cwd)
    {
        var t = job.Thread;
        try
        {
            await titleGate.WaitAsync(job.Stop.Token);
            try
            {
                if (job.Stop.IsCancellationRequested || t.ManualTitle || !t.IsTitleJob(job.Stop)) return;
                var mcp = await EffectiveMcpAsync(cwd, job.Stop.Token);
                if (mcp is null)
                {
                    Log($"codex {job.SessionId}: no generated title: the effective MCP servers could not be read, so tools cannot be turned off");
                    return;
                }
                var start = new ThreadStartParams
                {
                    Cwd = cwd,
                    Config = CodexTitleGeneration.Config(mcp),
                    Model = t.Settings.Model,
                    ApprovalPolicy = JsonValue.Create("never"),
                    Sandbox = SandboxMode.ReadOnly,
                    Ephemeral = true,
                    ThreadSource = "thread_title",
                    BaseInstructions = CodexTitleGeneration.Instructions,
                };
                ThreadStartResponse started;
                try
                {
                    started = await StartTitleHelperAsync(start, job.Stop.Token);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log($"codex {job.SessionId}: no generated title: the isolated thread did not start: {e.Message}");
                    return;
                }
                job.HelperThreadId = started.Thread.Id;
                helperThreadIds[started.Thread.Id] = 0;
                titleHelpers[started.Thread.Id] = job;
                if (job.Stop.IsCancellationRequested) return;
                var turn = new TurnStartParams
                {
                    ThreadId = started.Thread.Id,
                    Input = [new TextUserInput { Text = CodexTitleGeneration.Prompt(input, options.TitleInputBytes) }],
                    OutputSchema = CodexTitleGeneration.Schema(options.TitleMaxChars),
                };
                TurnStartResponse answer;
                try
                {
                    answer = From(await app.SendRequestAsync(CodexMethods.Client.TurnStart, ToNode(turn, CodexJsonContext.Default.TurnStartParams),
                        job.Stop.Token), CodexJsonContext.Default.TurnStartResponse);
                }
                catch (OperationCanceledException) when (job.Stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    Log($"codex {job.SessionId}: no generated title: the isolated turn did not start: {e.Message}");
                    return;
                }
                job.Started(answer.Turn.Id);
                string? text;
                try
                {
                    text = await job.Answer.Task.WaitAsync(options.TitleTimeout, job.Stop.Token);
                }
                catch (TimeoutException)
                {
                    Log($"codex {job.SessionId}: no generated title: the isolated turn timed out");
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Log($"codex {job.SessionId}: no generated title: {e.Message}");
                    return;
                }
                if (CodexTitleGeneration.Normalize(text, options.TitleMaxChars) is not { } title)
                {
                    Log($"codex {job.SessionId}: no generated title: the isolated turn returned no usable title");
                    return;
                }
                await ApplyGeneratedTitleAsync(job, title, provisional);
            }
            finally
            {
                titleGate.Release();
            }
        }
        catch (OperationCanceledException) when (job.Stop.IsCancellationRequested)
        {
            Log($"codex {job.SessionId}: title generation cancelled or its deadline elapsed");
        }
        catch (Exception e)
        {
            Log($"codex {job.SessionId}: title generation failed: {e.Message}");
        }
        finally
        {
            if (job.TurnId is { } turnId && !job.TurnEnded) await InterruptAsync(job.HelperThreadId ?? "", turnId);
            if (job.HelperThreadId is { } helper)
            {
                titleHelpers.TryRemove(helper, out _);
                await UnsubscribeHelperAsync(helper);
            }
            t.EndTitleJob(job.Stop);
            job.Stop.Dispose();
        }
    }

    async Task<ThreadStartResponse> StartTitleHelperAsync(ThreadStartParams start, CancellationToken ct)
    {
        await titleStartGate.WaitAsync(ct);
        var release = true;
        try
        {
            // Do not cancel away the response carrying the helper ID: app-server may already
            // have created it. On timeout its eventual response belongs to cleanup instead.
            var response = app.SendRequestAsync(CodexMethods.Client.ThreadStart,
                ToNode(start, CodexJsonContext.Default.ThreadStartParams));
            try
            {
                return From(await response.WaitAsync(ct), CodexJsonContext.Default.ThreadStartResponse);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                release = false;
                _ = DiscardLateTitleHelperAsync(response);
                throw;
            }
        }
        finally { if (release) titleStartGate.Release(); }
    }

    async Task DiscardLateTitleHelperAsync(Task<JsonNode?> response)
    {
        try
        {
            var started = From(await response, CodexJsonContext.Default.ThreadStartResponse);
            helperThreadIds[started.Thread.Id] = 0;
            await UnsubscribeHelperAsync(started.Thread.Id);
        }
        catch (Exception e) { Log($"codex: late title thread cleanup: {e.Message}"); }
        finally { titleStartGate.Release(); }
    }

    /// <summary>The config/read for this folder, with every effective MCP server explicitly disabled; null fails closed.</summary>
    async Task<JsonObject?> EffectiveMcpAsync(string cwd, CancellationToken ct)
    {
        try
        {
            var response = await app.SendRequestAsync(CodexMethods.Client.ConfigRead, new JsonObject { ["cwd"] = cwd }, ct);
            if (response?["config"]?["mcp_servers"] is not JsonObject servers) return new JsonObject();
            var mcp = new JsonObject();
            foreach (var (name, _) in servers) mcp[name] = new JsonObject { ["enabled"] = false };
            foreach (var name in userServers) if (!mcp.ContainsKey(name)) mcp[name] = new JsonObject { ["enabled"] = false };
            return mcp;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"codex: the effective MCP servers could not be read: {e.Message}");
            return null;
        }
    }

    /// <summary>Writes a generated title natively unless a person named the thread meanwhile.</summary>
    async Task ApplyGeneratedTitleAsync(TitleJob job, string title, string? provisional)
    {
        try
        {
            await job.Thread.TitleGate.WaitAsync(job.Stop.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (job.Stop.IsCancellationRequested || job.Thread.ManualTitle || !job.Thread.IsTitleJob(job.Stop)) return;
            await app.SendRequestAsync(CodexMethods.Client.ThreadNameSet,
                ToNode(new ThreadSetNameParams { ThreadId = job.SessionId, Name = title }, CodexJsonContext.Default.ThreadSetNameParams), job.Stop.Token);
            Log($"codex {job.SessionId}: generated title \"{title}\"{(provisional is null ? "" : $" (was \"{provisional}\")")}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log($"codex {job.SessionId}: the generated title was not saved: {e.Message}");
        }
        finally
        {
            job.Thread.TitleGate.Release();
        }
    }

    /// <summary>Detaches an ephemeral title thread, best effort and bounded; it was never durable.</summary>
    async Task UnsubscribeHelperAsync(string threadId)
    {
        try
        {
            using var limit = new CancellationTokenSource(options.TurnEndWait);
            await app.SendRequestAsync(CodexMethods.Client.ThreadUnsubscribe, ToNode(new ThreadUnsubscribeParams { ThreadId = threadId },
                CodexJsonContext.Default.ThreadUnsubscribeParams), limit.Token);
        }
        catch (Exception e) when (e is JsonRpcException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            Log($"codex: the title thread {threadId} was not detached: {e.Message}");
        }
    }

    /// <summary>True for a notification or request of a generated title's ephemeral thread; it is never forwarded.</summary>
    bool IsTitleHelper(string? threadId) => threadId is not null && helperThreadIds.ContainsKey(threadId);

    /// <summary>A title thread's notifications: only the answer and its turn's end matter.</summary>
    async Task OnTitleNotificationAsync(string method, JsonNode? p)
    {
        var threadId = (string?)p?["threadId"];
        if (threadId is null || !titleHelpers.TryGetValue(threadId, out var job)) return;
        try
        {
            switch (method)
            {
                case CodexMethods.Notification.TurnStarted:
                    job.Started(From(p, CodexJsonContext.Default.TurnStartedNotification).Turn.Id);
                    break;
                case CodexMethods.Notification.ItemCompleted:
                {
                    var n = From(p, CodexJsonContext.Default.ItemCompletedNotification);
                    if (n.Item is AgentMessageThreadItem a && a.Text.Length > 0 && !job.Text(a.Text))
                        job.Fail(new InvalidOperationException("the title answer was too large"));
                    break;
                }
                case CodexMethods.Notification.TurnCompleted:
                {
                    var n = From(p, CodexJsonContext.Default.TurnCompletedNotification);
                    job.Complete(n.Turn.Status);
                    break;
                }
                case CodexMethods.Notification.ThreadClosed:
                    job.Fail(new IOException("the title thread was unloaded"));
                    break;
                case CodexMethods.Notification.ThreadStatusChanged:
                {
                    var n = From(p, CodexJsonContext.Default.ThreadStatusChangedNotification);
                    if (n.Status is NotLoadedThreadStatus) job.Fail(new IOException("the title thread was unloaded"));
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            job.Fail(e);
        }
        await Task.CompletedTask;
    }


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
            if (loaded?["data"] is not JsonArray loadedThreads) throw new InvalidDataException("invalid loaded thread list");
            if (loadedThreads.Any(x => x is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw new InvalidDataException("invalid loaded thread identity");
            if (loadedThreads.Any(x => (string?)x == t.Id))
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
        t.StopBackground();
        if (t.BackgroundTask is { } backgroundTask) await backgroundTask;
        if (threads.TryGetValue(t.Id, out var current) && ReferenceEquals(current, t))
        try { await Update(t.Id, new SessionInfoUpdate { Meta = new JsonObject
        {
            [GorynychProtocol.MetaBackgroundKnown] = null,
        } }); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { Log($"codex {t.Id}: background state not sent: {e.Message}"); }
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
        if (IsTitleHelper((string?)p?["threadId"]))
        {
            await OnTitleNotificationAsync(method, p);
            return;
        }
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
                {
                    // Native names are authoritative too, including a rename made outside our
                    // _gorynych/set_title path while the isolated request was still running.
                    Thread(n.ThreadId)?.CancelTitleJob(manual: true);
                    await Update(n.ThreadId, new SessionInfoUpdate
                    {
                        Title = n.ThreadName,
                        UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    });
                }
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
                var state = n.Status switch
                {
                    ActiveThreadStatus active when active.ActiveFlags.Any(f => f == ThreadActiveFlag.WaitingOnApproval || f == ThreadActiveFlag.WaitingOnUserInput) => "requires_action",
                    ActiveThreadStatus => "running",
                    IdleThreadStatus or NotLoadedThreadStatus or SystemErrorThreadStatus => "idle",
                    _ => null,
                };
                if (state is not null)
                    await Update(n.ThreadId, new SessionInfoUpdate { Meta = new JsonObject { [GorynychProtocol.MetaForegroundState] = state } });
                if (n.Status is not NotLoadedThreadStatus) break;
                if (unloading.TryGetValue(n.ThreadId, out var closing)) closing.TrySetResult();
                if (Thread(n.ThreadId) is { Loaded: true } t) Unloading(t, "status notLoaded");
                break;
            }
            case CodexMethods.Notification.ThreadClosed:
            {
                var n = From(p, ctx.ThreadClosedNotification);
                await Update(n.ThreadId, new SessionInfoUpdate { Meta = new JsonObject { [GorynychProtocol.MetaForegroundState] = "idle" } });
                if (unloading.TryGetValue(n.ThreadId, out var closing)) closing.TrySetResult();
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
        // A title thread never asks the person or the service: it has no tools, and anything it
        // still asks is refused here, off the visible connection.
        if (IsTitleHelper((string?)request.Params?["threadId"])) return TitleHelperRefusal(request.Method);
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

    /// <summary>What a request from a title thread gets: nothing done, never a question to the person.</summary>
    static JsonObject TitleHelperRefusal(string method) => method switch
    {
        CodexMethods.Server.ItemToolRequestUserInput => new JsonObject { ["answers"] = new JsonObject() },
        CodexMethods.Server.ItemPermissionsRequestApproval => NoPermissions(),
        CodexMethods.Server.McpServerElicitationRequest => new JsonObject { ["action"] = "cancel" },
        _ => new JsonObject { ["decision"] = "cancel" },
    };

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
        foreach (var t in threads.Values) { t.CancelTitleJob(); t.StopBackground(); }
        foreach (var t in threads.Values) if (t.BackgroundTask is { } backgroundTask) await backgroundTask;
        if (appServer.Exited.IsCompleted)
            foreach (var t in threads.Values)
                try { await Update(t.Id, new SessionInfoUpdate { Meta = new JsonObject
                { [GorynychProtocol.MetaBackgroundWork] = null, [GorynychProtocol.MetaBackgroundProcessExited] = true } }); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or JsonRpcException) { }
        foreach (var job in titleHelpers.Values) job.Fail(new IOException("Codex's app-server stopped during title generation."));
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
