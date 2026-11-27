using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service.Hosts;

/// <summary>Options of an <see cref="AdapterHost"/>.</summary>
public sealed class AdapterHostOptions
{
    /// <summary>The adapter it runs, as the conductor names it.</summary>
    public required string Adapter { get; init; }

    /// <summary>The token from the service's data folder that proves the host to its conductor.</summary>
    public required string Token { get; init; }

    /// <summary>This host's ID, new in every run: the first half of its <c>gorynych/hostSeq</c>s.</summary>
    public string HostId { get; init; } = $"h{GorynychProtocol.NewRunTag()}{Environment.ProcessId:x}";

    /// <summary>Which host of its adapter it is (<see cref="IHostLauncher"/>), as the conductor started it.</summary>
    public string Slot { get; init; } = "";

    /// <summary>What it runs: its build and its adapter's options; the conductor compares it with the generation it would start.</summary>
    public HostGeneration? Generation { get; init; }

    /// <summary>How long a host with nothing running waits for a conductor before it exits.</summary>
    public TimeSpan OrphanIdle { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a host with a turn running, or a question waiting, waits for a conductor; then it cancels and exits.</summary>
    public TimeSpan OrphanBusy { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the turns an orphan cancels get to end.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The pauses between the attempts to reach a conductor; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    /// <summary>How many session updates it keeps for a conductor that comes back.</summary>
    public int BufferLimit { get; init; } = 16384;

    /// <summary>How many bytes of them, at most: a tool's output may be large.</summary>
    public long BufferBytes { get; init; } = 32 << 20;

    /// <summary>How many of the agent's questions may wait for a conductor at once; one more is refused to the agent.</summary>
    public int QuestionLimit { get; init; } = 1000;

    /// <summary>How long a conductor's <c>initialize</c> waits for the agent's answer to an earlier one.</summary>
    public TimeSpan InitializeWait { get; init; } = TimeSpan.FromSeconds(60);

    internal ReportStore? ReportStore { get; init; }
    internal CompletionHostLedger? CompletionHostLedger { get; init; }
    internal Action? BeforeCompletionAckRecordForTests { get; init; }
    internal Action? BeforeCompletionRetireRecordForTests { get; init; }
    internal Func<string, Task>? BeforeCompletionHandlerForTests { get; init; }
    internal Func<string, Task>? AfterCompletionHandlerForTests { get; init; }
    internal Action<string>? AfterCompletionHandlerFinallyForTests { get; init; }
    internal Action<ReportCapture>? CaptureOpened { get; init; }
    internal Action? BeforeProducerEvidence { get; init; }
    internal bool PeerTerminal { get; init; }
    // Isolated protocol tests only. Production remains disabled until conductor integration.
    internal bool FilterCodexIdleForTests { get; init; }
    internal Action? IdleFrameProcessedForTests { get; init; }
    internal Func<string, Task>? BeforeIdleResponseAdmissionForTests { get; init; }
    public Action<string>? Log { get; init; }
}

/// <summary>
/// An adapter host (docs/service-protocol.md, "Adapter hosts"): it runs one adapter and its agents
/// in a process of its own, so that the conductor can restart without breaking a turn. It is the
/// agent's end of ACP toward the conductor, and passes the lines through, in order; and since the
/// agent lives on while conductors come and go, it dials each next conductor itself, and keeps for it:
/// <list type="bullet">
/// <item>the agent's <c>initialize</c> answer, which a conductor that comes back gets again, the agent
/// being initialized once;</item>
/// <item>the sessions open in the agent, and the prompts running there with their <c>gorynych/promptId</c>,
/// whose end it reports (<c>_gorynych/turn_ended</c>) before their answer, so that a conductor that never
/// got the answer learns it;</item>
/// <item>the latest session updates, each with its place in the host's stream (<c>gorynych/hostSeq</c>),
/// which it sends again after the cursors a conductor gives it, so its journal misses none and
/// doubles none;</item>
/// <item>the agent's questions not yet answered, which it asks the next conductor again.</item>
/// </list>
/// With no conductor for a while it exits: at once when idle after <see cref="AdapterHostOptions.OrphanIdle"/>,
/// else after <see cref="AdapterHostOptions.OrphanBusy"/>, cancelling what still runs.
/// </summary>
public sealed class AdapterHost
{
    static readonly JsonSerializerOptions Wire = AcpJson.TextOptions;

    readonly AgentInstance agent;
    readonly Func<CancellationToken, Task<(Stream Input, Stream Output)>> connect;
    readonly AdapterHostOptions options;
    readonly Lock state = new();
    readonly SubagentRouting subagents = new();
    readonly Dictionary<string, IdleEligibility> idleEligibility = new(StringComparer.Ordinal); // under state
    readonly Dictionary<string, (Connection Owner, string Prompt, long Admission)> claudeObservations = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> idleAdmissions = new(StringComparer.Ordinal); // under state; one per live session
    long nextIdleAdmission;
    string? noteReceiptFailure;
    readonly SemaphoreSlim sendGate = new(1, 1); // what goes to the conductor in the agent's order, and the switch to the next one
    readonly SemaphoreSlim agentGate = new(1, 1); // writes to the agent
    readonly CancellationTokenSource stopping = new();
    readonly TaskCompletionSource<int> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // under sendGate
    Connection? current;

    // under state
    readonly Dictionary<long, Forwarded> forwarded = [];
    readonly Dictionary<string, Asked> asked = new(StringComparer.Ordinal);
    readonly HashSet<string> live = new(StringComparer.Ordinal);
    readonly Dictionary<string, SessionConfiguration> configurations = new(StringComparer.Ordinal);
    readonly Dictionary<string, JsonObject> activities = new(StringComparer.Ordinal);
    readonly Queue<Kept> buffer = new();
    long bufferedBytes;
    readonly Dictionary<string, long> dropped = new(StringComparer.Ordinal); // per session, the newest update the buffer let go
    long seq;
    long nextToAgent;
    long nextConnection;
    JsonObject? initialize;
    bool initializing;
    long aloneSince = Stopwatch.GetTimestamp();
    bool reconnecting;

    public AdapterHost(AgentInstance agent, Func<CancellationToken, Task<(Stream Input, Stream Output)>> connect, AdapterHostOptions options)
    {
        this.agent = agent;
        this.connect = connect;
        this.options = options;
    }

    public string HostId => options.HostId;

    /// <summary>
    /// Runs until the agent exits or a conductor stops the host (0), or no conductor comes for too
    /// long (2).
    /// </summary>
    // Fixture-only graceful stop for a detached host; never exposed on ACP.
    internal void StopForTests() => ended.TrySetResult(0);

    public async Task<int> RunAsync()
    {
        var reading = Task.Run(AgentLoopAsync);
        StartReconnecting();
        var code = await ended.Task;
        stopping.Cancel();
        Connection? last;
        await sendGate.WaitAsync();
        last = current;
        current = null;
        sendGate.Release();
        if (last is not null) Drop(last);
        // Stop new dispatches, then release all unsealed producers under the same state lock
        // used by admission, live capture and terminal sealing. Their spools stay recoverable.
        await agentGate.WaitAsync();
        try
        {
            lock (state)
                foreach (var request in forwarded.Values)
                    if (request.Capture is { } capture)
                    {
                        try { capture.Dispose(); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"capture cleanup failed: {e.Message}"); }
                        request.Capture = null;
                    }
        }
        finally { agentGate.Release(); }
        try { await agent.DisposeAsync(); }
        catch (Exception e) { Log($"the agent did not stop cleanly: {e.Message}"); }
        _ = reading;
        return code;
    }

    // ---------------------------------------------------------------- the agent's side

    async Task AgentLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(agent.Input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
            while (await reader.ReadLineAsync(stopping.Token) is { } line)
            {
                if (line.Length == 0) continue;
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (message is null) continue;
                var method = Text(message["method"]);
                var id = message["id"];
                if (method is null) await AgentAnsweredAsync(id, message);
                else if (id is null) await AgentNotifiedAsync(method, message);
                else await AgentAsksAsync(id, method, message);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { }
        catch (Exception e)
        {
            Log($"the host failed: {e.GetType().Name}: {e.Message}");
        }
        Log("the agent's connection ended; the host exits");
        ended.TrySetResult(0);
    }

    // Terminal records must follow the outcomes of steers already in flight, even if the
    // conductor is absent. Waiting in the conductor alone cannot fix the order of host replay.
    sealed record DeferredTerminal(JsonObject Message, long? Response, long[] Steering, int Bytes);
    readonly List<DeferredTerminal> deferredTerminals = []; // under state
    long deferredTerminalBytes;

    bool DeferTerminal(JsonObject message, long? response, string sessionId)
    {
        lock (state)
        {
            var dependencies = forwarded.Where(p => p.Value.Method == GorynychProtocol.MethodSteering
                && p.Value.SessionId == sessionId).Select(p => p.Key).ToArray();
            if (dependencies.Length == 0) return false;
            var bytes = Encoding.UTF8.GetByteCount(message.ToJsonString());
            if (deferredTerminals.Count >= options.BufferLimit || deferredTerminalBytes + bytes > options.BufferBytes)
                throw new IOException("The host's pending terminal buffer overflowed while awaiting steering outcomes.");
            deferredTerminals.Add(new(message, response, dependencies, bytes));
            deferredTerminalBytes += bytes;
            return true;
        }
    }

    async Task FlushTerminalsAsync()
    {
        List<DeferredTerminal> ready;
        lock (state)
        {
            ready = deferredTerminals.Where(t => t.Steering.All(id => !forwarded.ContainsKey(id))).ToList();
            foreach (var terminal in ready)
            {
                deferredTerminals.Remove(terminal);
                deferredTerminalBytes -= terminal.Bytes;
            }
        }
        foreach (var terminal in ready)
            if (terminal.Response is { } id)
                await AgentAnsweredAsync(JsonValue.Create(id), terminal.Message, released: true);
            else
                await AgentNotifiedAsync(Text(terminal.Message["method"])!, terminal.Message, released: true);
    }

    async Task AgentAnsweredAsync(JsonNode? id, JsonObject message, bool released = false)
    {
        if (!TryId(id, out var key)) return;
        Forwarded? request;
        await sendGate.WaitAsync();
        try
        {
            // How a turn ended goes first, numbered and kept as a session's update is: the conductor
            // marks the end by it, and one that went before it took it, killed or restarting, finds it
            // again after its cursor, though the answer itself was lost on the way. The prompt leaves
            // the running ones as its end enters the buffer, so a report (host_state) has one or the other.
            string? ended = null;
            lock (state)
            {
                if (stopping.IsCancellationRequested) return;
                if (!released && forwarded.TryGetValue(key, out var pending)
                    && pending.Method == AcpMethods.Agent.SessionPrompt && pending.SessionId is { } pendingSession
                    && DeferTerminal(message, key, pendingSession)) return;
                if (!forwarded.TryGetValue(key, out request)) return;
                // Keep a guarded request reserved through durable receipt processing.
                if (request.GuardedAttempt is null) forwarded.Remove(key);
                Track(request, message);
                if (request.Method == AcpMethods.Agent.SessionPrompt && request.SessionId is { } sessionId)
                {
                    var report = new JsonObject { ["sessionId"] = sessionId, ["turn"] = key };
                    if (request.PromptId is { } promptId) report["promptId"] = promptId;
                    if (request.ReportId is { } reportId)
                    {
                        SealReport(request, message);
                        report["_meta"] = new JsonObject { [GorynychProtocol.MetaReportId] = reportId,
                            [GorynychProtocol.MetaReportCapture] = new JsonObject { ["failure"] = request.CaptureFailure } };
                    }
                    (message["result"]?["_meta"] as JsonObject)?.Remove(GorynychProtocol.MetaPeerTerminal);
                    if (request.Delivery is { } delivery)
                    {
                        if (report["_meta"] is not JsonObject deliveryMeta) report["_meta"] = deliveryMeta = new JsonObject();
                        deliveryMeta[GorynychProtocol.MetaReportDelivery] = delivery.DeepClone();
                    }
                    if (request.AskId is { } askId) report["askId"] = askId;
                    if (request.Opinion is { } opinion) report["opinion"] = opinion;
                    if (request.AskId is not null && request.From is { } from) report["from"] = from;
                    if (message["result"] is { } r) report["result"] = r.DeepClone();
                    if (message["error"] is { } e) report["error"] = e.DeepClone();
                    ended = Number(Notification(GorynychProtocol.MethodTurnEnded, report));
                }
                // So is the answer to a question the agent took by its own way in (Claude's channel):
                // the conductor that asked may be gone, and the next one gives it to the asker.
                else if (request.Method == GorynychProtocol.MethodOpinion && request.AskId is { } askId && request.SessionId is { } asked)
                {
                    var report = new JsonObject { ["sessionId"] = asked, ["askId"] = askId };
                    if (request.From is { } from) report["from"] = from;
                    if (message["result"] is { } r) report["result"] = r.DeepClone();
                    if (message["error"] is { } e) report["error"] = e.DeepClone();
                    ended = Number(Notification(GorynychProtocol.MethodAskAnswered, report));
                }
                // An accepted steer is kept before its answer, like a turn's end: a conductor that
                // journals it and then goes without recording the reply finds it after its cursor.
                else if (request.Method == GorynychProtocol.MethodSteering && request.SessionId is { } steered
                    && message["result"] is JsonObject accepted && (string?)accepted["outcome"] is AcpSteering.Injected or AcpSteering.StartedNewTurn)
                {
                    var report = new JsonObject
                    {
                        ["sessionId"] = steered,
                        ["messageId"] = request.MessageId,
                        ["outcome"] = (string?)accepted["outcome"],
                        ["prompt"] = request.Prompt?.DeepClone(),
                    };
                    if (request.PromptId is { } promptId) report["promptId"] = promptId;
                    if (request.SteerOrigin is { } origin) report["origin"] = origin;
                    ended = Number(Notification(GorynychProtocol.MethodSteerAccepted, report));
                }
            }
            if (ended is not null && current is { IsLost: false } connection) await TryWriteLineAsync(connection, ended);
        }
        finally
        {
            sendGate.Release();
        }
        if (request.GuardedAttempt is { } guardedAttempt && request.GuardedIdentity is { } guardedIdentity && options.CompletionHostLedger is { } guardedStore)
        {
            try
            {
                if (message["error"] is not null) { /* Admission remains unresolved; never retry it. */ }
                else if (request.GuardedIsAbort && message["result"]?["_meta"] is JsonObject abort && Text(abort["state"]) is "confirmed_precommit" or "already_committed")
                {
                    guardedStore.RecordAbort(guardedAttempt, guardedIdentity, abort);
                    var retained = guardedStore.ReadAttempt(guardedAttempt)!;
                    retained["attemptId"] = guardedAttempt;
                    message["result"] = new JsonObject { ["_meta"] = retained };
                }
                else if (request.GuardedRetireRevision is { } retireRevision && message["result"]?["_meta"] is JsonObject retired && Text(retired["state"]) == "retired")
                {
                    options.BeforeCompletionRetireRecordForTests?.Invoke();
                    var retained = guardedStore.RecordRetire(guardedAttempt, guardedIdentity, retireRevision, retired);
                    retained["adapterState"] = "retired";
                    message["result"] = new JsonObject { ["_meta"] = retained };
                }
                else if (request.GuardedAckRevision is { } ackRevision && message["result"]?["_meta"] is JsonObject ack && Text(ack["state"]) == "acknowledged")
                {
                    options.BeforeCompletionAckRecordForTests?.Invoke();
                    var retained = guardedStore.RecordAck(guardedAttempt, guardedIdentity, ackRevision, ack);
                    retained["adapterState"] = "acknowledged";
                    message["result"] = new JsonObject { ["_meta"] = retained };
                }
                else if (!request.GuardedIsAbort && request.GuardedAckRevision is null && request.GuardedRetireRevision is null && message["result"]?["_meta"] is JsonObject facts && Text(facts["state"]) == "receipt" && Text(facts["attemptId"]) == guardedAttempt)
                {
                    var retained = guardedStore.Record(guardedAttempt, guardedIdentity, facts);
                    retained["adapterState"] = "observed";
                    message["result"] = new JsonObject { ["_meta"] = retained };
                }
                else
                {
                    var retained = guardedStore.ReadAttempt(guardedAttempt) ?? new JsonObject();
                    retained["adapterState"] = "unknown";
                    message["result"] = new JsonObject { ["_meta"] = retained };
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
            {
                message.Remove("result");
                message["error"] = new JsonObject { ["code"] = JsonRpcException.InternalError, ["message"] = "Guarded receipt persistence unresolved; do not retry input: " + e.Message };
            }
        }
        if (request.GuardedAttempt is not null)
        {
            lock (state) forwarded.Remove(key);
            await sendGate.WaitAsync();
            try
            {
                lock (state)
                    if (current != request.Connection || request.Connection.IsLost || !request.Connection.AcceptsCompletionGuarded ||
                        request.Connection.CompletionGuardedEpoch != request.GuardedEpoch) return;
                message["id"] = request.ConductorId.DeepClone();
                await TryWriteAsync(request.Connection, message);
            }
            finally { sendGate.Release(); }
            return;
        }
        if (request.ClaudeObservationAdmission is { } queryAdmission)
        {
            await sendGate.WaitAsync();
            try
            {
                bool valid;
                lock (state) valid = current == request.Connection && !request.Connection.IsLost && ClaudeObservationSupported(request.Connection) &&
                    request.ClaudeObservationEpoch == request.Connection.ClaudeObservationEpoch &&
                    request.SessionId is { } querySession && request.PromptId is { } queryPrompt &&
                    live.Contains(querySession) && subagents.RootOf(querySession) is null &&
                    claudeObservations.TryGetValue(querySession, out var candidate) && candidate.Owner == request.Connection &&
                    candidate.Prompt == queryPrompt && candidate.Admission == queryAdmission && idleAdmissions.GetValueOrDefault(querySession) == queryAdmission;
                var meta = message["result"]?["_meta"] as JsonObject;
                if (!valid || meta is null || meta.Count != 6 || Text(meta["state"]) != "observed" ||
                    Text(meta["sessionId"]) != request.SessionId || Text(meta["promptId"]) != request.PromptId ||
                    !Guid.TryParseExact(Text(meta["nativeUuid"]), "D", out _) || !Guid.TryParseExact(Text(meta["stream"]), "N", out _) ||
                    meta["revision"] is not JsonValue revision || !revision.TryGetValue<long>(out var number) || number < 0)
                {
                    message["result"] = new JsonObject { ["_meta"] = new JsonObject { ["state"] = "unknown" } };
                }
                message.Remove("error"); message["id"] = request.ConductorId.DeepClone();
                if (!request.Connection.IsLost) await TryWriteAsync(request.Connection, message);
            }
            finally { sendGate.Release(); }
            return;
        }
        if (request.Method == AcpMethods.Agent.Initialize)
        {
            if (message["result"] is JsonObject result) message["result"] = WithHostMeta(result, request.Connection, reattached: false);
            initialized.TrySetResult();
        }
        if (request.ReportId is { } completedId && request.Method == AcpMethods.Agent.SessionPrompt)
        {
            var target = message["result"] as JsonObject ?? message["error"] as JsonObject;
            if (target is not null)
            {
                var meta = target["_meta"] as JsonObject ?? new JsonObject();
                meta[GorynychProtocol.MetaReportId] = completedId;
                meta[GorynychProtocol.MetaReportCapture] = new JsonObject { ["failure"] = request.CaptureFailure };
                target["_meta"] = meta;
            }
        }
        if (request.NoteStatusReport is { } statusReport && request.SessionId is { } statusSession &&
            message["result"]?["_meta"]?[GorynychProtocol.MetaReportNote] is JsonObject statusFacts &&
            Text(statusFacts["state"]) != "unknown")
        {
            try
            {
                if (Text(statusFacts["noInputProof"]) is { } proof)
                {
                    if (request.Method != GorynychProtocol.MethodReportNote || request.NoteAttempt is not { } attempt ||
                        request.NoteDigest is not { } digest || Text(statusFacts["reportId"]) != statusReport ||
                        Text(statusFacts["attemptId"]) != attempt || Text(statusFacts["digest"]) != digest)
                        throw new InvalidOperationException("No-input proof does not match the forwarded attempt");
                    var store = options.ReportStore ?? throw new InvalidOperationException("Report storage is unavailable");
                    var generation = options.Generation?.Key ?? throw new InvalidOperationException("Host generation is unavailable");
                    store.RecordNoteNoInput(statusReport, statusSession, options.HostId, generation, attempt, digest, proof);
                }
                else if (statusFacts.ContainsKey("ledgerNotRetained"))
                {
                    if ((bool?)statusFacts["ledgerNotRetained"] != true)
                        throw new InvalidOperationException("Ledger absence was not confirmed");
                    if (request.Method != GorynychProtocol.MethodReportNoteAck || request.NoteAckRevision is not { } absentRevision ||
                        Text(statusFacts["reportId"]) != statusReport || Text(statusFacts["attemptId"]) != request.NoteAttempt ||
                        Text(statusFacts["nativeMessageId"]) != request.NoteAckUuid || (long?)statusFacts["revision"] != absentRevision)
                        throw new InvalidOperationException("Ledger absence does not match the forwarded ACK request");
                    var store = options.ReportStore ?? throw new InvalidOperationException("Report storage is unavailable");
                    var generation = options.Generation?.Key ?? throw new InvalidOperationException("Host generation is unavailable");
                    if (!store.RecordNoteLedgerNotRetained(statusReport, statusSession, options.HostId, generation, request.NoteAttempt!, request.NoteAckUuid!, absentRevision))
                        throw new InvalidOperationException("Receipt changed before ledger absence publication");
                }
                else
                {
                    StoreNoteFacts(statusReport, statusSession, statusFacts);
                    if (request.NoteAckRevision is { } revision && (bool?)statusFacts["acknowledged"] == true)
                    {
                        if (Text(statusFacts["attemptId"]) != request.NoteAttempt || Text(statusFacts["nativeMessageId"]) != request.NoteAckUuid ||
                            (long?)statusFacts["revision"] != revision)
                            throw new InvalidOperationException("Adapter ACK does not match the requested receipt");
                        var store = options.ReportStore ?? throw new InvalidOperationException("Report storage is unavailable");
                        var generation = options.Generation?.Key ?? throw new InvalidOperationException("Host generation is unavailable");
                        if (!store.RecordNoteAcknowledged(statusReport, statusSession, options.HostId, generation, request.NoteAttempt!, revision))
                            throw new InvalidOperationException("Receipt changed before ACK publication");
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                lock (state) noteReceiptFailure = "Report note evidence could not be persisted: " + e.GetType().Name;
                message.Remove("result");
                message["error"] = new JsonObject { ["code"] = JsonRpcException.InternalError, ["message"] = noteReceiptFailure };
            }
            if (message["error"] is null && (request.Method == GorynychProtocol.MethodReportNote ||
                request.NoteAckRevision is not null && ((bool?)statusFacts["acknowledged"] == true || (bool?)statusFacts["ledgerNotRetained"] == true)))
            {
                try { await EmitAsync(Notification(GorynychProtocol.MethodHostState, new JsonObject { ["_meta"] = new JsonObject
                    { [GorynychProtocol.CapHostReportNote] = new JsonObject { ["reportId"] = statusReport, ["sessionId"] = statusSession } } })); }
                catch (Exception notifyError) when (notifyError is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
        }
        // Native errors cannot impersonate a host admission receipt.
        if (message["error"]?["data"] is JsonObject nativeErrorData)
            nativeErrorData.Remove(GorynychProtocol.MetaReportRefused);
        message["id"] = request.ConductorId.DeepClone();
        var answered = !request.Connection.IsLost && await TryWriteAsync(request.Connection, message);
        if (answered && IdleSupported(request.Connection) &&
            request.Method == AcpMethods.Agent.SessionPrompt && request.SessionId is { } eligibleSession &&
            request.PromptId is { Length: > 0 and <= 256 } eligiblePrompt &&
            Text(message["result"]?["stopReason"]) == "end_turn" &&
            Text(message["result"]?["_meta"]?[GorynychProtocol.MetaHistoryMark]) is { Length: > 0 and <= 256 } nativeTurn)
        {
            if (options.BeforeIdleResponseAdmissionForTests is { } beforeAdmission) await beforeAdmission(eligiblePrompt);
            await sendGate.WaitAsync();
            try
            {
                lock (state)
                    if (current == request.Connection && !current.IsLost && live.Contains(eligibleSession) && subagents.RootOf(eligibleSession) is null &&
                        request.IdleAdmission is { } admission && idleAdmissions.GetValueOrDefault(eligibleSession) == admission)
                        idleEligibility[eligibleSession] = new IdleEligibility(request.Connection, eligiblePrompt, nativeTurn);
            }
            finally { sendGate.Release(); }
        }
        if (answered && ClaudeObservationSupported(request.Connection) && request.Method == AcpMethods.Agent.SessionPrompt &&
            request.TurnKind != "report" && request.SessionId is { } observedSession && request.PromptId is { Length: > 0 and <= 256 } observedPrompt &&
            Text(message["result"]?["stopReason"]) == "end_turn")
        {
            await sendGate.WaitAsync();
            try
            {
                lock (state)
                {
                    if (current == request.Connection && !current.IsLost && live.Contains(observedSession) &&
                        subagents.RootOf(observedSession) is null && request.ClaudeObservationEpoch == request.Connection.ClaudeObservationEpoch &&
                        request.IdleAdmission is { } admission && idleAdmissions.GetValueOrDefault(observedSession) == admission)
                        claudeObservations[observedSession] = (request.Connection, observedPrompt, admission);
                }
            }
            finally { sendGate.Release(); }
        }
        if (request.Method == GorynychProtocol.MethodSteering) await FlushTerminalsAsync();
    }

    /// <summary>Follows the sessions open in the agent and its prompts. Under <see cref="state"/>.</summary>
    void Track(Forwarded request, JsonObject response)
    {
        var result = response["result"] as JsonObject;
        switch (request.Method)
        {
            case AcpMethods.Agent.Initialize when result is not null:
                initialize = (JsonObject)result.DeepClone();
                initializing = false;
                break;
            case AcpMethods.Agent.Initialize:
                initializing = false;
                break;
            case AcpMethods.Agent.SessionNew when Text(result?["sessionId"]) is { } created:
                live.Add(created);
                Configuration(created).Capture(result);
                break;
            case AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionResume when result is not null && request.SessionId is { } opened:
                live.Add(opened);
                Configuration(opened).Capture(result);
                break;
            case AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionResume when result is null && request.SessionId is { } failed && !live.Contains(failed):
                activities.Remove(failed);
                configurations.Remove(failed);
                break;
            case AcpMethods.Agent.SessionSetConfigOption when result is not null && request.SessionId is { } configured:
                Configuration(configured).Capture(result);
                break;
            case AcpMethods.Agent.SessionSetMode when result is not null && request.SessionId is { } modeSession:
                Configuration(modeSession).SetCurrent("modes", "currentModeId", JsonValue.Create(request.ModeId));
                break;
            case "session/set_model" when result is not null && request.SessionId is { } modelSession:
                Configuration(modelSession).SetCurrent("models", "currentModelId", JsonValue.Create(request.ModelId));
                break;
            case AcpMethods.Agent.SessionClose or AcpMethods.Agent.SessionDelete when request.SessionId is { } closed:
                live.Remove(closed);
                subagents.Forget(closed);
                configurations.Remove(closed);
                activities.Remove(closed);
                break;
        }
    }

    // Under state. Only sessions open in this host have retained configuration.
    SessionConfiguration Configuration(string sessionId)
    {
        if (!configurations.TryGetValue(sessionId, out var configuration))
            configurations[sessionId] = configuration = new();
        return configuration;
    }

    JsonObject SessionConfigurations() => new(live.Where(configurations.ContainsKey)
        .Select(id => new KeyValuePair<string, JsonNode?>(id, configurations[id].Snapshot())));

    void StoreNoteFacts(string id, string session, JsonObject facts)
    {
        var store = options.ReportStore ?? throw new InvalidOperationException("Report storage is unavailable");
        var generation = options.Generation?.Key ?? throw new InvalidOperationException("Host generation is unavailable");
        var bounded = (JsonObject)facts.DeepClone();
        foreach (var field in new[] { "nativeError", "nativeSubtype", "nativeStopReason", "nativeTerminalReason", "dropReason", "uncertainty" })
            if (bounded[field] is JsonValue value && value.TryGetValue<string>(out var text)) bounded[field] = BoundReceiptField(text);
        store.RecordNoteReceipt(id, session, options.HostId, generation, bounded);
    }

    async Task AgentNotifiedAsync(string method, JsonObject message, bool released = false)
    {
        if (method is GorynychProtocol.MethodCodexProvisionalIdle or GorynychProtocol.MethodHostCodexProvisionalIdle)
        {
            if (method == GorynychProtocol.MethodCodexProvisionalIdle) await FilterIdleFrameAsync(message);
            options.IdleFrameProcessedForTests?.Invoke();
            return;
        }
        // Host-state is generated only by this host; the adapter cannot forge its retained inventory.
        if (method == GorynychProtocol.MethodHostState) return;
        if (method == GorynychProtocol.MethodHostReportNoteReceipt)
        {
            string? failedSession = null, failedReport = null;
            try
            {
                var session = failedSession = Text(message["params"]?["sessionId"]) ?? throw new ArgumentException("Missing receipt session");
                var facts = message["params"]?["_meta"]?[GorynychProtocol.MetaReportNote] as JsonObject ?? throw new ArgumentException("Missing receipt facts");
                var id = failedReport = Text(facts["reportId"]) ?? throw new ArgumentException("Missing receipt report");
                StoreNoteFacts(id, session, facts);
                // A hint is emitted only after durable publication; recovery also scans
                // the manifest, so this notification need not survive host replay eviction.
                try { await EmitAsync(Notification(GorynychProtocol.MethodHostState, new JsonObject { ["_meta"] = new JsonObject
                    { [GorynychProtocol.CapHostReportNote] = new JsonObject { ["reportId"] = id, ["sessionId"] = session } } })); }
                catch (Exception notifyError) when (notifyError is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                lock (state) noteReceiptFailure = "Report note evidence could not be persisted: " + e.GetType().Name;
                Log(noteReceiptFailure);
                try { await EmitAsync(Notification(GorynychProtocol.MethodHostState, new JsonObject { ["_meta"] = new JsonObject
                    { [GorynychProtocol.CapHostReportNote] = new JsonObject { ["failure"] = noteReceiptFailure,
                        ["reportId"] = failedReport, ["sessionId"] = failedSession } } })); }
                catch (Exception notifyError) when (notifyError is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            return; // private adapter-to-host evidence is not ordinary replay or a public update
        }
        if (!released && method == AcpMethods.Client.SessionUpdate
            && Text(message["params"]?["sessionId"]) is { } sessionId
            && Text(message["params"]?["update"]?["_meta"]?[GorynychProtocol.MetaAgentTurn]) == "ended"
            && DeferTerminal(message, null, sessionId)) return;
        if (method == AcpMethods.Protocol.CancelRequest)
        {
            // The agent withdraws a question: withdrawn from the conductor that has it.
            var agentId = message["params"]?["requestId"];
            Asked? question = null;
            lock (state)
                if (agentId is not null && asked.Remove(agentId.ToJsonString(), out var found)) question = found;
            if (question is { Connection: { IsLost: false } on, ConductorId: { } conductorId })
                await TryWriteAsync(on, Notification(AcpMethods.Protocol.CancelRequest, new JsonObject { ["requestId"] = conductorId }));
            return;
        }
        await EmitAsync(message);
    }

    bool ClaudeObservationSupported(Connection connection) => options.Adapter == "claude" && connection.AcceptsClaudeObservation &&
        initialize?["_meta"]?[GorynychProtocol.CapClaudePromptObservation] is JsonValue value && value.TryGetValue<bool>(out var supported) && supported;

    bool IdleSupported(Connection connection) => options.Adapter == "codex" && (options.FilterCodexIdleForTests ||
        (connection.AcceptsIdle && initialize?["_meta"]?[GorynychProtocol.CapCodexIdleAdapter] is JsonValue v && v.TryGetValue<bool>(out var supported) && supported));

    // Never enters Number/buffer/replay. Only the producing live adapter and the same
    // admitted conductor connection can use a successful response's exact identity.
    async Task FilterIdleFrameAsync(JsonObject message)
    {
        if (options.Adapter != "codex" || message["params"] is not JsonObject parameters ||
            parameters.Count != 1 || parameters["_meta"] is not JsonObject meta || meta.Count != 6 ||
            Text(meta["sessionId"]) is not { Length: > 0 and <= 256 } session ||
            Text(meta["promptId"]) is not { Length: > 0 and <= 256 } prompt ||
            Text(meta["nativeTurnId"]) is not { Length: > 0 and <= 256 } native ||
            Text(meta["state"]) is not ("confirmed" or "revoked") ||
            meta["turnEpoch"] is not JsonValue e || !e.TryGetValue<long>(out var epoch) || epoch < 1 ||
            meta["ingressRevision"] is not JsonValue r || !r.TryGetValue<long>(out var revision) || revision < 0) return;
        await sendGate.WaitAsync();
        try
        {
            Connection? destination;
            lock (state)
            {
                if (!live.Contains(session) || subagents.RootOf(session) is not null || current is null || current.IsLost || !IdleSupported(current) ||
                    !idleEligibility.TryGetValue(session, out var entry) || entry.Owner != current ||
                    entry.Prompt != prompt || entry.NativeTurn != native) return;
                var frameState = Text(meta["state"]);
                if (frameState == "confirmed")
                {
                    if (entry.Seen || entry.Revoked) return;
                    entry.Seen = true; entry.Epoch = epoch; entry.Revision = revision;
                }
                else
                {
                    if (!entry.Seen || entry.Revoked || entry.Epoch != epoch || entry.Revision != revision) return;
                    entry.Revoked = true;
                }
                destination = current;
            }
            await TryWriteAsync(destination, Notification(GorynychProtocol.MethodHostCodexProvisionalIdle,
                new JsonObject { ["_meta"] = meta.DeepClone() }));
        }
        finally { sendGate.Release(); }
    }

    async Task AgentAsksAsync(JsonNode id, string method, JsonObject message)
    {
        var question = new Asked { AgentId = id.DeepClone(), Message = message };
        var key = id.ToJsonString();
        bool full;
        lock (state)
        {
            full = asked.Count >= options.QuestionLimit;
            if (!full) asked[key] = question;
        }
        if (full)
        {
            Log($"{options.QuestionLimit} of the agent's questions wait already; {method} is refused");
            await WriteToAgentAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = JsonRpcException.InternalError,
                    ["message"] = $"Gorynych has {options.QuestionLimit} questions of this agent waiting for an answer already; ask again once they are answered.",
                },
            });
            return;
        }
        await sendGate.WaitAsync();
        try
        {
            // A conductor that took the host meanwhile asked it already (ActivateAsync); one the agent withdrew is asked no more.
            bool open;
            lock (state) open = asked.TryGetValue(key, out var still) && ReferenceEquals(still, question) && question.Connection is null;
            if (open && current is { IsLost: false } connection) await AskAsync(connection, question);
        }
        finally
        {
            sendGate.Release();
        }
    }

    /// <summary>Asks the agent's question on a connection, under a number of the connection's own. Under <see cref="sendGate"/>.</summary>
    async Task AskAsync(Connection connection, Asked question)
    {
        JsonObject message;
        lock (state)
        {
            question.Connection = connection;
            question.ConductorId = ++connection.NextAsk;
            message = (JsonObject)question.Message.DeepClone();
        }
        message["id"] = question.ConductorId;
        await TryWriteAsync(connection, message);
    }

    /// <summary>
    /// A notification of the agent's for the conductor: numbered, kept (a session's update), and sent
    /// on if there is a conductor. A session update carries its number in its own <c>_meta</c>, so the
    /// conductor journals it with the update.
    /// </summary>
    async Task EmitAsync(JsonObject message)
    {
        await sendGate.WaitAsync();
        try
        {
            string line;
            lock (state)
            {
                CaptureUpdate(message);
                line = Number(message);
            }
            if (current is { IsLost: false } connection) await TryWriteLineAsync(connection, line);
        }
        finally
        {
            sendGate.Release();
        }
    }

    /// <summary>
    /// Numbers a notification of the agent's (<c>gorynych/hostSeq</c>) and keeps it, if it is a
    /// session's, within the buffer's bounds; returns its line. Under <see cref="sendGate"/> and
    /// <see cref="state"/>, so the numbers follow the order the conductor gets them in.
    /// </summary>
    string Number(JsonObject message)
    {
        var @params = message["params"] as JsonObject;
        var target = Text(@params?["sessionId"]);
        var root = target is null ? null : subagents.RootOf(target);
        var sessionId = root ?? target;
        if (Text(message["method"]) == AcpMethods.Client.SessionUpdate && target is not null
            && SubagentRouting.Announcement(@params?["update"]) is { } child)
            subagents.Announce(target, child, sessionId!);
        if (Text(message["method"]) == AcpMethods.Client.SessionUpdate && root is null && sessionId is not null
            && (live.Contains(sessionId) || forwarded.Values.Any(request => request.SessionId == sessionId && request.Method is AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionResume))
            && @params?["update"] is JsonObject update)
        {
            Configuration(sessionId).Apply(update);
            if (update["_meta"] is JsonObject activityMeta)
            {
                // Adapter-supplied values cannot impersonate this host-generated observation.
                activityMeta.Remove(GorynychProtocol.MetaHostBackgroundEvidence);
                // Private live evidence is only accepted from the actual Codex adapter while
                // the session is open and no history operation can emit historical updates.
                var discontinuity = activityMeta[GorynychProtocol.MetaCodexLoadedIdle] is JsonValue marker &&
                    marker.TryGetValue<string>(out var markerText) && markerText == "discontinuity";
                if (options.Adapter != "codex" || !live.Contains(sessionId) ||
                    !discontinuity && forwarded.Values.Any(f => f.SessionId == sessionId && IsHistoryOperation(f.Method)))
                    activityMeta.Remove(GorynychProtocol.MetaCodexLoadedIdle);
                var foreground = Text(activityMeta[GorynychProtocol.MetaForegroundState]);
                var turn = Text(activityMeta[GorynychProtocol.MetaAgentTurn]);
                var hasBackground = Text(update["sessionUpdate"]) == "session_info_update" && activityMeta.ContainsKey(GorynychProtocol.MetaBackgroundWork);
                var hasKnown = Text(update["sessionUpdate"]) == "session_info_update" && activityMeta.ContainsKey(GorynychProtocol.MetaBackgroundKnown);
                var processExited = activityMeta[GorynychProtocol.MetaBackgroundProcessExited] is JsonValue exitValue && exitValue.TryGetValue<bool>(out var exited) && exited;
                if (foreground is "running" or "idle" or "requires_action" || turn is "started" or "ended" || hasBackground || hasKnown)
                {
                    if (!activities.TryGetValue(sessionId, out var activity)) activities[sessionId] = activity = [];
                    if (foreground is "running" or "idle" or "requires_action") activity["foreground"] = foreground;
                    if (turn is "started" or "ended")
                    {
                        activity["agentTurn"] = turn == "started";
                        activity["agentTurnKind"] = turn == "started" && Text(activityMeta[GorynychProtocol.MetaOrigin]) == "report" ? "report" : "unknown";
                        activity["agentReportDelivery"] = turn == "started" ? activityMeta[GorynychProtocol.MetaReportDelivery]?.DeepClone() : null;
                    }
                    if (hasBackground || hasKnown)
                    {
                        var historyInFlight = forwarded.Values.Any(f => f.SessionId == sessionId && IsHistoryOperation(f.Method));
                        var evidence = options.Adapter == "claude" && live.Contains(sessionId) && !historyInFlight && hasBackground && !processExited
                            ? BackgroundEvidence.FromSnapshot(options.HostId, seq + 1,
                                Text(activityMeta[GorynychProtocol.MetaBackgroundCompleteStream]), activityMeta[GorynychProtocol.MetaBackgroundWork]) : null;
                        var observation = evidence?.ToJson() ?? BackgroundEvidence.Unknown(options.HostId, seq + 1);
                        activity["backgroundEvidence"] = observation.DeepClone();
                        activityMeta[GorynychProtocol.MetaHostBackgroundEvidence] = observation;
                    }
                    if (hasBackground)
                    {
                        var raw = activityMeta[GorynychProtocol.MetaBackgroundWork];
                        var valid = BackgroundWorkSnapshot.TryParse(raw, out var background);
                        activity["background"] = valid ? background?.ToJson() : null;
                        activity["backgroundProcessExited"] = valid && raw is null && processExited;
                        if (valid && background is not null) activity["backgroundHeld"] = background.Count > 0;
                        else if (valid && raw is null && processExited) activity["backgroundHeld"] = false;
                        if (valid && raw is null && processExited) { activity["backgroundKnown"] = null; activity["backgroundKnownHeld"] = false; }
                    }
                    if (hasKnown)
                    {
                        var valid = BackgroundWorkSnapshot.TryParse(activityMeta[GorynychProtocol.MetaBackgroundKnown], out var known);
                        activity["backgroundKnown"] = valid ? known?.ToJson() : null;
                        if (valid && known is not null) activity["backgroundKnownHeld"] = known.Count > 0;
                    }
                }
            }
        }
        var number = ++seq;
        var carrier = Text(message["method"]) == AcpMethods.Client.SessionUpdate ? @params?["update"] as JsonObject : @params;
        if (SubagentRouting.IsCurrentSnapshot(carrier)) return message.ToJsonString(Wire);
        // Retain the snapshot separately; preserve any unrelated update in replay.
        var backgroundSnapshot = Text(message["method"]) == AcpMethods.Client.SessionUpdate
            && Text(carrier?["sessionUpdate"]) == "session_info_update"
            && carrier?["_meta"] is JsonObject backgroundMeta && (backgroundMeta.ContainsKey(GorynychProtocol.MetaBackgroundWork) || backgroundMeta.ContainsKey(GorynychProtocol.MetaBackgroundKnown));
        var privateIdle = carrier?["_meta"] is JsonObject privateMeta && privateMeta.ContainsKey(GorynychProtocol.MetaCodexLoadedIdle);
        var replayMessage = backgroundSnapshot || privateIdle ? (JsonObject)message.DeepClone() : message;
        if (privateIdle && replayMessage["params"]?["update"]?["_meta"] is JsonObject privateReplay)
            privateReplay.Remove(GorynychProtocol.MetaCodexLoadedIdle);
        var replayCarrier = Text(message["method"]) == AcpMethods.Client.SessionUpdate ? replayMessage["params"]?["update"] as JsonObject : replayMessage["params"] as JsonObject;
        if (backgroundSnapshot && replayCarrier?["_meta"] is JsonObject replayMeta)
        {
            replayMeta.Remove(GorynychProtocol.MetaHostBackgroundEvidence);
            replayMeta.Remove(GorynychProtocol.MetaBackgroundCompleteStream);
            replayMeta.Remove(GorynychProtocol.MetaBackgroundWork);
            replayMeta.Remove(GorynychProtocol.MetaBackgroundKnown);
            replayMeta.Remove(GorynychProtocol.MetaBackgroundProcessExited);
            replayMeta.Remove(GorynychProtocol.MetaForegroundState);
            if (Text(replayMeta[GorynychProtocol.MetaAgentTurn]) == "started"
                || (Text(replayMeta[GorynychProtocol.MetaAgentTurn]) == "ended"
                    && !replayMeta.ContainsKey(GorynychProtocol.MetaHistoryMark)
                    && !replayMeta.ContainsKey(GorynychProtocol.MetaPromptId)))
                replayMeta.Remove(GorynychProtocol.MetaAgentTurn);
            if (replayMeta.Count == 0) replayCarrier.Remove("_meta");
        }
        var emptyActivity = (backgroundSnapshot || privateIdle) && Text(replayCarrier?["sessionUpdate"]) == "session_info_update"
            && replayCarrier!.All(pair => pair.Key == "sessionUpdate" || pair.Key == "_meta" && pair.Value is JsonObject m &&
                m.All(entry => entry.Key == GorynychProtocol.MetaHostSeq));
        var kept = carrier is not null && sessionId is not null && !emptyActivity;
        if (kept)
        {
            if (carrier!["_meta"] is not JsonObject meta) carrier["_meta"] = meta = [];
            meta[GorynychProtocol.MetaHostSeq] = $"{options.HostId}/{number}";
            if ((backgroundSnapshot || privateIdle) && replayCarrier is not null)
            {
                if (replayCarrier["_meta"] is not JsonObject stampedMeta) replayCarrier["_meta"] = stampedMeta = [];
                stampedMeta[GorynychProtocol.MetaHostSeq] = $"{options.HostId}/{number}";
            }
        }
        var line = message.ToJsonString(Wire);
        if (!kept) return line;
        var replayLine = backgroundSnapshot || privateIdle ? replayMessage.ToJsonString(Wire) : line;
        buffer.Enqueue(new Kept(number, sessionId!, replayLine));
        bufferedBytes += replayLine.Length;
        while (buffer.Count > options.BufferLimit || (bufferedBytes > options.BufferBytes && buffer.Count > 1))
        {
            var old = buffer.Dequeue();
            bufferedBytes -= old.Line.Length;
            dropped[old.SessionId] = old.Seq;
        }
        return line;
    }

    // ---------------------------------------------------------------- the conductor's side

    void StartReconnecting()
    {
        lock (state)
        {
            if (reconnecting) return;
            reconnecting = true;
        }
        _ = Task.Run(ReconnectAsync);
    }

    async Task ReconnectAsync()
    {
        try
        {
            for (var attempt = 0; !stopping.IsCancellationRequested; attempt++)
            {
                if (await OrphanedAsync()) return;
                var delays = options.RetryDelays;
                var delay = delays.Count == 0 ? TimeSpan.Zero : delays[Math.Min(attempt, delays.Count - 1)];
                if (attempt > 0)
                    try { await Task.Delay(delay, stopping.Token); }
                    catch (OperationCanceledException) { return; }
                Connection? connection = null;
                try
                {
                    var streams = await connect(stopping.Token);
                    lock (state) connection = new Connection(++nextConnection, streams.Input, streams.Output);
                    _ = Task.Run(() => ConductorLoopAsync(connection));
                    var hello = new JsonObject
                    {
                        ["token"] = options.Token, ["hostId"] = options.HostId, ["adapter"] = options.Adapter,
                        ["link"] = GorynychProtocol.HostLinkVersion, ["pid"] = Environment.ProcessId, ["version"] = AgentHost.Version,
                        ["slot"] = options.Slot,
                    };
                    if (options.Generation is { } generation)
                    {
                        hello["build"] = generation.Build;
                        hello["options"] = new JsonArray(generation.Options.Select(o => (JsonNode)o).ToArray());
                    }
                    if (!await TryWriteAsync(connection, Notification(GorynychProtocol.MethodHostHello, hello))) continue;
                    // The conductor takes it, or closes it.
                    var outcome = await Task.WhenAny(connection.Activated.Task, connection.Gone.Task, Task.Delay(TimeSpan.FromSeconds(60), stopping.Token));
                    if (outcome == connection.Activated.Task)
                    {
                        Log($"connected to the conductor ({attempt + 1} attempt{(attempt == 0 ? "" : "s")})");
                        return;
                    }
                    Drop(connection);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    if (connection is not null) Drop(connection);
                    return;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    if (connection is not null) Drop(connection);
                }
            }
        }
        finally
        {
            lock (state) reconnecting = false;
            await AgainIfLostAsync();
        }
    }

    /// <summary>
    /// The connection the loop got was lost before the loop saw it was taken: LostAsync found the
    /// loop still running, and left it to it.
    /// </summary>
    async Task AgainIfLostAsync()
    {
        bool again;
        await sendGate.WaitAsync();
        try { again = current is null && !ended.Task.IsCompleted && !stopping.IsCancellationRequested; }
        finally { sendGate.Release(); }
        if (again) StartReconnecting();
    }

    /// <summary>
    /// No conductor for too long: an idle host exits; a busy one cancels its turns and exits. True
    /// once it is ending.
    /// </summary>
    async Task<bool> OrphanedAsync()
    {
        bool busy;
        TimeSpan alone;
        lock (state)
        {
            busy = forwarded.Values.Any(f => f.Method == AcpMethods.Agent.SessionPrompt) || asked.Count > 0
                || activities.Values.Any(ActivityRunning);
            alone = Stopwatch.GetElapsedTime(aloneSince);
        }
        if (alone < (busy ? options.OrphanBusy : options.OrphanIdle)) return false;
        List<string> running;
        lock (state) running = forwarded.Values.Where(f => f.Method == AcpMethods.Agent.SessionPrompt).Select(f => f.SessionId).OfType<string>().Distinct().ToList();
        Log($"no conductor for {alone.TotalSeconds:0} s; cancelling {running.Count} turns and exiting");
        foreach (var sessionId in running)
            await WriteToAgentAsync(Notification(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId }));
        var grace = Stopwatch.StartNew();
        while (grace.Elapsed < options.CancelGrace)
        {
            lock (state)
                if (!forwarded.Values.Any(f => f.Method == AcpMethods.Agent.SessionPrompt)) break;
            await Task.Delay(100);
        }
        ended.TrySetResult(2);
        return true;
    }

    async Task ConductorLoopAsync(Connection connection)
    {
        try
        {
            using var reader = new StreamReader(connection.Input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
            while (await reader.ReadLineAsync(connection.Lost.Token) is { } line)
            {
                if (line.Length == 0) continue;
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (message is null) continue;
                var method = Text(message["method"]);
                var id = message["id"];
                if (method is null) await ConductorAnsweredAsync(connection, id, message);
                else if (id is null) await ConductorNotifiedAsync(connection, method, message);
                else if (options.CompletionHostLedger is not null && method is (GorynychProtocol.MethodHostCompletionGuardedSubmit or GorynychProtocol.MethodHostCompletionGuardedStatus or GorynychProtocol.MethodHostCompletionGuardedAck or GorynychProtocol.MethodHostCompletionGuardedRetire or GorynychProtocol.MethodHostCompletionGuardedAbort))
                {
                    // Abort and retire must remain available while a submit waits at its writer.
                    var abort = method is GorynychProtocol.MethodHostCompletionGuardedAbort or GorynychProtocol.MethodHostCompletionGuardedRetire;
                    GuardedLease? lease = null;
                    lock (state)
                    {
                        var alreadyForwarded = forwarded.Values.Any(f => f.Connection == connection && f.GuardedAttempt is not null &&
                            (f.GuardedIsAbort || f.GuardedRetireRevision is not null) == abort);
                        var occupied = abort ? connection.GuardedAbortLease : connection.CompletionGuardedLease;
                        if (!alreadyForwarded && occupied is null)
                        {
                            lease = new GuardedLease(this, connection, abort);
                            if (abort) connection.GuardedAbortLease = lease; else connection.CompletionGuardedLease = lease;
                        }
                    }
                    if (lease is null)
                        await TryWriteAsync(connection, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                            ["error"] = new JsonObject { ["code"] = JsonRpcException.InvalidRequest, ["message"] = "Guarded bridge busy" } });
                    else _ = HandleCompletionGuardedRequestAsync(connection, id.DeepClone(), method, message, lease);
                }
                else await ConductorAsksAsync(connection, id, method, message);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { }
        catch (Exception e)
        {
            Log($"the connection to the conductor failed: {e.GetType().Name}: {e.Message}");
        }
        await LostAsync(connection);
    }

    // A bounded detached operation keeps the private host reader responsive. Keeping the reader live lets it
    // observe disconnect and capability changes while a guarded write is waiting for a gate.
    async Task HandleCompletionGuardedRequestAsync(Connection connection, JsonNode id, string method, JsonObject message, GuardedLease lease)
    {
        try
        {
            if (options.BeforeCompletionHandlerForTests is { } before) await before(method);
            await ConductorAsksAsync(connection, id, method, message, lease);
            if (options.AfterCompletionHandlerForTests is { } barrier) await barrier(method);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            lease.Release();
            await TryWriteAsync(connection, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                ["error"] = new JsonObject { ["code"] = JsonRpcException.InternalError, ["message"] = "Guarded bridge unresolved" } });
        }
        finally { lease.Release(); options.AfterCompletionHandlerFinallyForTests?.Invoke(method); }
    }

    async Task ConductorAnsweredAsync(Connection connection, JsonNode? id, JsonObject message)
    {
        if (!TryId(id, out var number)) return;
        Asked? question;
        lock (state)
        {
            question = asked.Values.FirstOrDefault(q => q.Connection == connection && q.ConductorId == number);
            if (question is not null) asked.Remove(question.AgentId.ToJsonString());
        }
        if (question is null) return;
        message["id"] = question.AgentId.DeepClone();
        await WriteToAgentAsync(message);
    }

    async Task ConductorNotifiedAsync(Connection connection, string method, JsonObject message)
    {
        if (method is GorynychProtocol.MethodClaudePromptObservation or GorynychProtocol.MethodHostClaudePromptObservation or
            GorynychProtocol.MethodCompletionGuardedSubmit or GorynychProtocol.MethodCompletionGuardedStatus or GorynychProtocol.MethodCompletionGuardedAck or GorynychProtocol.MethodCompletionGuardedRetire or GorynychProtocol.MethodCompletionGuardedAbort or
            GorynychProtocol.MethodHostCompletionGuardedSubmit or GorynychProtocol.MethodHostCompletionGuardedStatus or GorynychProtocol.MethodHostCompletionGuardedAck or GorynychProtocol.MethodHostCompletionGuardedRetire or GorynychProtocol.MethodHostCompletionGuardedAbort) return;
        if (method == AcpMethods.Agent.SessionCancel && Text(message["params"]?["sessionId"]) is { } session && ClaudeObservationSupported(connection))
        {
            await sendGate.WaitAsync();
            try { lock (state) if (current == connection && !connection.IsLost) { claudeObservations.Remove(session);
                if (live.Contains(session)) idleAdmissions[session] = ++nextIdleAdmission; else idleAdmissions.Remove(session);
 } }
            finally { sendGate.Release(); }
        }
        if (method == AcpMethods.Protocol.CancelRequest)
        {
            var conductorId = message["params"]?["requestId"];
            long? key = null;
            lock (state)
                foreach (var (k, f) in forwarded)
                    if (f.Connection == connection && JsonNode.DeepEquals(f.ConductorId, conductorId)) { key = k; break; }
            if (key is { } found) await WriteToAgentAsync(Notification(AcpMethods.Protocol.CancelRequest, new JsonObject { ["requestId"] = found }));
            return;
        }
        await WriteToAgentAsync(message);
    }

    async Task ConductorAsksAsync(Connection connection, JsonNode id, string method, JsonObject message, GuardedLease? lease = null)
    {
        Task<bool> RespondAsync(JsonObject response) { lease?.Release(); return TryWriteAsync(connection, response); }
        JsonObject? guardedIdentity = null;
        long? guardedAdmissionNumber = null, guardedEpoch = null, guardedWriteAdmission = null, guardedAckRevision = null, guardedRetireRevision = null;
        string? guardedAttempt = null;
        var guardedIsAbort = method == GorynychProtocol.MethodHostCompletionGuardedAbort;
        var guardedIsRetire = method == GorynychProtocol.MethodHostCompletionGuardedRetire;
        if (method is GorynychProtocol.MethodCompletionGuardedSubmit or GorynychProtocol.MethodCompletionGuardedStatus or GorynychProtocol.MethodCompletionGuardedAck or GorynychProtocol.MethodCompletionGuardedRetire or GorynychProtocol.MethodCompletionGuardedAbort)
        {
            await RespondAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                ["error"] = new JsonObject { ["code"] = JsonRpcException.MethodNotFound, ["message"] = "Private adapter method" } });
            return;
        }
        if (method is GorynychProtocol.MethodHostCompletionGuardedSubmit or GorynychProtocol.MethodHostCompletionGuardedStatus or GorynychProtocol.MethodHostCompletionGuardedAck or GorynychProtocol.MethodHostCompletionGuardedRetire or GorynychProtocol.MethodHostCompletionGuardedAbort)
        {
            try
            {
                var store = options.CompletionHostLedger ?? throw new InvalidOperationException("Completion bridge disabled");
                var p = message["params"] as JsonObject ?? throw new ArgumentException("Missing params");
                var meta = p["_meta"] as JsonObject ?? throw new ArgumentException("Missing metadata");
                string Field(string key, int max = 256) => Text(meta[key]) is { Length: > 0 } value && value.Length <= max
                    ? value : throw new ArgumentException("Invalid " + key);
                var session = Field("sessionId"); guardedAttempt = Field("attemptId", 32);
                if (!Guid.TryParseExact(guardedAttempt, "N", out _) || guardedAttempt != guardedAttempt.ToLowerInvariant()) throw new ArgumentException("Invalid attempt");
                await sendGate.WaitAsync();
                try
                {
                    lock (state)
                        if (current != connection || connection.IsLost || !connection.AcceptsCompletionGuarded || options.Adapter != "claude" ||
                            !live.Contains(session) || subagents.RootOf(session) is not null ||
                            initialize?["_meta"]?[GorynychProtocol.CapCompletionGuardedAdapter]?.GetValue<bool>() != true)
                            throw new InvalidOperationException("Completion bridge unavailable");
                    if (method == GorynychProtocol.MethodHostCompletionGuardedSubmit)
                    {
                        var prompt = Field("promptId"); var native = Field("nativeUuid"); var stream = Field("stream");
                        if (!Guid.TryParseExact(native, "D", out _) || !Guid.TryParseExact(stream, "N", out _) ||
                            options.HostId.Length is < 1 or > 256 || options.Generation?.Key is not { Length: > 0 and <= 256 })
                            throw new ArgumentException("Invalid process or host identity");
                        var assignment = Field("assignmentId"); var assignmentGeneration = Field("assignmentGeneration"); var owner = Field("ownerId");
                        var project = Field("projectKey", 512); var group = Field("groupId");
                        var producerStart = ProcessIncarnation.Read(Environment.ProcessId);
                        var text = Field("text", 32000);
                        if (meta["assignmentRevision"] is not JsonValue ar || !ar.TryGetValue<long>(out var assignmentRevision) || assignmentRevision <= 0 ||
                            meta["observationRevision"] is not JsonValue ov || !ov.TryGetValue<long>(out var observationRevision) || observationRevision < 0)
                            throw new ArgumentException("Invalid revision");
                        var instance = Field("groupInstance"); var ledgerStream = Field("ledgerStream");
                        if (meta["ledgerNumber"] is not JsonValue lv || !lv.TryGetValue<long>(out var ledgerNumber) || ledgerNumber < 1 ||
                            !Guid.TryParseExact(ledgerStream, "N", out _)) throw new ArgumentException("Invalid ledger identity");
                        var binding = new CompletionReminderBinding(project, group, instance, prompt, native, stream, observationRevision,
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                            options.HostId, options.Generation!.Key, producerStart is null ? null : Environment.ProcessId, producerStart);
                        if (!binding.Valid()) throw new ArgumentException("Invalid completion binding");
                        guardedIdentity = CompletionHostLedger.Identity("delegation", assignment, owner, session, assignmentGeneration,
                            assignmentRevision, guardedAttempt, binding, ledgerStream, ledgerNumber);
                        foreach (var field in guardedIdentity)
                            if (!JsonNode.DeepEquals(meta[field.Key], field.Value)) throw new InvalidOperationException("Submit identity mismatch: " + field.Key);
                        var existingAttempt = store.ReadAttempt(guardedAttempt) ?? throw new InvalidOperationException("No durable admission");
                        if (!JsonNode.DeepEquals(existingAttempt["identity"], guardedIdentity) ||
                            (long?)existingAttempt["number"] != ledgerNumber || (string?)existingAttempt["state"] != "admitted" ||
                            (bool?)existingAttempt["forwardStarted"] != false)
                            throw new InvalidOperationException("Admission unavailable or already forwarded");
                        lock (state)
                            if (!claudeObservations.TryGetValue(session, out var previous) || previous.Owner != connection || previous.Prompt != prompt ||
                                idleAdmissions.GetValueOrDefault(session) != previous.Admission)
                                throw new InvalidOperationException("No matching live prompt admission");
                            else guardedAdmissionNumber = previous.Admission;
                        method = GorynychProtocol.MethodCompletionGuardedSubmit;
                    }
                    else
                    {
                        var row = store.ReadAttempt(guardedAttempt);
                        if (row is null) throw new InvalidOperationException("Unknown attempt");
                        guardedIdentity = row["identity"] as JsonObject ?? throw new InvalidDataException("Invalid identity");
                        foreach (var field in guardedIdentity)
                            if (!JsonNode.DeepEquals(field.Value, meta[field.Key])) throw new InvalidOperationException("Status identity mismatch");
                        if (guardedIsAbort)
                        {
                            foreach (var field in new[] { "hostId", "hostGeneration", "producerPid", "producerStart", "projectKey", "groupId", "groupInstance", "source", "digest", "ledgerStream", "ledgerNumber" })
                                if (!JsonNode.DeepEquals(guardedIdentity[field], meta[field])) throw new InvalidOperationException("Abort identity mismatch");
                            if (Text(guardedIdentity["hostId"]) != options.HostId || Text(guardedIdentity["hostGeneration"]) != options.Generation?.Key)
                            {
                                row["abortState"] = "unresolved";
                                await RespondAsync(Response(id, new JsonObject { ["_meta"] = row })); return;
                            }
                            // A matching name and generation do not identify a restarted OS
                            // producer. Only the original instance can fence its live adapter.
                            var producerStart = Text(guardedIdentity["producerStart"]);
                            if ((int?)guardedIdentity["producerPid"] != Environment.ProcessId || producerStart is null ||
                                !CompletionReminderBinding.CanonicalProducerStart(producerStart) ||
                                producerStart != ProcessIncarnation.Read(Environment.ProcessId))
                            {
                                row["abortState"] = "unresolved";
                                await RespondAsync(Response(id, new JsonObject { ["_meta"] = row })); return;
                            }
                            if ((string?)row["abortVerdict"] is "confirmed_precommit" or "already_committed")
                            {
                                row["attemptId"] = guardedAttempt;
                                await RespondAsync(Response(id, new JsonObject { ["_meta"] = row })); return;
                            }
                            var verdict = store.AbortBeforeForward(guardedAttempt, guardedIdentity);
                            if (verdict != "needs_adapter")
                            {
                                row = store.ReadAttempt(guardedAttempt)!; row["attemptId"] = guardedAttempt;
                                await RespondAsync(Response(id, new JsonObject { ["_meta"] = row })); return;
                            }
                        }
                        if (method == GorynychProtocol.MethodHostCompletionGuardedAck)
                        {
                            if (meta["receiptRevision"] is not JsonValue rv || !rv.TryGetValue<long>(out var requestedRevision) || requestedRevision < 0)
                                throw new ArgumentException("Invalid receipt revision");
                            guardedAckRevision = requestedRevision;
                            row = store.PrepareAck(guardedAttempt, guardedIdentity, requestedRevision);
                            if ((long?)row["ackRevision"] == requestedRevision)
                            {
                                await RespondAsync(Response(id, new JsonObject { ["_meta"] = row }));
                                return;
                            }
                        }
                        if (guardedIsRetire)
                        {
                            if (meta["receiptRevision"] is not JsonValue rv || !rv.TryGetValue<long>(out var requestedRevision) || requestedRevision < 0)
                                throw new ArgumentException("Invalid receipt revision");
                            guardedRetireRevision = requestedRevision;
                            row = store.PrepareRetire(guardedAttempt, guardedIdentity, requestedRevision);
                            if ((long?)row["retiredRevision"] == requestedRevision)
                            {
                                row["adapterState"] = "retired";
                                await RespondAsync(Response(id, new JsonObject { ["_meta"] = row })); return;
                            }
                        }
                        var originalStart = Text(guardedIdentity["producerStart"]);
                        if (Text(guardedIdentity["hostId"]) != options.HostId || Text(guardedIdentity["hostGeneration"]) != options.Generation?.Key ||
                            (int?)guardedIdentity["producerPid"] != Environment.ProcessId || originalStart is null ||
                            !CompletionReminderBinding.CanonicalProducerStart(originalStart) || originalStart != ProcessIncarnation.Read(Environment.ProcessId))
                        {
                            row["adapterState"] = "unknown";
                            await RespondAsync(Response(id, new JsonObject { ["_meta"] = row }));
                            return;
                        }
                        guardedIdentity = (JsonObject)guardedIdentity.DeepClone();
                        method = guardedIsAbort ? GorynychProtocol.MethodCompletionGuardedAbort : guardedIsRetire ? GorynychProtocol.MethodCompletionGuardedRetire : guardedAckRevision is null ? GorynychProtocol.MethodCompletionGuardedStatus : GorynychProtocol.MethodCompletionGuardedAck;
                    }
                    meta["digest"] = guardedIdentity!["digest"]!.DeepClone();
                    guardedEpoch = connection.CompletionGuardedEpoch;
                    p["sessionId"] = session;
                    message["method"] = method;
                }
                finally { sendGate.Release(); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
            {
                await RespondAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = JsonRpcException.InvalidRequest, ["message"] = "Guarded bridge unresolved: " + e.Message } });
                return;
            }
        }
        string? observationSession = null, observationPrompt = null;
        long? observationAdmission = null, observationCapabilityEpoch = null;
        if (method == GorynychProtocol.MethodClaudePromptObservation)
        {
            await TryWriteAsync(connection, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                ["error"] = new JsonObject { ["code"] = JsonRpcException.MethodNotFound, ["message"] = "Private adapter method" } });
            return;
        }
        if (method == GorynychProtocol.MethodHostClaudePromptObservation)
        {
            var args = message["params"] as JsonObject; var meta = args?["_meta"] as JsonObject;
            observationSession = Text(meta?["sessionId"]); observationPrompt = Text(meta?["promptId"]);
            await sendGate.WaitAsync();
            try
            {
                lock (state)
                    if (args?.Count == 1 && meta?.Count == 2 && observationSession is { Length: > 0 and <= 256 } session &&
                        observationPrompt is { Length: > 0 and <= 256 } prompt && current == connection && !connection.IsLost &&
                        ClaudeObservationSupported(connection) && live.Contains(session) && subagents.RootOf(session) is null &&
                        claudeObservations.TryGetValue(session, out var existing) && existing.Owner == connection && existing.Prompt == prompt &&
                        idleAdmissions.GetValueOrDefault(session) == existing.Admission)
                    { observationAdmission = existing.Admission; observationCapabilityEpoch = connection.ClaudeObservationEpoch; }
                if (observationAdmission is null)
                {
                    await TryWriteAsync(connection, Response(id, new JsonObject { ["_meta"] = new JsonObject { ["state"] = "unknown" } }));
                    return;
                }
            }
            finally { sendGate.Release(); }
            method = GorynychProtocol.MethodClaudePromptObservation; message["method"] = method;
        }
        string? noteStatusReport = null;
        string? noteAttempt = null, noteDigest = null, noteAckUuid = null;
        long? noteAckRevision = null;
        if (method == GorynychProtocol.MethodHostReportNoteAck)
        {
            try
            {
                var p = message["params"] as JsonObject ?? throw new ArgumentException("Missing request");
                if (p.ContainsKey("text")) throw new ArgumentException("ACK accepts no text");
                var session = Text(p["sessionId"]) ?? throw new ArgumentException("Missing session");
                var note = p["_meta"]?[GorynychProtocol.MetaReportNote];
                var report = Text(note?["reportId"]) ?? throw new ArgumentException("Missing report ID");
                var attempt = Text(note?["attemptId"]) ?? throw new ArgumentException("Missing attempt ID");
                var uuid = Text(note?["nativeMessageId"]) ?? throw new ArgumentException("Missing native UUID");
                if (note?["revision"] is not JsonValue rv || !rv.TryGetValue<long>(out var revision) || revision <= 0)
                    throw new ArgumentException("Invalid receipt revision");
                var store = options.ReportStore ?? throw new InvalidOperationException("Report storage is unavailable");
                var generation = options.Generation?.Key ?? throw new InvalidOperationException("Host generation is unavailable");
                if (!store.CanAcknowledgeNote(report, session, options.HostId, generation, attempt, uuid, revision))
                    throw new InvalidOperationException("Receipt is not durably settled at this revision");
                var meta = p["_meta"] as JsonObject ?? new JsonObject(); p["_meta"] = meta;
                meta[GorynychProtocol.MetaHostNoteTracking] = true;
                noteStatusReport = report; noteAttempt = attempt; noteAckUuid = uuid; noteAckRevision = revision;
                method = GorynychProtocol.MethodReportNoteAck; message["method"] = method;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                await TryWriteAsync(connection, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = JsonRpcException.InvalidRequest, ["message"] = "Report note ACK unresolved: " + e.Message } });
                return;
            }
        }
        if (method == GorynychProtocol.MethodHostReportNoteStatus)
        {
            try
            {
                var p = message["params"] as JsonObject ?? throw new ArgumentException("Missing request");
                if (p.ContainsKey("text")) throw new ArgumentException("Status accepts no text");
                var session = Text(p["sessionId"]) ?? throw new ArgumentException("Missing session");
                var note = p["_meta"]?[GorynychProtocol.MetaReportNote];
                var report = Text(note?["reportId"]) ?? throw new ArgumentException("Missing report ID");
                var attempt = Text(note?["attemptId"]) ?? throw new ArgumentException("Missing attempt ID");
                var store = options.ReportStore ?? throw new InvalidOperationException("Report storage is unavailable");
                var generation = options.Generation?.Key ?? throw new InvalidOperationException("Host generation is unavailable");
                _ = store.ReadNoteReceipt(report, session, options.HostId, generation, attempt);
                noteStatusReport = report;
                method = GorynychProtocol.MethodReportNoteStatus; message["method"] = method;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                await TryWriteAsync(connection, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = JsonRpcException.InvalidRequest, ["message"] = "Report note status refused: " + e.Message } });
                return;
            }
        }
        if (method == GorynychProtocol.MethodHostReportNote)
        {
            try
            {
                var p = message["params"] as JsonObject ?? throw new ArgumentException("Missing request");
                var session = Text(p["sessionId"]) ?? throw new ArgumentException("Missing session");
                var text = Text(p["text"]) ?? throw new ArgumentException("Missing text");
                var note = p["_meta"]?[GorynychProtocol.MetaReportNote];
                var report = Text(note?["reportId"]) ?? throw new ArgumentException("Missing report ID");
                var attempt = Text(note?["attemptId"]) ?? throw new ArgumentException("Missing attempt ID");
                var store = options.ReportStore ?? throw new InvalidOperationException("Host receipt storage is unavailable");
                var generation = options.Generation?.Key ?? throw new InvalidOperationException("Host generation is unavailable");
                if ((bool?)initialize?["_meta"]?[GorynychProtocol.CapReportNote] != true)
                    throw new InvalidOperationException("Adapter cannot confirm report notes");
                if (!store.BeginHostNote(report, session, options.HostId, generation, attempt, text))
                {
                    var retained = store.ReadNoteReceipt(report, session, options.HostId, generation, attempt) ?? new JsonObject();
                    var proof = store.ReadNoteNoInput(report, session, options.HostId, generation, attempt);
                    if (proof is not null)
                    {
                        retained["noInputProof"] = proof;
                        retained["digest"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
                    }
                    retained["reportId"] = report; retained["attemptId"] = attempt;
                    retained["state"] = (bool?)retained["contradiction"] == true || (bool?)retained["cancelReceiptPending"] == true ? "uncertain" :
                        retained["cycleResult"] is not null ? "cycle_result" : (bool?)retained["nativeCancelled"] == true ? "dropped" : "uncertain";
                    await TryWriteAsync(connection, Response(id, new JsonObject { ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = retained } }));
                    return;
                }
                noteAttempt = attempt;
                noteDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
                var meta = p["_meta"] as JsonObject ?? new JsonObject(); p["_meta"] = meta;
                meta[GorynychProtocol.MetaHostNoteTracking] = true;
                noteStatusReport = report; // persist the initial response before acknowledging the caller
                method = GorynychProtocol.MethodReportNote; message["method"] = method;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                await TryWriteAsync(connection, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = e is IOException or UnauthorizedAccessException ? JsonRpcException.InternalError : JsonRpcException.InvalidRequest,
                        ["message"] = "Report note verification is unresolved; a prior native handoff may have occurred. Do not retry or fall back without evidence. " + e.Message } });
                return;
            }
        }
        switch (method)
        {
            case AcpMethods.Agent.Initialize:
            {
                if (message["params"] is not JsonObject initParams) message["params"] = initParams = new JsonObject();
                if (initParams["_meta"] is not JsonObject initMeta) initParams["_meta"] = initMeta = new JsonObject();
                initMeta.Remove(GorynychProtocol.CapPeerTerminalReceiver);
                if (options.PeerTerminal && options.ReportStore is not null && options.Adapter == "claude")
                    initMeta[GorynychProtocol.CapPeerTerminalReceiver] = true;
                var acceptsCompletionGuarded = options.CompletionHostLedger is not null && initMeta[GorynychProtocol.CapHostCompletionGuarded] is JsonValue testValue && testValue.TryGetValue<bool>(out var testAccepted) && testAccepted;
                initMeta.Remove(GorynychProtocol.CapHostCompletionGuarded);
                initMeta.Remove(GorynychProtocol.CapCompletionGuardedReceiver);
                if (options.Adapter == "claude" && options.CompletionHostLedger is not null)
                    initMeta[GorynychProtocol.CapCompletionGuardedReceiver] = true;
                var acceptsClaude = initMeta[GorynychProtocol.CapHostClaudePromptObservation] is JsonValue requestedClaude && requestedClaude.TryGetValue<bool>(out var acceptedClaude) && acceptedClaude;
                await sendGate.WaitAsync();
                try
                {
                    lock (state)
                    {
                        connection.ClaudeObservationEpoch++;
                        connection.CompletionGuardedEpoch++;
                        connection.AcceptsCompletionGuarded = acceptsCompletionGuarded;
                        connection.AcceptsClaudeObservation = acceptsClaude;
                        foreach (var session in claudeObservations.Where(p => p.Value.Owner == connection).Select(p => p.Key).ToArray()) claudeObservations.Remove(session);
                    }
                }
                finally { sendGate.Release(); }
                initMeta.Remove(GorynychProtocol.CapHostClaudePromptObservation);
                initMeta.Remove(GorynychProtocol.CapClaudeObservationReceiver);
                initMeta[GorynychProtocol.CapClaudeObservationReceiver] = options.Adapter == "claude";
                connection.AcceptsIdle = initMeta[GorynychProtocol.CapHostCodexProvisionalIdle] is JsonValue requested && requested.TryGetValue<bool>(out var accepted) && accepted;
                initMeta.Remove(GorynychProtocol.CapHostCodexProvisionalIdle);
                initMeta.Remove(GorynychProtocol.CapCodexIdleHostReceiver);
                initMeta[GorynychProtocol.CapCodexIdleHostReceiver] = options.Adapter == "codex";
                JsonObject? known;
                bool forward;
                lock (state)
                {
                    known = initialize;
                    forward = known is null && !initializing;
                    if (forward) initializing = true;
                }
                if (known is null && !forward)
                {
                    // An earlier conductor's initialize is still on its way: its answer is this one's too.
                    try
                    {
                        await initialized.Task.WaitAsync(options.InitializeWait, stopping.Token);
                    }
                    catch (TimeoutException)
                    {
                        await TryWriteAsync(connection, new JsonObject
                        {
                            ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                            ["error"] = new JsonObject
                            {
                                ["code"] = JsonRpcException.InternalError,
                                ["message"] = $"The agent has not answered its first initialize in {options.InitializeWait.TotalSeconds:0} seconds.",
                            },
                        });
                        return;
                    }
                    lock (state) known = initialize;
                }
                if (known is not null)
                {
                    await TryWriteAsync(connection, Response(id, WithHostMeta((JsonObject)known.DeepClone(), connection, reattached: true)));
                    return;
                }
                break;
            }
            case GorynychProtocol.MethodHostState:
                await TryWriteAsync(connection, Response(id, Report()));
                return;
            case GorynychProtocol.MethodHostReplay:
                await TryWriteAsync(connection, Response(id, await ActivateAsync(connection, message["params"]?["after"] as JsonObject)));
                return;
            case GorynychProtocol.MethodHostStop:
                Log("the conductor stops the host");
                await TryWriteAsync(connection, Response(id, new JsonObject()));
                ended.TrySetResult(0);
                return;
        }
        long key = 0;
        var @params = message["params"] as JsonObject;
        var sessionId = observationSession ?? Text(@params?["sessionId"]);
        var reportId = method == AcpMethods.Agent.SessionPrompt ? Text(@params?["_meta"]?[GorynychProtocol.MetaReportId]) : null;
        ReportCapture? capture = null;
        PeerTurnIdentity? peerRoute = null;
        if (method == AcpMethods.Agent.SessionPrompt && @params?["_meta"] is JsonObject callerMeta)
            callerMeta.Remove(GorynychProtocol.MetaPeerTerminal);
        var provenUnstarted = reportId is not null;
        Exception? refusal = null;
        JsonObject? admissionRevocation = null;
        var sharedIdleAdmission = IsHistoryOperation(method) || method is AcpMethods.Agent.SessionPrompt or AcpMethods.Agent.SessionClose or AcpMethods.Agent.SessionDelete;
        var claudeInputAdmission = method is GorynychProtocol.MethodSteering or GorynychProtocol.MethodNote or GorynychProtocol.MethodOpinion or GorynychProtocol.MethodReportNote or GorynychProtocol.MethodCompletionGuardedSubmit;
        var idleAdmissionGate = sessionId is not null && ((guardedAttempt is not null) || (IdleSupported(connection) && sharedIdleAdmission) ||
            (ClaudeObservationSupported(connection) && (sharedIdleAdmission || claudeInputAdmission)));
        if (idleAdmissionGate) await sendGate.WaitAsync();
        try
        {
            lock (state)
            {
              try
              {
                if (stopping.IsCancellationRequested) throw new InvalidOperationException("The host is stopping");
                if (guardedAttempt is not null && (current != connection || connection.IsLost || !connection.AcceptsCompletionGuarded ||
                    guardedEpoch != connection.CompletionGuardedEpoch || sessionId is null || !live.Contains(sessionId) ||
                    method == GorynychProtocol.MethodCompletionGuardedSubmit && (guardedAdmissionNumber is null || idleAdmissions.GetValueOrDefault(sessionId) != guardedAdmissionNumber)))
                    throw new InvalidOperationException("Guarded admission changed before forwarding");
                var history = IsHistoryOperation(method);
                long? idleAdmission = null;
                if (idleAdmissionGate && sessionId is not null && (sharedIdleAdmission || claudeInputAdmission))
                {
                    claudeObservations.Remove(sessionId);
                    if (idleEligibility.Remove(sessionId, out var oldIdle) && oldIdle.Seen && !oldIdle.Revoked && oldIdle.Owner == current && !oldIdle.Owner.IsLost)
                        admissionRevocation = oldIdle.Frame(sessionId, "revoked");
                    if (method is AcpMethods.Agent.SessionClose or AcpMethods.Agent.SessionDelete || !live.Contains(sessionId)) idleAdmissions.Remove(sessionId);
                    else idleAdmissions[sessionId] = ++nextIdleAdmission;

                    if (method == AcpMethods.Agent.SessionPrompt && idleAdmissions.TryGetValue(sessionId, out var admitted)) idleAdmission = admitted;
                    if (method == GorynychProtocol.MethodCompletionGuardedSubmit && idleAdmissions.TryGetValue(sessionId, out var guardedAdmission)) guardedWriteAdmission = guardedAdmission;
                }
                if (sessionId is not null && (history || method == AcpMethods.Agent.SessionPrompt) &&
                    forwarded.Values.Any(f => f.SessionId == sessionId && f.Capture is not null))
                    throw new InvalidOperationException("A report capture is active for this session; retry after its turn ends");
                if (reportId is not null)
                {
                    if (options.ReportStore is null) throw new InvalidOperationException("This host does not support report capture");
                    var session = sessionId ?? throw new InvalidOperationException("Missing report session");
                    var prompt = Text(@params?["_meta"]?[GorynychProtocol.MetaPromptId]) ?? throw new InvalidOperationException("Missing report prompt ID");
                    if (forwarded.Values.Any(f => f.SessionId == session && (f.Method == AcpMethods.Agent.SessionPrompt || IsHistoryOperation(f.Method))))
                        throw new InvalidOperationException("A prompt or history operation is already running in this session");
                    // A failed open may mean another producer or an existing spool; it is
                    // not proof that this reservation was never handed off.
                    provenUnstarted = false;
                    capture = options.ReportStore.OpenReservedCapture(reportId, session, prompt);
                    if (options.PeerTerminal && options.Adapter == "claude" &&
                        initialize?["_meta"]?[GorynychProtocol.CapPeerTerminalAdapter] is JsonValue capability &&
                        capability.TryGetValue<bool>(out var supported) && supported)
                    {
                        var reserved = options.ReportStore.GetReport(reportId);
                        if (reserved.Identity.PeerRoute is { } exact && reserved.Identity.HostId == options.HostId &&
                            reserved.Identity.HostGeneration == options.Generation?.Key && exact.Recipient == session && exact.PromptId == prompt)
                        {
                            peerRoute = exact;
                            if (@params!["_meta"] is not JsonObject peerMeta) @params["_meta"] = peerMeta = new JsonObject();
                            peerMeta[GorynychProtocol.MetaPeerTerminal] = new JsonObject { ["sourceMessageId"] = exact.MessageId };
                        }
                    }
                    provenUnstarted = true;
                    options.CaptureOpened?.Invoke(capture);
                    (@params?["_meta"] as JsonObject)?.Remove(GorynychProtocol.MetaReportId);
                }
                key = ++nextToAgent;
                forwarded[key] = new Forwarded
                {
                Connection = connection, ConductorId = id.DeepClone(), Method = method, SessionId = sessionId, IdleAdmission = idleAdmission,
                GuardedAttempt = guardedAttempt, GuardedIdentity = guardedIdentity, GuardedEpoch = guardedEpoch, GuardedWriteAdmission = guardedWriteAdmission, GuardedAckRevision = guardedAckRevision, GuardedRetireRevision = guardedRetireRevision, GuardedIsAbort = guardedIsAbort,
                ClaudeObservationAdmission = observationAdmission,
                ClaudeObservationEpoch = observationCapabilityEpoch ?? connection.ClaudeObservationEpoch, PeerRoute = peerRoute, PromptId = observationPrompt ?? Text(@params?["_meta"]?[GorynychProtocol.MetaPromptId]), ReportId = reportId, Capture = capture,
                NoteStatusReport = noteStatusReport, NoteAttempt = noteAttempt, NoteDigest = noteDigest,
                NoteAckUuid = noteAckUuid, NoteAckRevision = noteAckRevision,
                Delivery = method == AcpMethods.Agent.SessionPrompt && Text(@params?["_meta"]?[GorynychProtocol.MetaOrigin]) == "report"
                    ? @params?["_meta"]?[GorynychProtocol.MetaReportDelivery]?.DeepClone() as JsonObject : null,
                TurnKind = reportId is not null ? "work" : Text(@params?["_meta"]?[GorynychProtocol.MetaOrigin]) == "report" ? "report" : "unknown",
                ModeId = method == AcpMethods.Agent.SessionSetMode ? Text(@params?["modeId"]) : null,
                ModelId = method == "session/set_model" ? Text(@params?["modelId"]) : null,
                AskId = Text(@params?["_meta"]?[GorynychProtocol.MetaAskId]) ?? Text(@params?["askId"]), // a prompt's _meta; _gorynych/opinion's params
                Opinion = Text(@params?["_meta"]?[GorynychProtocol.MetaOpinion]),
                From = Text(@params?["_meta"]?[GorynychProtocol.MetaFromSessionId]),
                MessageId = method == GorynychProtocol.MethodSteering ? Text(@params?["_meta"]?[GorynychProtocol.MetaMessageId]) : null,
                SteerOrigin = method == GorynychProtocol.MethodSteering ? Text(@params?["_meta"]?[GorynychProtocol.MetaSteerOrigin]) : null,
                Prompt = method == GorynychProtocol.MethodSteering ? @params?["prompt"]?.DeepClone() as JsonArray : null,
                };
                // Transfer to forwarded while holding the same lock used by admission.
                lease?.Release();
              }
              catch
              {
                  // Keep an unregistered lease inside the same shutdown exclusion.
                  capture?.Dispose(); capture = null;
                  throw;
              }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            capture?.Dispose(); refusal = e;
        }
        finally
        {
            if (idleAdmissionGate)
            {
                try { if (admissionRevocation is not null && current is { IsLost: false } target)
                    await TryWriteAsync(target, Notification(GorynychProtocol.MethodHostCodexProvisionalIdle, admissionRevocation)); }
                finally { sendGate.Release(); }
            }
        }
        if (refusal is not null)
        {
            await RespondAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                ["error"] = new JsonObject { ["code"] = JsonRpcException.InvalidRequest, ["message"] = "Host request refused: " + refusal.Message,
                    ["data"] = !provenUnstarted ? null : new JsonObject { [GorynychProtocol.MetaReportRefused] = new JsonObject
                    { ["reportId"] = reportId, ["sessionId"] = sessionId, ["promptId"] = Text(@params?["_meta"]?[GorynychProtocol.MetaPromptId]) } } } });
            return;
        }
        message["id"] = key;
        if (guardedAttempt is not null)
        {
            if (!await WriteCompletionGuardedToAgentAsync(message, connection, key, guardedEpoch, guardedIsAbort))
                await RespondAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = JsonRpcException.InvalidRequest, ["message"] = "Guarded admission unresolved" } });
        }
        else await WriteToAgentAsync(message);
    }

    static bool IsHistoryOperation(string method) => method is AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionResume or GorynychProtocol.MethodCatchUp;

    // Called once for live agent notifications, under the ordered send gate and state lock.
    void CaptureUpdate(JsonObject message)
    {
        if (Text(message["method"]) != AcpMethods.Client.SessionUpdate) return;
        var session = Text(message["params"]?["sessionId"]);
        var update = message["params"]?["update"];
        var type = Text(update?["sessionUpdate"]);
        if (session is null || type is not ("agent_message_chunk" or "tool_call")) return;
        var request = forwarded.Values.FirstOrDefault(f => f.Method == AcpMethods.Agent.SessionPrompt && f.SessionId == session && f.Capture is not null);
        if (request?.Capture is null) return;
        if (request.CaptureFailure is not null && type != "tool_call") return;
        try
        {
            if (type == "tool_call") { request.Capture.ToolCall(); request.CaptureFailure = null; }
            else if (Text(update?["content"]?["type"]) == "text" && Text(update?["content"]?["text"]) is { } text) request.Capture.Append(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            request.CaptureFailure = BoundError(e.Message);
        }
    }

    static string BoundReceiptField(string text)
    {
        const string suffix = " [truncated]"; const int limit = 64;
        if (Encoding.UTF8.GetByteCount(text) <= limit) return text;
        var output = new StringBuilder(); var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > limit - suffix.Length) break;
            output.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return output.Append(suffix).ToString();
    }

    static string BoundError(string text)
    {
        const string suffix = " [truncated]";
        if (Encoding.UTF8.GetByteCount(text) <= ReportCapture.ErrorBytes) return text;
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > ReportCapture.ErrorBytes - suffix.Length) break;
            result.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return result.Append(suffix).ToString();
    }

    void SealReport(Forwarded request, JsonObject response)
    {
        if (request.Capture is not { } capture) return;
        var error = response["error"] is { } nativeError ? BoundError(nativeError.ToJsonString()) : null;
        var outcome = ReportTerminalOutcome.From(response["result"], error is not null);
        PeerProducerEvidence? peerEvidence = null;
        var meta = response["result"]?["_meta"] as JsonObject;
        var peer = meta?[GorynychProtocol.MetaPeerTerminal] as JsonObject;
        // This is meaningful only for the exact host-opened reservation and negotiated adapter.
        if (request.PeerRoute is { } route && peer is { Count: 6 } && outcome == "ended" && error is null &&
            Text(peer["sessionId"]) == route.Recipient && Text(peer["promptId"]) == route.PromptId &&
            Text(peer["sourceMessageId"]) == route.MessageId && Text(peer["nativeUuid"]) is { } uuid &&
            Text(peer["stream"]) is { } stream && peer.ContainsKey("acceptedMessageId") &&
            (peer["acceptedMessageId"] is null || Text(peer["acceptedMessageId"]) is not null))
        {
            var candidate = new PeerProducerEvidence(route.PromptId, uuid, stream, Text(peer["acceptedMessageId"]));
            if (ReportStore.IsValidPeerEvidence(candidate)) peerEvidence = candidate;
        }
        meta?.Remove(GorynychProtocol.MetaPeerTerminal);
        try
        {
            if (request.CaptureFailure is { } failure) capture.SealIncomplete(failure, outcome, error);
            else capture.Seal(outcome, error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            request.CaptureFailure = BoundError(e.Message);
        }
        finally
        {
            try { capture.Dispose(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { request.CaptureFailure = BoundError(e.Message); }
        }
        if (request.ReportId is { } id && options.ReportStore is { } reports)
        {
            try
            {
                options.BeforeProducerEvidence?.Invoke();
                reports.RecordProducerEvidence(id, options.HostId, outcome, error, request.CaptureFailure, request.CaptureFailure is null ? peerEvidence : null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { request.CaptureFailure = BoundError("Producer evidence could not be saved: " + e.Message); }
        }
    }

    // Live snapshots survive eviction from the bounded notification replay buffer.
    JsonObject SessionActivities() => new(live.Where(activities.ContainsKey)
        .Select(id => new KeyValuePair<string, JsonNode?>(id, activities[id].DeepClone())));

    static bool ActivityRunning(JsonObject activity) => (bool?)activity["agentTurn"] == true
        || Text(activity["foreground"]) is "running" or "requires_action"
        || (bool?)activity["backgroundHeld"] == true || (bool?)activity["backgroundKnownHeld"] == true;

    internal JsonObject Report()
    {
        lock (state)
            return new JsonObject
            {
                ["sessions"] = new JsonArray(live.Select(s => (JsonNode)s).ToArray()),
                ["sessionStates"] = SessionConfigurations(),
                ["sessionActivities"] = SessionActivities(),
                ["turns"] = new JsonArray(forwarded.Where(f => f.Value.Method == AcpMethods.Agent.SessionPrompt && f.Value.SessionId is not null)
                    .Select(f => (JsonNode)new JsonObject { ["sessionId"] = f.Value.SessionId, ["turn"] = f.Key, ["promptId"] = f.Value.PromptId,
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportId] = f.Value.ReportId,
                            [GorynychProtocol.MetaHostTurnKind] = f.Value.TurnKind,
                            [GorynychProtocol.MetaReportDelivery] = f.Value.Delivery?.DeepClone() } }).ToArray()),
                // The questions the agent is answering, by turn or by its own way in: the next conductor waits for their answers.
                ["asks"] = new JsonArray(forwarded.Values.Where(f => f.AskId is not null && f.SessionId is not null
                        && f.Method is AcpMethods.Agent.SessionPrompt or GorynychProtocol.MethodOpinion)
                    .Select(f => (JsonNode)new JsonObject { ["sessionId"] = f.SessionId, ["askId"] = f.AskId, ["opinion"] = f.Opinion, ["from"] = f.From }).ToArray()),
                ["buffered"] = new JsonArray(buffer.Select(b => b.SessionId).Distinct(StringComparer.Ordinal).Select(s => (JsonNode)s).ToArray()),
                ["requests"] = asked.Count,
                ["_meta"] = new JsonObject { [GorynychProtocol.CapHostReportNote] = new JsonObject { ["failure"] = noteReceiptFailure } },
                ["seq"] = seq,
            };
    }

    /// <summary>
    /// Sends the connection what the conductor's journal lacks, after the cursor of each session,
    /// then makes it the conductor's connection and asks the agent's waiting questions on it again.
    /// </summary>
    async Task<JsonObject> ActivateAsync(Connection connection, JsonObject? after)
    {
        await sendGate.WaitAsync();
        try
        {
            if (connection.IsLost) throw new IOException("the connection went");
            List<Kept> replay;
            List<string> trimmed;
            List<Asked> questions;
            lock (state)
            {
                long Cursor(string sessionId) => after?[sessionId] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
                replay = buffer.Where(b => b.Seq > Cursor(b.SessionId)).ToList();
                trimmed = dropped.Where(d => d.Value > Cursor(d.Key)).Select(d => d.Key).ToList();
                questions = asked.Values.ToList();
            }
            foreach (var kept in replay)
                if (!await TryWriteLineAsync(connection, kept.Line)) throw new IOException("the connection went");
            // A current snapshot after history: a replayed old option update must not replace a
            // newer value returned by set_config_option while no conductor was connected.
            JsonObject snapshot;
            lock (state) snapshot = new JsonObject { ["sessionStates"] = SessionConfigurations(), ["sessionActivities"] = SessionActivities(),
                ["_meta"] = new JsonObject { [GorynychProtocol.CapHostReportNote] = new JsonObject { ["failure"] = noteReceiptFailure } } };
            if (!await TryWriteAsync(connection, Notification(GorynychProtocol.MethodHostState, snapshot)))
                throw new IOException("the connection went");
            current = connection;
            lock (state) { claudeObservations.Clear(); idleEligibility.Clear(); idleAdmissions.Clear(); aloneSince = long.MaxValue; }
            foreach (var question in questions) await AskAsync(connection, question);
            connection.Activated.TrySetResult();
            Log($"a conductor took the host: {replay.Count} updates sent again, {questions.Count} questions asked again");
            return new JsonObject
            {
                ["replayed"] = replay.Count,
                ["trimmed"] = new JsonArray(trimmed.Select(s => (JsonNode)s).ToArray()),
            };
        }
        finally
        {
            sendGate.Release();
        }
    }

    async Task LostAsync(Connection connection)
    {
        Drop(connection);
        if (ended.Task.IsCompleted) return;
        bool wasCurrent;
        await sendGate.WaitAsync();
        try
        {
            wasCurrent = current == connection;
            if (wasCurrent) { current = null; lock (state) { claudeObservations.Clear(); idleEligibility.Clear(); idleAdmissions.Clear(); } }
        }
        finally
        {
            sendGate.Release();
        }
        if (!wasCurrent) return; // one that never got through its handshake: the attempt goes on
        lock (state) aloneSince = Stopwatch.GetTimestamp();
        Log("lost the conductor; waiting for the next one");
        StartReconnecting();
    }

    // ---------------------------------------------------------------- writing

    JsonObject WithHostMeta(JsonObject result, Connection connection, bool reattached)
    {
        var meta = result["_meta"] as JsonObject ?? [];
        // Capability belongs to the host implementation, not the wrapped adapter.
        var peerAdapter = meta[GorynychProtocol.CapPeerTerminalAdapter] is JsonValue pv && pv.TryGetValue<bool>(out var ps) && ps;
        meta.Remove(GorynychProtocol.CapPeerTerminalAdapter);
        meta.Remove(GorynychProtocol.CapPeerTerminalReceiver);
        meta.Remove(GorynychProtocol.CapHostPeerTerminal);
        if (options.PeerTerminal && options.Adapter == "claude" && options.ReportStore is not null && peerAdapter)
            meta[GorynychProtocol.CapHostPeerTerminal] = true;
        meta.Remove(GorynychProtocol.CapHostCompletionGuarded);
        var guardedAdapter = meta[GorynychProtocol.CapCompletionGuardedAdapter] is JsonValue tv && tv.TryGetValue<bool>(out var ts) && ts;
        meta.Remove(GorynychProtocol.CapCompletionGuardedAdapter);
        if (options.CompletionHostLedger is not null && connection.AcceptsCompletionGuarded && options.Adapter == "claude" && guardedAdapter)
            meta[GorynychProtocol.CapHostCompletionGuarded] = true;
        meta.Remove(GorynychProtocol.CapHostBackgroundEvidence);
        meta.Remove(GorynychProtocol.CapHostCodexProvisionalIdle);
        meta.Remove(GorynychProtocol.CapHostClaudePromptObservation);
        var claudeObservation = meta[GorynychProtocol.CapClaudePromptObservation] is JsonValue co && co.TryGetValue<bool>(out var cs) && cs;
        meta.Remove(GorynychProtocol.CapClaudePromptObservation);
        if (options.Adapter == "claude" && connection.AcceptsClaudeObservation && claudeObservation) meta[GorynychProtocol.CapHostClaudePromptObservation] = true;
        var adapterIdle = meta[GorynychProtocol.CapCodexIdleAdapter] is JsonValue v && v.TryGetValue<bool>(out var supported) && supported;
        meta.Remove(GorynychProtocol.CapCodexIdleAdapter);
        if (options.Adapter == "codex" && ((connection.AcceptsIdle && adapterIdle) || options.FilterCodexIdleForTests)) meta[GorynychProtocol.CapHostCodexProvisionalIdle] = true;
        if (options.Adapter == "claude") meta[GorynychProtocol.CapHostBackgroundEvidence] = true;
        meta[GorynychProtocol.MetaHost] = new JsonObject
        {
            ["hostId"] = options.HostId, ["link"] = GorynychProtocol.HostLinkVersion, ["reattached"] = reattached,
        };
        if (options.ReportStore is not null) meta[GorynychProtocol.MetaReportCapture] = new JsonObject { ["supported"] = true };
        if (options.ReportStore is not null && options.Generation is not null &&
            (bool?)result["_meta"]?[GorynychProtocol.CapReportNote] == true)
            meta[GorynychProtocol.CapHostReportNote] = new JsonObject { ["supported"] = true,
                ["durableRouting"] = (bool?)result["_meta"]?[GorynychProtocol.CapReportNoteAck] == true };
        result["_meta"] = meta;
        return result;
    }

    // Commit the frame under the ordered current-connection gate after taking the adapter writer.
    // Never hold either host state lock across I/O. A lost/downgraded connection leaves the
    // already durable attempt unresolved and cannot cause a second forward.
    async Task<bool> WriteCompletionGuardedToAgentAsync(JsonObject message, Connection connection, long key, long? epoch, bool isAbort)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Wire) + "\n");
        if (isAbort && !agentGate.Wait(0)) { lock (state) forwarded.Remove(key); return false; }
        if (!isAbort)
        {
            var waiting = agentGate.WaitAsync();
            await waiting;
        }
        try
        {
            if (isAbort && !sendGate.Wait(0)) { lock (state) forwarded.Remove(key); return false; }
            if (!isAbort) await sendGate.WaitAsync();
            ValueTask write;
            try
            {
                Forwarded guardedRequest;
                lock (state)
                {
                    if (stopping.IsCancellationRequested || current != connection || connection.IsLost ||
                        !connection.AcceptsCompletionGuarded || connection.CompletionGuardedEpoch != epoch ||
                        !forwarded.TryGetValue(key, out guardedRequest!) || guardedRequest.SessionId is not { } session ||
                        !live.Contains(session) || subagents.RootOf(session) is not null ||
                        guardedRequest.Method == GorynychProtocol.MethodCompletionGuardedSubmit &&
                            (guardedRequest.GuardedWriteAdmission is null || idleAdmissions.GetValueOrDefault(session) != guardedRequest.GuardedWriteAdmission))
                    {
                        forwarded.Remove(key);
                        return false;
                    }
                }
                if (guardedRequest.Method == GorynychProtocol.MethodCompletionGuardedSubmit)
                {
                    try
                    {
                        if (guardedRequest.GuardedAttempt is null || guardedRequest.GuardedIdentity is null || options.CompletionHostLedger is null ||
                            !options.CompletionHostLedger.BeginForward(guardedRequest.GuardedAttempt, guardedRequest.GuardedIdentity))
                        { lock (state) forwarded.Remove(key); return false; }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
                    { lock (state) forwarded.Remove(key); return false; }
                }
                write = agent.Output.WriteAsync(bytes);
            }
            finally { sendGate.Release(); }
            await write;
            await agent.Output.FlushAsync();
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        {
            lock (state) forwarded.Remove(key);
            ended.TrySetResult(0);
            return false;
        }
        finally { agentGate.Release(); }
    }

    async Task WriteToAgentAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Wire) + "\n");
        await agentGate.WaitAsync();
        try
        {
            if (stopping.IsCancellationRequested) return;
            await agent.Output.WriteAsync(bytes);
            await agent.Output.FlushAsync();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        {
            ended.TrySetResult(0); // the agent is gone
        }
        finally
        {
            agentGate.Release();
        }
    }

    static Task<bool> TryWriteAsync(Connection connection, JsonObject message) => TryWriteLineAsync(connection, message.ToJsonString(Wire));

    static async Task<bool> TryWriteLineAsync(Connection connection, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        try
        {
            await connection.WriteGate.WaitAsync(connection.Lost.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            await connection.Output.WriteAsync(bytes, connection.Lost.Token);
            await connection.Output.FlushAsync(connection.Lost.Token);
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        {
            connection.Lost.Cancel();
            return false;
        }
        finally
        {
            connection.WriteGate.Release();
        }
    }

    static void Drop(Connection connection)
    {
        connection.Lost.Cancel();
        connection.Gone.TrySetResult();
        try { connection.Output.Dispose(); } catch { }
        try { connection.Input.Dispose(); } catch { }
    }

    static JsonObject Notification(string method, JsonObject @params) => new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params };

    static JsonObject Response(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool TryId(JsonNode? id, out long value)
    {
        value = 0;
        return id is JsonValue v && (v.TryGetValue(out value) || (v.TryGetValue<string>(out var s) && long.TryParse(s, out value)));
    }

    void Log(string message) => options.Log?.Invoke($"host {options.Adapter} {options.HostId}: {message}");

    /// <summary>A session's notification as the host sent it, kept for a conductor that comes back.</summary>
    sealed record Kept(long Seq, string SessionId, string Line);

    sealed class IdleEligibility(Connection owner, string prompt, string nativeTurn)
    {
        public Connection Owner { get; } = owner;
        public string Prompt { get; } = prompt;
        public string NativeTurn { get; } = nativeTurn;
        public bool Seen; public bool Revoked; public long Epoch; public long Revision;
        public JsonObject Frame(string session, string state) => new() { ["_meta"] = new JsonObject { ["sessionId"] = session,
            ["promptId"] = Prompt, ["nativeTurnId"] = NativeTurn, ["turnEpoch"] = Epoch, ["ingressRevision"] = Revision, ["state"] = state } };
    }

    /// <summary>A conductor's request on its way to the agent.</summary>
    sealed class Forwarded
    {
        public string? GuardedAttempt { get; init; }
        public JsonObject? GuardedIdentity { get; init; }
        public long? GuardedEpoch { get; init; }
        public long? GuardedWriteAdmission { get; init; }
        public long? GuardedAckRevision { get; init; }
        public long? GuardedRetireRevision { get; init; }
        public bool GuardedIsAbort { get; init; }
        public required Connection Connection { get; init; }
        public required JsonNode ConductorId { get; init; }
        public required string Method { get; init; }
        public string? SessionId { get; init; }
        public string? PromptId { get; init; }
        public long? IdleAdmission { get; init; }
        public long? ClaudeObservationAdmission { get; init; }
        public long ClaudeObservationEpoch { get; init; }
        public string? ReportId { get; init; }
        public PeerTurnIdentity? PeerRoute { get; init; }
        public string? NoteStatusReport { get; init; }
        public string? NoteAttempt { get; init; }
        public string? NoteDigest { get; init; }
        public string? NoteAckUuid { get; init; }
        public long? NoteAckRevision { get; init; }
        public JsonObject? Delivery { get; init; }
        public string TurnKind { get; init; } = "unknown";
        public ReportCapture? Capture { get; set; }
        public string? CaptureFailure { get; set; }
        public string? ModeId { get; init; }
        public string? ModelId { get; init; }

        /// <summary>The asker's name for the question this request carries (<c>gorynych/askId</c>): an opinion, by channel or in a turn.</summary>
        public string? AskId { get; init; }

        /// <summary>For an opinion asked in a turn: its <c>message_id</c> (<c>gorynych/opinion</c>), which the agent's reply names.</summary>
        public string? Opinion { get; init; }

        /// <summary>For an opinion: the session that asks (<c>gorynych/fromSessionId</c>), which alone takes the answer from the next service.</summary>
        public string? From { get; init; }

        /// <summary>For a steer: the service message ID under which its content is journaled and kept natively.</summary>
        public string? MessageId { get; init; }

        /// <summary>For a steer: the conductor's internal sender token, kept with the acceptance.</summary>
        public string? SteerOrigin { get; init; }

        /// <summary>For a steer: its content, kept so the next conductor journals what was accepted.</summary>
        public JsonArray? Prompt { get; init; }
    }

    /// <summary>A question of the agent's no conductor has answered yet.</summary>
    sealed class Asked
    {
        public required JsonNode AgentId { get; init; }
        public required JsonObject Message { get; init; }
        public Connection? Connection { get; set; }
        public long? ConductorId { get; set; }
    }

    // A release belongs to one admission, never to a subsequent use of the lane.
    sealed class GuardedLease(AdapterHost owner, Connection connection, bool abort)
    {
        public void Release()
        {
            lock (owner.state)
            {
                if (abort)
                {
                    if (ReferenceEquals(connection.GuardedAbortLease, this)) connection.GuardedAbortLease = null;
                }
                else if (ReferenceEquals(connection.CompletionGuardedLease, this)) connection.CompletionGuardedLease = null;
            }
        }
    }

    sealed class Connection(long number, Stream input, Stream output)
    {
        public long Number { get; } = number;
        public Stream Input { get; } = input;
        public Stream Output { get; } = output;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public CancellationTokenSource Lost { get; } = new();
        public TaskCompletionSource Activated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long NextAsk;
        public bool AcceptsIdle;
        public GuardedLease? CompletionGuardedLease, GuardedAbortLease;
        public bool AcceptsCompletionGuarded;
        public long CompletionGuardedEpoch;
        public bool AcceptsClaudeObservation;
        public long ClaudeObservationEpoch;
        public bool IsLost => Lost.IsCancellationRequested;
    }
}
