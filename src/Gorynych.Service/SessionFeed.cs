using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

/// <summary>
/// The live side of one session: its journal, the clients attached to it, and the gate that
/// keeps journal order, live delivery and replay consistent. A client that attaches during
/// a replay sees the whole history and then every later update exactly once.
/// </summary>
internal sealed class SessionFeed(string sessionId, SessionJournal journal)
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Lock clientsGate = new();
    readonly List<ClientConnection> clients = [];
    TaskCompletionSource clientAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int pendingRequests;

    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    Queue<JsonObject>? replay; // under clientsGate
    int replayLimit;
    bool replayDropped;

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
        if (!SessionJournal.IsTurnKind((string?)update["sessionUpdate"])) return false;
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
    public bool MarkOtherSessionText() => Interlocked.Exchange(ref otherText, 1) != 0;

    /// <summary>Takes back a mark whose text did not reach the agent after all (it has no live-note path), unless it was marked before.</summary>
    public void UnmarkOtherSessionText(bool markedBefore)
    {
        if (!markedBefore) Interlocked.Exchange(ref otherText, 0);
    }

    /// <summary>A turn ended: clears <see cref="HasOtherSessionText"/> once neither a client's turn nor one of the agent's own runs.</summary>
    public void TurnEnded()
    {
        if (!ClientTurn && Volatile.Read(ref agentTurn) == 0) Interlocked.Exchange(ref otherText, 0);
    }

    int agentTurn;

    /// <summary>
    /// Marks the start of a turn the agent runs that the service did not start; false when one was
    /// already marked, so a turn counts as one operation (<see cref="EnterUse"/>) however often
    /// its start is marked.
    /// </summary>
    public bool BeginAgentTurn() => Interlocked.Exchange(ref agentTurn, 1) == 0;

    /// <summary>Marks the end of the agent's own turn; false when none was marked.</summary>
    public bool EndAgentTurn()
    {
        if (Interlocked.Exchange(ref agentTurn, 0) != 1) return false;
        Quieted();
        return true;
    }

    /// <summary>A turn of the agent's own runs (<see cref="BeginAgentTurn"/>).</summary>
    public bool AgentTurnRunning => Volatile.Read(ref agentTurn) != 0;

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

    int openedReadOnly;

    /// <summary>
    /// Its agent opened it for reading only, and has not opened it in full since: also once it is
    /// writable again, the agent has not taken it yet (Codex takes the thread at its next resume),
    /// so a turn opens it again first, which also catches its journal up with what was made meanwhile.
    /// </summary>
    public bool OpenedReadOnly
    {
        get => Volatile.Read(ref openedReadOnly) != 0;
        set => Volatile.Write(ref openedReadOnly, value ? 1 : 0);
    }

    int asking;

    /// <summary>
    /// How many of the agent's requests (approvals, forms) wait for an answer: the session does not
    /// move meanwhile. One counts from the moment its connection read it (<see cref="Conductor.OnAgentRequestArrived"/>).
    /// </summary>
    public int Asking => Volatile.Read(ref asking);

    public void BeginAsking() => Interlocked.Increment(ref asking);

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

    public void Active(DateTimeOffset now) => Interlocked.Exchange(ref lastActivity, now.UtcTicks);

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

    readonly Lock stateGate = new();
    JsonObject state = [];

    /// <summary>The fields of the agent's session state, as <c>session/new</c>, <c>session/load</c> and <c>session/resume</c> return them.</summary>
    static readonly string[] StateKeys = ["configOptions", "modes", "models"];

    /// <summary>
    /// Takes the session state from an agent's response (<c>session/new</c>, <c>session/load</c>,
    /// <c>session/resume</c>, <c>session/set_config_option</c>): the fields it has replace ours.
    /// </summary>
    public void CaptureState(JsonNode? response)
    {
        if (response is not JsonObject o) return;
        lock (stateGate)
            foreach (var key in StateKeys)
                if (o[key] is { } value) state[key] = value.DeepClone();
    }

    /// <summary>
    /// Follows the agent's <c>config_option_update</c> and <c>current_mode_update</c>. The list of
    /// commands is state too, but goes through the feed's gate (<see cref="PublishCommandsAsync"/>).
    /// </summary>
    public void ApplyStateUpdate(JsonObject update)
    {
        switch ((string?)update["sessionUpdate"])
        {
            case "config_option_update":
                CaptureState(new JsonObject { ["configOptions"] = update["configOptions"]?.DeepClone() });
                break;
            case "current_mode_update":
                SetCurrent("modes", "currentModeId", update["currentModeId"]);
                break;
        }
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
        if (value is null) return;
        lock (stateGate)
            if (state[key] is JsonObject part) part[field] = value.DeepClone();
    }

    /// <summary>A copy of the session state as the agent last reported it; empty if it reported none.</summary>
    public JsonObject State()
    {
        lock (stateGate) return state.DeepClone().AsObject();
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

    /// <summary>
    /// Starts keeping the agent's final message of a turn whose end is reported to a parent: at
    /// most the first <paramref name="limit"/> characters of it.
    /// </summary>
    public void BeginTurnText(int limit)
    {
        lock (clientsGate) { turnText = new(); turnTextLimit = limit; turnTextStale = false; }
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
            turnTextStale = false;
            return text;
        }
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
                if (turnTextStale) { turnText.Clear(); turnTextStale = false; }
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
            signal = clientAttached;
            clientAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        client.Hold(SessionId);
        StopIdle();
        signal.TrySetResult();
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
                    var open = clients.Where(c => !c.Rpc.IsClosed).ToList();
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
    public async Task PublishAsync(JsonObject update, ClientConnection? except, Action remember, bool journal = true)
    {
        await gate.WaitAsync();
        try
        {
            if (Retired) return; // a late update of a deleted session must not bring its journal back
            StampMessageId(update);
            RememberToolTitle(update);
            if (journal) Journal.Append(update);
            remember();
            foreach (var client in Clients())
                if (!ReferenceEquals(client, except))
                    await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(update.DeepClone()));
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
                StampMessageId(update);
                RememberToolTitle(update);
            }
            Journal.AppendMany(updates);
            if (trimmed) Journal.MarkTrimmed();
            foreach (var client in Clients())
                foreach (var update in updates)
                    if (!await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(update.DeepClone()))) break;
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
                    if (!await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(entry.DeepClone()))) break;
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
    public async Task<long> ReplayAndAttachAsync(ClientConnection client)
    {
        await gate.WaitAsync();
        try
        {
            var position = Journal.LastPosition;
            if (Retired) return position;
            JsonNode? journaled = null;
            foreach (var update in Journal.ReadAll())
            {
                if (SessionJournal.IsCommands(update)) { journaled = update; continue; }
                if (!await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(update))) return position;
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
    public async Task<long> ResumeAndAttachAsync(ClientConnection client, long? after = null)
    {
        await gate.WaitAsync();
        try
        {
            var position = Journal.LastPosition;
            if (Retired) return position;
            if (after is { } from)
                foreach (var update in Journal.ReadAfter(from))
                    if (!await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(update))) return position;
            if (commands?.DeepClone() is { } list && !await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(list))) return position;
            Attach(client);
            return position;
        }
        finally
        {
            gate.Release();
        }
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
        lock (clientsGate)
        {
            var key = ++promptCount;
            prompts[key] = (promptId, false);
            return key;
        }
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
        lock (clientsGate) prompts.Remove(key);
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
    public sealed record AdoptedTurn(long Key, string? PromptId, string Agent, long? HostTurn, string? HostId = null);

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
            return TurnGate.CurrentCount == 0 || Volatile.Read(ref agentTurn) != 0;
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
    JsonObject Notification(JsonNode update)
    {
        if (update["_meta"] is JsonObject meta && meta.Remove(Gorynych.Acp.Hosting.GorynychProtocol.MetaHostSeq) && meta.Count == 0)
            ((JsonObject)update).Remove("_meta");
        return new() { ["sessionId"] = SessionId, ["update"] = update };
    }
}
