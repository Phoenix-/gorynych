using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Outside;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Agents outside Gorynych (docs/service-protocol.md, "Agents outside Gorynych"): the
/// gorynych-outside server (Gorynych.Outside.exe) in both eras of MCP, its own copy of the tools,
/// hiding by ancestry, the service's tools behind _gorynych/mcp_message, progress, cancellation,
/// and reading a session (_gorynych/read).
/// </summary>
[TestClass]
public sealed class OutsideTests
{
    static readonly string Cwd = Path.GetTempPath();
    const string Folder = @"D:\work\project";

    static Conductor Conductor(params IAgentAdapter[] adapters) => Conductor(TimeSpan.FromSeconds(60), 5000, adapters);

    static Conductor Conductor(TimeSpan progress, int journal, params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        OutsideProgressInterval = progress,
        JournalMaxEntries = journal,
        UnloadAfter = TimeSpan.Zero, // a session woken or loaded to read is unloaded at once after
    }, adapters);

    static async Task<string> NewPromptedAsync(TestClient client, string agent, string text)
    {
        var id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd,
            McpServers = [],
            Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        })).SessionId;
        await PromptAsync(client, id, text);
        return id;
    }

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static JsonObject Host() => new()
    {
        ["client"] = new JsonObject { ["name"] = "probe-host", ["title"] = "Probe Host", ["version"] = "1" },
        ["folder"] = Folder,
    };

    /// <summary>A request of the outside server, as it sends it to the service.</summary>
    static Task<JsonNode?> McpAsync(TestClient client, string method, JsonObject? @params, string requestId = "1") =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodMcpMessage, new JsonObject
        {
            ["serverId"] = OutsideTools.ServerName,
            ["requestId"] = requestId,
            ["method"] = method,
            ["params"] = @params,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaOutside] = Host() },
        });

    static JsonObject Call(string tool, JsonObject arguments) => new() { ["name"] = tool, ["arguments"] = arguments };

    static (string Text, bool IsError) ToolText(JsonNode? answer) =>
        ((string)answer!["result"]!["content"]![0]!["text"]!, (bool)answer["result"]!["isError"]!);

    static async Task Until(Func<bool> condition, string what, int ms = 10_000)
    {
        for (var waited = 0; waited < ms; waited += 20)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    // ---------------------------------------------------------------- hiding by ancestry

    static Func<int, Ancestry.ProcessEntry?> Tree(params (int Pid, int Parent, string Image, int Born)[] processes) =>
        pid => processes.FirstOrDefault(p => p.Pid == pid) is { Pid: > 0 } p
            ? new Ancestry.ProcessEntry(p.Parent, p.Image, DateTime.UnixEpoch.AddMinutes(p.Born))
            : null;

    [TestMethod]
    public void Ancestry_AnyGorynychServiceAboveMakesItInside_AndTheWalkStopsAtAReusedOrDeadParent()
    {
        const string Outside = "Gorynych.Outside.exe";
        var inside = Ancestry.Walk(10, Tree((10, 9, Outside, 50), (9, 8, "claude.exe", 40), (8, 1, "Gorynych.Service.exe", 30), (1, 0, "explorer.exe", 1)));
        Assert.IsTrue(inside.Inside);
        CollectionAssert.AreEqual(new[] { "claude.exe 9", "Gorynych.Service.exe 8" }, inside.Chain.ToArray());

        var testInstance = Ancestry.Walk(10, Tree((10, 9, Outside, 50), (9, 7, "kimi.EXE", 40), (7, 8, "gorynych.service.exe", 35), (8, 1, "Gorynych.Service.exe", 30)));
        Assert.IsTrue(testInstance.Inside, "any service counts, whatever its pipe; the image is compared ignoring case");

        var outside = Ancestry.Walk(10, Tree((10, 9, Outside, 50), (9, 7, "claude.exe", 40), (7, 1, "WindowsTerminal.exe", 30), (1, 0, "explorer.exe", 1)));
        Assert.IsFalse(outside.Inside);

        var reused = Ancestry.Walk(10, Tree((10, 9, Outside, 50), (9, 8, "claude.exe", 40), (8, 1, "Gorynych.Service.exe", 45)));
        Assert.IsFalse(reused.Inside, "a parent younger than its child holds a reused ID");
        StringAssert.Contains(reused.Why, "reused");

        var dead = Ancestry.Walk(10, Tree((10, 9, Outside, 50), (9, 8, "claude.exe", 40)));
        Assert.IsFalse(dead.Inside);
        StringAssert.Contains(dead.Why, "no longer exists");

        var dotnet = Ancestry.Walk(10, Tree((10, 9, Outside, 50), (9, 8, "claude.exe", 40), (8, 1, "dotnet.exe", 30)));
        Assert.IsFalse(dotnet.Inside, "a service hosted by dotnet is not recognised by its image");

        Assert.IsFalse(Ancestry.OfCurrentProcess().Inside, "the test runner runs under no service");
    }

    [TestMethod]
    public void TheClaudeAdapter_DeniesTheOutsideServersTools()
    {
        var deny = ClaudeHooks.Settings("http://127.0.0.1:1/hook/s/t")["permissions"]!["deny"]!.AsArray().Select(d => (string?)d).ToArray();
        CollectionAssert.AreEqual(new[] { "mcp__gorynych-outside" }, deny);
        StringAssert.Contains(ChannelHost.SystemPrompt, "A message from an agent outside Gorynych says so in its first line instead of naming a session_id; ask_agent cannot reach it.");
    }

    // ---------------------------------------------------------------- reading a session

    static JsonNode Chunk(string kind, string text, string? id = null, JsonObject? meta = null)
    {
        var o = new JsonObject { ["sessionUpdate"] = kind, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } };
        if (id is not null) o["messageId"] = id;
        if (meta is not null) o["_meta"] = meta;
        return o;
    }

    static JsonNode User(string text, string? id = null, JsonObject? meta = null) => Chunk("user_message_chunk", text, id, meta);
    static JsonNode Agent(string text, string? id = null) => Chunk("agent_message_chunk", text, id);

    static string Texts(SessionReader.Page page) => string.Join(" | ", page.Updates.Select(u =>
        (string)u["sessionUpdate"]! == "tool_call" ? $"tool {(string?)u["title"]} {(string?)u["status"]}" : $"{(string)u["sessionUpdate"]!} {(string?)u["content"]!["text"]}"));

    [TestMethod]
    public void Read_MergesEachMessage_FoldsToolCalls_AndPagesByMessageId()
    {
        var journal = new List<JsonNode>
        {
            User("Fix it", "u1"),
            Agent("Look", "a1"),
            Agent("ing.", "a1"),
            new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t1", ["title"] = "Read x", ["status"] = "pending", ["rawInput"] = new JsonObject { ["path"] = "x" } },
            new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t1", ["title"] = "Read x.cs", ["status"] = "completed", ["content"] = new JsonArray() },
            Chunk("agent_thought_chunk", "Hmm.", "th1"),
            new JsonObject { ["sessionUpdate"] = "usage_update", ["used"] = 10, ["size"] = 100 },
            Agent("Done.", "a2"),
            User("Next?", "u2"),
            Agent("Yes.", "a3"),
        };

        var newest = SessionReader.Read(journal, cursor: null, limit: 2, thoughts: false);
        Assert.AreEqual("user_message_chunk Next? | agent_message_chunk Yes.", Texts(newest));
        Assert.AreEqual("u2", newest.NextCursor, "the oldest message it returned");
        Assert.AreEqual((5, 4, 2), (newest.Total, newest.First, newest.Count));

        var older = SessionReader.Read(journal, newest.NextCursor, limit: 10, thoughts: false);
        Assert.AreEqual("user_message_chunk Fix it | agent_message_chunk Looking. | tool Read x.cs completed | agent_message_chunk Done.", Texts(older));
        Assert.IsNull(older.NextCursor);
        Assert.AreEqual((5, 1, 3), (older.Total, older.First, older.Count));
        var tool = older.Updates.Single(u => (string?)u["sessionUpdate"] == "tool_call");
        Assert.IsNull(tool["rawInput"], "raw input and output and content are dropped");
        Assert.AreEqual("a1", (string?)older.Updates[1]["messageId"]);

        StringAssert.Contains(Texts(SessionReader.Read(journal, "u2", 10, thoughts: true)), "agent_thought_chunk Hmm.");
        Assert.ThrowsExactly<SessionReader.UnknownCursorException>(() => SessionReader.Read(journal, "nope", 10, false));
        Assert.AreEqual(1, SessionReader.Read(journal, null, limit: 0, false).Count, "limit is at least 1");
    }

    [TestMethod]
    public void Read_ChunksWithoutIds_AreOneMessageWhileTheyFollowEachOther_WithJournalIndexCursors()
    {
        var journal = new List<JsonNode>
        {
            User("Q"),
            Agent("A1"),
            Agent("A2"),
            new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t1", ["title"] = "Grep" },
            Agent("B"),
            User("R"),
        };
        var newest = SessionReader.Read(journal, null, limit: 2, thoughts: false);
        Assert.AreEqual("agent_message_chunk B | user_message_chunk R", Texts(newest));
        Assert.AreEqual("#4", newest.NextCursor, "the journal index of the message's first chunk");
        var older = SessionReader.Read(journal, "#4", 10, false);
        Assert.AreEqual("user_message_chunk Q | agent_message_chunk A1A2 | tool Grep ", Texts(older));
        Assert.AreEqual(4, older.Total);
    }

    [TestMethod]
    public void Read_CursorsWithoutAnIdOfTheirOwn_SurviveJournalCompaction()
    {
        static JsonNode Tool(string id) => new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = id, ["title"] = id };
        // A message from before IDs, then one whose chunks a tool call splits (one ID, two messages), then newer ones.
        var journal = new List<JsonNode>
        {
            User("old question"), Agent("old"), Agent(" answer"),
            User("Q", "u1"), Agent("Split ", "a1"), Agent("one", "a1"), Tool("t1"), Agent("two", "a1"),
            User("Next?", "u2"), Agent("Yes.", "a3"),
        };
        var page = SessionReader.Read(journal, null, limit: 3, thoughts: false);
        Assert.AreEqual("agent_message_chunk two | user_message_chunk Next? | agent_message_chunk Yes.", Texts(page).Replace(" | tool t1 ", "", StringComparison.Ordinal));
        Assert.AreEqual("u2~1", page.NextCursor, "the second half of a1, one message before u2");
        var older = SessionReader.Read(journal, page.NextCursor, limit: 2, thoughts: false);
        Assert.AreEqual("user_message_chunk Q | agent_message_chunk Split one | tool t1 ", Texts(older));
        Assert.AreEqual("u1", older.NextCursor);
        var oldest = SessionReader.Read(journal, "u1", limit: 5, thoughts: false);
        Assert.AreEqual("user_message_chunk old question | agent_message_chunk old answer", Texts(oldest));

        // Compaction merged a1's first chunks and dropped the oldest entries: the same cursors name the same messages.
        var compacted = new List<JsonNode>
        {
            Agent(" answer"), User("Q", "u1"), Agent("Split one", "a1"), Tool("t1"), Agent("two", "a1"), User("Next?", "u2"), Agent("Yes.", "a3"),
        };
        Assert.AreEqual("user_message_chunk Q | agent_message_chunk Split one | tool t1 ", Texts(SessionReader.Read(compacted, "u2~1", 2, false)));
        Assert.AreEqual(0, SessionReader.Read(compacted, "u1~2", 5, false).Count, "a message the journal dropped has nothing older left");
    }

    [TestMethod]
    public void Read_FillsTheBudgetFromTheNewest_AndCutsAMessageThatDoesNotFitInTheMiddle()
    {
        var huge = "start-" + new string('x', 30_000) + "-end";
        var journal = new List<JsonNode> { User("short", "u1"), Agent(huge, "a1"), User("q2", "u2"), Agent("fine", "a2") };
        var page = SessionReader.Read(journal, null, 10, false, budget: 20_000);
        Assert.AreEqual(3, page.Count, "the cut message is the oldest shown");
        Assert.AreEqual("a1", page.NextCursor);
        var cut = (string)page.Updates[0]["content"]!["text"]!;
        StringAssert.StartsWith(cut, "start-");
        StringAssert.EndsWith(cut, "-end");
        StringAssert.Contains(cut, "characters cut …]");
        Assert.IsTrue(cut.Length <= 20_000, $"{cut.Length}");

        var alone = SessionReader.Read([Agent(huge, "a1")], null, 10, false, budget: 1000);
        Assert.AreEqual(1, alone.Count, "at least one message is always shown");
        Assert.IsTrue(((string)alone.Updates[0]["content"]!["text"]!).Length <= 1000);
    }

    [TestMethod]
    public void ReadSession_LabelsWhereEachUserMessageCameFrom()
    {
        static JsonObject Origin(string origin) => new() { [GorynychProtocol.MetaOrigin] = origin };
        var journal = new List<JsonNode>
        {
            User("Typed by the person", "u1"),
            Agent("Sure.", "a1"),
            new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t1", ["title"] = "Edit a.cs", ["status"] = "failed" },
            User("[Opinion request · from session_id k1 (kimi)]\nTabs?", "u2", Origin("peer")),
            User("[Report · from session_id c1 (claude)]\nok", "u3", Origin("report")),
            User("Do the task", "u4", Origin("spawn")),
            User("[Opinion request · from an agent outside Gorynych (Claude Code, D:\\x)]\nWhy?", "u5",
                new JsonObject { [GorynychProtocol.MetaOrigin] = "peer", [GorynychProtocol.MetaOutside] = new JsonObject() }),
            // Through Claude's channel, as journaled before the messages carried their request's _meta: the tag says.
            User("<channel source=\"gorynych\" kind=\"report\">\n[Report · from session_id c1 (claude)]\nok\n</channel>", "u6", Origin("channel")),
            User("<channel source=\"gorynych\" kind=\"opinion\" message_id=\"c1\">\n[Opinion request · from session_id k1 (kimi)]\nWhy?\n</channel>", "u7", Origin("channel")),
            // And now: the _meta says, whatever the question quotes.
            User("<channel source=\"gorynych\" kind=\"opinion\" message_id=\"c2\">\n[Opinion request · from session_id k1 (kimi)]\nIs kind=\"report\" an attribute?\n</channel>", "u8",
                new JsonObject { [GorynychProtocol.MetaOrigin] = "channel", [GorynychProtocol.MetaOpinion] = "c2", [GorynychProtocol.MetaSender] = "peer_kimi" }),
            User("<channel source=\"gorynych\" kind=\"opinion\" message_id=\"c3\">\n[Opinion request · from an agent outside Gorynych (Claude Code, D:\\x)]\nWhy?\n</channel>", "u9",
                new JsonObject { [GorynychProtocol.MetaOrigin] = "channel", [GorynychProtocol.MetaOpinion] = "c3", [GorynychProtocol.MetaOutside] = new JsonObject() }),
            User("<channel source=\"gorynych\" kind=\"report\">\n[Report · from session_id c1 (claude)]\nIt said: from an agent outside Gorynych\n</channel>", "u10",
                new JsonObject { [GorynychProtocol.MetaOrigin] = "channel", [GorynychProtocol.MetaSender] = "child_claude", [GorynychProtocol.MetaFromSessionId] = "c1" }),
            Agent("Done.", "a2"),
        };
        var record = new SessionRecord { SessionId = "s1", Agent = "kimi", Title = "Fix the \"flaky\" test" };
        var page = SessionReader.Read(journal, null, 20, false);
        var text = OutsideService.ReadSessionText(new Conductor.SessionRead(record, Live: false, page, InProgress: true, Trimmed: true), newest: true);
        Assert.AreEqual(
            "session_id s1 (kimi, \"Fix the 'flaky' test\") · status saved · messages 1–12 of 12, oldest first\n" +
            "(Older history was trimmed from Gorynych's journal.)\n" +
            "── user\nTyped by the person\n" +
            "── agent\nSure.\n" +
            "   · tool: Edit a.cs · failed\n" +
            "── user · opinion request\n[Opinion request · from session_id k1 (kimi)]\nTabs?\n" +
            "── user · report\n[Report · from session_id c1 (claude)]\nok\n" +
            "── user · spawned task\nDo the task\n" +
            "── user · opinion request · from outside\n[Opinion request · from an agent outside Gorynych (Claude Code, D:\\x)]\nWhy?\n" +
            // Through Claude's channel: without the channel tag, which is how it travelled.
            "── user · report\n[Report · from session_id c1 (claude)]\nok\n" +
            "── user · opinion request\n[Opinion request · from session_id k1 (kimi)]\nWhy?\n" +
            "── user · opinion request\n[Opinion request · from session_id k1 (kimi)]\nIs kind=\"report\" an attribute?\n" +
            "── user · opinion request · from outside\n[Opinion request · from an agent outside Gorynych (Claude Code, D:\\x)]\nWhy?\n" +
            "── user · report\n[Report · from session_id c1 (claude)]\nIt said: from an agent outside Gorynych\n" +
            "── agent\nDone.\n" +
            "(turn in progress)", text);

        var older = OutsideService.ReadSessionText(new Conductor.SessionRead(record, true, SessionReader.Read(journal, null, 1, false), false, false), newest: true);
        StringAssert.EndsWith(older, "\nOlder messages: call read_session with session_id \"s1\" and before \"a2\".");
        Assert.AreEqual("session_id s1 (kimi, \"Fix the 'flaky' test\") · status open\nGorynych has no history for this session yet.",
            OutsideService.ReadSessionText(new Conductor.SessionRead(record, true, SessionReader.Read([], null, 10, false), false, false), newest: true));
    }

    [TestMethod]
    public void ReadSession_PutsATurnsToolCallsInTheAgentsBlock_InClaudesOrderAndKimis()
    {
        static JsonNode Tool(string id, string title, JsonObject? input = null) => new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = id, ["title"] = title, ["status"] = "completed", ["rawInput"] = input,
        };
        var record = new SessionRecord { SessionId = "s1", Agent = "claude", Title = "Notes" };
        string Render(List<JsonNode> journal, int limit, bool thoughts = false, string? cursor = null) =>
            OutsideService.ReadSessionText(new Conductor.SessionRead(record, true, SessionReader.Read(journal, cursor, limit, thoughts), false, false),
                newest: cursor is null, thoughts);

        // Claude: its hooks report a tool call before the text that announces it.
        var claude = new List<JsonNode>
        {
            User("<channel source=\"gorynych\" kind=\"report\">\n[Report · from session_id k2 (kimi)]\nok\n</channel>", "u1",
                new JsonObject { [GorynychProtocol.MetaOrigin] = "channel", [GorynychProtocol.MetaSender] = "child_kimi" }),
            Tool("t1", "ask_agent (gorynych)", new JsonObject { ["session_id"] = "k2", ["question"] = "Did the test\npass?" }),
            Agent("Checking ask_agent as well.", "a1"),
            User("Thanks", "u2"),
            Agent("Done.", "a2"),
        };
        Assert.AreEqual(
            "session_id s1 (claude, \"Notes\") · status open · messages 1–4 of 4, oldest first · no thoughts recorded\n" +
            "── user · report\n[Report · from session_id k2 (kimi)]\nok\n" +
            "── agent\n" +
            "   · tool: ask_agent (gorynych) · completed · session_id k2: Did the test\n     pass?\n" + // a message: whole, its lines kept
            "Checking ask_agent as well.\n" +
            "── user\nThanks\n" +
            "── agent\nDone.", Render(claude, 10, thoughts: true));
        var newest = Render(claude, 3);
        StringAssert.Contains(newest, "messages 2–4 of 4");
        StringAssert.StartsWith(newest[newest.IndexOf('\n')..], "\n── agent\n   · tool: ask_agent (gorynych)", "the tool call stays with its turn's message");
        Assert.IsFalse(Render(claude, 10, cursor: "a1").Contains("tool:", StringComparison.Ordinal), "and is not on the older page");

        // Kimi: thinking, a tool call, then the answer; its tool titles are MCP names. Its turn may end
        // with a word, so the reply is the answer's only copy: whole. A task is cut at a word.
        var answer = "Yes: " + new string('y', 400) + "\n\nSecond paragraph.";
        var task = string.Join(' ', Enumerable.Repeat("word", 100));
        var kimi = new List<JsonNode>
        {
            User("Fix it", "u1"),
            Chunk("agent_thought_chunk", "Let me look.", "th1"),
            Tool("t1", "mcp__gorynych__reply", new JsonObject { ["message_id"] = "mab23-3", ["text"] = answer }),
            Tool("t2", "mcp__gorynych__spawn_session", new JsonObject { ["agent"] = "claude", ["prompt"] = task }),
            Tool("t3", "mcp__gorynych__archive_session", new JsonObject { ["session_id"] = "c9" }),
            Tool("t4", "Shell", new JsonObject { ["command"] = "dotnet test" }),
            Agent("Fixed.", "a1"),
        };
        var withThoughts = Render(kimi, 10, thoughts: true);
        var cutTask = string.Join(' ', Enumerable.Repeat("word", 60)) + "… (+200 characters)";
        StringAssert.Contains(withThoughts, "── user\nFix it\n── agent thinking\nLet me look.\n   · tool: reply (gorynych) · completed · message_id mab23-3: Yes: " +
            new string('y', 400) + "\n     Second paragraph.\n   · tool: spawn_session (gorynych) · completed · claude: " + cutTask + "\n" +
            "   · tool: archive_session (gorynych) · completed · session_id c9\n   · tool: Shell · completed\n── agent\nFixed.");
        Assert.IsFalse(withThoughts.Contains("no thoughts recorded", StringComparison.Ordinal));
        StringAssert.Contains(Render(kimi, 10), "── user\nFix it\n── agent\n   · tool: reply (gorynych)");
        var read = SessionReader.Read(kimi, null, 10, false);
        Assert.IsNull(read.Updates.Single(u => (string?)u["title"] == "Shell")["rawInput"], "only the gorynych tools' input is kept");
        Assert.AreEqual(answer, (string?)read.Updates.Single(u => (string?)u["title"] == "mcp__gorynych__reply")["rawInput"]!["text"]);
        Assert.AreEqual(cutTask, (string?)read.Updates.Single(u => (string?)u["title"] == "mcp__gorynych__spawn_session")["rawInput"]!["prompt"]);
        Assert.AreEqual(new string('z', 300) + "… (+100 characters)", SessionReader.CutAtWord(new string('z', 400), 300), "no space: cut in the word");
        Assert.AreEqual("short", SessionReader.CutAtWord("short", 300));
        Assert.AreEqual(("reply", "gorynych"), SessionReader.ToolName("mcp__gorynych__reply"));
        Assert.AreEqual(("list_sessions", "gorynych-outside"), SessionReader.ToolName("mcp__gorynych-outside__list_sessions"));
        Assert.AreEqual(("Read x.cs", (string?)null), SessionReader.ToolName("Read x.cs"));
    }

    [TestMethod]
    public async Task Read_KnowsATurnInProgress_AndATrimmedJournal()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(TimeSpan.FromSeconds(60), journal: 16, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewPromptedAsync(zed, "kimi", "Prompt 0");
        for (var i = 1; i < 10; i++) await PromptAsync(zed, id, $"Prompt {i}");
        var running = new TaskCompletionSource();
        kimi.BeforePrompt = _ => running.Task;
        var turn = PromptAsync(zed, id, "Prompt 10");
        await Until(() => kimi.Prompts.Count == 11, "the running turn");

        var read = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = id, ["limit"] = 3 });
        Assert.AreEqual(true, (bool?)read!["inProgress"]);
        Assert.AreEqual(true, (bool?)read["trimmed"], "16 entries at most: the oldest were dropped");
        Assert.AreEqual(3, read["updates"]!.AsArray().Count(u => ((string?)u!["sessionUpdate"])!.EndsWith("message_chunk", StringComparison.Ordinal)));
        Assert.IsNotNull(read["nextCursor"]);
        var text = ToolText(await McpAsync(zed, "tools/call", Call("read_session", new JsonObject { ["session_id"] = id }))).Text;
        StringAssert.Contains(text, "\n(Older history was trimmed from Gorynych's journal.)\n");
        StringAssert.Contains(text, "\n── user\nPrompt 10\n(turn in progress)\nOlder messages: call read_session with session_id");

        running.SetResult();
        await turn;
        Assert.AreEqual(false, (bool?)(await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = id }))!["inProgress"]);
    }

    [TestMethod]
    public async Task Read_LoadsASessionNeverJournaledOnce_AndClosesItAgain_ButNeverStartsClaude()
    {
        var kimi = new ScriptedAdapter("kimi", async (agent, req, ct) =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize:
                    return AcpJson.ToNode(new InitializeResponse
                    {
                        ProtocolVersion = 1,
                        AgentCapabilities = new AgentCapabilities
                        {
                            LoadSession = true,
                            SessionCapabilities = new SessionCapabilities { List = new SessionListCapabilities(), Close = new SessionCloseCapabilities() },
                        },
                    });
                case AcpMethods.Agent.SessionList:
                    return AcpJson.ToNode(new ListSessionsResponse { Sessions = [new SessionInfo { SessionId = "k-1", Cwd = Cwd, Title = "Made in Kimi" }] });
                case AcpMethods.Agent.SessionLoad:
                    // Kimi replays the history, without message IDs, and runs no turn.
                    await agent.Update("k-1", new SessionUpdateUserMessageChunk { Content = new TextContent { Text = "What is X?" } });
                    await agent.Update("k-1", new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "X is Y." } });
                    return new JsonObject();
                case AcpMethods.Agent.SessionClose:
                    return new JsonObject();
                default:
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        });
        var claude = new LifecycleAgent("claude", canDelete: false, existing: ["claude-native"]);
        await using var conductor = Conductor(kimi, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");

        var read = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = "k-1" });
        var updates = read!["updates"]!.AsArray();
        CollectionAssert.AreEqual(new[] { "What is X?", "X is Y." }, updates.Select(u => (string?)u!["content"]!["text"]).ToArray());
        Assert.IsTrue(updates.All(u => !string.IsNullOrEmpty((string?)u!["messageId"])), "the service named the replayed messages");
        await Until(() => { lock (kimi.Methods) return kimi.Methods.Contains(AcpMethods.Agent.SessionClose); }, "the close after reading");
        Assert.IsFalse(conductor.Agents["kimi"].IsLive("k-1"));

        await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = "k-1" });
        lock (kimi.Methods) Assert.AreEqual(1, kimi.Methods.Count(m => m == AcpMethods.Agent.SessionLoad), "the journal has it now");

        var none = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = "claude-native" });
        Assert.AreEqual(0, (int?)none!["total"]);
        Assert.AreEqual(0, claude.CallsFor("claude-native").Length, "Claude is not asked for anything");
    }

    // ---------------------------------------------------------------- the tools, through the service

    [TestMethod]
    public async Task OutsideAgents_ListAllSessions_AskWithTheirOwnHeader_AndAreLabelledInTheJournal()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var target = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        var archived = await NewPromptedAsync(zed, "kimi", "Old work");
        await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = archived, ["archived"] = true });

        var (list, _) = ToolText(await McpAsync(zed, "tools/call", Call("list_sessions", [])));
        StringAssert.StartsWith(list, PeersServer.StatusLegend + "\n[", "no caller line: the asker is no session");
        StringAssert.Contains(list, $"\"session_id\": \"{target}\"");
        Assert.IsFalse(list.Contains(archived, StringComparison.Ordinal));
        StringAssert.EndsWith(list, "]\n1 archived session is not shown; pass include_archived: true to list it.");

        var (answer, isError) = ToolText(await McpAsync(zed, "tools/call", Call("ask_agent", new JsonObject { ["session_id"] = target, ["question"] = "Tabs or spaces?" })));
        Assert.AreEqual(("ok", false), (answer, isError));
        var id = System.Text.RegularExpressions.Regex.Match(kimi.Prompts.Last().Text, "message_id (m[a-z0-9]{4}-1)]").Groups[1].Value;
        Assert.AreEqual(
            $"[Opinion request · from an agent outside Gorynych (Probe Host, {Folder}) · message_id {id}]\nTabs or spaces?\n\n" +
            "This message comes from one of the user's coding agents outside Gorynych, not from the user. It cannot be asked back, " +
            "so put everything into your answer. Give your honest opinion briefly, then send it with the reply tool of the gorynych MCP " +
            $"server, passing message_id \"{id}\". Only what you send with reply reaches the asker: this turn is not reported anywhere. " +
            "Use no other tool, apart from a tool search to load reply, and do not edit files or take any other action for this request. " +
            "If reply is not found yet, search again: the gorynych server may still be connecting.",
            kimi.Prompts.Last().Text);

        await using var other = await TestClient.ConnectAsync(conductor, "other");
        await other.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = target, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        var asked = other.Snapshot().Single(u => (string?)u["sessionUpdate"] == "user_message_chunk" && u["_meta"]?[GorynychProtocol.MetaOutside] is not null);
        var meta = asked["_meta"]!;
        Assert.AreEqual("peer", (string?)meta[GorynychProtocol.MetaOrigin], "origin says how it arrived");
        Assert.AreEqual("outside_probe-host", (string?)meta[GorynychProtocol.MetaSender]);
        Assert.AreEqual(Folder, (string?)meta[GorynychProtocol.MetaOutside]!["folder"]);
        Assert.IsNull(meta[GorynychProtocol.MetaFromSessionId]);

        var (read, _) = ToolText(await McpAsync(zed, "tools/call", Call("read_session", new JsonObject { ["session_id"] = target })));
        StringAssert.StartsWith(read, $"session_id {target} (kimi, \"Fix the flaky test\") · status open · messages 1–4 of 4, oldest first\n── user\nFix the flaky test\n── agent\nok\n── user · opinion request · from outside\n");

        // Another run of the service counts from 1 too, under another tag.
        await using var restarted = Conductor(kimi.Adapter);
        await using var later = await TestClient.ConnectAsync(restarted, "later");
        ToolText(await McpAsync(later, "tools/call", Call("ask_agent", new JsonObject { ["session_id"] = target, ["question"] = "And now?" })));
        var again = System.Text.RegularExpressions.Regex.Match(kimi.Prompts.Last().Text, "message_id (m[a-z0-9]{4}-1)]").Groups[1].Value;
        Assert.AreNotEqual("", again);
        Assert.AreNotEqual(id, again, "a late reply to the first request cannot answer this one");

        var (unknown, unknownIsError) = ToolText(await McpAsync(zed, "tools/call", Call("ask_agent", new JsonObject { ["session_id"] = "peer_kimi", ["question"] = "?" })));
        Assert.IsTrue(unknownIsError);
        Assert.AreEqual($"Gorynych could not do that: No session \"peer_kimi\". Pass an exact session_id from list_sessions. Sessions in Gorynych:\n" +
            $"- session_id {target} (kimi, \"Fix the flaky test\")", unknown);
        var (badCursor, _) = ToolText(await McpAsync(zed, "tools/call", Call("read_session", new JsonObject { ["session_id"] = target, ["before"] = "zzz" })));
        Assert.AreEqual($"Gorynych could not do that: session_id {target} has no message \"zzz\". Pass before exactly as a previous read_session " +
            "result gave it, or leave it out to read the latest messages.", badCursor);

        var nobody = OutsideService.Asker(new JsonObject());
        Assert.AreEqual(("outside_unknown", "from an agent outside Gorynych (an unknown agent)"), (nobody.Sender, nobody.Origin));
    }

    [TestMethod]
    public async Task ListSessions_KeepsTitlesAsTheyAre_AndSaysWhenThereIsNone()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        Assert.AreEqual(OutsideService.NoSession, ToolText(await McpAsync(zed, "tools/call", Call("list_sessions", []))).Text);
        Assert.AreEqual("There is no session in Gorynych now.", OutsideService.NoSession);

        await NewPromptedAsync(zed, "kimi", "Почини тест + обнови план");
        var (list, _) = ToolText(await McpAsync(zed, "tools/call", Call("list_sessions", [])));
        StringAssert.Contains(list, "\"title\": \"Почини тест + обнови план\"");
        StringAssert.Contains(list, "+00:00\"", "the time's offset as it is");
        Assert.IsFalse(list.Contains("\\u", StringComparison.Ordinal), list);
    }

    [TestMethod]
    public async Task OnTheLiveNotePath_TheRequestCarriesTheMetaItIsJournaledWith()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var target = await NewPromptedAsync(zed, "claude", "Write the notes");
        var asker = await NewPromptedAsync(zed, "kimi", "Coordinate the release");

        ToolText(await McpAsync(zed, "tools/call", Call("ask_agent", new JsonObject { ["session_id"] = target, ["question"] = "Why?" })));
        var fromOutside = claude.Opinions.Last()["_meta"]!;
        Assert.AreEqual("outside_probe-host", (string?)fromOutside[GorynychProtocol.MetaSender]);
        Assert.AreEqual(Folder, (string?)fromOutside[GorynychProtocol.MetaOutside]!["folder"]);
        Assert.IsNull(fromOutside[GorynychProtocol.MetaFromSessionId]);

        await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = target, ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Tokens[asker] },
        });
        var fromSession = claude.Opinions.Last()["_meta"]!;
        Assert.AreEqual(("peer_kimi", asker), ((string?)fromSession[GorynychProtocol.MetaSender], (string?)fromSession[GorynychProtocol.MetaFromSessionId]));
        Assert.IsNull(fromSession[GorynychProtocol.MetaOutside]);
    }

    [TestMethod]
    public async Task TheService_ServesBothEras_RefusesOtherVersions_AndOtherServers()
    {
        await using var conductor = Conductor(new LifecycleAgent("kimi", canDelete: true).Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var modern = new JsonObject { ["_meta"] = new JsonObject { [OutsideTools.MetaProtocolVersion] = OutsideTools.ModernVersion } };

        var discover = (await McpAsync(zed, "server/discover", modern.DeepClone().AsObject()))!["result"]!;
        Assert.AreEqual("complete", (string?)discover["resultType"]);
        Assert.AreEqual(OutsideTools.ModernVersion, (string?)discover["supportedVersions"]![0]);
        Assert.AreEqual(OutsideTools.Instructions, (string?)discover["instructions"]);
        var tools = (await McpAsync(zed, "tools/list", modern.DeepClone().AsObject()))!["result"]!;
        Assert.AreEqual("complete", (string?)tools["resultType"]);
        CollectionAssert.AreEqual(new[] { "list_sessions", "read_session", "ask_agent" }, tools["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToArray());
        Assert.IsNull((await McpAsync(zed, "tools/list", null))!["result"]!["resultType"], "a legacy result has no resultType");

        var old = new JsonObject { ["_meta"] = new JsonObject { [OutsideTools.MetaProtocolVersion] = "1900-01-01" } };
        var unsupported = (await McpAsync(zed, "tools/list", old))!["error"]!;
        Assert.AreEqual(-32022, (int?)unsupported["code"]);
        Assert.AreEqual(OutsideTools.ModernVersion, (string?)unsupported["data"]!["supported"]![0]);
        Assert.AreEqual(-32601, (int?)(await McpAsync(zed, "resources/list", null))!["error"]!["code"]);
        Assert.AreEqual(-32602, (int?)(await McpAsync(zed, "tools/call", Call("spawn_session", [])))!["error"]!["code"], "an unknown tool");

        var other = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(GorynychProtocol.MethodMcpMessage,
            new JsonObject { ["serverId"] = "someone-else", ["requestId"] = "1", ["method"] = "tools/list" }));
        Assert.AreEqual(-33001, other.Code, "a binding failure is an error of the outer request");
    }

    // ---------------------------------------------------------------- the outside server (Gorynych.Outside.exe)

    /// <summary>A service executable that does not exist.</summary>
    static readonly string Nowhere = OperatingSystem.IsWindows() ? @"C:\nowhere\Gorynych.Service.exe" : "/nowhere/Gorynych.Service";

    static string OutsideExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx")))
                return Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "Gorynych.Outside", "bin"), TestDirs.Exe("Gorynych.Outside"), SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).First();
        throw new InvalidOperationException("repository root not found");
    }

    /// <summary>The outside server as a host runs it: a child process over stdio. It never starts a real service here.</summary>
    sealed class Shim : IAsyncDisposable
    {
        readonly Process process;
        public JsonRpcConnection Host { get; }
        public List<JsonObject> Progress { get; } = [];

        public Shim(string pipe, string? service = null, string? log = null)
        {
            var psi = new ProcessStartInfo(OutsideExe())
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false), WorkingDirectory = Cwd,
            };
            foreach (var a in new[] { "--pipe", pipe, "--service", service ?? Nowhere, "--log", log ?? TestDirs.New() }) psi.ArgumentList.Add(a);
            process = Process.Start(psi)!;
            Host = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, "host")
            {
                CancelNotification = "notifications/cancelled",
            };
            Host.NotificationHandler = (method, p) =>
            {
                if (method == "notifications/progress" && p is JsonObject o) lock (Progress) Progress.Add(o);
                return Task.CompletedTask;
            };
            Host.Start();
        }

        public string[] Messages()
        {
            lock (Progress) return Progress.Select(p => (string)p["message"]!).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            if (!process.WaitForExit(5000)) process.Kill();
            process.Dispose();
        }
    }

    static JsonObject Modern(JsonNode? progressToken = null, string version = OutsideTools.ModernVersion)
    {
        var meta = new JsonObject
        {
            [OutsideTools.MetaProtocolVersion] = version,
            [OutsideTools.MetaClientInfo] = new JsonObject { ["name"] = "claude-code", ["title"] = "Claude Code", ["version"] = "2.1.302" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };
        if (progressToken is not null) meta["progressToken"] = progressToken;
        return meta;
    }

    static JsonObject ModernCall(string tool, JsonObject arguments, JsonNode? progressToken = null)
    {
        var call = Call(tool, arguments);
        call["_meta"] = Modern(progressToken);
        return call;
    }

    static string Pipe() => "gorynych-test-" + Guid.NewGuid().ToString("N")[..8];

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheOutsideServer_ServesAModernAndALegacyHost()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var pipe = Pipe();
        await using var conductor = Conductor(kimi.Adapter);
        using var stop = new CancellationTokenSource();
        var listening = PipeListener.RunAsync(conductor, pipe, stop.Token);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var target = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        try
        {
            await using (var modern = new Shim(pipe))
            {
                var discover = await modern.Host.SendRequestAsync("server/discover", new JsonObject { ["_meta"] = Modern() });
                Assert.AreEqual("complete", (string?)discover!["resultType"]);
                Assert.AreEqual(OutsideTools.ServerName, (string?)discover["_meta"]![OutsideTools.MetaServerInfo]!["name"]);
                var tools = await modern.Host.SendRequestAsync("tools/list", new JsonObject { ["_meta"] = Modern() });
                Assert.AreEqual(3, tools!["tools"]!.AsArray().Count);
                var listed = await modern.Host.SendRequestAsync("tools/call", ModernCall("list_sessions", []));
                Assert.AreEqual("complete", (string?)listed!["resultType"]);
                StringAssert.Contains((string?)listed["content"]![0]!["text"], target);
                var unsupported = await Assert.ThrowsExactlyAsync<JsonRpcException>(() =>
                    modern.Host.SendRequestAsync("tools/list", new JsonObject { ["_meta"] = Modern(version: "1900-01-01") }));
                Assert.AreEqual(-32022, unsupported.Code);
                Assert.AreEqual(OutsideTools.ModernVersion, (string?)unsupported.ErrorData!["supported"]![0]);
                Assert.AreEqual(-32601, (await Assert.ThrowsExactlyAsync<JsonRpcException>(() => modern.Host.SendRequestAsync("prompts/list", null))).Code,
                    "an unknown request is forwarded, and the service does not know it");
                Assert.IsNotNull(await modern.Host.SendRequestAsync("ping", null));
            }

            await using var legacy = new Shim(pipe);
            var init = await legacy.Host.SendRequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "kimi-code", ["version"] = "0.0.0" },
            });
            Assert.AreEqual("2025-06-18", (string?)init!["protocolVersion"]);
            Assert.AreEqual(OutsideTools.Instructions, (string?)init["instructions"]);
            await legacy.Host.SendNotificationAsync("notifications/initialized", null);
            var legacyTools = await legacy.Host.SendRequestAsync("tools/list", null);
            Assert.IsNull(legacyTools!["resultType"]);
            var answer = await legacy.Host.SendRequestAsync("tools/call", Call("ask_agent", new JsonObject { ["session_id"] = target, ["question"] = "Done?" }));
            Assert.AreEqual("ok", (string?)answer!["content"]![0]!["text"]);
            StringAssert.StartsWith(kimi.Prompts.Last().Text, $"[Opinion request · from an agent outside Gorynych (kimi-code, {Path.TrimEndingDirectorySeparator(Cwd)}",
                "a legacy host's clientInfo comes from its initialize, the folder is the server's own");
        }
        finally
        {
            stop.Cancel();
            await listening;
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task WhileTheServiceIsDown_TheToolsComeFromTheServersCopy_AndTheServiceWinsOnceItIsBack()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var pipe = Pipe();
        await using var conductor = Conductor(kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var target = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        await using var shim = new Shim(pipe);

        var tools = await shim.Host.SendRequestAsync("tools/list", new JsonObject { ["_meta"] = Modern() });
        CollectionAssert.AreEqual(new[] { "list_sessions", "read_session", "ask_agent" }, tools!["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToArray());
        var down = await shim.Host.SendRequestAsync("tools/call", ModernCall("list_sessions", []));
        Assert.AreEqual(true, (bool?)down!["isError"]);
        Assert.AreEqual($"Gorynych is not running and could not be started: {Nowhere} does not exist. Start Gorynych, then call the tool again.",
            (string?)down["content"]![0]!["text"]);
        var discover = await shim.Host.SendRequestAsync("server/discover", new JsonObject { ["_meta"] = Modern() });
        Assert.AreEqual(OutsideTools.Instructions, (string?)discover!["instructions"]);

        using var stop = new CancellationTokenSource();
        var listening = PipeListener.RunAsync(conductor, pipe, stop.Token);
        try
        {
            var up = await shim.Host.SendRequestAsync("tools/call", ModernCall("list_sessions", []));
            Assert.AreEqual(false, (bool?)up!["isError"], "the server connects on every request");
            StringAssert.Contains((string?)up["content"]![0]!["text"], target);
        }
        finally
        {
            stop.Cancel();
            await listening;
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AskAgent_ReportsProgressAtEachStateAndEveryInterval_NoneAfterTheAnswer_NoneWithoutAToken()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var pipe = Pipe();
        await using var conductor = Conductor(TimeSpan.FromMilliseconds(150), 5000, kimi.Adapter);
        using var stop = new CancellationTokenSource();
        var listening = PipeListener.RunAsync(conductor, pipe, stop.Token);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var target = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        var running = new TaskCompletionSource();
        var answering = new TaskCompletionSource();
        kimi.BeforePrompt = _ => kimi.Prompts.Count switch { 2 => running.Task, 3 => answering.Task, _ => Task.CompletedTask };
        try
        {
            await using var shim = new Shim(pipe);
            var turn = PromptAsync(zed, target, "Keep going");
            await Until(() => kimi.Prompts.Count == 2, "a running turn");

            var asking = shim.Host.SendRequestAsync("tools/call", ModernCall("ask_agent", new JsonObject { ["session_id"] = target, ["question"] = "Done?" }, progressToken: 7));
            var waiting = $"Waiting for session_id {target} to finish its running turn.";
            await Until(() => shim.Messages().Count(m => m == waiting) >= 3, "the state, then the interval's repeats");
            running.SetResult();
            await turn;
            await Until(() => shim.Messages().Contains($"Asked session_id {target}; waiting for its answer."), "the next state");
            answering.SetResult();
            var answer = await asking;
            Assert.AreEqual("ok", (string?)answer!["content"]![0]!["text"]);

            JsonObject[] seen;
            lock (shim.Progress) seen = [.. shim.Progress];
            CollectionAssert.AreEqual(Enumerable.Range(1, seen.Length).ToArray(), seen.Select(p => (int)p["progress"]!).ToArray(), "counts up from 1");
            Assert.IsTrue(seen.All(p => (int?)p["progressToken"] == 7 && p["total"] is null), "the host's token, no total");
            await Task.Delay(600);
            Assert.AreEqual(seen.Length, shim.Progress.Count, "none after the answer");

            await shim.Host.SendRequestAsync("tools/call", ModernCall("ask_agent", new JsonObject { ["session_id"] = target, ["question"] = "And now?" }));
            await Task.Delay(400);
            Assert.AreEqual(seen.Length, shim.Progress.Count, "none without a progressToken");
        }
        finally
        {
            running.TrySetResult();
            answering.TrySetResult();
            stop.Cancel();
            await listening;
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARequestCancelledWhileTheServiceAnswersItsInitialize_ClosesThePipe()
    {
        var pipe = Pipe();
        // A service that takes the connection and never answers.
        await using var service = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var accepted = service.WaitForConnectionAsync();
        await using var shim = new Shim(pipe);
        using var cancel = new CancellationTokenSource();
        var call = shim.Host.SendRequestAsync("tools/call", ModernCall("list_sessions", []), cancel.Token);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(service, new UTF8Encoding(false));
        StringAssert.Contains(await reader.ReadLineAsync(), "\"initialize\"");

        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => call);
        // Its $/cancel_request may come first; then the connection ends.
        var closed = Task.Run(async () => { while (await reader.ReadLineAsync() is not null) { } });
        await closed.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARequestCancelledWhileTheServiceStarts_LetsTheNextRequestStartItAgain()
    {
        var logs = TestDirs.New();
        int Starts() => Directory.EnumerateFiles(logs, "outside-*.log").SelectMany(File.ReadAllLines).Count(l => l.Contains("starting the service", StringComparison.Ordinal));
        // A "service" that never opens the pipe, so the server waits for it.
        await using var shim = new Shim(Pipe(), OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "whoami.exe") : "/bin/true", logs);
        foreach (var expected in new[] { 1, 2 })
        {
            using var cancel = new CancellationTokenSource();
            var call = shim.Host.SendRequestAsync("tools/call", ModernCall("list_sessions", []), cancel.Token);
            await Until(() => Starts() == expected, $"start {expected}, not a minute's wait");
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => call);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ACancelledQuestion_IsWithdrawnBeforeItReachesTheSession()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var pipe = Pipe();
        await using var conductor = Conductor(TimeSpan.FromSeconds(60), 5000, kimi.Adapter);
        using var stop = new CancellationTokenSource();
        var listening = PipeListener.RunAsync(conductor, pipe, stop.Token);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var target = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        var running = new TaskCompletionSource();
        kimi.BeforePrompt = _ => kimi.Prompts.Count == 2 ? running.Task : Task.CompletedTask;
        try
        {
            await using var shim = new Shim(pipe);
            var turn = PromptAsync(zed, target, "Keep going");
            await Until(() => kimi.Prompts.Count == 2, "a running turn");
            using var cancel = new CancellationTokenSource();
            var asking = shim.Host.SendRequestAsync("tools/call", ModernCall("ask_agent", new JsonObject { ["session_id"] = target, ["question"] = "Done?" }, "p"), cancel.Token);
            await Until(() => shim.Messages().Length > 0, "the question waiting for the running turn");

            cancel.Cancel(); // the host sends notifications/cancelled
            await Assert.ThrowsAsync<OperationCanceledException>(() => asking);
            await Task.Delay(300);
            running.SetResult();
            await turn;
            await Task.Delay(500);
            Assert.AreEqual(2, kimi.Prompts.Count, "the question never reached the session");
        }
        finally
        {
            running.TrySetResult();
            stop.Cancel();
            await listening;
        }
    }
}
