using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Read-only sessions (docs/service-protocol.md, "Read-only sessions"), first source a Codex thread
/// another Codex app holds: it opens with its history read without taking it, the flag reaches the
/// client in the list, the open's answer and updates, a client that shows notices is told, a
/// prompt is refused while it is held and runs once it is free. Against a fake app-server, with
/// the thread's holder set by the test and the adapter's lock watcher on a folder of its own.
/// </summary>
[TestClass]
public sealed class ReadOnlySessionsTests
{
    static readonly string Cwd = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    const string Thread = "t-desk";
    const string Reason = "This Codex thread is open in Codex Desktop. Close it there to write here.";

    /// <summary>A Codex thread made in Codex Desktop, the service with the Codex adapter over a fake app-server, and who holds the thread.</summary>
    sealed class Rig : IAsyncDisposable
    {
        public readonly FakeAppServer Fake = new();
        public readonly string Data = TestDirs.New();
        public readonly string Locks = TestDirs.New();
        readonly Dictionary<string, string> held = new(StringComparer.Ordinal);
        public Conductor Service;

        readonly IAgentAdapter[] more;

        readonly TimeSpan lockRecheck;

        public Rig(bool heldAtStart = true, params IAgentAdapter[] more) : this(heldAtStart, TimeSpan.FromMilliseconds(200), more) { }

        /// <param name="lockRecheck">How often the adapter's lock watcher looks again at the threads it saw held.</param>
        public Rig(bool heldAtStart, TimeSpan lockRecheck, params IAgentAdapter[] more)
        {
            this.more = more;
            this.lockRecheck = lockRecheck;
            Fake.Existing(Thread, Cwd, name: "Open in Codex Desktop");
            if (heldAtStart) Hold();
            Service = NewService();
        }

        public Conductor NewService() => new(new ConductorOptions
        {
            DataDirectory = Data,
            ServiceId = "t",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        }, [new CodexAdapter(new CodexAdapterOptions
        {
            Launch = Fake.StartAsync,
            ClientVersion = "test",
            TurnEndWait = TimeSpan.FromMilliseconds(200),
            FindHolders = ids =>
            {
                lock (held) return ids.Where(held.ContainsKey).ToDictionary(id => id, id => held[id]);
            },
            LocksDirectory = Locks,
            LockSettle = TimeSpan.FromMilliseconds(50),
            LockRecheck = lockRecheck,
        }), .. more]);

        /// <summary>Codex Desktop takes the thread: app-server refuses it to anyone else, and its lock file appears.</summary>
        public void Hold(string id = Thread)
        {
            lock (held) held[id] = "Codex Desktop";
            if (Fake.Threads.TryGetValue(id, out var t)) t.OpenElsewhere = true;
            File.WriteAllText(Path.Combine(Locks, id + ".lock"), "");
        }

        /// <summary>Codex Desktop lets the thread go.</summary>
        public void LetGo(string id = Thread)
        {
            LetGoUnseen(id);
            File.Delete(Path.Combine(Locks, id + ".lock"));
        }

        /// <summary>Codex Desktop lets the thread go, and its lock file stays (it went without deleting it): the watcher sees nothing until it looks again.</summary>
        public void LetGoUnseen(string id = Thread)
        {
            lock (held) held.Remove(id);
            if (Fake.Threads.TryGetValue(id, out var t)) t.OpenElsewhere = false;
        }

        public Task<TestClient> ZedAsync(bool notices = true, string name = "zed") => TestClient.ConnectAsync(Service, name, agent: "codex", notices: notices);

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    static JsonObject Resume(string id = Thread) => new() { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() };

    static async Task<JsonObject> MetaOfAsync(TestClient zed, string method) =>
        (JsonObject)(await zed.Rpc.SendRequestAsync(method, Resume()).WaitAsync(Wait))!["_meta"]!;

    static string? Interactivity(JsonNode? meta) => (string?)meta?[GorynychProtocol.MetaInteractivity];

    static JsonObject[] Notices(TestClient zed) => zed.Snapshot().Where(u => (string?)u["sessionUpdate"] == "notice").ToArray();

    /// <summary>The interactivity updates the client got, in order: the value each said.</summary>
    static string?[] FlagUpdates(TestClient zed) => zed.Snapshot()
        .Where(u => (string?)u["sessionUpdate"] == "session_info_update" && u["_meta"]?[GorynychProtocol.MetaInteractivity] is not null)
        .Select(u => Interactivity(u["_meta"])).ToArray();

    static Task<PromptResponse> PromptAsync(TestClient zed, string text, string id = Thread) =>
        zed.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = text }] });

    static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHeldThreadTheJournalNeverSaw_OpensReadOnly_WithItsHistoryReadWithoutTakingIt()
    {
        await using var rig = new Rig();
        await using var zed = await rig.ZedAsync();

        var meta = await MetaOfAsync(zed, AcpMethods.Agent.SessionLoad);

        Assert.AreEqual(GorynychProtocol.InteractivityReadOnly, Interactivity(meta));
        Assert.AreEqual(Reason, (string?)meta[GorynychProtocol.MetaInteractivityReason]);
        Assert.AreEqual("Codex Desktop", (string?)meta[GorynychProtocol.MetaOpenElsewhere]);
        StringAssert.Contains(zed.Text(), "Fixed it.", "its history, read from Codex");
        Assert.AreEqual(0, rig.Fake.All("thread/resume").Length, "the thread was not taken");
        Assert.IsTrue(rig.Fake.All("thread/turns/list").Length > 0, "it was read with thread/turns/list");
        var notice = Notices(zed).Single();
        Assert.AreEqual(("warning", Reason), ((string?)notice["severity"], (string?)notice["description"]));
        StringAssert.Contains(HostTests.JournalText(rig.Service, Thread), "Fixed it.", "the journal has the history now");
        Assert.IsFalse(HostTests.Journal(rig.Service, Thread).Any(u => (string?)u["sessionUpdate"] == "notice"), "no notice is journaled");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHeldThreadWithAJournal_OpensReadOnly_AndCatchesItsJournalUpByReading()
    {
        // Gorynych had it open before, while it was free; then Codex Desktop took it and ran a turn.
        await using var rig = new Rig(heldAtStart: false);
        await using (var first = await rig.ZedAsync())
            await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);
        await rig.Service.DisposeAsync();
        rig.Hold();
        rig.Fake.AddTurn(Thread, "Now in Desktop", "Done in Desktop.");
        var resumesBefore = rig.Fake.All("thread/resume").Length;

        rig.Service = rig.NewService();
        await using var zed = await rig.ZedAsync();
        var meta = await MetaOfAsync(zed, AcpMethods.Agent.SessionResume);

        Assert.AreEqual(GorynychProtocol.InteractivityReadOnly, Interactivity(meta));
        Assert.AreEqual(resumesBefore, rig.Fake.All("thread/resume").Length, "not taken");
        var journal = HostTests.JournalText(rig.Service, Thread);
        Assert.AreEqual(1, journal.Split("Fixed it.").Length - 1, "the turn it had once, no double: " + journal);
        Assert.AreEqual(1, journal.Split("Done in Desktop.").Length - 1, "and the one made meanwhile, read without taking the thread");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheFlag_IsInTheList_InTheLoadAndResumeAnswers_AndInAnUpdateEachTimeItChanges()
    {
        await using var rig = new Rig();
        await using var zed = await rig.ZedAsync();
        var row = (await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest())).Sessions.Single();
        Assert.AreEqual((GorynychProtocol.InteractivityReadOnly, Reason), (Interactivity(row.Meta), (string?)row.Meta?[GorynychProtocol.MetaInteractivityReason]));
        Assert.AreEqual(GorynychProtocol.InteractivityReadOnly, Interactivity(await MetaOfAsync(zed, AcpMethods.Agent.SessionResume)));

        // Codex Desktop lets go: the flag clears on the fly, said as "full", and the list follows.
        rig.LetGo();
        await Until(() => FlagUpdates(zed).LastOrDefault() == GorynychProtocol.InteractivityFull, "the flag to clear");
        row = (await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest())).Sessions.Single();
        Assert.IsNull(Interactivity(row.Meta), "absent: full");
        Assert.AreEqual(GorynychProtocol.InteractivityFull, Interactivity(await MetaOfAsync(zed, AcpMethods.Agent.SessionLoad)), "an open says it in full");

        // It takes the thread again before Gorynych did: read-only once more.
        rig.Hold();
        await Until(() => FlagUpdates(zed).LastOrDefault() == GorynychProtocol.InteractivityReadOnly, "the flag to come back");
        var last = zed.Snapshot().Last(u => u["_meta"]?[GorynychProtocol.MetaInteractivity] is not null)["_meta"]!;
        Assert.AreEqual((Reason, "Codex Desktop"), ((string?)last[GorynychProtocol.MetaInteractivityReason], (string?)last[GorynychProtocol.MetaOpenElsewhere]));
        CollectionAssert.AreEqual(new[] { "warning", "info", "warning" }, Notices(zed).Select(n => (string?)n["severity"]).ToArray(),
            "one when it opened read-only, one when it was free, one when it was taken again; none for the open while it was free");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ANotice_GoesOnlyToAClientThatShowsNotices()
    {
        await using var rig = new Rig();
        await using var shows = await rig.ZedAsync(notices: true, name: "shows");
        await using var plain = await rig.ZedAsync(notices: false, name: "plain");
        await shows.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);
        await plain.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);

        rig.LetGo();
        await Until(() => FlagUpdates(plain).LastOrDefault() == GorynychProtocol.InteractivityFull, "the plain client's update");
        await Until(() => Notices(shows).Length == 2, "both notices to the client that shows them");
        Assert.AreEqual(0, Notices(plain).Length, "none to one that did not say it shows them");
        CollectionAssert.AreEqual(new[] { "warning", "info" }, Notices(shows).Select(n => (string?)n["severity"]).ToArray());
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APrompt_WhileTheThreadIsHeld_IsRefusedInWords_AndOnceItIsFree_TakesItAndRuns()
    {
        await using var rig = new Rig();
        await using var zed = await rig.ZedAsync();
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(zed, "hello").WaitAsync(Wait));
        StringAssert.Contains(refused.Message, Thread, "the session's ID");
        StringAssert.Contains(refused.Message, Reason);
        StringAssert.Contains(refused.Message, "send the prompt again");
        Assert.IsNull(refused.ErrorData, "no data, which Zed would show as raw JSON: the flag is in the session's state");
        Assert.AreEqual(0, rig.Fake.All("turn/start").Length, "no turn started");

        // Desktop runs a turn, then lets go; the next prompt takes the thread, with the turn made meanwhile.
        rig.Fake.AddTurn(Thread, "Desktop's question", "Desktop's answer.");
        rig.LetGo();
        await Until(() => FlagUpdates(zed).LastOrDefault() == GorynychProtocol.InteractivityFull, "the flag to clear");
        Assert.AreEqual(0, rig.Fake.All("thread/resume").Length, "free is not taken: only a prompt takes it");
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(zed, "hello").WaitAsync(Wait)).StopReason);
        Assert.AreEqual(1, rig.Fake.All("thread/resume").Length, "taken for the turn");
        Assert.AreEqual(1, rig.Fake.All("turn/start").Length);
        var journal = HostTests.JournalText(rig.Service, Thread);
        Assert.AreEqual(1, journal.Split("Desktop's answer.").Length - 1, "the turn made in Desktop meanwhile reached the journal once: " + journal);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APrompt_ToAThreadFreedBeforeTheWatcherSawIt_TakesIt_AndClearsTheFlag()
    {
        // The flag still says read-only; the prompt opens the session again, which finds it free.
        await using var rig = new Rig(heldAtStart: true, lockRecheck: TimeSpan.FromHours(1));
        await using var zed = await rig.ZedAsync();
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);
        rig.LetGoUnseen();

        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(zed, "hello").WaitAsync(Wait)).StopReason);
        CollectionAssert.AreEqual(new[] { GorynychProtocol.InteractivityFull }, FlagUpdates(zed), "the open in full cleared it");
        Assert.AreEqual(GorynychProtocol.InteractivityFull, Interactivity(await MetaOfAsync(zed, AcpMethods.Agent.SessionResume)));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APrompt_ToAThreadTakenAgainBeforeItWasWritten_IsRefusedAgain()
    {
        await using var rig = new Rig();
        await using var zed = await rig.ZedAsync();
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);
        rig.LetGo();
        await Until(() => FlagUpdates(zed).LastOrDefault() == GorynychProtocol.InteractivityFull, "the flag to clear");
        rig.Fake.Threads[Thread].OpenElsewhere = true; // Desktop took it again, its lock file not seen yet

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(zed, "hello").WaitAsync(Wait));
        StringAssert.Contains(refused.Message, "read-only");
        Assert.AreEqual(GorynychProtocol.InteractivityReadOnly, FlagUpdates(zed).Last(), "the client learns it is read-only again");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AQuestionFromAnotherSession_ToAReadOnlySession_IsRefusedInWordsForTheAskingAgent()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var rig = new Rig(heldAtStart: true, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(rig.Service, "zed");
        var asker = (await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" },
        })).SessionId;
        await PromptAsync(zed, "Coordinate", asker).WaitAsync(Wait);

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = Thread, ["text"] = "Is it done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Tokens[asker] },
        }).WaitAsync(Wait));

        // As before for a session another app holds: at once, with its ID and what the agent can do.
        StringAssert.Contains(refused.Message, Thread);
        StringAssert.Contains(refused.Message, "is open in Codex Desktop, which holds it, so it cannot be asked until it is closed there.");
        StringAssert.Contains(refused.Message, "Ask another session from list_agents, or ask the user to close it in Codex Desktop.");
        Assert.AreEqual(0, rig.Fake.All("turn/start").Length);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheFirstList_CarriesTheMarks_InCodexsOwnOrder()
    {
        await using var rig = new Rig();
        rig.Fake.Threads[Thread].UpdatedAt = 1_794_000_300;
        rig.Fake.Existing("t-old", Cwd, name: "Older").UpdatedAt = 1_794_000_100;
        rig.Fake.Existing("t-mid", Cwd, name: "Middle").UpdatedAt = 1_794_000_200;
        await using var zed = await rig.ZedAsync();

        // Gorynych's own activity on the oldest (its journal catches up, a turn streams) moves no row:
        // Codex's own time orders the list, and Codex moves it when a turn of its ends.
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("t-old")).WaitAsync(Wait);
        await PromptAsync(zed, "hi", "t-old").WaitAsync(Wait);
        var rows = (await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest())).Sessions;

        CollectionAssert.AreEqual(new[] { Thread, "t-mid", "t-old" }, rows.Select(r => r.SessionId).ToArray(), "Codex's own order");
        Assert.AreEqual(GorynychProtocol.InteractivityReadOnly, Interactivity(rows[0].Meta), "marked in the first list");
        Assert.IsNull(Interactivity(rows[1].Meta));
    }
}
