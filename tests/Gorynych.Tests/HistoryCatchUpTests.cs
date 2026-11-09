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
    public async Task ASessionTheJournalNeverSaw_IsImportedFromTheAgent()
    {
        var codex = new LifecycleAgent("codex", canDelete: true, existing: ["made-in-desktop"]) { KeepsHistory = true };
        var last = codex.AddNativeTurn("made-in-desktop", "Hello", "Hi");
        await using var conductor = Conductor(codex.Adapter);

        var (texts, updates) = await SeenAsync(conductor, "made-in-desktop");

        CollectionAssert.AreEqual(new[] { "user: Hello", "agent: Hi" }, texts);
        Assert.IsTrue(updates.All(u => Origin(u) == "native"));
        Assert.AreEqual(last, conductor.Feed("made-in-desktop").Journal.NativeMark);
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
        codex.AddNativeTurn(id, "Instead", "Another answer");

        var (texts, updates) = await SeenAsync(conductor, id);

        StringAssert.StartsWith(texts[0], "user: [Gorynych: codex's own history of this session no longer holds");
        Assert.AreEqual("notice", Origin(updates[0]));
        Assert.AreEqual("historyRewritten", (string?)updates[0]["_meta"]?[GorynychProtocol.MetaNotice]);
        CollectionAssert.AreEqual(new[] { "user: First", "agent: ok", "user: Instead", "agent: Another answer" }, texts[1..]);
        Assert.AreEqual(firstId, (string?)updates.Single(u => (string?)u["content"]?["text"] == "First")["messageId"], "a message that is still there keeps its ID");
        var kept = conductor.Feed(id).Journal.KeptHistories();
        Assert.AreEqual(1, kept.Count);
        StringAssert.Contains(File.ReadAllText(kept[0]), "Second", "the earlier journal is kept");
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
    public void TheMark_IsKeptNextToTheJournal_AndGoesWithIt()
    {
        var dir = TestDirs.New();
        var path = Path.Combine(dir, SessionJournal.FileNameFor("s1"));
        var journal = new SessionJournal(path, 100);
        journal.Append(new JsonObject { ["sessionUpdate"] = "user_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "hi" } });
        journal.NativeMark = "turn-7";

        Assert.AreEqual("turn-7", new SessionJournal(path, 100).NativeMark, "read again after a restart");
        for (var i = 0; i < 5; i++)
            journal.Replace([new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = $"v{i}" } }],
                new DateTimeOffset(2026, 11, 9, 12, 0, i, TimeSpan.Zero));
        Assert.AreEqual(SessionJournal.KeptHistoryCount, journal.KeptHistories().Count, "the last three earlier journals");
        StringAssert.Contains(File.ReadAllText(journal.KeptHistories()[^1]), "v3");

        journal.Delete();
        Assert.IsNull(new SessionJournal(path, 100).NativeMark);
        Assert.AreEqual(0, journal.KeptHistories().Count);
    }
}
