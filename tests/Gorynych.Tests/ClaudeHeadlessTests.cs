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
public sealed class ClaudeHeadlessTests
{
    static string FakeClaude()
    {
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var tests = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var exe = Path.Combine(tests, "Gorynych.FakeClaude", "bin", config, "net10.0", TestDirs.Exe("Gorynych.FakeClaude"));
        return File.Exists(exe) ? exe : throw new AssertInconclusiveException($"build Gorynych.FakeClaude first: {exe}");
    }

    static readonly string FakeRoot = Path.Combine(Path.GetTempPath(), "gorynych-fake-claude");

    static ClaudeAdapterOptions Options(string dir, List<string>? log = null, bool liveNotes = false) => new()
    {
        DataDirectory = Path.Combine(dir, "claude"),
        Executable = FakeClaude(),
        ProjectsDirectory = Path.Combine(FakeRoot, "projects"),
        ClaudeStatePath = Path.Combine(dir, "no-claude-state.json"),
        // The fake reads only the reply route from --mcp-config; it starts no server.
        ChannelServerCommand = liveNotes ? ["gorynych-peers-not-started"] : null,
        StartupTimeoutSeconds = 20,
        InterruptGraceMs = 3000,
        CloseTimeoutMs = 3000,
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
        Assert.IsFalse(client.Approvals.Any(a => a.ToolCall.Title?.Contains("list_agents", StringComparison.Ordinal) == true), "list_agents is pre-allowed");
        Assert.IsTrue(client.Approvals.Any(a => a.ToolCall.Title?.Contains("ask_agent", StringComparison.Ordinal) == true), "ask_agent asks the person");

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
    public async Task ALaunchWithoutTheLogin_FailsLoudly_AndNamesTheWayBack()
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "signed-out"), "");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = await NewAsync(client, dir);
        var failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(client, sessionId, "hello"));
        StringAssert.Contains(failed.Message, "--claude-transport tui");
        Assert.AreEqual("", client.Text(), "no turn ran");
    }
}
