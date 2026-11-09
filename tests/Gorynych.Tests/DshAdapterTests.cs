using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Dsh;

namespace Gorynych.Tests;

/// <summary>What DSH keeps on disk, shared by the DSH processes of one test: sessions and their writers.</summary>
sealed class FakeDshStore
{
    public Dictionary<string, string> Cwds { get; } = [];
    public Dictionary<string, FakeDsh> Writers { get; } = [];
    public int Next;
}

/// <summary>
/// A scripted <c>dsh --profile acp</c>, as DSH 0.2.2-rc.1 answers (research/dsh-adapter-spec.md):
/// a session is durable at once, <c>session/list</c> leaves out the sessions open in this process and
/// gives only <c>sessionId</c> and <c>cwd</c>, a session has one writer, and permission requests name
/// the tool call by ID alone.
/// </summary>
sealed class FakeDsh(FakeDshStore? shared = null)
{
    /// <summary>DSH's own options of a session, which the adapter keeps beside its mode.</summary>
    public const string ModelOption = """{"id":"model","name":"Model","category":"model","type":"select","currentValue":"flash","options":[{"value":"flash","name":"Flash"}]}""";

    readonly FakeDshStore store = shared ?? new FakeDshStore();
    readonly HashSet<string> open = [];

    public JsonRpcConnection Rpc { get; private set; } = null!;
    public string? PatchPath { get; private set; }

    /// <summary>The sandbox its patch gives it.</summary>
    public bool FullAccess => PatchPath is not null && File.ReadAllText(PatchPath).Contains("mode: danger-full-access", StringComparison.Ordinal);

    /// <summary>A resume fails here, as DSH's would for a reason of its own.</summary>
    public bool ResumeFails { get; set; }

    /// <summary>`initialize` is never answered, as by a DSH that hangs at boot.</summary>
    public bool HangInitialize { get; set; }

    /// <summary>Runs when a session is closed, before the answer.</summary>
    public Func<string, Task>? OnClose { get; set; }

    public bool IsOpen(string sessionId)
    {
        lock (store) return open.Contains(sessionId);
    }
    public List<(string Method, JsonNode? Params)> Calls { get; } = [];

    /// <summary>Runs a prompt: sends updates and requests through <see cref="Rpc"/>; the prompt ends when it returns.</summary>
    public Func<string, Task>? OnPrompt { get; set; }

    /// <summary>Sessions another DSH process (dsh web) holds.</summary>
    public HashSet<string> OwnedElsewhere { get; } = [];

    Stream? output;

    /// <summary>Sessions DSH does not have (any more): a prompt for one fails as DSH's does.</summary>
    public HashSet<string> Unknown { get; } = [];

    /// <summary>Writes one line as it is, for output that is not well-formed ACP.</summary>
    public async Task SendRawAsync(string line)
    {
        await output!.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
        await output.FlushAsync();
    }

    public Task<AgentInstance> LaunchAsync(string patch, CancellationToken ct)
    {
        PatchPath = patch;
        var link = new DuplexLink();
        var (ai, ao) = link.AgentEnd;
        output = ao;
        Rpc = new JsonRpcConnection(ai, ao, "fake-dsh");
        // Answers to requests written raw (SendRawAsync) are not the connection's own: kept here.
        Rpc.Trace = (direction, line) =>
        {
            if (direction != "<" || JsonNode.Parse(line) is not JsonObject m || m["method"] is not null) return;
            if (m["id"] is JsonValue v && v.TryGetValue<string>(out var rawId)) lock (Calls) Calls.Add(("response " + rawId, m));
        };
        Rpc.RequestHandler = HandleAsync;
        Rpc.NotificationHandler = (method, p) => { lock (Calls) Calls.Add((method, p?.DeepClone())); return Task.CompletedTask; };
        Rpc.Start();
        // A process that ends lets go of its sessions.
        _ = Rpc.Completion.ContinueWith(_ =>
        {
            lock (store)
            {
                foreach (var held in store.Writers.Where(kv => kv.Value == this).Select(kv => kv.Key).ToList()) store.Writers.Remove(held);
                open.Clear();
            }
        }, TaskScheduler.Default);
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
                if (HangInitialize) await Task.Delay(Timeout.Infinite, ct);
                return JsonNode.Parse("""
                    {"protocolVersion":1,"agentInfo":{"name":"deepseek-harness-acp","version":"0.0.1"},
                     "agentCapabilities":{"mcpCapabilities":{"http":true},"promptCapabilities":{"image":false,"audio":false,"embeddedContext":false},
                       "sessionCapabilities":{"close":{},"list":{},"resume":{}}},"authMethods":[]}
                    """);
            case AcpMethods.Agent.SessionNew:
                string id;
                lock (store)
                {
                    id = $"s-{++store.Next}";
                    store.Cwds[id] = (string)req.Params!["cwd"]!;
                    store.Writers[id] = this;
                    open.Add(id);
                }
                return new JsonObject { ["sessionId"] = id, ["configOptions"] = new JsonArray(JsonNode.Parse(ModelOption)) };
            case AcpMethods.Agent.SessionList:
                var filter = (string?)req.Params?["cwd"];
                lock (store)
                    return new JsonObject
                    {
                        ["sessions"] = new JsonArray(store.Cwds.Where(kv => !open.Contains(kv.Key) && (filter is null || kv.Value == filter))
                            .Select(kv => (JsonNode)new JsonObject { ["sessionId"] = kv.Key, ["cwd"] = kv.Value }).ToArray()),
                    };
            case AcpMethods.Agent.SessionResume:
                lock (store)
                {
                    if (OwnedElsewhere.Contains(sessionId!) || store.Writers.GetValueOrDefault(sessionId!) is { } writer && writer != this)
                        throw new JsonRpcException(JsonRpcException.InternalError, "Internal error",
                            new JsonObject { ["details"] = $"session \"{sessionId}\" is already owned by an active write handle" });
                    if (open.Contains(sessionId!)) throw new JsonRpcException(JsonRpcException.InvalidParams, $"Invalid params: session is already active: {sessionId}");
                    if (ResumeFails) throw new JsonRpcException(JsonRpcException.InternalError, "Internal error: resume failed");
                    store.Cwds.TryAdd(sessionId!, (string?)req.Params?["cwd"] ?? "");
                    store.Writers[sessionId!] = this;
                    open.Add(sessionId!);
                }
                return new JsonObject { ["configOptions"] = new JsonArray(JsonNode.Parse(ModelOption)) };
            case "session/fork":
                string fork;
                lock (store)
                {
                    fork = $"s-{++store.Next}";
                    store.Cwds[fork] = store.Cwds[sessionId!];
                    store.Writers[fork] = this;
                    open.Add(fork);
                }
                return new JsonObject { ["sessionId"] = fork, ["configOptions"] = new JsonArray(JsonNode.Parse(ModelOption)) };
            case AcpMethods.Agent.SessionClose:
                if (OnClose is { } closing) await closing(sessionId!);
                lock (store)
                {
                    open.Remove(sessionId!);
                    if (store.Writers.GetValueOrDefault(sessionId!) == this) store.Writers.Remove(sessionId!);
                }
                return new JsonObject();
            case AcpMethods.Agent.SessionPrompt:
                if (Unknown.Contains(sessionId!)) throw new JsonRpcException(JsonRpcException.InvalidParams, $"Invalid params: unknown session: {sessionId}");
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
public sealed partial class DshAdapterTests
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
        Assert.IsNull(caps["_meta"]!["gorynych/unaskedMcpTools"], "the service decides who asks, the same for every agent");
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

    [TestMethod]
    [DataRow("bash", """{"command":"make","sandbox_permissions":"workspace-write","justification":"build"}""", false)]
    [DataRow("PWSH", """{"command":"npm test","sandbox_permissions":"workspace-write"}""", false)]
    [DataRow("something_new", """{"path":"a.txt","sandbox_permissions":"workspace-write"}""", false)]
    [DataRow("move_file", """{"file_path":"a.txt","sandbox_permissions":"workspace-write","justification":"tidy"}""", false)]
    [DataRow("mcp__gorynych__ask_agent", """{"session_id":"x","question":"y"}""", false)]
    [DataRow("write", """{"file_path":"a.txt","command":"Set-Content a.txt x","sandbox_permissions":"workspace-write"}""", false)]
    [DataRow("Write", """{"file_path":"a.txt","content":"x","sandbox_permissions":"workspace-write"}""", true)]
    [DataRow("edit", """{"file_path":"a.txt","old_string":"a","new_string":"b","sandbox_permissions":"workspace-write"}""", true)]
    public async Task OnlyAFileToolsEscalation_ReachesThePerson(string tool, string input, bool asked)
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        service.Answer = (_, _) => Task.FromResult<JsonNode?>(JsonNode.Parse("""{"outcome":{"outcome":"selected","optionId":"allow-once"}}"""));
        JsonNode? answer = null;
        dsh.OnPrompt = async s =>
        {
            await dsh.Update(s, $$"""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"{{tool}}","kind":"other","status":"in_progress","rawInput":{{input}}}""");
            answer = await (await dsh.AskAsync(s, "c1")).WaitAsync(Bound);
        };

        await PromptAsync(service, id);

        Assert.AreEqual(asked ? 1 : 0, service.Questions.Count);
        Assert.AreEqual(asked ? "allow-once" : "reject-once", (string?)answer?["outcome"]?["optionId"]);
    }

    [TestMethod]
    public async Task AQuestionForACallThatHasEnded_IsRefused()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        JsonNode? answer = null;
        dsh.OnPrompt = async s =>
        {
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"write","kind":"other","status":"in_progress","rawInput":{"file_path":"a.txt","content":"x","sandbox_permissions":"workspace-write"}}""");
            await dsh.Update(s, """{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed","content":[]}""");
            answer = await (await dsh.AskAsync(s, "c1")).WaitAsync(Bound);
        };

        await PromptAsync(service, id);

        Assert.AreEqual("reject-once", (string?)answer?["outcome"]?["optionId"]);
        Assert.AreEqual(0, service.Questions.Count);
    }

    [TestMethod]
    public async Task MalformedOutput_FailsClosed_AndTheProxyKeepsGoing()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        dsh.OnPrompt = async s =>
        {
            // A permission request whose tool call ID and options are not what ACP says.
            await dsh.SendRawAsync("""{"jsonrpc":"2.0","id":"q1","method":"session/request_permission","params":{"sessionId":""" +
                JsonValue.Create(s).ToJsonString() + ""","toolCall":{"toolCallId":7},"options":{"a":1}}}""");
            // A notification and a request whose method is not a string.
            await dsh.SendRawAsync("""{"jsonrpc":"2.0","method":5,"params":{}}""");
            await dsh.SendRawAsync("""{"jsonrpc":"2.0","id":"q2","method":{"x":1},"params":{}}""");
            await dsh.Update(s, """{"sessionUpdate":"agent_message_chunk","messageId":"m1","content":{"type":"text","text":"still here"}}""");
        };

        await PromptAsync(service, id);

        await Until(() => { lock (service.Updates) return service.Updates.Any(u => (string?)u["content"]?["text"] == "still here"); }, "the update after the malformed lines");
        Assert.AreEqual(0, service.Questions.Count, "nothing malformed reached the service");
        await Until(() => { lock (dsh.Calls) return dsh.Calls.Count(c => c.Method == "response q1" || c.Method == "response q2") == 2; }, "answers to both malformed requests");
        JsonNode? q1, q2;
        lock (dsh.Calls)
        {
            q1 = dsh.Calls.Single(c => c.Method == "response q1").Params;
            q2 = dsh.Calls.Single(c => c.Method == "response q2").Params;
        }
        Assert.AreEqual("reject-once", (string?)q1?["result"]?["outcome"]?["optionId"], "a malformed permission request is refused");
        Assert.AreEqual(-32603, (int?)q2?["error"]?["code"], "another malformed request gets an error");
    }

    [TestMethod]
    public async Task ASessionDshDoesNotHave_IsNoLongerAddedToTheList()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        CollectionAssert.Contains(await ListAsync(service), id);
        dsh.Unknown.Add(id);

        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(service, id));

        CollectionAssert.DoesNotContain(await ListAsync(service), id);
    }

    [TestMethod]
    public async Task DshDyingWithRequestsOpen_EndsThemAll_AndTheAdapterExits()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        dsh.OnPrompt = async s =>
        {
            await dsh.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"write","kind":"other","status":"in_progress","rawInput":{"file_path":"a.txt","content":"x","sandbox_permissions":"workspace-write"}}""");
            await dsh.AskAsync(s, "c1");
            await Until(() => service.Questions.Count == 1, "the question");
            var dying = Task.Run(async () => await dsh.Rpc.DisposeAsync()); // DSH dies mid-turn
            await Task.Delay(Timeout.Infinite);
        };

        var prompt = PromptAsync(service, id);

        var failed = await Assert.ThrowsAsync<Exception>(() => prompt);
        Assert.IsNotInstanceOfType<TimeoutException>(failed, "the prompt ended when DSH died");
        await service.Instance.Exited.WaitAsync(Bound);
        await Until(() => Volatile.Read(ref service.Withdrawn) == 1, "the open question ended");
    }
}
