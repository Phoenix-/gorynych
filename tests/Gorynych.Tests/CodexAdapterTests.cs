using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The Codex adapter's translation, row by row of its mapping (docs/adapters.md, "Codex"), through
/// the service against a fake app-server (<see cref="FakeAppServer"/>), and what the service does
/// with the adapter's flags: native titles, its own idle time, drafts that were never durable.
/// </summary>
[TestClass]
public sealed class CodexAdapterTests
{
    static readonly string Cwd = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    const string NotForThisAgent = "codex";

    static Conductor Conductor(FakeAppServer fake, ManualTime? time = null, IAgentAdapter[]? more = null, CodexAdapterOptions? codex = null) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        Time = (TimeProvider?)time ?? TimeProvider.System,
    }, [new CodexAdapter(codex ?? Options(fake)), .. more ?? []]);

    static CodexAdapterOptions Options(FakeAppServer fake, List<string>? log = null, Func<IEnumerable<string>, IReadOnlyDictionary<string, string>>? holders = null) => new()
    {
        Launch = fake.StartAsync,
        ClientVersion = "test",
        TurnEndWait = TimeSpan.FromMilliseconds(200),
        Log = log is null ? null : m => { lock (log) log.Add(m); },
        // No other Codex app holds a fake thread; the user's own Codex folder is never read.
        FindHolders = holders ?? (_ => new Dictionary<string, string>()),
        LocksDirectory = Path.Combine(Path.GetTempPath(), "gorynych-no-codex-locks"), // never there: nothing watched
    };

    /// <summary>The adapter alone, without the service: an ACP connection straight to it.</summary>
    static async Task<(AgentInstance Agent, JsonRpcConnection Acp)> DirectAsync(FakeAppServer fake, List<string>? log = null)
    {
        var agent = await new CodexAdapter(Options(fake, log)).StartAsync(CancellationToken.None);
        var acp = new JsonRpcConnection(agent.Input, agent.Output, "direct-client") { NotificationHandler = (_, _) => Task.CompletedTask };
        acp.Start();
        return (agent, acp);
    }

    static async Task<string> NewDirectAsync(JsonRpcConnection acp) =>
        (string)(await acp.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;

    static Task<JsonNode?> PromptDirectAsync(JsonRpcConnection acp, string sessionId, string text) =>
        acp.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
        {
            ["sessionId"] = sessionId, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        });

    static bool Logged(List<string> log, string text)
    {
        lock (log) return log.Any(l => l.Contains(text, StringComparison.Ordinal));
    }

    static Task<TestClient> ZedAsync(Conductor conductor, string name = "zed") => TestClient.ConnectAsync(conductor, name, agent: NotForThisAgent);

    static async Task<string> NewAsync(TestClient client) =>
        (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Cwd, McpServers = [] })).SessionId;

    static Task<PromptResponse> PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static JsonObject[] Of(TestClient client, string kind) => client.Snapshot().Where(u => (string?)u["sessionUpdate"] == kind).ToArray();

    static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 250; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    // ---------------------------------------------------------------- initialize, new, prompt

    [TestMethod]
    public async Task Initialize_SaysWhatTheServiceNeeds_AndIdentifiesGorynych()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        var host = conductor.Agents["codex"];
        await host.GetAsync(CancellationToken.None);

        Assert.IsTrue(host.CanList && host.CanResume && host.CanLoad && host.CanClose && host.CanDelete && host.TakesSystemPrompt);
        Assert.IsTrue(host.CanSetTitle && host.DraftsNotDurable);
        Assert.AreEqual(TimeSpan.FromMinutes(2), host.UnloadAfter);
        var init = fake.Last("initialize");
        Assert.AreEqual(("gorynych", "Gorynych"), ((string?)init["clientInfo"]!["name"], (string?)init["clientInfo"]!["title"]));
        CollectionAssert.Contains(init["capabilities"]!["optOutNotificationMethods"]!.AsArray().Select(n => (string?)n).ToList(), "hook/started");
        CollectionAssert.IsSubsetOf(new[] { "initialize", "model/list", "config/read" }, fake.Methods());
    }

    [TestMethod]
    public async Task New_StartsAThreadWithTheGorynychServer_ThePeerInstructions_AndTheOutsideServerTurnedOff()
    {
        var fake = new FakeAppServer { UserServers = ["gorynych-outside", "other-tool"] };
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);

        var created = await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Cwd, McpServers = [], Meta = new JsonObject { ["gorynych/agent"] = "codex" } });

        var start = fake.Last("thread/start");
        Assert.AreEqual(Cwd, (string?)start["cwd"]);
        var servers = start["config"]!["mcp_servers"]!.AsObject();
        Assert.AreEqual("svc.exe", (string?)servers["gorynych"]!["command"]);
        CollectionAssert.AreEqual(new[] { "peers", "--pipe", "p", "--token" }, servers["gorynych"]!["args"]!.AsArray().Take(4).Select(a => (string?)a).ToArray());
        Assert.AreEqual("approve", (string?)servers["gorynych"]!["default_tools_approval_mode"], "Codex runs the gorynych tools without asking");
        Assert.AreEqual(false, (bool?)servers["gorynych-outside"]!["enabled"], "the user's outside server is off in Gorynych's threads");
        Assert.IsNull(servers["other-tool"], "the user's other servers are left as they are");
        Assert.AreEqual(PeersServer.SystemPrompt, (string?)start["developerInstructions"]);
        CollectionAssert.AreEqual(new[] { "mode", "model", "reasoning_effort" }, created.ConfigOptions!.Select(o => o.Id).ToArray());
        Assert.AreEqual("auto", ((SessionConfigSelect)created.ConfigOptions![0]).CurrentValue, "on-request with workspace-write is Codex's Auto");
    }

    [TestMethod]
    public async Task New_LeavesTheOutsideServerAlone_WhenTheUserHasNone()
    {
        var fake = new FakeAppServer { UserServers = ["other-tool"] };
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        await NewAsync(zed);
        Assert.IsNull(fake.Last("thread/start")["config"]!["mcp_servers"]!["gorynych-outside"], "overriding a server the config does not have fails the thread");
    }

    [TestMethod]
    public async Task APrompt_StreamsTheAnswerUnderItsItemId_DoesNotEchoThePrompt_AndPassesTheServicesMessageId()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);

        var done = await PromptAsync(zed, id, "hello");

        Assert.AreEqual(StopReason.EndTurn, done.StopReason);
        var start = fake.Last("turn/start");
        Assert.AreEqual("hello", (string?)start["input"]![0]!["text"]);
        var read = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = id });
        var journaled = read!["updates"]!.AsArray().First(u => (string?)u!["sessionUpdate"] == "user_message_chunk")!;
        Assert.AreEqual((string?)journaled["messageId"], (string?)start["clientUserMessageId"], "Codex keeps the journal's ID with the message");
        Assert.AreEqual(0, Of(zed, "user_message_chunk").Length, "the client's own prompt is not echoed");
        var chunks = Of(zed, "agent_message_chunk");
        Assert.AreEqual("ok", string.Concat(chunks.Select(c => (string?)c["content"]!["text"])));
        Assert.IsTrue(chunks.All(c => ((string?)c["messageId"])!.StartsWith("msg-turn-", StringComparison.Ordinal)), "Codex's item ID");
        Assert.AreEqual(1, chunks.Select(c => (string?)c["messageId"]).Distinct().Count());
    }

    // ---------------------------------------------------------------- tool calls and approvals

    [TestMethod]
    public async Task ACommand_IsAToolCall_AndItsApprovalOffersNoOptionThatWritesCodexsConfig()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        zed.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "acceptForSession" });
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "run");

        var asked = zed.Approvals.Single();
        CollectionAssert.AreEqual(new[] { "accept", "acceptForSession", "decline", "cancel" }, asked.Options.Select(o => o.OptionId).ToArray(),
            "the exec-policy amendment Codex offers is left out: it writes ~/.codex/rules");
        CollectionAssert.AreEqual(new[] { "allow_once", "allow_always", "reject_once", "reject_once" }, asked.Options.Select(o => o.Kind.Value).ToArray());
        Assert.AreEqual(("cmd /c echo hi", "execute"), (asked.ToolCall.Title, asked.ToolCall.Kind?.Value));
        Assert.AreEqual("acceptForSession", (string?)fake.Answers.Single().Answer!["decision"]);
        var call = Of(zed, "tool_call").Single();
        Assert.AreEqual(("cmd /c echo hi", "execute", "in_progress"), ((string?)call["title"], (string?)call["kind"], (string?)call["status"]));
        Assert.AreEqual(Cwd, (string?)call["rawInput"]!["cwd"]);
        var end = Of(zed, "tool_call_update").Last();
        Assert.AreEqual(("completed", "hi\n"), ((string?)end["status"], (string?)end["content"]![0]!["content"]!["text"]));
        Assert.AreEqual(0, (int?)end["rawOutput"]!["exitCode"]);
    }

    [TestMethod]
    public async Task AFileChange_IsAnEdit_AndADeclineFailsIt()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        zed.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "decline" });
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "patch");

        var asked = zed.Approvals.Single();
        CollectionAssert.AreEqual(new[] { "accept", "acceptForSession", "decline", "cancel" }, asked.Options.Select(o => o.OptionId).ToArray());
        Assert.AreEqual(("Create note.txt", "edit"), (asked.ToolCall.Title, asked.ToolCall.Kind?.Value));
        Assert.AreEqual(Path.Combine(Cwd, "note.txt"), asked.ToolCall.Locations![0].Path);
        Assert.AreEqual("decline", (string?)fake.Answers.Single().Answer!["decision"]);
        var call = Of(zed, "tool_call").Single();
        StringAssert.Contains((string?)call["content"]![0]!["content"]!["text"], "```diff\nhi\n```");
        Assert.AreEqual("failed", (string?)Of(zed, "tool_call_update").Last()["status"]);
    }

    [TestMethod]
    public async Task AGorynychToolCall_AsksThePerson_ForOnceOrTheSession_NeverAlways()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        zed.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "session" });
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "mcp");

        var asked = zed.Approvals.Single(); // not answered by the service: the permissions pass decides about pre-allowing
        Assert.AreEqual("reply (gorynych)", asked.ToolCall.Title);
        CollectionAssert.AreEqual(new[] { "accept", "session", "decline" }, asked.Options.Select(o => o.OptionId).ToArray(), "persist: always is never offered");
        Assert.AreEqual("Spaces.", (string?)asked.ToolCall.RawInput!["text"]);
        var answer = fake.Answers.Single().Answer!;
        Assert.AreEqual(("accept", "session"), ((string?)answer["action"], (string?)answer["_meta"]!["persist"]));
        var call = Of(zed, "tool_call").Single();
        Assert.AreEqual(("reply (gorynych)", "mk7q2-1"), ((string?)call["title"], (string?)call["rawInput"]!["message_id"]));
        Assert.AreEqual((string?)call["toolCallId"], asked.ToolCall.ToolCallId, "the approval names the tool call");
        Assert.AreEqual("delivered", (string?)Of(zed, "tool_call_update").Last()["content"]![0]!["content"]!["text"]);
    }

    [TestMethod]
    public async Task Cancel_WithAnApprovalPending_InterruptsTheTurn_AndWithdrawsTheQuestion()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        zed.Approve = r => new TaskCompletionSource<RequestPermissionOutcome>().Task;
        var id = await NewAsync(zed);

        var turn = PromptAsync(zed, id, "slow");
        await Until(() => zed.Approvals.Count == 1, "the approval");
        await zed.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = id });

        Assert.AreEqual(StopReason.Cancelled, (await turn.WaitAsync(Wait)).StopReason);
        Assert.IsNotNull(fake.Last("turn/interrupt"));
        await Until(() => zed.Withdrawn == 1, "the question withdrawn from the client");
        Assert.AreEqual("failed", (string?)Of(zed, "tool_call_update").Last()["status"]);
    }

    [TestMethod]
    public async Task AQuestionOfCodexs_IsAForm()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        zed.Fill = form => new ElicitationAcceptAction { Content = new() { ["color"] = "Blue" } };
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "ask");

        var form = zed.Forms.Single();
        Assert.AreEqual("Which color?", form.Message);
        var color = (StringPropertySchema)form.RequestedSchema.Properties!["color"]!;
        CollectionAssert.AreEqual(new[] { "Red", "Blue" }, color.OneOf!.Select(o => o.Const).ToArray());
        Assert.AreEqual("Blue", (string?)fake.Answers.Single().Answer!["answers"]!["color"]!["answers"]![0]);
        Assert.AreEqual("you chose Blue", zed.Text());
    }

    // ---------------------------------------------------------------- the rest of a turn

    [TestMethod]
    public async Task ReasoningPlanUsageAndAGeneratedTitle_ReachTheClient()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "think plan usage name");

        var thoughts = Of(zed, "agent_thought_chunk");
        Assert.AreEqual("First\n\nSecond", string.Concat(thoughts.Select(t => (string?)t["content"]!["text"])), "streamed once, not again when completed");
        Assert.IsTrue(thoughts.All(t => ((string?)t["messageId"])!.StartsWith("rs-", StringComparison.Ordinal)));
        var plan = Of(zed, "plan").Single();
        CollectionAssert.AreEqual(new[] { "completed", "in_progress" }, plan["entries"]!.AsArray().Select(e => (string?)e!["status"]).ToArray());
        var usage = Of(zed, "usage_update").Single();
        Assert.AreEqual((1500, 200_000), ((int)usage["used"]!, (int)usage["size"]!));
        Assert.IsTrue(Of(zed, "session_info_update").Any(u => (string?)u["title"] == "Generated title"));
    }

    [TestMethod]
    public async Task AFailedTurn_FailsThePrompt_WithCodexsMessage()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);
        var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(zed, id, "fail"));
        Assert.AreEqual("boom", e.Message);
    }

    [TestMethod]
    public async Task AModeChange_GoesWithTheNextTurn_AndApproveForMeIsCodexsReviewer()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);

        var set = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = id, ["configId"] = "mode", ["value"] = "auto-review" });
        var mode = set!["configOptions"]!.AsArray().First(o => (string?)o!["id"] == "mode")!;
        Assert.AreEqual("auto-review", (string?)mode["currentValue"]);
        CollectionAssert.AreEqual(new[] { "read-only", "auto", "auto-review", "full-access" }, mode["options"]!.AsArray().Select(o => (string?)o!["value"]).ToArray());
        CollectionAssert.AreEqual(new[] { "full-access" }, mode["options"]!.AsArray()
            .Where(o => o!["_meta"]?[GorynychProtocol.MetaFullAccess] is JsonValue v && v.GetValue<bool>()).Select(o => (string?)o!["value"]).ToArray(),
            "the service asks before a session outside full access asks this one");
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = id, ["configId"] = "reasoning_effort", ["value"] = "low" });
        await PromptAsync(zed, id, "hello");
        await PromptAsync(zed, id, "again");

        var first = fake.All("turn/start")[0];
        Assert.AreEqual(("on-request", "workspaceWrite", "auto_review", "low"),
            ((string?)first["approvalPolicy"], (string?)first["sandboxPolicy"]!["type"], (string?)first["approvalsReviewer"], (string?)first["effort"]));
        Assert.IsNull(fake.All("turn/start")[1]["approvalPolicy"], "Codex keeps a turn's settings for the next ones");
    }

    // ---------------------------------------------------------------- sessions

    [TestMethod]
    public async Task TheList_IsCodexsThreadList_PageByPage_WithNamesAsTitles_AndArchivedLeftOut()
    {
        var fake = new FakeAppServer();
        fake.Existing("t1", Cwd, name: "Flaky test");
        fake.Existing("t2", Cwd);
        fake.Existing("t3", Cwd, name: "Old");
        fake.Existing("t4", Cwd, name: "Archived in Codex", archived: true);
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);

        var listed = await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());

        CollectionAssert.AreEquivalent(new[] { "t1", "t2", "t3" }, listed.Sessions.Select(s => s.SessionId).ToArray());
        Assert.AreEqual("Flaky test", listed.Sessions.Single(s => s.SessionId == "t1").Title);
        var lists = fake.All("thread/list");
        Assert.IsTrue(lists.Length >= 2, "followed nextCursor");
        Assert.IsTrue(lists.All(l => (bool?)l["archived"] == false && (string?)l["sortKey"] == "updated_at"));
    }

    [TestMethod]
    public async Task Resume_GivesTheServerAgain_AndExplainsAThreadOpenElsewhereOrArchived()
    {
        var fake = new FakeAppServer();
        fake.Existing("t-ok", Cwd, name: "Fine");
        fake.Existing("t-open", Cwd, name: "Open in the desktop app", openElsewhere: true);
        var later = fake.Existing("t-archived", Cwd, name: "Archived in Codex later");
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        JsonObject Resume(string id) => new() { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() };
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject()); // the service learns them all
        later.Archived = true; // then the person archives one in Codex Desktop

        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("t-ok"));
        var resumed = fake.Last("thread/resume");
        Assert.AreEqual((true, "svc.exe"), ((bool?)resumed["excludeTurns"], (string?)resumed["config"]!["mcp_servers"]!["gorynych"]!["command"]));

        var open = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("t-open")));
        Assert.AreEqual("This Codex thread is open in another Codex app (Codex Desktop, the CLI or VS Code); close it there, then try again.", open.Message);
        Assert.IsNull(open.ErrorData, "no data: Zed would show it as raw JSON");
        var archived = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("t-archived")));
        Assert.AreEqual("This Codex thread is archived in Codex. Unarchive it there (codex unarchive t-archived), then try again.", archived.Message);
    }

    [TestMethod]
    public async Task AThreadAnotherCodexAppHolds_IsMarkedInTheList_AndRefusedAtOnce_WithoutAskingAppServer()
    {
        var fake = new FakeAppServer();
        fake.Existing("t-desk", Cwd, name: "Open in Codex Desktop");
        fake.Existing("t-term", Cwd, name: "Open in a terminal");
        fake.Existing("t-free", Cwd, name: "Free");
        var held = new Dictionary<string, string> { ["t-desk"] = "Codex Desktop", ["t-term"] = "a terminal codex (pid 4242)" };
        var asked = new List<string[]>();
        await using var conductor = Conductor(fake, codex: Options(fake, holders: ids =>
        {
            var list = ids.ToArray();
            lock (asked) asked.Add(list);
            return held.Where(kv => list.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }));
        await using var zed = await ZedAsync(conductor);
        JsonObject Resume(string id) => new() { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() };

        var listed = await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());

        string? Mark(string id) => (string?)listed.Sessions.Single(s => s.SessionId == id).Meta?[GorynychProtocol.MetaOpenElsewhere];
        Assert.AreEqual(("Codex Desktop", "a terminal codex (pid 4242)", (string?)null), (Mark("t-desk"), Mark("t-term"), Mark("t-free")));
        lock (asked) CollectionAssert.AreEquivalent(new[] { "t-desk", "t-term", "t-free" }, asked.SelectMany(a => a).ToArray(), "once per page, each thread once");
        Assert.IsTrue(conductor.Agents["codex"].TellsOpenElsewhere);

        var desk = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("t-desk")));
        Assert.AreEqual("This Codex thread is open in Codex Desktop; close it there, then try again.", desk.Message);
        Assert.IsNull(desk.ErrorData, "the message names the holder; no data, which Zed would show as raw JSON");
        var term = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, Resume("t-term")));
        Assert.AreEqual("This Codex thread is open in a terminal codex (pid 4242); close it there, then try again.", term.Message);
        Assert.AreEqual(0, fake.All("thread/resume").Length, "app-server was not asked: it would refuse, and Codex Desktop is left alone");

        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("t-free"));
        Assert.AreEqual("t-free", (string?)fake.Last("thread/resume")["threadId"]);

        var answer = await (await conductor.Agents["codex"].GetAsync(CancellationToken.None)).SendRequestAsync(GorynychProtocol.MethodOpenElsewhere,
            new JsonObject { ["sessionIds"] = new JsonArray("t-desk", "t-free") });
        Assert.AreEqual("{\"t-desk\":\"Codex Desktop\"}", answer!["sessions"]!.ToJsonString(), "the held ones only");
    }

    [TestMethod]
    public async Task Load_OfAThreadMadeInCodex_ImportsItsHistory_FromItsTurns()
    {
        var fake = new FakeAppServer();
        fake.Existing("t1", Cwd, name: "Made in Codex Desktop");
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);

        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = "t1", ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });

        CollectionAssert.AreEqual(new[] { "user_message_chunk", "agent_thought_chunk", "tool_call", "agent_message_chunk" },
            zed.Kinds().Where(k => k is "user_message_chunk" or "agent_thought_chunk" or "tool_call" or "agent_message_chunk").ToArray());
        Assert.AreEqual(("t1-u1", "Fix the flaky test"), ((string?)Of(zed, "user_message_chunk")[0]["messageId"], (string?)Of(zed, "user_message_chunk")[0]["content"]!["text"]));
        Assert.AreEqual("t1-msg1", (string?)Of(zed, "agent_message_chunk")[0]["messageId"]);
        Assert.AreEqual("completed", (string?)Of(zed, "tool_call")[0]["status"]);
        Assert.IsTrue(zed.Snapshot().Where(u => (string?)u["sessionUpdate"] != "config_option_update").All(u => (string?)u["_meta"]?["gorynych/origin"] == "native"));
        var listed = fake.Last("thread/turns/list");
        Assert.AreEqual(("desc", "full"), ((string?)listed["sortDirection"], (string?)listed["itemsView"]));
        Assert.AreEqual("turn-0", conductor.Feed("t1").Journal.NativeMark, "the thread's newest turn");
    }

    // ---------------------------------------------------------------- the journal caught up with the thread

    /// <summary>The adapter alone, its updates kept: a resume with gorynych/replayFrom.</summary>
    static async Task<(JsonObject? Replay, List<JsonObject> Updates)> ResumeDirectAsync(FakeAppServer fake, string id, JsonObject replayFrom)
    {
        var agent = await new CodexAdapter(Options(fake)).StartAsync(CancellationToken.None);
        var updates = new List<JsonObject>();
        var acp = new JsonRpcConnection(agent.Input, agent.Output, "direct-client")
        {
            NotificationHandler = (method, p) =>
            {
                if (method == AcpMethods.Client.SessionUpdate) lock (updates) updates.Add((JsonObject)p!["update"]!.DeepClone());
                return Task.CompletedTask;
            },
        };
        acp.Start();
        try
        {
            var resumed = await acp.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject
            {
                ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray(),
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaReplayFrom] = replayFrom },
            });
            lock (updates) return (resumed?["_meta"]?[GorynychProtocol.MetaReplay] as JsonObject, updates.ToList());
        }
        finally
        {
            await acp.DisposeAsync();
            await agent.DisposeAsync();
        }
    }

    static JsonObject After(string mark) => new() { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = mark };

    /// <summary>The user's and the agent's messages, "user: …" or "agent: …", a message's chunks joined.</summary>
    static string[] Texts(IEnumerable<JsonObject> updates)
    {
        var texts = new List<(string Kind, string? Id, string Text)>();
        foreach (var u in updates.Where(u => (string?)u["sessionUpdate"] is "user_message_chunk" or "agent_message_chunk"))
        {
            var (kind, id, text) = (((string)u["sessionUpdate"]!).Split('_')[0], (string?)u["messageId"], (string?)u["content"]?["text"] ?? "");
            if (texts.Count > 0 && texts[^1].Kind == kind && texts[^1].Id == id) texts[^1] = (kind, id, texts[^1].Text + text);
            else texts.Add((kind, id, text));
        }
        return texts.Select(t => $"{t.Kind}: {t.Text}").ToArray();
    }

    [TestMethod]
    public async Task AResumeAfterAMark_ReplaysTheNewerTurnsInOrder_ReadingBackOnlyToTheMark()
    {
        var fake = new FakeAppServer(); // two turns a page
        fake.Existing("t1", Cwd);
        var turns = Enumerable.Range(1, 5).Select(i => fake.AddTurn("t1", $"Ask {i}", $"Answer {i}")).ToList();

        var (replay, updates) = await ResumeDirectAsync(fake, "t1", After(turns[2]));

        CollectionAssert.AreEqual(new[] { "user: Ask 4", "agent: Answer 4", "user: Ask 5", "agent: Answer 5" }, Texts(updates));
        Assert.AreEqual(("mark", turns[4]), ((string?)replay?["from"], (string?)replay?["mark"]));
        Assert.AreEqual(2, fake.All("thread/turns/list").Length, "newest first, until the marked turn: not the thread's six turns");
        Assert.AreEqual($"{turns[3]}-u", (string?)updates[0]["messageId"], "Codex's own IDs");
    }

    [TestMethod]
    public async Task AResumeAfterAMarkCodexNoLongerHas_ReplaysEveryTurn()
    {
        var fake = new FakeAppServer();
        fake.Existing("t1", Cwd);
        var second = fake.AddTurn("t1", "Ask", "Answer");
        fake.DropLastTurns("t1", 1); // rolled back in Codex's TUI
        var third = fake.AddTurn("t1", "Instead", "Another answer");

        var (replay, updates) = await ResumeDirectAsync(fake, "t1", After(second));

        CollectionAssert.AreEqual(new[] { "user: Fix the flaky test", "agent: Fixed it.", "user: Instead", "agent: Another answer" }, Texts(updates));
        Assert.AreEqual(("start", third), ((string?)replay?["from"], (string?)replay?["mark"]));
    }

    [TestMethod]
    public async Task APrompt_EndsWithItsTurnAsTheMark()
    {
        var fake = new FakeAppServer();
        var (agent, acp) = await DirectAsync(fake);
        await using var _ = agent;
        await using var __ = acp;
        var id = await NewDirectAsync(acp);

        var result = await PromptDirectAsync(acp, id, "hello");

        Assert.AreEqual(fake.LastTurnId, (string?)result?["_meta"]?[GorynychProtocol.MetaHistoryMark]);
    }

    [TestMethod]
    public async Task ATurnMadeInCodexDesktop_LandsBeforeTheNextPrompt_OnceInTheJournal()
    {
        var time = new ManualTime();
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake, time);
        string id;
        await using (var zed = await ZedAsync(conductor))
        {
            id = await NewAsync(zed);
            await PromptAsync(zed, id, "hello");
        }
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
        time.Advance(TimeSpan.FromMinutes(2)); // Codex's idle time: the adapter lets the thread go
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
        var desktop = fake.AddTurn(id, "Continue in Desktop", "Done in Desktop");

        await using (var zed = await ZedAsync(conductor, "zed-again"))
            await PromptAsync(zed, id, "Next");

        await using var viewer = await ZedAsync(conductor, "viewer");
        await viewer.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        CollectionAssert.AreEqual(new[] { "user: hello", "agent: ok", "user: Continue in Desktop", "agent: Done in Desktop", "user: Next", "agent: ok" },
            Texts(viewer.Snapshot()), string.Join(" | ", Texts(viewer.Snapshot())));
        Assert.AreEqual(fake.LastTurnId, conductor.Feed(id).Journal.NativeMark, "the last prompt's turn");
        Assert.AreNotEqual(desktop, fake.LastTurnId);
    }

    [TestMethod]
    public async Task Delete_DeletesTheThread_AndAThreadCodexNoLongerHasCountsAsDeleted()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);
        await PromptAsync(zed, id, "hello");

        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });
        Assert.IsFalse(fake.Threads.ContainsKey(id));
        Assert.AreEqual(id, (string?)fake.Last("thread/delete")["threadId"]);
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id }); // already gone: no error
    }

    // ---------------------------------------------------------------- what the service does with the adapter's flags

    [TestMethod]
    public async Task ADraftCodexNeverWrote_IsDropped_WithoutATombstone()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        var zed = await ZedAsync(conductor);
        var draft = await NewAsync(zed);

        await zed.DisposeAsync();
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);

        Assert.AreEqual(draft, (string?)fake.Last("thread/unsubscribe")["threadId"]);
        CollectionAssert.DoesNotContain(fake.Methods(), "thread/delete", "nothing durable to delete");
        Assert.IsNull(conductor.Store.Get(draft), "dropped, not hidden behind a tombstone");
    }

    [TestMethod]
    public async Task AnIdleCodexSession_IsUnloadedAfterCodexsOwnIdleTime()
    {
        var time = new ManualTime();
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake, time);
        var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);
        await PromptAsync(zed, id, "hello");

        await zed.DisposeAsync();
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
        time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
        CollectionAssert.DoesNotContain(fake.Methods(), "thread/unsubscribe");
        time.Advance(TimeSpan.FromSeconds(1));
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
        Assert.AreEqual(id, (string?)fake.Last("thread/unsubscribe")["threadId"], "two minutes, not the service's ten");
    }

    [TestMethod]
    public async Task ASpawnedCodexSession_GetsItsNameNatively()
    {
        var fake = new FakeAppServer();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(fake, more: [kimi.Adapter]);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = (await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd, McpServers = [], Meta = new JsonObject { ["gorynych/agent"] = "kimi" },
        })).SessionId;
        await PromptAsync(zed, parent, "Coordinate the release");

        var child = (string)(await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject
        {
            ["agent"] = "codex", ["prompt"] = "Write the notes", ["title"] = "Release notes",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Tokens[parent] },
        }))!["sessionId"]!;
        await conductor.BackgroundTurnsIdleAsync().WaitAsync(Wait);

        Assert.AreEqual(("Release notes", child), ((string?)fake.Last("thread/name/set")["name"], (string?)fake.Last("thread/name/set")["threadId"]));
        Assert.AreEqual("Release notes", fake.Threads[child].Name);
        Assert.AreEqual(2, fake.Last("turn/start")["input"]!.AsArray().Count, "the task and where it came from, as two inputs");
    }

    [TestMethod]
    public async Task AHugeIdleTimeFromAnAdapter_IsCappedAtADay()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake, codex: new CodexAdapterOptions { Launch = fake.StartAsync, UnloadAfter = TimeSpan.MaxValue });
        var host = conductor.Agents["codex"];
        await host.GetAsync(CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromDays(1), host.UnloadAfter);
    }

    // ---------------------------------------------------------------- settings Codex has and Gorynych has no preset for

    static JsonObject OwnSettings(JsonNode approvalPolicy, JsonObject sandbox) => new()
    {
        ["approvalPolicy"] = approvalPolicy, ["approvalsReviewer"] = "user", ["model"] = "gpt-fake", ["modelProvider"] = "openai",
        ["reasoningEffort"] = "medium", ["sandbox"] = sandbox,
    };

    [TestMethod]
    public async Task ThreadSettingsThatAskNothing_WithNoSandbox_CountAsFullAccess_WhateverTheReviewer()
    {
        var fake = new FakeAppServer();
        var settings = OwnSettings("never", new JsonObject { ["type"] = "dangerFullAccess" });
        settings["approvalsReviewer"] = "auto_review";
        fake.Existing("t-yolo", Cwd, settings: settings);
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);

        var resumed = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = "t-yolo", ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });

        var mode = resumed!["configOptions"]!.AsArray().First(o => (string?)o!["id"] == "mode")!;
        Assert.AreEqual("custom", (string?)mode["currentValue"]);
        Assert.IsTrue(Gorynych.Service.Conductor.IsFullAccess(resumed.AsObject()), "the service asks before a session outside full access asks this one");
    }

    [TestMethod]
    public async Task AModelOrEffortChange_OnAThreadWithItsOwnSettings_SendsOnlyThat()
    {
        var fake = new FakeAppServer();
        fake.Existing("t-own", Cwd, settings: OwnSettings(
            new JsonObject { ["granular"] = new JsonObject { ["sandbox_approval"] = true, ["rules"] = false } },
            new JsonObject { ["type"] = "externalSandbox", ["networkAccess"] = "enabled" }));
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        var resumed = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = "t-own", ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        var mode = resumed!["configOptions"]!.AsArray().First(o => (string?)o!["id"] == "mode")!;
        Assert.AreEqual("custom", (string?)mode["currentValue"]);
        StringAssert.Contains(mode["options"]!.AsArray().Last()!["description"]!.ToString(), "approval granular, sandbox externalSandbox");

        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = "t-own", ["configId"] = "reasoning_effort", ["value"] = "low" });
        var nope = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = "t-own", ["configId"] = "model", ["value"] = "gpt-imaginary" }));
        Assert.AreEqual(JsonRpcException.InvalidParams, nope.Code, "a model Codex does not offer is not held for the next turn");
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(zed, "t-own", "hello")).StopReason);
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(zed, "t-own", "again")).StopReason);

        var first = fake.All("turn/start")[0];
        Assert.AreEqual("low", (string?)first["effort"]);
        foreach (var key in new[] { "model", "approvalPolicy", "sandboxPolicy", "approvalsReviewer" })
            Assert.IsNull(first[key], $"{key} is the thread's own and is not sent");
        Assert.IsNull(fake.All("turn/start")[1]["effort"], "sent once: Codex keeps it");
    }

    [TestMethod]
    public async Task AModeChange_KeepsTheThreadsOwnSandbox_WithItsWritableRootsAndNetwork()
    {
        var fake = new FakeAppServer();
        var roots = new JsonObject { ["type"] = "workspaceWrite", ["writableRoots"] = new JsonArray("D:\\shared"), ["networkAccess"] = true };
        fake.Existing("t-roots", Cwd, settings: OwnSettings("untrusted", roots));
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = "t-roots", ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        Task SetMode(string value) => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = "t-roots", ["configId"] = "mode", ["value"] = value });

        await SetMode("auto");
        await PromptAsync(zed, "t-roots", "hello");
        await SetMode("read-only");
        await PromptAsync(zed, "t-roots", "again");
        await SetMode("auto");
        await PromptAsync(zed, "t-roots", "and again");

        var starts = fake.All("turn/start");
        Assert.AreEqual(("on-request", "user"), ((string?)starts[0]["approvalPolicy"], (string?)starts[0]["approvalsReviewer"]));
        Assert.IsTrue(JsonNode.DeepEquals(roots, starts[0]["sandboxPolicy"]), starts[0]["sandboxPolicy"]?.ToJsonString());
        Assert.AreEqual("readOnly", (string?)starts[1]["sandboxPolicy"]!["type"]);
        Assert.IsTrue(JsonNode.DeepEquals(roots, starts[2]["sandboxPolicy"]), "back to Auto, the thread's own roots come back");
        Assert.IsNull(starts[0]["model"]);
    }

    // ---------------------------------------------------------------- the end of a turn, whatever happens

    [TestMethod]
    public async Task ACancel_BeforeCodexNamesTheTurn_InterruptsItOnceNamed()
    {
        var fake = new FakeAppServer { HoldTurnStart = new TaskCompletionSource() };
        var log = new List<string>();
        await using var conductor = Conductor(fake, codex: Options(fake, log));
        await using var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);

        var turn = PromptAsync(zed, id, "wait");
        await Until(() => fake.Methods().Contains("turn/start"), "turn/start");
        await zed.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = id });
        await Until(() => Logged(log, "stop asked before Codex named the turn"), "the cancel to reach the adapter");
        fake.HoldTurnStart.SetResult();

        Assert.AreEqual(StopReason.Cancelled, (await turn.WaitAsync(Wait)).StopReason);
        Assert.IsNotNull(fake.Last("turn/interrupt")["turnId"]);
    }

    [TestMethod]
    public async Task ACancel_RightBehindThePrompt_StopsIt()
    {
        var fake = new FakeAppServer();
        var (agent, acp) = await DirectAsync(fake);
        await using var _ = agent;
        await using var __ = acp;
        var id = await NewDirectAsync(acp);

        // The prompt runs on its own task and the cancel in the read loop, so the cancel is usually
        // handled first; either way the turn stops. The order itself is pinned by the test below.
        var turn = PromptDirectAsync(acp, id, "wait");
        await acp.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = id });

        Assert.AreEqual("cancelled", (string?)(await turn.WaitAsync(Wait))!["stopReason"]);
    }

    [TestMethod]
    public void ACancelHandledBeforeItsPromptBegins_StopsThatTurn_ButNotALaterOne()
    {
        var t = new CodexThread("t", new CodexMap.CodexSettings("gpt-fake", "low", null, null, "user"));

        // The prompt was message 5 of the ACP stream and the cancel message 6, handled first.
        Assert.AreEqual((false, (string?)null), t.Stop(6));
        var first = t.Begin(5)!;
        Assert.IsTrue(t.Named("turn-1"), "interrupted as soon as Codex names it");
        t.End(first);

        t.Begin(7);
        Assert.IsFalse(t.Named("turn-2"), "a prompt sent after the cancel is not stopped by it");
    }

    [TestMethod]
    public async Task ATurnCodexRefuses_DropsTheHeldChange_SoTheNextTurnRuns()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = id, ["configId"] = "model", ["value"] = "gpt-hidden" });

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(zed, id, "hello"));
        StringAssert.Contains(refused.Message, "were reset");
        var reset = Of(zed, "config_option_update").Last()["configOptions"]!.AsArray().First(o => (string?)o!["id"] == "model")!;
        Assert.AreEqual("gpt-fake", (string?)reset["currentValue"], "the client sees the thread's own model again");

        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(zed, id, "again")).StopReason);
        Assert.IsNull(fake.All("turn/start").Last()["model"]);
    }

    [TestMethod]
    public async Task Close_WhenUnsubscribingFails_StillEndsTheTurn()
    {
        var fake = new FakeAppServer();
        var (agent, acp) = await DirectAsync(fake);
        await using var _ = agent;
        await using var __ = acp;
        var id = await NewDirectAsync(acp);
        var turn = PromptDirectAsync(acp, id, "stuck");
        await Until(() => fake.Methods().Contains("turn/start"), "turn/start");
        fake.FailUnsubscribe = true;

        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => acp.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id }));

        Assert.AreEqual("cancelled", (string?)(await turn.WaitAsync(Wait))!["stopReason"]);
    }

    [TestMethod]
    public async Task ALateUnloadNote_ForAThreadOpenedAgain_IsIgnored()
    {
        var fake = new FakeAppServer();
        var log = new List<string>();
        var (agent, acp) = await DirectAsync(fake, log);
        await using var _ = agent;
        await using var __ = acp;
        var id = await NewDirectAsync(acp);
        await PromptDirectAsync(acp, id, "hello");
        await acp.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
        await acp.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });

        // the note of the first unload, late
        await fake.Rpc!.SendNotificationAsync("thread/closed", new JsonObject { ["threadId"] = id });
        await Until(() => Logged(log, "Codex still has the thread loaded; ignored"), "the note checked against thread/loaded/list");

        Assert.AreEqual("end_turn", (string?)(await PromptDirectAsync(acp, id, "again").WaitAsync(Wait))!["stopReason"]);
    }

    [TestMethod]
    public async Task ALateEndOfATurnClosedBefore_IsNotTakenForTheNextTurns()
    {
        var fake = new FakeAppServer();
        var (agent, acp) = await DirectAsync(fake);
        await using var _ = agent;
        await using var __ = acp;
        var id = await NewDirectAsync(acp);
        await PromptDirectAsync(acp, id, "hello");
        var first = fake.LastTurnId;
        var stuck = PromptDirectAsync(acp, id, "stuck");
        await Until(() => fake.LastTurnId != first, "the stuck turn");
        var abandonedTurn = fake.LastTurnId!;
        await acp.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
        Assert.AreEqual("cancelled", (string?)(await stuck.WaitAsync(Wait))!["stopReason"]);
        await acp.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });

        // The next turn has begun but Codex has not named it yet, when the closed turn's end comes late.
        fake.HoldTurnStart = new TaskCompletionSource();
        var next = PromptDirectAsync(acp, id, "wait");
        await Until(() => fake.All("turn/start").Length == 3, "the third turn/start");
        await fake.Rpc!.SendNotificationAsync("turn/completed", new JsonObject
        {
            ["threadId"] = id, ["turn"] = new JsonObject { ["id"] = abandonedTurn, ["items"] = new JsonArray(), ["status"] = "completed" },
        });
        fake.HoldTurnStart.SetResult();
        await Until(() => fake.LastTurnId != abandonedTurn, "the next turn");
        await acp.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = id });

        Assert.AreEqual("cancelled", (string?)(await next.WaitAsync(Wait))!["stopReason"], "it ran on until cancelled, not ended by the late note");
    }

    [TestMethod]
    public async Task Close_DuringATurnCodexDoesNotEnd_EndsThePromptAsCancelled()
    {
        var fake = new FakeAppServer();
        var (agent, acp) = await DirectAsync(fake);
        await using var _ = agent;
        await using var __ = acp;
        var id = await NewDirectAsync(acp);

        var turn = PromptDirectAsync(acp, id, "stuck");
        await Until(() => fake.Methods().Contains("turn/start"), "turn/start");
        await acp.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });

        Assert.AreEqual("cancelled", (string?)(await turn.WaitAsync(Wait))!["stopReason"]);
        await Until(() => fake.Methods().Contains("turn/interrupt") && fake.Methods().Contains("thread/unsubscribe"), "the interrupt and the unload");
        var again = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptDirectAsync(acp, id, "hello"));
        StringAssert.Contains(again.Message, "resume it first", "unloaded, not a turn still running");
    }

    [TestMethod]
    public async Task CodexUnloadingTheThreadMidTurn_EndsThePromptAsCancelled()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);

        Assert.AreEqual(StopReason.Cancelled, (await PromptAsync(zed, id, "closed").WaitAsync(Wait)).StopReason);
    }

    [TestMethod]
    public async Task TheAppServerDyingMidTurn_FailsThePrompt_AndTheAdapterExits()
    {
        var fake = new FakeAppServer();
        var (agent, acp) = await DirectAsync(fake);
        await using var _ = agent;
        await using var __ = acp;
        var id = await NewDirectAsync(acp);

        var turn = PromptDirectAsync(acp, id, "wait");
        await Until(() => fake.Methods().Contains("turn/start"), "turn/start");
        await fake.Rpc!.DisposeAsync();

        Exception? failed = null;
        try { await turn.WaitAsync(Wait); }
        catch (Exception e) when (e is not TimeoutException) { failed = e; }
        Assert.IsNotNull(failed, "the prompt fails, not hangs");
        await agent.Exited.WaitAsync(Wait);
    }

    // ---------------------------------------------------------------- approvals: what is offered, and only that

    [TestMethod]
    public async Task AnOptionThatWasNotOffered_IsADecline()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        var id = await NewAsync(zed);

        zed.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "acceptWithExecpolicyAmendment" });
        await PromptAsync(zed, id, "run");
        zed.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "always" });
        await PromptAsync(zed, id, "mcp");

        Assert.AreEqual("decline", (string?)fake.Answers[0].Answer!["decision"]);
        Assert.AreEqual("decline", (string?)fake.Answers[1].Answer!["action"]);
        Assert.IsNull(fake.Answers[1].Answer!["_meta"], "nothing persisted");
    }

    [TestMethod]
    public async Task AWithdrawnPermissionsQuestion_GrantsNothing()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        zed.Approve = r => new TaskCompletionSource<RequestPermissionOutcome>().Task;
        var id = await NewAsync(zed);

        var turn = PromptAsync(zed, id, "perm");
        await Until(() => zed.Approvals.Count == 1, "the question");
        Assert.AreEqual("Allow more access: Fetch the docs", zed.Approvals[0].ToolCall.Title);
        CollectionAssert.AreEqual(new[] { "turn", "session", "deny" }, zed.Approvals[0].Options.Select(o => o.OptionId).ToArray());
        await zed.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = id });

        Assert.AreEqual(StopReason.Cancelled, (await turn.WaitAsync(Wait)).StopReason);
        await Until(() => { lock (fake.Answers) return fake.Answers.Count == 1; }, "the withdrawn question's answer");
        Assert.IsTrue(JsonNode.DeepEquals(new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" }, fake.Answers[0].Answer),
            fake.Answers[0].Answer?.ToJsonString());
        await Until(() => zed.Withdrawn == 1, "the question withdrawn from the client");
    }

    [TestMethod]
    public async Task AnApprovalThatOvertakesItsItem_StillSaysWhatChanges()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        zed.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "decline" });
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "patch late");

        var asked = zed.Approvals.Single();
        Assert.AreEqual("Create note.txt", asked.ToolCall.Title);
        Assert.AreEqual(Path.Combine(Cwd, "note.txt"), asked.ToolCall.Locations![0].Path);
    }

    [TestMethod]
    public async Task AnMcpServersOwnForm_IsAnAcpForm_AndItsAnswerGoesBack()
    {
        var fake = new FakeAppServer();
        await using var conductor = Conductor(fake);
        await using var zed = await ZedAsync(conductor);
        zed.Fill = form => new ElicitationAcceptAction { Content = new() { ["branch"] = "main" } };
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "form");

        Assert.AreEqual("Which branch?", zed.Forms.Single().Message);
        Assert.IsTrue(JsonNode.DeepEquals(new JsonObject { ["action"] = "accept", ["content"] = new JsonObject { ["branch"] = "main" } }, fake.Answers.Single().Answer),
            fake.Answers.Single().Answer?.ToJsonString());
        Assert.AreEqual("branch main", zed.Text());
    }
}
