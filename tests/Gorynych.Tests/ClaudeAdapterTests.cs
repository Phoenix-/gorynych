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
