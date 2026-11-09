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

    /// <summary>Non-zero while the agent replays history the service already has.</summary>
    public int SuppressReplay;

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

    int agentTurn;

    /// <summary>
    /// Marks the start of a turn the agent runs that the service did not start; false when one was
    /// already marked, so a turn counts as one operation (<see cref="EnterUse"/>) however often
    /// its start is marked.
    /// </summary>
    public bool BeginAgentTurn() => Interlocked.Exchange(ref agentTurn, 1) == 0;

    /// <summary>Marks the end of the agent's own turn; false when none was marked.</summary>
    public bool EndAgentTurn() => Interlocked.Exchange(ref agentTurn, 0) == 1;

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

    /// <summary>Follows the agent's <c>config_option_update</c> and <c>current_mode_update</c>.</summary>
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

    /// <summary>Sends the journal to <paramref name="client"/>, then attaches it, without losing updates in between.</summary>
    public async Task ReplayAndAttachAsync(ClientConnection client)
    {
        await gate.WaitAsync();
        try
        {
            if (Retired) return;
            foreach (var update in Journal.ReadAll())
                if (!await SendAsync(client, AcpMethods.Client.SessionUpdate, Notification(update))) return;
            Attach(client);
        }
        finally
        {
            gate.Release();
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

    JsonObject Notification(JsonNode update) => new() { ["sessionId"] = SessionId, ["update"] = update };
}
