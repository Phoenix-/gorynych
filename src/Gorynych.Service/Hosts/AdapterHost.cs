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

    async Task AgentAnsweredAsync(JsonNode? id, JsonObject message)
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
                if (!forwarded.Remove(key, out request)) return;
                Track(request, message);
                if (request.Method == AcpMethods.Agent.SessionPrompt && request.SessionId is { } sessionId)
                {
                    var report = new JsonObject { ["sessionId"] = sessionId, ["turn"] = key };
                    if (request.PromptId is { } promptId) report["promptId"] = promptId;
                    if (request.AskId is { } askId) report["askId"] = askId;
                    if (request.Opinion is { } opinion) report["opinion"] = opinion;
                    if (message["result"] is { } r) report["result"] = r.DeepClone();
                    if (message["error"] is { } e) report["error"] = e.DeepClone();
                    ended = Number(Notification(GorynychProtocol.MethodTurnEnded, report));
                }
                // So is the answer to a question the agent took by its own way in (Claude's channel):
                // the conductor that asked may be gone, and the next one gives it to the asker.
                else if (request.Method == GorynychProtocol.MethodOpinion && request.AskId is { } askId && request.SessionId is { } asked)
                {
                    var report = new JsonObject { ["sessionId"] = asked, ["askId"] = askId };
                    if (message["result"] is { } r) report["result"] = r.DeepClone();
                    if (message["error"] is { } e) report["error"] = e.DeepClone();
                    ended = Number(Notification(GorynychProtocol.MethodAskAnswered, report));
                }
            }
            if (ended is not null && current is { IsLost: false } connection) await TryWriteLineAsync(connection, ended);
        }
        finally
        {
            sendGate.Release();
        }
        if (request.Method == AcpMethods.Agent.Initialize)
        {
            if (message["result"] is JsonObject result) message["result"] = WithHostMeta(result, reattached: false);
            initialized.TrySetResult();
        }
        message["id"] = request.ConductorId.DeepClone();
        if (!request.Connection.IsLost) await TryWriteAsync(request.Connection, message);
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
                break;
            case AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionResume when result is not null && request.SessionId is { } opened:
                live.Add(opened);
                break;
            case AcpMethods.Agent.SessionClose or AcpMethods.Agent.SessionDelete when request.SessionId is { } closed:
                live.Remove(closed);
                break;
        }
    }

    async Task AgentNotifiedAsync(string method, JsonObject message)
    {
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
            lock (state) line = Number(message);
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
        var sessionId = Text(@params?["sessionId"]);
        var number = ++seq;
        var carrier = Text(message["method"]) == AcpMethods.Client.SessionUpdate ? @params?["update"] as JsonObject : @params;
        var kept = carrier is not null && sessionId is not null;
        if (kept)
        {
            if (carrier!["_meta"] is not JsonObject meta) carrier["_meta"] = meta = [];
            meta[GorynychProtocol.MetaHostSeq] = $"{options.HostId}/{number}";
        }
        var line = message.ToJsonString(Wire);
        if (!kept) return line;
        buffer.Enqueue(new Kept(number, sessionId!, line));
        bufferedBytes += line.Length;
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
            busy = forwarded.Values.Any(f => f.Method == AcpMethods.Agent.SessionPrompt) || asked.Count > 0;
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

    async Task ConductorAsksAsync(Connection connection, JsonNode id, string method, JsonObject message)
    {
        switch (method)
        {
            case AcpMethods.Agent.Initialize:
            {
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
                    await TryWriteAsync(connection, Response(id, WithHostMeta((JsonObject)known.DeepClone(), reattached: true)));
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
        long key;
        var @params = message["params"] as JsonObject;
        lock (state)
        {
            key = ++nextToAgent;
            forwarded[key] = new Forwarded
            {
                Connection = connection, ConductorId = id.DeepClone(), Method = method, SessionId = Text(@params?["sessionId"]),
                PromptId = Text(@params?["_meta"]?[GorynychProtocol.MetaPromptId]),
                AskId = Text(@params?["_meta"]?[GorynychProtocol.MetaAskId]) ?? Text(@params?["askId"]), // a prompt's _meta; _gorynych/opinion's params
                Opinion = Text(@params?["_meta"]?[GorynychProtocol.MetaOpinion]),
            };
        }
        message["id"] = key;
        await WriteToAgentAsync(message);
    }

    internal JsonObject Report()
    {
        lock (state)
            return new JsonObject
            {
                ["sessions"] = new JsonArray(live.Select(s => (JsonNode)s).ToArray()),
                ["turns"] = new JsonArray(forwarded.Where(f => f.Value.Method == AcpMethods.Agent.SessionPrompt && f.Value.SessionId is not null)
                    .Select(f => (JsonNode)new JsonObject { ["sessionId"] = f.Value.SessionId, ["turn"] = f.Key, ["promptId"] = f.Value.PromptId }).ToArray()),
                // The questions the agent is answering, by turn or by its own way in: the next conductor waits for their answers.
                ["asks"] = new JsonArray(forwarded.Values.Where(f => f.AskId is not null && f.SessionId is not null
                        && f.Method is AcpMethods.Agent.SessionPrompt or GorynychProtocol.MethodOpinion)
                    .Select(f => (JsonNode)new JsonObject { ["sessionId"] = f.SessionId, ["askId"] = f.AskId, ["opinion"] = f.Opinion }).ToArray()),
                ["buffered"] = new JsonArray(buffer.Select(b => b.SessionId).Distinct(StringComparer.Ordinal).Select(s => (JsonNode)s).ToArray()),
                ["requests"] = asked.Count,
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
            current = connection;
            lock (state) aloneSince = long.MaxValue;
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
            if (wasCurrent) current = null;
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

    JsonObject WithHostMeta(JsonObject result, bool reattached)
    {
        var meta = result["_meta"] as JsonObject ?? [];
        meta[GorynychProtocol.MetaHost] = new JsonObject
        {
            ["hostId"] = options.HostId, ["link"] = GorynychProtocol.HostLinkVersion, ["reattached"] = reattached,
        };
        result["_meta"] = meta;
        return result;
    }

    async Task WriteToAgentAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Wire) + "\n");
        await agentGate.WaitAsync();
        try
        {
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

    /// <summary>A conductor's request on its way to the agent.</summary>
    sealed class Forwarded
    {
        public required Connection Connection { get; init; }
        public required JsonNode ConductorId { get; init; }
        public required string Method { get; init; }
        public string? SessionId { get; init; }
        public string? PromptId { get; init; }

        /// <summary>The asker's name for the question this request carries (<c>gorynych/askId</c>): an opinion, by channel or in a turn.</summary>
        public string? AskId { get; init; }

        /// <summary>For an opinion asked in a turn: its <c>message_id</c> (<c>gorynych/opinion</c>), which the agent's reply names.</summary>
        public string? Opinion { get; init; }
    }

    /// <summary>A question of the agent's no conductor has answered yet.</summary>
    sealed class Asked
    {
        public required JsonNode AgentId { get; init; }
        public required JsonObject Message { get; init; }
        public Connection? Connection { get; set; }
        public long? ConductorId { get; set; }
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
        public bool IsLost => Lost.IsCancellationRequested;
    }
}
