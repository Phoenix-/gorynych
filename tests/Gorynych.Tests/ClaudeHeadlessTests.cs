using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

/// <summary>
/// The Claude adapter's headless transport end to end (<c>claude -p</c>, stream-json): trust,
/// streaming, approvals and questions over <c>can_use_tool</c>, options, cancel, live notes on
/// stdin, the native title and the loud failures, against Gorynych.FakeClaude in its <c>-p</c> mode.
/// </summary>
[TestClass]
public sealed partial class ClaudeHeadlessTests
{
    static string FakeClaude()
    {
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var tests = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var exe = Path.Combine(tests, "Gorynych.FakeClaude", "bin", config, "net10.0", TestDirs.Exe("Gorynych.FakeClaude"));
        return File.Exists(exe) ? exe : throw new AssertInconclusiveException($"build Gorynych.FakeClaude first: {exe}");
    }

    static readonly string FakeRoot = Path.Combine(Path.GetTempPath(), "gorynych-fake-claude");

    static ClaudeAdapterOptions Options(string dir, List<string>? log = null, bool liveNotes = false, Func<Task>? beforeInterrupt = null,
        bool fetchCatalog = true, string permissionMode = "default", int stallMs = 1500,
        Func<Task>? beforeStartup = null, bool autoTitle = false) => new()
    {
        BeforeInterrupt = beforeInterrupt,
        BeforeStartup = beforeStartup,
        FetchCatalog = fetchCatalog,
        AutoTitle = autoTitle,
        PermissionMode = permissionMode,
        DataDirectory = Path.Combine(dir, "claude"),
        Executable = FakeClaude(),
        ProjectsDirectory = Path.Combine(FakeRoot, "projects"),
        ClaudeStatePath = Path.Combine(dir, "no-claude-state.json"),
        // The fake reads only the reply route from --mcp-config; it starts no server.
        ChannelServerCommand = liveNotes ? ["gorynych-peers-not-started"] : null,
        StartupTimeoutSeconds = 20,
        InterruptGraceMs = 3000,
        CloseTimeoutMs = 3000,
        StallMs = stallMs,
        Log = log is null ? null : m => { lock (log) log.Add(m); },
    };

    static List<string> LaunchArgs(string sessionId) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FakeRoot, sessionId + ".args.json")))!.AsArray().Select(a => (string)a!).ToList();

    static List<JsonObject> Controls(string sessionId)
    {
        var path = Path.Combine(FakeRoot, sessionId + ".controls.jsonl");
        return File.Exists(path) ? File.ReadAllLines(path).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList() : [];
    }

    static async Task<string> NewAsync(TestClient client, string dir) =>
        (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir,
            McpServers = [],
            Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" },
        })).SessionId;

    static Task<PromptResponse> PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static Task<JsonNode?> SteerAsync(TestClient client, string sessionId, string text, string messageId) =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodSteering, new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["_meta"] = new JsonObject
            {
                ["gorynych/messageId"] = messageId,
                ["gorynych/promptId"] = "steer-" + messageId,
                ["steering"] = new JsonObject { ["idleBehavior"] = "promptRequired" },
            },
        });

    static string[] PlanEntries(JsonObject? update) =>
        update?["entries"]?.AsArray().Select(e => $"{e!["status"]}:{e!["priority"]}:{e!["content"]}").ToArray() ?? [];

    static string[] LastPlan(TestClient client) =>
        PlanEntries(client.Snapshot().LastOrDefault(u => (string?)u["sessionUpdate"] == "plan"));

    static int PlanCount(TestClient client) => client.Snapshot().Count(u => (string?)u["sessionUpdate"] == "plan");

    static bool Sleeping(TestClient c, int n) =>
        c.Snapshot().Count(u => (string?)u["sessionUpdate"] == "tool_call" && (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)) >= n;

    static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }

    /// <summary>The mark of a turn of Claude's own; the service's mark of a prompt's end, which carries the prompt's ID, is not one.</summary>
    static string? Mark(JsonObject u) => u["_meta"]?[GorynychProtocol.MetaPromptId] is null ? (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] : null;

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task BackgroundSnapshot_TracksProcessLifecycleIndependentlyOfForeground()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        try
        {
            await PromptAsync(client, id, "background snapshot probe");
            await Until(() => conductor.Feed(id).Activity.Snapshot()["background"] is JsonArray { Count: 1 });
            var live = conductor.Feed(id).Activity.Snapshot();
            Assert.AreEqual("idle", (string?)live["state"]);
            Assert.AreEqual("_claude_task", (string?)live["background"]?[0]?["type"]);
            Assert.AreEqual(1, conductor.Feed(id).Users);
            File.WriteAllText(Path.Combine(dir, "release-background"), "");
            await Until(() => conductor.Feed(id).Activity.Snapshot()["background"] is JsonArray { Count: 0 });
            Assert.AreEqual(0, conductor.Feed(id).Users);
        }
        finally { File.WriteAllText(Path.Combine(dir, "release-background"), ""); }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task FirstPrompt_AsksTrustOnce_StreamsText_AndKeepsOneProcess()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);

        var first = await PromptAsync(client, sessionId, "hello");
        Assert.AreEqual(StopReason.EndTurn, first.StopReason);
        Assert.AreEqual("echo: hello", client.Text());
        var trust = client.Approvals.Single();
        StringAssert.Contains(trust.ToolCall.Title ?? "", Path.GetFileName(dir), "the folder-trust question comes first");
        Assert.IsTrue(client.Snapshot().Where(u => (string?)u["sessionUpdate"] == "agent_message_chunk").All(u => (string?)u["messageId"] == "msg_fake1"),
            "the chunks carry Claude's message ID");

        var args = LaunchArgs(sessionId);
        CollectionAssert.IsSubsetOf(new[] { "-p", "--input-format", "stream-json", "--permission-prompt-tool=stdio", "--replay-user-messages", "--session-id" }, args);
        CollectionAssert.DoesNotContain(args, "--dangerously-load-development-channels");
        CollectionAssert.DoesNotContain(args, "--append-system-prompt", "no live-note path and no systemPrompt: nothing to add");
        var settings = JsonNode.Parse(File.ReadAllText(args[args.IndexOf("--settings") + 1]))!;
        Assert.IsNull(settings["hooks"], "the headless transport needs no hooks");

        // The catalog came with initialize: the options show model, effort and mode.
        await Until(() => client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "config_option_update"));
        var options = client.Snapshot().Last(u => (string?)u["sessionUpdate"] == "config_option_update")["configOptions"]!.AsArray();
        CollectionAssert.AreEquivalent(new[] { "mode", "model", "reasoning_effort" }, options.Select(o => (string)o!["id"]!).ToArray());

        var second = await PromptAsync(client, sessionId, "again\nand more");
        Assert.AreEqual(StopReason.EndTurn, second.StopReason);
        StringAssert.Contains(client.Text(), "echo: again\nand more");
        Assert.AreEqual(1, Controls(sessionId).Count(c => (string?)c["subtype"] == "initialize"), "one process for both turns");

        // Another session in the same folder: trusted now, so nobody is asked.
        var other = await NewAsync(client, dir);
        await PromptAsync(client, other, "hi");
        Assert.AreEqual(1, client.Approvals.Count);
        StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "claude", "trusted.json")), Path.GetFileName(dir));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Approvals_Questions_AndDenials_GoThroughCanUseTool()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);

        await PromptAsync(client, sessionId, "please write a file");
        Assert.AreEqual("Write fake.txt", client.Approvals.Last().ToolCall.Title);
        CollectionAssert.AreEqual(new[] { ClaudeHooks.AllowOptionId, ClaudeHooks.DenyOptionId }, client.Approvals.Last().Options.Select(o => o.OptionId).ToArray(),
            "the same two options as the TUI: nothing that writes Claude's settings");
        StringAssert.Contains(client.Text(), "wrote it");
        var write = client.Snapshot().First(u => (string?)u["sessionUpdate"] == "tool_call");
        Assert.AreEqual("toolu_fake1", (string?)write["toolCallId"]);
        Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call_update" && (string?)u["toolCallId"] == "toolu_fake1" && (string?)u["status"] == "completed"));

        await PromptAsync(client, sessionId, "pick a color for me");
        Assert.AreEqual(1, client.Forms.Count, "AskUserQuestion became one form");
        StringAssert.Contains(client.Text(), "you chose Red");

        await PromptAsync(client, sessionId, "peers please");
        StringAssert.Contains(client.Text(), "list_agents:allow ask_agent:allow");
        Assert.IsFalse(client.Approvals.Any(a => a.ToolCall.Title?.Contains("(gorynych)", StringComparison.Ordinal) == true), "the gorynych tools ask nothing");

        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = ClaudeHooks.DenyOptionId });
        await PromptAsync(client, sessionId, "write it again");
        StringAssert.Contains(client.Text(), "not allowed");
        Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call_update" && (string?)u["status"] == "failed"), "a denial fails the tool call");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task TaskTools_ShowFullReplacementPlans_AndKeepTheirToolCalls()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);

        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, sessionId, "plan tasks please")).StopReason);
        StringAssert.Contains(client.Text(), "planned it");
        CollectionAssert.AreEqual(new[] { "completed:medium:Plan the renamed work" }, LastPlan(client));
        Assert.IsTrue(PlanCount(client) >= 4, "each successful task change replaces the whole plan");
        Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call" && (string?)u["toolCallId"] == "toolu_fake1"),
            "the ordinary TaskCreate tool call still shows");
        Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call_update" && (string?)u["toolCallId"] == "toolu_fake1" && (string?)u["status"] == "completed"),
            "the ordinary TaskCreate result still shows");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task TodoWrite_ShowsItsSnapshot_AndAnEmptyOneClearsThePlan()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);

        await PromptAsync(client, sessionId, "plan todo please");
        CollectionAssert.AreEqual(new[] { "pending:medium:First todo", "in_progress:medium:Doing the second todo" }, LastPlan(client));

        await PromptAsync(client, sessionId, "plan todo clear please");
        Assert.AreEqual(0, LastPlan(client).Length, "the empty native snapshot clears the plan");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task FailedTaskCreate_PublishesNoPlan()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);

        await PromptAsync(client, sessionId, "plan task failure please");
        Assert.AreEqual(0, PlanCount(client), "a failed call is not an applied plan");
        Assert.IsFalse(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "plan"));
        StringAssert.Contains(client.Text(), "create failed");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task SubagentTaskTools_DoNotBecomeTheParentsPlan()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);

        await PromptAsync(client, sessionId, "plan subagent please");
        CollectionAssert.AreEqual(new[] { "pending:medium:Parent task" }, LastPlan(client));
        Assert.IsFalse(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "plan" && u.ToJsonString().Contains("Subagent task", StringComparison.Ordinal)),
            "the subagent's checklist never reaches the parent's plan");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Cancel_InterruptsTheTurn_AndTheNextPromptRuns()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        await PromptAsync(client, sessionId, "warm up");

        var slow = PromptAsync(client, sessionId, "slow work");
        await Until(() => client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call" && (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId });
        Assert.AreEqual(StopReason.Cancelled, (await slow).StopReason);
        Assert.IsTrue(Controls(sessionId).Any(c => (string?)c["subtype"] == "interrupt"));
        Assert.IsFalse(client.Text().Contains("[Claude stopped", StringComparison.Ordinal), "a cancelled turn's error is no news");

        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, sessionId, "after")).StopReason);
        StringAssert.Contains(client.Text(), "echo: after");
    }

    [TestMethod]
    public void LiveOpinionText_PreservesTheQuestion_AndAllowsReadOnlyInvestigation()
    {
        var text = ClaudeAgent.LiveOpinionText("Inspect the diff without editing files.", "c-test");
        StringAssert.StartsWith(text, "Inspect the diff without editing files.\n\n");
        StringAssert.Contains(text, "using read-only tools");
        StringAssert.Contains(text, "does not authorize edits or other state-changing actions");
        StringAssert.Contains(text, "passing message_id \"c-test\"");
        StringAssert.Contains(text, "Do not call ask_agent while answering this request");
        Assert.IsFalse(text.Contains("Take no other action", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task LiveNotes_StartATurnOfClaudesOwn_OrJoinTheRunningOne()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, liveNotes: true)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        await PromptAsync(client, sessionId, "hello");
        var args = LaunchArgs(sessionId);
        var mcp = JsonNode.Parse(File.ReadAllText(args[args.IndexOf("--mcp-config") + 1]))!["mcpServers"]!["gorynych"]!["args"]!.AsArray().Select(a => (string)a!).ToList();
        CollectionAssert.Contains(mcp, "--reply-url");
        CollectionAssert.DoesNotContain(mcp, "--channel-url", "no development channel");
        var instructions = args[args.IndexOf("--append-system-prompt") + 1];
        StringAssert.Contains(instructions, "[Opinion request");
        StringAssert.Contains(instructions, "using read-only tools");
        StringAssert.Contains(instructions, "does not authorize edits or other state-changing actions");
        StringAssert.Contains(instructions, "Distinguish verified findings from assumptions");
        StringAssert.Contains(instructions, "passing the message_id the request names");
        StringAssert.Contains(instructions, "Do not call ask_agent while answering this request");
        Assert.IsFalse(instructions.Contains("Run no tool", StringComparison.Ordinal));

        // Claude is idle: the request starts a turn of its own, marked from the request to its result.
        var opinion = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = sessionId, ["sender"] = "peer_kimi", ["text"] = "Tabs or spaces?",
        });
        Assert.AreEqual("fake opinion", (string?)opinion?["text"]);
        await Until(() => client.Snapshot().Any(u => Mark(u) == "ended"));
        var updates = client.Snapshot().ToList();
        var request = updates.Single(u => (string?)u["sessionUpdate"] == "user_message_chunk");
        Assert.AreEqual("live", (string?)request["_meta"]![GorynychProtocol.MetaOrigin]);
        StringAssert.StartsWith((string?)request["_meta"]![GorynychProtocol.MetaOpinion], "c");
        Assert.AreEqual("started", Mark(request));
        StringAssert.Contains((string?)request["content"]!["text"], "Tabs or spaces?");
        StringAssert.Contains((string?)request["content"]!["text"], "message_id \"c");
        Assert.AreEqual(1, updates.Count(u => Mark(u) == "ended"));

        // Claude is busy with the client's turn: the request joins it, and nothing is marked.
        var slow = PromptAsync(client, sessionId, "slow work");
        await Until(() => client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call" && (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        var joined = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = sessionId, ["sender"] = "peer_codex", ["text"] = "Tabs?",
        });
        Assert.AreEqual(StopReason.EndTurn, (await slow).StopReason);
        Assert.AreEqual("fake opinion", (string?)joined?["text"]);
        StringAssert.Contains(client.Text(), "also: [Opinion request");
        Assert.AreEqual(1, client.Snapshot().Count(u => Mark(u) == "started"), "a request inside the client's turn is not a turn of Claude's own");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ATurnClaudeBeginsItself_IsMarked()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        await PromptAsync(client, sessionId, "answer, then go on on your own");
        await Until(() => client.Snapshot().Any(u => Mark(u) == "ended"));
        var updates = client.Snapshot().ToList();
        var started = updates.FindIndex(u => Mark(u) == "started");
        var ended = updates.FindIndex(u => Mark(u) == "ended");
        Assert.IsTrue(started >= 0 && ended > started);
        StringAssert.Contains(client.Text(), "background done");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ADraftResumedAfterARestart_StartsAfreshUnderItsId_ButAnUnknownSessionIsNotFound()
    {
        var dir = TestDirs.New();
        var agent = await new ClaudeAdapter(Options(dir)).StartAsync(CancellationToken.None);
        var acp = new JsonRpcConnection(agent.Input, agent.Output, "direct-client")
        {
            RequestHandler = (_, _) => Task.FromResult<JsonNode?>(AcpJson.ToNode(new RequestPermissionResponse
            {
                Outcome = new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId }, // the folder's trust
            })),
        };
        acp.Start();
        try
        {
            await acp.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
            JsonObject Resume(string id, bool draft) => new()
            {
                ["sessionId"] = id, ["cwd"] = dir, ["mcpServers"] = new JsonArray(),
                ["_meta"] = draft ? new JsonObject { [GorynychProtocol.MetaDraft] = true } : null,
            };
            var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => acp.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume(Guid.NewGuid().ToString(), draft: false)));
            Assert.AreEqual(JsonRpcException.ResourceNotFound, e.Code, "without the mark, a session nobody knows is not made up");

            // A session the service created before this adapter restarted, and nobody prompted.
            var draftId = Guid.NewGuid().ToString();
            await acp.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume(draftId, draft: true));
            var r = await acp.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = draftId, Prompt = [new TextContent { Text = "hi" }] });

            Assert.AreEqual(StopReason.EndTurn, r.StopReason);
            var args = LaunchArgs(draftId);
            Assert.AreEqual(draftId, args[args.IndexOf("--session-id") + 1], "a new Claude session, under the draft's ID");
            CollectionAssert.DoesNotContain(args, "--resume");
        }
        finally
        {
            await acp.DisposeAsync();
            await agent.DisposeAsync();
        }
    }

    static List<string>? TitleLaunch(string token)
    {
        foreach (var file in Directory.GetFiles(FakeRoot, "title-*.args.json"))
            try
            {
                var args = JsonNode.Parse(File.ReadAllText(file))!.AsArray().Select(a => (string)a!).ToList();
                if (args.LastOrDefault()?.Contains(token, StringComparison.Ordinal) == true) return args;
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        return null;
    }

    static async Task UntilTitleLaunch(string token)
    {
        for (var i = 0; i < 200 && TitleLaunch(token) is null; i++) await Task.Delay(25);
        Assert.IsNotNull(TitleLaunch(token), "the isolated title request started");
    }

    [TestMethod]
    public async Task Review_ANativeStartupFailure_DoesNotStartATitleModelCall()
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "initialize-error"), "");
        var token = "failed-startup-title-" + Guid.NewGuid().ToString("N");
        var (agent, acp, _) = await DirectTitleAsync(dir);
        await using var ownedAgent = agent;
        await using var ownedClient = acp;
        var id = (await acp.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, McpServers = [] })).SessionId;
        await acp.SendRequestAsync(GorynychProtocol.MethodGenerateTitle, new JsonObject
        {
            ["sessionId"] = id, ["input"] = token, ["title"] = "Fallback",
        });
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => acp.RequestAsync<PromptRequest, PromptResponse>(
            AcpMethods.Agent.SessionPrompt, new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = token }] }));
        await Task.Delay(200);
        Assert.IsNull(TitleLaunch(token), "A failed native startup must not launch the title helper.");
    }

    static async Task<(AgentInstance Agent, JsonRpcConnection Acp, List<JsonObject> Updates)> DirectTitleAsync(string dir, List<string>? log = null, TimeSpan? timeout = null)
    {
        var options = Options(dir, log, autoTitle: true);
        if (timeout is { } t)
            options = new ClaudeAdapterOptions
            {
                BeforeInterrupt = options.BeforeInterrupt, FetchCatalog = options.FetchCatalog, PermissionMode = options.PermissionMode,
                DataDirectory = options.DataDirectory, Executable = options.Executable, ProjectsDirectory = options.ProjectsDirectory,
                ClaudeStatePath = options.ClaudeStatePath, ChannelServerCommand = options.ChannelServerCommand,
                StartupTimeoutSeconds = options.StartupTimeoutSeconds, InterruptGraceMs = options.InterruptGraceMs,
                CloseTimeoutMs = options.CloseTimeoutMs, StallMs = options.StallMs, BeforeStartup = options.BeforeStartup,
                Log = options.Log, TitleTimeout = t, AutoTitle = true,
            };
        var agent = await new ClaudeAdapter(options).StartAsync(CancellationToken.None);
        var updates = new List<JsonObject>();
        var acp = new JsonRpcConnection(agent.Input, agent.Output, "direct-client")
        {
            NotificationHandler = (_, p) =>
            {
                if (p?["update"] is JsonObject u) lock (updates) updates.Add(u);
                return Task.CompletedTask;
            },
            RequestHandler = (_, _) => Task.FromResult<JsonNode?>(AcpJson.ToNode(new RequestPermissionResponse
            {
                Outcome = new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId },
            })),
        };
        acp.Start();
        return (agent, acp, updates);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task GeneratedTitle_RunsAnIsolatedNoToolRequest_AndWritesClaudesOwnCustomTitle()
    {
        var dir = TestDirs.New();
        var token = "token-" + Guid.NewGuid().ToString("N");
        var log = new List<string>();
        var (agent, acp, updates) = await DirectTitleAsync(dir, log);
        try
        {
            var init = await acp.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
            Assert.IsTrue((bool?)init.AgentCapabilities?.Meta?[GorynychProtocol.CapAutoTitle] == true);
            var id = (await acp.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, McpServers = [] })).SessionId;
            await acp.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "Fix the flaky login test " + token }] });

            await acp.SendRequestAsync(GorynychProtocol.MethodGenerateTitle, new JsonObject
            {
                ["sessionId"] = id, ["title"] = "Fix the flaky login test", ["input"] = "Fix the flaky login test " + token,
            });
            await Until(() => { lock (updates) return updates.Any(u => (string?)u["title"] == "Fake generated title"); });
            string diagnostic;
            lock (log) diagnostic = string.Join(" | ", log);
            lock (updates) Assert.IsTrue(updates.Any(u => (string?)u["title"] == "Fake generated title"), diagnostic);

            var args = TitleLaunch(token)!;
            Assert.AreEqual("", args[args.IndexOf("--tools") + 1], "no built-in tool is offered");
            CollectionAssert.Contains(args, "--strict-mcp-config");
            CollectionAssert.Contains(args, "--safe-mode");
            CollectionAssert.Contains(args, "--no-session-persistence");
            Assert.AreEqual("none", args[args.IndexOf("--permission-prompts") + 1], "no approval dialog can appear");
            Assert.AreEqual("default", args[args.IndexOf("--permission-mode") + 1]);
            Assert.IsTrue(Controls(id).Any(c => (string?)c["subtype"] == "rename_session" && (string?)c["title"] == "Fake generated title"),
                "the name is Claude's own custom-title");
            var listed = await acp.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
            Assert.AreEqual(1, listed.Sessions.Count, "the helper left no session behind");
        }
        finally
        {
            await acp.DisposeAsync();
            await agent.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AManualName_WinsOverAGeneratedTitleStillInFlight()
    {
        var dir = TestDirs.New();
        var token = "hold-token-" + Guid.NewGuid().ToString("N");
        var (agent, acp, updates) = await DirectTitleAsync(dir);
        try
        {
            await acp.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
            var id = (await acp.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, McpServers = [] })).SessionId;
            await acp.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "hold-title fix the login test " + token }] });

            await acp.SendRequestAsync(GorynychProtocol.MethodGenerateTitle, new JsonObject
            {
                ["sessionId"] = id, ["title"] = "Fix the login test", ["input"] = "hold-title fix the login test " + token,
            });
            await UntilTitleLaunch(token);
            await acp.SendRequestAsync(GorynychProtocol.MethodSetTitle, new JsonObject { ["sessionId"] = id, ["title"] = "Manual title" });
            await Until(() => { lock (updates) return updates.Any(u => (string?)u["title"] == "Manual title"); });
            File.WriteAllText(Path.Combine(dir, "claude", "title", "release-title"), "");
            await Task.Delay(750);
            lock (updates)
            {
                Assert.IsTrue(updates.Any(u => (string?)u["title"] == "Manual title"));
                Assert.IsFalse(updates.Any(u => (string?)u["title"] == "Fake generated title"), "the stale helper never renamed the session");
            }
        }
        finally
        {
            await acp.DisposeAsync();
            await agent.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AFailedOrTimedOutHelper_LeavesTheProvisionalTitle()
    {
        var dir = TestDirs.New();
        var token = "fail-token-" + Guid.NewGuid().ToString("N");
        var (agent, acp, updates) = await DirectTitleAsync(dir);
        try
        {
            await acp.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
            var id = (await acp.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, McpServers = [] })).SessionId;
            await acp.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "fail-title fix the login test " + token }] });
            await acp.SendRequestAsync(GorynychProtocol.MethodGenerateTitle, new JsonObject
            {
                ["sessionId"] = id, ["title"] = "Fix the login test", ["input"] = "fail-title fix the login test " + token,
            });
            await UntilTitleLaunch(token);
            await Task.Delay(500);
            lock (updates) Assert.IsFalse(updates.Any(u => (string?)u["title"] == "Fake generated title"));
        }
        finally
        {
            await acp.DisposeAsync();
            await agent.DisposeAsync();
        }

        var timeoutDir = TestDirs.New();
        var timeoutToken = "timeout-token-" + Guid.NewGuid().ToString("N");
        var (slowAgent, slowAcp, slowUpdates) = await DirectTitleAsync(timeoutDir, timeout: TimeSpan.FromMilliseconds(150));
        try
        {
            await slowAcp.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
            var id = (await slowAcp.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = timeoutDir, McpServers = [] })).SessionId;
            await slowAcp.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "timeout-title fix the login test " + timeoutToken }] });
            await slowAcp.SendRequestAsync(GorynychProtocol.MethodGenerateTitle, new JsonObject
            {
                ["sessionId"] = id, ["title"] = "Fix the login test", ["input"] = "timeout-title fix the login test " + timeoutToken,
            });
            await UntilTitleLaunch(timeoutToken);
            await Task.Delay(750);
            lock (slowUpdates) Assert.IsFalse(slowUpdates.Any(u => (string?)u["title"] == "Fake generated title"));
        }
        finally
        {
            await slowAcp.DisposeAsync();
            await slowAgent.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Options_ApplyToTheRunningClaude_AndCarryIntoTheNextLaunch_AndTheTitleIsClaudesOwn()
    {
        var dir = TestDirs.New();
        var agent = await new ClaudeAdapter(Options(dir)).StartAsync(CancellationToken.None);
        var updates = new List<JsonObject>();
        var acp = new JsonRpcConnection(agent.Input, agent.Output, "direct-client")
        {
            NotificationHandler = (_, p) =>
            {
                if (p?["update"] is JsonObject u) lock (updates) updates.Add(u);
                return Task.CompletedTask;
            },
            RequestHandler = (_, _) => Task.FromResult<JsonNode?>(AcpJson.ToNode(new RequestPermissionResponse
            {
                Outcome = new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId },
            })),
        };
        acp.Start();
        try
        {
            var init = await acp.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
            Assert.IsTrue((bool?)init.AgentCapabilities?.Meta?[GorynychProtocol.CapSetTitle] == true);
            Assert.IsTrue(init.AgentCapabilities?.PromptCapabilities?.Image == true);
            var sessionId = (await acp.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = dir, McpServers = [] })).SessionId;
            await acp.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest
            {
                SessionId = sessionId,
                Prompt = [new TextContent { Text = "look" }, new ImageContent { Data = "iVBORw0KGgo=", MimeType = "image/png" }],
            });
            lock (updates) StringAssert.Contains(string.Concat(updates.Select(u => (string?)u["content"]?["text"])), "[image]", "the image reached Claude");

            async Task<List<JsonObject>> Set(string id, string value) =>
                (await acp.RequestAsync<SetSessionConfigOptionRequest, SetSessionConfigOptionResponse>(AcpMethods.Agent.SessionSetConfigOption,
                    new SetSessionConfigOptionRequestValueId { SessionId = sessionId, ConfigId = id, Value = value }))
                .ConfigOptions.Select(o => (JsonObject)AcpJson.ToNode(o)!).ToList();
            var afterModel = await Set("model", "fast");
            Assert.AreEqual("fast", (string?)afterModel.Single(o => (string?)o["id"] == "model")["currentValue"]);
            await Set("reasoning_effort", "low");
            await Set("mode", "plan");
            CollectionAssert.IsSubsetOf(new[] { "set_model", "apply_flag_settings", "set_permission_mode" },
                Controls(sessionId).Select(c => (string)c["subtype"]!).ToArray());
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Set("reasoning_effort", "max"), "the fast model has no max effort");

            await acp.SendRequestAsync(GorynychProtocol.MethodSetTitle, new JsonObject { ["sessionId"] = sessionId, ["title"] = "Named here" });
            await Until(() => { lock (updates) return updates.Any(u => (string?)u["title"] == "Named here"); });
            lock (updates) Assert.IsTrue(updates.Any(u => (string?)u["title"] == "Named here"), "rename_session wrote Claude's custom-title, read back from the transcript");

            // Closed and resumed: a new process, started with the choices made.
            await acp.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId });
            await acp.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = sessionId, ["cwd"] = dir, ["mcpServers"] = new JsonArray() });
            await acp.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = "back" }] });
            var args = LaunchArgs(sessionId);
            Assert.AreEqual("fast", args[args.IndexOf("--model") + 1]);
            Assert.AreEqual("low", args[args.IndexOf("--effort") + 1]);
            Assert.AreEqual("plan", args[args.IndexOf("--permission-mode") + 1]);
            Assert.AreEqual(sessionId, args[args.IndexOf("--resume") + 1]);
        }
        finally
        {
            await acp.DisposeAsync();
            await agent.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow("signed-out")]
    [DataRow("no-account")]
    [DataRow("no-provider")]
    [DataRow("empty-account")]
    [DataRow("unknown-provider")]
    public async Task AccountMetadata_DoesNotGateAWorkingNativeSession(string marker)
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, marker), "");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, sessionId, "hello")).StopReason);
        StringAssert.Contains(client.Text(), "echo: hello");
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, sessionId, "again")).StopReason);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ACancelThatArrivesLate_DoesNotInterruptTheNextPrompt()
    {
        var dir = TestDirs.New();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, beforeInterrupt: () => release.Task)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        await PromptAsync(client, sessionId, "warm up");

        static bool Sleeping(TestClient c, int n) =>
            c.Snapshot().Count(u => (string?)u["sessionUpdate"] == "tool_call" && (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)) >= n;
        var first = PromptAsync(client, sessionId, "slow work");
        await Until(() => Sleeping(client, 1));
        // The cancel's interrupt is held back until the first turn is over and the next prompt runs.
        await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId });
        Assert.AreEqual(StopReason.Cancelled, (await first).StopReason, "it ended by itself, as cancelled");
        var second = PromptAsync(client, sessionId, "slow again");
        await Until(() => Sleeping(client, 2));
        release.TrySetResult();

        Assert.AreEqual(StopReason.EndTurn, (await second).StopReason, "the late interrupt left the next prompt alone");
        Assert.IsFalse(Controls(sessionId).Any(c => (string?)c["subtype"] == "interrupt"), "no interrupt was sent for a turn that was over");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AClaudeSlowToStart_IsNotTakenForOneThatDroppedThePrompt_WithApiAuthentication()
    {
        // Claude says system/init once its MCP servers are up, which may take long on a slow machine:
        // its silence until then is its start, not a message it did not take. Were it taken for one,
        // the prompt would fail before the configured API authentication could run.
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "api-key-init"), "");
        var held = Path.Combine(dir, "init-held");
        File.WriteAllText(held, "");
        var log = new List<string>();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, log, stallMs: 100)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);

        var prompt = PromptAsync(client, sessionId, "hello");
        await Task.Delay(1000); // ten times what a stall is, after Claude started
        Assert.IsFalse(prompt.IsCompleted, "still starting");
        lock (log) Assert.IsFalse(log.Any(l => l.Contains("in no turn", StringComparison.Ordinal)), string.Join(Environment.NewLine, log));
        File.Delete(held);

        Assert.AreEqual(StopReason.EndTurn, (await prompt).StopReason);
        StringAssert.Contains(client.Text(), "echo: hello");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow("authentication_failed")]
    [DataRow("billing_error")]
    public async Task InvalidCredentialsOrBilling_StillFailThePrompt_AndAllowRecovery(string error)
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "auth-token"), "");
        var marker = Path.Combine(dir, "auth-failure");
        File.WriteAllText(marker, error);
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        var failure = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(client, sessionId, "first"));
        StringAssert.Contains(failure.Message, error);
        StringAssert.Contains(client.Text(), error);
        File.Delete(marker);
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, sessionId, "second")).StopReason);
        StringAssert.Contains(client.Text(), "echo: second");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AStoppedClaude_LeavesNothingBehind_AndTheNextLaunchStartsClean()
    {
        var dir = TestDirs.New();
        var marker = Path.Combine(dir, "initialize-error");
        File.WriteAllText(marker, "");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        // A native protocol startup failure must not leak state into the next launch.
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(client, sessionId, "first try"));
        File.Delete(marker);
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, sessionId, "second try")).StopReason);
        StringAssert.Contains(client.Text(), "echo: second try");
        Assert.IsFalse(client.Text().Contains("echo: first try", StringComparison.Ordinal), "nothing of the stopped process reached the feed");
        Assert.AreEqual(0, client.Snapshot().Count(u => Mark(u) is not null), "no turn of Claude's own was opened by its leftovers");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ClosingASessionWhoseClaudeStoppedReading_KillsIt_InsteadOfHanging()
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "deaf"), "");
        var agent = await new ClaudeAdapter(Options(dir)).StartAsync(CancellationToken.None);
        var acp = new JsonRpcConnection(agent.Input, agent.Output, "direct-client")
        {
            NotificationHandler = (_, _) => Task.CompletedTask,
            RequestHandler = (_, _) => Task.FromResult<JsonNode?>(AcpJson.ToNode(new RequestPermissionResponse
            {
                Outcome = new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId },
            })),
        };
        acp.Start();
        try
        {
            var sessionId = (await acp.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = dir, McpServers = [] })).SessionId;
            // Far more than a pipe holds: the write blocks, with the input held.
            var prompt = acp.SendRequestAsync(AcpMethods.Agent.SessionPrompt, AcpJson.ToNode(new PromptRequest
            {
                SessionId = sessionId, Prompt = [new TextContent { Text = new string('x', 4 * 1024 * 1024) }],
            }));
            await Task.Delay(1500);
            Assert.IsFalse(prompt.IsCompleted);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await acp.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId }).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), $"close took {watch.Elapsed}");
            try { await prompt.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (JsonRpcException) { } // cancelled or failed: either way it ended
        }
        finally
        {
            await acp.DisposeAsync();
            await agent.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ACancel_DropsAQueuedLiveNote_AndTheServiceAsksInATurnInstead()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, liveNotes: true)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        await PromptAsync(client, sessionId, "hello");

        var slow = PromptAsync(client, sessionId, "slow, hold the rest");
        await Until(() => client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call" && (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        var opinion = client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = sessionId, ["sender"] = "peer_kimi", ["text"] = "Tabs?",
        });
        await Task.Delay(300); // the request waits in Claude's queue
        await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId });
        Assert.AreEqual(StopReason.Cancelled, (await slow).StopReason);
        var interrupt = Controls(sessionId).Last(c => (string?)c["subtype"] == "interrupt");
        Assert.IsTrue((bool?)interrupt["cancel_queued"] == true);

        // Dropped from the live-note path, the request is asked in a turn: the fake answers it in its text.
        var answer = (string?)(await opinion)?["text"];
        StringAssert.Contains(answer, "Tabs?");
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, sessionId, "after")).StopReason);
    }

    [TestMethod]
    public async Task SteeringCapability_IsHeadlessOnly()
    {
        var dir = TestDirs.New();
        var headless = await new ClaudeAdapter(Options(dir, fetchCatalog: false)).StartAsync(CancellationToken.None);
        try
        {
            var rpc = new JsonRpcConnection(headless.Input, headless.Output, "direct");
            rpc.Start();
            try
            {
                var init = await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize,
                    new InitializeRequest { ProtocolVersion = 1 });
                Assert.IsTrue((bool?)init.Meta?[GorynychProtocol.MetaSteering]?["supported"]);
            }
            finally { await rpc.DisposeAsync(); }
        }
        finally { await headless.DisposeAsync(); }

        if (!OperatingSystem.IsWindows()) return; // the TUI transport starts only on Windows
        var tui = await new ClaudeAdapter(new ClaudeAdapterOptions
        {
            Transport = ClaudeTransport.Tui,
            DataDirectory = Path.Combine(dir, "tui"),
            Executable = FakeClaude(),
            FetchCatalog = false,
            StartupTimeoutSeconds = 20,
        }).StartAsync(CancellationToken.None);
        try
        {
            var rpc = new JsonRpcConnection(tui.Input, tui.Output, "direct-tui");
            rpc.Start();
            try
            {
                var init = await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize,
                    new InitializeRequest { ProtocolVersion = 1 });
                Assert.AreEqual(false, (bool?)init.Meta?[GorynychProtocol.MetaSteering]?["supported"],
                    "the TUI transport never advertises ACP steering");
            }
            finally { await rpc.DisposeAsync(); }
        }
        finally { await tui.DisposeAsync(); }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow("subscription")]
    [DataRow("api-key")]
    [DataRow("api-key-init")]
    [DataRow("auth-token")]
    [DataRow("bedrock")]
    public async Task ASteer_JoinsTheRunningTurn_OneNativeResult_AndNoDuplicateEcho(string credentials)
    {
        var dir = TestDirs.New();
        if (credentials != "subscription") File.WriteAllText(Path.Combine(dir, credentials), "");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        await PromptAsync(client, sessionId, "warm up");

        var slow = PromptAsync(client, sessionId, "slow work");
        await Until(() => Sleeping(client, 1));
        var steered = await SteerAsync(client, sessionId, "steer one", "m-steer-one");
        Assert.AreEqual(AcpSteering.Injected, (string?)steered?["outcome"]);
        Assert.AreEqual(StopReason.EndTurn, (await slow).StopReason);

        StringAssert.Contains(client.Text(), "slept / also: steer one");
        var steeredChunks = conductor.Feed(sessionId).Journal.ReadAll().OfType<JsonObject>()
            .Where(u => u["content"] is JsonObject content && (string?)content["text"] == "steer one").ToList();
        Assert.AreEqual(1, steeredChunks.Count, "one journaled steered message, no adapter echo");
        Assert.IsFalse(string.IsNullOrEmpty((string?)steeredChunks[0]["messageId"]), "the service's own message ID");
        Assert.AreEqual("steer-m-steer-one", (string?)steeredChunks[0]["_meta"]?[GorynychProtocol.MetaPromptId]);
        Assert.AreEqual(true, (bool?)steeredChunks[0]["_meta"]?[GorynychProtocol.MetaSteered]);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task PendingSteers_KeepTheACPTurnOpenAcrossAnIntermediateResult()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        await PromptAsync(client, sessionId, "warm up");
        var endsBefore = conductor.Feed(sessionId).Journal.ReadAll().OfType<JsonObject>()
            .Count(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended" && u["_meta"]?[GorynychProtocol.MetaPromptId] is not null);

        var slow = PromptAsync(client, sessionId, "slow hold");
        await Until(() => Sleeping(client, 1));
        Assert.AreEqual(AcpSteering.Injected, (string?)(await SteerAsync(client, sessionId, "first steer", "m-first"))?["outcome"]);
        Assert.AreEqual(AcpSteering.Injected, (string?)(await SteerAsync(client, sessionId, "second steer", "m-second"))?["outcome"]);
        Assert.AreEqual(StopReason.EndTurn, (await slow).StopReason);
        StringAssert.Contains(client.Text(), "echo: first steer");
        StringAssert.Contains(client.Text(), "echo: second steer");
        var journal = conductor.Feed(sessionId).Journal.ReadAll().OfType<JsonObject>().ToList();
        Assert.AreEqual(2, journal.Count(u => (bool?)u["_meta"]?[GorynychProtocol.MetaSteered] == true));
        Assert.AreEqual(endsBefore + 1, journal.Count(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended" && u["_meta"]?[GorynychProtocol.MetaPromptId] is not null),
            "one ACP turn end, after both native cycles");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task CancellingALaterNativeCycle_CancelsTheOwningAcpPrompt()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        var prompt = PromptAsync(client, id, "slow hold");
        await Until(() => Sleeping(client, 1));
        await SteerAsync(client, id, "slow steered work", "later-native-cycle");
        await Until(() => Sleeping(client, 2));
        await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = id });
        Assert.AreEqual(StopReason.Cancelled, (await prompt).StopReason,
            "the first native cycle's end_turn must not mask cancellation of the steered cycle");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ASteerLostAfterAnIntermediateResult_DoesNotCompleteThePromptSuccessfully()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        var prompt = PromptAsync(client, id, "slow hold");
        await Until(() => Sleeping(client, 1));
        await SteerAsync(client, id, "swallow this input", "lost-steer");
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => prompt.WaitAsync(TimeSpan.FromSeconds(15)));
        StringAssert.Contains(error.Message, "Claude did not take");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ASteer_LeavesAnOpenApprovalPending()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        await PromptAsync(client, sessionId, "warm up");

        var ask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvals = 0;
        client.Approve = async r =>
        {
            Interlocked.Increment(ref approvals);
            ask.TrySetResult();
            await release.Task;
            return new SelectedPermissionOutcome { OptionId = r.Options[0].OptionId };
        };
        var turn = PromptAsync(client, sessionId, "please write a file");
        await ask.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var steered = await SteerAsync(client, sessionId, "do it differently", "m-during-approval");
        Assert.AreEqual(AcpSteering.Injected, (string?)steered?["outcome"]);
        Assert.AreEqual(1, Volatile.Read(ref approvals), "steering neither answers nor replaces the approval");
        release.TrySetResult();
        Assert.AreEqual(StopReason.EndTurn, (await turn).StopReason);
        StringAssert.Contains(client.Text(), "wrote it");
        StringAssert.Contains(client.Text(), "echo: do it differently");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task DirectIdleSteer_WithoutTheOptIn_KeepsTheLegacyStartedNewTurn()
    {
        var dir = TestDirs.New();
        var updates = new List<JsonObject>();
        var owner = await new ClaudeAdapter(Options(dir, fetchCatalog: false)).StartAsync(CancellationToken.None);
        var acp = new JsonRpcConnection(owner.Input, owner.Output, "direct-idle")
        {
            NotificationHandler = (_, p) =>
            {
                if (p?["update"] is JsonObject u) lock (updates) updates.Add((JsonObject)u.DeepClone());
                return Task.CompletedTask;
            },
            RequestHandler = (_, _) => Task.FromResult<JsonNode?>(AcpJson.ToNode(new RequestPermissionResponse
            {
                Outcome = new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId },
            })),
        };
        acp.Start();
        try
        {
            await acp.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
            var sessionId = (await acp.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, McpServers = [] })).SessionId;
            var idle = await acp.SendRequestAsync(GorynychProtocol.MethodSteering, new JsonObject
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hello from idle" }),
                ["_meta"] = new JsonObject { ["gorynych/messageId"] = "idle-claude" },
            });
            Assert.AreEqual(AcpSteering.StartedNewTurn, (string?)idle?["outcome"]);
            await Until(() => { lock (updates) return updates.Any(u => (string?)u["sessionUpdate"] == "agent_message_chunk"); });
            lock (updates) StringAssert.Contains(string.Concat(updates.Where(u => (string?)u["sessionUpdate"] == "agent_message_chunk").Select(u => (string?)u["content"]?["text"])), "hello from idle");
        }
        finally
        {
            await acp.DisposeAsync();
            await owner.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ASteerRacingAFailedStartup_IsRefusedWithoutDelivery()
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "initialize-error"), "");
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false,
            beforeStartup: () => { starting.TrySetResult(); return release.Task; })));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        var prompt = PromptAsync(client, sessionId, "hello");
        await starting.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the prompt is accepted, its process still starting
        var steering = SteerAsync(client, sessionId, "too early", "m-early");
        await Task.Delay(100);
        Assert.IsFalse(steering.IsCompleted, "the steer waits for the accepted prompt's input write");
        release.TrySetResult();
        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => steering);
        Assert.AreEqual(JsonRpcException.InvalidRequest, refused.Code, refused.Message);
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<JsonRpcException>(() => prompt)).Message, "native initialize failed");
        Assert.AreEqual(0, conductor.Feed(sessionId).Journal.SteeredMessageIds().Count, "nothing was accepted");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ACancel_StopsATurnClaudeStartedItself()
    {
        var dir = TestDirs.New();
        var updates = new List<JsonObject>();
        var owner = await new ClaudeAdapter(Options(dir, liveNotes: true)).StartAsync(CancellationToken.None);
        var acp = new JsonRpcConnection(owner.Input, owner.Output, "direct")
        {
            NotificationHandler = (_, p) =>
            {
                if (p?["update"] is JsonObject u) lock (updates) updates.Add((JsonObject)u.DeepClone());
                return Task.CompletedTask;
            },
            RequestHandler = (_, _) => Task.FromResult<JsonNode?>(AcpJson.ToNode(new RequestPermissionResponse
            {
                Outcome = new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId },
            })),
        };
        acp.Start();
        try
        {
            await acp.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
            var sessionId = (await acp.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, McpServers = [] })).SessionId;
            await acp.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = "warm up" }] });

            _ = acp.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
            {
                ["sessionId"] = sessionId, ["sender"] = "peer_kimi", ["text"] = "slow work, please",
            });
            await Until(() => { lock (updates) return updates.Any(u => (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)); });
            await acp.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId });
            await Until(() => { lock (updates) return updates.Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended"); });
            lock (updates)
            {
                var started = updates.FindIndex(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "started");
                var ended = updates.FindIndex(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended");
                Assert.IsTrue(started >= 0 && ended > started, "the agent's own turn is marked started and ended");
            }
            Assert.IsTrue(Controls(sessionId).Any(c => (string?)c["subtype"] == "interrupt"), "the cancel interrupted the own turn");
        }
        finally
        {
            await acp.DisposeAsync();
            await owner.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AMessageClaudeNeverTakes_IsLetGo_AndTheSessionGoesOn()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        await PromptAsync(client, sessionId, "hello");
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(client, sessionId, "swallow this"));
        StringAssert.Contains(failed.Message, "did not take the prompt");
        // Claude runs (it said system/init with "hello"): its stall time (1.5 s) holds, not its startup time (20 s).
        Assert.IsLessThan(15, waited.Elapsed.TotalSeconds, "let go after its stall time, not its startup time");
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, sessionId, "still here")).StopReason);
        StringAssert.Contains(client.Text(), "echo: still here");
    }
}
