using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

/// <summary>
/// The Claude adapter end to end — ConPTY launch, http hooks, approvals, paste after Stop,
/// transcript title — against Gorynych.FakeClaude instead of the real binary.
/// </summary>
[TestClass]
public sealed class ClaudeAdapterTests
{
    static string FakeClaude()
    {
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var tests = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var exe = Path.Combine(tests, "Gorynych.FakeClaude", "bin", config, "net10.0", "Gorynych.FakeClaude.exe");
        return File.Exists(exe) ? exe : throw new AssertInconclusiveException($"build Gorynych.FakeClaude first: {exe}");
    }

    [TestMethod]
    public async Task ChannelMessages_CarryTheirKind_AndNoSenderLabel()
    {
        var channel = new ChannelHost(["svc.exe", "peers"]);
        var asking = channel.AskAsync("s1", "[Opinion request · from session_id k1 (kimi)]\nTabs?", null, CancellationToken.None);
        await channel.NotifyAsync("s1", "[Report · from session_id k2 (kimi) · your child session · its turn ended]\nok", null, CancellationToken.None);

        var messages = (await channel.NextAsync("s1", CancellationToken.None))["messages"]!.AsArray();
        Assert.AreEqual(2, messages.Count);
        // "c", never the "m" of a request the service asks as a turn: the peers server tries the channel first.
        Assert.AreEqual("""{"kind":"opinion","message_id":"c1"}""", messages[0]!["meta"]!.ToJsonString());
        Assert.AreEqual("""{"kind":"report"}""", messages[1]!["meta"]!.ToJsonString());
        StringAssert.StartsWith((string?)messages[0]!["content"], "[Opinion request · from session_id k1 (kimi)]");

        Assert.AreEqual(false, (bool?)channel.Reply(new System.Text.Json.Nodes.JsonObject { ["message_id"] = "m1", ["text"] = "Spaces." })["delivered"],
            "a service turn's message_id is not the channel's");
        Assert.AreEqual(true, (bool?)channel.Reply(new System.Text.Json.Nodes.JsonObject { ["message_id"] = "c1", ["text"] = "Spaces." })["delivered"]);
        Assert.AreEqual("Spaces.", await asking);
    }

    [TestMethod]
    public async Task AChannelMessage_IsJournaledWithItsRequestsMeta_AsInATurn()
    {
        var channel = new ChannelHost(["svc.exe", "peers"]);
        var outside = new System.Text.Json.Nodes.JsonObject { ["client"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "claude-code" }, ["folder"] = @"D:\w" };
        var asking = channel.AskAsync("s1", "[Opinion request · from an agent outside Gorynych (Claude Code, D:\\w)]\nWhy?",
            new System.Text.Json.Nodes.JsonObject { ["gorynych/sender"] = "outside_claude-code", ["gorynych/outside"] = outside.DeepClone() }, CancellationToken.None);
        await channel.NotifyAsync("s1", "[Report · from session_id k2 (kimi) · your child session · its turn ended]\nok",
            new System.Text.Json.Nodes.JsonObject { ["gorynych/sender"] = "child_kimi", ["gorynych/fromSessionId"] = "k2" }, CancellationToken.None);
        await channel.NextAsync("s1", CancellationToken.None);

        // As Claude reports them in UserPromptSubmit (recorded in hooks-channel.jsonl): the channel tag, its meta as attributes.
        var request = "<channel source=\"gorynych\" kind=\"opinion\" message_id=\"c1\">\n[Opinion request · from an agent outside Gorynych (Claude Code, D:\\w)]\nWhy?\n</channel>";
        var report = "<channel source=\"gorynych\" kind=\"report\">\n[Report · from session_id k2 (kimi) · your child session · its turn ended]\nok\n</channel>";
        Assert.IsNull(channel.MetaFor("other-session", request), "only the session it was pushed into");
        var asked = ClaudeHooks.ForeignPrompt(request, "p1", channel.MetaFor("s1", request)).Meta!;
        Assert.AreEqual("channel", (string?)asked["gorynych/origin"], "origin still says how it arrived");
        Assert.AreEqual("c1", (string?)asked["gorynych/opinion"]);
        Assert.AreEqual("outside_claude-code", (string?)asked["gorynych/sender"]);
        Assert.AreEqual(@"D:\w", (string?)asked["gorynych/outside"]!["folder"]);
        var told = ClaudeHooks.ForeignPrompt(report, "p2", channel.MetaFor("s1", report)).Meta!;
        Assert.AreEqual(("channel", "child_kimi", "k2"), ((string?)told["gorynych/origin"], (string?)told["gorynych/sender"], (string?)told["gorynych/fromSessionId"]));
        Assert.IsNull(told["gorynych/opinion"]);
        Assert.IsNull(channel.MetaFor("s1", request), "each message is matched once");
        Assert.AreEqual(1, ClaudeHooks.ForeignPrompt("typed in the terminal", "p3").Meta!.Count);

        channel.Reply(new System.Text.Json.Nodes.JsonObject { ["message_id"] = "c1", ["text"] = "Because." });
        Assert.AreEqual("Because.", await asking);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task PeerTools_ComeThroughTheLaunchFlags_AndOnlyAskAgentNeedsApproval()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        await using var conductor = new Gorynych.Service.Conductor(new Gorynych.Service.ConductorOptions
        {
            DataDirectory = dir,
            ServiceId = "t",
            PeerServerCommand = [@"C:\svc\Gorynych.Service.exe", "peers", "--pipe", "p"],
        }, [new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "claude"),
            Executable = FakeClaude(),
            ChannelServerCommand = [@"C:\svc\Gorynych.Service.exe", "peers"],
            StartupTimeoutSeconds = 30,
        })]);
        await using var client = await TestClient.ConnectAsync(conductor);
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = ClaudeHooks.DenyOptionId });
        var sessionId = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir,
            McpServers = [new McpServerStdio { Name = "other", Command = "other.exe", Args = ["--x"], Env = [new EnvVariable { Name = "K", Value = "V" }] }],
            Meta = new System.Text.Json.Nodes.JsonObject { ["gorynych/agent"] = "claude" },
        })).SessionId;

        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = "try the peers tools" }] });
        Assert.AreEqual("list_agents:allow ask_agent:deny", client.Text(), "list_agents is pre-allowed, ask_agent asks the person");
        Assert.AreEqual("ask_agent (gorynych)", client.Approvals.Single().ToolCall.Title);

        var args = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "gorynych-fake-claude", sessionId + ".args.json")))!
            .AsArray().Select(a => (string)a!).ToList();
        CollectionAssert.Contains(args, "server:gorynych");
        Assert.AreEqual(ChannelHost.SystemPrompt + "\n\n" + Gorynych.Service.PeersServer.SystemPrompt, args[args.IndexOf("--append-system-prompt") + 1],
            "the channel's authorization, then the service's systemPrompt (append), which the adapter advertises");
        Assert.AreEqual(1, args.Count(a => a == "--append-system-prompt"));
        var mcp = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(args[args.IndexOf("--mcp-config") + 1]))!["mcpServers"]!;
        var gorynych = mcp["gorynych"]!["args"]!.AsArray().Select(a => (string)a!).ToArray();
        CollectionAssert.AreEqual(new[] { "peers", "--pipe", "p", "--token" }, gorynych[..4]);
        Assert.AreEqual("--channel-url", gorynych[5], "one server is both the peer tools and the channel");
        StringAssert.Contains(gorynych[6], $"/channel/{sessionId}/");
        Assert.AreEqual(@"C:\svc\Gorynych.Service.exe", (string?)mcp["gorynych"]!["command"]);
        Assert.AreEqual("other.exe", (string?)mcp["other"]!["command"], "the client's own MCP servers reach Claude too");
        Assert.AreEqual("V", (string?)mcp["other"]!["env"]!["K"]);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task StuckLaunch_FailsThePrompt_AndTheNextPromptStartsAFreshProcess()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        var log = new List<string>();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "claude"),
            Executable = FakeClaude(),
            StartupTimeoutSeconds = 3,
            Log = m => { lock (log) log.Add($"{DateTime.Now:HH:mm:ss.fff} {m}"); },
        }));
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir,
            McpServers = [],
            Meta = new System.Text.Json.Nodes.JsonObject { ["gorynych/agent"] = "claude" },
        })).SessionId;

        var stuck = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt,
            AcpJson.ToNode(new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = "hang here" }] })));
        StringAssert.Contains(stuck.Message, "did not start the turn");
        StringAssert.Contains(stuck.Message, "a dialog the adapter does not know", "the error shows the last screen");

        PromptResponse next;
        try
        {
            next = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = "hello" }] });
        }
        catch (JsonRpcException e)
        {
            lock (log) throw new AssertFailedException(e.Message + Environment.NewLine + string.Join(Environment.NewLine, log));
        }
        Assert.AreEqual(StopReason.EndTurn, next.StopReason);
        Assert.AreEqual("echo: hello", client.Text());
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Session_FromFirstPrompt_Approval_Paste_AndTitle()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        var adapter = new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "claude"),
            Executable = FakeClaude(),
            StartupTimeoutSeconds = 30,
            PasteEnterDelayMs = 200,
        });
        await using var conductor = TestDirs.Conductor(dir, adapter);
        await using var client = await TestClient.ConnectAsync(conductor);

        var created = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir,
            McpServers = [],
            Meta = new System.Text.Json.Nodes.JsonObject { ["gorynych/agent"] = "claude" },
        });

        // First prompt: the launch argument. It asks for a Write approval.
        var first = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = created.SessionId, Prompt = [new TextContent { Text = "please write a file" }] });
        Assert.AreEqual(StopReason.EndTurn, first.StopReason);
        Assert.AreEqual("Write fake.txt", client.Approvals.Single().ToolCall.Title);
        Assert.AreEqual("wrote it", client.Text());
        CollectionAssert.IsSubsetOf(new[] { "tool_call", "tool_call_update", "agent_message_chunk" }, client.Kinds());
        var launched = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "gorynych-fake-claude", created.SessionId + ".args.json")))!
            .AsArray().Select(a => (string)a!).ToList();
        CollectionAssert.DoesNotContain(launched, "--append-system-prompt", "no channel and no peers server: nothing to add");
        Assert.IsNotNull(client.Snapshot().Last(u => (string?)u["sessionUpdate"] == "agent_message_chunk")["messageId"], "Claude's own message_id");

        // The generated title comes from the transcript a moment later.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !client.Snapshot().Any(u => (string?)u["title"] == "Fake title")) await Task.Delay(200);
        Assert.IsTrue(client.Snapshot().Any(u => (string?)u["title"] == "Fake title"), "ai-title from the transcript");

        // Next prompt: a bracketed paste plus Enter into the running TUI, after Stop.
        var second = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = created.SessionId, Prompt = [new TextContent { Text = "and now something\nmulti-line" }] });
        Assert.AreEqual(StopReason.EndTurn, second.StopReason);
        StringAssert.Contains(client.Text(), "echo: and now something");

        // A denied approval reaches Claude as a deny decision and fails the tool call in the feed.
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = ClaudeHooks.DenyOptionId });
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = created.SessionId, Prompt = [new TextContent { Text = "write again" }] });
        StringAssert.EndsWith(client.Text(), "not allowed");
        Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call_update" && (string?)u["status"] == "failed"));

        // AskUserQuestion is a form (elicitation/create), not an approval.
        client.Fill = form => new ElicitationAcceptAction { Content = new() { ["q1"] = "Blue" } };
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = created.SessionId, Prompt = [new TextContent { Text = "a question for you" }] });
        StringAssert.EndsWith(client.Text(), "you chose Blue");
        var asked = client.Forms.Single();
        Assert.AreEqual("Which color?", asked.Message);
        Assert.AreEqual(created.SessionId, asked.SessionId);
        Assert.IsNotNull(asked.ToolCallId);
        Assert.AreEqual(2, client.Approvals.Count, "the question did not become an approval");

        // The adapter lists the session with its title.
        var list = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
        Assert.AreEqual("Fake title", list.Sessions.Single(s => s.SessionId == created.SessionId).Title);

        // After a service restart the next prompt relaunches Claude with --resume.
        await client.DisposeAsync();
        await conductor.DisposeAsync();
        var log = new List<string>();
        await using var restarted = TestDirs.Conductor(dir, new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "claude"),
            Executable = FakeClaude(),
            StartupTimeoutSeconds = 30,
            Log = m => { lock (log) log.Add(m); },
        }));
        await using var later = await TestClient.ConnectAsync(restarted, "later");
        var resumed = await later.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = created.SessionId, Prompt = [new TextContent { Text = "after restart" }] });
        Assert.AreEqual(StopReason.EndTurn, resumed.StopReason);
        Assert.AreEqual("echo: after restart", later.Text());
        lock (log) Assert.IsTrue(log.Any(l => l.Contains($"claude {created.SessionId}: launching") && l.EndsWith("(resume)", StringComparison.Ordinal)), string.Join(Environment.NewLine, log));
    }
}
