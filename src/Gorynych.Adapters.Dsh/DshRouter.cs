using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;
using static Gorynych.Adapters.Dsh.DshLines;

namespace Gorynych.Adapters.Dsh;

/// <summary>One DSH process of a mode behind its <see cref="DshProxy"/>, as the router sees it.</summary>
internal sealed class DshLane(DshMode mode, Stream input, Stream output, Task completion, Func<ValueTask> stop)
{
    public DshMode Mode => mode;

    /// <summary>Lines from DSH, rewritten by the proxy.</summary>
    public Stream Input => input;

    /// <summary>Lines for DSH, through the proxy.</summary>
    public Stream Output => output;

    public Task Completion => completion;
    public SemaphoreSlim WriteGate { get; } = new(1, 1);

    readonly Lock stopGate = new();
    Task? stopping;

    /// <summary>Stops the process once; later calls wait for the same stop.</summary>
    public Task StopAsync()
    {
        lock (stopGate) return stopping ??= stop().AsTask();
    }

    /// <summary>Set under the router's lock once the lane holds nothing and is being stopped; nothing new goes to it.</summary>
    public bool Stopping { get; set; }
}

/// <summary>
/// Puts each DSH session in the process of its permission mode (docs/adapters.md, "DeepSeek
/// Harness"). DSH's sandbox is per process, so read-only sessions run in one <c>dsh --profile
/// acp</c> and full-access sessions in a second one, started when first needed and stopped when it
/// holds no session. The router:
/// <list type="bullet">
/// <item>sends a session's messages to the process that has it open, and a resume to the process of the mode Gorynych keeps for it;</item>
/// <item>adds the <c>mode</c> config option, and switches a session's mode by closing it in one process and resuming it in the other;</item>
/// <item>numbers DSH's own requests anew, so two processes' IDs never meet at the service;</item>
/// <item>adds the sessions open in the second process to <c>session/list</c>.</item>
/// </list>
/// </summary>
internal sealed class DshRouter
{
    /// <summary>The IDs of the router's own requests to a process.</summary>
    const string OwnPrefix = "gorynych-dsh:";

    /// <summary>A service request sent to a process, kept until it is answered.</summary>
    readonly record struct Pending(DshLane Lane, string Method, string? SessionId, JsonNode? Params, DshPin? Pin);

    /// <summary>A session open in a process.</summary>
    sealed class Live(DshLane lane, string cwd, JsonNode? mcpServers, DshPin? pin)
    {
        public DshLane Lane => lane;
        public string Cwd => cwd;
        public JsonNode? McpServers => mcpServers;
        public DshPin? Pin => pin;

        /// <summary>DSH's own options (model, effort) as it last gave them, without the mode.</summary>
        public JsonArray? ConfigOptions { get; set; }
    }

    readonly DshAdapterOptions options;
    readonly DshLane main;
    readonly Func<DshMode, Task<DshLane>> startLane;
    readonly DshModeStore modes;
    readonly DshPins pins;
    readonly Stream fromService, toService;
    readonly SemaphoreSlim serviceGate = new(1, 1);
    readonly Lock gate = new();
    readonly Dictionary<DshMode, Task<DshLane>> lanes = [];
    readonly Dictionary<string, Pending> pending = [];
    /// <summary>DSH's requests to the service, by the router's ID.</summary>
    readonly Dictionary<string, (DshLane Lane, JsonNode Id)> laneRequests = [];
    readonly Dictionary<(DshLane Lane, string Id), string> routerIds = [];
    readonly Dictionary<string, (DshLane Lane, TaskCompletionSource<JsonObject> Answer)> own = [];
    readonly Dictionary<string, Live> live = [];
    readonly Dictionary<string, int> prompting = [];
    /// <summary>Per session, the work on its last message: the next one waits for it, so a session's messages keep their order.</summary>
    readonly Dictionary<string, Task> busy = [];
    readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    JsonNode? initializeParams;
    long next;

    public DshRouter(DshAdapterOptions options, (Stream Input, Stream Output) service, DshLane main,
        Func<DshMode, Task<DshLane>> startLane, DshModeStore modes, DshPins pins)
    {
        this.options = options;
        (fromService, toService) = service;
        this.main = main;
        this.startLane = startLane;
        this.modes = modes;
        this.pins = pins;
        lanes[main.Mode] = Task.FromResult(main);
    }

    /// <summary>Completes when the read-only process's output ends and everything from it has reached the service.</summary>
    public Task Completion => done.Task;

    public void Start()
    {
        _ = Task.Run(FromServiceAsync);
        _ = Task.Run(() => FromLaneAsync(main));
    }

    /// <summary>Stops every process.</summary>
    public async Task StopAsync()
    {
        List<Task<DshLane>> all;
        lock (gate) all = [.. lanes.Values];
        foreach (var lane in all)
            if (lane.IsCompletedSuccessfully) await lane.Result.StopAsync();
    }

    // ---------------------------------------------------------------- service -> DSH

    async Task FromServiceAsync()
    {
        try
        {
            using var reader = new StreamReader(fromService, Encoding.UTF8);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    await FromServiceAsync(line);
                }
                catch (Exception e) when (e is not (IOException or ObjectDisposedException or OperationCanceledException))
                {
                    options.Log?.Invoke($"dsh: could not route a message from the service ({e.Message}): {Shorten(line)}");
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            // The service let go: each process's stdin ends, and each DSH exits.
            List<Task<DshLane>> all;
            lock (gate) all = [.. lanes.Values];
            foreach (var lane in all)
                if (lane.IsCompletedSuccessfully)
                    try { await lane.Result.Output.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    async Task FromServiceAsync(string line)
    {
        if (Parse(line) is not JsonObject message)
        {
            await ToLaneAsync(main, line);
            return;
        }
        var id = message["id"];
        var method = Str(message["method"]);
        if (message["method"] is null)
        {
            if (id is not null) await AnswerToLaneAsync(message, id);
            return;
        }
        if (method is null)
        {
            await ToLaneAsync(main, line); // DSH answers what it cannot read
            return;
        }
        var sessionId = Str(message["params"]?["sessionId"]);
        if (method == AcpMethods.Protocol.CancelRequest)
        {
            // The service withdraws a request of its own: it went to one process.
            DshLane lane = main;
            if (message["params"]?["requestId"] is { } requestId)
                lock (gate)
                    if (pending.TryGetValue(Key(requestId), out var p)) lane = p.Lane;
            await ToLaneAsync(lane, message);
            return;
        }
        if (sessionId is null || method is AcpMethods.Agent.SessionNew or AcpMethods.Agent.SessionList)
        {
            if (id is not null)
            {
                if (method == AcpMethods.Agent.Initialize) initializeParams = message["params"]?.DeepClone();
                var keep = method is AcpMethods.Agent.SessionNew or AcpMethods.Agent.SessionList ? message["params"]?.DeepClone() : null;
                lock (gate) pending[Key(id)] = new Pending(main, method, null, keep, null);
            }
            await ToLaneAsync(main, message);
            return;
        }
        // A session's messages wait for the work on its earlier ones (a switch, a process starting).
        Task prior;
        lock (gate) prior = busy.GetValueOrDefault(sessionId) ?? Task.CompletedTask;
        var work = SessionMessageAfterAsync(prior, message, id, method, sessionId);
        lock (gate) busy[sessionId] = work;
        _ = work.ContinueWith(_ =>
        {
            lock (gate)
                if (busy.GetValueOrDefault(sessionId) == work) busy.Remove(sessionId);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    async Task SessionMessageAfterAsync(Task prior, JsonObject message, JsonNode? id, string method, string sessionId)
    {
        try { await prior; } catch { /* its own failure was answered already */ }
        try
        {
            if (id is not null && method == AcpMethods.Agent.SessionSetConfigOption && Str(message["params"]?["configId"]) == DshModes.OptionId)
                await SwitchAsync(message, id, sessionId);
            else
                await SessionMessageAsync(message, id, method, sessionId);
        }
        catch (Exception e)
        {
            // A process that stopped under the message counts too: the service must not wait for an answer.
            options.Log?.Invoke($"dsh {sessionId}: {method} failed in the adapter: {e.Message}");
            if (id is not null)
                try { await ToServiceAsync(Error(id, e is JsonRpcException j ? j.Code : JsonRpcException.InternalError, e.Message)); }
                catch (Exception gone) when (gone is IOException or ObjectDisposedException) { }
        }
    }

    async Task SessionMessageAsync(JsonObject message, JsonNode? id, string method, string sessionId)
    {
        if (method is AcpMethods.Agent.SessionResume or AcpMethods.Agent.SessionLoad)
        {
            // A session DSH pinned runs as pinned in any process; it goes to the one of its pin.
            var pin = pins.Read(sessionId);
            var mode = (pin is null ? null : DshModes.Of(pin)) ?? modes.Get(sessionId);
            await SendAsync(mode, message, id is null ? null : lane => new Pending(lane, method, sessionId, message["params"]?.DeepClone(), pin));
            return;
        }
        DshLane target;
        lock (gate)
            target = live.TryGetValue(sessionId, out var open) ? open.Lane
                : lanes.TryGetValue(modes.Get(sessionId), out var started) && started.IsCompletedSuccessfully && !started.Result.Stopping ? started.Result
                : main;
        lock (gate)
        {
            if (id is not null) pending[Key(id)] = new Pending(target, method, sessionId, null, null);
            if (id is not null && method == AcpMethods.Agent.SessionPrompt) prompting[sessionId] = prompting.GetValueOrDefault(sessionId) + 1;
        }
        await ToLaneAsync(target, message);
    }

    /// <summary>Sends a request or notification to the process of <paramref name="mode"/>, starting it if needed.</summary>
    async Task SendAsync(DshMode mode, JsonObject message, Func<DshLane, Pending>? track)
    {
        var lane = await ReserveAsync(mode, l =>
        {
            if (track is not null && message["id"] is { } id) pending[Key(id)] = track(l);
        });
        await ToLaneAsync(lane, message);
    }

    /// <summary>
    /// The running process of <paramref name="mode"/>, started if there is none, with
    /// <paramref name="hold"/> run under the router's lock while the process is surely not stopping.
    /// </summary>
    async Task<DshLane> ReserveAsync(DshMode mode, Action<DshLane> hold)
    {
        while (true)
        {
            Task<DshLane> starting;
            lock (gate)
            {
                if (!lanes.TryGetValue(mode, out starting!) || starting.IsFaulted || starting.IsCanceled
                    || (starting.IsCompletedSuccessfully && starting.Result.Stopping))
                    lanes[mode] = starting = Task.Run(() => StartLaneAsync(mode));
            }
            var lane = await starting;
            lock (gate)
            {
                if (lane.Stopping) continue;
                hold(lane);
                return lane;
            }
        }
    }

    async Task<DshLane> StartLaneAsync(DshMode mode)
    {
        options.Log?.Invoke($"dsh: starting the {mode.Name.ToLowerInvariant()} process");
        var lane = await startLane(mode);
        _ = Task.Run(() => FromLaneAsync(lane));
        var init = await RequestAsync(lane, AcpMethods.Agent.Initialize, initializeParams?.DeepClone() ?? new JsonObject { ["protocolVersion"] = 1 });
        if (init["error"] is JsonObject error)
        {
            await lane.StopAsync();
            throw new JsonRpcException(JsonRpcException.InternalError, $"DeepSeek Harness ({mode.Name}) did not start: {Str(error["message"])}");
        }
        return lane;
    }

    /// <summary>The router's own request to a process, answered to the router alone.</summary>
    async Task<JsonObject> RequestAsync(DshLane lane, string method, JsonNode? @params)
    {
        var id = OwnPrefix + Interlocked.Increment(ref next);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) own[id] = (lane, answer);
        await ToLaneAsync(lane, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        return await answer.Task;
    }

    /// <summary>The service's answer to one of DSH's requests goes back to its process, with DSH's ID.</summary>
    async Task AnswerToLaneAsync(JsonObject message, JsonNode id)
    {
        (DshLane Lane, JsonNode Id) request;
        lock (gate)
        {
            var key = Str(id) ?? Key(id);
            if (!laneRequests.Remove(key, out request)) return; // withdrawn, or not DSH's
            routerIds.Remove((request.Lane, Key(request.Id)));
        }
        message["id"] = request.Id.DeepClone();
        await ToLaneAsync(request.Lane, message);
    }

    // ---------------------------------------------------------------- switching

    /// <summary>
    /// <c>session/set_config_option</c> for <c>mode</c>: the session is closed in its process and resumed in
    /// the other one (DSH's resume brings back its model and effort). A session DSH pinned cannot switch.
    /// </summary>
    async Task SwitchAsync(JsonObject message, JsonNode id, string sessionId)
    {
        var value = Str(message["params"]?["value"]);
        Live? open;
        int turns;
        lock (gate)
        {
            open = live.GetValueOrDefault(sessionId);
            turns = prompting.GetValueOrDefault(sessionId);
        }
        if (open is null)
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"The DeepSeek Harness session {sessionId} is not open.");
        var target = DshModes.ById(value);
        var now = open.Pin is { } pinned ? DshModes.Of(pinned) : open.Lane.Mode;
        if (target is not null && target == now || target is null && value == DshModes.PinnedValue && now is null)
        {
            await ToServiceAsync(Result(id, Options(open, open.Lane.Mode)));
            return;
        }
        if (target is null)
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"No mode {value}: DeepSeek Harness sessions are read-only or full-access.");
        if (open.Pin is { } pin)
            throw new JsonRpcException(JsonRpcException.InvalidParams,
                $"DeepSeek Harness pinned this session's permissions (sandbox {pin.Sandbox}) when it was made, and keeps them in any process, " +
                "so Gorynych cannot switch it. Change them in dsh web with /permission, or start a new session for another mode.");
        if (turns > 0)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "A turn of this session is running; switch the mode once it has ended.");

        var from = open.Lane;
        await ReserveAsync(target, _ => { }); // started first: a process that cannot start leaves the session where it is
        var closed = await RequestAsync(from, AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId });
        if (closed["error"] is JsonObject closeError)
            options.Log?.Invoke($"dsh {sessionId}: close in the {from.Mode.Id} process failed ({Str(closeError["message"])}); DSH lets the session go anyway");
        lock (gate) if (live.GetValueOrDefault(sessionId) == open) live.Remove(sessionId);
        var resume = new JsonObject { ["sessionId"] = sessionId, ["cwd"] = open.Cwd, ["mcpServers"] = open.McpServers?.DeepClone() ?? new JsonArray() };
        var resumed = await ResumeInAsync(target, resume);
        if (resumed.Answer["error"] is JsonObject error)
        {
            // Back where it was, so the session stays usable; the client hears why the switch failed.
            var back = await ResumeInAsync(from.Mode, resume.DeepClone().AsObject());
            if (back.Answer["result"] is JsonObject backResult) Open(sessionId, back.Lane, open.Cwd, open.McpServers, null, backResult);
            else options.Log?.Invoke($"dsh {sessionId}: could not resume it in the {from.Mode.Id} process either: {Str(back.Answer["error"]?["message"])}");
            StopIfIdle(resumed.Lane);
            await ToServiceAsync(Error(id, JsonRpcException.InternalError,
                $"DeepSeek Harness could not open this session in {target.Name.ToLowerInvariant()} mode: {Str(error["message"]) ?? "no reason given"}"));
            return;
        }
        var opened = Open(sessionId, resumed.Lane, open.Cwd, open.McpServers, null, resumed.Answer["result"] as JsonObject);
        try
        {
            modes.Set(sessionId, target);
        }
        catch (IOException e)
        {
            options.Log?.Invoke($"dsh {sessionId}: {e.Message}; the session opens read-only next time");
        }
        options.Log?.Invoke($"dsh {sessionId}: now {target.Id}");
        StopIfIdle(from);
        await ToServiceAsync(Result(id, Options(opened, target)));
    }

    /// <summary>A <c>session/resume</c> of the router's own in the process of <paramref name="mode"/>; the answer carries the lane.</summary>
    async Task<(JsonObject Answer, DshLane Lane)> ResumeInAsync(DshMode mode, JsonObject resume)
    {
        var lane = await ReserveAsync(mode, _ => { });
        return (await RequestAsync(lane, AcpMethods.Agent.SessionResume, resume), lane);
    }

    Live Open(string sessionId, DshLane lane, string cwd, JsonNode? mcpServers, DshPin? pin, JsonObject? result)
    {
        var opened = new Live(lane, cwd, mcpServers?.DeepClone(), pin) { ConfigOptions = OwnOptions(result) };
        lock (gate) live[sessionId] = opened;
        return opened;
    }

    /// <summary>DSH's options from a result, without a mode option.</summary>
    static JsonArray? OwnOptions(JsonObject? holder) =>
        holder?["configOptions"] is JsonArray options
            ? new JsonArray(options.Where(o => Str(o?["id"]) != DshModes.OptionId).Select(o => o?.DeepClone()).ToArray())
            : null;

    static JsonObject Options(Live session, DshMode mode)
    {
        var result = new JsonObject { ["configOptions"] = session.ConfigOptions?.DeepClone() ?? new JsonArray() };
        DshModes.AddTo(result, mode, session.Pin);
        return result;
    }

    static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    /// <summary>Stops a second process that holds nothing any more; the read-only process stays.</summary>
    void StopIfIdle(DshLane lane)
    {
        if (lane == main) return;
        lock (gate)
        {
            if (lane.Stopping
                || live.Values.Any(s => s.Lane == lane)
                || pending.Values.Any(p => p.Lane == lane)
                || own.Values.Any(o => o.Lane == lane)
                || laneRequests.Values.Any(r => r.Lane == lane))
                return;
            lane.Stopping = true;
            if (lanes.TryGetValue(lane.Mode, out var t) && t.IsCompletedSuccessfully && t.Result == lane) lanes.Remove(lane.Mode);
        }
        options.Log?.Invoke($"dsh: the {lane.Mode.Name.ToLowerInvariant()} process holds no session; stopping it");
        _ = Task.Run(lane.StopAsync);
    }

    // ---------------------------------------------------------------- DSH -> service

    async Task FromLaneAsync(DshLane lane)
    {
        try
        {
            using var reader = new StreamReader(lane.Input, Encoding.UTF8);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                if (Parse(line) is not JsonObject message)
                {
                    await ToServiceAsync(line);
                    continue;
                }
                try
                {
                    await FromLaneAsync(lane, message);
                }
                catch (Exception e) when (e is not (IOException or ObjectDisposedException or OperationCanceledException))
                {
                    options.Log?.Invoke($"dsh: passed on a message from DSH unread ({e.Message}): {Shorten(line)}");
                    await ToServiceAsync(line);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            await LaneEndedAsync(lane);
        }
    }

    async Task FromLaneAsync(DshLane lane, JsonObject message)
    {
        var id = message["id"];
        if (message["method"] is null && id is not null)
        {
            await AnswerFromLaneAsync(lane, message, id);
            return;
        }
        var method = Str(message["method"]);
        if (id is not null && method is not null)
        {
            // DSH's own request: each process numbers its own, so the service gets the router's numbers.
            var routerId = $"dsh-{Interlocked.Increment(ref next)}";
            lock (gate)
            {
                laneRequests[routerId] = (lane, id.DeepClone());
                routerIds[(lane, Key(id))] = routerId;
            }
            message["id"] = routerId;
        }
        else if (method == AcpMethods.Protocol.CancelRequest && message["params"] is JsonObject cancel && cancel["requestId"] is { } requestId)
        {
            string? routerId;
            lock (gate)
                if (routerIds.Remove((lane, Key(requestId)), out routerId)) laneRequests.Remove(routerId);
            if (routerId is null) return; // answered already
            cancel["requestId"] = routerId;
        }
        else if (method == AcpMethods.Client.SessionUpdate
            && message["params"]?["update"] is JsonObject update && Str(update["sessionUpdate"]) == "config_option_update"
            && Str(message["params"]?["sessionId"]) is { } sessionId)
        {
            Live? open;
            lock (gate) open = live.GetValueOrDefault(sessionId);
            if (open is not null) open.ConfigOptions = OwnOptions(update);
            DshModes.AddTo(update, open?.Lane.Mode ?? lane.Mode, open?.Pin);
        }
        await ToServiceAsync(message);
    }

    async Task AnswerFromLaneAsync(DshLane lane, JsonObject message, JsonNode id)
    {
        var key = Key(id);
        Pending request;
        lock (gate)
        {
            if (Str(id) is { } ownId && ownId.StartsWith(OwnPrefix, StringComparison.Ordinal))
            {
                if (own.Remove(ownId, out var mine)) mine.Answer.TrySetResult(message);
                return;
            }
            if (!pending.Remove(key, out request)) request = default;
        }
        var result = message["result"] as JsonObject;
        var sessionId = request.SessionId;
        switch (request.Method)
        {
            case AcpMethods.Agent.SessionNew when result is not null && Str(result["sessionId"]) is { } created:
                Open(created, lane, Str(request.Params?["cwd"]) ?? "", request.Params?["mcpServers"], null, result);
                DshModes.AddTo(result, lane.Mode, null);
                break;
            case AcpMethods.Agent.SessionResume or AcpMethods.Agent.SessionLoad when result is not null && sessionId is not null:
                Open(sessionId, lane, Str(request.Params?["cwd"]) ?? "", request.Params?["mcpServers"], request.Pin, result);
                DshModes.AddTo(result, lane.Mode, request.Pin);
                break;
            case AcpMethods.Agent.SessionSetConfigOption when result is not null && sessionId is not null:
                Live? open;
                lock (gate) open = live.GetValueOrDefault(sessionId);
                if (open is not null) open.ConfigOptions = OwnOptions(result);
                DshModes.AddTo(result, open?.Lane.Mode ?? lane.Mode, open?.Pin);
                break;
            case AcpMethods.Agent.SessionList when (result?["sessions"]) is JsonArray sessions && (request.Params?["cursor"]) is null:
                MergeOthers(sessions, Str(request.Params?["cwd"]));
                break;
            case AcpMethods.Agent.SessionPrompt when sessionId is not null:
                lock (gate)
                    if (prompting.GetValueOrDefault(sessionId) is > 1 and var n) prompting[sessionId] = n - 1;
                    else prompting.Remove(sessionId);
                break;
        }
        if (sessionId is not null
            && (request.Method == AcpMethods.Agent.SessionClose
                || (Str(message["error"]?["message"]) ?? "").Contains("unknown session", StringComparison.OrdinalIgnoreCase)))
        {
            // DSH lets go of a session even when its close fails.
            lock (gate)
                if (live.TryGetValue(sessionId, out var open) && open.Lane == lane) live.Remove(sessionId);
            StopIfIdle(lane);
        }
        await ToServiceAsync(message);
    }

    /// <summary>
    /// The read-only process lists every session DSH keeps but those open in itself, which its proxy
    /// adds; the sessions open in the other process are added here, once, on the first page.
    /// </summary>
    void MergeOthers(JsonArray sessions, string? cwd)
    {
        var listed = sessions.Select(s => Str(s?["sessionId"])).OfType<string>().ToHashSet();
        List<KeyValuePair<string, Live>> others;
        lock (gate) others = live.Where(kv => kv.Value.Lane != main).ToList();
        var at = 0;
        foreach (var (id, open) in others)
        {
            if (listed.Contains(id) || (cwd is not null && !DshProxy.SameFolder(open.Cwd, cwd))) continue;
            sessions.Insert(at++, new JsonObject { ["sessionId"] = id, ["cwd"] = open.Cwd });
        }
    }

    /// <summary>
    /// A process ended. Its open sessions are gone, the service's requests to it are answered with an
    /// error and its own requests withdrawn. The read-only process ending ends the adapter.
    /// </summary>
    async Task LaneEndedAsync(DshLane lane)
    {
        List<JsonNode> failed = [];
        List<string> withdrawn = [];
        lock (gate)
        {
            lane.Stopping = true;
            if (lanes.TryGetValue(lane.Mode, out var t) && t.IsCompletedSuccessfully && t.Result == lane) lanes.Remove(lane.Mode);
            foreach (var (key, p) in pending.Where(kv => kv.Value.Lane == lane).ToList())
            {
                pending.Remove(key);
                if (Parse(key) is { } requestId) failed.Add(requestId);
            }
            foreach (var (routerId, r) in laneRequests.Where(kv => kv.Value.Lane == lane).ToList())
            {
                laneRequests.Remove(routerId);
                routerIds.Remove((lane, Key(r.Id)));
                withdrawn.Add(routerId);
            }
            foreach (var (ownId, o) in own.Where(kv => kv.Value.Lane == lane).ToList())
            {
                own.Remove(ownId);
                o.Answer.TrySetResult(Error(ownId, JsonRpcException.InternalError, "DeepSeek Harness stopped."));
            }
            foreach (var sessionId in live.Where(kv => kv.Value.Lane == lane).Select(kv => kv.Key).ToList())
            {
                live.Remove(sessionId);
                prompting.Remove(sessionId);
            }
        }
        try
        {
            foreach (var requestId in withdrawn)
                await ToServiceAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = AcpMethods.Protocol.CancelRequest,
                    ["params"] = new JsonObject { ["requestId"] = requestId },
                });
            foreach (var requestId in failed)
                await ToServiceAsync(Error(requestId, JsonRpcException.InternalError, $"DeepSeek Harness ({lane.Mode.Name.ToLowerInvariant()}) stopped."));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        if (lane != main) return;
        try
        {
            await StopAsync();
        }
        catch (Exception e)
        {
            options.Log?.Invoke($"dsh: stopping the processes failed: {e.Message}");
        }
        finally
        {
            try { await toService.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            done.TrySetResult();
        }
    }

    // ---------------------------------------------------------------- framing

    Task ToLaneAsync(DshLane lane, JsonObject message) => ToLaneAsync(lane, message.ToJsonString(Wire));

    async Task ToLaneAsync(DshLane lane, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await lane.WriteGate.WaitAsync();
        try
        {
            await lane.Output.WriteAsync(bytes);
            await lane.Output.FlushAsync();
        }
        finally
        {
            lane.WriteGate.Release();
        }
    }

    Task ToServiceAsync(JsonObject message) => ToServiceAsync(message.ToJsonString(Wire));

    async Task ToServiceAsync(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await serviceGate.WaitAsync();
        try
        {
            await toService.WriteAsync(bytes);
            await toService.FlushAsync();
        }
        finally
        {
            serviceGate.Release();
        }
    }
}
