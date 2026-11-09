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

    // The router's state of the lane, read and written under the router's lock.

    /// <summary>Holds nothing and is being stopped: nothing new goes to it.</summary>
    public bool Stopping { get; set; }

    /// <summary>Its output has ended: the process is gone.</summary>
    public bool Ended { get; set; }

    /// <summary>Switches that need the lane until they end; a held lane is not stopped as idle.</summary>
    public int Holds { get; set; }
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
/// <item>adds the sessions open in the second process to a one-page <c>session/list</c>.</item>
/// </list>
/// </summary>
internal sealed class DshRouter
{
    /// <summary>The IDs of the router's own requests to a process.</summary>
    const string OwnPrefix = "gorynych-dsh:";

    /// <summary>ACP's unstable fork. DSH 0.2.2 does not offer it; a fork, should DSH answer one, runs where its parent does.</summary>
    const string SessionFork = "session/fork";

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
    /// <summary>Per mode, its running process or the start of one.</summary>
    readonly Dictionary<DshMode, Task<DshLane>> lanes = [];
    /// <summary>Every process started and not ended yet, also one still initializing.</summary>
    readonly HashSet<DshLane> all = [];
    readonly Dictionary<string, Pending> pending = [];
    /// <summary>DSH's requests to the service, by the router's ID.</summary>
    readonly Dictionary<string, (DshLane Lane, JsonNode Id)> laneRequests = [];
    readonly Dictionary<(DshLane Lane, string Id), string> routerIds = [];
    readonly Dictionary<string, (DshLane Lane, TaskCompletionSource<JsonObject> Answer)> own = [];
    readonly Dictionary<string, Live> live = [];
    /// <summary>Sessions that were open in a process that ended, with its mode, until they are opened again.</summary>
    readonly Dictionary<string, DshMode> lost = [];
    readonly Dictionary<string, int> prompting = [];
    /// <summary>Per session, the work on its last message: the next one waits for it, so a session's messages keep their order.</summary>
    readonly Dictionary<string, Task> busy = [];
    readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    JsonNode? initializeParams;
    bool shuttingDown;
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
        all.Add(main);
    }

    /// <summary>Completes when the read-only process's output ends and everything from it has reached the service.</summary>
    public Task Completion => done.Task;

    public void Start()
    {
        _ = Task.Run(FromServiceAsync);
        _ = Task.Run(() => FromLaneAsync(main));
    }

    /// <summary>Stops every process, also one still starting; none starts after this.</summary>
    public async Task StopAsync()
    {
        foreach (var lane in BeginShutdown())
            try { await lane.StopAsync(); }
            catch (Exception e) { options.Log?.Invoke($"dsh: stopping the {lane.Mode.Id} process failed: {e.Message}"); }
    }

    /// <summary>From now on no process starts; the ones there are.</summary>
    List<DshLane> BeginShutdown()
    {
        lock (gate)
        {
            shuttingDown = true;
            return [.. all];
        }
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
            // The service let go: each process's stdin ends, and each DSH exits. One still
            // starting sees the shutdown and stops itself.
            foreach (var lane in BeginShutdown())
                try { await lane.Output.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
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
            if (method == AcpMethods.Agent.Initialize && id is not null) initializeParams = message["params"]?.DeepClone();
            var keep = method is AcpMethods.Agent.SessionNew or AcpMethods.Agent.SessionList ? message["params"]?.DeepClone() : null;
            try
            {
                await SendToAsync(main, message, id, new Pending(main, method, null, keep, null));
            }
            catch (Exception e) when (id is not null && e is not OperationCanceledException)
            {
                await TryToServiceAsync(Error(id, e is JsonRpcException j ? j.Code : JsonRpcException.InternalError, e.Message));
            }
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
            if (id is not null) await TryToServiceAsync(Error(id, e is JsonRpcException j ? j.Code : JsonRpcException.InternalError, e.Message));
        }
    }

    async Task SessionMessageAsync(JsonObject message, JsonNode? id, string method, string sessionId)
    {
        DshLane? open;
        DshMode? gone;
        lock (gate)
        {
            open = live.GetValueOrDefault(sessionId)?.Lane;
            gone = open is null && lost.TryGetValue(sessionId, out var m) ? m : null;
        }
        if (method is AcpMethods.Agent.SessionResume or AcpMethods.Agent.SessionLoad)
        {
            if (open is not null)
            {
                // Open already: the process that has it answers, as DSH does to a second resume.
                await SendToAsync(open, message, id, new Pending(open, method, sessionId, message["params"]?.DeepClone(), null));
                return;
            }
            // A session DSH pinned runs as pinned in any process; it goes to the one of its pin.
            var pin = pins.Read(sessionId);
            var mode = (pin is null ? null : DshModes.Of(pin)) ?? modes.Get(sessionId);
            var lane = await ReserveAsync(mode, l =>
            {
                lost.Remove(sessionId);
                if (id is not null) pending[Key(id)] = new Pending(l, method, sessionId, message["params"]?.DeepClone(), pin);
            });
            await SendToAsync(lane, message, id, null);
            return;
        }
        if (open is null && gone is not null)
        {
            if (id is null) return; // a cancel for a turn that ended with its process
            if (method == AcpMethods.Agent.SessionClose)
            {
                lock (gate) lost.Remove(sessionId);
                await ToServiceAsync(Result(id, []));
                return;
            }
            throw new JsonRpcException(JsonRpcException.InternalError,
                $"The {gone.Name.ToLowerInvariant()} DeepSeek Harness process stopped, and this session with it; open the session again.");
        }
        DshLane target;
        lock (gate)
            target = open
                ?? (lanes.TryGetValue(modes.Get(sessionId), out var started) && started.IsCompletedSuccessfully && !started.Result.Stopping
                    ? started.Result : main);
        var keep = method == SessionFork ? message["params"]?.DeepClone() : null;
        await SendToAsync(target, message, id, new Pending(target, method, sessionId, keep, null));
    }

    /// <summary>
    /// Writes a service message to a process, with the request noted as pending (and a prompt as a
    /// running turn) unless <paramref name="track"/> is null. A write that fails takes both back.
    /// </summary>
    async Task SendToAsync(DshLane lane, JsonObject message, JsonNode? id, Pending? track)
    {
        var key = id is null ? null : Key(id);
        var turn = track?.Method == AcpMethods.Agent.SessionPrompt && track.Value.SessionId is not null ? track.Value.SessionId : null;
        lock (gate)
        {
            if (lane.Ended)
            {
                // A request noted before the process ended was answered by its end already.
                if (key is not null && track is null && !pending.Remove(key)) return;
                throw new JsonRpcException(JsonRpcException.InternalError, $"The {lane.Mode.Name.ToLowerInvariant()} DeepSeek Harness process stopped.");
            }
            if (key is not null && track is { } p) pending[key] = p;
            if (key is not null && turn is not null) prompting[turn] = prompting.GetValueOrDefault(turn) + 1;
        }
        try
        {
            await ToLaneAsync(lane, message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (gate)
            {
                if (key is not null && !pending.Remove(key)) return; // answered by the process's end
                if (key is not null && turn is not null) EndTurn(turn);
            }
            throw;
        }
    }

    /// <summary>A prompt of the session ended. Called under the lock.</summary>
    void EndTurn(string sessionId)
    {
        if (prompting.GetValueOrDefault(sessionId) is > 1 and var n) prompting[sessionId] = n - 1;
        else prompting.Remove(sessionId);
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
                if (shuttingDown) throw new JsonRpcException(JsonRpcException.InternalError, "DeepSeek Harness is stopping.");
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
        bool late;
        lock (gate)
        {
            late = shuttingDown;
            if (!late) all.Add(lane);
        }
        if (late)
        {
            await lane.StopAsync();
            throw new JsonRpcException(JsonRpcException.InternalError, "DeepSeek Harness is stopping.");
        }
        _ = Task.Run(() => FromLaneAsync(lane));
        var asking = RequestAsync(lane, AcpMethods.Agent.Initialize, initializeParams?.DeepClone() ?? new JsonObject { ["protocolVersion"] = 1 });
        string? failure = null;
        if (await Task.WhenAny(asking, Task.Delay(options.StartTimeout)) != asking)
            failure = $"no answer to initialize within {options.StartTimeout.TotalSeconds:0} s";
        else if ((await asking)["error"] is JsonObject error)
            failure = Str(error["message"]) ?? "initialize failed";
        if (failure is null) return lane;
        lock (gate) lane.Stopping = true;
        await lane.StopAsync();
        throw new JsonRpcException(JsonRpcException.InternalError, $"DeepSeek Harness ({mode.Name.ToLowerInvariant()}) did not start: {failure}");
    }

    /// <summary>
    /// The router's own request to a process, answered to the router alone. A process that is gone,
    /// or cannot be written to, answers with an error.
    /// </summary>
    async Task<JsonObject> RequestAsync(DshLane lane, string method, JsonNode? @params)
    {
        var id = OwnPrefix + Interlocked.Increment(ref next);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (lane.Ended) return Gone(id, lane);
            own[id] = (lane, answer);
        }
        try
        {
            await ToLaneAsync(lane, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            lock (gate) own.Remove(id);
            return Gone(id, lane);
        }
        return await answer.Task;
    }

    static JsonObject Gone(string id, DshLane lane) =>
        Error(id, JsonRpcException.InternalError, $"The {lane.Mode.Name.ToLowerInvariant()} DeepSeek Harness process stopped.");

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
        try { await ToLaneAsync(request.Lane, message); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { } // the process is gone, and its question with it
    }

    // ---------------------------------------------------------------- switching

    /// <summary>
    /// <c>session/set_config_option</c> for <c>mode</c>: the session is closed in its process and resumed in
    /// the other one (DSH's resume brings back its model and effort). A session DSH pinned cannot switch.
    /// Read-only is saved before the switch: a session must never stay full access on disk while the
    /// client shows it read-only. Full access is saved after; a failure there only loses the choice.
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
        var held = new List<DshLane>();
        void Hold(DshLane lane) // under the lock
        {
            lane.Holds++;
            held.Add(lane);
        }
        var closed = false;
        var lowering = target == DshModes.ReadOnly;
        try
        {
            lock (gate) Hold(from);
            await ReserveAsync(target, Hold); // started first: a process that cannot start leaves the session where it is
            if (lowering)
                try
                {
                    modes.Set(sessionId, target);
                }
                catch (IOException e)
                {
                    throw new JsonRpcException(JsonRpcException.InternalError,
                        $"Gorynych could not save this session as read-only ({e.Message}), so it stays in full access.");
                }

            var answer = await RequestAsync(from, AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId });
            if (answer["error"] is JsonObject closeError)
                options.Log?.Invoke($"dsh {sessionId}: close in the {from.Mode.Id} process failed ({Str(closeError["message"])}); DSH lets the session go anyway");
            lock (gate) if (live.GetValueOrDefault(sessionId) == open) live.Remove(sessionId);
            closed = true;

            var resume = new JsonObject { ["sessionId"] = sessionId, ["cwd"] = open.Cwd, ["mcpServers"] = open.McpServers?.DeepClone() ?? new JsonArray() };
            var (resumed, opened, why) = await ResumeInAsync(target, resume, open, Hold);
            if (opened is null)
            {
                // Back where it was, so the session stays usable; the client hears why the switch failed.
                var (_, back, whyNot) = await ResumeInAsync(from.Mode, resume.DeepClone().AsObject(), open, Hold);
                if (back is not null)
                {
                    closed = false;
                    if (lowering) TrySave(sessionId, from.Mode);
                }
                else options.Log?.Invoke($"dsh {sessionId}: could not resume it in the {from.Mode.Id} process either: {whyNot}");
                await ToServiceAsync(Error(id, JsonRpcException.InternalError,
                    $"DeepSeek Harness could not open this session in {target.Name.ToLowerInvariant()} mode: {why}"));
                return;
            }
            closed = false;
            if (!lowering && !TrySave(sessionId, target))
                options.Log?.Invoke($"dsh {sessionId}: full access was not saved; the session opens read-only next time");
            options.Log?.Invoke($"dsh {sessionId}: now {target.Id}");
            await ToServiceAsync(Result(id, Options(opened, target)));
        }
        catch when (closed)
        {
            // Closed in its process and open nowhere: back where it was before the error is answered.
            var resume = new JsonObject { ["sessionId"] = sessionId, ["cwd"] = open.Cwd, ["mcpServers"] = open.McpServers?.DeepClone() ?? new JsonArray() };
            try
            {
                var (_, back, whyNot) = await ResumeInAsync(from.Mode, resume, open, Hold);
                if (back is null) options.Log?.Invoke($"dsh {sessionId}: could not resume it in the {from.Mode.Id} process: {whyNot}");
                else if (lowering) TrySave(sessionId, from.Mode);
            }
            catch (Exception e)
            {
                options.Log?.Invoke($"dsh {sessionId}: could not resume it in the {from.Mode.Id} process: {e.Message}");
            }
            throw;
        }
        finally
        {
            lock (gate) foreach (var lane in held) lane.Holds--;
            foreach (var lane in held.Distinct()) StopIfIdle(lane);
        }
    }

    /// <summary>
    /// A <c>session/resume</c> of the router's own in the process of <paramref name="mode"/>, held for the
    /// switch. On success the session is open there (registered with its answer); otherwise the reason.
    /// </summary>
    async Task<(JsonObject? Answer, Live? Opened, string Why)> ResumeInAsync(DshMode mode, JsonObject resume, Live was, Action<DshLane> hold)
    {
        var lane = await ReserveAsync(mode, hold);
        var answer = await RequestAsync(lane, AcpMethods.Agent.SessionResume, resume);
        if (answer["result"] is not JsonObject result) return (answer, null, Str(answer["error"]?["message"]) ?? "no reason given");
        var opened = Opened(lane, was.Cwd, was.McpServers, null, result);
        lock (gate) live[Str(resume["sessionId"])!] = opened;
        return (answer, opened, "");
    }

    bool TrySave(string sessionId, DshMode mode)
    {
        try
        {
            modes.Set(sessionId, mode);
            return true;
        }
        catch (IOException e)
        {
            options.Log?.Invoke($"dsh {sessionId}: {e.Message}");
            return false;
        }
    }

    static Live Opened(DshLane lane, string cwd, JsonNode? mcpServers, DshPin? pin, JsonObject? result) =>
        new(lane, cwd, mcpServers?.DeepClone(), pin) { ConfigOptions = OwnOptions(result) };

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
            if (lane.Stopping || lane.Holds > 0
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
            if (open is not null && open.Lane != lane) return; // from a process that no longer has the session
            if (open is not null) open.ConfigOptions = OwnOptions(update);
            DshModes.AddTo(update, lane.Mode, open?.Pin);
        }
        await ToServiceAsync(message);
    }

    async Task AnswerFromLaneAsync(DshLane lane, JsonObject message, JsonNode id)
    {
        var key = Key(id);
        var result = message["result"] as JsonObject;
        Pending request;
        Live? open;
        string? forked = null;
        lock (gate)
        {
            if (Str(id) is { } ownId && ownId.StartsWith(OwnPrefix, StringComparison.Ordinal))
            {
                if (own.Remove(ownId, out var mine)) mine.Answer.TrySetResult(message);
                return;
            }
            if (!pending.Remove(key, out request)) request = default;
            var sessionId = request.SessionId;
            // A session that opens is registered together with its request's end, so no stop of an idle process comes in between.
            switch (request.Method)
            {
                case AcpMethods.Agent.SessionNew when result is not null && Str(result["sessionId"]) is { } created:
                    live[created] = Opened(lane, Str(request.Params?["cwd"]) ?? "", request.Params?["mcpServers"], null, result);
                    break;
                case AcpMethods.Agent.SessionResume or AcpMethods.Agent.SessionLoad when result is not null && sessionId is not null:
                    if (!live.ContainsKey(sessionId))
                        live[sessionId] = Opened(lane, Str(request.Params?["cwd"]) ?? "", request.Params?["mcpServers"], request.Pin, result);
                    break;
                case SessionFork when result is not null && Str(result["sessionId"]) is { } fork && sessionId is not null:
                    var parent = live.GetValueOrDefault(sessionId);
                    live[fork] = Opened(lane, Str(request.Params?["cwd"]) ?? parent?.Cwd ?? "", request.Params?["mcpServers"] ?? parent?.McpServers, null, result);
                    forked = fork;
                    break;
                case AcpMethods.Agent.SessionPrompt when sessionId is not null:
                    EndTurn(sessionId);
                    break;
            }
            open = sessionId is null ? null : live.GetValueOrDefault(sessionId);
        }
        switch (request.Method)
        {
            case AcpMethods.Agent.SessionNew or AcpMethods.Agent.SessionResume or AcpMethods.Agent.SessionLoad when result is not null:
                DshModes.AddTo(result, lane.Mode, request.Pin);
                break;
            case SessionFork when result is not null && forked is not null:
                // A fork runs in the process of its parent, so it has its parent's mode.
                if (lane.Mode != DshModes.ReadOnly && !TrySave(forked, lane.Mode))
                    options.Log?.Invoke($"dsh {forked}: full access was not saved; the fork opens read-only next time");
                DshModes.AddTo(result, lane.Mode, null);
                break;
            case AcpMethods.Agent.SessionSetConfigOption when result is not null:
                if (open is not null && open.Lane == lane) open.ConfigOptions = OwnOptions(result);
                DshModes.AddTo(result, lane.Mode, open?.Lane == lane ? open.Pin : null);
                break;
            case AcpMethods.Agent.SessionList when (result?["sessions"]) is JsonArray sessions
                && (request.Params?["cursor"]) is null && (result?["nextCursor"]) is null:
                MergeOthers(sessions, Str(request.Params?["cwd"]));
                break;
        }
        if (request.SessionId is { } ended
            && (request.Method == AcpMethods.Agent.SessionClose
                || (Str(message["error"]?["message"]) ?? "").Contains("unknown session", StringComparison.OrdinalIgnoreCase)))
        {
            // DSH lets go of a session even when its close fails.
            lock (gate)
                if (live.TryGetValue(ended, out var gone) && gone.Lane == lane) live.Remove(ended);
            StopIfIdle(lane);
        }
        await ToServiceAsync(message);
    }

    /// <summary>
    /// DSH lists every session it keeps but those open in its own process: the read-only process's
    /// proxy adds its own, and the full-access process's open sessions are on the read-only list
    /// already, on whichever page. Only a list that fits one page is complete enough to add those
    /// it still leaves out without listing one twice.
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
            lane.Ended = true;
            all.Remove(lane);
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
                o.Answer.TrySetResult(Gone(ownId, lane));
            }
            foreach (var sessionId in live.Where(kv => kv.Value.Lane == lane).Select(kv => kv.Key).ToList())
            {
                live.Remove(sessionId);
                prompting.Remove(sessionId);
                if (lane != main) lost[sessionId] = lane.Mode;
            }
        }
        foreach (var requestId in withdrawn)
            await TryToServiceAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = AcpMethods.Protocol.CancelRequest,
                ["params"] = new JsonObject { ["requestId"] = requestId },
            });
        foreach (var requestId in failed)
            await TryToServiceAsync(Error(requestId, JsonRpcException.InternalError, $"The {lane.Mode.Name.ToLowerInvariant()} DeepSeek Harness process stopped."));
        if (lane != main) return;
        try
        {
            await StopAsync();
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

    async Task TryToServiceAsync(JsonObject message)
    {
        try { await ToServiceAsync(message); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

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
