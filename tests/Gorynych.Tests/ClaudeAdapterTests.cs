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
        // "c", never the "m" of a request the service asks as a turn: the peers server tries the channel first;
        // then this run's tag, so a late reply from before a restart answers nothing new.
        var id = (string)messages[0]!["meta"]!["message_id"]!;
        StringAssert.Matches(id, new System.Text.RegularExpressions.Regex("^c[a-z0-9]{4}-1$"));
        Assert.AreEqual($$"""{"kind":"opinion","message_id":"{{id}}"}""", messages[0]!["meta"]!.ToJsonString());
        Assert.AreNotEqual(id, await NextIdAsync(), "another run counts from 1 too, under another tag");
        Assert.AreEqual("""{"kind":"report"}""", messages[1]!["meta"]!.ToJsonString());
        StringAssert.StartsWith((string?)messages[0]!["content"], "[Opinion request · from session_id k1 (kimi)]");

        Assert.AreEqual(false, (bool?)channel.Reply(new System.Text.Json.Nodes.JsonObject { ["message_id"] = "m1", ["text"] = "Spaces." })["delivered"],
            "a service turn's message_id is not the channel's");
        Assert.AreEqual(true, (bool?)channel.Reply(new System.Text.Json.Nodes.JsonObject { ["message_id"] = id, ["text"] = "Spaces." })["delivered"]);
        Assert.AreEqual("Spaces.", await asking);
    }

    /// <summary>The first message_id of a channel host of another run.</summary>
    static async Task<string?> NextIdAsync()
    {
        var other = new ChannelHost(["svc.exe", "peers"]);
        _ = other.AskAsync("s1", "?", null, CancellationToken.None);
        return (string?)(await other.NextAsync("s1", CancellationToken.None))["messages"]![0]!["meta"]!["message_id"];
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
        var id = (string)(await channel.NextAsync("s1", CancellationToken.None))["messages"]![0]!["meta"]!["message_id"]!;

        // As Claude reports them in UserPromptSubmit (recorded in hooks-channel.jsonl): the channel tag, its meta as attributes.
        var request = $"<channel source=\"gorynych\" kind=\"opinion\" message_id=\"{id}\">\n[Opinion request · from an agent outside Gorynych (Claude Code, D:\\w)]\nWhy?\n</channel>";
        var report = "<channel source=\"gorynych\" kind=\"report\">\n[Report · from session_id k2 (kimi) · your child session · its turn ended]\nok\n</channel>";
        Assert.IsNull(channel.MetaFor("other-session", request), "only the session it was pushed into");
        var asked = ClaudeHooks.ForeignPrompt(request, "p1", channel.MetaFor("s1", request)).Meta!;
        Assert.AreEqual("channel", (string?)asked["gorynych/origin"], "origin still says how it arrived");
        Assert.AreEqual(id, (string?)asked["gorynych/opinion"]);
        Assert.AreEqual("outside_claude-code", (string?)asked["gorynych/sender"]);
        Assert.AreEqual(@"D:\w", (string?)asked["gorynych/outside"]!["folder"]);
        var told = ClaudeHooks.ForeignPrompt(report, "p2", channel.MetaFor("s1", report)).Meta!;
        Assert.AreEqual(("channel", "child_kimi", "k2"), ((string?)told["gorynych/origin"], (string?)told["gorynych/sender"], (string?)told["gorynych/fromSessionId"]));
        Assert.IsNull(told["gorynych/opinion"]);
        Assert.IsNull(channel.MetaFor("s1", request), "each message is matched once");
        Assert.AreEqual(1, ClaudeHooks.ForeignPrompt("typed in the terminal", "p3").Meta!.Count);

        channel.Reply(new System.Text.Json.Nodes.JsonObject { ["message_id"] = id, ["text"] = "Because." });
        Assert.AreEqual("Because.", await asking);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task PeerTools_ComeThroughTheLaunchFlags_AndNeedNoApproval()
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
            Transport = ClaudeTransport.Tui,
            DataDirectory = Path.Combine(dir, "claude"),
            Executable = FakeClaude(),
            ChannelServerCommand = [@"C:\svc\Gorynych.Service.exe", "peers"],
            Channels = ChannelConsent.Accept, // given up front: no question, and the notice is confirmed
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
        Assert.AreEqual("list_agents:allow ask_agent:allow", client.Text(), "both pre-allowed, though the person would deny anything");
        Assert.AreEqual(0, client.Approvals.Count);

        var args = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetTempPath(), "gorynych-fake-claude", sessionId + ".args.json")))!
            .AsArray().Select(a => (string)a!).ToList();
        var allow = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(args[args.IndexOf("--settings") + 1]))!["permissions"]!["allow"]!.AsArray();
        CollectionAssert.AreEquivalent(ChannelHost.AllowedTools, allow.Select(r => (string?)r).ToArray(), "allowed by the launch too, as headless");
        CollectionAssert.Contains(args, "server:gorynych");
        Assert.IsTrue(File.Exists(NoticeFile(sessionId)), "the adapter confirmed Claude's development-channels notice");
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

    static string FakeFile(string sessionId, string extension) =>
        Path.Combine(Path.GetTempPath(), "gorynych-fake-claude", sessionId + extension);

    /// <summary>Written by the fake when the adapter confirmed its development-channels notice with Enter.</summary>
    static string NoticeFile(string sessionId) => FakeFile(sessionId, ".notice.txt");

    static List<string> LaunchArgs(string sessionId) =>
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FakeFile(sessionId, ".args.json")))!.AsArray().Select(a => (string)a!).ToList();

    static ClaudeAdapter ChannelAdapter(string dir, ChannelConsent channels = ChannelConsent.Ask) => new(new ClaudeAdapterOptions
    {
        Transport = ClaudeTransport.Tui,
        DataDirectory = Path.Combine(dir, "claude"),
        Executable = FakeClaude(),
        ChannelServerCommand = [@"C:\svc\Gorynych.Service.exe", "peers"],
        Channels = channels,
        StartupTimeoutSeconds = 30,
    });

    static async Task<string> NewClaudeSessionAsync(TestClient client, string dir) =>
        (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir,
            McpServers = [],
            Meta = new System.Text.Json.Nodes.JsonObject { ["gorynych/agent"] = "claude" },
        })).SessionId;

    static Task<PromptResponse> PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static RequestPermissionRequest[] ChannelQuestions(TestClient client)
    {
        lock (client.Approvals) return client.Approvals.Where(a => a.ToolCall.ToolCallId == ClaudeHooks.ChannelConsentToolCallId).ToArray();
    }

    static bool? KeptAnswer(string dir)
    {
        var path = Path.Combine(dir, "claude", "channels.json");
        return File.Exists(path) ? (bool?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["accepted"] : null;
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task DevelopmentChannels_AreAskedOnce_AndTheNoticeIsConfirmedOnlyAfterTheAllow()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        await using (var conductor = TestDirs.Conductor(dir, ChannelAdapter(dir)))
        await using (var client = await TestClient.ConnectAsync(conductor))
        {
            var first = await NewClaudeSessionAsync(client, dir);
            Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, first, "hello")).StopReason);
            var question = ChannelQuestions(client).Single();
            Assert.AreEqual(first, question.SessionId, "asked on the session that launches");
            Assert.AreEqual(ClaudeHooks.AllowOptionId, question.Options[0].OptionId);
            StringAssert.Contains(question.ToolCall.Title, "development channels");
            StringAssert.Contains(((TextContent)((Content)question.ToolCall.Content!.Single()).ContentValue).Text, "research preview");
            ZedRules.CheckPermission(question);

            CollectionAssert.Contains(LaunchArgs(first), "--dangerously-load-development-channels");
            Assert.IsTrue(File.Exists(NoticeFile(first)), "the notice is confirmed once the person allowed it");
            Assert.AreEqual(true, KeptAnswer(dir), "kept in the service's own data");
            Assert.IsTrue(client.Snapshot().Any(u => (string?)u["toolCallId"] == ClaudeHooks.ChannelConsentToolCallId && (string?)u["status"] == "completed"));

            var second = await NewClaudeSessionAsync(client, dir);
            Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, second, "hello again")).StopReason);
            Assert.AreEqual(1, ChannelQuestions(client).Length, "asked once");
            CollectionAssert.Contains(LaunchArgs(second), "--dangerously-load-development-channels");
        }

        // The answer outlives the service.
        await using var restarted = TestDirs.Conductor(dir, ChannelAdapter(dir));
        await using var later = await TestClient.ConnectAsync(restarted, "later");
        var third = await NewClaudeSessionAsync(later, dir);
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(later, third, "after restart")).StopReason);
        Assert.AreEqual(0, ChannelQuestions(later).Length);
        Assert.IsTrue(File.Exists(NoticeFile(third)));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task UnansweredChannelQuestion_IsAskedAgain_AndADeclineRunsClaudeWithoutTheChannel()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, ChannelAdapter(dir));
        await using var client = await TestClient.ConnectAsync(conductor);

        // Dismissed: this launch runs without the channel, and nothing is kept.
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new RequestPermissionOutcomeCancelled());
        var first = await NewClaudeSessionAsync(client, dir);
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, first, "hello")).StopReason);
        CollectionAssert.DoesNotContain(LaunchArgs(first), "--dangerously-load-development-channels");
        Assert.IsNull(KeptAnswer(dir));

        // Asked again at the next launch; declined.
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = ClaudeHooks.DenyOptionId });
        var second = await NewClaudeSessionAsync(client, dir);
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, second, "hello")).StopReason);
        Assert.AreEqual(2, ChannelQuestions(client).Length);
        var args = LaunchArgs(second);
        CollectionAssert.DoesNotContain(args, "--dangerously-load-development-channels");
        CollectionAssert.DoesNotContain(args, "--append-system-prompt", "no channel authorization without the channel");
        Assert.IsFalse(File.Exists(NoticeFile(second)));
        Assert.AreEqual(false, KeptAnswer(dir));
        Assert.IsTrue(client.Snapshot().Any(u => (string?)u["toolCallId"] == ClaudeHooks.ChannelConsentToolCallId && (string?)u["status"] == "failed"));

        // A decline is kept too: the next session is not asked.
        var third = await NewClaudeSessionAsync(client, dir);
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, third, "hello")).StopReason);
        Assert.AreEqual(2, ChannelQuestions(client).Length);
        CollectionAssert.DoesNotContain(LaunchArgs(third), "--dangerously-load-development-channels");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task CancellingThePrompt_WithdrawsTheChannelQuestion_AndLaunchesNothing()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, ChannelAdapter(dir));
        await using var client = await TestClient.ConnectAsync(conductor);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Approve = r =>
        {
            asked.TrySetResult();
            return new TaskCompletionSource<RequestPermissionOutcome>().Task; // the person never answers
        };
        var session = await NewClaudeSessionAsync(client, dir);
        var prompt = PromptAsync(client, session, "hello");
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new System.Text.Json.Nodes.JsonObject { ["sessionId"] = session });

        Assert.AreEqual(StopReason.Cancelled, (await prompt.WaitAsync(TimeSpan.FromSeconds(20))).StopReason);
        Assert.IsFalse(File.Exists(FakeFile(session, ".args.json")), "Claude was not started");
        Assert.IsNull(KeptAnswer(dir));
        // The question's tool call is closed, so the client does not keep showing it pending.
        await Until(() => client.Snapshot().Any(u => (string?)u["toolCallId"] == ClaudeHooks.ChannelConsentToolCallId
            && (string?)u["status"] == "failed"
            && ((string?)u["content"]?[0]?["content"]?["text"] ?? "").StartsWith("Withdrawn", StringComparison.Ordinal)));

        // The next prompt asks again.
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId });
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, session, "hello")).StopReason);
        Assert.AreEqual(2, ChannelQuestions(client).Length);
        Assert.IsTrue(File.Exists(NoticeFile(session)));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AnOpenChannelQuestion_HoldsUpNoOtherSession_AndLaterLaunchesFollowTheAnswer()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, ChannelAdapter(dir));
        await using var client = await TestClient.ConnectAsync(conductor);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<RequestPermissionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Approve = r =>
        {
            asked.TrySetResult();
            return answer.Task; // the person answers only later
        };
        var first = await NewClaudeSessionAsync(client, dir);
        var firstPrompt = PromptAsync(client, first, "hello");
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // While the question is open, other sessions start at once, without the channel, and nobody asks again.
        var second = await NewClaudeSessionAsync(client, dir);
        var third = await NewClaudeSessionAsync(client, dir);
        var others = await Task.WhenAll(PromptAsync(client, second, "a"), PromptAsync(client, third, "b")).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.IsTrue(others.All(r => r.StopReason == StopReason.EndTurn));
        Assert.AreEqual(1, ChannelQuestions(client).Length, "one question at a time");
        CollectionAssert.DoesNotContain(LaunchArgs(second), "--dangerously-load-development-channels");
        CollectionAssert.DoesNotContain(LaunchArgs(third), "--dangerously-load-development-channels");
        Assert.IsFalse(firstPrompt.IsCompleted, "the asking session waits for the person");

        answer.SetResult(new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId });
        Assert.AreEqual(StopReason.EndTurn, (await firstPrompt.WaitAsync(TimeSpan.FromSeconds(30))).StopReason);
        CollectionAssert.Contains(LaunchArgs(first), "--dangerously-load-development-channels");

        // Launches after the answer follow it without asking.
        var fourth = await NewClaudeSessionAsync(client, dir);
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, fourth, "c")).StopReason);
        Assert.AreEqual(1, ChannelQuestions(client).Length);
        CollectionAssert.Contains(LaunchArgs(fourth), "--dangerously-load-development-channels");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "holds the file open to make it unreadable, which only Windows refuses")]
    public void AChannelsFileThatCannotBeRead_CountsAsNotAskedYet()
    {
        var path = Path.Combine(TestDirs.New(), "channels.json");
        File.WriteAllText(path, """{"accepted":true}""");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.IsNull(new ChannelConsentStore(path).Answer);
        Assert.AreEqual(true, new ChannelConsentStore(path).Answer);
    }

    static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("the condition did not hold in time");
            await Task.Delay(25);
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task StuckLaunch_FailsThePrompt_AndTheNextPromptStartsAFreshProcess()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        var log = new List<string>();
        var launches = 0;
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(new ClaudeAdapterOptions
        {
            Transport = ClaudeTransport.Tui,
            DataDirectory = Path.Combine(dir, "claude"),
            Executable = FakeClaude(),
            StartupTimeoutSeconds = 3,
            // Given up when the dialog it stops at is on the screen, not after a time a loaded machine
            // may need for the start; the fresh launch after it is never given up here.
            GiveUpStartup = async (screen, ct) =>
            {
                if (Interlocked.Increment(ref launches) > 1) await Task.Delay(Timeout.Infinite, ct);
                while (!screen().Contains("a dialog the adapter does not know", StringComparison.Ordinal)) await Task.Delay(25, ct);
            },
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
    public async Task ATurnClaudeRunsOnItsOwn_IsMarked_FromItsPromptToItsStop()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        var adapter = new ClaudeAdapter(new ClaudeAdapterOptions
        {
            Transport = ClaudeTransport.Tui,
            DataDirectory = Path.Combine(dir, "claude"),
            Executable = FakeClaude(),
            StartupTimeoutSeconds = 30,
        });
        await using var conductor = TestDirs.Conductor(dir, adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var created = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir,
            McpServers = [],
            Meta = new System.Text.Json.Nodes.JsonObject { ["gorynych/agent"] = "claude" },
        });

        // The fake follows this turn with one of its own, as for a message through its channel.
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = created.SessionId, Prompt = [new TextContent { Text = "answer, then go on on your own" }] });
        static string? Mark(System.Text.Json.Nodes.JsonObject u) => (string?)u["_meta"]?["gorynych/agentTurn"];
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !client.Snapshot().Any(u => Mark(u) == "ended")) await Task.Delay(100);

        var updates = client.Snapshot().ToList();
        var started = updates.FindIndex(u => Mark(u) == "started");
        var ended = updates.FindIndex(u => Mark(u) == "ended");
        Assert.IsTrue(started >= 0 && ended > started, "the turn of Claude's own is marked from its prompt to its Stop");
        Assert.AreEqual(1, updates.Count(u => Mark(u) is not null) / 2, "one turn of its own; the client's prompt is not marked");
        Assert.AreEqual(("user_message_chunk", "channel"), ((string?)updates[started]["sessionUpdate"], (string?)updates[started]["_meta"]!["gorynych/origin"]));
        Assert.AreEqual("session_info_update", (string?)updates[ended]["sessionUpdate"]);
        Assert.AreEqual(0, conductor.Feed(created.SessionId).Users, "the service holds the session no longer");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Session_FromFirstPrompt_Approval_Paste_AndTitle()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var dir = TestDirs.New();
        var adapter = new ClaudeAdapter(new ClaudeAdapterOptions
        {
            Transport = ClaudeTransport.Tui,
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
            Transport = ClaudeTransport.Tui,
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
