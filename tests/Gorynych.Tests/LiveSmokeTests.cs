using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Kimi;

namespace Gorynych.Tests;

/// <summary>
/// Opt-in checks against the real agents on this machine, under the user's own logins.
/// Run with <c>GORYNYCH_LIVE=1</c>; Claude uses <c>--model haiku</c> to spare the limit.
/// The Claude session runs in the repository folder (already trusted) and writes only to a
/// temporary file outside it.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class LiveSmokeTests
{
    static void RequireLive()
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1") Assert.Inconclusive("set GORYNYCH_LIVE=1 to run live agents");
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    static string ServiceExe(string root)
    {
        var bin = Path.Combine(root, "src", "Gorynych.Service", "bin");
        return Directory.EnumerateFiles(bin, "Gorynych.Service.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Claude_FirstPrompt_Approval_Title_Reload_AndOpinion()
    {
        RequireLive();
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var root = RepoRoot();
        var dir = TestDirs.New();
        var target = Path.Combine(dir, "live.txt");
        var adapter = new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "claude"),
            Model = "haiku",
            ChannelServerCommand = [ServiceExe(root), "peers"],
        });
        await using var conductor = TestDirs.Conductor(dir, adapter);
        string sessionId;
        await using (var client = await TestClient.ConnectAsync(conductor, "live-1"))
        {
            client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId });
            sessionId = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
            {
                Cwd = root,
                McpServers = [],
                Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" },
            })).SessionId;
            var result = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest
            {
                SessionId = sessionId,
                Prompt = [new TextContent { Text = $"Use the Write tool to create the file {target} containing the word hi. Then reply with exactly DONE." }],
            });
            Assert.AreEqual(StopReason.EndTurn, result.StopReason);
            Assert.IsTrue(client.Approvals.Any(a => a.ToolCall.Title?.Contains("live.txt", StringComparison.Ordinal) == true), "a Write approval round-tripped");
            Assert.AreEqual("hi", File.ReadAllText(target).Trim());
            StringAssert.Contains(client.Text(), "DONE");

            var provisional = client.Snapshot().First(u => (string?)u["sessionUpdate"] == "session_info_update")["title"]?.ToString();
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline && !client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "session_info_update" && (string?)u["title"] != provisional))
                await Task.Delay(500);
            Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "session_info_update" && (string?)u["title"] != provisional),
                "Claude's generated title arrived");
        }

        await using (var again = await TestClient.ConnectAsync(conductor, "live-2"))
        {
            await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
                AcpJson.ToNode(new LoadSessionRequest { SessionId = sessionId, Cwd = root, McpServers = [] }));
            StringAssert.Contains(again.Text("user_message_chunk"), "live.txt");
            StringAssert.Contains(again.Text(), "DONE");

            var opinion = await again.Rpc.SendRequestAsync("_gorynych/opinion", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["sender"] = "peer_kimi",
                ["text"] = "In one sentence: tabs or spaces for C#?",
            });
            Assert.IsFalse(string.IsNullOrWhiteSpace((string?)opinion?["text"]), "the channel reply came back");
        }
    }

    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Opinions_BothWays_ClaudeAsksKimi_KimiAsksClaude()
    {
        RequireLive();
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var root = RepoRoot();
        var exe = ServiceExe(root);
        var dir = TestDirs.New();
        var pipe = "gorynych-live-" + Guid.NewGuid().ToString("N")[..8];
        await using var conductor = new Gorynych.Service.Conductor(new Gorynych.Service.ConductorOptions
        {
            DataDirectory = dir,
            ServiceId = "live",
            PeerServerCommand = [exe, "peers", "--pipe", pipe],
        }, [
            new ClaudeAdapter(new ClaudeAdapterOptions { DataDirectory = Path.Combine(dir, "claude"), Model = "haiku", ChannelServerCommand = [exe, "peers"] }),
            new KimiAdapter(new KimiAdapterOptions()),
        ]);
        using var stop = new CancellationTokenSource();
        var listening = Gorynych.Service.PipeListener.RunAsync(conductor, pipe, stop.Token);
        await using var client = await TestClient.ConnectAsync(conductor);
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome
        {
            OptionId = r.Options.First(o => o.Kind.Value.StartsWith("allow", StringComparison.Ordinal)).OptionId,
        });

        var claude = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = root, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" },
        })).SessionId;
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = claude, Prompt = [new TextContent { Text = "Reply with exactly READY." }] });

        var kimi = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" },
        })).SessionId;
        try
        {
            // Claude asks Kimi: the request reaches Kimi as a marked turn, Kimi answers with reply.
            var mark = client.Text().Length;
            var asked = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest
            {
                SessionId = claude,
                Prompt = [new TextContent { Text =
                    $"Use the gorynych ask_agent tool to ask the agent session {kimi} this question: " +
                    "\"Tabs or spaces for C#, and why? One sentence.\" Then reply with ANSWER: followed by the answer you got." }],
            });
            Assert.AreEqual(StopReason.EndTurn, asked.StopReason);
            var claudeText = client.Text()[mark..];
            StringAssert.Contains(claudeText, "ANSWER");
            Assert.IsTrue(client.Approvals.Any(a => a.ToolCall.Title == "ask_agent (gorynych)"), "Claude's ask_agent asked for approval");
            var request = client.Snapshot().FirstOrDefault(u => (string?)u["sessionUpdate"] == "user_message_chunk"
                && (string?)u["_meta"]?[GorynychProtocol.MetaOrigin] == "peer");
            Assert.IsNotNull(request, "the question reached Kimi as a marked turn");
            Assert.AreEqual("peer_claude", (string?)request["_meta"]![GorynychProtocol.MetaSender]);
            var opinion = (string?)request["_meta"]![GorynychProtocol.MetaOpinion];
            var replied = client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call"
                && (string?)u["_meta"]?[GorynychProtocol.MetaOpinion] == opinion && ((string?)u["title"] ?? "").Contains("reply", StringComparison.Ordinal));
            Console.WriteLine($"Claude asked Kimi; Kimi answered {(replied ? "with the reply tool" : "in the turn's text")}: {claudeText.Trim()}");

            // Kimi asks Claude: the question reaches Claude through its channel.
            var before = client.Text().Length;
            var result = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest
            {
                SessionId = kimi,
                Prompt = [new TextContent { Text =
                    $"Use the ask_agent tool (gorynych MCP server) to ask the agent session {claude} this question: " +
                    "\"For a single-user desktop app, is SQLite or PostgreSQL the better local store? One sentence.\" " +
                    "Then reply with ANSWER: followed by the answer you got." }],
            });
            Assert.AreEqual(StopReason.EndTurn, result.StopReason);
            var kimiText = client.Text()[before..];
            StringAssert.Contains(kimiText, "ANSWER");
            Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call" && ((string?)u["title"] ?? "").Contains("ask_agent", StringComparison.OrdinalIgnoreCase)),
                "Kimi called ask_agent");
            Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "user_message_chunk"
                && (string?)u["_meta"]?[GorynychProtocol.MetaOrigin] == "channel" && ((string?)u["content"]?["text"] ?? "").Contains("peer_kimi")),
                "the question reached Claude through the channel, from peer_kimi");
        }
        finally
        {
            try { await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = kimi }); }
            catch (JsonRpcException) { }
            stop.Cancel();
            await listening;
        }
    }

    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task PlainClient_ThroughTheBridge_ClaudesQuestionBecomesAnApproval()
    {
        RequireLive();
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var root = RepoRoot();
        var pipe = "gorynych-live-" + Guid.NewGuid().ToString("N")[..8];
        await using var zed = new PlainClient("acp", "--agent", "claude", "--pipe", pipe, "--data", TestDirs.New(), "--claude-model", "haiku");
        var init = await zed.InitializeAsync();
        Assert.AreEqual("Gorynych: Claude Code", (string?)init!["agentInfo"]!["title"]);
        try
        {
            var created = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = root, ["mcpServers"] = new JsonArray() });
            var sessionId = (string)created!["sessionId"]!;
            var result = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "Use the AskUserQuestion tool to ask me one question: which color I prefer, with the options Red and Blue. " +
                        "Then reply with exactly the color I chose.",
                }),
            });
            Assert.AreEqual("end_turn", (string?)result!["stopReason"]);
            var question = zed.Approvals.Single(a => a.Options.Any(o => o.Name.StartsWith("Blue", StringComparison.Ordinal)));
            Assert.AreEqual("Skip", question.Options[^1].Name);
            string text;
            lock (zed.Updates)
                text = string.Concat(zed.Updates.Where(u => (string?)u["sessionUpdate"] == "agent_message_chunk").Select(u => (string?)u["content"]?["text"]));
            StringAssert.Contains(text, "Blue", "the plain client picked the second option, Blue");
        }
        finally
        {
            await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodShutdown, new JsonObject());
        }
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Kimi_DraftIsDeletedNatively_WhenItsClientLeaves()
    {
        RequireLive();
        var dir = TestDirs.New();
        await using var check = await RawKimiAsync();
        await using var conductor = TestDirs.Conductor(dir, new KimiAdapter(new KimiAdapterOptions()));
        string draft;
        await using (var zed = await TestClient.ConnectAsync(conductor, "zed"))
        {
            // What Zed does when it opens a thread view, before any prompt.
            draft = (await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
            {
                Cwd = dir,
                McpServers = [],
                Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" },
            })).SessionId;
            CollectionAssert.Contains(await check.ListAsync(dir), draft, "Kimi keeps an empty native session for the draft");
        }
        Assert.IsNull(conductor.Store.Get(draft), "the service closed and deleted the draft without an error");
        CollectionAssert.DoesNotContain(await check.ListAsync(dir), draft, "Kimi no longer has it");
    }

    /// <summary>A separate <c>kimi acp</c> to look at Kimi's own session list.</summary>
    static async Task<RawAgent> RawKimiAsync()
    {
        var instance = await new KimiAdapter(new KimiAdapterOptions()).StartAsync(CancellationToken.None);
        var rpc = new JsonRpcConnection(instance.Input, instance.Output, "kimi-check");
        rpc.Start();
        await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
        return new RawAgent(instance, rpc);
    }

    sealed class RawAgent(AgentInstance instance, JsonRpcConnection rpc) : IAsyncDisposable
    {
        public async Task<List<string>> ListAsync(string cwd) =>
            (await rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest { Cwd = cwd }))
                .Sessions.Select(s => s.SessionId).ToList();

        public async ValueTask DisposeAsync()
        {
            await rpc.DisposeAsync();
            await instance.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task Kimi_FirstPrompt()
    {
        RequireLive();
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new KimiAdapter(new KimiAdapterOptions()));
        await using var client = await TestClient.ConnectAsync(conductor);
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome
        {
            OptionId = r.Options.First(o => o.Kind.Value.StartsWith("allow", StringComparison.Ordinal)).OptionId,
        });
        var sessionId = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir,
            McpServers = [],
            Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" },
        })).SessionId;
        try
        {
            var result = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest
            {
                SessionId = sessionId,
                Prompt = [new TextContent { Text = "Create a file named hello.txt in the current directory containing the word hi. Then reply with exactly DONE." }],
            });
            Assert.AreEqual(StopReason.EndTurn, result.StopReason);
            Assert.IsTrue(client.Approvals.Count > 0, "Kimi asked for approval");
            StringAssert.Contains(client.Text(), "DONE");
        }
        catch (JsonRpcException e) when (e.Code == JsonRpcException.AuthRequired)
        {
            Assert.Inconclusive($"Kimi refused the turn: {e.Message}");
        }
        finally
        {
            // Leave no test session behind in the user's Kimi history.
            try { await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = sessionId }); }
            catch (JsonRpcException) { }
        }
    }
}
