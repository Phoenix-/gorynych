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
        bool fetchCatalog = true, string permissionMode = "default", int stallMs = 1500) => new()
    {
        BeforeInterrupt = beforeInterrupt,
        FetchCatalog = fetchCatalog,
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

    static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }

    static string? Mark(JsonObject u) => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn];

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
        StringAssert.Contains(args[args.IndexOf("--append-system-prompt") + 1], "[Opinion request");

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
    [DataRow("signed-out", "--claude-transport tui")]
    [DataRow("no-account", "--claude-transport tui")]
    [DataRow("api-key", "remove ANTHROPIC_API_KEY")]
    [DataRow("api-key-init", "remove ANTHROPIC_API_KEY")]
    [DataRow("auth-token", "reports no plan")]
    [DataRow("bedrock", "runs through bedrock")]
    public async Task ALaunchOffThePlan_FailsLoudly_AndRunsNothing(string marker, string hint)
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, marker), "");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        var failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(client, sessionId, "hello"));
        StringAssert.Contains(failed.Message, hint);
        Assert.AreEqual("", client.Text(), "no answer came");
        // Still off the plan: the next prompt fails the same way instead of running unchecked.
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(client, sessionId, "again"))).Message, hint);
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
    public async Task AClaudeSlowToStart_IsNotTakenForOneThatDroppedThePrompt_AndAnOffPlanInitSaysWhy()
    {
        // Claude says system/init once its MCP servers are up, which may take long on a slow machine:
        // its silence until then is its start, not a message it did not take. Were it taken for one,
        // the person would read "did not take the prompt" instead of how to get back on the plan.
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

        var failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => prompt);
        StringAssert.Contains(failed.Message, "remove ANTHROPIC_API_KEY");
        Assert.IsFalse(failed.Message.Contains("did not take the prompt", StringComparison.Ordinal), failed.Message);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AStoppedClaude_LeavesNothingBehind_AndTheNextLaunchStartsClean()
    {
        var dir = TestDirs.New();
        var marker = Path.Combine(dir, "api-key-init");
        File.WriteAllText(marker, "");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        // The fake goes on with its turn after the system/init the adapter stops it for: those lines must be dropped.
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
