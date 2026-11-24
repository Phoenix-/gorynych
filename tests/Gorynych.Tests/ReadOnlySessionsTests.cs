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
                lock (held)
                {
                    var found = ids.Where(held.ContainsKey).ToDictionary(id => id, id => held[id]);
                    foreach (var id in found.Keys.Where(lookedAtOnce.Contains).ToList())
                    {
                        // It lets go right after it was seen, before the adapter noted it.
                        lookedAtOnce.Remove(id);
                        held.Remove(id);
                        Fake.Threads[id].OpenElsewhere = false;
                    }
                    return found;
                }
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

        readonly HashSet<string> lookedAtOnce = new(StringComparer.Ordinal);

        /// <summary>Codex Desktop lets the thread go right after the next look at who holds it, with no event of its lock file.</summary>
        public void LetGoAfterOneLook(string id = Thread)
        {
            lock (held) lookedAtOnce.Add(id);
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
    public async Task OldReattachCompletionCannotClearNewRouteCheck()
    {
        var feed = new SessionFeed("s", new SessionJournal(Path.Combine(TestDirs.New(), "journal"), 100));
        var oldWire = new DuplexLink(); var newWire = new DuplexLink();
        await using var oldRoute = new JsonRpcConnection(oldWire.ClientEnd.Input, oldWire.ClientEnd.Output, "old-route");
        await using var newRoute = new JsonRpcConnection(newWire.ClientEnd.Input, newWire.ClientEnd.Output, "new-route");
        feed.RequireReattachedOpen(oldRoute);
        using var staleReady = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var stale = Task.Run(() =>
        {
            staleReady.Set(); if (!release.Wait(Wait)) throw new TimeoutException("stale completion barrier");
            return feed.ApplyNativeOpen(oldRoute, false, () => throw new AssertFailedException("stale result was applied"));
        });
        Assert.IsTrue(await Task.Run(() => staleReady.Wait(Wait)));
        feed.RequireReattachedOpen(newRoute);
        release.Set(); Assert.IsFalse(await stale.WaitAsync(Wait), "old native open is rejected");
        feed.ClearReattachedOpenRoute(oldRoute);
        Assert.AreSame(newRoute, feed.ReattachedOpenRoute, "old completion cannot clear a replacement route");
        Assert.IsTrue(feed.NeedsRetakeFor(newRoute), "pre-handoff decision keeps new route fenced");
        Assert.IsFalse(feed.TrySetReadOnlyFromOpen(oldRoute, null, out _), "stale UI state is rejected");
        feed.ClearReattachedOpenRoute(newRoute);
        Assert.IsNull(feed.ReattachedOpenRoute);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ReattachedRunningCodexTurnSurvivesExplicitResume()
    {
        var data = TestDirs.New(); var fake = new FakeAppServer();
        var adapter = new CodexAdapter(new CodexAdapterOptions { Launch = fake.StartAsync, ClientVersion = "test",
            FindHolders = _ => new Dictionary<string, string>(), LocksDirectory = TestDirs.New() });
        var hosts = new InProcessHosts(_ => adapter);
        var first = hosts.Conductor(data, adapter); hosts.Service = first;
        await using var client = await TestClient.ConnectAsync(first, agent: "codex");
        var id = await HostTests.NewSession(client);
        var originalTurn = PromptAsync(client, "wait", id);
        _ = originalTurn.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        await Until(() => fake.All("turn/start").Length == 1, "native turn started");
        var beforeResumes = fake.All("thread/resume").Length;
        await HostTests.RestartAsync(first); await HostTests.GoneAsync(first, null, hosts);
        await using var reopened = hosts.Conductor(data, adapter); hosts.Service = reopened;
        await Until(() => reopened.Agents["codex"].RouteOf(id) is not null && reopened.Feed(id).Adopted is not null, "running turn adopted");
        await using var resumed = await TestClient.ConnectAsync(reopened, agent: "codex");
        await resumed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume(id)).WaitAsync(Wait);
        Assert.IsNotNull(reopened.Feed(id).Adopted, "resume leaves the original turn running");
        Assert.AreEqual(beforeResumes, fake.All("thread/resume").Length, "no native retake while a turn runs");
        Assert.AreEqual(1, fake.All("turn/start").Length, "no replayed input");
        Assert.AreEqual(0, fake.All("turn/interrupt").Length, "resume did not cancel the native turn");
        Assert.IsNotNull(reopened.Feed(id).ReattachedOpenRoute, "idle handoff remains fenced");
        await resumed.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = id });
        await Until(() => reopened.Feed(id).Adopted is null && !reopened.Feed(id).TurnRunning, "fixture turn ended by normal cancel");
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(resumed, "hello", id).WaitAsync(Wait)).StopReason);
        Assert.AreEqual(2, fake.All("turn/start").Length, "only the deliberate next input starts");
        Assert.IsNull(reopened.Feed(id).ReattachedOpenRoute, "idle handoff checked its exact route");
    }

    [TestMethod]
    [DataRow("prompt")]
    [DataRow("direct")]
    [DataRow("mailbox")]
    [DataRow("held")]
    [DataRow("already-full")]
    [DataRow("usage")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task HostedReadOnlyOpenIsRetakenAfterConductorReattach(string scenario)
    {
        var data = TestDirs.New(); var locks = TestDirs.New(); var fake = new FakeAppServer();
        fake.Existing(Thread, Cwd, name: "Open in Codex Desktop", openElsewhere: scenario != "already-full");
        var held = scenario == "already-full" ? 0 : 1;
        if (held == 1) File.WriteAllText(Path.Combine(locks, Thread + ".lock"), "");
        var adapter = new CodexAdapter(new CodexAdapterOptions { Launch = fake.StartAsync, ClientVersion = "test",
            TurnEndWait = TimeSpan.FromMilliseconds(200), LocksDirectory = locks, LockSettle = TimeSpan.FromMilliseconds(50),
            LockRecheck = TimeSpan.FromMilliseconds(200),
            FindHolders = ids => Volatile.Read(ref held) == 1 ? ids.ToDictionary(id => id, _ => "Codex Desktop") : new Dictionary<string, string>() });
        var hosts = new InProcessHosts(_ => adapter);
        Conductor NewService() => new(new ConductorOptions { DataDirectory = data, ServiceId = "retake-host",
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [adapter]);
        var first = NewService(); hosts.Service = first;
        await using (var client = await TestClient.ConnectAsync(first, agent: "codex"))
        {
            var meta = await MetaOfAsync(client, AcpMethods.Agent.SessionResume);
            Assert.AreEqual(scenario == "already-full" ? GorynychProtocol.InteractivityFull : GorynychProtocol.InteractivityReadOnly, Interactivity(meta));
            Assert.AreEqual(scenario != "already-full", first.Feed(Thread).OpenedReadOnly);
            if (scenario is not ("held" or "already-full"))
            {
                fake.Threads[Thread].OpenElsewhere = false; Interlocked.Exchange(ref held, 0);
                File.Delete(Path.Combine(locks, Thread + ".lock"));
                await Until(() => first.Feed(Thread).ReadOnly is null && first.Feed(Thread).OpenedReadOnly, "holder released but native open remains read-only");
            }
            Assert.AreEqual(scenario == "already-full" ? 1 : 0, fake.All("thread/resume").Length);
        }
        var initialResumes = fake.All("thread/resume").Length;
        var initialUsageReads = fake.All("account/rateLimits/read").Length;
        var original = hosts["codex"]; await HostTests.RestartAsync(first);
        await HostTests.GoneAsync(first, null, hosts);
        await using var reopened = NewService(); hosts.Service = reopened;
        await Until(() => reopened.Agents["codex"].RouteOf(Thread) is not null, "original host reattached");
        Assert.AreSame(original.Host, hosts["codex"].Host);
        await using var resumed = await TestClient.ConnectAsync(reopened, agent: "codex");
        Assert.IsTrue(reopened.Feed(Thread).OpenedReadOnly, "reattached native open must be checked before a handoff");
        if (scenario is not ("mailbox" or "direct"))
        {
            var resumedMeta = (await resumed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait))?["_meta"];
            Assert.AreEqual(scenario == "held", reopened.Feed(Thread).OpenedReadOnly, "resume checks the exact native open");
            Assert.AreEqual(scenario == "held" ? GorynychProtocol.InteractivityReadOnly : GorynychProtocol.InteractivityFull,
                Interactivity(resumedMeta), "the UI reflects the current holder separately from native authority");
        }
        if (scenario == "held")
        {
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(resumed, "held").WaitAsync(Wait));
            Assert.AreEqual(0, fake.All("turn/start").Length, "external holder was not bypassed");
            fake.Threads[Thread].OpenElsewhere = false; Interlocked.Exchange(ref held, 0);
            File.Delete(Path.Combine(locks, Thread + ".lock"));
            await Until(() => reopened.Feed(Thread).ReadOnly is null, "holder released after reattach");
        }
        if (scenario == "mailbox")
        {
            var sender = (string)(await resumed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() }).WaitAsync(Wait))!["sessionId"]!;
            var token = reopened.Store.Get(sender)!.PeerToken!; const string id = "reattach-mailbox";
            await resumed.Rpc.SendRequestAsync(GorynychProtocol.MethodSendMessage, new JsonObject { ["sessionId"] = Thread,
                ["text"] = "mailbox after reattach", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token,
                    [GorynychProtocol.MetaMessageRequestId] = id } }).WaitAsync(Wait);
            try { await Until(() => (bool?)reopened.ReplyMailbox.Find(id)?["delivered"] == true, "mailbox handoff completed"); }
            catch (AssertFailedException e) { throw new AssertFailedException(e.Message + " row=" + reopened.ReplyMailbox.Find(id)?.ToJsonString() +
                " starts=" + fake.All("turn/start").Length + " resumes=" + fake.All("thread/resume").Length + " host=" + hosts.Log()); }
            Assert.IsNull(reopened.ReplyMailbox.Find(id)?["error"]);
        }
        else Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(resumed, scenario == "usage" ? "/usage" : "hello").WaitAsync(Wait)).StopReason);
        Assert.AreEqual(initialResumes + (scenario == "already-full" ? 0 : 1), fake.All("thread/resume").Length,
            "the adapter retakes a read-only thread and keeps an already-owned thread");
        Assert.AreEqual(scenario == "usage" ? 0 : 1, fake.All("turn/start").Length, "one native turn or local usage only");
        if (scenario == "usage") Assert.AreEqual(initialUsageReads + 1, fake.All("account/rateLimits/read").Length);
        else
        {
            Assert.AreEqual("workspaceWrite", (string?)fake.Threads[Thread].Settings["sandbox"]?["type"], "native mode remains unchanged");
            Assert.IsNull(fake.Last("turn/start")["sandboxPolicy"], "retake did not override native mode");
        }
        Assert.AreEqual("svc.exe", (string?)fake.Last("thread/resume")["config"]?["mcp_servers"]?["gorynych"]?["command"], "managed MCP survives retake");
        Assert.AreEqual(1, HostTests.JournalText(reopened, Thread).Split("Fixed it.").Length - 1, "original native history remains once");
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
    public async Task AHolderTheWatcherCannotSee_KeepsTheSessionReadOnly_UntilAResumeTakesIt()
    {
        // App-server refuses the thread, but who holds it cannot be told (outside Windows, or an
        // elevated holder): the lock watcher seeing no holder does not mean it went.
        await using var rig = new Rig(heldAtStart: false);
        rig.Fake.Threads[Thread].OpenElsewhere = true;
        await using var zed = await rig.ZedAsync();
        var meta = await MetaOfAsync(zed, AcpMethods.Agent.SessionResume);
        Assert.AreEqual(GorynychProtocol.InteractivityReadOnly, Interactivity(meta));
        Assert.AreEqual("another Codex app (Codex Desktop, the CLI or VS Code)", (string?)meta[GorynychProtocol.MetaOpenElsewhere]);

        await Task.Delay(1000); // the watcher looks again every 200 ms, and sees no holder
        Assert.AreEqual(0, FlagUpdates(zed).Length, "not told it can be written to: " + string.Join(", ", FlagUpdates(zed)));
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(zed, "hello").WaitAsync(Wait));
        Assert.AreEqual(0, FlagUpdates(zed).Length, "still read-only, unchanged");
        CollectionAssert.AreEqual(new[] { "warning" }, Notices(zed).Select(n => (string?)n["severity"]).ToArray(), "one warning, at the open");

        rig.Fake.Threads[Thread].OpenElsewhere = false; // the other app lets go
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(zed, "hello").WaitAsync(Wait)).StopReason);
        CollectionAssert.AreEqual(new[] { GorynychProtocol.InteractivityFull }, FlagUpdates(zed), "free once a resume took it");
        CollectionAssert.AreEqual(new[] { "warning", "info" }, Notices(zed).Select(n => (string?)n["severity"]).ToArray());
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHolderThatLetsGoAsTheOpenNotesIt_IsLookedAtAgain_AndTheThreadIsTaken()
    {
        // It went between the look and the note, with no event of its lock file: the open looks once more.
        await using var rig = new Rig(heldAtStart: true, lockRecheck: TimeSpan.FromHours(1));
        await using var zed = await rig.ZedAsync();
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject()).WaitAsync(Wait); // the service learns of it
        rig.LetGoAfterOneLook(); // the open's own look is the one
        var meta = await MetaOfAsync(zed, AcpMethods.Agent.SessionResume);
        Assert.AreEqual(GorynychProtocol.InteractivityFull, Interactivity(meta));
        Assert.AreEqual(1, rig.Fake.All("thread/resume").Length, "taken");
        Assert.AreEqual(0, Notices(zed).Length);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AThreadTakenAfterItWasReadOnly_SendsItsOptions_AndItsSettingsChangeOnlyThen()
    {
        await using var rig = new Rig();
        await using var zed = await rig.ZedAsync();
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);
        foreach (var (method, @params) in new[]
                 {
                     (AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = Thread, ["configId"] = "model", ["value"] = "gpt-fake" }),
                     (AcpMethods.Agent.SessionSetMode, new JsonObject { ["sessionId"] = Thread, ["modeId"] = "read-only" }),
                 })
        {
            var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(method, @params).WaitAsync(Wait));
            Assert.AreEqual($"Session {Thread} is read-only for now, so its settings cannot change: {Reason} Then change them again.", refused.Message, method);
        }
        Assert.AreEqual(0, CodexAdapterOptions().Length);

        rig.LetGo();
        await Until(() => FlagUpdates(zed).LastOrDefault() == GorynychProtocol.InteractivityFull, "the flag to clear");
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(zed, "hello").WaitAsync(Wait)).StopReason);
        var options = CodexAdapterOptions();
        Assert.AreEqual(1, options.Length, "the options Codex reports once it took the thread reached the client");
        var option = (JsonObject)options[0]["configOptions"]!.AsArray()[0]!;
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject
        {
            ["sessionId"] = Thread, ["configId"] = (string?)option["id"], ["value"] = option["currentValue"]?.DeepClone(),
        }).WaitAsync(Wait);

        JsonObject[] CodexAdapterOptions() => zed.Snapshot().Where(u => (string?)u["sessionUpdate"] == "config_option_update").ToArray();
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AReadOnlyNotice_IsNotSentAgain_ToTheSameConnection_NorAfterTheLinkComesBack()
    {
        await using var rig = new Rig();
        await using var zed = await rig.ZedAsync();
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, Resume()).WaitAsync(Wait);
        Assert.AreEqual(1, Notices(zed).Length, "one warning on this connection");

        // A client on the resumable link: the service restarts twice; the first time nothing changed,
        // the second time the other app let go while the service was away.
        var said = new System.Text.StringBuilder();
        await using var link = new LinkHarness(rig.Service, new Gorynych.Acp.Link.ResumableLinkOptions
        {
            Log = m => { lock (said) said.AppendLine(m); },
            RetryDelays = [TimeSpan.FromMilliseconds(50)],
            GiveUpAfter = TimeSpan.FromSeconds(15),
            InitializeMeta = new JsonObject { [GorynychProtocol.MetaAgent] = "codex" },
        });
        await link.Zed.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject
        {
            ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject { ["session"] = new JsonObject { ["notices"] = new JsonObject() } },
        }).WaitAsync(Wait);
        await link.Zed.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);
        string?[] LinkNotices() { lock (link.Updates) return [.. link.Updates.Where(u => (string?)u["sessionUpdate"] == "notice").Select(u => (string?)u["severity"])]; }
        string?[] LinkFlags()
        {
            lock (link.Updates)
                return [.. link.Updates.Where(u => (string?)u["sessionUpdate"] == "session_info_update" && u["_meta"]?[GorynychProtocol.MetaInteractivity] is not null)
                    .Select(u => Interactivity(u["_meta"]))];
        }
        CollectionAssert.AreEqual(new[] { "warning" }, LinkNotices());

        async Task RestartAsync(Action whileAway)
        {
            var connects = link.Connects;
            link.Service = null;
            await rig.Service.DisposeAsync();
            link.Break();
            whileAway();
            rig.Service = rig.NewService();
            link.Service = rig.Service;
            await Until(() => { lock (said) return link.Connects > connects && said.ToString().Contains("connected again", StringComparison.Ordinal); }, "the link to come back");
            lock (said) said.Clear();
            await Task.Delay(300);
        }

        await RestartAsync(() => { });
        CollectionAssert.AreEqual(new[] { "warning" }, LinkNotices(), "the link said its client shows it read-only already");
        await RestartAsync(() => rig.LetGo());
        await Until(() => LinkFlags().LastOrDefault() == GorynychProtocol.InteractivityFull, "the client to learn it can write again");
        CollectionAssert.AreEqual(new[] { "warning", "info" }, LinkNotices(), "the change it missed, said once");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHoldersTurnWhileReadOnly_ReachesTheClientBeforeTheFlagClears_AndTheJournalKeepsItsOrder()
    {
        // As in Zed: it shows a prompt as soon as it is sent, so what was made meanwhile must be there before the input comes back.
        await using var rig = new Rig();
        await using var zed = await rig.ZedAsync();
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);
        rig.Fake.AddTurn(Thread, "two", "Desktop's two.");
        rig.LetGo();
        await Until(() => FlagUpdates(zed).LastOrDefault() == GorynychProtocol.InteractivityFull, "the flag to clear");

        var seen = zed.Snapshot();
        var two = Array.FindIndex(seen, u => (string?)u["content"]?["text"] == "Desktop's two.");
        var full = Array.FindIndex(seen, u => Interactivity(u["_meta"]) == GorynychProtocol.InteractivityFull);
        Assert.IsTrue(two >= 0 && two < full, $"the holder's turn came before the flag cleared ({two}, {full})");
        Assert.AreEqual(0, rig.Fake.All("thread/resume").Length, "read, not taken");

        var fullReadsBeforePrompt = rig.Fake.All("thread/turns/list").Count(p => (string?)p["itemsView"] == "full");
        Assert.AreEqual(2, fullReadsBeforePrompt, "the initial history and the holder's new turn were read");
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(zed, "three").WaitAsync(Wait)).StopReason);
        var users = HostTests.Journal(rig.Service, Thread).Where(u => (string?)u["sessionUpdate"] == "user_message_chunk")
            .Select(u => (string?)u["content"]?["text"]).ToArray();
        CollectionAssert.AreEqual(new[] { "Fix the flaky test", "two", "three" }, users, "one, two, three");
        Assert.AreEqual(1, HostTests.JournalText(rig.Service, Thread).Split("Desktop's two.").Length - 1, "once");
        Assert.AreEqual(fullReadsBeforePrompt, rig.Fake.All("thread/turns/list").Count(p => (string?)p["itemsView"] == "full"),
            "the catch-up kept its mark, so the prompt's open reads no full history again");
        Assert.AreEqual(0, rig.Fake.All("thread/items/list").Length, "unsupported in native Codex");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHoldersTurn_TheWatcherMissed_ComesWithTheRetake_BeforeTheOptions_TheFlag_AndThePrompt()
    {
        // The safety net: the holder went unseen, so the flag never cleared, and the prompt's own open catches up.
        await using var rig = new Rig(heldAtStart: true, lockRecheck: TimeSpan.FromHours(1));
        await using var zed = await rig.ZedAsync();
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume()).WaitAsync(Wait);
        rig.Fake.AddTurn(Thread, "two", "Desktop's two.");
        rig.LetGoUnseen();

        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(zed, "three").WaitAsync(Wait)).StopReason);
        var seen = zed.Snapshot();
        var two = Array.FindIndex(seen, u => (string?)u["content"]?["text"] == "Desktop's two.");
        var options = Array.FindIndex(seen, u => (string?)u["sessionUpdate"] == "config_option_update");
        var full = Array.FindIndex(seen, u => Interactivity(u["_meta"]) == GorynychProtocol.InteractivityFull);
        Assert.IsTrue(two >= 0 && two < options && options < full, $"the holder's turn, then the options, then the flag ({two}, {options}, {full})");
        Assert.AreEqual(1, rig.Fake.All("thread/resume").Length, "taken by the prompt");
        var users = HostTests.Journal(rig.Service, Thread).Where(u => (string?)u["sessionUpdate"] == "user_message_chunk")
            .Select(u => (string?)u["content"]?["text"]).ToArray();
        CollectionAssert.AreEqual(new[] { "Fix the flaky test", "two", "three" }, users, "one, two, three");
        Assert.AreEqual(1, HostTests.JournalText(rig.Service, Thread).Split("Desktop's two.").Length - 1, "once");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheOptions_OfAThreadTakenAfterItWasReadOnly_ReachAClientOnTheLink()
    {
        // As Zed gets them, through the bridge's resumable link.
        await using var rig = new Rig();
        await using var link = new LinkHarness(rig.Service, new Gorynych.Acp.Link.ResumableLinkOptions
        {
            RetryDelays = [TimeSpan.FromMilliseconds(50)],
            GiveUpAfter = TimeSpan.FromSeconds(15),
            InitializeMeta = new JsonObject { [GorynychProtocol.MetaAgent] = "codex" },
        });
        await link.Zed.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() }).WaitAsync(Wait);
        var opened = await link.Zed.SendRequestAsync(AcpMethods.Agent.SessionLoad, Resume()).WaitAsync(Wait);
        Assert.IsNull(opened?["configOptions"], "none while it is read-only: Codex says a thread's settings only once it loads it");
        rig.LetGo();
        JsonObject[] Of(string kind) { lock (link.Updates) return [.. link.Updates.Where(u => (string?)u["sessionUpdate"] == kind)]; }
        await Until(() => Of("session_info_update").Any(u => Interactivity(u["_meta"]) == GorynychProtocol.InteractivityFull), "the flag to clear");
        Assert.AreEqual(0, Of("config_option_update").Length, "free is not taken yet");

        await link.PromptAsync(Thread, "three").WaitAsync(Wait);
        var options = Of("config_option_update");
        Assert.AreEqual(1, options.Length, "the options, once a prompt took it");
        Assert.IsTrue(options[0]["configOptions"]!.AsArray().Count > 0);
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

        // Opening the oldest, which catches its journal up, moves no row: Codex's own time orders the list.
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("t-old")).WaitAsync(Wait);
        var rows = (await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest())).Sessions;
        CollectionAssert.AreEqual(new[] { Thread, "t-mid", "t-old" }, rows.Select(r => r.SessionId).ToArray(), "Codex's own order");
        Assert.AreEqual(GorynychProtocol.InteractivityReadOnly, Interactivity(rows[0].Meta), "marked in the first list");
        Assert.IsNull(Interactivity(rows[1].Meta));

        // A turn that ends there does, though Codex notes it a moment later (here never).
        await PromptAsync(zed, "hi", "t-old").WaitAsync(Wait);
        rows = (await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest())).Sessions;
        CollectionAssert.AreEqual(new[] { "t-old", Thread, "t-mid" }, rows.Select(r => r.SessionId).ToArray(), "its turn's end counts");
    }
}
