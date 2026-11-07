using System.Text.Json.Nodes;
using Gorynych.Acp;

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

    /// <summary>
    /// Marks an update that belongs to a running opinion turn with <c>_meta["gorynych/opinion"]</c>,
    /// so a GUI can keep that exchange apart from the user's own turns, and keeps its text.
    /// </summary>
    public void TagOpinionTurn(JsonObject update)
    {
        lock (clientsGate)
        {
            if (opinionTurn is null) return;
            var meta = update["_meta"] as JsonObject ?? [];
            meta[Gorynych.Acp.Hosting.GorynychProtocol.MetaOpinion] = opinionTurn;
            update["_meta"] = meta;
            if ((string?)update["sessionUpdate"] == "agent_message_chunk" && (string?)update["content"]?["text"] is { } text)
                opinionText.Append(text);
        }
    }

    public string SessionId { get; } = sessionId;
    public SessionJournal Journal { get; } = journal;
    public int PendingRequests => Volatile.Read(ref pendingRequests);

    public ClientConnection[] Clients()
    {
        lock (clientsGate) return clients.Where(c => !c.Rpc.IsClosed).ToArray();
    }

    /// <summary>Attaches a client; it becomes the first choice for the session's requests.</summary>
    public void Attach(ClientConnection client)
    {
        TaskCompletionSource signal;
        lock (clientsGate)
        {
            clients.Remove(client);
            clients.Add(client);
            signal = clientAttached;
            clientAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }

    public void Detach(ClientConnection client)
    {
        lock (clientsGate) clients.Remove(client);
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

    /// <summary>Journals an update, runs <paramref name="remember"/>, and sends it to the attached clients.</summary>
    public async Task PublishAsync(JsonObject update, ClientConnection? except, Action remember)
    {
        await gate.WaitAsync();
        try
        {
            Journal.Append(update);
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
