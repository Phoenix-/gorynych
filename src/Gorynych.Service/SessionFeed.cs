using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

/// <summary>
/// The live side of one session: its journal, the clients attached to it, and the gate that
/// keeps journal order, live delivery and replay consistent. A client that attaches during
/// a replay sees the whole history and then every later update exactly once.
/// </summary>
internal sealed class SessionFeed(string sessionId, SessionJournal journal)
{
    // Both state ownership and delivery are serialized by gate, so a late host-loss callback
    // cannot replace a newer host's already-delivered observation.
    readonly Dictionary<string, (string Parent, JsonRpcConnection Source)> subagentStateOwners = new(StringComparer.Ordinal);
    JsonRpcConnection? importingHistory;

    public void BeginHistoryImport(JsonRpcConnection source) { lock (clientsGate) importingHistory = source; }
    public void EndHistoryImport(JsonRpcConnection source) { lock (clientsGate) if (ReferenceEquals(importingHistory, source)) importingHistory = null; }
    public bool IsHistoryImport(JsonRpcConnection? source) { lock (clientsGate) return source is not null && ReferenceEquals(importingHistory, source); }

    public SessionActivity Activity { get; init; } = new("standalone");
    // A completed native prompt is positive idle evidence only for that exact live route.
    JsonRpcConnection? confirmedIdleRoute;
    public void ConfirmIdle(JsonRpcConnection rpc) => Volatile.Write(ref confirmedIdleRoute, rpc);
    public bool HasConfirmedIdle(JsonRpcConnection rpc) => ReferenceEquals(Volatile.Read(ref confirmedIdleRoute), rpc);
    // Volatile in-memory evidence only; route lifecycle invalidation changes the epoch.
    JsonRpcConnection? strictLoadedIdleRoute;
    int evidencePrompts;
    bool strictIdleEligible;
    long assignmentActivityFence;
    internal long AssignmentActivityFence { get { lock (nativeActivityGate) return assignmentActivityFence; } }
    sealed class PendingEarlyIdle(string id, string generation, string prompt, JsonRpcConnection route, long epoch)
    {
        public string Id = id, Generation = generation, Prompt = prompt;
        public JsonRpcConnection Route = route; public long Epoch = epoch;
        public long EarlyFence = -1, ResponseFence = -1, TurnEpoch, IngressRevision;
        public string? NativeTurn, TerminalTurn; public bool Seen, Revoked, Terminal;
    }
    PendingEarlyIdle? pendingEarlyIdle;
    bool strictIdleFromEarly;
    long assignmentEvidenceEpoch;
    (string Id, string Generation, string Prompt, JsonRpcConnection Route)? initialAssignmentTurn;
    internal long AssignmentEvidenceEpoch { get { lock (nativeActivityGate) return assignmentEvidenceEpoch; } }
    internal void InvalidateAssignmentEvidence(JsonRpcConnection source)
    {
        lock (nativeActivityGate) { assignmentEvidenceEpoch++; if (initialAssignmentTurn?.Route == source) { initialAssignmentTurn = null; strictIdleEligible = false; } if (pendingEarlyIdle?.Route == source) pendingEarlyIdle = null; if (strictLoadedIdleRoute == source) { strictLoadedIdleRoute = null; strictIdleFromEarly = false; } }
    }
    internal void BeginEarlyIdleCandidate(long epoch, string id, string generation, string prompt, JsonRpcConnection source)
    {
        lock (nativeActivityGate) if (assignmentEvidenceEpoch == epoch) pendingEarlyIdle = new(id, generation, prompt, source, epoch);
    }
    internal void ObserveInitialAssignmentTurn(long epoch, long responseFence, string id, string generation, string prompt, JsonRpcConnection source, string? nativeTurn = null)
    {
        lock (nativeActivityGate) if (assignmentEvidenceEpoch == epoch)
        {
            initialAssignmentTurn = (id, generation, prompt, source); strictLoadedIdleRoute = null; strictIdleFromEarly = false;
            strictIdleEligible = responseFence >= 0 && assignmentActivityFence == responseFence && !AgentTurnRunning && Asking == 0 && PendingRequests == 0;
            if (pendingEarlyIdle is { } pending && pending.Epoch == epoch && pending.Route == source && pending.Id == id &&
                pending.Generation == generation && pending.Prompt == prompt)
            { pending.Terminal = true; pending.TerminalTurn = nativeTurn; pending.ResponseFence = responseFence;
                if (pending.Revoked) strictIdleEligible = false;
                ApplyEarlyIdle(pending); }
        }
    }
    internal (bool Seen, bool Revoked) EarlyIdleStateForTests
    { get { lock (nativeActivityGate) return (pendingEarlyIdle?.Seen == true, pendingEarlyIdle?.Revoked == true); } }
    void ApplyEarlyIdle(PendingEarlyIdle pending)
    {
        if (pending.Terminal && pending.Seen && !pending.Revoked && pending.NativeTurn is not null &&
            pending.TerminalTurn == pending.NativeTurn && pending.EarlyFence >= 0 && pending.EarlyFence == pending.ResponseFence &&
            assignmentActivityFence == pending.ResponseFence && pending.Epoch == assignmentEvidenceEpoch && strictIdleEligible &&
            initialAssignmentTurn is { } fact && fact.Route == pending.Route && fact.Prompt == pending.Prompt &&
            fact.Id == pending.Id && fact.Generation == pending.Generation && evidencePrompts <= 1 &&
            !AgentTurnRunning && Asking == 0 && PendingRequests == 0)
        { strictLoadedIdleRoute = pending.Route; strictIdleFromEarly = true; }
    }
    internal void ObserveEarlyIdleFrame(JsonRpcConnection source, string prompt, string nativeTurn, long turnEpoch, long ingressRevision, bool revoked)
    {
        lock (nativeActivityGate)
        {
            if (pendingEarlyIdle is not { } pending || pending.Route != source || pending.Prompt != prompt || pending.Epoch != assignmentEvidenceEpoch) return;
            if (!revoked)
            {
                if (pending.Seen || pending.Revoked || pending.EarlyFence < 0) return;
                pending.Seen = true; pending.NativeTurn = nativeTurn; pending.TurnEpoch = turnEpoch; pending.IngressRevision = ingressRevision;
                ApplyEarlyIdle(pending);
            }
            else if (pending.Seen && !pending.Revoked && pending.NativeTurn == nativeTurn && pending.TurnEpoch == turnEpoch && pending.IngressRevision == ingressRevision)
            {
                pending.Revoked = true; assignmentActivityFence++; strictIdleEligible = false;
                if (strictIdleFromEarly && strictLoadedIdleRoute == source) { strictLoadedIdleRoute = null; strictIdleFromEarly = false; }
            }
        }
    }
    internal bool HasInitialAssignmentTurn(string id, string generation, JsonRpcConnection source)
    {
        lock (nativeActivityGate) return initialAssignmentTurn is { } fact && fact.Id == id && fact.Generation == generation && fact.Route == source;
    }
    internal void RevokeStrictLoadedIdle() { lock (nativeActivityGate) strictLoadedIdleRoute = null; }
    internal void ObserveStrictLoadedIdle(JsonRpcConnection source, bool strict, bool discontinuity)
    {
        lock (nativeActivityGate)
        {
            if (discontinuity) { assignmentActivityFence++; strictIdleEligible = false; }
            if (strict && pendingEarlyIdle is { } pending && pending.Route == source && pending.Epoch == assignmentEvidenceEpoch && !pending.Terminal)
                pending.EarlyFence = assignmentActivityFence;
            strictIdleFromEarly = false;
            strictLoadedIdleRoute = strict && strictIdleEligible && initialAssignmentTurn?.Route == source &&
                TurnGate.CurrentCount == 1 && evidencePrompts == 0 && !AgentTurnRunning && Asking == 0 && PendingRequests == 0 ? source : null;
        }
    }
    internal (bool Terminal, bool LoadedIdle) InitialAssignmentEvidence(JsonRpcConnection source, string id, string generation)
    {
        lock (nativeActivityGate)
        {
            var terminal = initialAssignmentTurn is { } fact && fact.Route == source && fact.Id == id && fact.Generation == generation;
            var idle = terminal && strictIdleEligible && strictLoadedIdleRoute == source && TurnGate.CurrentCount == 1 &&
                evidencePrompts == 0 && !AgentTurnRunning && Asking == 0 && PendingRequests == 0;
            return (terminal, idle);
        }
    }
    internal ClaudeObservationFence? ClaudeObservationFor(HostLink route, string id, string generation)
    {
        lock (nativeActivityGate)
        {
            if (route.Rpc.IsClosed || route.HostId is null || route.Info.Meta?[GorynychProtocol.CapHostClaudePromptObservation] is not JsonValue capability ||
                !capability.TryGetValue<bool>(out var supported) || !supported ||
                route.Info.Meta?[GorynychProtocol.CapHostBackgroundEvidence] is not JsonValue bgCapability || !bgCapability.TryGetValue<bool>(out var bgSupported) || !bgSupported ||
                !ReferenceEquals(route.Rpc, backgroundEvidenceRoute) || backgroundEvidence is not { Items.Length: 0 } bg || bg.HostId != route.HostId ||
                !ReferenceEquals(completeBackgroundRoute, route.Rpc) || completeBackgroundStream != bg.Stream || backgroundUse || knownBackgroundUse ||
                initialAssignmentTurn is not { } fact || fact.Route != route.Rpc || fact.Id != id || fact.Generation != generation || !strictIdleEligible ||
                TurnGate.CurrentCount != 1 || evidencePrompts != 0 || AgentTurnRunning || Asking != 0 || PendingRequests != 0) return null;
            return new ClaudeObservationFence(route.Rpc, route.HostId, fact.Prompt, assignmentEvidenceEpoch, assignmentActivityFence, bg.Stream, bg.Revision);
        }
    }

    int nativeActivity;
    bool backgroundUse;
    bool knownBackgroundUse;
    readonly Lock nativeActivityGate = new();
    JsonRpcConnection? backgroundEvidenceRoute;
    BackgroundEvidence? backgroundEvidence;
    string? backgroundEvidenceHost;
    long backgroundEvidenceRevision;
    internal void ObserveBackgroundEvidence(JsonRpcConnection source, JsonNode? value)
    {
        var observed = BackgroundEvidence.Parse(value);
        var stamped = BackgroundEvidence.TryStamp(value, out var host, out var revision);
        lock (nativeActivityGate)
        {
            if (stamped && host == backgroundEvidenceHost && (revision < backgroundEvidenceRevision ||
                revision == backgroundEvidenceRevision && backgroundEvidence is null && observed is not null)) return;
            backgroundEvidenceRoute = source; backgroundEvidence = observed;
            // An unstamped malformed observation revokes confidence but must not erase
            // the high-water mark and permit an older complete snapshot to revive it.
            if (stamped) { backgroundEvidenceHost = host; backgroundEvidenceRevision = revision; }
        }
    }
    internal void InvalidateBackgroundEvidence(JsonRpcConnection source)
    {
        lock (nativeActivityGate)
            if (ReferenceEquals(backgroundEvidenceRoute, source)) backgroundEvidence = null;
        // Preserve host/revision: a delayed old snapshot cannot revive a removed route.
    }
    internal long ObservedBackgroundRevision { get { lock (nativeActivityGate) return backgroundEvidenceRevision; } }
    // The caller must pass the current host route, not a remembered connection or client metadata.
    internal BackgroundEvidence? BackgroundEvidenceFor(HostLink? route)
    {
        lock (nativeActivityGate)
            return route is not null && !route.Rpc.IsClosed && ReferenceEquals(route.Rpc, backgroundEvidenceRoute) &&
                route.Info.Meta?[GorynychProtocol.CapHostBackgroundEvidence] is JsonValue cap && cap.TryGetValue<bool>(out var supported) && supported &&
                route.HostId is not null && route.HostId == backgroundEvidence?.HostId ? backgroundEvidence : null;
    }

    JsonRpcConnection? completeBackgroundRoute;
    string? completeBackgroundStream;
    // A full change stream remains authoritative across ordinary turns on the same
    // live host; null/invalid/exit or a different route revokes that evidence.
    public void BeginReminderActivityEpoch() => Volatile.Write(ref confirmedIdleRoute, null);
    public bool HasFreshReminderBackground(JsonRpcConnection rpc)
    {
        lock (nativeActivityGate) return ReferenceEquals(completeBackgroundRoute, rpc) && completeBackgroundStream is not null;
    }
    public string? ReminderBackgroundStream(JsonRpcConnection rpc)
    {
        lock (nativeActivityGate) return ReferenceEquals(completeBackgroundRoute, rpc) ? completeBackgroundStream : null;
    }
    public bool HasBackgroundWork
    {
        get { lock (nativeActivityGate) return backgroundUse || knownBackgroundUse; }
    }
    public void PinBackground()
    {
        lock (nativeActivityGate)
            if (!backgroundUse) { EnterUse(); backgroundUse = true; }
    }
    public void PinKnownBackground()
    {
        lock (nativeActivityGate)
            if (!knownBackgroundUse) { EnterUse(); knownBackgroundUse = true; }
    }
    public bool ReportKnownBackground(BackgroundWorkSnapshot? tasks)
    {
        lock (nativeActivityGate)
        {
            var needed = tasks is null ? knownBackgroundUse : tasks.Count > 0;
            if (needed && !knownBackgroundUse) { EnterUse(); knownBackgroundUse = true; }
            Activity.ReportKnownBackground(tasks);
            if (!needed && knownBackgroundUse) { knownBackgroundUse = false; ExitUse(); return true; }
            return false;
        }
    }
    public bool ReportBackground(BackgroundWorkSnapshot? tasks, JsonRpcConnection? observedFrom = null, string? stream = null)
    {
        lock (nativeActivityGate)
        {
            var needed = tasks is null ? backgroundUse : tasks.Count > 0;
            if (needed && !backgroundUse) { EnterUse(); backgroundUse = true; }
            Activity.ReportBackground(tasks);
            completeBackgroundRoute = tasks is { Count: 0 } && stream is not null ? observedFrom : null;
            completeBackgroundStream = completeBackgroundRoute is null ? null : stream;
            if (!needed && backgroundUse) { backgroundUse = false; ExitUse(); return true; }
            return false;
        }
    }
    public bool ReleaseBackground()
    {
        lock (nativeActivityGate)
        {
            Activity.ReportBackground(null);
            Activity.ReportKnownBackground(null);
            completeBackgroundRoute = null; completeBackgroundStream = null;
            var released = backgroundUse || knownBackgroundUse;
            if (backgroundUse) { backgroundUse = false; ExitUse(); }
            if (knownBackgroundUse) { knownBackgroundUse = false; ExitUse(); }
            return released;
        }
    }
    public void BeginNativeActivity()
    {
        lock (nativeActivityGate)
        {
            strictLoadedIdleRoute = null; strictIdleEligible = false; assignmentActivityFence++;
            if (Interlocked.Exchange(ref nativeActivity, 1) == 0) { Volatile.Write(ref confirmedIdleRoute, null); EnterUse(); }
        }
    }
    public bool EndNativeActivity()
    {
        lock (nativeActivityGate)
        {
            if (Interlocked.Exchange(ref nativeActivity, 0) == 0) return false;
            ExitUse();
            return true;
        }
    }
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Lock clientsGate = new();
    readonly List<ClientConnection> clients = [];
    TaskCompletionSource clientAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int pendingRequests;

    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    Queue<JsonObject>? replay; // under clientsGate
    int replayLimit;
    bool replayDropped;

    readonly SemaphoreSlim steerGate = new(1, 1); // one acceptance record and its response journal a message once
    HashSet<string>? steeredIds; // under clientsGate once read: steered message IDs already journaled

    int steeringCalls;
    TaskCompletionSource steeringIdle = Completed();

    /// <summary>
    /// A steering request on its way to the adapter, from before it is written until its outcome is
    /// known. A turn's end mark waits for these, so an accepted message is journaled before it.
    /// </summary>
    public IDisposable BeginSteering()
    {
        lock (clientsGate)
        {
            steeringCalls++;
            if (steeringCalls == 1) steeringIdle = NewSignal();
        }
        return new SteeringCall(this);
    }

    sealed class SteeringCall(SessionFeed feed) : IDisposable
    {
        int done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) != 0) return;
            lock (feed.clientsGate)
                if (--feed.steeringCalls == 0) feed.steeringIdle.TrySetResult();
        }
    }

    /// <summary>Whether a steering request of the session is still on its way.</summary>
    public bool HasSteeringInFlight
    {
        get { lock (clientsGate) return steeringCalls > 0; }
    }

    /// <summary>Completes once no steering request of the session is still on its way.</summary>
    public Task WaitForSteeringAsync()
    {
        lock (clientsGate) return steeringCalls == 0 ? Task.CompletedTask : steeringIdle.Task;
    }

    HashSet<string> SteeredIds()
    {
        if (Volatile.Read(ref steeredIds) is { } known) return known;
        var read = Journal.SteeredMessageIds();
        lock (clientsGate) return steeredIds ??= read;
    }

    /// <summary>Whether an accepted steer with this service message ID is already in the journal.</summary>
    public bool HasSteeredMessage(string messageId)
    {
        var ids = SteeredIds();
        lock (clientsGate) return ids.Contains(messageId);
    }

    /// <summary>
    /// Journals a confirmed accepted steer as one user message (its blocks share
    /// <paramref name="messageId"/>) and publishes it to the clients except <paramref name="except"/>.
    /// False when that message is journaled already: a host record and its response, or a replayed
    /// record, journal it once. The acceptance record's place in the host stream goes on the first
    /// block, so the journal itself remembers the cursor.
    /// </summary>
    public async Task<bool> PublishSteeredAsync(List<JsonObject> blocks, string messageId, string? hostSeq,
        ClientConnection? except, Action remember)
    {
        await steerGate.WaitAsync();
        try
        {
            if (Retired) return false;
            var ids = SteeredIds();
            lock (clientsGate)
                if (ids.Contains(messageId)) return false;
            for (var i = 0; i < blocks.Count; i++)
            {
                var meta = blocks[i]["_meta"] as JsonObject ?? [];
                meta[GorynychProtocol.MetaSteered] = true;
                if (i == 0 && hostSeq is not null) meta[GorynychProtocol.MetaHostSeq] = hostSeq;
                blocks[i]["_meta"] = meta;
            }
            await gate.WaitAsync();
            try
            {
                if (Retired) return false;
                foreach (var block in blocks)
                {
                    StampMessageId(block);
                    RememberToolTitle(block);
                }
                Journal.AppendMany(blocks);
                lock (clientsGate) ids.Add(messageId);
                remember();
                foreach (var client in Clients())
                    if (!ReferenceEquals(client, except))
                        foreach (var block in blocks)
                            if (!await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(block.DeepClone()))) break;
            }
            finally
            {
                gate.Release();
            }
            return true;
        }
        finally
        {
            steerGate.Release();
        }
    }

    /// <summary>
    /// From now until <see cref="EndReplay"/>, the parts of turns the agent sends are its history
    /// replayed for a catch-up (<see cref="HistoryCatchUp"/>): kept aside, neither journaled nor
    /// sent. Only the last <paramref name="limit"/> are kept (the journal could keep no more);
    /// anything else (a title, the commands, usage) is published as usual.
    /// </summary>
    public void BeginReplay(int limit)
    {
        lock (clientsGate)
        {
            replay = new Queue<JsonObject>();
            replayLimit = Math.Max(1, limit);
            replayDropped = false;
        }
    }

    /// <summary>Keeps <paramref name="update"/> aside while a replay is collected; false otherwise.</summary>
    public bool CollectReplay(JsonObject update)
    {
        if (!SessionJournal.IsTurnKind((string?)update["sessionUpdate"]) && !SubagentRouting.IsPreview(update)) return false;
        lock (clientsGate)
        {
            if (replay is null) return false;
            replay.Enqueue(update);
            if (replay.Count > replayLimit)
            {
                replay.Dequeue();
                replayDropped = true;
            }
            return true;
        }
    }

    /// <summary>The updates collected since <see cref="BeginReplay"/>, and whether older ones were dropped.</summary>
    public (List<JsonObject> Updates, bool Dropped) EndReplay()
    {
        lock (clientsGate)
        {
            var got = (replay?.ToList() ?? [], replayDropped);
            replay = null;
            return got;
        }
    }

    /// <summary>Orders a prompt and a later <c>session/cancel</c> on their way to the agent.</summary>
    public SemaphoreSlim ForwardGate { get; } = new(1, 1);

    // A reminder's mode must remain stable until the native turn answers; ACP write
    // completion alone does not prove the adapter has started processing the prompt.
    public SemaphoreSlim ReminderModeGate { get; } = new(1, 1);

    /// <summary>One resume or load of the session in its agent at a time.</summary>
    public SemaphoreSlim OpenGate { get; } = new(1, 1);

    /// <summary>One prompt turn at a time: the user's turns and opinion requests delivered as turns.</summary>
    public SemaphoreSlim TurnGate { get; } = new(1, 1);

    /// <summary>
    /// Held for the whole of a draft's discard or a deletion. An operation that uses the session
    /// takes it briefly to register (<see cref="EnterUse"/>), so it waits for a discard under way
    /// and a discard never starts under it.
    /// </summary>
    public SemaphoreSlim LifeGate { get; } = new(1, 1);

    int users;

    /// <summary>
    /// Operations using the session now: loads, resumes, prompts, opinions, forwarded requests, and
    /// the agent's requests on their way to a client.
    /// </summary>
    public int Users => Volatile.Read(ref users);

    public void EnterUse()
    {
        Interlocked.Increment(ref users);
        StopIdle();
    }
    public void ExitUse() => Interlocked.Decrement(ref users);

    volatile bool clientTurn;
    readonly Lock discardGate = new();
    long discardVersion;
    bool discardAfterClose;

    /// <summary>
    /// True while the turn that holds <see cref="TurnGate"/> is a client's prompt; false for a
    /// service turn (an opinion request, a report) and a spawned session's first turn. A client's
    /// <c>session/close</c> cancels only a client's turn.
    /// </summary>
    public bool ClientTurn
    {
        get => clientTurn;
        set => clientTurn = value;
    }

    /// <summary>An explicit close's generation, or zero when no close asks for a discard retry.</summary>
    public long DiscardAfterCloseVersion
    {
        get { lock (discardGate) return discardAfterClose ? discardVersion : 0; }
    }

    long RequestDiscardAfterClose()
    {
        lock (discardGate)
        {
            discardAfterClose = true;
            return ++discardVersion;
        }
    }

    public void CancelDiscardAfterClose()
    {
        lock (discardGate)
        {
            discardAfterClose = false;
            discardVersion++;
        }
    }

    public bool IsDiscardAfterClose(long version)
    {
        lock (discardGate) return discardAfterClose && discardVersion == version;
    }

    public void CompleteDiscardAfterClose(long version)
    {
        lock (discardGate)
            if (discardVersion == version) discardAfterClose = false;
    }

    int otherText;

    /// <summary>
    /// True from the moment another session's text reaches the agent in the middle of its work (a
    /// question or a child's report through its live-note path: Claude's input or channel, which
    /// may join a client's turn), or a turn of the agent's own begins (which such text, or anything
    /// but a client's prompt, starts), until the session is idle again (<see cref="TurnEnded"/>). A
    /// client's prompt that joins such a turn finds it set too. So a spawn the agent says the user
    /// asked for is asked all the same: the request may come from that text.
    /// </summary>
    public bool HasOtherSessionText => Volatile.Read(ref otherText) != 0;

    /// <summary>Marks <see cref="HasOtherSessionText"/>; returns whether it was marked already.</summary>
    readonly Lock otherTextGate = new();
    long otherTextVersion;
    public bool MarkOtherSessionText() => MarkOtherSessionText(out _);
    internal bool MarkOtherSessionText(out long version)
    {
        lock (otherTextGate)
        {
            version = ++otherTextVersion;
            return Interlocked.Exchange(ref otherText, 1) != 0;
        }
    }

    /// <summary>Takes back a mark whose text did not reach the agent after all (it has no live-note path), unless it was marked before.</summary>
    public void UnmarkOtherSessionText(bool markedBefore)
    {
        lock (otherTextGate)
            if (!markedBefore) { otherTextVersion++; Interlocked.Exchange(ref otherText, 0); }
    }
    internal void UnmarkOtherSessionText(bool markedBefore, long version)
    {
        lock (otherTextGate)
            if (!markedBefore && otherTextVersion == version) Interlocked.Exchange(ref otherText, 0);
    }

    /// <summary>
    /// A turn ended: clears <see cref="HasOtherSessionText"/> once neither a client's turn nor one of
    /// the agent's own runs, and notes when (<see cref="LastTurnEnded"/>).
    /// </summary>
    public void TurnEnded()
    {
        Interlocked.Exchange(ref lastTurnEnded, DateTimeOffset.UtcNow.UtcTicks);
        lock (otherTextGate)
            if (!ClientTurn && Volatile.Read(ref agentTurn) == 0) { otherTextVersion++; Interlocked.Exchange(ref otherText, 0); }
    }

    long lastTurnEnded;

    /// <summary>
    /// When the last turn this service saw end ended (null: none in this run): the session's time in
    /// the list while its agent's own time is behind it (an agent that notes a turn's end a moment late).
    /// </summary>
    public DateTimeOffset? LastTurnEnded => Interlocked.Read(ref lastTurnEnded) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    int agentTurn;
    readonly Lock agentActivityGate = new();

    /// <summary>
    /// Marks the start of a turn the agent runs that the service did not start; false when one was
    /// already marked, so a turn counts as one operation (<see cref="EnterUse"/>) however often
    /// its start is marked.
    /// </summary>
    public bool BeginAgentTurn()
    {
        lock (agentActivityGate)
        {
            lock (nativeActivityGate)
            {
                strictLoadedIdleRoute = null; strictIdleEligible = false; assignmentActivityFence++;
                if (Interlocked.Exchange(ref agentTurn, 1) != 0) return false;
            }
            BeginReminderActivityEpoch();
            Activity.Begin();
            return true;
        }
    }

    /// <summary>Marks the end of the agent's own turn; false when none was marked.</summary>
    public bool EndAgentTurn()
    {
        lock (agentActivityGate)
        {
            if (Interlocked.Exchange(ref agentTurn, 0) != 1) return false;
            Activity.End();
            Quieted();
            return true;
        }
    }

    /// <summary>A turn of the agent's own runs (<see cref="BeginAgentTurn"/>).</summary>
    public bool AgentTurnRunning => Volatile.Read(ref agentTurn) != 0 || Volatile.Read(ref nativeActivity) != 0;

    /// <summary>Why a session is read-only, in words for a person, and who holds it, if another app does.</summary>
    public sealed record ReadOnlyState(string Reason, string? OpenElsewhere);

    ReadOnlyState? readOnly;

    /// <summary>
    /// Read-only, as its agent said last (<c>gorynych/interactivity</c> on its open or in an update);
    /// null: full, a client may prompt it (docs/service-protocol.md, "Read-only sessions").
    /// </summary>
    public ReadOnlyState? ReadOnly => Volatile.Read(ref readOnly);

    /// <summary>Sets <see cref="ReadOnly"/>; true when that changed it.</summary>
    public bool SetReadOnly(ReadOnlyState? state) => !Equals(Interlocked.Exchange(ref readOnly, state), state);

    ReadOnlyState? reportedReadOnly;

    /// <summary>
    /// What its agent said last of whether the session is read-only, which <see cref="ReadOnly"/>
    /// follows once what was made meanwhile is caught up (Conductor.WritableAgainAsync).
    /// </summary>
    public ReadOnlyState? ReportedReadOnly
    {
        get => Volatile.Read(ref reportedReadOnly);
        set => Volatile.Write(ref reportedReadOnly, value);
    }

    readonly Lock nativeOpenGate = new();
    JsonRpcConnection? reattachedOpenRoute;

    /// <summary>The exact host connection whose native open has not been rechecked.</summary>
    public JsonRpcConnection? ReattachedOpenRoute
    {
        get => Volatile.Read(ref reattachedOpenRoute);
    }

    public void RequireReattachedOpen(JsonRpcConnection source)
    {
        lock (nativeOpenGate) { Volatile.Write(ref openedReadOnly, 1); Volatile.Write(ref reattachedOpenRoute, source); }
    }

    public bool NeedsRetakeFor(JsonRpcConnection source) => OpenedReadOnly || ReferenceEquals(ReattachedOpenRoute, source);

    public bool ApplyNativeOpen(JsonRpcConnection source, bool readOnly, Action apply)
    {
        lock (nativeOpenGate)
        {
            if (reattachedOpenRoute is { } route && !ReferenceEquals(route, source)) return false;
            apply();
            Volatile.Write(ref openedReadOnly, readOnly ? 1 : 0);
            return true;
        }
    }

    public bool TrySetReadOnlyFromOpen(JsonRpcConnection source, ReadOnlyState? state, out bool changed)
    {
        lock (nativeOpenGate)
        {
            if (reattachedOpenRoute is { } route && !ReferenceEquals(route, source)) { changed = false; return false; }
            changed = SetReadOnly(state);
            return true;
        }
    }

    public void ClearReattachedOpenRoute(JsonRpcConnection source)
    {
        lock (nativeOpenGate) if (ReferenceEquals(reattachedOpenRoute, source)) Volatile.Write(ref reattachedOpenRoute, null);
    }

    int openedReadOnly;

    /// <summary>
    /// Its agent opened it for reading only, or a reattached Codex host has not proved its native
    /// open was writable. Even once it is writable again, the agent has not taken it yet (Codex takes the thread at its next resume),
    /// so a turn opens it again first, which also catches its journal up with what was made meanwhile.
    /// </summary>
    public bool OpenedReadOnly
    {
        get => Volatile.Read(ref openedReadOnly) != 0;
    }

    int asking;

    /// <summary>
    /// How many of the agent's requests (approvals, forms) wait for an answer: the session does not
    /// move meanwhile. One counts from the moment its connection read it (<see cref="Conductor.OnAgentRequestArrived"/>).
    /// </summary>
    public int Asking => Volatile.Read(ref asking);

    public void BeginAsking() { lock (nativeActivityGate) { strictLoadedIdleRoute = null; strictIdleEligible = false; assignmentActivityFence++; Interlocked.Increment(ref asking); } }

    public void EndAsking()
    {
        Interlocked.Decrement(ref asking);
        Quieted();
    }

    TaskCompletionSource quieter = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes the next time the agent's own turn ends or one of its questions is answered: what a
    /// prompt for a session in a stale host waits for before the session moves. Taken before the
    /// state is looked at, so an end in between is not missed.
    /// </summary>
    public Task Quieter => Volatile.Read(ref quieter).Task;

    void Quieted() => Interlocked.Exchange(ref quieter, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    long lastActivity = DateTimeOffset.MinValue.UtcTicks;

    /// <summary>When the agent last sent an update for the session, on the conductor's clock.</summary>
    public DateTimeOffset LastActivity => new(Interlocked.Read(ref lastActivity), TimeSpan.Zero);

    public void Active(DateTimeOffset now)
    {
        Volatile.Write(ref confirmedIdleRoute, null);
        Interlocked.Exchange(ref lastActivity, now.UtcTicks);
    }

    readonly Lock idleGate = new();
    CancellationTokenSource? idleStop;
    long idleTimer;

    /// <summary>
    /// Stops the session's idle timer (a client attached, an operation registered): cancels its
    /// wait, and makes it no longer the current one. Returns the new current number.
    /// </summary>
    public long StopIdle() => SwapIdle(null);

    /// <summary>
    /// Starts a new idle timer, stopping the one before: its number, and the token that stops its
    /// wait (with <paramref name="shutdown"/> too).
    /// </summary>
    public (long Timer, CancellationToken Stop) StartIdle(CancellationToken shutdown)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        return (SwapIdle(stop), stop.Token);
    }

    long SwapIdle(CancellationTokenSource? next)
    {
        CancellationTokenSource? stopped;
        long timer;
        lock (idleGate)
        {
            stopped = idleStop;
            idleStop = next;
            timer = ++idleTimer;
        }
        if (stopped is not null)
        {
            try { stopped.Cancel(); }
            catch (ObjectDisposedException) { }
            stopped.Dispose();
        }
        return timer;
    }

    /// <summary>The current idle timer (see <see cref="StopIdle"/>).</summary>
    public long IdleTimer
    {
        get { lock (idleGate) return idleTimer; }
    }

    /// <summary>
    /// Runs when a detach leaves the session with no client attached (a client that stopped
    /// answering, a send that failed), so the conductor can start its idle timer. A close or a
    /// disconnect detaches without it and starts the timer itself.
    /// </summary>
    public Action? LastClientLeft { get; init; }

    bool retired;

    /// <summary>True once the session is deleted: nothing more is journaled or sent.</summary>
    public bool Retired
    {
        get { lock (clientsGate) return retired; }
    }

    /// <summary>Marks the session deleted: detaches its clients and fails requests waiting for one.</summary>
    public void Retire()
    {
        TaskCompletionSource signal;
        lock (clientsGate)
        {
            retired = true;
            clients.Clear();
            unanswered.Clear();
            signal = clientAttached;
        }
        signal.TrySetResult();
        StopIdle();
    }

    readonly SessionConfiguration configuration = new();
    int reminderModeUncertain;
    internal bool ReminderModeUncertain => Volatile.Read(ref reminderModeUncertain) != 0;
    internal void InvalidateReminderMode() => Volatile.Write(ref reminderModeUncertain, 1);
    void ConfirmReminderMode() => Volatile.Write(ref reminderModeUncertain, 0);
    static bool HasModeOption(JsonNode? options) => options is JsonArray array &&
        array.OfType<JsonObject>().Any(option => (string?)option["category"] == "mode" && option["currentValue"] is not null);

    /// <summary>
    /// Takes the session state from an agent's response (<c>session/new</c>, <c>session/load</c>,
    /// <c>session/resume</c>, <c>session/set_config_option</c>): the fields it has replace ours.
    /// </summary>
    public void CaptureState(JsonNode? response)
    {
        configuration.Capture(response);
        if (response?["modes"]?["currentModeId"] is not null || HasModeOption(response?["configOptions"])) ConfirmReminderMode();
    }

    /// <summary>
    /// Follows the agent's <c>config_option_update</c> and <c>current_mode_update</c>. The list of
    /// commands is state too, but goes through the feed's gate (<see cref="PublishCommandsAsync"/>).
    /// </summary>
    public void ApplyStateUpdate(JsonObject update)
    {
        configuration.Apply(update);
        if (((string?)update["sessionUpdate"] == "current_mode_update" && update["currentModeId"] is not null) ||
            ((string?)update["sessionUpdate"] == "config_option_update" && HasModeOption(update["configOptions"]))) ConfirmReminderMode();
    }

    JsonObject? commands; // under gate

    /// <summary>
    /// Follows the agent's <c>available_commands_update</c>. In ACP each one replaces the list
    /// before it, so it is the session's state, not its history: it is not journaled. The attached
    /// clients get a new list as it comes, and a client that loads or resumes the session gets the
    /// current one (<see cref="ReplayAndAttachAsync"/>, <see cref="ResumeAndAttachAsync"/>), as does
    /// the one that created it, once it has its answer (<see cref="AnsweredAsync"/>). A list
    /// the same as the current one is not sent again: Kimi sends it twice on every load and resume.
    /// </summary>
    public async Task PublishCommandsAsync(JsonObject update)
    {
        await gate.WaitAsync();
        try
        {
            if (Retired || JsonNode.DeepEquals(commands, update)) return;
            commands = update.DeepClone().AsObject();
            foreach (var client in Clients())
                if (!IsUnanswered(client))
                    await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(update.DeepClone()));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether the agent has sent a list of commands for the session (for tests).</summary>
    internal bool HasCommands => Volatile.Read(ref commands) is not null;

    readonly HashSet<ClientConnection> unanswered = []; // under clientsGate

    bool IsUnanswered(ClientConnection client)
    {
        lock (clientsGate) return unanswered.Contains(client);
    }

    /// <summary>
    /// Attaches the client that created the session with <c>session/new</c>, before its answer is
    /// written. A client knows the session only from that answer and drops its updates before it
    /// (Zed does), while the agent may send its list of commands right after its own answer (Kimi
    /// does), before the client has the session's ID. So the list is not sent to this client until
    /// <see cref="AnsweredAsync"/>.
    /// </summary>
    public void AttachUnanswered(ClientConnection client)
    {
        lock (clientsGate)
            if (!retired) unanswered.Add(client);
        Attach(client);
    }

    /// <summary>
    /// The answer to the client's <c>session/new</c> is written (<see cref="AttachUnanswered"/>):
    /// sends it the current list of commands, if the agent has sent one. A list that comes later
    /// reaches it live, so it gets each list once.
    /// </summary>
    public async Task AnsweredAsync(ClientConnection client)
    {
        await gate.WaitAsync();
        try
        {
            bool attached; // and still unanswered: a detach or a deletion meanwhile took it out
            lock (clientsGate) attached = unanswered.Remove(client);
            if (!attached || commands is null) return;
            await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(commands.DeepClone()));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Sets the current value of a part of the state, such as <c>modes.currentModeId</c> after <c>session/set_mode</c>.</summary>
    public void SetCurrent(string key, string field, JsonNode? value)
    {
        configuration.SetCurrent(key, field, value);
        if (key == "modes" && field == "currentModeId" && value is not null) ConfirmReminderMode();
    }

    /// <summary>A copy of the session state as the agent last reported it; empty if it reported none.</summary>
    public JsonObject State()
    {
        return configuration.Snapshot();
    }

    string? opinionTurn;
    System.Text.StringBuilder opinionText = new();

    /// <summary>The <c>message_id</c> of the opinion request whose turn is running, if any.</summary>
    public string? CurrentOpinion
    {
        get { lock (clientsGate) return opinionTurn; }
    }

    public void BeginOpinionTurn(string messageId)
    {
        lock (clientsGate) { opinionTurn = messageId; opinionText = new(); }
    }

    /// <summary>Ends the opinion turn and returns the agent's text from it.</summary>
    public string EndOpinionTurn()
    {
        lock (clientsGate)
        {
            opinionTurn = null;
            return opinionText.ToString();
        }
    }

    System.Text.StringBuilder? turnText;
    int turnTextLimit;
    bool turnTextStale; // a tool call came after the text kept so far
    bool turnTextHasText;

    /// <summary>
    /// Starts keeping the agent's final message of a turn whose end is reported to a parent: at
    /// most the first <paramref name="limit"/> characters of it.
    /// </summary>
    public void BeginTurnText(int limit)
    {
        lock (clientsGate) { turnText = new(); turnTextLimit = limit; turnTextStale = false; turnTextHasText = false; }
    }

    /// <summary>
    /// Stops keeping the turn's text and returns its final message: the agent's text after its
    /// last tool call, empty when no text followed that call.
    /// </summary>
    public string EndTurnText()
    {
        lock (clientsGate)
        {
            var text = turnTextStale ? "" : turnText?.ToString() ?? "";
            turnText = null;
            turnTextStale = false; turnTextHasText = false;
            return text;
        }
    }

    internal string? SnapshotTurnText()
    {
        lock (clientsGate) return turnText is null || turnTextStale || !turnTextHasText ? null : turnText.ToString();
    }

    /// <summary>
    /// Marks an update that belongs to a running opinion turn with <c>_meta["gorynych/opinion"]</c>,
    /// so a GUI can keep that exchange apart from the user's own turns, and keeps its text. In a
    /// turn begun with <see cref="BeginTurnText"/> it keeps the agent's latest message instead.
    /// </summary>
    public void TagOpinionTurn(JsonObject update)
    {
        var kind = (string?)update["sessionUpdate"];
        var text = kind == "agent_message_chunk" ? (string?)update["content"]?["text"] : null;
        lock (clientsGate)
        {
            if (opinionTurn is null)
            {
                if (turnText is null) return;
                if (kind == "tool_call") turnTextStale = true; // what the agent says next is a new message
                if (text is null) return;
                if (turnTextStale) { turnText.Clear(); turnTextStale = false; turnTextHasText = false; }
                turnTextHasText = true;
                var room = turnTextLimit - turnText.Length;
                if (room > 0) turnText.Append(text, 0, Math.Min(room, text.Length));
                return;
            }
            var meta = update["_meta"] as JsonObject ?? [];
            meta[Gorynych.Acp.Hosting.GorynychProtocol.MetaOpinion] = opinionTurn;
            update["_meta"] = meta;
            if (text is not null) opinionText.Append(text);
        }
    }

    public string SessionId { get; } = sessionId;
    public SessionJournal Journal { get; } = journal;
    public int PendingRequests => Volatile.Read(ref pendingRequests);

    public ClientConnection[] Clients()
    {
        lock (clientsGate) return clients.Where(c => !c.Rpc.IsClosed).ToArray();
    }

    /// <summary>Attaches a client; it becomes the first choice for the session's requests, and holds the session.</summary>
    public void Attach(ClientConnection client)
    {
        TaskCompletionSource signal;
        lock (clientsGate)
        {
            if (retired) return;
            clients.Remove(client);
            clients.Add(client);
            CancelDiscardAfterClose();
            signal = clientAttached;
            clientAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        client.Hold(SessionId);
        StopIdle();
        signal.TrySetResult();
    }

    /// <summary>Detaches for an explicit close and, if this was the last client, atomically requests its discard generation.</summary>
    public long DetachForClose(ClientConnection client)
    {
        lock (clientsGate)
        {
            clients.Remove(client);
            unanswered.Remove(client);
            return clients.Any(c => !c.Rpc.IsClosed) ? 0 : RequestDiscardAfterClose();
        }
    }

    /// <param name="notify">False for a close or a disconnect, which deal with the session themselves; otherwise
    /// <see cref="LastClientLeft"/> runs when this detach leaves no client attached.</param>
    /// <returns>True when the client was attached.</returns>
    public bool Detach(ClientConnection client, bool notify = true)
    {
        bool detached, none;
        lock (clientsGate)
        {
            detached = clients.Remove(client);
            unanswered.Remove(client);
            none = !clients.Any(c => !c.Rpc.IsClosed);
        }
        if (detached && none && notify) LastClientLeft?.Invoke();
        return detached;
    }

    /// <summary>
    /// Returns the most recently attached client, preferring one that <paramref name="prefer"/>s
    /// the request, waiting for one if none is attached.
    /// </summary>
    public async Task<ClientConnection> WaitForClientAsync(Func<ClientConnection, bool> prefer, CancellationToken ct)
    {
        Interlocked.Increment(ref pendingRequests);
        try
        {
            while (true)
            {
                Task wait;
                lock (clientsGate)
                {
                    if (retired) throw new JsonRpcException(JsonRpcException.ResourceNotFound, $"Session {SessionId} was deleted");
                    // A client that takes no requests (gorynych/requests: false) is as none.
                    var open = clients.Where(c => !c.Rpc.IsClosed && c.TakesRequests).ToList();
                    var live = open.LastOrDefault(prefer) ?? open.LastOrDefault();
                    if (live is not null) return live;
                    wait = clientAttached.Task;
                }
                await wait.WaitAsync(ct);
            }
        }
        finally
        {
            Interlocked.Decrement(ref pendingRequests);
        }
    }

    string? lastTitle;

    /// <summary>
    /// True for a <c>session_info_update</c> that only repeats the title already published
    /// (the service's first-prompt title and Kimi's own are the same text).
    /// </summary>
    public bool IsRepeatedTitle(JsonObject update)
    {
        if ((string?)update["sessionUpdate"] != "session_info_update" || (string?)update["title"] is not { } title) return false;
        if (update.Count > 2 && !(update.Count == 3 && update.ContainsKey("updatedAt"))) return false;
        lock (clientsGate)
        {
            if (title == lastTitle) return true;
            lastTitle = title;
            return false;
        }
    }

    /// <summary>A new ACP <c>messageId</c> for a message the service writes or names.</summary>
    public static string NewMessageId() => Guid.NewGuid().ToString();

    // The message that chunks without a messageId of their own continue (see StampMessageId).
    string? runKind;
    string? runId;

    /// <summary>
    /// Gives a message chunk that has no <c>messageId</c> (Kimi sends none) the ID of the message it
    /// belongs to, so clients and the journal see every chunk with one and replay shows the same IDs
    /// as the live stream: consecutive chunks of one kind without an ID are one message, as a client
    /// would infer without IDs, and any other part of a turn (a chunk with its own ID, a chunk of
    /// another kind, a tool call, a plan) ends it. Chunks with their own ID pass unchanged. Runs
    /// under <see cref="gate"/>, in publishing order.
    /// </summary>
    void StampMessageId(JsonObject update)
    {
        var kind = (string?)update["sessionUpdate"];
        if (!SessionJournal.IsTurnKind(kind)) return; // a title or usage update does not end a message
        if (SessionJournal.IsChunkKind(kind) && update["messageId"] is null)
        {
            if (runKind != kind) { runKind = kind; runId = NewMessageId(); }
            update["messageId"] = runId;
            return;
        }
        runKind = null;
    }

    readonly Dictionary<string, string> toolTitles = [];
    readonly Queue<string> toolTitleOrder = new();

    /// <summary>
    /// The title of a tool call published on this feed, for an approval that names the call by
    /// <c>toolCallId</c> alone, as ACP allows (docs/adapter-contract.md, §8). Kept for the latest 64 calls.
    /// </summary>
    public string? ToolTitle(string toolCallId)
    {
        lock (toolTitles) return toolTitles.GetValueOrDefault(toolCallId);
    }

    void RememberToolTitle(JsonObject update)
    {
        if ((string?)update["sessionUpdate"] is not ("tool_call" or "tool_call_update")
            || (string?)update["toolCallId"] is not { } id || (string?)update["title"] is not { Length: > 0 } title) return;
        lock (toolTitles)
        {
            if (!toolTitles.ContainsKey(id))
            {
                toolTitleOrder.Enqueue(id);
                if (toolTitleOrder.Count > 64) toolTitles.Remove(toolTitleOrder.Dequeue());
            }
            toolTitles[id] = title;
        }
    }

    /// <summary>
    /// Sends an update to the attached clients that <paramref name="to"/> picks, without journaling
    /// it: something live, not the session's history (a notice).
    /// </summary>
    public async Task SendLiveAsync(JsonObject update, Func<ClientConnection, bool> to)
    {
        await gate.WaitAsync();
        try
        {
            if (Retired) return;
            foreach (var client in Clients())
                if (to(client))
                    await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(update.DeepClone()));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Journals an update, runs <paramref name="remember"/>, and sends it to the attached clients.</summary>
    /// <param name="journal">False sends it without journaling it.</param>
    public async Task PublishAsync(JsonObject update, ClientConnection? except, Action remember, bool journal = true,
        JsonRpcConnection? stateSource = null, bool historical = false)
    {
        await gate.WaitAsync();
        try
        {
            if (Retired) return; // a late update of a deleted session must not bring its journal back
            if (stateSource?.IsClosed == true) return;
            if (!historical && stateSource is not null && SubagentRouting.Announcement(update) is { } child && update["state"] is JsonObject)
                subagentStateOwners[child] = (SubagentRouting.SessionOf(update) ?? SessionId, stateSource);
            if (SubagentRouting.SessionOf(update) is null)
            {
                StampMessageId(update);
                RememberToolTitle(update);
            }
            if (journal) Journal.Append(update);
            remember();
            foreach (var client in Clients())
                if (!ReferenceEquals(client, except))
                    await SendUpdateAsync(client, historical ? WithoutHistoricalState(update) : update);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Appends what the agent's own history gained (<see cref="HistoryCatchUp"/>) with one write,
    /// then sends it to the attached clients, as <see cref="PublishAsync"/> does one update.
    /// <paramref name="trimmed"/>: it is only the end of what the history gained, and the journal
    /// is marked so.
    /// </summary>
    public async Task AppendHistoryAsync(List<JsonObject> updates, bool trimmed = false)
    {
        await gate.WaitAsync();
        try
        {
            if (Retired) return;
            foreach (var update in updates)
            {
                if (SubagentRouting.SessionOf(update) is null)
                {
                    StampMessageId(update);
                    RememberToolTitle(update);
                }
            }
            Journal.AppendMany(updates);
            if (trimmed) Journal.MarkTrimmed();
            foreach (var client in Clients())
                foreach (var update in updates)
                    if (!await SendUpdateAsync(client, WithoutHistoricalState(update))) break;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Replaces the journal with the agent's history imported again (<see cref="SessionJournal.Replace"/>,
    /// with the new <paramref name="mark"/>) and sends the new entries to the attached clients,
    /// which saw the earlier ones.
    /// </summary>
    public async Task ReplaceHistoryAsync(List<JsonObject> entries, DateTimeOffset now, string? mark, bool trimmed)
    {
        await gate.WaitAsync();
        try
        {
            if (Retired) return;
            runKind = null;
            Journal.Replace(entries, now, mark, trimmed);
            foreach (var client in Clients())
                foreach (var entry in entries)
                    if (!await SendUpdateAsync(client, WithoutHistoricalState(entry))) break;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sends the journal to <paramref name="client"/>, then the current list of commands
    /// (<see cref="PublishCommandsAsync"/>), then attaches it, without losing updates in between.
    /// A journal written before the list was kept as state may hold a list (the last one, see
    /// <see cref="SessionJournal"/>): it is sent last, in place of the current one only when the
    /// agent has sent none since the service started.
    /// </summary>
    /// <returns>The position the client is at: what it gets next follows it.</returns>
    public async Task<long> ReplayAndAttachAsync(ClientConnection client, bool codex)
    {
        await gate.WaitAsync();
        try
        {
            var position = Journal.LastPosition;
            if (Retired) return position;
            JsonNode? journaled = null;
            foreach (var update in Journal.ReadAll())
            {
                if (SessionJournal.IsCommands(update) && !SubagentRouting.IsPreview(update)) { journaled = update; continue; }
                if (codex) RemoveLegacyCodexCommandLocation(update);
                if (!await SendUpdateAsync(client, update)) return position;
            }
            if ((commands?.DeepClone() ?? journaled) is { } list && !await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(list))) return position;
            Attach(client);
            return position;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sends <paramref name="client"/> what the journal has after <paramref name="after"/> (a client
    /// on the resumable link coming back; nothing for a plain <c>session/resume</c>, which replays no
    /// history), then the current list of commands, which the client needs all the same, then
    /// attaches it, without losing updates in between.
    /// </summary>
    /// <returns>The position the client is at: what it gets next follows it.</returns>
    public async Task<long> ResumeAndAttachAsync(ClientConnection client, long? after, bool codex)
    {
        await gate.WaitAsync();
        try
        {
            var position = Journal.LastPosition;
            if (Retired) return position;
            if (client.TakesSubagents)
                foreach (var update in Journal.ReadAll().Where(u => SubagentRouting.Announcement(u) is not null))
                {
                    var child = SubagentRouting.Announcement(update)!;
                    if (client.Subagents.RootOf(child) is not null) continue;
                    var association = (JsonObject)update.DeepClone();
                    association.Remove("state");
                    association.Remove("capabilities");
                    if (!await SendUpdateAsync(client, association)) return position;
                }
            if (after is { } from)
                foreach (var update in Journal.ReadAfter(from))
                {
                    if (codex) RemoveLegacyCodexCommandLocation(update);
                    if (!await SendUpdateAsync(client, update)) return position;
                }
            if (commands?.DeepClone() is { } list && !await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(list))) return position;
            Attach(client);
            return position;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Older Codex adapters used a command's working directory as its sole file location.
    /// Remove that shape from the parsed journal entry to keep clients from trying to open the directory.</summary>
    static void RemoveLegacyCodexCommandLocation(JsonNode update)
    {
        if (update is JsonObject call
            && call["sessionUpdate"] is JsonValue kind && kind.TryGetValue<string>(out var type) && type is "tool_call" or "tool_call_update"
            && call["rawInput"] is JsonObject input && input["command"] is JsonValue command && command.TryGetValue<string>(out _)
            && input["cwd"] is JsonValue directory && directory.TryGetValue<string>(out var cwd) && cwd is { Length: > 0 }
            && call["locations"] is JsonArray { Count: 1 } locations && locations[0] is JsonObject { Count: 1 } location
            && location["path"] is JsonValue path && path.TryGetValue<string>(out var file) && file == cwd)
            call.Remove("locations");
    }

    readonly Dictionary<long, (string? PromptId, bool Handed)> prompts = []; // under clientsGate: the client prompts in progress, by a number of the feed's own
    long promptCount;

    /// <summary>
    /// A client's prompt for the session is in progress, from its arrival to its answer, waiting for
    /// the turn before it included; <paramref name="promptId"/> is the ID its client gave it, if any
    /// (<c>gorynych/promptId</c>). Returns what <see cref="EndPrompt"/> takes.
    /// </summary>
    public long BeginPrompt(string? promptId)
    {
        lock (nativeActivityGate) { strictLoadedIdleRoute = null; strictIdleEligible = false; strictIdleFromEarly = false; pendingEarlyIdle = null; assignmentActivityFence++; evidencePrompts++; }
        long key;
        lock (clientsGate)
        {
            key = ++promptCount;
            prompts[key] = (promptId, false);
        }
        Activity.Begin();
        return key;
    }

    /// <summary>The prompt went to its agent: in a host, it runs on whatever happens to this service.</summary>
    public void Handed(long key)
    {
        lock (clientsGate)
            if (prompts.TryGetValue(key, out var prompt)) prompts[key] = (prompt.PromptId, true);
    }

    /// <summary>Whether the prompt reached its agent (<see cref="Handed"/>).</summary>
    public bool WasHanded(long key)
    {
        lock (clientsGate) return prompts.TryGetValue(key, out var prompt) && prompt.Handed;
    }

    public void EndPrompt(long key)
    {
        bool removed;
        lock (clientsGate) removed = prompts.Remove(key);
        if (removed) { lock (nativeActivityGate) evidencePrompts--; Activity.End(); }
    }

    /// <summary>The IDs of the client prompts in progress that reached their agent (<see cref="Handed"/>).</summary>
    public string[] HandedPromptIds()
    {
        lock (clientsGate) return prompts.Values.Where(p => p.Handed).Select(p => p.PromptId).OfType<string>().ToArray();
    }

    /// <summary>The IDs of the client prompts in progress that have one.</summary>
    public string[] PromptsInProgress()
    {
        lock (clientsGate) return prompts.Values.Select(p => p.PromptId).OfType<string>().ToArray();
    }

    /// <summary>Client prompts in progress that have not reached their agent yet: a restart with hosts waits only for them.</summary>
    public int PromptsOnTheirWay
    {
        get { lock (clientsGate) return prompts.Values.Count(p => !p.Handed); }
    }

    /// <summary>
    /// A turn this service did not start but found running in a host that came back to it: it holds
    /// <see cref="TurnGate"/> and counts as a client's prompt in progress until the host reports its end.
    /// </summary>
    /// <param name="HostTurn">The host's number for it, which its <c>_gorynych/turn_ended</c> carries.</param>
    /// <param name="HostId">The host it runs in.</param>
    public sealed record AdoptedTurn(long Key, string? PromptId, string Agent, long? HostTurn, string? HostId = null,
        string TurnKind = "unknown", string? ReportId = null);

    AdoptedTurn? adopted; // under clientsGate

    public AdoptedTurn? Adopted
    {
        get { lock (clientsGate) return adopted; }
    }

    /// <summary>Holds the session for a turn a host came back with; false if one is held already.</summary>
    public bool Adopt(AdoptedTurn turn)
    {
        lock (clientsGate)
        {
            if (adopted is not null) return false;
            adopted = turn;
            return true;
        }
    }

    /// <summary>Lets go of the adopted turn; null when there was none (or it was another one).</summary>
    public AdoptedTurn? EndAdopted(long? hostTurn = null, bool any = false)
    {
        lock (clientsGate)
        {
            if (adopted is null || (!any && (hostTurn is null || adopted.HostTurn != hostTurn))) return null;
            var turn = adopted;
            adopted = null;
            return turn;
        }
    }

    internal SemaphoreSlim EndMarkGate { get; } = new(1, 1);

    internal AdoptedTurn? EndAdoptedIf(AdoptedTurn expected)
    {
        lock (clientsGate)
        {
            if (!ReferenceEquals(adopted, expected)) return null;
            adopted = null;
            return expected;
        }
    }

    HashSet<string>? endMarks; // under clientsGate once read: the prompts whose end the journal has

    HashSet<string> EndMarks()
    {
        if (Volatile.Read(ref endMarks) is { } known) return known;
        var read = Journal.EndMarkedPromptIds(); // outside clientsGate: the journal has a lock of its own
        lock (clientsGate) return endMarks ??= read;
    }

    /// <summary>Whether the end of the prompt is marked in the journal.</summary>
    public bool HasEndMark(string promptId)
    {
        var marks = EndMarks();
        lock (clientsGate) return marks.Contains(promptId);
    }

    /// <summary>Claims the mark of the prompt's end: false when it is marked already, once per prompt.</summary>
    public bool ClaimEndMark(string promptId)
    {
        var marks = EndMarks();
        lock (clientsGate) return marks.Add(promptId);
    }

    // A failed durable journal append must remain retryable by inventory or replay.
    public void ReleaseEndMark(string promptId)
    {
        // The write may have reached disk despite throwing. Preserve its claim if the
        // reloaded journal proves it; otherwise allow inventory/replay to retry.
        bool durable;
        try { durable = Journal.EndMarkedPromptIds().Contains(promptId); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { durable = false; }
        if (durable) return;
        var marks = EndMarks();
        lock (clientsGate) marks.Remove(promptId);
    }

    readonly Dictionary<string, long> hostSeen = new(StringComparer.Ordinal); // under clientsGate: per host, the last of its updates taken
    string? lastTaken; // under clientsGate

    /// <summary>What the service that ran before took from a host for this session (<see cref="SessionRecord.HostSeen"/>).</summary>
    public string? KeptHostSeen { get; init; }

    /// <summary>The newest place taken from a host in this run, <c>"&lt;hostId&gt;/&lt;n&gt;"</c>, which the record keeps at exit.</summary>
    public string? LastTakenFromHost
    {
        get { lock (clientsGate) return lastTaken; }
    }

    /// <summary>
    /// The last place in a host's stream this session has (<c>gorynych/hostSeq</c>), from what it took
    /// in this run, else the newer of the journal's and what the service before kept: what the host
    /// sends again must follow it.
    /// </summary>
    public long HostCursor(string hostId)
    {
        lock (clientsGate)
        {
            if (hostSeen.TryGetValue(hostId, out var seen)) return seen;
            var cursor = Journal.LastHostSeq(hostId);
            if (KeptHostSeen is { } kept && SessionJournal.TryParseHostSeq(kept, out var keptHost, out var keptNumber) && keptHost == hostId)
                cursor = Math.Max(cursor, keptNumber);
            hostSeen[hostId] = cursor;
            return cursor;
        }
    }

    /// <summary>
    /// Takes an update or a report from a host once: false for one at or before the session's place
    /// in that host's stream, which the journal has already.
    /// </summary>
    public bool TakeFromHost(string hostSeq)
    {
        if (!SessionJournal.TryParseHostSeq(hostSeq, out var hostId, out var number)) return true;
        var cursor = HostCursor(hostId);
        lock (clientsGate)
        {
            if (number <= cursor) return false;
            hostSeen[hostId] = number;
            lastTaken = hostSeq;
            return true;
        }
    }

    /// <summary>
    /// True while a turn runs (a client's, a service turn, one of the agent's own) or a client's
    /// prompt waits for one: what a client resuming the session is told (<c>gorynych/turn</c>), and
    /// what a restart waits for.
    /// </summary>
    public bool TurnRunning
    {
        get
        {
            lock (clientsGate)
                if (prompts.Count > 0) return true;
            return TurnGate.CurrentCount == 0 || AgentTurnRunning;
        }
    }

    /// <summary>
    /// Sends to one client. A client that is gone, or stops reading for <see cref="SendTimeout"/>,
    /// is detached and disconnected, so it cannot stall the agent that produced the update.
    /// </summary>
    public async Task<bool> SendAsync(ClientConnection client, string method, JsonNode? @params)
    {
        try
        {
            await client.Rpc.SendNotificationAsync(method, @params).WaitAsync(SendTimeout);
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException)
        {
            Detach(client);
            if (e is TimeoutException)
            {
                ServiceLog.Warn($"{client.Rpc.Name}: not reading; disconnecting it");
                _ = client.Rpc.DisposeAsync().AsTask();
            }
            return false;
        }
    }

    /// <summary>
    /// What a client gets: the update, without its place in a host's stream (<c>gorynych/hostSeq</c>),
    /// which is for the journal. <paramref name="update"/> is a copy of the service's own.
    /// </summary>
    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static TaskCompletionSource Completed()
    {
        var done = NewSignal();
        done.TrySetResult();
        return done;
    }

    async Task<bool> SendUpdateAsync(ClientConnection client, JsonNode update)
    {
        if (SubagentRouting.IsPreview(update) && !client.TakesSubagents) return true;
        var target = SubagentRouting.SessionOf(update);
        if (target is not null && client.Subagents.RootOf(target) != SessionId) return true;
        var outgoing = update.DeepClone();
        if (!client.TakesSubagents && outgoing["content"] is JsonArray content)
            for (var index = content.Count - 1; index >= 0; index--)
                if ((string?)content[index]?["type"] == "session") content.RemoveAt(index);
        if (!await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(outgoing))) return false;
        if (SubagentRouting.Announcement(update) is { } child)
            client.Subagents.Announce(target ?? SessionId, child, SessionId);
        return true;
    }

    static JsonNode WithoutHistoricalState(JsonNode update)
    {
        if (SubagentRouting.Announcement(update) is null || update is not JsonObject obj || !obj.ContainsKey("state")) return update;
        var copy = (JsonObject)obj.DeepClone();
        copy.Remove("state");
        return copy;
    }

    /// <summary>Withdraw only snapshots last observed on this lost connection.</summary>
    public async Task SubagentHostLostAsync(JsonRpcConnection source)
    {
        await gate.WaitAsync();
        try
        {
            if (Retired) return;
            foreach (var (child, owner) in subagentStateOwners.Where(p => ReferenceEquals(p.Value.Source, source)).ToArray())
            {
                subagentStateOwners.Remove(child);
                var update = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = child,
                    ["state"] = new JsonObject { ["state"] = "unknown" } };
                if (owner.Parent != SessionId) SubagentRouting.SetSession(update, owner.Parent);
                foreach (var client in Clients()) await SendUpdateAsync(client, update);
            }
        }
        finally { gate.Release(); }
    }

    JsonObject Notification(JsonNode update)
    {
        var target = SubagentRouting.SessionOf(update) ?? SessionId;
        if (update["_meta"] is JsonObject meta)
        {
            var hasPrivate = meta.Remove(Gorynych.Acp.Hosting.GorynychProtocol.MetaHostSeq);
            hasPrivate |= meta.Remove(SubagentRouting.MetaSession);
            if (hasPrivate && meta.Count == 0) ((JsonObject)update).Remove("_meta");
        }
        return new() { ["sessionId"] = target, ["update"] = update };
    }
}

internal sealed record ClaudeObservationFence(JsonRpcConnection Route, string Host, string Prompt, long Epoch, long ActivityFence, string BackgroundStream, long BackgroundRevision);
