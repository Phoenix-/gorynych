using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using static Gorynych.Adapters.Dsh.DshLines;

namespace Gorynych.Adapters.Dsh;

/// <summary>
/// Relays ACP between the service and <c>dsh --profile acp</c> line by line, keeping every ID and
/// field, and rewrites only what DSH's ACP lacks (docs/adapters.md, "DeepSeek Harness"):
/// <list type="bullet">
/// <item>its <c>initialize</c> result gets Gorynych's flags;</item>
/// <item><c>session/list</c> gets back the sessions open in DSH, which DSH leaves out of its own list;</item>
/// <item>tool calls get a kind, a title, locations and diffs;</item>
/// <item>thoughts get IDs of their own;</item>
/// <item>permission requests get their tool call, and only a file tool's sandbox escalation reaches the person;</item>
/// <item>a request DSH drops on a cancel is withdrawn from the service;</item>
/// <item>the one-writer error of <c>session/resume</c> says what to do.</item>
/// </list>
/// A message the proxy cannot handle is logged and fails closed: a permission request is refused,
/// another request is answered with an error, a notification is dropped, an answer passes unchanged.
/// </summary>
internal sealed class DshProxy
{
    /// <summary>What a resume of a session open in another DSH process (<c>dsh web</c>) answers.</summary>
    public const string OpenElsewhere =
        "This DeepSeek Harness session is open in another DSH app (dsh web or another dsh); close it there, then try again.";

    /// <summary>The service's requests whose answers the proxy reads, kept as little as it needs.</summary>
    readonly record struct Asked(string Method, string? SessionId, string? Cwd, bool Cursor);

    /// <summary>A permission request of DSH's forwarded to the service and not answered yet.</summary>
    readonly record struct Question(JsonNode Id, string Session, string? Call);

    readonly DshAdapterOptions options;
    readonly Stream fromService, toService, fromDsh, toDsh;
    readonly SemaphoreSlim serviceGate = new(1, 1);
    /// <summary>
    /// Everything for DSH's stdin goes through one queue and one writer, so neither reading loop
    /// ever waits on DSH reading its input.
    /// </summary>
    readonly Channel<string> toDshQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly Lock gate = new();
    readonly Dictionary<string, Asked> asked = [];
    /// <summary>The sessions open in DSH through this proxy, with their folders.</summary>
    readonly Dictionary<string, string> live = [];
    /// <summary>Tool calls announced and not yet ended, by session and call ID.</summary>
    readonly Dictionary<(string Session, string Call), DshTool> tools = [];
    readonly Dictionary<string, Question> questions = [];
    /// <summary>Requests withdrawn from the service, by ID, with their session: their late answers are dropped.</summary>
    readonly Dictionary<string, string> withdrawn = [];
    readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DshProxy(DshAdapterOptions options, (Stream Input, Stream Output) service, AgentInstance dsh)
    {
        this.options = options;
        (fromService, toService) = service;
        fromDsh = dsh.Input;
        toDsh = dsh.Output;
    }

    /// <summary>Completes when DSH's output ends and everything from it has reached the service.</summary>
    public Task Completion => done.Task;

    public void Start()
    {
        _ = Task.Run(FromServiceAsync);
        _ = Task.Run(FromDshAsync);
        _ = Task.Run(ToDshAsync);
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
                    if (!FromService(line)) continue;
                }
                catch (Exception e) when (e is not (IOException or ObjectDisposedException or OperationCanceledException))
                {
                    options.Log?.Invoke($"dsh: passed on a message from the service unread ({e.Message}): {Shorten(line)}");
                }
                ToDsh(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            // The service let go: DSH's stdin ends once the queue is written, and DSH exits.
            toDshQueue.Writer.TryComplete();
        }
    }

    /// <summary>Notes what the proxy needs from a service message; false drops it (a withdrawn request's late answer).</summary>
    bool FromService(string line)
    {
        if (Parse(line) is not JsonObject message) return true;
        var id = message["id"];
        if (Str(message["method"]) is { } method)
        {
            if (id is not null && method is AcpMethods.Agent.Initialize or AcpMethods.Agent.SessionNew or AcpMethods.Agent.SessionResume
                or AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionClose or AcpMethods.Agent.SessionList
                or AcpMethods.Agent.SessionPrompt or AcpMethods.Agent.SessionSetConfigOption)
            {
                var p = message["params"];
                lock (gate) asked[Key(id)] = new Asked(method, Str(p?["sessionId"]), Str(p?["cwd"]), p?["cursor"] is not null);
            }
        }
        else if (id is not null)
        {
            // An answer to one of DSH's requests: one the proxy withdrew is DSH's no longer.
            lock (gate)
            {
                if (withdrawn.Remove(Key(id))) return false;
                questions.Remove(Key(id));
            }
        }
        return true;
    }

    void ToDsh(string line) => toDshQueue.Writer.TryWrite(line);

    void ToDsh(JsonObject message) => ToDsh(message.ToJsonString(Wire));

    async Task ToDshAsync()
    {
        try
        {
            await foreach (var line in toDshQueue.Reader.ReadAllAsync())
            {
                await toDsh.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
                await toDsh.FlushAsync();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            try { await toDsh.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    // ---------------------------------------------------------------- DSH -> service

    async Task FromDshAsync()
    {
        try
        {
            using var reader = new StreamReader(fromDsh, Encoding.UTF8);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                if (Parse(line) is not JsonObject message) { await ToServiceAsync(line); continue; }
                try
                {
                    await FromDshAsync(message);
                }
                catch (Exception e) when (e is not (IOException or ObjectDisposedException or OperationCanceledException))
                {
                    await FailClosedAsync(message, line, e);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            lock (gate) live.Clear();
            try { await toService.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            done.TrySetResult();
        }
    }

    async Task FromDshAsync(JsonObject message)
    {
        var id = message["id"];
        var method = Str(message["method"]);
        if (message["method"] is null && id is not null) await AnswerAsync(message, id);
        else if (method == AcpMethods.Client.SessionUpdate) await UpdateAsync(message);
        else if (method == AcpMethods.Client.SessionRequestPermission && id is not null) await AskAsync(message, id);
        else if (method is null && message["method"] is not null) throw new FormatException("its method is not a string");
        else await ToServiceAsync(message);
    }

    /// <summary>
    /// A message from DSH the proxy could not handle: a permission request is refused, so no
    /// escalation it cannot read reaches anyone; another request is answered with an error; an
    /// answer passes unchanged, so the service's request does not hang; a notification is dropped.
    /// </summary>
    async Task FailClosedAsync(JsonObject message, string line, Exception e)
    {
        options.Log?.Invoke($"dsh: could not read a message from DSH ({e.Message}); handled it as malformed: {Shorten(line)}");
        var id = message["id"];
        if (id is null or JsonObject or JsonArray) return; // a notification, or nothing anyone waits for
        if (message["method"] is null)
        {
            await ToServiceAsync(line); // an answer: the service's request must not hang
            return;
        }
        if (Str(message["method"]) == AcpMethods.Client.SessionRequestPermission)
            ToDsh(Reject(id));
        else
            ToDsh(Error(id, -32603, "Gorynych could not read this request."));
    }

    /// <summary>DSH's answer to a service request, rewritten where the method needs it.</summary>
    async Task AnswerAsync(JsonObject message, JsonNode id)
    {
        Asked request;
        lock (gate)
            if (!asked.Remove(Key(id), out request)) request = default;
        var result = message["result"] as JsonObject;
        var error = message["error"] as JsonObject;
        var sessionId = request.SessionId;
        switch (request.Method)
        {
            case AcpMethods.Agent.Initialize when result is not null:
                if (result["agentCapabilities"] is not JsonObject caps) result["agentCapabilities"] = caps = [];
                if (caps["_meta"] is not JsonObject meta) caps["_meta"] = meta = [];
                meta[GorynychProtocol.CapUnloadAfter] = (int)options.UnloadAfter.TotalSeconds;
                break;
            case AcpMethods.Agent.SessionNew when Str(result?["sessionId"]) is { } created:
                lock (gate) live[created] = request.Cwd ?? "";
                break;
            case AcpMethods.Agent.SessionResume or AcpMethods.Agent.SessionLoad when result is not null && sessionId is not null:
                lock (gate) live[sessionId] = request.Cwd ?? "";
                break;
            case AcpMethods.Agent.SessionResume when (Str(error?["data"]?["details"]) ?? "").Contains("already owned by an active write handle", StringComparison.Ordinal):
                error!["message"] = OpenElsewhere;
                break;
            case AcpMethods.Agent.SessionClose when sessionId is not null:
                // DSH lets go of the session even when its close fails.
                await EndSessionAsync(sessionId);
                break;
            case AcpMethods.Agent.SessionList when !request.Cursor && (result?["sessions"]) is JsonArray sessions:
                MergeLive(sessions, request.Cwd);
                break;
            case AcpMethods.Agent.SessionPrompt when sessionId is not null:
                // The prompt is over, so no question of its turn is still DSH's.
                await WithdrawAsync(q => q.Session == sessionId);
                break;
        }
        // A session DSH says it does not have is not open in it, whatever the proxy noted.
        if (sessionId is not null && (Str(error?["message"]) ?? "").Contains("unknown session", StringComparison.OrdinalIgnoreCase))
            await EndSessionAsync(sessionId);
        await ToServiceAsync(message);
    }

    /// <summary>
    /// DSH leaves the sessions open in its own process out of <c>session/list</c>; the service would
    /// take them for gone. They are added to the first page, those in the request's folder if it names one.
    /// </summary>
    void MergeLive(JsonArray sessions, string? cwd)
    {
        var listed = sessions.Select(s => Str(s?["sessionId"])).OfType<string>().ToHashSet();
        List<KeyValuePair<string, string>> open;
        lock (gate) open = [.. live];
        var at = 0;
        foreach (var (id, folder) in open)
        {
            if (listed.Contains(id) || (cwd is not null && !SameFolder(folder, cwd))) continue;
            sessions.Insert(at++, new JsonObject { ["sessionId"] = id, ["cwd"] = folder });
        }
    }

    /// <summary>The session is no longer open in DSH: its calls, questions and late answers are forgotten.</summary>
    async Task EndSessionAsync(string sessionId)
    {
        lock (gate)
        {
            live.Remove(sessionId);
            foreach (var key in tools.Keys.Where(k => k.Session == sessionId).ToList()) tools.Remove(key);
            foreach (var key in withdrawn.Where(kv => kv.Value == sessionId).Select(kv => kv.Key).ToList()) withdrawn.Remove(key);
        }
        await WithdrawAsync(q => q.Session == sessionId);
    }

    /// <summary>A <c>session/update</c>: tool calls are recorded and completed, thoughts get IDs of their own.</summary>
    async Task UpdateAsync(JsonObject message)
    {
        var sessionId = Str(message["params"]?["sessionId"]);
        if (message["params"]?["update"] is JsonObject update && sessionId is not null)
        {
            switch (Str(update["sessionUpdate"]))
            {
                case "agent_thought_chunk" when Str(update["messageId"]) is { Length: > 0 } messageId:
                    // DSH gives a thought and its answer one ID; in ACP they are two messages.
                    update["messageId"] = messageId + ":thought";
                    break;
                case "tool_call" when Str(update["toolCallId"]) is { } callId && Str(update["title"]) is { } name:
                    string? cwd;
                    lock (gate) cwd = live.GetValueOrDefault(sessionId) is { Length: > 0 } folder ? folder : null;
                    var tool = new DshTool(sessionId, callId, name, update["rawInput"]?.DeepClone(), cwd);
                    lock (gate) tools[(sessionId, callId)] = tool;
                    Describe(update, tool);
                    break;
                case "tool_call_update" when Str(update["toolCallId"]) is { } callId && Str(update["status"]) is "completed" or "failed":
                    lock (gate) tools.Remove((sessionId, callId));
                    // DSH ends a call whose question it dropped (a cancel) without withdrawing the question.
                    await WithdrawAsync(q => q.Session == sessionId && q.Call == callId);
                    break;
            }
        }
        await ToServiceAsync(message);
    }

    /// <summary>
    /// Adds what DSH leaves out of a tool call: a kind (where DSH says only <c>other</c>), locations and a
    /// diff (where DSH sent none), and a readable title in place of the bare tool name.
    /// </summary>
    static void Describe(JsonObject call, DshTool tool)
    {
        if (call["kind"] is null || Str(call["kind"]) == "other") call["kind"] = DshMap.KindOf(tool.Name);
        call["title"] = DshMap.TitleOf(tool.Name, tool.Input);
        if (call["locations"] is null && DshMap.LocationsOf(tool.Name, tool.Input, tool.Cwd) is { } locations) call["locations"] = locations;
        if (call["content"] is null && DshMap.DiffOf(tool.Name, tool.Input, tool.Cwd) is { } diff) call["content"] = new JsonArray(diff);
    }

    /// <summary>
    /// A permission request, which in DSH is always a sandbox escalation. Only a running file tool
    /// call's goes to the person, with its tool call; everything else is refused here, so the
    /// workspace's ACL never changes (<see cref="DshMap.Refused"/>).
    /// </summary>
    async Task AskAsync(JsonObject message, JsonNode id)
    {
        var request = message["params"] as JsonObject;
        var sessionId = Str(request?["sessionId"]);
        var callId = Str(request?["toolCall"]?["toolCallId"]);
        DshTool? tool = null;
        if (sessionId is not null && callId is not null)
            lock (gate) tool = tools.GetValueOrDefault((sessionId, callId));
        if (DshMap.Refused(tool) || request?["options"] is not JsonArray)
        {
            options.Log?.Invoke($"dsh {sessionId}: refused the sandbox escalation of {tool?.Name ?? "an unknown call"} {callId} without asking");
            ToDsh(Reject(id, request?["options"] as JsonArray));
            return;
        }
        if (request["toolCall"] is JsonObject call && tool is not null)
        {
            Describe(call, tool);
            if (call["rawInput"] is null && tool.Input is not null) call["rawInput"] = tool.Input.DeepClone();
            if (DshMap.Escalation(tool.Input) is { } asks)
            {
                if (call["content"] is not JsonArray content) call["content"] = content = [];
                content.Insert(0, new JsonObject { ["type"] = "content", ["content"] = new JsonObject { ["type"] = "text", ["text"] = asks } });
            }
        }
        lock (gate) questions[Key(id)] = new Question(id.DeepClone(), sessionId!, callId);
        await ToServiceAsync(message);
    }

    /// <summary>The answer that refuses a permission request: its <c>reject_once</c> option, or DSH's <c>reject-once</c>.</summary>
    static JsonObject Reject(JsonNode id, JsonArray? options = null)
    {
        var reject = options?.OfType<JsonObject>().FirstOrDefault(o => Str(o["kind"]) == "reject_once") is { } option
            && Str(option["optionId"]) is { } optionId ? optionId : "reject-once";
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = reject } },
        };
    }

    /// <summary>Withdraws from the service the open questions that match, and drops their late answers.</summary>
    async Task WithdrawAsync(Func<Question, bool> which)
    {
        List<JsonNode> ids;
        lock (gate)
        {
            ids = [];
            foreach (var (key, q) in questions.Where(kv => which(kv.Value)).ToList())
            {
                questions.Remove(key);
                withdrawn[key] = q.Session;
                ids.Add(q.Id);
            }
        }
        foreach (var requestId in ids)
            await ToServiceAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = AcpMethods.Protocol.CancelRequest,
                ["params"] = new JsonObject { ["requestId"] = requestId.DeepClone() },
            });
    }

    // ---------------------------------------------------------------- framing


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

    /// <summary>DSH compares folders by their real path; here by the full path, ignoring case where the file system does.</summary>
    internal static bool SameFolder(string a, string b)
    {
        static string Norm(string p)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
        }
        return string.Equals(Norm(a), Norm(b), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
