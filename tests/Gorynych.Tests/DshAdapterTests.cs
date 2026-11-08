using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Dsh;

namespace Gorynych.Tests;

/// <summary>
/// A scripted <c>dsh --profile acp</c>, as DSH 0.2.2-rc.1 answers (research/dsh-adapter-spec.md):
/// a session is durable at once, <c>session/list</c> leaves out the sessions open in this process and
/// gives only <c>sessionId</c> and <c>cwd</c>, a session has one writer, and permission requests name
/// the tool call by ID alone.
/// </summary>
sealed class FakeDsh
{
    readonly Dictionary<string, string> cwds = [];
    readonly HashSet<string> open = [];
    int next;

    public JsonRpcConnection Rpc { get; private set; } = null!;
    public string? PatchPath { get; private set; }
    public List<(string Method, JsonNode? Params)> Calls { get; } = [];

    /// <summary>Runs a prompt: sends updates and requests through <see cref="Rpc"/>; the prompt ends when it returns.</summary>
    public Func<string, Task>? OnPrompt { get; set; }

    /// <summary>Sessions another DSH process (dsh web) holds.</summary>
    public HashSet<string> OwnedElsewhere { get; } = [];

    public Task<AgentInstance> LaunchAsync(string patch, CancellationToken ct)
    {
        PatchPath = patch;
        var link = new DuplexLink();
        var (ai, ao) = link.AgentEnd;
        Rpc = new JsonRpcConnection(ai, ao, "fake-dsh");
        Rpc.RequestHandler = HandleAsync;
        Rpc.NotificationHandler = (method, p) => { lock (Calls) Calls.Add((method, p?.DeepClone())); return Task.CompletedTask; };
        Rpc.Start();
        var (ci, co) = link.ClientEnd;
        return Task.FromResult(new AgentInstance(ci, co, Rpc.Completion, () => Rpc.DisposeAsync()));
    }

    async Task<JsonNode?> HandleAsync(JsonRpcRequest req, CancellationToken ct)
    {
        lock (Calls) Calls.Add((req.Method, req.Params?.DeepClone()));
        var sessionId = (string?)req.Params?["sessionId"];
        switch (req.Method)
        {
            case AcpMethods.Agent.Initialize:
                return JsonNode.Parse("""
                    {"protocolVersion":1,"agentInfo":{"name":"deepseek-harness-acp","version":"0.0.1"},
                     "agentCapabilities":{"mcpCapabilities":{"http":true},"promptCapabilities":{"image":false,"audio":false,"embeddedContext":false},
                       "sessionCapabilities":{"close":{},"list":{},"resume":{}}},"authMethods":[]}
                    """);
            case AcpMethods.Agent.SessionNew:
                var id = $"s-{Interlocked.Increment(ref next)}";
                lock (cwds) { cwds[id] = (string)req.Params!["cwd"]!; open.Add(id); }
                return new JsonObject { ["sessionId"] = id, ["configOptions"] = new JsonArray() };
            case AcpMethods.Agent.SessionList:
                var filter = (string?)req.Params?["cwd"];
                lock (cwds)
                    return new JsonObject
                    {
                        ["sessions"] = new JsonArray(cwds.Where(kv => !open.Contains(kv.Key) && (filter is null || kv.Value == filter))
                            .Select(kv => (JsonNode)new JsonObject { ["sessionId"] = kv.Key, ["cwd"] = kv.Value }).ToArray()),
                    };
            case AcpMethods.Agent.SessionResume:
                if (OwnedElsewhere.Contains(sessionId!))
                    throw new JsonRpcException(JsonRpcException.InternalError, "Internal error",
                        new JsonObject { ["details"] = $"session \"{sessionId}\" is already owned by an active write handle" });
                lock (cwds) open.Add(sessionId!);
                return new JsonObject { ["configOptions"] = new JsonArray() };
            case AcpMethods.Agent.SessionClose:
                lock (cwds) open.Remove(sessionId!);
                return new JsonObject();
            case AcpMethods.Agent.SessionPrompt:
                if (OnPrompt is { } run) await run(sessionId!);
                return new JsonObject { ["stopReason"] = "end_turn" };
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"\"Method not found\": {req.Method}");
        }
    }

    public Task Update(string sessionId, string update) =>
        Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject { ["sessionId"] = sessionId, ["update"] = JsonNode.Parse(update) });

    /// <summary>A permission request as DSH sends it: the tool call by ID alone, allow once or reject.</summary>
    public Task<Task<JsonNode?>> AskAsync(string sessionId, string toolCallId) =>
        Rpc.StartRequestAsync(AcpMethods.Client.SessionRequestPermission, JsonNode.Parse($$"""
            {"sessionId":"{{sessionId}}","toolCall":{"toolCallId":"{{toolCallId}}"},
             "options":[{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"reject-once","name":"Reject","kind":"reject_once"}]}
            """));
}

/// <summary>The DSH adapter's proxy against a scripted DSH (docs/adapters.md, "DeepSeek Harness").</summary>
[TestClass]
public sealed class DshAdapterTests
{
    static readonly string Work = Path.Combine(Path.GetTempPath(), "dsh-work");
    static readonly string Other = Path.Combine(Path.GetTempPath(), "dsh-other");

    sealed class Service : IAsyncDisposable
    {
        public required AgentInstance Instance { get; init; }
        public required JsonRpcConnection Rpc { get; init; }
        public List<JsonObject> Updates { get; } = [];
        public List<JsonObject> Questions { get; } = [];
        public int Withdrawn;
        /// <summary>Answers a question; by default holds it until withdrawn.</summary>
        public Func<JsonObject, CancellationToken, Task<JsonNode?>> Answer { get; set; } = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

        public async ValueTask DisposeAsync()
        {
            await Instance.DisposeAsync();
            await Rpc.DisposeAsync();
        }
    }

    static async Task<(Service Service, FakeDsh Dsh, string Data)> StartAsync(string? instructions = "Peer instructions.")
    {
        var dsh = new FakeDsh();
        var data = TestDirs.New();
        var adapter = new DshAdapter(new DshAdapterOptions { DataDirectory = data, Instructions = instructions, Launch = dsh.LaunchAsync });
        var instance = await adapter.StartAsync(CancellationToken.None);
        var rpc = new JsonRpcConnection(instance.Input, instance.Output, "service");
        var service = new Service { Instance = instance, Rpc = rpc };
        rpc.NotificationHandler = (method, p) =>
        {
            if (method == AcpMethods.Client.SessionUpdate && p?["update"] is JsonObject u) lock (service.Updates) service.Updates.Add(u);
            return Task.CompletedTask;
        };
        rpc.RequestHandler = async (req, ct) =>
        {
            lock (service.Questions) service.Questions.Add(req.Params!.DeepClone().AsObject());
            await using var _ = ct.Register(() => Interlocked.Increment(ref service.Withdrawn));
            return await service.Answer(req.Params!.AsObject(), ct);
        };
        rpc.Start();
        await rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 });
        return (service, dsh, data);
    }

    /// <summary>How long a step may take before the test fails instead of hanging.</summary>
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    static async Task<string> NewAsync(Service service, string cwd) =>
        (string)(await service.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = cwd, ["mcpServers"] = new JsonArray() }).WaitAsync(Bound))!["sessionId"]!;

    static Task<JsonNode?> PromptAsync(Service service, string sessionId) =>
        service.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "go" }),
        }).WaitAsync(TimeSpan.FromSeconds(20));

    static async Task<string[]> ListAsync(Service service, JsonObject? request = null)
    {
        var listed = await service.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, request ?? []);
        return listed!["sessions"]!.AsArray().Select(s => (string)s!["sessionId"]!).ToArray();
    }

    static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 250; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    JsonObject Last(Service service, string toolCallId)
    {
        lock (service.Updates) return service.Updates.Last(u => (string?)u["toolCallId"] == toolCallId);
    }

    [TestMethod]
    public async Task Initialize_KeepsDshsCapabilities_AndAddsGorynychsFlags()
    {
        var dsh = new FakeDsh();
        var adapter = new DshAdapter(new DshAdapterOptions { DataDirectory = TestDirs.New(), Launch = dsh.LaunchAsync });
        await using var instance = await adapter.StartAsync(CancellationToken.None);
        await using var rpc = new JsonRpcConnection(instance.Input, instance.Output, "service");
        rpc.Start();

        var init = await rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 });

        var caps = init!["agentCapabilities"]!;
        Assert.IsNotNull(caps["sessionCapabilities"]!["resume"]);
        Assert.AreEqual(120, (int)caps["_meta"]![GorynychProtocol.CapUnloadAfter]!);
        Assert.IsTrue((bool)caps["_meta"]![GorynychProtocol.CapUnaskedMcpTools]!);
        Assert.AreEqual("dsh", adapter.Name);
    }

    [TestMethod]
    public async Task List_GetsBackTheSessionsOpenInDsh_InTheRequestedFolder_OnTheFirstPageOnly()
    {
        var (service, _, _) = await StartAsync();
        await using var _ = service;
        var a = await NewAsync(service, Work);
        var b = await NewAsync(service, Other);

        CollectionAssert.AreEquivalent(new[] { a, b }, await ListAsync(service), "DSH lists neither while they are open");
        CollectionAssert.AreEqual(new[] { a }, await ListAsync(service, new JsonObject { ["cwd"] = Work + Path.DirectorySeparatorChar }));
        CollectionAssert.AreEqual(Array.Empty<string>(), await ListAsync(service, new JsonObject { ["cursor"] = "next" }));

        await service.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = a });
        var after = await ListAsync(service);
        CollectionAssert.AreEquivalent(new[] { a, b }, after, "DSH lists a closed session itself, once");
    }

    [TestMethod]
    public async Task ToolCalls_GetKindsTitlesLocationsAndDiffs_AndThoughtsIdsOfTheirOwn()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        dsh.OnPrompt = async s =>
        {
            await dsh.Update(s, """{"sessionUpdate":"agent_thought_chunk","messageId":"m1","content":{"type":"text","text":"thinking"}}""");
            await dsh.Update(s, """{"sessionUpdate":"agent_message_chunk","messageId":"m1","content":{"type":"text","text":"done"}}""");
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"pwsh","kind":"other","status":"in_progress","rawInput":{"description":"List files","command":"Get-ChildItem"}}""");
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c2","title":"write","kind":"other","status":"in_progress","rawInput":{"file_path":"notes/a.txt","content":"hi"}}""");
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c3","title":"edit","kind":"other","status":"in_progress","rawInput":{"file_path":"b.txt","old_string":"x","new_string":"y"}}""");
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c4","title":"mcp__gorynych__list_agents","kind":"other","status":"in_progress","rawInput":{}}""");
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c5","title":"grep","kind":"other","status":"in_progress","rawInput":{"pattern":"TODO"}}""");
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c6","title":"something_new","kind":"other","status":"in_progress","rawInput":{}}""");
        };

        await PromptAsync(service, id);

        JsonObject[] updates;
        lock (service.Updates) updates = [.. service.Updates];
        Assert.AreEqual("m1:thought", (string?)updates[0]["messageId"]);
        Assert.AreEqual("m1", (string?)updates[1]["messageId"]);
        var pwsh = Last(service, "c1");
        Assert.AreEqual("execute", (string?)pwsh["kind"]);
        Assert.AreEqual("List files", (string?)pwsh["title"]);
        Assert.AreEqual("Get-ChildItem", (string?)pwsh["rawInput"]!["command"]);
        Assert.AreEqual(Path.GetFullPath(Work), (string?)pwsh["locations"]![0]!["path"]);
        var write = Last(service, "c2");
        Assert.AreEqual("edit", (string?)write["kind"]);
        Assert.AreEqual("write notes/a.txt", (string?)write["title"]);
        var path = Path.GetFullPath(Path.Combine(Work, "notes", "a.txt"));
        Assert.AreEqual(path, (string?)write["locations"]![0]!["path"]);
        Assert.AreEqual("diff", (string?)write["content"]![0]!["type"]);
        Assert.IsNull(write["content"]![0]!["oldText"]);
        Assert.AreEqual("hi", (string?)write["content"]![0]!["newText"]);
        var edit = Last(service, "c3");
        Assert.AreEqual("x", (string?)edit["content"]![0]!["oldText"]);
        Assert.AreEqual("y", (string?)edit["content"]![0]!["newText"]);
        Assert.AreEqual("mcp__gorynych__list_agents", (string?)Last(service, "c4")["title"], "MCP titles stay: pre-allow reads them");
        Assert.AreEqual("search", (string?)Last(service, "c5")["kind"]);
        Assert.AreEqual("grep TODO", (string?)Last(service, "c5")["title"]);
        Assert.AreEqual("other", (string?)Last(service, "c6")["kind"]);
        Assert.AreEqual("something_new", (string?)Last(service, "c6")["title"]);
    }

    [TestMethod]
    public async Task AShellEscalation_IsRefusedWithoutAskingThePerson()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        JsonNode? answer = null;
        dsh.OnPrompt = async s =>
        {
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"pwsh","kind":"other","status":"in_progress","rawInput":{"command":"npm test","sandbox_permissions":"workspace-write","justification":"run the tests"}}""");
            answer = await (await dsh.AskAsync(s, "c1")).WaitAsync(Bound);
        };

        await PromptAsync(service, id);

        Assert.AreEqual("reject-once", (string?)answer?["outcome"]?["optionId"]);
        Assert.AreEqual(0, service.Questions.Count, "the person is not asked");
    }

    [TestMethod]
    public async Task AQuestionForACallTheProxyNeverSaw_IsRefused()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        JsonNode? answer = null;
        dsh.OnPrompt = async s => answer = await (await dsh.AskAsync(s, "unknown")).WaitAsync(Bound);

        await PromptAsync(service, id);

        Assert.AreEqual("reject-once", (string?)answer?["outcome"]?["optionId"]);
        Assert.AreEqual(0, service.Questions.Count);
    }

    [TestMethod]
    public async Task AFileEscalation_GoesToThePerson_WithItsToolCallAndWhatItAsks()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        service.Answer = (_, _) => Task.FromResult<JsonNode?>(JsonNode.Parse("""{"outcome":{"outcome":"selected","optionId":"allow-once"}}"""));
        JsonNode? answer = null;
        dsh.OnPrompt = async s =>
        {
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"write","kind":"other","status":"in_progress","rawInput":{"file_path":"a.txt","content":"hi","sandbox_permissions":"workspace-write","justification":"save the note"}}""");
            answer = await (await dsh.AskAsync(s, "c1")).WaitAsync(Bound);
        };

        await PromptAsync(service, id);

        Assert.AreEqual("allow-once", (string?)answer?["outcome"]?["optionId"]);
        var call = service.Questions.Single()["toolCall"]!;
        Assert.AreEqual("write a.txt", (string?)call["title"]);
        Assert.AreEqual("edit", (string?)call["kind"]);
        Assert.AreEqual("hi", (string?)call["rawInput"]!["content"]);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Work, "a.txt")), (string?)call["locations"]![0]!["path"]);
        Assert.AreEqual("Asks to run this once with workspace-write permissions: save the note", (string?)call["content"]![0]!["content"]!["text"]);
        Assert.AreEqual("diff", (string?)call["content"]![1]!["type"]);
    }

    [TestMethod]
    public async Task AQuestionDshDropsOnACancel_IsWithdrawn_AndItsLateAnswerNeverReachesDsh()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        Task<JsonNode?>? pending = null;
        dsh.OnPrompt = async s =>
        {
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"edit","kind":"other","status":"in_progress","rawInput":{"file_path":"a.txt","old_string":"a","new_string":"b","sandbox_permissions":"workspace-write","justification":"fix"}}""");
            pending = await dsh.AskAsync(s, "c1");
            await Until(() => service.Questions.Count == 1, "the question");
            // DSH on session/cancel: the escalation fails, and no $/cancel_request comes.
            await dsh.Update(s, """{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"failed","content":[{"type":"content","content":{"type":"text","text":"Error: approval for escalating to \"workspace-write\" was cancelled"}}]}""");
            // Withdrawn when the call ends, while the prompt still runs.
            await Until(() => Volatile.Read(ref service.Withdrawn) == 1, "the withdrawal");
        };

        await PromptAsync(service, id);

        await Task.Delay(200);
        Assert.IsFalse(pending!.IsCompleted, "the service's late answer is dropped");
    }

    [TestMethod]
    public async Task TheEndOfAPrompt_WithdrawsItsOpenQuestions()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        dsh.OnPrompt = async s =>
        {
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"write","kind":"other","status":"in_progress","rawInput":{"file_path":"a.txt","content":"x","sandbox_permissions":"workspace-write"}}""");
            await dsh.AskAsync(s, "c1");
            await Until(() => service.Questions.Count == 1, "the question");
        };

        await PromptAsync(service, id);

        await Until(() => Volatile.Read(ref service.Withdrawn) == 1, "the withdrawal");
    }

    [TestMethod]
    public async Task AResumeOfASessionOpenInDshWeb_SaysWhatToDo()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        dsh.OwnedElsewhere.Add("s-9");

        var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => service.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume,
            new JsonObject { ["sessionId"] = "s-9", ["cwd"] = Work, ["mcpServers"] = new JsonArray() }));

        Assert.AreEqual(DshProxy.OpenElsewhere, e.Message);
        StringAssert.Contains((string?)e.ErrorData?["details"], "already owned");
    }

    [TestMethod]
    public async Task ThePatch_AppendsTheInstructions_AndKeepsTheShellReadOnlyWithApprovals()
    {
        var (service, dsh, data) = await StartAsync("Use the gorynych tools.");
        await using var _ = service;

        Assert.AreEqual(Path.Combine(data, DshAdapter.PatchFile), dsh.PatchPath);
        var patch = File.ReadAllText(dsh.PatchPath!);
        StringAssert.Contains(patch, "personaPrefix: >-\n      You are a coding agent powered by the {{model}} model.");
        StringAssert.Contains(patch, "personaSuffix: |-\n      Use the gorynych tools.\n\n      In this harness the gorynych tools are named mcp__gorynych__list_agents");
        StringAssert.Contains(patch, "      In Gorynych your shell is read-only.");
        StringAssert.Contains(patch, "- id: sandbox-policy\n  config:\n    mode: read-only\n    workspaceRoot: !!js process.cwd()\n");
        StringAssert.Contains(patch, "- id: approval\n  config:\n    policy: ask\n");
        Assert.ThrowsExactly<ArgumentException>(() => DshPatch.Text("a {{cwd}} b"));
        Assert.IsFalse(DshPatch.Text(null).Contains("mcp__gorynych__", StringComparison.Ordinal), "no peer tools without the peers server");
    }
}
