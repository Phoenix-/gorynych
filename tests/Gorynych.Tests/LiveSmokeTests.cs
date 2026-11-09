using System.Diagnostics;
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

    /// <summary>The Claude transport the live tests run: <c>GORYNYCH_CLAUDE_TRANSPORT=tui</c>, else headless.</summary>
    static ClaudeTransport LiveClaudeTransport() =>
        Environment.GetEnvironmentVariable("GORYNYCH_CLAUDE_TRANSPORT") == "tui" ? ClaudeTransport.Tui : ClaudeTransport.Headless;

    static string ServiceExe(string root)
    {
        var bin = Path.Combine(root, "src", "Gorynych.Service", "bin");
        return Directory.EnumerateFiles(bin, TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Claude_FirstPrompt_Approval_Title_Reload_AndOpinion()
    {
        RequireLive();
        var tui = LiveClaudeTransport() == ClaudeTransport.Tui;
        if (tui && !OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var root = RepoRoot();
        var dir = TestDirs.New();
        var target = Path.Combine(dir, "live.txt");
        var adapter = new ClaudeAdapter(new ClaudeAdapterOptions
        {
            Transport = LiveClaudeTransport(),
            DataDirectory = Path.Combine(dir, "claude"),
            Model = "haiku",
            ChannelServerCommand = [ServiceExe(root), "peers"],
            Channels = ChannelConsent.Accept, // a live run answers up front
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

            // Headless Claude writes no generated title; the service's provisional one stays.
            if (tui)
            {
            var provisional = client.Snapshot().First(u => (string?)u["sessionUpdate"] == "session_info_update")["title"]?.ToString();
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline && !client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "session_info_update" && (string?)u["title"] != provisional))
                await Task.Delay(500);
            Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "session_info_update" && (string?)u["title"] != provisional),
                "Claude's generated title arrived");
            }
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

            // The live note's user message: through the channel (TUI) or Claude's input (headless).
            JsonObject? channelMessage = null;
            for (var i = 0; i < 100 && channelMessage is null; i++, await Task.Delay(100))
                channelMessage = again.Snapshot().FirstOrDefault(u => (string?)u["sessionUpdate"] == "user_message_chunk"
                    && (string?)u["_meta"]?[GorynychProtocol.MetaOrigin] == (tui ? "channel" : "live"));
            Assert.IsNotNull(channelMessage, "the live note reached the feed");
            var messageId = (string?)channelMessage["messageId"];
            Assert.IsFalse(string.IsNullOrEmpty(messageId));
            Assert.IsNotNull(channelMessage["_meta"]?[GorynychProtocol.MetaOpinion], "it carries its request's message_id");
            if (!tui) return;
            // The channel message's messageId is Claude's own prompt_id, kept in its transcript as promptId.
            var transcript = File.ReadAllLines(Gorynych.Adapters.Claude.TranscriptTitles.ExpectedPath(root, sessionId));
            Assert.IsTrue(transcript.Any(l => l.Contains($"\"promptId\":\"{messageId}\"", StringComparison.Ordinal) && l.Contains("<channel", StringComparison.Ordinal)),
                $"Claude's transcript has the channel message with promptId {messageId}");
        }
    }

    /// <summary>
    /// Switching the transport needs no migration: a session started headless continues in the TUI,
    /// and one started in the TUI continues headless, since both use Claude's own transcripts and
    /// the adapter's one session list.
    /// </summary>
    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Claude_SessionsMoveBetweenTransports_BothWays()
    {
        RequireLive();
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("the TUI transport needs ConPTY");
        var root = RepoRoot();
        var dir = TestDirs.New();
        ClaudeAdapter Adapter(ClaudeTransport transport) => new(new ClaudeAdapterOptions
        {
            Transport = transport, DataDirectory = Path.Combine(dir, "claude"), Model = "haiku",
        });
        async Task<(string SessionId, string Text)> Turn(ClaudeTransport transport, string? sessionId, string text)
        {
            await using var conductor = TestDirs.Conductor(dir, Adapter(transport));
            await using var client = await TestClient.ConnectAsync(conductor, "live-" + transport);
            client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId });
            if (sessionId is null)
                sessionId = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
                {
                    Cwd = root, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" },
                })).SessionId;
            else
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume,
                    AcpJson.ToNode(new ResumeSessionRequest { SessionId = sessionId, Cwd = root, McpServers = [] }));
            var result = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });
            Assert.AreEqual(StopReason.EndTurn, result.StopReason);
            Console.WriteLine($"{transport}: {client.Text().Trim()}");
            return (sessionId, client.Text());
        }
        foreach (var (first, second) in new[] { (ClaudeTransport.Headless, ClaudeTransport.Tui), (ClaudeTransport.Tui, ClaudeTransport.Headless) })
        {
            var word = first == ClaudeTransport.Headless ? "PELICAN" : "MARMOT";
            var (sessionId, _) = await Turn(first, null, $"Remember the word {word}. Reply with exactly OK.");
            var (_, answer) = await Turn(second, sessionId, "Which word did I ask you to remember? Reply with that word only.");
            StringAssert.Contains(answer.ToUpperInvariant(), word, $"a session started with {first} continues with {second}");
        }
    }

    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Claude_NotRunning_IsStartedForAQuestion_AnswersWithReply_AndIsClosedAgain()
    {
        RequireLive();
        if (LiveClaudeTransport() == ClaudeTransport.Tui && !OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var root = RepoRoot();
        var exe = ServiceExe(root);
        var dir = TestDirs.New();
        var pipe = "gorynych-live-" + Guid.NewGuid().ToString("N")[..8];
        await using var conductor = new Gorynych.Service.Conductor(new Gorynych.Service.ConductorOptions
        {
            DataDirectory = dir,
            ServiceId = "live",
            PeerServerCommand = [exe, "peers", "--pipe", pipe],
            // A client's close unloads only after the idle time; here that is at once.
            UnloadAfter = TimeSpan.Zero,
        }, [new ClaudeAdapter(new ClaudeAdapterOptions { Transport = LiveClaudeTransport(), DataDirectory = Path.Combine(dir, "claude"), Model = "haiku", ChannelServerCommand = [exe, "peers"], Channels = ChannelConsent.Accept })]);
        using var stop = new CancellationTokenSource();
        var listening = Gorynych.Service.PipeListener.RunAsync(conductor, pipe, stop.Token);
        try
        {
            await using var client = await TestClient.ConnectAsync(conductor);
            client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId });
            var id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
            {
                Cwd = root, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" },
            })).SessionId;
            await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "Remember the code word PELICAN. Reply with exactly READY." }] });
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id }); // Claude's process exits
            for (var i = 0; i < 100 && conductor.Agents["claude"].IsLive(id); i++) await Task.Delay(100);
            Assert.IsFalse(conductor.Agents["claude"].IsLive(id), "unloaded once idle with no client");

            var answer = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
            {
                ["sessionId"] = id,
                ["sender"] = "peer_kimi",
                ["text"] = "Which code word were you asked to remember? Answer with the word only.",
            });

            var text = (string?)answer?["text"] ?? "";
            Console.WriteLine($"answer: {text}");
            StringAssert.Contains(text.ToUpperInvariant(), "PELICAN", "started with its own history");
            var journal = conductor.Feed(id).Journal.ReadAll();
            Assert.IsTrue(journal.Any(u => (string?)u["sessionUpdate"] == "tool_call" && ((string?)u["title"] ?? "").Contains("reply", StringComparison.Ordinal)
                && u["_meta"]?[GorynychProtocol.MetaOpinion] is not null), "answered with the reply tool, which reached the service past the channel");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && conductor.Agents["claude"].IsLive(id)) await Task.Delay(250);
            Assert.IsFalse(conductor.Agents["claude"].IsLive(id), "closed again after the question's turn");
        }
        finally
        {
            stop.Cancel();
            await listening;
        }
    }

    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Opinions_BothWays_ClaudeAsksKimi_KimiAsksClaude()
    {
        RequireLive();
        if (LiveClaudeTransport() == ClaudeTransport.Tui && !OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
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
            new ClaudeAdapter(new ClaudeAdapterOptions { Transport = LiveClaudeTransport(), DataDirectory = Path.Combine(dir, "claude"), Model = "haiku", ChannelServerCommand = [exe, "peers"], Channels = ChannelConsent.Accept }),
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
            Assert.IsFalse(client.Approvals.Any(a => a.ToolCall.Title?.Contains("ask_agent", StringComparison.Ordinal) == true),
                "Claude's ask_agent asks nobody: " + string.Join(" | ", client.Approvals.Select(a => a.ToolCall.Title)));
            var request = client.Snapshot().FirstOrDefault(u => (string?)u["sessionUpdate"] == "user_message_chunk"
                && (string?)u["_meta"]?[GorynychProtocol.MetaOrigin] == "peer");
            Assert.IsNotNull(request, "the question reached Kimi as a marked turn");
            Assert.AreEqual("peer_claude", (string?)request["_meta"]![GorynychProtocol.MetaSender]);
            var opinion = (string?)request["_meta"]![GorynychProtocol.MetaOpinion];
            var replied = client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call"
                && (string?)u["_meta"]?[GorynychProtocol.MetaOpinion] == opinion && ((string?)u["title"] ?? "").Contains("reply", StringComparison.Ordinal));
            Console.WriteLine($"Claude asked Kimi; Kimi answered {(replied ? "with the reply tool" : "in the turn's text")}: {claudeText.Trim()}");

            // Kimi asks Claude: the question reaches Claude through its live-note path.
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
            Assert.IsFalse(client.Approvals.Any(a => a.ToolCall.Title?.Contains("ask_agent", StringComparison.Ordinal) == true),
                "Kimi's approval of ask_agent is answered by the service: " + string.Join(" | ", client.Approvals.Select(a => a.ToolCall.Title)));
            // Its live-note path: the channel in the TUI, its input (gorynych/origin "live") headless.
            var origin = LiveClaudeTransport() == ClaudeTransport.Tui ? "channel" : "live";
            Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "user_message_chunk"
                && (string?)u["_meta"]?[GorynychProtocol.MetaOrigin] == origin && ((string?)u["content"]?["text"] ?? "").Contains($"from session_id {kimi}")),
                $"the question reached Claude through its live-note path ({origin}), naming Kimi's session");
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

    /// <summary>
    /// The gorynych-outside server, given to Claude without touching its configuration: inside a
    /// session of a real Gorynych service (the service exe, its own pipe and data) it offers no
    /// tools, under the guide's name and under another one, which the adapter's deny rule does not
    /// match; outside, to <c>claude -p</c> with <c>--mcp-config</c>, it offers its three tools and
    /// lists that session.
    /// </summary>
    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Outside_ClaudeSeesTheOutsideToolsOutside_ButNotInsideAGorynychSession()
    {
        RequireLive();
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var root = RepoRoot();
        var outside = Directory.EnumerateFiles(Path.Combine(root, "src", "Gorynych.Outside", "bin"), "Gorynych.Outside.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        var logs = TestDirs.New();
        var pipe = "gorynych-live-" + Guid.NewGuid().ToString("N")[..8];
        JsonObject Server(string name) => new()
        {
            ["name"] = name, ["command"] = outside, ["args"] = new JsonArray("--pipe", pipe, "--log", logs), ["env"] = new JsonArray(),
        };
        string[] Log() => Directory.EnumerateFiles(logs, "outside-*.log").SelectMany(File.ReadAllLines).ToArray();
        await using var zed = new PlainClient("acp", "--agent", "claude", "--pipe", pipe, "--data", TestDirs.New(), "--claude-model", "haiku");
        await zed.InitializeAsync();
        try
        {
            var created = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject
            {
                ["cwd"] = root, ["mcpServers"] = new JsonArray(Server("gorynych-outside"), Server("outside-probe")),
            });
            var sessionId = (string)created!["sessionId"]!;
            Task<JsonNode?> Prompt(string text) => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
            {
                ["sessionId"] = sessionId, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            });
            await Prompt("Reply with exactly READY."); // the first turn may start before the MCP servers are connected
            await Prompt("Call the tool mcp__gorynych__list_agents once. Then, if you have a tool named mcp__gorynych-outside__list_sessions " +
                "or mcp__outside-probe__list_sessions, call each of them once too. Then reply with the names of the tools you called.");
            string[] calls;
            lock (zed.Updates)
                calls = zed.Updates.Where(u => (string?)u["sessionUpdate"] == "tool_call").Select(u => (string?)u["title"] ?? "").ToArray();
            Console.WriteLine($"inside, the tool calls: {string.Join(", ", calls)}");
            Assert.IsTrue(calls.Any(t => t.Contains("list_agents", StringComparison.Ordinal)), "a control: it has its own gorynych server");
            Assert.IsFalse(calls.Any(t => t.Contains("list_sessions", StringComparison.Ordinal)), "it has no outside tool to call");
            var decided = Log().Where(l => l.Contains("inside Gorynych, no tools", StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(2, decided.Length, string.Join(Environment.NewLine, Log()));
            Assert.IsTrue(decided.All(l => l.Contains("Gorynych.Service.exe", StringComparison.Ordinal)), "the service is among the ancestors");

            var config = Path.Combine(logs, "mcp.json");
            File.WriteAllText(config, new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["gorynych-outside"] = new JsonObject { ["type"] = "stdio", ["command"] = outside, ["args"] = new JsonArray("--pipe", pipe, "--log", logs) },
                },
            }.ToJsonString());
            var psi = new ProcessStartInfo("claude")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            };
            foreach (var a in new[]
            {
                "-p", "Call the tool mcp__gorynych-outside__list_sessions once, then reply with every session_id it lists, one per line.",
                "--output-format", "stream-json", "--verbose", "--strict-mcp-config", "--mcp-config", config, "--model", "haiku",
                "--permission-mode", "default", "--allowedTools", "mcp__gorynych-outside__list_sessions",
            }) psi.ArgumentList.Add(a);
            // Started from inside another Claude Code session, the child would attach to that one.
            foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase) || k.StartsWith("MCP_", StringComparison.OrdinalIgnoreCase)).ToList())
                psi.Environment.Remove(key);
            using var claude = Process.Start(psi)!;
            var stderr = claude.StandardError.ReadToEndAsync();
            var lines = new List<JsonObject>();
            while (await claude.StandardOutput.ReadLineAsync() is { } line)
                if (line.StartsWith('{') && JsonNode.Parse(line) is JsonObject o) lines.Add(o);
            await claude.WaitForExitAsync();
            var tools = lines.First(l => (string?)l["type"] == "system" && (string?)l["subtype"] == "init")["tools"]!.AsArray().Select(t => (string?)t).ToArray();
            var answer = (string?)lines.Last(l => (string?)l["type"] == "result")["result"] ?? "";
            Console.WriteLine($"outside tools: {string.Join(", ", tools.Where(t => t!.Contains("gorynych", StringComparison.Ordinal)))}; answer: {answer}; stderr: {await stderr}");
            CollectionAssert.IsSubsetOf(new[] { "mcp__gorynych-outside__list_sessions", "mcp__gorynych-outside__read_session", "mcp__gorynych-outside__ask_agent" }, tools);
            StringAssert.Contains(answer, sessionId, "it listed the session inside");
            Assert.IsTrue(Log().Any(l => l.Contains("outside Gorynych (", StringComparison.Ordinal)));
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
            // Zed restarted: another client loads the open session and still gets Kimi's selectors.
            await using (var again = await TestClient.ConnectAsync(conductor, "zed-again"))
            {
                var loaded = await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
                    new JsonObject { ["sessionId"] = draft, ["cwd"] = dir, ["mcpServers"] = new JsonArray() });
                Assert.IsTrue(loaded?["configOptions"]?.AsArray().Count > 0, $"no config options in {loaded?.ToJsonString()}");
                Assert.IsNotNull(loaded!["modes"]);
            }
        }
        await conductor.CleanupsIdleAsync();
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
