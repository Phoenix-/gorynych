using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Drafts, deletion and the peer list, as an external client like Zed drives them: it creates
/// a session whenever a thread view opens, closes sessions when it drops a thread, quits
/// without closing anything, and deletes from its history only when the agent advertises it.
/// A native delete cannot be undone, so whenever the service is unsure it keeps the session.
/// </summary>
[TestClass]
public sealed class SessionLifecycleTests
{
    static readonly string Cwd = Path.GetTempPath();
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static async Task<string> NewAsync(TestClient client) =>
        (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Cwd, McpServers = [] })).SessionId;

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static Task ResumeAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, AcpJson.ToNode(new ResumeSessionRequest { SessionId = sessionId, Cwd = Cwd }));

    static Task CloseAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId });

    static Task DeleteAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = sessionId });

    static async Task<string[]> ListAsync(TestClient client) =>
        (await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest()))
            .Sessions.Select(s => s.SessionId).ToArray();

    static async Task<string[]> PeersAsync(TestClient client) =>
        (await client.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject()))!["sessions"]!.AsArray()
            .Select(p => (string)p!["session_id"]!).ToArray();

    /// <summary>Disconnects a client and waits for the drafts it leaves to be dealt with.</summary>
    static async Task LeaveAsync(Conductor conductor, TestClient client)
    {
        await client.DisposeAsync();
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
    }

    // ---------------------------------------------------------------- drafts

    [TestMethod]
    public async Task Draft_IsDeletedNatively_WhenItsOnlyClientLeaves()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var draft = await NewAsync(zed);
        Assert.IsTrue(conductor.Store.Get(draft)!.Draft);
        Assert.AreEqual(0, (await ListAsync(zed)).Length, "a draft is not listed, though its agent lists it");

        await LeaveAsync(conductor, zed);

        CollectionAssert.AreEqual(new[] { "session/new", "session/close", "session/delete" }, kimi.CallsFor(draft));
        Assert.IsNull(conductor.Store.Get(draft));
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(dir, "journal")).Length, "the draft's journal is gone");
    }

    [TestMethod]
    public async Task Close_DiscardsADraft_AndOnlyClosesAPromptedSession()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.UnloadingAtOnce(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var draft = await NewAsync(zed);
        var used = await NewAsync(zed);
        await PromptAsync(zed, used, "hello");

        await CloseAsync(zed, draft);
        await CloseAsync(zed, used);

        CollectionAssert.AreEqual(new[] { "session/new", "session/close", "session/delete" }, kimi.CallsFor(draft));
        CollectionAssert.AreEqual(new[] { "session/new", "session/prompt", "session/close" }, kimi.CallsFor(used));
        Assert.IsNull(conductor.Store.Get(draft));
        Assert.IsFalse(conductor.Store.Get(used)!.Draft);
        CollectionAssert.AreEqual(new[] { used }, await ListAsync(zed));
    }

    [TestMethod]
    public async Task Close_LeavesTheSessionOpenWhileAnotherClientHasIt()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.UnloadingAtOnce(TestDirs.New(), kimi.Adapter);
        await using var first = await TestClient.ConnectAsync(conductor, "first");
        await using var second = await TestClient.ConnectAsync(conductor, "second");
        var id = await NewAsync(first);
        await PromptAsync(first, id, "hello");
        await ResumeAsync(second, id);

        await CloseAsync(first, id);
        CollectionAssert.DoesNotContain(kimi.CallsFor(id), "session/close", "the second client still has it open");
        await CloseAsync(second, id);
        CollectionAssert.Contains(kimi.CallsFor(id), "session/close");
    }

    [TestMethod]
    public async Task Draft_StaysWhileAnotherClientHasItOpen()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        var first = await TestClient.ConnectAsync(conductor, "first");
        var draft = await NewAsync(first);
        var second = await TestClient.ConnectAsync(conductor, "second");
        await ResumeAsync(second, draft);

        await LeaveAsync(conductor, first);
        Assert.IsNotNull(conductor.Store.Get(draft), "the second client still has it open");
        await LeaveAsync(conductor, second);
        Assert.IsNull(conductor.Store.Get(draft));
        CollectionAssert.Contains(kimi.CallsFor(draft), "session/delete");
    }

    [TestMethod]
    public async Task Draft_OfAnAgentThatCannotDelete_IsClosedAndDropped()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), claude.Adapter);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var draft = await NewAsync(zed);

        await LeaveAsync(conductor, zed);

        CollectionAssert.AreEqual(new[] { "session/new", "session/close" }, claude.CallsFor(draft));
        Assert.IsNull(conductor.Store.Get(draft), "no tombstone: a draft never reached the harness's own list");
    }

    [TestMethod]
    public async Task ADiscardUnderWay_MakesAResumeWaitForItsOutcome()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var deleting = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        kimi.BeforeDelete = async _ => { deleting.TrySetResult(); await release.Task; };
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var draft = await NewAsync(zed);
        await zed.DisposeAsync();
        await deleting.Task.WaitAsync(Wait); // the discard is in the agent now

        await using var again = await TestClient.ConnectAsync(conductor, "zed-again");
        var resume = ResumeAsync(again, draft);
        await Task.Delay(200);
        Assert.IsFalse(resume.IsCompleted, "the resume waits for the discard");
        release.SetResult();

        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => resume.WaitAsync(Wait));
        Assert.AreEqual(JsonRpcException.ResourceNotFound, error.Code);
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
        CollectionAssert.AreEqual(new[] { "session/new", "session/close", "session/delete" }, kimi.CallsFor(draft), "nothing reached the agent after the delete");
    }

    [TestMethod]
    public async Task ADiscard_SkipsADraftThatAnOperationIsUsing()
    {
        var dir = TestDirs.New();
        new SessionStore(Path.Combine(dir, "sessions.json")).Update("kimi-old", "kimi", r => { r.Cwd = Cwd; r.Draft = true; });
        var kimi = new LifecycleAgent("kimi", canDelete: true, existing: ["kimi-old"]);
        var resuming = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        kimi.BeforeResume = async _ => { resuming.TrySetResult(); await release.Task; };
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var resume = ResumeAsync(zed, "kimi-old");
        await resuming.Task.WaitAsync(Wait);

        await conductor.SweepDraftsAsync(); // a client reached the draft first

        CollectionAssert.DoesNotContain(kimi.CallsFor("kimi-old"), "session/delete");
        release.SetResult();
        await resume.WaitAsync(Wait);
        Assert.IsNotNull(conductor.Store.Get("kimi-old"));
        await LeaveAsync(conductor, zed);
        Assert.IsNull(conductor.Store.Get("kimi-old"), "the draft goes with the client that had it open");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "fails a write by holding the file open, which only Windows refuses")]
    public async Task ADraftMarkThatCannotBeSaved_RefusesThePrompt_AndKeepsTheSession()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var draft = await NewAsync(zed);

        JsonRpcException error;
        // An antivirus scanner holds the sessions file through every retry.
        using (new FileStream(Path.Combine(dir, "sessions.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(zed, draft, "hello"));

        Assert.AreEqual(JsonRpcException.InternalError, error.Code);
        CollectionAssert.DoesNotContain(kimi.CallsFor(draft), "session/prompt", "nothing reached the agent");
        Assert.IsFalse(conductor.Store.Get(draft)!.Draft, "this run keeps it");
        await LeaveAsync(conductor, zed);
        Assert.IsNotNull(conductor.Store.Get(draft));
        CollectionAssert.DoesNotContain(kimi.CallsFor(draft), "session/delete");
    }

    [TestMethod]
    public async Task TheSweep_DeletesOnlyDraftsShownToBeEmpty()
    {
        var dir = TestDirs.New();
        var earlier = new SessionStore(Path.Combine(dir, "sessions.json"));
        foreach (var id in new[] { "kimi-with-turn", "kimi-titled", "kimi-unlisted", "kimi-empty" })
            earlier.Update(id, "kimi", r => { r.Cwd = Cwd; r.Draft = true; });
        earlier.Update("kimi-used", "kimi", r => { r.Cwd = Cwd; r.Title = "Used"; });
        // The mark says draft, but the journal holds a turn.
        Directory.CreateDirectory(Path.Combine(dir, "journal"));
        new SessionJournal(Path.Combine(dir, "journal", SessionJournal.FileNameFor("kimi-with-turn")), 100)
            .Append(AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk { Content = new TextContent { Text = "hi" } })!);
        var kimi = new LifecycleAgent("kimi", canDelete: true, existing: ["kimi-with-turn", "kimi-titled", "kimi-empty", "kimi-used"],
            titles: new() { ["kimi-titled"] = "Fix the build" });
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);

        await conductor.SweepDraftsAsync();

        CollectionAssert.AreEqual(new[] { "session/delete" }, kimi.CallsFor("kimi-empty"), "listed without a title: empty, deleted");
        foreach (var kept in new[] { "kimi-with-turn", "kimi-titled", "kimi-unlisted", "kimi-used" })
            CollectionAssert.DoesNotContain(kimi.CallsFor(kept), "session/delete", kept);
        Assert.IsNull(conductor.Store.Get("kimi-empty"));
        Assert.IsFalse(conductor.Store.Get("kimi-with-turn")!.Draft, "a turn in the journal: kept");
        Assert.IsFalse(conductor.Store.Get("kimi-titled")!.Draft, "the agent titled it: kept");
        Assert.IsTrue(conductor.Store.Get("kimi-unlisted")!.Deleted, "not listed, so not shown to be empty: hidden, not deleted");
        Assert.IsFalse(conductor.Store.Get("kimi-used")!.Draft);
    }

    [TestMethod]
    public async Task DraftsLeftByAnEarlierRun_AreSweptAtStart()
    {
        var dir = TestDirs.New();
        var earlier = new SessionStore(Path.Combine(dir, "sessions.json"));
        earlier.Update("kimi-old-draft", "kimi", r => { r.Cwd = Cwd; r.Draft = true; });
        earlier.Update("kimi-old-used", "kimi", r => { r.Cwd = Cwd; r.Title = "Used"; });
        var kimi = new LifecycleAgent("kimi", canDelete: true, existing: ["kimi-old-draft", "kimi-old-used"]);
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);

        await conductor.SweepDraftsAsync();

        CollectionAssert.AreEqual(new[] { "session/delete" }, kimi.CallsFor("kimi-old-draft"));
        Assert.AreEqual(0, kimi.CallsFor("kimi-old-used").Length);
        Assert.IsNull(conductor.Store.Get("kimi-old-draft"));
        Assert.IsNotNull(conductor.Store.Get("kimi-old-used"));
    }

    [TestMethod]
    public async Task ADraftThatAnsweredThroughItsChannel_IsKept()
    {
        var answerer = new ScriptedAdapter("claude", async (agent, req, ct) =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init();
                case AcpMethods.Agent.SessionNew: return AcpJson.ToNode(new NewSessionResponse { SessionId = "answering" });
                case AcpMethods.Agent.SessionList: return AcpJson.ToNode(new ListSessionsResponse { Sessions = [] });
                case GorynychProtocol.MethodOpinion:
                    // As Claude's hooks report a channel message and the answer.
                    await agent.Update("answering", new SessionUpdateUserMessageChunk { Content = new TextContent { Text = "<channel>SQLite?</channel>" } });
                    await agent.Update("answering", new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "SQLite." } });
                    return new JsonObject { ["text"] = "SQLite." };
                default: throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        });
        await using var conductor = TestDirs.Conductor(TestDirs.New(), answerer);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var draft = await NewAsync(zed);

        var reply = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject { ["sessionId"] = draft, ["text"] = "SQLite?" });

        Assert.AreEqual("SQLite.", (string?)reply!["text"]);
        await LeaveAsync(conductor, zed);
        Assert.IsFalse(conductor.Store.Get(draft)!.Draft);
        await using var other = await TestClient.ConnectAsync(conductor, "other");
        CollectionAssert.AreEqual(new[] { draft }, await PeersAsync(other), "it has history, so other agents can ask it");
    }

    // ---------------------------------------------------------------- session/delete

    [TestMethod]
    public async Task Delete_ClosesTheSession_ThenDeletesItInAnAgentThatCan()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var init = await zed.Rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
        Assert.IsNotNull(init.AgentCapabilities!.SessionCapabilities!.Delete, "Zed sends session/delete only when it is advertised");
        var id = await NewAsync(zed);
        await PromptAsync(zed, id, "hello");

        await DeleteAsync(zed, id);

        CollectionAssert.AreEqual(new[] { "session/new", "session/prompt", "session/close", "session/delete" }, kimi.CallsFor(id));
        Assert.IsNull(conductor.Store.Get(id));
        Assert.AreEqual(0, (await ListAsync(zed)).Length);
        var load = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
            AcpJson.ToNode(new LoadSessionRequest { SessionId = id, Cwd = Cwd, McpServers = [] })));
        Assert.AreEqual(JsonRpcException.ResourceNotFound, load.Code);
        await DeleteAsync(zed, id);        // already deleted
        await DeleteAsync(zed, "unknown"); // never existed
    }

    [TestMethod]
    public async Task Delete_ReachesASessionOnlyTheAgentKnows()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true, existing: ["kimi-native"], titles: new() { ["kimi-native"] = "Old work" });
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");

        await DeleteAsync(zed, "kimi-native");

        CollectionAssert.AreEqual(new[] { "session/delete" }, kimi.CallsFor("kimi-native"));
        Assert.AreEqual(0, (await ListAsync(zed)).Length);
    }

    [TestMethod]
    public async Task Delete_HidesASessionItsAgentKeeps_AcrossARestart()
    {
        var dir = TestDirs.New();
        var claude = new LifecycleAgent("claude", canDelete: false);
        string id;
        await using (var conductor = TestDirs.Conductor(dir, claude.Adapter))
        await using (var zed = await TestClient.ConnectAsync(conductor, "zed"))
        {
            id = await NewAsync(zed);
            await PromptAsync(zed, id, "keep my transcript");
            CollectionAssert.AreEqual(new[] { id }, await ListAsync(zed));

            await DeleteAsync(zed, id);

            CollectionAssert.AreEqual(new[] { "session/new", "session/prompt", "session/close" }, claude.CallsFor(id), "closed, never deleted in the harness");
            Assert.IsTrue(conductor.Store.Get(id)!.Deleted);
            Assert.AreEqual(0, (await ListAsync(zed)).Length, "hidden although the agent still lists it");
            Assert.AreEqual(0, (await PeersAsync(zed)).Length);
            var prompt = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(zed, id, "again"));
            Assert.AreEqual(JsonRpcException.ResourceNotFound, prompt.Code);
        }

        var restarted = new LifecycleAgent("claude", canDelete: false, existing: [id]);
        await using (var conductor = TestDirs.Conductor(dir, restarted.Adapter))
        await using (var zed = await TestClient.ConnectAsync(conductor, "zed"))
        {
            Assert.AreEqual(0, (await ListAsync(zed)).Length);
            var load = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
                AcpJson.ToNode(new LoadSessionRequest { SessionId = id, Cwd = Cwd, McpServers = [] })));
            Assert.AreEqual(JsonRpcException.ResourceNotFound, load.Code);
        }
    }

    [TestMethod]
    public async Task Delete_TakesTheAgentsNotFoundAsSuccess()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true, notFoundOnDelete: true);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewAsync(zed);
        await PromptAsync(zed, id, "hello");

        await DeleteAsync(zed, id);

        Assert.IsNull(conductor.Store.Get(id));
    }

    [TestMethod]
    public async Task Delete_OfASessionTheAgentAlreadyLost_Succeeds()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewAsync(zed);
        await PromptAsync(zed, id, "hello");
        kimi.LoseNatively(id); // deleted from Kimi's own CLI, say

        await DeleteAsync(zed, id); // Kimi answers -32602 "Unknown sessionId", and no longer lists it

        Assert.IsNull(conductor.Store.Get(id));
    }

    [TestMethod]
    public async Task Delete_LateUpdatesAndAQueuedPrompt_BringNothingBack()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        kimi.BeforePrompt = async _ => { started.TrySetResult(); await release.Task; };
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewAsync(zed);
        var first = PromptAsync(zed, id, "first");
        await started.Task.WaitAsync(Wait);
        var second = PromptAsync(zed, id, "second"); // waits for the first turn
        await Task.Delay(100);

        await DeleteAsync(zed, id);
        release.SetResult(); // the agent now streams the rest of the first turn and ends it
        await first.WaitAsync(Wait);
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => second.WaitAsync(Wait));
        Assert.AreEqual(JsonRpcException.ResourceNotFound, error.Code);
        await Task.Delay(100);

        CollectionAssert.AreEqual(new[] { "session/new", "session/prompt", "session/close", "session/delete" }, kimi.CallsFor(id));
        Assert.IsNull(conductor.Store.Get(id));
        Assert.IsNull(new SessionStore(Path.Combine(dir, "sessions.json")).Get(id));
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(dir, "journal")).Length, "no journal came back");
        Assert.AreEqual(0, (await ListAsync(zed)).Length);
        Assert.AreEqual(0, (await PeersAsync(zed)).Length);
    }

    // ---------------------------------------------------------------- list_agents

    [TestMethod]
    public async Task Peers_ListOnlySessionsWithHistory_OpenOnesFirst()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.UnloadingAtOnce(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var older = await NewAsync(zed);
        await PromptAsync(zed, older, "older, closed");
        await Task.Delay(30);
        var open = await NewAsync(zed);
        await PromptAsync(zed, open, "open");
        await Task.Delay(30);
        var newer = await NewAsync(zed);
        await PromptAsync(zed, newer, "newer, closed");
        var deleted = await NewAsync(zed);
        await PromptAsync(zed, deleted, "deleted");
        await NewAsync(zed); // a draft
        await CloseAsync(zed, older);
        await CloseAsync(zed, newer);
        await DeleteAsync(zed, deleted);

        var peers = (await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject()))!["sessions"]!.AsArray();

        CollectionAssert.AreEqual(new[] { open, newer, older }, peers.Select(p => (string)p!["session_id"]!).ToArray());
        CollectionAssert.AreEqual(new[] { "open", "saved", "saved" }, peers.Select(p => (string)p!["status"]!).ToArray());
        Assert.IsFalse(peers.Any(p => p!.AsObject().ContainsKey("open")), "status replaced the open flag");
    }

    // ---------------------------------------------------------------- the overlay file

    [TestMethod]
    public void Store_RetriesAWriteWhileTheFileIsHeld()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var store = new SessionStore(path);
        store.Update("a", "kimi", r => r.Title = "first");
        // An antivirus scanner or the indexer opens the file for a moment without delete sharing.
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Released by a thread of its own: a pool callback can come late in a busy parallel run,
        // after the store's retries (about 0.4 s) are over.
        var releaser = new Thread(() => { Thread.Sleep(120); held.Dispose(); }) { IsBackground = true };
        releaser.Start();

        store.Update("a", "kimi", r => r.Title = "second");

        releaser.Join();
        Assert.AreEqual("second", new SessionStore(path).Get("a")!.Title);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "fails a write by holding the file open, which only Windows refuses")]
    public void Store_KeepsAChangeItCouldNotWrite_AndWritesItWithTheNext()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var store = new SessionStore(path);
        store.Update("a", "kimi", r => r.Title = "first");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Update("a", "kimi", r => r.Title = "second"); // every retry fails: logged, not thrown
            Assert.ThrowsExactly<IOException>(() => store.Update("a", "kimi", r => r.Draft = false, durable: true));
        }
        Assert.AreEqual("first", new SessionStore(path).Get("a")!.Title);

        store.Update("b", "kimi", r => r.Title = "other");

        var reread = new SessionStore(path);
        Assert.AreEqual("second", reread.Get("a")!.Title);
        Assert.AreEqual("other", reread.Get("b")!.Title);
    }

    [TestMethod]
    public void Store_WritesActivityStampsAtMostOnceASecond_AndFlushesTheRest()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var store = new SessionStore(path);
        store.Update("a", "kimi", r => r.Title = "t");
        Thread.Sleep(20);
        store.Touch("a");
        var stamped = store.Get("a")!.UpdatedAt;
        Assert.AreNotEqual(stamped, new SessionStore(path).Get("a")!.UpdatedAt, "a stamp right after a write waits");

        store.Flush();

        Assert.AreEqual(stamped, new SessionStore(path).Get("a")!.UpdatedAt);
    }

    /// <summary>
    /// A stamp that cannot be written is not tried again by every stamp after it: the next try waits a
    /// second from when the failed one ended, however long it took (on CI one took 1.3 s, and the next
    /// stamp tried again at once). And a stamp makes one attempt, not AtomicFile's retries: its caller
    /// is an agent's update on its way in, which must not wait some 400 ms for a file held for long.
    /// </summary>
    [TestMethod]
    public void Store_BacksOffFromAFailedWrite_InsteadOfRetryingOnEveryStamp()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        long now = 0;
        List<bool> attempts = []; // whether each write retried
        // Each write moves the clock 1.5 s on, longer than a stamp's interval: a slow machine's write.
        var store = new SessionStore(path)
        {
            Ticks = () => now,
            WriteFile = (file, json, retry) =>
            {
                attempts.Add(retry);
                now += 1500;
                AtomicFile.WriteAllText(file, json, retry: retry);
            },
        };
        store.Update("a", "kimi", r => r.Title = "t");
        now += 1100;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Touch("a"); // tries once, and fails
            CollectionAssert.AreEqual(new[] { true, false }, attempts, "an update retries; a stamp tries once");

            for (var i = 0; i < 20; i++) store.Touch("a");
            Assert.AreEqual(2, attempts.Count, "no stamp within a second of the failed write's end tries again");

            now += 1000;
            store.Touch("a");
            Assert.AreEqual(3, attempts.Count, "a second after it, one does");
        }
        store.Flush();
        Assert.AreEqual(store.Get("a")!.UpdatedAt, new SessionStore(path).Get("a")!.UpdatedAt, "the stamps reach the file with the next write");
    }

    [TestMethod]
    public void Store_IgnoresUpdatesOfARemovedSession()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var store = new SessionStore(path);
        store.Update("a", "kimi", r => r.Title = "t");
        store.Remove("a");

        store.Touch("a");
        Assert.IsNull(store.Update("a", "kimi", r => r.Title = "late"));

        Assert.IsNull(store.Get("a"));
        Assert.IsNull(new SessionStore(path).Get("a"));
    }

    // ---------------------------------------------------------------- Zed's rules for forms and approvals

    [TestMethod]
    public void ClaudeQuestionsAndApprovals_FollowZedsRules()
    {
        foreach (var input in new[]
        {
            """{"questions":[{"question":"Which color?","header":"Color","multiSelect":false,"options":[{"label":"Red","description":"warm"},{"label":"Blue"}]}]}""",
            """{"questions":[{"question":"Which parts?","header":"Parts","multiSelect":true,"options":[{"label":"A"},{"label":"B"}]}]}""",
            """{"questions":[{"question":"Q1?","header":"One","options":[{"label":"x"},{"label":"y"}]},{"question":"Q2?","multiSelect":true,"options":[{"label":"z"},{"label":"w"}]}]}""",
        })
            ZedRules.CheckForm(ClaudeHooks.QuestionForm("s", "t", JsonNode.Parse(input)!.AsObject()));

        var write = JsonNode.Parse("""{"hook_event_name":"PermissionRequest","tool_name":"Write","tool_input":{"file_path":"C:\\w\\a.txt","content":"hi"}}""")!.AsObject();
        ZedRules.CheckPermission(ClaudeHooks.Permission("s", "t", write));
        ZedRules.CheckPermission(ClaudeHooks.TrustFolder("s", @"C:\w"));
        ZedRules.CheckPermission(ClaudeHooks.ChannelConsentQuestion("s"));
    }

    [TestMethod]
    public async Task FormFallbackApprovals_FollowZedsRules()
    {
        CreateElicitationResponse? answer = null;
        var agent = new ScriptedAdapter("fake", async (a, req, ct) =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init();
                case AcpMethods.Agent.SessionNew: return AcpJson.ToNode(new NewSessionResponse { SessionId = "f1" });
                case AcpMethods.Agent.SessionList: return AcpJson.ToNode(new ListSessionsResponse { Sessions = [] });
                case AcpMethods.Agent.SessionPrompt:
                    answer = await a.Rpc.RequestAsync<CreateElicitationRequest, CreateElicitationResponse>(AcpMethods.Client.ElicitationCreate, new ElicitationFormMode
                    {
                        Message = "A few questions",
                        SessionId = "f1",
                        ToolCallId = "t1",
                        RequestedSchema = new ElicitationSchema
                        {
                            Type = ElicitationSchemaType.Object,
                            Properties = new()
                            {
                                ["q1"] = new StringPropertySchema { Title = "Color", OneOf = [new EnumOption { Const = "Red", Title = "Red" }, new EnumOption { Const = "Blue", Title = "Blue" }] },
                                ["q2"] = new StringPropertySchema { Title = "Size", Enum = ["S", "M"] },
                                ["q3"] = new MultiSelectPropertySchema { Title = "Parts", Items = new TitledMultiSelectItems { AnyOf = [new EnumOption { Const = "A", Title = "A" }] } },
                                ["q4"] = new BooleanPropertySchema { Title = "Sure?" },
                            },
                            Required = ["q1", "q2", "q3", "q4"],
                        },
                    }, ct);
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                default: throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        });
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed", forms: false);
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "ask me");

        Assert.AreEqual(4, zed.Approvals.Count);
        foreach (var approval in zed.Approvals) ZedRules.CheckPermission(approval);
        var content = ((ElicitationAcceptAction)answer!).Content!;
        Assert.AreEqual("Red", (string?)content["q1"]);
        Assert.AreEqual("S", (string?)content["q2"]);
        Assert.AreEqual("A", (string?)content["q3"]![0]);
        Assert.IsTrue((bool)content["q4"]!);
    }
}

/// <summary>
/// What Zed (main b5cd310) accepts, from its ACP client code: permission options of the four
/// standard kinds with distinct IDs (<c>permission_option_from_v1</c>, <c>request_permission</c>),
/// and forms whose required properties are defined and renderable, with no string that has both
/// <c>enum</c> and <c>oneOf</c> (<c>validate_form_schema</c>). Anything else fails the whole
/// request with -32602.
/// </summary>
static class ZedRules
{
    static readonly HashSet<string> Kinds = ["allow_once", "allow_always", "reject_once", "reject_always"];

    public static void CheckPermission(RequestPermissionRequest request)
    {
        Assert.IsTrue(request.Options.Count > 0, "Zed rejects an approval without options");
        Assert.AreEqual(request.Options.Count, request.Options.Select(o => o.OptionId).Distinct().Count(), "Zed rejects duplicate option IDs");
        foreach (var option in request.Options)
            Assert.IsTrue(Kinds.Contains(option.Kind.Value), $"Zed rejects the option kind '{option.Kind.Value}'");
    }

    public static void CheckForm(ElicitationFormMode form)
    {
        var properties = form.RequestedSchema.Properties ?? [];
        foreach (var name in form.RequestedSchema.Required ?? [])
        {
            Assert.IsTrue(properties.TryGetValue(name, out var property), $"required property '{name}' is not defined");
            Assert.IsTrue(Renderable(property), $"required property '{name}' has a type Zed cannot render");
        }
        foreach (var (name, property) in properties)
            Assert.IsFalse(property is StringPropertySchema { Enum: not null, OneOf: not null }, $"'{name}' sets both enum and oneOf");
    }

    static bool Renderable(ElicitationPropertySchema? property) => property switch
    {
        StringPropertySchema or NumberPropertySchema or IntegerPropertySchema or BooleanPropertySchema => true,
        MultiSelectPropertySchema m => m.Items is StringMultiSelectItems or TitledMultiSelectItems,
        _ => false,
    };
}

/// <summary>
/// A scripted agent with numbered sessions that records every call by session and behaves like
/// Kimi 2.4.1 where it matters: it announces its commands as soon as a session exists, lists
/// sessions untitled until their first prompt titles them, and answers a delete of a session it
/// does not have with -32602. One that cannot delete keeps listing a session after a delete, as
/// the Claude adapter keeps listing the sessions it started.
/// </summary>
sealed class LifecycleAgent
{
    readonly List<(string Method, string? SessionId)> calls = [];
    readonly List<string> sessions;
    readonly Dictionary<string, string> titles;
    int next;

    public LifecycleAgent(string name, bool canDelete, bool notFoundOnDelete = false, string[]? existing = null, Dictionary<string, string>? titles = null)
    {
        sessions = [.. existing ?? []];
        this.titles = titles ?? [];
        Adapter = new ScriptedAdapter(name, async (agent, req, ct) =>
        {
            var sessionId = (string?)req.Params?["sessionId"];
            if (req.Method is not (AcpMethods.Agent.Initialize or AcpMethods.Agent.SessionList or AcpMethods.Agent.SessionNew))
                lock (calls) calls.Add((req.Method, sessionId));
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize:
                    return AcpJson.ToNode(new InitializeResponse
                    {
                        ProtocolVersion = 1,
                        AgentCapabilities = new AgentCapabilities
                        {
                            LoadSession = LoadsWithoutIds,
                            SessionCapabilities = new SessionCapabilities
                            {
                                List = new SessionListCapabilities(),
                                Resume = new SessionResumeCapabilities(),
                                Close = new SessionCloseCapabilities(),
                                Delete = canDelete ? new SessionDeleteCapabilities() : null,
                                SystemPrompt = TakesSystemPrompt ? new SessionSystemPromptCapabilities() : null,
                            },
                            Meta = new JsonObject
                            {
                                [GorynychProtocol.CapOpenElsewhere] = TellsOpenElsewhere,
                                [GorynychProtocol.MetaSessionsChanged] = TellsListChanges,
                                [GorynychProtocol.CapReplayFrom] = KeepsHistory,
                            },
                        },
                        AgentInfo = new Implementation { Name = name, Version = "1" },
                    });
                case AcpMethods.Agent.SessionNew:
                    var id = $"{name}-{Interlocked.Increment(ref next)}";
                    lock (sessions) sessions.Add(id);
                    lock (calls) calls.Add((req.Method, id));
                    lock (SystemPrompts) SystemPrompts.Add((req.Method, id, req.Params?["systemPrompt"]?.DeepClone()));
                    if (req.Params?["mcpServers"]?.AsArray().FirstOrDefault(s => (string?)s?["name"] == "gorynych")?["args"]?.AsArray() is { Count: > 0 } peerArgs)
                        lock (Tokens) Tokens[id] = (string)peerArgs[peerArgs.Count - 1]!;
                    if (BeforeNew is { } beforeNew) await beforeNew(id);
                    await agent.Update(id, new AvailableCommandsUpdate { AvailableCommands = CommandsOnNew ?? [] });
                    return WithModes(AcpJson.ToNode(new NewSessionResponse { SessionId = id })!.AsObject(), id);
                case AcpMethods.Agent.SessionList:
                    if (BeforeList is { } beforeList) await beforeList();
                    lock (sessions)
                        return AcpJson.ToNode(new ListSessionsResponse
                        {
                            Sessions = sessions.Select(s => new SessionInfo { SessionId = s, Cwd = ListCwd, Title = this.titles.GetValueOrDefault(s) }).ToList(),
                        });
                case AcpMethods.Agent.SessionPrompt:
                    // Text, and a link as the Claude adapter writes one.
                    var blocks = req.ParamsAs<PromptRequest>().Prompt.Select(b => b switch { TextContent t => t.Text, ResourceLink l => $"@{l.Uri}", _ => null })
                        .OfType<string>().ToArray();
                    // The blocks joined as the Claude adapter joins them; the title from the first, like Kimi's.
                    var text = string.Join("\n\n", blocks);
                    lock (Prompts) { Prompts.Add((sessionId!, text)); firstBlocks.TryAdd(sessionId!, blocks); }
                    if (BeforePrompt is { } beforePrompt) await beforePrompt(sessionId!);
                    if (DuringPrompt is { } during) await during(agent, sessionId!);
                    lock (sessions) this.titles.TryAdd(sessionId!, blocks[0]);
                    var turn = KeepsHistory || LoadsWithoutIds ? $"t{Interlocked.Increment(ref nativeTurns)}" : null;
                    if (AnswerUpdates is { } updates)
                        foreach (var u in updates) await agent.Update(sessionId!, u);
                    else if (Answer is { } answer)
                        await agent.Update(sessionId!, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = answer }, MessageId = KeepsHistory ? turn + "-answer" : null });
                    // Its turn goes into its own history, the prompt under the service's ID as Codex keeps it.
                    if (turn is not null) Record(sessionId!, turn, text, (string?)req.Params?["_meta"]?[GorynychProtocol.MetaMessageId], Answer);
                    var prompted = AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn })!.AsObject();
                    if (KeepsHistory) prompted["_meta"] = new JsonObject { [GorynychProtocol.MetaHistoryMark] = turn };
                    return prompted;
                case AcpMethods.Agent.SessionResume when HolderOf(sessionId!) is { } holder:
                    throw new JsonRpcException(JsonRpcException.InvalidRequest, $"This thread is open in {holder}; close it there, then try again.");
                case GorynychProtocol.MethodOpenElsewhere when TellsOpenElsewhere && OpenElsewhereFails:
                    throw new InvalidOperationException("the adapter broke");
                case GorynychProtocol.MethodOpenElsewhere when TellsOpenElsewhere:
                {
                    var held = new JsonObject();
                    foreach (var asked in (req.Params?["sessionIds"] as JsonArray ?? []).Select(i => (string?)i).OfType<string>())
                        if (HolderOf(asked) is { } h) held[asked] = h;
                    return new JsonObject { ["sessions"] = held };
                }
                case AcpMethods.Agent.SessionResume:
                {
                    lock (SystemPrompts) SystemPrompts.Add((req.Method, sessionId!, req.Params?["systemPrompt"]?.DeepClone()));
                    if (BeforeResume is { } beforeResume) await beforeResume(sessionId!);
                    var resumed = WithModes([], sessionId!);
                    if (KeepsHistory && req.Params?["_meta"]?[GorynychProtocol.MetaReplayFrom] is JsonObject from)
                    {
                        lock (Replays) Replays.Add((sessionId!, from.DeepClone()));
                        if (HistoryUnreadable) return resumed;
                        var (turns, whence) = TurnsAfter(sessionId!, (string?)from["type"] == GorynychProtocol.ReplayAfterMark ? (string?)from["mark"] : null);
                        foreach (var u in turns.SelectMany(t => t.Updates)) await SendUpdateAsync(agent, sessionId!, u);
                        resumed["_meta"] = new JsonObject { [GorynychProtocol.MetaReplay] = new JsonObject { ["from"] = whence, ["mark"] = LastTurn(sessionId!) } };
                    }
                    return resumed;
                }
                case AcpMethods.Agent.SessionLoad when LoadsWithoutIds:
                    foreach (var u in TurnsAfter(sessionId!, null).Turns.SelectMany(t => t.Updates)) await SendUpdateAsync(agent, sessionId!, u);
                    return WithModes([], sessionId!);
                case AcpMethods.Agent.SessionSetConfigOption when OnSetConfig is { } onSetConfig && await onSetConfig((string)req.Params!["configId"]!, req.Params["value"]):
                    return WithModes([], sessionId!);
                case AcpMethods.Agent.SessionSetConfigOption when ReportsModes && Equals((string?)req.Params?["configId"], "mode"):
                    lock (Modes) Modes[sessionId!] = (string)req.Params!["value"]!;
                    return WithModes([], sessionId!);
                case AcpMethods.Agent.SessionSetConfigOption when ReportsModels && Equals((string?)req.Params?["configId"], "model"):
                    lock (ModelsChosen) ModelsChosen[sessionId!] = (string)req.Params!["value"]!;
                    return WithModes([], sessionId!);
                case AcpMethods.Agent.SessionClose:
                    return new JsonObject();
                case GorynychProtocol.MethodOpinion when HasChannel:
                    if (!Running) throw new JsonRpcException(JsonRpcException.InvalidRequest, "The Claude session is not running; prompt it first.");
                    lock (Opinions) Opinions.Add(req.Params!.DeepClone().AsObject());
                    if (HoldOpinions is { } hold) await hold.Task;
                    return new JsonObject { ["text"] = Answer ?? "" };
                case GorynychProtocol.MethodNote when HasChannel:
                    if (!Running) throw new JsonRpcException(JsonRpcException.InvalidRequest, "The Claude session is not running.");
                    lock (Notes) Notes.Add(req.Params!.DeepClone().AsObject());
                    return new JsonObject();
                case AcpMethods.Agent.SessionDelete when canDelete:
                    if (BeforeDelete is { } beforeDelete) await beforeDelete(sessionId!);
                    if (notFoundOnDelete) throw new JsonRpcException(JsonRpcException.ResourceNotFound, "no such session");
                    lock (sessions)
                        if (!sessions.Remove(sessionId!)) throw new JsonRpcException(JsonRpcException.InvalidParams, $"Invalid params: Unknown sessionId: {sessionId}");
                    return new JsonObject();
                default:
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        });
    }

    public ScriptedAdapter Adapter { get; }

    /// <summary>The peer token each session got in its <c>gorynych</c> MCP server.</summary>
    public Dictionary<string, string> Tokens { get; } = [];

    /// <summary>Every prompt as it arrives (before <see cref="BeforePrompt"/>), with its text blocks joined.</summary>
    public List<(string SessionId, string Text)> Prompts { get; } = [];

    readonly Dictionary<string, string[]> firstBlocks = [];

    /// <summary>The text blocks of the session's first prompt, apart.</summary>
    public string[] FirstBlocks(string sessionId)
    {
        lock (Prompts) return firstBlocks.GetValueOrDefault(sessionId) ?? [];
    }

    /// <summary>Advertises <c>sessionCapabilities.systemPrompt</c> (append), as the Claude adapter does; set before the first use.</summary>
    public bool TakesSystemPrompt { get; set; }

    /// <summary>
    /// Reports a <c>mode</c> config option on new, resume and set_config_option: <c>ask</c>, or
    /// <c>full</c>, marked <c>gorynych/fullAccess</c> as the adapters mark their full access.
    /// </summary>
    public bool ReportsModes { get; set; }

    /// <summary>Reports a <c>model</c> config option (category model) too: <c>small</c> or <c>large</c>, kept per session in <see cref="ModelsChosen"/>.</summary>
    public bool ReportsModels { get; set; }

    /// <summary>Each session's model (<c>small</c> when absent).</summary>
    public Dictionary<string, string> ModelsChosen { get; } = [];

    /// <summary>Advertises <c>gorynych/openElsewhere</c> and answers <c>_gorynych/open_elsewhere</c> from <see cref="OpenElsewhere"/>; set before the first use.</summary>
    public bool TellsOpenElsewhere { get; set; }

    /// <summary>Advertises <c>gorynych/sessionsChanged</c>, as the Codex adapter does; set before the first use.</summary>
    public bool TellsListChanges { get; set; }

    /// <summary>Fails every <c>_gorynych/open_elsewhere</c>, as a broken adapter would.</summary>
    public bool OpenElsewhereFails { get; set; }

    /// <summary>Sessions another app has open, with who: their resume is refused as Codex's is.</summary>
    public Dictionary<string, string> OpenElsewhere { get; } = [];

    string? HolderOf(string sessionId)
    {
        lock (OpenElsewhere) return OpenElsewhere.GetValueOrDefault(sessionId);
    }

    /// <summary>Each session's mode (<c>ask</c> when absent), as its agent keeps it: a change here is what the next resume reports.</summary>
    public Dictionary<string, string> Modes { get; } = [];

    JsonObject WithModes(JsonObject response, string sessionId)
    {
        if (!ReportsModes) return response;
        string current;
        lock (Modes) current = Modes.GetValueOrDefault(sessionId, "ask");
        var options = new JsonArray();
        if (ReportsModels)
        {
            string model;
            lock (ModelsChosen) model = ModelsChosen.GetValueOrDefault(sessionId, "small");
            options.Add(AcpJson.ToNode<SessionConfigOption>(new SessionConfigSelect
            {
                Id = "model",
                Name = "Model",
                Category = SessionConfigOptionCategory.Model,
                CurrentValue = model,
                Options = new JsonArray(
                    AcpJson.ToNode(new SessionConfigSelectOption { Value = "small", Name = "Small", Description = "Quick" }),
                    AcpJson.ToNode(new SessionConfigSelectOption { Value = "large", Name = "Large" })),
            }));
        }
        response["configOptions"] = options;
        options.Add(AcpJson.ToNode<SessionConfigOption>(new SessionConfigSelect
        {
            Id = "mode",
            Name = "Permissions",
            Category = SessionConfigOptionCategory.Mode,
            CurrentValue = current,
            Options = new JsonArray(
                AcpJson.ToNode(new SessionConfigSelectOption { Value = "ask", Name = "Ask" }),
                AcpJson.ToNode(new SessionConfigSelectOption { Value = "full", Name = "Full access", Meta = new JsonObject { [GorynychProtocol.MetaFullAccess] = true } })),
        }));
        return response;
    }

    /// <summary>The <c>systemPrompt</c> param of every <c>session/new</c> and <c>session/resume</c>, in order.</summary>
    public List<(string Method, string SessionId, JsonNode? SystemPrompt)> SystemPrompts { get; } = [];

    /// <summary>The commands it says of a new session, right after making it (none when null).</summary>
    public List<AvailableCommand>? CommandsOnNew { get; set; }

    /// <summary>Runs first on <c>session/set_config_option</c> (its option and value): may wait or throw; true answers it as is.</summary>
    public Func<string, JsonNode?, Task<bool>>? OnSetConfig { get; set; }

    /// <summary>Chunks a prompt answers with instead of <see cref="Answer"/>, sent as given (so without a messageId unless they have one).</summary>
    public List<SessionUpdate>? AnswerUpdates { get; set; }

    /// <summary>Takes <c>_gorynych/note</c> as Claude's channel does; otherwise the agent does not know the method, like Kimi.</summary>
    public bool HasChannel { get; set; }

    /// <summary>The <c>_gorynych/note</c> params received.</summary>
    public List<JsonObject> Notes { get; } = [];

    /// <summary>The <c>_gorynych/opinion</c> params received through the channel (with <see cref="HasChannel"/>).</summary>
    public List<JsonObject> Opinions { get; } = [];

    /// <summary>While set, a request through the channel is answered only once this completes.</summary>
    public TaskCompletionSource? HoldOpinions { get; set; }

    /// <summary>False refuses notes and opinions as the Claude adapter does while Claude's process is not running.</summary>
    public bool Running { get; set; } = true;

    /// <summary>The text a turn answers with after <see cref="BeforePrompt"/>; null answers with none.</summary>
    public string? Answer { get; set; } = "ok";

    /// <summary>The folder the agent lists its sessions in, spelled its own way.</summary>
    public string ListCwd { get; set; } = Path.GetTempPath();

    /// <summary>Runs before <c>session/new</c> answers, with the new session's ID.</summary>
    public Func<string, Task>? BeforeNew { get; set; }

    public Func<string, Task>? BeforePrompt { get; set; }

    /// <summary>Runs in a prompt's turn, with the agent's side of the connection: to ask its client something.</summary>
    public Func<ScriptedAgent, string, Task>? DuringPrompt { get; set; }

    /// <summary>Runs before each <c>session/list</c> is answered: a slow agent's list.</summary>
    public Func<Task>? BeforeList { get; set; }
    public Func<string, Task>? BeforeResume { get; set; }
    public Func<string, Task>? BeforeDelete { get; set; }

    // ---------------------------------------------------------------- its own history

    readonly Dictionary<string, List<(string Turn, List<JsonObject> Updates)>> history = [];
    int nativeTurns;

    /// <summary>
    /// Keeps each session's history as its own, turn by turn with IDs, as Codex does: advertises
    /// <c>gorynych/replayFrom</c>, ends every prompt with the turn's ID as its mark, and replays the
    /// turns after a mark on <c>session/resume</c>. Set before the first use.
    /// </summary>
    public bool KeepsHistory { get; set; }

    /// <summary>
    /// Keeps each session's history without IDs and replays all of it on <c>session/load</c>, as
    /// Kimi does (it advertises <c>loadSession</c>). Set before the first use.
    /// </summary>
    public bool LoadsWithoutIds { get; set; }

    /// <summary>With <see cref="KeepsHistory"/>: answers a replay without <c>gorynych/replay</c>, as an adapter that cannot read the history.</summary>
    public bool HistoryUnreadable { get; set; }

    /// <summary>The <c>gorynych/replayFrom</c> of every resume that had one.</summary>
    public List<(string SessionId, JsonNode ReplayFrom)> Replays { get; } = [];

    void Record(string sessionId, string turn, string user, string? userId, string? answer, int answerChunks = 1)
    {
        var updates = new List<JsonObject>
        {
            new() { ["sessionUpdate"] = "user_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = user } },
        };
        if (KeepsHistory) updates[0]["messageId"] = userId ?? turn + "-user";
        // Kimi replays a user message with a system reminder of its own after the text.
        if (LoadsWithoutIds) updates.Add(new JsonObject { ["sessionUpdate"] = "user_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "<system-reminder> Plan mode is the exception. </system-reminder>" } });
        if (answer is not null)
            for (var i = 0; i < answerChunks; i++) // the answer, then its further chunks of one message: " 1", " 2", …
            {
                updates.Add(new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = i == 0 ? answer : $" {i}" } });
                if (KeepsHistory) updates[^1]["messageId"] = turn + "-answer";
            }
        lock (history)
        {
            if (!history.TryGetValue(sessionId, out var turns)) history[sessionId] = turns = [];
            turns.Add((turn, updates));
        }
    }

    /// <summary>
    /// A turn made outside Gorynych (Codex Desktop, the CLI): in the history only, its answer in
    /// <paramref name="answerChunks"/> chunks. Returns its ID.
    /// </summary>
    public string AddNativeTurn(string sessionId, string user, string answer, int answerChunks = 1)
    {
        var turn = $"n{Interlocked.Increment(ref nativeTurns)}";
        Record(sessionId, turn, user, null, answer, answerChunks);
        return turn;
    }

    /// <summary>Drops the last turns of the history, as a rollback or an undo does.</summary>
    public void DropNativeTurns(string sessionId, int count)
    {
        lock (history) history[sessionId].RemoveRange(history[sessionId].Count - count, count);
    }

    /// <summary>Replaces the history by a summary and its last turns, as Kimi's compaction does.</summary>
    public void CompactNatively(string sessionId, int keep)
    {
        lock (history)
        {
            var turns = history[sessionId];
            var kept = turns.Skip(turns.Count - keep).ToList();
            turns.Clear();
            turns.Add(("c", [new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Summary of the earlier work." } }]));
            turns.AddRange(kept);
        }
    }

    (List<(string Turn, List<JsonObject> Updates)> Turns, string From) TurnsAfter(string sessionId, string? mark)
    {
        lock (history)
        {
            var turns = history.GetValueOrDefault(sessionId) ?? [];
            var at = mark is null ? -1 : turns.FindIndex(t => t.Turn == mark);
            return mark is not null && at >= 0 ? (turns.Skip(at + 1).ToList(), "mark") : (turns.ToList(), "start");
        }
    }

    string? LastTurn(string sessionId)
    {
        lock (history) return history.GetValueOrDefault(sessionId)?.LastOrDefault().Turn;
    }

    static Task SendUpdateAsync(ScriptedAgent agent, string sessionId, JsonObject update) =>
        agent.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject { ["sessionId"] = sessionId, ["update"] = update.DeepClone() });

    /// <summary>The agent forgets the session on its own, as if deleted from its own CLI.</summary>
    public void LoseNatively(string sessionId)
    {
        lock (sessions) sessions.Remove(sessionId);
    }

    /// <summary>The methods called for <paramref name="sessionId"/>, in order (session/new counted under the ID it returned).</summary>
    public string[] CallsFor(string sessionId)
    {
        lock (calls) return calls.Where(c => c.SessionId == sessionId).Select(c => c.Method).ToArray();
    }
}
