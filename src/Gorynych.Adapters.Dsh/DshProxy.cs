using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;

namespace Gorynych.Adapters.Dsh;

/// <summary>
/// Relays ACP between the service and <c>dsh --profile acp</c> line by line, keeping every ID and
/// field, and rewrites only what DSH's ACP lacks (docs/adapters.md, "DeepSeek Harness"):
/// <list type="bullet">
/// <item>its <c>initialize</c> result gets Gorynych's flags;</item>
/// <item><c>session/list</c> gets back the sessions open in DSH, which DSH leaves out of its own list;</item>
/// <item>tool calls get a kind, a title, locations and diffs;</item>
/// <item>thoughts get IDs of their own;</item>
/// <item>permission requests get their tool call, and a shell command's sandbox escalation is refused without asking;</item>
/// <item>a request DSH drops on a cancel is withdrawn from the service;</item>
/// <item>the one-writer error of <c>session/resume</c> says what to do.</item>
/// </list>
/// </summary>
internal sealed class DshProxy
{
    /// <summary>What a resume of a session open in another DSH process (<c>dsh web</c>) answers.</summary>
    public const string OpenElsewhere =
        "This DeepSeek Harness session is open in another DSH app (dsh web or another dsh); close it there, then try again.";

    static readonly JsonSerializerOptions Wire = AcpJson.TextOptions;

    readonly DshAdapterOptions options;
    readonly Stream fromService, toService, fromDsh, toDsh;
    readonly SemaphoreSlim serviceGate = new(1, 1), dshGate = new(1, 1);
    readonly Lock gate = new();
    /// <summary>The service's requests whose answers are rewritten, by ID.</summary>
    readonly Dictionary<string, (string Method, JsonNode? Params)> asked = [];
    /// <summary>The sessions open in DSH through this proxy, with their folders.</summary>
    readonly Dictionary<string, string> live = [];
    /// <summary>Tool calls announced and not yet ended, by session and call ID.</summary>
    readonly Dictionary<(string Session, string Call), DshTool> tools = [];
    /// <summary>DSH's permission requests the service has not answered yet, by ID.</summary>
    readonly Dictionary<string, (JsonNode Id, string Session, string? Call)> questions = [];
    /// <summary>Requests withdrawn from the service, whose late answers are dropped.</summary>
    readonly HashSet<string> withdrawn = [];
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
                if (Parse(line) is not JsonObject message) { await WriteAsync(toDsh, dshGate, line); continue; }
                var id = message["id"];
                if (message["method"] is JsonValue m && m.TryGetValue<string>(out var method))
                {
                    if (id is not null && method is AcpMethods.Agent.Initialize or AcpMethods.Agent.SessionNew or AcpMethods.Agent.SessionResume
                        or AcpMethods.Agent.SessionClose or AcpMethods.Agent.SessionList or AcpMethods.Agent.SessionPrompt)
                        lock (gate) asked[Key(id)] = (method, message["params"]?.DeepClone());
                }
                else if (id is not null)
                {
                    // An answer to one of DSH's requests: one the proxy withdrew is DSH's no longer.
                    lock (gate)
                    {
                        if (withdrawn.Remove(Key(id))) continue;
                        questions.Remove(Key(id));
                    }
                }
                await WriteAsync(toDsh, dshGate, line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            // The service let go: DSH's stdin ends, and DSH exits.
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
                if (Parse(line) is not JsonObject message) { await WriteAsync(toService, serviceGate, line); continue; }
                var id = message["id"];
                var method = (string?)(message["method"] as JsonValue);
                if (method is null && id is not null) await AnswerAsync(message, id);
                else if (method == AcpMethods.Client.SessionUpdate) await UpdateAsync(message);
                else if (method == AcpMethods.Client.SessionRequestPermission && id is not null) await AskAsync(message, id);
                else await WriteAsync(toService, serviceGate, message);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            try { await toService.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            done.TrySetResult();
        }
    }

    /// <summary>DSH's answer to a service request, rewritten where the method needs it.</summary>
    async Task AnswerAsync(JsonObject message, JsonNode id)
    {
        (string Method, JsonNode? Params) request;
        lock (gate)
            if (!asked.Remove(Key(id), out request)) request = default;
        var result = message["result"] as JsonObject;
        var sessionId = (string?)request.Params?["sessionId"];
        switch (request.Method)
        {
            case AcpMethods.Agent.Initialize when result is not null:
                if (result["agentCapabilities"] is not JsonObject caps) result["agentCapabilities"] = caps = [];
                if (caps["_meta"] is not JsonObject meta) caps["_meta"] = meta = [];
                meta[GorynychProtocol.CapUnloadAfter] = (int)options.UnloadAfter.TotalSeconds;
                meta[GorynychProtocol.CapUnaskedMcpTools] = true;
                break;
            case AcpMethods.Agent.SessionNew when ((string?)result?["sessionId"]) is { } created:
                lock (gate) live[created] = (string?)request.Params?["cwd"] ?? "";
                break;
            case AcpMethods.Agent.SessionResume when result is not null && sessionId is not null:
                lock (gate) live[sessionId] = (string?)request.Params?["cwd"] ?? "";
                break;
            case AcpMethods.Agent.SessionResume when message["error"] is JsonObject error
                && ((string?)error["data"]?["details"] ?? "").Contains("already owned by an active write handle", StringComparison.Ordinal):
                error["message"] = OpenElsewhere;
                break;
            case AcpMethods.Agent.SessionClose when result is not null && sessionId is not null:
                await EndSessionAsync(sessionId);
                break;
            case AcpMethods.Agent.SessionList when (result?["sessions"]) is JsonArray sessions && (request.Params?["cursor"]) is null:
                MergeLive(sessions, (string?)request.Params?["cwd"]);
                break;
            case AcpMethods.Agent.SessionPrompt when sessionId is not null:
                // The prompt is over, so no question of its turn is still DSH's.
                await WithdrawAsync(q => q.Session == sessionId);
                break;
        }
        await WriteAsync(toService, serviceGate, message);
    }

    /// <summary>
    /// DSH leaves the sessions open in its own process out of <c>session/list</c>; the service would
    /// take them for gone. They are added to the first page, those in the request's folder if it names one.
    /// </summary>
    void MergeLive(JsonArray sessions, string? cwd)
    {
        var listed = sessions.Select(s => (string?)s?["sessionId"]).OfType<string>().ToHashSet();
        List<KeyValuePair<string, string>> open;
        lock (gate) open = [.. live];
        var at = 0;
        foreach (var (id, folder) in open)
        {
            if (listed.Contains(id) || (cwd is not null && !SameFolder(folder, cwd))) continue;
            sessions.Insert(at++, new JsonObject { ["sessionId"] = id, ["cwd"] = folder });
        }
    }

    async Task EndSessionAsync(string sessionId)
    {
        lock (gate)
        {
            live.Remove(sessionId);
            foreach (var key in tools.Keys.Where(k => k.Session == sessionId).ToList()) tools.Remove(key);
        }
        await WithdrawAsync(q => q.Session == sessionId);
    }

    /// <summary>A <c>session/update</c>: tool calls are recorded and completed, thoughts get IDs of their own.</summary>
    async Task UpdateAsync(JsonObject message)
    {
        var sessionId = (string?)message["params"]?["sessionId"];
        if (message["params"]?["update"] is JsonObject update && sessionId is not null)
        {
            switch ((string?)update["sessionUpdate"])
            {
                case "agent_thought_chunk" when (string?)update["messageId"] is { Length: > 0 } messageId:
                    // DSH gives a thought and its answer one ID; in ACP they are two messages.
                    update["messageId"] = messageId + ":thought";
                    break;
                case "tool_call" when (string?)update["toolCallId"] is { } callId && (string?)update["title"] is { } name:
                    string? cwd;
                    lock (gate) cwd = live.GetValueOrDefault(sessionId) is { Length: > 0 } folder ? folder : null;
                    var tool = new DshTool(sessionId, callId, name, update["rawInput"]?.DeepClone(), cwd);
                    lock (gate) tools[(sessionId, callId)] = tool;
                    Describe(update, tool);
                    break;
                case "tool_call_update" when (string?)update["toolCallId"] is { } callId && (string?)update["status"] is "completed" or "failed":
                    lock (gate) tools.Remove((sessionId, callId));
                    // DSH ends a call whose question it dropped (a cancel) without withdrawing the question.
                    await WithdrawAsync(q => q.Session == sessionId && q.Call == callId);
                    break;
            }
        }
        await WriteAsync(toService, serviceGate, message);
    }

    /// <summary>
    /// Adds what DSH leaves out of a tool call: a kind (where DSH says only <c>other</c>), locations and a
    /// diff (where DSH sent none), and a readable title in place of the bare tool name.
    /// </summary>
    static void Describe(JsonObject call, DshTool tool)
    {
        if (call["kind"] is null || (string?)call["kind"] == "other") call["kind"] = DshMap.KindOf(tool.Name);
        call["title"] = DshMap.TitleOf(tool.Name, tool.Input);
        if (call["locations"] is null && DshMap.LocationsOf(tool.Name, tool.Input, tool.Cwd) is { } locations) call["locations"] = locations;
        if (call["content"] is null && DshMap.DiffOf(tool.Name, tool.Input, tool.Cwd) is { } diff) call["content"] = new JsonArray(diff);
    }

    /// <summary>
    /// A permission request, which in DSH is always a sandbox escalation. A shell command's is refused
    /// here, so the workspace's ACL never changes; any other goes to the person with its tool call.
    /// </summary>
    async Task AskAsync(JsonObject message, JsonNode id)
    {
        var request = message["params"] as JsonObject;
        var sessionId = (string?)request?["sessionId"];
        var callId = (string?)request?["toolCall"]?["toolCallId"];
        DshTool? tool = null;
        if (sessionId is not null && callId is not null)
            lock (gate) tool = tools.GetValueOrDefault((sessionId, callId));
        if (DshMap.Refused(tool))
        {
            var reject = request?["options"]?.AsArray().FirstOrDefault(o => (string?)o?["kind"] == "reject_once")?["optionId"]?.GetValue<string>() ?? "reject-once";
            options.Log?.Invoke($"dsh {sessionId}: refused the sandbox escalation of {tool?.Name ?? "an unknown call"} {callId} without asking (Gorynych's read-only shell)");
            await WriteAsync(toDsh, dshGate, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["result"] = new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = reject } },
            });
            return;
        }
        if (request?["toolCall"] is JsonObject call && tool is not null)
        {
            Describe(call, tool);
            if (call["rawInput"] is null && tool.Input is not null) call["rawInput"] = tool.Input.DeepClone();
            if (DshMap.Escalation(tool.Input) is { } asks)
            {
                if (call["content"] is not JsonArray content) call["content"] = content = [];
                content.Insert(0, new JsonObject { ["type"] = "content", ["content"] = new JsonObject { ["type"] = "text", ["text"] = asks } });
            }
        }
        if (sessionId is not null)
            lock (gate) questions[Key(id)] = (id.DeepClone(), sessionId, callId);
        await WriteAsync(toService, serviceGate, message);
    }

    /// <summary>Withdraws from the service the open questions that match, and drops their late answers.</summary>
    async Task WithdrawAsync(Func<(JsonNode Id, string Session, string? Call), bool> which)
    {
        List<JsonNode> ids;
        lock (gate)
        {
            ids = [];
            foreach (var (key, q) in questions.Where(kv => which(kv.Value)).ToList())
            {
                questions.Remove(key);
                withdrawn.Add(key);
                ids.Add(q.Id);
            }
        }
        foreach (var requestId in ids)
            await WriteAsync(toService, serviceGate, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = AcpMethods.Protocol.CancelRequest,
                ["params"] = new JsonObject { ["requestId"] = requestId.DeepClone() },
            });
    }

    // ---------------------------------------------------------------- framing

    static JsonNode? Parse(string line)
    {
        try { return JsonNode.Parse(line); }
        catch (JsonException) { return null; }
    }

    static string Key(JsonNode id) => id.ToJsonString(Wire);

    static Task WriteAsync(Stream stream, SemaphoreSlim writeGate, JsonObject message) => WriteAsync(stream, writeGate, message.ToJsonString(Wire));

    static async Task WriteAsync(Stream stream, SemaphoreSlim writeGate, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await writeGate.WaitAsync();
        try
        {
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
        finally
        {
            writeGate.Release();
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
