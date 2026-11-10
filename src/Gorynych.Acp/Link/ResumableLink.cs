using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Acp.Link;

/// <summary>Options of a <see cref="ResumableLink"/>.</summary>
public sealed class ResumableLinkOptions
{
    /// <summary>What the link tells about its connection: lost, back, given up.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Keys added to the client's <c>initialize</c> <c>_meta</c> unless it set them (the bridge's agent scope).</summary>
    public JsonObject? InitializeMeta { get; init; }

    /// <summary>The pauses before the attempts to connect again; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    /// <summary>How long after losing the connection the link stops trying, fails what waits, and ends.</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long one request of the handshake (<c>initialize</c>, a <c>session/resume</c>) may take.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The resumable link (docs/service-protocol.md, "The resumable link"): the client side of an ACP
/// connection to an agent that may go away and come back, such as the service across a restart,
/// presented to its own client as one connection that never breaks. It passes ACP messages through
/// line by line and in order, and keeps what it needs to resume:
/// <list type="bullet">
/// <item>the client's <c>initialize</c>, sent again to the agent that comes back, whose answer the client never sees;</item>
/// <item>the sessions the client has open (<c>session/new</c>, <c>load</c>, <c>resume</c>, <c>close</c>) and the
/// position of the last update of each (<c>gorynych/position</c>), resumed after it
/// (<c>session/resume</c> with <c>gorynych/replayFrom</c>);</item>
/// <item>the requests on their way: the ones that may run twice go again, a <c>session/prompt</c> stays open
/// until its turn ends, a load or resume goes again from where its replay got to, and anything else ends
/// with an error that says what happened;</item>
/// <item>the agent's questions (approvals, forms): one the agent that comes back asks again is joined to the
/// one the client has, which so appears once, and an answer the client gave meanwhile goes to it.</item>
/// </list>
/// Its own IDs go to each side, so the IDs of the connections before never meet those of the next one.
/// It resumes only an agent that advertises <c>agentCapabilities._meta["gorynych/link"]</c>; with any other,
/// a lost connection ends it, as a plain pipe would. The stdio bridge uses it toward the service; the hub
/// and the adapter hosts are to use it too (docs/design.md, "Updating without losing work").
/// </summary>
public sealed class ResumableLink
{
    static readonly JsonSerializerOptions Wire = AcpJson.TextOptions;

    readonly Stream clientInput;
    readonly Stream clientOutput;
    readonly Func<CancellationToken, Task<(Stream Input, Stream Output)>> connect;
    readonly ResumableLinkOptions options;
    readonly string tag = GorynychProtocol.NewRunTag();

    readonly Lock state = new();
    readonly SemaphoreSlim sendGate = new(1, 1); // writes to the agent in the client's order, and the switch to the next connection
    readonly SemaphoreSlim clientGate = new(1, 1); // writes to the client
    readonly CancellationTokenSource stopping = new();
    readonly TaskCompletionSource<int> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // under sendGate
    Connection? current;
    readonly List<(JsonObject Message, Upstream? Request)> outbox = [];

    // under state
    int generation;
    bool resumable;
    bool restarting;
    JsonObject? initialize;
    readonly Dictionary<long, Upstream> upstream = [];
    readonly List<Upstream> again = [];
    readonly Dictionary<string, JsonObject> live = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> cursors = new(StringComparer.Ordinal);
    readonly HashSet<string> promptIds = new(StringComparer.Ordinal);
    readonly Dictionary<long, Downstream> downstream = [];
    long nextUpstream;
    long nextDownstream;
    long nextInternal;

    /// <param name="clientInput">What the client sends (the bridge's stdin).</param>
    /// <param name="clientOutput">Where the client reads (the bridge's stdout).</param>
    /// <param name="connect">Connects to the agent again, starting it if need be; throws when it cannot.</param>
    public ResumableLink(Stream clientInput, Stream clientOutput, Func<CancellationToken, Task<(Stream Input, Stream Output)>> connect, ResumableLinkOptions? options = null)
    {
        this.clientInput = clientInput;
        this.clientOutput = clientOutput;
        this.connect = connect;
        this.options = options ?? new ResumableLinkOptions();
    }

    /// <summary>
    /// Runs the link over its first connection until the client closes its side (0), the agent closes
    /// a connection it does not resume (0), or the link gives up on it (1).
    /// </summary>
    public async Task<int> RunAsync((Stream Input, Stream Output) first)
    {
        var connection = Create(first);
        await sendGate.WaitAsync();
        current = connection;
        sendGate.Release();
        Start(connection);
        var reading = Task.Run(ClientLoopAsync);
        var code = await ended.Task;
        stopping.Cancel();
        Connection? last;
        await sendGate.WaitAsync();
        last = current;
        sendGate.Release();
        if (last is not null) Drop(last);
        _ = reading; // it ends with the client's stream
        return code;
    }

    // ---------------------------------------------------------------- the client's side

    async Task ClientLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(clientInput, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
            while (await reader.ReadLineAsync(stopping.Token) is { } line)
            {
                if (line.Length == 0) continue;
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { message = null; }
                if (message is null)
                {
                    Log($"the client sent something that is not a JSON-RPC message: {Cut(line)}");
                    continue;
                }
                await FromClientAsync(message);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { }
        ended.TrySetResult(0);
    }

    async Task FromClientAsync(JsonObject message)
    {
        var method = Text(message["method"]);
        var id = message["id"];
        if (method is null)
        {
            if (id is not null) await ClientAnsweredAsync(id, message);
            return;
        }
        if (id is null)
        {
            await ClientNotificationAsync(method, message);
            return;
        }
        var @params = message["params"] as JsonObject;
        if (method == AcpMethods.Agent.Initialize)
        {
            // The agent learns that this client may come back for its sessions.
            @params = AddMeta(AddMeta(@params, options.InitializeMeta), new JsonObject { [GorynychProtocol.CapLink] = GorynychProtocol.LinkVersion });
            message["params"] = @params;
            lock (state) initialize = (JsonObject)@params.DeepClone();
        }
        var request = new Upstream
        {
            Id = Interlocked.Increment(ref nextUpstream), ClientId = id.DeepClone(), Method = method, SessionId = Text(@params?["sessionId"]),
        };
        bool tagPrompts;
        lock (state) tagPrompts = resumable;
        if (method == AcpMethods.Agent.SessionPrompt && tagPrompts && @params is not null)
        {
            // Its ID tells after a reconnect whether it is still in progress, and marks its end.
            request.PromptId = $"{tag}-{request.Id}";
            var meta = @params["_meta"] as JsonObject ?? [];
            meta[GorynychProtocol.MetaPromptId] = request.PromptId;
            @params["_meta"] = meta;
            lock (state) promptIds.Add(request.PromptId);
        }
        if (method is AcpMethods.Agent.SessionClose or AcpMethods.Agent.SessionDelete && request.SessionId is { } closed)
            lock (state)
            {
                // Not resumed after a reconnect from now on, whatever the answer.
                live.Remove(closed);
                cursors.Remove(closed);
            }
        message["id"] = request.Id;
        request.Message = message;
        lock (state) upstream[request.Id] = request;
        await SendAsync(message, request);
    }

    async Task ClientNotificationAsync(string method, JsonObject message)
    {
        var @params = message["params"] as JsonObject;
        if (method == AcpMethods.Protocol.CancelRequest)
        {
            await ClientCancelsAsync(@params?["requestId"]);
            return;
        }
        if (method == AcpMethods.Agent.SessionCancel && Text(@params?["sessionId"]) is { } sessionId)
        {
            // A prompt still waiting to go out never started: it ends here. One in flight when the
            // connection went is cancelled in the agent that comes back, if it runs there.
            var dropped = new List<Upstream>();
            await sendGate.WaitAsync();
            try
            {
                outbox.RemoveAll(q =>
                {
                    if (q.Request is not { Method: AcpMethods.Agent.SessionPrompt } r || r.SessionId != sessionId) return false;
                    dropped.Add(r);
                    return true;
                });
            }
            finally
            {
                sendGate.Release();
            }
            lock (state)
            {
                foreach (var r in dropped) upstream.Remove(r.Id);
                foreach (var r in upstream.Values)
                    if (r.Held && r.SessionId == sessionId) r.CancelAsked = true;
                foreach (var r in again.Where(r => r.Method == AcpMethods.Agent.SessionPrompt && r.SessionId == sessionId).ToList())
                {
                    again.Remove(r);
                    upstream.Remove(r.Id);
                    dropped.Add(r);
                }
            }
            foreach (var r in dropped) await AnswerAsync(r, Result(StopReason.Cancelled));
            // The running turn: at once, ahead of anything waiting.
            if (await CurrentAsync() is { } now) await TryWriteAsync(now, message);
            return;
        }
        await SendAsync(message, null);
    }

    async Task ClientCancelsAsync(JsonNode? clientId)
    {
        if (clientId is null) return;
        Upstream? request;
        lock (state) request = upstream.Values.FirstOrDefault(r => JsonNode.DeepEquals(r.ClientId, clientId));
        if (request is null) return;
        var waiting = false;
        await sendGate.WaitAsync();
        try
        {
            waiting = outbox.RemoveAll(q => q.Request == request) > 0;
        }
        finally
        {
            sendGate.Release();
        }
        lock (state)
            if (again.Remove(request)) waiting = true;
        if (waiting)
        {
            lock (state) upstream.Remove(request.Id);
            await AnswerAsync(request, Error(JsonRpcException.RequestCancelled, "Request cancelled"));
            return;
        }
        if (await CurrentAsync() is { } connection && request.Generation == connection.Generation)
            await TryWriteAsync(connection, Notification(AcpMethods.Protocol.CancelRequest, new JsonObject { ["requestId"] = request.Id }));
    }

    /// <summary>The client answered a request of the agent's: to the connection that asks it now, or kept for the next one.</summary>
    async Task ClientAnsweredAsync(JsonNode id, JsonObject message)
    {
        if (!TryId(id, out var key)) return;
        Downstream? question;
        Connection target;
        JsonNode serviceId;
        var answer = new JsonObject();
        if (message["result"] is { } result) answer["result"] = result.DeepClone();
        if (message["error"] is { } error) answer["error"] = error.DeepClone();
        lock (state)
        {
            if (!downstream.TryGetValue(key, out question)) return; // withdrawn
            question.Answer = answer;
            target = question.Connection;
            serviceId = question.ServiceId;
        }
        if (await TryWriteAsync(target, Response(serviceId, answer)))
            lock (state) downstream.Remove(key);
        // Otherwise its connection is gone: the agent that comes back asks it again (ServiceRequestAsync).
    }

    /// <summary>Sends a client's message to the agent in order, or keeps it for the next connection.</summary>
    async Task SendAsync(JsonObject message, Upstream? request)
    {
        await sendGate.WaitAsync();
        try
        {
            bool hold;
            lock (state) hold = restarting && request?.Method == AcpMethods.Agent.SessionPrompt;
            if (current is { IsLost: false } connection && outbox.Count == 0 && !hold)
            {
                if (request is not null) lock (state) request.Generation = connection.Generation;
                if (await TryWriteAsync(connection, message)) return;
                if (request is not null) lock (state) request.Generation = 0;
            }
            outbox.Add((message, request));
        }
        finally
        {
            sendGate.Release();
        }
    }

    async Task<Connection?> CurrentAsync()
    {
        await sendGate.WaitAsync();
        try { return current is { IsLost: false } c ? c : null; }
        finally { sendGate.Release(); }
    }

    // ---------------------------------------------------------------- the agent's side

    Connection Create((Stream Input, Stream Output) streams)
    {
        lock (state) return new Connection(++generation, streams.Input, streams.Output);
    }

    void Start(Connection connection) => _ = Task.Run(() => ServiceLoopAsync(connection));

    async Task ServiceLoopAsync(Connection connection)
    {
        try
        {
            using var reader = new StreamReader(connection.Input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
            while (await reader.ReadLineAsync(connection.Lost.Token) is { } line)
            {
                if (line.Length == 0) continue;
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { message = null; }
                if (message is null) continue;
                var method = Text(message["method"]);
                var id = message["id"];
                if (method is null) await ServiceAnsweredAsync(connection, id, message);
                else if (id is null) await ServiceNotificationAsync(connection, method, message);
                else await ServiceRequestAsync(connection, id, method, message);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { }
        Fail(connection);
    }

    async Task ServiceAnsweredAsync(Connection connection, JsonNode? id, JsonObject message)
    {
        if (Text(id) is { } own && connection.Internal.TryRemove(own, out var waiting))
        {
            waiting.TrySetResult(message);
            return;
        }
        if (!TryId(id, out var key)) return;
        Upstream? request;
        lock (state)
        {
            if (!upstream.TryGetValue(key, out request) || request.Generation != connection.Generation) return;
            if (IsRestarting(message))
            {
                // Not carried out: the next agent gets it as it is.
                restarting = true;
                request.Generation = 0;
                again.Add(request);
                return;
            }
            upstream.Remove(key);
        }
        Track(request, message);
        message["id"] = request.ClientId.DeepClone();
        await ToClientAsync(message);
    }

    /// <summary>Follows the client's sessions and the agent's capability from the answers it gets.</summary>
    void Track(Upstream request, JsonObject response)
    {
        if (response["result"] is not JsonObject result) return;
        lock (state)
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize:
                    resumable = result["agentCapabilities"]?["_meta"]?[GorynychProtocol.CapLink] is JsonValue v && v.TryGetValue<int>(out var version) && version >= 1;
                    break;
                case AcpMethods.Agent.SessionNew when Text(result["sessionId"]) is { } created:
                    Opened(created, request, result);
                    break;
                case AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionResume when request.SessionId is { } opened:
                    Opened(opened, request, result);
                    break;
            }
    }

    /// <summary>A session the client has open: kept with the params to resume it with, and its position. Under <see cref="state"/>.</summary>
    void Opened(string sessionId, Upstream request, JsonObject result)
    {
        var open = request.Message["params"]?.DeepClone() as JsonObject ?? [];
        open.Remove("_meta");
        open.Remove("sessionId");
        live[sessionId] = open;
        if (Text(result["_meta"]?[GorynychProtocol.MetaPosition]) is { } position) cursors[sessionId] = position;
    }

    async Task ServiceNotificationAsync(Connection connection, string method, JsonObject message)
    {
        if (method == GorynychProtocol.MethodRestarting)
        {
            lock (state) restarting = true;
            Log("the service is restarting; new prompts wait for the next one");
            return;
        }
        if (method == AcpMethods.Protocol.CancelRequest)
        {
            // The agent withdraws a question it asked: withdrawn from the client too.
            var serviceId = message["params"]?["requestId"];
            Downstream? question;
            lock (state)
            {
                question = downstream.Values.FirstOrDefault(q => q.Connection == connection && JsonNode.DeepEquals(q.ServiceId, serviceId));
                if (question is not null) downstream.Remove(question.ClientFacingId);
            }
            if (question is not null)
                await ToClientAsync(Notification(AcpMethods.Protocol.CancelRequest, new JsonObject { ["requestId"] = question.ClientFacingId }));
            return;
        }
        if (method == AcpMethods.Client.SessionUpdate && message["params"] is JsonObject p && Text(p["sessionId"]) is { } sessionId && p["update"] is JsonObject update)
        {
            var meta = update["_meta"] as JsonObject;
            if (Text(meta?[GorynychProtocol.MetaPosition]) is { } position) lock (state) cursors[sessionId] = position;
            if (Text(meta?[GorynychProtocol.MetaPromptId]) is { } promptId)
            {
                bool mine;
                lock (state) mine = promptIds.Contains(promptId);
                // The end of a prompt's turn whose client was gone: the link's own business.
                if (Text(meta![GorynychProtocol.MetaAgentTurn]) == "ended")
                {
                    if (mine) await EndHeldPromptAsync(promptId, meta);
                    return;
                }
                // The text of the client's own prompt, which it never gets live: a resume may send it from the journal.
                if (mine && Text(update["sessionUpdate"]) == "user_message_chunk") return;
            }
        }
        await ToClientAsync(message);
    }

    async Task ServiceRequestAsync(Connection connection, JsonNode id, string method, JsonObject message)
    {
        var @params = message["params"] as JsonObject;
        var key = QuestionKey(method, @params);
        Downstream? asked = null;
        long clientFacing = 0;
        lock (state)
        {
            if (key is not null) asked = downstream.Values.FirstOrDefault(q => q.Key == key && q.Connection.IsLost);
            if (asked is not null)
            {
                asked.Connection = connection;
                asked.ServiceId = id.DeepClone();
            }
            else
            {
                clientFacing = ++nextDownstream;
                downstream[clientFacing] = new Downstream
                {
                    ClientFacingId = clientFacing, Key = key, SessionId = Text(@params?["sessionId"]), Connection = connection, ServiceId = id.DeepClone(),
                };
            }
        }
        if (asked is not null)
        {
            // Asked again by the agent that came back: the client has it already, or answered it meanwhile.
            if (asked.Answer is { } answer && await TryWriteAsync(connection, Response(id, answer)))
                lock (state) downstream.Remove(asked.ClientFacingId);
            return;
        }
        message["id"] = clientFacing;
        await ToClientAsync(message);
    }

    /// <summary>What makes two requests of the agent's the same question: an approval or a form for one tool call.</summary>
    static string? QuestionKey(string method, JsonObject? @params)
    {
        var sessionId = Text(@params?["sessionId"]);
        if (sessionId is null) return null;
        var toolCall = method switch
        {
            AcpMethods.Client.SessionRequestPermission => Text(@params!["toolCall"]?["toolCallId"]),
            AcpMethods.Client.ElicitationCreate => Text(@params!["toolCallId"]) ?? Text(@params["message"]),
            _ => null,
        };
        return toolCall is null ? null : $"{method}\n{sessionId}\n{toolCall}";
    }

    /// <summary>The connection is gone, or a write to it failed; once per connection.</summary>
    void Fail(Connection connection)
    {
        if (Interlocked.Exchange(ref connection.Failed, 1) != 0) return;
        connection.Lost.Cancel();
        foreach (var (_, waiting) in connection.Internal) waiting.TrySetException(new IOException("the connection to the service was lost"));
        _ = Task.Run(() => LostAsync(connection));
    }

    async Task LostAsync(Connection connection)
    {
        Drop(connection);
        if (ended.Task.IsCompleted) return;
        await sendGate.WaitAsync();
        try
        {
            if (current != connection) return; // one that never got through its handshake
            current = null;
        }
        finally
        {
            sendGate.Release();
        }
        bool resumes;
        lock (state) resumes = resumable;
        if (!resumes)
        {
            ended.TrySetResult(0); // as a plain pipe: the client sees its agent go
            return;
        }
        var failed = new List<Upstream>();
        lock (state)
            foreach (var request in upstream.Values.Where(r => r.Generation == connection.Generation).ToList())
                switch (PolicyOf(request.Method))
                {
                    case Policy.Hold:
                        request.Held = true;
                        break;
                    case Policy.Reopen or Policy.Again:
                        request.Generation = 0;
                        again.Add(request);
                        break;
                    default:
                        upstream.Remove(request.Id);
                        failed.Add(request);
                        break;
                }
        foreach (var request in failed)
            await AnswerAsync(request, Error(JsonRpcException.InternalError,
                $"The connection to the Gorynych service was lost while {request.Method} was on its way, so it may or may not have been carried out."));
        Log("lost the connection to the service; connecting again");
        _ = Task.Run(ReconnectAsync);
    }

    enum Policy { Fail, Again, Reopen, Hold }

    /// <summary>
    /// What happens to a client's request that was on its way when the connection went: a prompt stays
    /// open (<see cref="Policy.Hold"/>); a load or resume goes again from where its replay got to; one
    /// that may run twice goes again; any other ends with an error.
    /// </summary>
    static Policy PolicyOf(string method) => method switch
    {
        AcpMethods.Agent.SessionPrompt => Policy.Hold,
        AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionResume => Policy.Reopen,
        AcpMethods.Agent.Initialize or AcpMethods.Agent.Authenticate or AcpMethods.Agent.Logout or AcpMethods.Agent.SessionList
            or AcpMethods.Agent.SessionClose or AcpMethods.Agent.SessionDelete or AcpMethods.Agent.SessionSetMode
            or AcpMethods.Agent.SessionSetConfigOption or "session/set_model" or GorynychProtocol.MethodStatus
            or GorynychProtocol.MethodRead or GorynychProtocol.MethodPeers or GorynychProtocol.MethodArchive => Policy.Again,
        _ => Policy.Fail,
    };

    async Task ReconnectAsync()
    {
        var since = Stopwatch.StartNew();
        string reason = "no attempt yet";
        for (var attempt = 0; ; attempt++)
        {
            var delays = options.RetryDelays;
            var delay = delays.Count == 0 ? TimeSpan.Zero : delays[Math.Min(attempt, delays.Count - 1)];
            try { await Task.Delay(delay, stopping.Token); }
            catch (OperationCanceledException) { return; }
            Connection? connection = null;
            try
            {
                connection = Create(await connect(stopping.Token));
                Start(connection);
                var resumed = await HandshakeAsync(connection);
                await FlushAsync(connection);
                Log($"connected again; {resumed} session{(resumed == 1 ? "" : "s")} resumed");
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (NotResumableException e)
            {
                if (connection is not null) Drop(connection);
                await GiveUpAsync(e.Message);
                return;
            }
            catch (Exception e)
            {
                reason = e.Message;
                if (connection is not null) Fail(connection);
            }
            if (since.Elapsed > options.GiveUpAfter)
            {
                await GiveUpAsync($"it did not come back within {options.GiveUpAfter.TotalSeconds:0} s: {reason}");
                return;
            }
        }
    }

    sealed class NotResumableException(string message) : Exception(message);

    /// <summary>Introduces the link to the agent that came back as its client did, and resumes the client's sessions there.</summary>
    async Task<int> HandshakeAsync(Connection connection)
    {
        JsonObject init;
        lock (state) init = (JsonObject)(initialize ?? []).DeepClone();
        var answer = await RequestAsync(connection, AcpMethods.Agent.Initialize, init);
        if (!(answer["agentCapabilities"]?["_meta"]?[GorynychProtocol.CapLink] is JsonValue v && v.TryGetValue<int>(out var version) && version >= 1))
            throw new NotResumableException("the service that came back does not resume sessions (an older version)");
        List<(string Id, JsonObject Open)> sessions;
        lock (state) sessions = live.Select(s => (s.Key, (JsonObject)s.Value.DeepClone())).ToList();
        await Task.WhenAll(sessions.Select(s => ResumeAsync(connection, s.Id, s.Open)));
        // A prompt in flight for a session the client did not have open runs nowhere now.
        List<Upstream> orphans;
        lock (state)
        {
            orphans = upstream.Values.Where(r => r.Held && (r.SessionId is null || !live.ContainsKey(r.SessionId))).ToList();
            foreach (var r in orphans) upstream.Remove(r.Id);
        }
        foreach (var r in orphans) await AnswerAsync(r, Result(StopReason.Cancelled));
        return sessions.Count;
    }

    async Task ResumeAsync(Connection connection, string sessionId, JsonObject open)
    {
        var @params = open;
        @params["sessionId"] = sessionId;
        @params["_meta"] = new JsonObject { [GorynychProtocol.MetaReplayFrom] = ReplayFrom(sessionId) };
        JsonObject result;
        try
        {
            result = await RequestAsync(connection, AcpMethods.Agent.SessionResume, @params);
        }
        catch (JsonRpcException e)
        {
            // Gone meanwhile (deleted, or its agent cannot take it): its prompts end with what the agent said.
            Log($"session {sessionId} could not be resumed: {e.Message}");
            List<Upstream> ending;
            lock (state)
            {
                live.Remove(sessionId);
                ending = upstream.Values.Where(r => r.Held && r.SessionId == sessionId).ToList();
                foreach (var r in ending) upstream.Remove(r.Id);
            }
            foreach (var r in ending) await AnswerAsync(r, Error(e.Code, e.Message));
            await WithdrawStaleQuestionsAsync(sessionId);
            return;
        }
        var meta = result["_meta"] as JsonObject;
        var inProgress = (meta?[GorynychProtocol.MetaPrompts] as JsonArray ?? []).Select(Text).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var idle = Text(meta?[GorynychProtocol.MetaTurn]) != "running";
        List<Upstream> over, cancel;
        lock (state)
        {
            if (Text(meta?[GorynychProtocol.MetaPosition]) is { } position) cursors[sessionId] = position;
            var held = upstream.Values.Where(r => r.Held && r.SessionId == sessionId).ToList();
            // Not in progress there: it never got there, or it went with the agent that ran it.
            over = held.Where(r => r.PromptId is null || !inProgress.Contains(r.PromptId)).ToList();
            cancel = held.Except(over).Where(r => r.CancelAsked).ToList();
            foreach (var r in over) upstream.Remove(r.Id);
        }
        foreach (var r in over) await AnswerAsync(r, Result(StopReason.Cancelled));
        if (cancel.Count > 0) await TryWriteAsync(connection, Notification(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId }));
        if (idle) await WithdrawStaleQuestionsAsync(sessionId);
    }

    JsonObject ReplayFrom(string sessionId)
    {
        lock (state)
            return cursors.TryGetValue(sessionId, out var position)
                ? new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = position }
                : new JsonObject { ["type"] = GorynychProtocol.ReplayFromStart };
    }

    /// <summary>Makes the connection the current one, then sends what waited for it, in order.</summary>
    async Task FlushAsync(Connection connection)
    {
        await sendGate.WaitAsync();
        try
        {
            List<(JsonObject Message, Upstream? Request)> waiting;
            lock (state)
            {
                restarting = false;
                // First what went out before the connection was lost, then what came since.
                waiting = again.OrderBy(r => r.Id).Where(r => upstream.ContainsKey(r.Id)).Select(r => (Again(r), (Upstream?)r)).ToList();
                again.Clear();
            }
            waiting.AddRange(outbox);
            outbox.Clear();
            current = connection;
            for (var i = 0; i < waiting.Count; i++)
            {
                var (message, request) = waiting[i];
                if (request is not null) lock (state) request.Generation = connection.Generation;
                if (await TryWriteAsync(connection, message)) continue;
                // Lost again: the rest waits for the next one (LostAsync takes the one sent).
                if (request is not null) lock (state) request.Generation = 0;
                outbox.AddRange(waiting.Skip(i));
                return;
            }
        }
        finally
        {
            sendGate.Release();
        }
    }

    /// <summary>
    /// A request sent again: a load or resume whose replay got somewhere goes on from there as a
    /// resume; anything else as it was. Under <see cref="state"/>.
    /// </summary>
    JsonObject Again(Upstream request)
    {
        if (PolicyOf(request.Method) != Policy.Reopen || request.SessionId is not { } sessionId || !cursors.TryGetValue(sessionId, out var position))
            return request.Message;
        var message = (JsonObject)request.Message.DeepClone();
        message["method"] = AcpMethods.Agent.SessionResume;
        var @params = message["params"] as JsonObject ?? [];
        var meta = @params["_meta"] as JsonObject ?? [];
        meta[GorynychProtocol.MetaReplayFrom] = new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = position };
        @params["_meta"] = meta;
        message["params"] = @params;
        return message;
    }

    /// <summary>A held prompt's turn ended in the agent while its connection was gone: answered as it ended.</summary>
    async Task EndHeldPromptAsync(string promptId, JsonObject meta)
    {
        Upstream? request;
        lock (state)
        {
            request = upstream.Values.FirstOrDefault(r => r.Held && r.PromptId == promptId);
            if (request is not null) upstream.Remove(request.Id);
        }
        if (request is null) return;
        if (meta[GorynychProtocol.MetaError] is JsonObject error)
            await AnswerAsync(request, Error(error["code"] is JsonValue c && c.TryGetValue<int>(out var code) ? code : JsonRpcException.InternalError,
                Text(error["message"]) ?? "the prompt failed"));
        else
            await AnswerAsync(request, Result(Text(meta[GorynychProtocol.MetaStopReason]) is { } reason ? new StopReason(reason) : StopReason.EndTurn));
        if (request.SessionId is { } sessionId) await WithdrawStaleQuestionsAsync(sessionId);
    }

    /// <summary>
    /// The session has no turn left that an earlier connection's question belongs to: those the client
    /// still shows are withdrawn (<c>$/cancel_request</c>), and answers kept for them dropped.
    /// </summary>
    async Task WithdrawStaleQuestionsAsync(string sessionId)
    {
        List<Downstream> stale;
        lock (state)
        {
            stale = downstream.Values.Where(q => q.SessionId == sessionId && q.Connection.IsLost).ToList();
            foreach (var q in stale) downstream.Remove(q.ClientFacingId);
        }
        foreach (var q in stale.Where(q => q.Answer is null))
            await ToClientAsync(Notification(AcpMethods.Protocol.CancelRequest, new JsonObject { ["requestId"] = q.ClientFacingId }));
    }

    async Task GiveUpAsync(string why)
    {
        Log($"gave up on the service: {why}");
        List<Upstream> waiting;
        await sendGate.WaitAsync();
        try
        {
            outbox.Clear();
        }
        finally
        {
            sendGate.Release();
        }
        lock (state)
        {
            waiting = upstream.Values.OrderBy(r => r.Id).ToList();
            upstream.Clear();
            again.Clear();
        }
        foreach (var request in waiting)
            await AnswerAsync(request, Error(JsonRpcException.InternalError, $"The connection to the Gorynych service was lost: {why}"));
        ended.TrySetResult(1);
    }

    /// <summary>A request of the link's own on a connection not yet in use (the handshake).</summary>
    async Task<JsonObject> RequestAsync(Connection connection, string method, JsonObject @params)
    {
        var id = $"gorynych-link-{Interlocked.Increment(ref nextInternal)}";
        var waiting = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Internal[id] = waiting;
        if (connection.IsLost) throw new IOException("the connection to the service was lost");
        if (!await TryWriteAsync(connection, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }))
            throw new IOException("the connection to the service was lost");
        var answer = await waiting.Task.WaitAsync(options.HandshakeTimeout, stopping.Token);
        if (answer["error"] is JsonObject error)
            throw new JsonRpcException(error["code"] is JsonValue c && c.TryGetValue<int>(out var code) ? code : JsonRpcException.InternalError,
                Text(error["message"]) ?? "error", error["data"]?.DeepClone());
        return answer["result"] as JsonObject ?? [];
    }

    // ---------------------------------------------------------------- writing

    async Task<bool> TryWriteAsync(Connection connection, JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Wire) + "\n");
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
            Fail(connection);
            return false;
        }
        finally
        {
            connection.WriteGate.Release();
        }
    }

    async Task ToClientAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Wire) + "\n");
        await clientGate.WaitAsync();
        try
        {
            await clientOutput.WriteAsync(bytes);
            await clientOutput.FlushAsync();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        {
            ended.TrySetResult(0); // the client is gone
        }
        finally
        {
            clientGate.Release();
        }
    }

    Task AnswerAsync(Upstream request, (string Key, JsonNode Value) outcome) =>
        ToClientAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.ClientId.DeepClone(), [outcome.Key] = outcome.Value });

    static (string, JsonNode) Result(StopReason reason) => ("result", new JsonObject { ["stopReason"] = reason.Value });

    static (string, JsonNode) Error(int code, string message) => ("error", new JsonObject { ["code"] = code, ["message"] = message });

    static JsonObject Response(JsonNode id, JsonObject answer)
    {
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        foreach (var (key, value) in answer) response[key] = value?.DeepClone();
        return response;
    }

    static JsonObject Notification(string method, JsonObject @params) => new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params };

    static void Drop(Connection connection)
    {
        connection.Lost.Cancel();
        try { connection.Output.Dispose(); } catch { }
        try { connection.Input.Dispose(); } catch { }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// <paramref name="params"/> with the keys of <paramref name="meta"/> its <c>_meta</c> lacks: how the
    /// bridge scopes a client's <c>initialize</c> to its agent.
    /// </summary>
    public static JsonObject AddMeta(JsonObject? @params, JsonObject? meta)
    {
        @params ??= [];
        if (meta is null || meta.Count == 0) return @params;
        var own = @params["_meta"] as JsonObject ?? [];
        foreach (var (key, value) in meta)
            if (own[key] is null) own[key] = value?.DeepClone();
        @params["_meta"] = own;
        return @params;
    }

    static bool IsRestarting(JsonObject response) =>
        response["error"]?["data"]?[GorynychProtocol.MetaRestarting] is JsonValue v && v.TryGetValue<bool>(out var restarting) && restarting;

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool TryId(JsonNode? id, out long value)
    {
        value = 0;
        return id is JsonValue v && (v.TryGetValue(out value) || (v.TryGetValue<string>(out var s) && long.TryParse(s, out value)));
    }

    static string Cut(string s) => s.Length > 200 ? s[..200] + "…" : s;

    void Log(string message) => options.Log?.Invoke(message);

    /// <summary>A client's request on its way to the agent.</summary>
    sealed class Upstream
    {
        public required long Id { get; init; }
        public required JsonNode ClientId { get; init; }
        public required string Method { get; init; }
        public string? SessionId { get; init; }
        public string? PromptId { get; set; }
        public JsonObject Message { get; set; } = [];

        /// <summary>The connection it went out on; 0 while it waits to go.</summary>
        public int Generation { get; set; }

        /// <summary>A prompt in flight when its connection went: answered by how its turn ends.</summary>
        public bool Held { get; set; }

        /// <summary>The client cancelled the held prompt's session meanwhile.</summary>
        public bool CancelAsked { get; set; }
    }

    /// <summary>A request of the agent's the client has not answered.</summary>
    sealed class Downstream
    {
        public required long ClientFacingId { get; init; }
        public string? Key { get; init; }
        public string? SessionId { get; init; }

        /// <summary>The connection that asks it now: the one that asked it, or the next one that asked it again.</summary>
        public required Connection Connection { get; set; }
        public required JsonNode ServiceId { get; set; }

        /// <summary>The client's answer, kept while no connection could take it.</summary>
        public JsonObject? Answer { get; set; }
    }

    sealed class Connection(int generation, Stream input, Stream output)
    {
        public int Generation { get; } = generation;
        public Stream Input { get; } = input;
        public Stream Output { get; } = output;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public CancellationTokenSource Lost { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> Internal { get; } = new(StringComparer.Ordinal);
        public int Failed;
        public bool IsLost => Lost.IsCancellationRequested;
    }
}
