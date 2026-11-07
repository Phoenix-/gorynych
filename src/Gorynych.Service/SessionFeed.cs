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

    /// <summary>Non-zero while the agent replays history the service already has.</summary>
    public int SuppressReplay;

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

    /// <summary>Returns the most recently attached client, waiting for one if none is attached.</summary>
    public async Task<ClientConnection> WaitForClientAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref pendingRequests);
        try
        {
            while (true)
            {
                Task wait;
                lock (clientsGate)
                {
                    var live = clients.LastOrDefault(c => !c.Rpc.IsClosed);
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
            {
                if (ReferenceEquals(client, except)) continue;
                try { await client.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, Notification(update.DeepClone())); }
                catch (IOException) { Detach(client); }
            }
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
                await client.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, Notification(update));
            Attach(client);
        }
        finally
        {
            gate.Release();
        }
    }

    JsonObject Notification(JsonNode update) => new() { ["sessionId"] = SessionId, ["update"] = update };
}
