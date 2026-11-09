using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The journal caught up with the agent's own history (docs/service-protocol.md, "History made
/// outside Gorynych"): whenever the service opens a session that is not loaded, before anything
/// runs in it, what the agent's history gained outside Gorynych is added once, in its place, marked
/// <c>native</c>; a history rewritten outside is imported again, with a notice and the earlier
/// journal kept. The fake agents keep a history of their own: <c>KeepsHistory</c> with marks and
/// IDs as Codex, <c>LoadsWithoutIds</c> as Kimi.
/// </summary>
[TestClass]
public sealed class HistoryCatchUpTests
{
    static readonly string Cwd = Path.GetTempPath();

    static Conductor Conductor(params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        UnloadAfter = TimeSpan.Zero, // a session its client left is closed in its agent at once
    }, adapters);

    static async Task<string> NewPromptedAsync(TestClient client, string agent, string text)
    {
        var id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        })).SessionId;
        await PromptAsync(client, id, text);
        return id;
    }

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static Task LoadAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = sessionId, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });

    static Task CloseAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId });

    /// <summary>A session one client made and prompted, then closed: it is closed in its agent too.</summary>
    static async Task<string> LeftSessionAsync(Conductor conductor, string agent, string text)
    {
        string id;
        await using (var owner = await TestClient.ConnectAsync(conductor, "owner"))
        {
            id = await NewPromptedAsync(owner, agent, text);
            await CloseAsync(owner, id);
        }
        await conductor.CleanupsIdleAsync();
        Assert.IsFalse(conductor.Agents[agent].IsLive(id), "closed in its agent");
        return id;
    }

    /// <summary>A prompt through a client that then leaves, so the session is closed in its agent again.</summary>
    static async Task PromptAndLeaveAsync(Conductor conductor, string sessionId, string text)
    {
        await using (var client = await TestClient.ConnectAsync(conductor, "again"))
        {
            await PromptAsync(client, sessionId, text);
            await CloseAsync(client, sessionId);
        }
        await conductor.CleanupsIdleAsync();
    }

    /// <summary>What a client that loads the session sees, message by message: "user: …", "agent: …".</summary>
    static async Task<(string[] Texts, JsonObject[] Updates)> SeenAsync(Conductor conductor, string sessionId)
    {
        await using var client = await TestClient.ConnectAsync(conductor, "viewer");
        await LoadAsync(client, sessionId);
        var updates = client.Snapshot();
        return (Texts(updates), updates);
    }

    static string[] Texts(IEnumerable<JsonNode?> updates) =>
        updates.OfType<JsonObject>()
            .Where(u => (string?)u["sessionUpdate"] is "user_message_chunk" or "agent_message_chunk")
            .Select(u => $"{((string)u["sessionUpdate"]!).Split('_')[0]}: {(string?)u["content"]?["text"]}")
            .ToArray();

    static string? Origin(JsonObject update) => (string?)update["_meta"]?[GorynychProtocol.MetaOrigin];

    [TestMethod]
    public async Task ATurnMadeOutside_IsAddedOnce_InItsPlace_BeforeTheNextTurnTheServiceRuns()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        await using var conductor = Conductor(codex.Adapter);
        var id = await LeftSessionAsync(conductor, "codex", "First");
        codex.AddNativeTurn(id, "Outside", "Done outside"); // in Codex Desktop, while Gorynych had let it go

        await PromptAndLeaveAsync(conductor, id, "Next");   // the prompt opens it: caught up first, then its turn

        var (texts, updates) = await SeenAsync(conductor, id);
        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Outside", "agent: Done outside", "user: Next", "agent: ok" }, texts);
        Assert.IsTrue(updates.Where(u => (string?)u["content"]?["text"] is "Outside" or "Done outside").All(u => Origin(u) == "native"));
        Assert.IsTrue(updates.Where(u => (string?)u["content"]?["text"] is "First" or "Next").All(u => Origin(u) is null), "the service's own turns are its own");
        Assert.AreEqual("gorynych/after", (string?)codex.Replays[0].ReplayFrom["type"]);
        Assert.AreEqual("t1", (string?)codex.Replays[0].ReplayFrom["mark"], "the mark the first turn ended with");

        // Opened again, twice: nothing is added again, and each open asks after the last turn.
        var again = (await SeenAsync(conductor, id)).Texts;
        await conductor.CleanupsIdleAsync();
        CollectionAssert.AreEqual(texts, again);
        CollectionAssert.AreEqual(texts, (await SeenAsync(conductor, id)).Texts);
        Assert.AreEqual(codex.Replays.Count, codex.Replays.Select(r => (string?)r.ReplayFrom["mark"]).Count(m => m is not null));
        Assert.AreEqual("t3", (string?)codex.Replays[^1].ReplayFrom["mark"]);
    }

    [TestMethod]
    public async Task AQuestionFromAnotherSession_IsAskedAfterTheTurnsMadeOutside()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(codex.Adapter, kimi.Adapter);
        var target = await LeftSessionAsync(conductor, "codex", "First");
        codex.AddNativeTurn(target, "Outside", "Done outside");
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "kimi", "Coordinate");

        // ask_agent wakes the session: the question is its next turn, after what was done outside.
        var answer = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = target, ["text"] = "Is it done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Tokens[asker] },
        });

        Assert.AreEqual("ok", (string?)answer!["text"]);
        var texts = (await SeenAsync(conductor, target)).Texts;
        var outside = Array.IndexOf(texts, "user: Outside");
        var question = Array.FindIndex(texts, t => t.StartsWith("user: [Opinion request", StringComparison.Ordinal));
        Assert.IsTrue(outside >= 0 && question > outside, string.Join(" | ", texts));
    }

    [TestMethod]
    public async Task ASessionStillLoaded_IsNotCaughtUp()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        await using var conductor = Conductor(codex.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewPromptedAsync(zed, "codex", "First");
        codex.AddNativeTurn(id, "Outside", "Done outside"); // cannot happen with one writer; the service does not look

        var texts = (await SeenAsync(conductor, id)).Texts;

        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok" }, texts);
        Assert.AreEqual(0, codex.Replays.Count);
    }

    [TestMethod]
    public async Task AStaleMark_RepeatsNothing()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        await using var conductor = Conductor(codex.Adapter);
        var id = await LeftSessionAsync(conductor, "codex", "First");
        await PromptAndLeaveAsync(conductor, id, "Second");
        conductor.Feed(id).Journal.NativeMark = "t1"; // as if the service stopped before keeping the second turn's mark
        codex.AddNativeTurn(id, "Outside", "Done outside");

        var texts = (await SeenAsync(conductor, id)).Texts;

        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Second", "agent: ok", "user: Outside", "agent: Done outside" }, texts);
    }

    [TestMethod]
    public async Task AJournalWithoutAMark_IsBroughtUpToDate_ByTheMessagesIds()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        await using var conductor = Conductor(codex.Adapter);
        var id = await LeftSessionAsync(conductor, "codex", "First");
        conductor.Feed(id).Journal.NativeMark = null; // a journal from before marks
        var outside = codex.AddNativeTurn(id, "Outside", "Done outside");

        var texts = (await SeenAsync(conductor, id)).Texts;

        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Outside", "agent: Done outside" }, texts);
        Assert.AreEqual("start", (string?)codex.Replays[^1].ReplayFrom["type"]);
        Assert.AreEqual(outside, conductor.Feed(id).Journal.NativeMark);
    }


    [TestMethod]
    public async Task ASessionTheJournalNeverSaw_IsImportedFromTheAgent_AsItIs()
    {
        var codex = new LifecycleAgent("codex", canDelete: true, existing: ["made-in-desktop"]) { KeepsHistory = true };
        var last = codex.AddNativeTurn("made-in-desktop", "Hello", "Hi");
        await using var conductor = Conductor(codex.Adapter);

        var (texts, updates) = await SeenAsync(conductor, "made-in-desktop");

        CollectionAssert.AreEqual(new[] { "user: Hello", "agent: Hi" }, texts);
        Assert.IsTrue(updates.All(u => Origin(u) is null), "a first import is just that: not marked native");
        Assert.AreEqual(last, conductor.Feed("made-in-desktop").Journal.NativeMark);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptAndAnotherLoad_DuringACatchUp_WaitForIt()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        await using var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(),
            ServiceId = "t",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
            UnloadAfter = TimeSpan.Zero,
            CatchingUp = async _ =>
            {
                if (Interlocked.Increment(ref first) != 1) return;
                holding.TrySetResult();
                await release.Task;
            },
        }, [codex.Adapter]);
        var id = await LeftSessionAsync(conductor, "codex", "First");
        codex.AddNativeTurn(id, "Outside", "Done outside");

        await using var viewer = await TestClient.ConnectAsync(conductor, "viewer");
        var loading = LoadAsync(viewer, id);
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the agent answered the open; the catch-up is held
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var prompting = PromptAsync(zed, id, "Next");
        await using var other = await TestClient.ConnectAsync(conductor, "other");
        var loadingToo = LoadAsync(other, id);
        await Task.Delay(300);
        Assert.IsFalse(prompting.IsCompleted || loadingToo.IsCompleted, "both wait for the catch-up");
        lock (codex.Prompts) Assert.AreEqual(1, codex.Prompts.Count, "the prompt has not reached the agent");
        release.SetResult();
        await Task.WhenAll(loading, prompting, loadingToo).WaitAsync(TimeSpan.FromSeconds(20));

        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Outside", "agent: Done outside", "user: Next", "agent: ok" },
            Texts(conductor.Feed(id).Journal.ReadAll()));
        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Outside", "agent: Done outside" }, Texts(other.Snapshot()).Take(4).ToArray());
        Assert.AreEqual(codex.Prompts.Count, 2);
        Assert.AreEqual("t3", conductor.Feed(id).Journal.NativeMark, "the prompt's turn, after the native one");
    }

    [TestMethod]
    public async Task AnEmptyReplay_KeepsTheJournal()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true) { LoadsWithoutIds = true };
        await using var conductor = Conductor(codex.Adapter, kimi.Adapter);
        var withMark = await LeftSessionAsync(conductor, "codex", "First");
        var withoutMarks = await LeftSessionAsync(conductor, "kimi", "First");
        codex.DropNativeTurns(withMark, 1);   // an empty or unreadable history: the mark is gone, nothing comes back
        kimi.DropNativeTurns(withoutMarks, 1);

        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok" }, (await SeenAsync(conductor, withMark)).Texts);
        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok" }, (await SeenAsync(conductor, withoutMarks)).Texts);
        Assert.AreEqual(0, conductor.Feed(withMark).Journal.KeptHistories().Count, "nothing imported again");
        Assert.AreEqual(0, conductor.Feed(withoutMarks).Journal.KeptHistories().Count);
    }

    [TestMethod]
    public async Task AMarkTheAgentNoLongerHas_ButAHistoryThatStillHoldsTheJournal_OnlyAppends()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        await using var conductor = Conductor(codex.Adapter);
        var id = await LeftSessionAsync(conductor, "codex", "First");
        conductor.Feed(id).Journal.NativeMark = "a-turn-it-forgot"; // replayed from the start
        var outside = codex.AddNativeTurn(id, "Outside", "Done outside");

        var (texts, updates) = await SeenAsync(conductor, id);

        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Outside", "agent: Done outside" }, texts);
        Assert.IsFalse(updates.Any(u => Origin(u) == "notice"), "no notice: the journal is the start of the history");
        Assert.AreEqual(0, conductor.Feed(id).Journal.KeptHistories().Count);
        Assert.AreEqual(outside, conductor.Feed(id).Journal.NativeMark);
    }

    [TestMethod]
    public async Task AHistoryRewrittenOutside_IsImportedAgain_WithANotice_AndTheEarlierJournalKept()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        await using var conductor = Conductor(codex.Adapter);
        var id = await LeftSessionAsync(conductor, "codex", "First");
        await PromptAndLeaveAsync(conductor, id, "Second");
        var firstId = (await SeenAsync(conductor, id)).Updates.Single(u => (string?)u["content"]?["text"] == "First")["messageId"]!.GetValue<string>();
        await conductor.CleanupsIdleAsync();
        codex.DropNativeTurns(id, 1); // rolled back in Codex: the mark's turn is gone
        var instead = codex.AddNativeTurn(id, "Instead", "Another answer");

        var (texts, updates) = await SeenAsync(conductor, id);

        StringAssert.StartsWith(texts[0], "user: [Gorynych: codex's own history of this session no longer holds");
        Assert.AreEqual("notice", Origin(updates[0]));
        Assert.AreEqual("historyRewritten", (string?)updates[0]["_meta"]?[GorynychProtocol.MetaNotice]);
        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Instead", "agent: Another answer" }, texts[1..]);
        Assert.AreEqual(firstId, (string?)updates.Single(u => (string?)u["content"]?["text"] == "First")["messageId"], "a message that is still there keeps its ID");
        var kept = conductor.Feed(id).Journal.KeptHistories();
        Assert.AreEqual(1, kept.Count);
        StringAssert.Contains(File.ReadAllText(kept[0]), "Second", "the earlier journal is kept");
        Assert.AreEqual(instead, conductor.Feed(id).Journal.NativeMark);
    }

    [TestMethod]
    public async Task AnAgentThatCannotReadItsHistory_LeavesTheJournalAsItIs()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true, HistoryUnreadable = true };
        await using var conductor = Conductor(codex.Adapter);
        var id = await LeftSessionAsync(conductor, "codex", "First");
        codex.AddNativeTurn(id, "Outside", "Done outside");

        var texts = (await SeenAsync(conductor, id)).Texts;

        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok" }, texts);
        Assert.AreEqual("t1", conductor.Feed(id).Journal.NativeMark);
    }

    [TestMethod]
    public async Task AnAgentWithoutMarks_IsAlignedByText_AlsoAfterACompaction_AndImportedAgainAfterAnUndo()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true) { LoadsWithoutIds = true };
        await using var conductor = Conductor(kimi.Adapter);
        var id = await LeftSessionAsync(conductor, "kimi", "First");
        await PromptAndLeaveAsync(conductor, id, "Second"); // reopened with its load: its own turns are not added again
        kimi.AddNativeTurn(id, "Outside", "Done outside");
        kimi.CompactNatively(id, keep: 2); // its replay is now a summary, Second and Outside

        var texts = (await SeenAsync(conductor, id)).Texts;

        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Second", "agent: ok", "user: Outside", "agent: Done outside" }, texts);

        await conductor.CleanupsIdleAsync();
        kimi.DropNativeTurns(id, 1); // an undo in the Kimi CLI: Outside is gone
        var (after, updates) = await SeenAsync(conductor, id);
        Assert.AreEqual("notice", Origin(updates[0]));
        CollectionAssert.AreEqual(new[] { "agent: Summary of the earlier work.", "user: Second", "agent: ok" }, after[1..]);
    }

    [TestMethod]
    public async Task ATextThatRepeats_IsNotTakenForTheSameTurn_SoNoTurnIsSkipped()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true) { LoadsWithoutIds = true };
        await using var conductor = Conductor(kimi.Adapter);
        var id = await LeftSessionAsync(conductor, "kimi", "Start");
        await PromptAndLeaveAsync(conductor, id, "continue");
        kimi.AddNativeTurn(id, "continue", "Done outside");
        kimi.CompactNatively(id, keep: 2); // continue, continue: which one is the journal's?

        var (texts, updates) = await SeenAsync(conductor, id);

        Assert.AreEqual("notice", Origin(updates[0]), "ambiguous: imported again rather than a turn lost");
        CollectionAssert.AreEqual(new[] { "agent: Summary of the earlier work.", "user: continue", "agent: ok", "user: continue", "agent: Done outside" }, texts[1..]);
    }

    [TestMethod]
    public async Task UpdatesThatAreNotTurns_DuringAResume_ArePublished()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { KeepsHistory = true };
        await using var conductor = Conductor(codex.Adapter);
        var id = await LeftSessionAsync(conductor, "codex", "First");
        codex.AddNativeTurn(id, "Outside", "Done outside");
        codex.BeforeResume = async sid => await codex.Adapter.Current!.Update(sid, new SessionInfoUpdate { Title = "Named in Codex" });

        var (texts, updates) = await SeenAsync(conductor, id);

        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Outside", "agent: Done outside" }, texts);
        Assert.AreEqual("Named in Codex", conductor.Store.Get(id)?.Title, "the title went through, not into the replay");
    }

    [TestMethod]
    public async Task ALoadOfANeverJournaledSession_ThatItsAgentCannotResume_ShowsNothingInsteadOfFailing()
    {
        // A resume-only agent (Claude, DSH): as before, the history is not worth an error there.
        var claude = new LifecycleAgent("claude", canDelete: false, existing: ["never-journaled"])
        {
            BeforeResume = _ => throw new JsonRpcException(JsonRpcException.InternalError, "cannot open it now"),
        };
        await using var conductor = Conductor(claude.Adapter);

        var (texts, _) = await SeenAsync(conductor, "never-journaled");

        Assert.AreEqual(0, texts.Length);
    }

    // ---------------------------------------------------------------- the plan alone

    static JsonObject Chunk(string kind, string text, string? id = null) =>
        new() { ["sessionUpdate"] = kind, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text }, ["messageId"] = id };

    static JsonObject User(string text, string? id = null) => Chunk("user_message_chunk", text, id);

    static JsonObject Agent(string text, string? id = null) => Chunk("agent_message_chunk", text, id);

    static string[] Planned(HistoryCatchUp.Plan plan) => Texts(plan.Updates);

    [TestMethod]
    public void AnEmptyText_IsNeverAligned_ButTheOthersAre()
    {
        JsonNode[] journal = [User("a", "j1"), Agent("x", "j2"), User("", "j3"), Agent("y", "j4"), User("b", "j5"), Agent("z", "j6")];
        JsonObject[] replay = [User("a"), Agent("x"), User(""), Agent("y"), User("b"), Agent("z"), User("c"), Agent("w")];

        var plan = HistoryCatchUp.Make(journal, replay, HistoryCatchUp.Replayed.All, "Kimi Code");

        Assert.AreEqual(HistoryCatchUp.Kind.Append, plan.Kind);
        CollectionAssert.AreEqual(new[] { "user: c", "agent: w" }, Planned(plan));
        Assert.AreEqual(HistoryCatchUp.Kind.Replace,
            HistoryCatchUp.Make([User("", "j1"), Agent("x", "j2")], [User(""), Agent("x"), User("next"), Agent("y")], HistoryCatchUp.Replayed.All, "Kimi Code").Kind,
            "only an empty text: nothing to align by");
    }

    [TestMethod]
    public void AJournalThatEndsWithAnUnansweredPrompt_GetsItsAnswer()
    {
        JsonNode[] journal = [User("a", "j1"), Agent("x", "j2"), User("b", "j3")]; // the service stopped before the answer came
        JsonObject[] replay = [User("a"), Agent("x"), User("b"), Agent("y"), User("c"), Agent("z")];

        var plan = HistoryCatchUp.Make(journal, replay, HistoryCatchUp.Replayed.All, "Kimi Code");

        CollectionAssert.AreEqual(new[] { "agent: y", "user: c", "agent: z" }, Planned(plan));
    }

    [TestMethod]
    public void ATurnTakenAgainWithTheSamePrompt_IsNotTakenForTheJournalsOwn()
    {
        // An agent that gives IDs: the last prompt's text is there again, but answered by other messages.
        JsonNode[] journal = [User("a", "u1"), Agent("x", "m1"), User("b", "u2"), Agent("y", "m2")];
        JsonObject[] replay = [User("a", "u1"), Agent("x", "m1"), User("b", "u9"), Agent("other", "m9")];

        Assert.AreEqual(HistoryCatchUp.Kind.Replace, HistoryCatchUp.Make(journal, replay, HistoryCatchUp.Replayed.All, "Claude Code").Kind);
    }

    [TestMethod]
    public void AToolCallTheJournalOnlyStarted_GetsTheUpdateThatEndsIt()
    {
        JsonNode[] journal =
        [
            new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t1", ["status"] = "in_progress", ["title"] = "ls" },
            new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t2", ["status"] = "in_progress", ["title"] = "pwd" },
            new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t2", ["status"] = "completed" },
        ];
        JsonObject[] replay =
        [
            new() { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t1", ["status"] = "completed" },
            new() { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t2", ["status"] = "completed", ["title"] = "pwd" },
        ];

        var plan = HistoryCatchUp.Make(journal, replay, HistoryCatchUp.Replayed.AfterMark, "Codex");

        Assert.AreEqual("t1", (string?)plan.Updates.Single()["toolCallId"], "t1's end is new; t2 the journal has done already");
    }

    [TestMethod]
    public void OnlyASystemReminderTheAgentPutFirst_IsCutOff()
    {
        JsonObject[] replay =
        [
            User("<system-reminder>\nPlan mode is the exception.\n</system-reminder>", "r1"),
            User("<system-reminder>a note</system-reminder>\nThe real question", "r2"),
            User("Explain the <system-reminder> tag in this file", "r3"),
        ];

        var plan = HistoryCatchUp.Make([User("a", "j1"), Agent("x", "j2")], replay, HistoryCatchUp.Replayed.AfterMark, "Kimi Code");

        CollectionAssert.AreEqual(new[] { "user: The real question", "user: Explain the <system-reminder> tag in this file" }, Planned(plan));
    }

    [TestMethod]
    public void NativeChunksOfOneMessage_AreMergedWhenTheJournalCompacts()
    {
        var journal = new SessionJournal(Path.Combine(TestDirs.New(), SessionJournal.FileNameFor("s1")), 16);
        for (var i = 0; i < 17; i++) // one past its limit: it compacts
        {
            var chunk = Agent($"part{i} ", "msg_1");
            chunk["_meta"] = new JsonObject { [GorynychProtocol.MetaOrigin] = "native" };
            journal.Append(chunk);
        }

        var merged = journal.ReadAll().Single();

        StringAssert.StartsWith((string?)merged["content"]?["text"], "part0 part1 ");
        Assert.AreEqual("native", (string?)merged["_meta"]?[GorynychProtocol.MetaOrigin]);
    }

    [TestMethod]
    public void TheMark_IsKeptNextToTheJournal_WrittenWithAReplacement_AndGoesWithIt()
    {
        var dir = TestDirs.New();
        var path = Path.Combine(dir, SessionJournal.FileNameFor("s1"));
        var journal = new SessionJournal(path, 100);
        journal.Append(User("hi", "j1"));
        journal.NativeMark = "turn-7";

        Assert.AreEqual("turn-7", new SessionJournal(path, 100).NativeMark, "read again after a restart");
        var at = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++) journal.Replace([Agent($"v{i}", $"m{i}")], at, mark: $"turn-{10 + i}", trimmed: false); // the same millisecond
        Assert.AreEqual("turn-14", new SessionJournal(path, 100).NativeMark, "written with the replacement");
        Assert.AreEqual(SessionJournal.KeptHistoryCount, journal.KeptHistories().Count, "the last three earlier journals, none overwritten");
        StringAssert.Contains(File.ReadAllText(journal.KeptHistories()[^1]), "v3");

        journal.Delete();
        Assert.IsNull(new SessionJournal(path, 100).NativeMark);
        Assert.AreEqual(0, journal.KeptHistories().Count);
    }
}
