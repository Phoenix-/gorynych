using System.Text.Json.Nodes;
using System.Threading.Channels;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Adapters.Codex;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

[TestClass]
public sealed class SessionActivityTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    static string State(JsonNode snapshot) => (string)snapshot["state"]!;
    static JsonObject Terminal(string id) => new() { ["type"] = "terminal", ["terminalId"] = id };
    static BackgroundWorkSnapshot Snapshot(JsonArray value)
    {
        Assert.IsTrue(BackgroundWorkSnapshot.TryParse(value, out var result));
        return result!;
    }

    [TestMethod]
    public void BackgroundReferencesValidateTheWholeSnapshot()
    {
        Assert.IsTrue(BackgroundWorkSnapshot.TryParse(null, out var unknown));
        Assert.IsNull(unknown);
        Assert.AreEqual(0, Snapshot(new JsonArray()).Count);
        var references = new JsonArray(Terminal("term"), new JsonObject { ["type"] = "tool_call", ["toolCallId"] = "tool" },
            new JsonObject { ["type"] = "subagent", ["sessionId"] = "child" }, new JsonObject { ["type"] = "_future" });
        var parsed = Snapshot(references);
        Assert.AreEqual(4, parsed.Count);
        Assert.AreEqual(new BackgroundWorkReference("tool_call", "tool"), parsed.References[1]);
        references.Clear();
        Assert.AreEqual(4, parsed.ToJson().Count, "the snapshot owns its data");
        foreach (var bad in new JsonNode?[]
        {
            JsonValue.Create("not an array"), new JsonArray(new JsonObject { ["id"] = "missing type" }),
            new JsonArray(new JsonObject { ["type"] = "terminal", ["terminalId"] = "" }),
            new JsonArray(new JsonObject { ["type"] = "tool_call", ["toolCallId"] = 3 }),
            new JsonArray(new JsonObject { ["type"] = "subagent", ["sessionId"] = "child", ["_meta"] = "invalid" }),
            new JsonArray(Terminal("duplicate"), Terminal("duplicate")),
        }) Assert.IsFalse(BackgroundWorkSnapshot.TryParse(bad, out _));
    }

    [TestMethod]
    public void ClaudeBackgroundSnapshotIsAuthoritativeAndRejectsMalformedTasks()
    {
        var message = JsonNode.Parse("""{"tasks":[{"task_id":"a","task_type":"local_agent","description":"running"}]}""")!.AsObject();
        var tasks = HeadlessSession.ParseBackgroundTasks(message)!;
        Assert.AreEqual("a", (string?)tasks[0]?["_meta"]?["gorynych/nativeTaskId"]);
        Assert.AreEqual(0, HeadlessSession.ParseBackgroundTasks(new JsonObject { ["tasks"] = new JsonArray() })!.Count);
        Assert.IsNull(HeadlessSession.ParseBackgroundTasks(new JsonObject()));
        Assert.IsNull(HeadlessSession.ParseBackgroundTasks(JsonNode.Parse("""{"tasks":[{"task_id":"a"}]}""")!.AsObject()));
        Assert.IsNull(HeadlessSession.ParseBackgroundTasks(JsonNode.Parse("""{"tasks":[{"task_id":"a","task_type":"x","description":"x"},{"task_id":"a","task_type":"x","description":"x"}]}""")!.AsObject()));
    }

    [TestMethod]
    public void BackgroundPinSurvivesUnknownWithoutChangingForeground()
    {
        var feed = new SessionFeed("id", new SessionJournal(Path.Combine(TestDirs.New(), "journal"), 10));
        feed.ReportBackground(Snapshot(new JsonArray(Terminal("a"))));
        Assert.AreEqual(1, feed.Users);
        Assert.AreEqual("idle", State(feed.Activity.Snapshot()));
        feed.ReportBackground(null);
        Assert.AreEqual(1, feed.Users);
        feed.ReportBackground(Snapshot(new JsonArray()));
        Assert.AreEqual(0, feed.Users);
    }

    [TestMethod]
    public void PartialKnownWorkDoesNotClaimACompleteSnapshot()
    {
        var feed = new SessionFeed("id", new SessionJournal(Path.Combine(TestDirs.New(), "journal"), 10));
        feed.ReportKnownBackground(Snapshot(new JsonArray(Terminal("known"))));
        var live = feed.Activity.Snapshot();
        Assert.IsNull(live["background"]);
        Assert.AreEqual(1, live["backgroundKnown"]!.AsArray().Count);
        Assert.AreEqual(1, feed.Users);
        feed.ReportKnownBackground(null);
        Assert.AreEqual(1, feed.Users, "a failed partial refresh cannot release work");
        feed.ReportKnownBackground(Snapshot(new JsonArray()));
        Assert.AreEqual(0, feed.Users);
        Assert.IsNull(feed.Activity.Snapshot()["background"]);
    }

    [TestMethod]
    public async Task CodexBackgroundPagesRequireCompleteValidRefresh()
    {
        var calls = 0;
        Task<JsonNode?> Read(string? cursor, CancellationToken _) => Task.FromResult<JsonNode?>(++calls == 1
            ? JsonNode.Parse("""{"data":[{"itemId":"b","processId":"2","command":"server","cwd":"/"}],"nextCursor":"next"}""")
            : JsonNode.Parse("""{"data":[{"itemId":"a","processId":"1","command":"watch","cwd":"/"}]}"""));
        var (tasks, empty) = await CodexAgent.ReadBackgroundAsync(Read, CancellationToken.None);
        Assert.IsFalse(empty);
        Assert.AreEqual(2, tasks!.Count);
        Assert.AreEqual("1:a1", (string?)tasks[0]?["_meta"]?["gorynych/nativeId"]);
        var disappeared = await CodexAgent.ReadBackgroundAsync((_, _) => Task.FromResult<JsonNode?>(JsonNode.Parse("""{"data":[]}""")), CancellationToken.None);
        Assert.AreEqual(0, disappeared.Tasks!.Count);
        Assert.IsTrue(disappeared.Empty);
        calls = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CodexAgent.ReadBackgroundAsync((cursor, ct) => ++calls == 1
            ? Task.FromResult<JsonNode?>(JsonNode.Parse("""{"data":[],"nextCursor":"next"}"""))
            : Task.FromResult<JsonNode?>(JsonNode.Parse("""{"data":[{}]}""")), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => CodexAgent.ReadBackgroundAsync(Read, cancelled.Token));
    }

    [TestMethod]
    public void BackgroundChangesAdvanceRevisionWithoutChangingForeground()
    {
        var changes = new List<JsonObject>();
        var activity = new SessionActivity("epoch", changes.Add);
        Assert.IsNull(activity.Snapshot()["background"]);
        var tasks = new JsonArray(Terminal("one"));
        activity.ReportBackground(Snapshot(tasks));
        tasks.Clear();
        Assert.AreEqual(1, activity.Snapshot()["background"]!.AsArray().Count);
        activity.ReportBackground(Snapshot(new JsonArray(Terminal("one"))));
        activity.ReportBackground(Snapshot(new JsonArray()));
        activity.ReportBackground(null);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, changes.Select(s => (long)s["revision"]!).ToArray());
        Assert.IsTrue(changes.All(s => State(s) == "idle"));
        Assert.AreEqual(0, changes[1]["background"]!.AsArray().Count);
        Assert.IsNull(changes[2]["background"]);
    }

    [TestMethod]
    public void OverlappingWork_AndAuthoritativeBlocking_AreAggregateAndIdempotent()
    {
        var changes = new List<JsonObject>();
        var activity = new SessionActivity("epoch", changes.Add);
        activity.Begin();
        activity.Begin();
        activity.End();
        Assert.AreEqual("running", State(activity.Snapshot()), "one response cannot end another source's work");
        activity.Report("requires_action");
        activity.Report("requires_action");
        activity.Report("running");
        activity.End();
        Assert.AreEqual("running", State(activity.Snapshot()), "native work outlives a local prompt");
        activity.Report("idle");
        CollectionAssert.AreEqual(new[] { "running", "requires_action", "running", "idle" }, changes.Select(State).ToArray());
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, changes.Select(s => (long)s["revision"]!).ToArray());
    }

    sealed class HeldAgent
    {
        public readonly SemaphoreSlim Release = new(0);
        public readonly Channel<string> Started = Channel.CreateUnbounded<string>();
        public ScriptedAdapter Adapter { get; }
        public HeldAgent()
        {
            Adapter = new ScriptedAdapter("activity", async (agent, request, ct) =>
            {
                switch (request.Method)
                {
                    case AcpMethods.Agent.Initialize: return ScriptedAgent.Init(list: true, resume: true, load: true);
                    case AcpMethods.Agent.SessionNew: return new JsonObject { ["sessionId"] = "activity-1" };
                    case AcpMethods.Agent.SessionList:
                        return new JsonObject { ["sessions"] = new JsonArray(new JsonObject { ["sessionId"] = "activity-1", ["cwd"] = Path.GetTempPath() }) };
                    case AcpMethods.Agent.SessionLoad:
                    case AcpMethods.Agent.SessionResume: return new JsonObject();
                    case AcpMethods.Agent.SessionPrompt:
                        var text = ((TextContent)request.ParamsAs<PromptRequest>().Prompt[0]).Text;
                        await Started.Writer.WriteAsync(text, ct);
                        await Release.WaitAsync(ct);
                        if (text == "fail") throw new JsonRpcException(JsonRpcException.InternalError, "deliberate failure");
                        return new JsonObject { ["stopReason"] = "end_turn" };
                    default: throw new JsonRpcException(JsonRpcException.MethodNotFound, request.Method);
                }
            });
        }
    }

    static async Task<string> NewAsync(TestClient client)
    {
        var response = await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
            new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() });
        Assert.AreEqual("idle", State(response!["_meta"]![GorynychProtocol.MetaSessionState]!));
        return (string)response["sessionId"]!;
    }

    static Channel<JsonObject> Observe(TestClient client)
    {
        var states = Channel.CreateUnbounded<JsonObject>();
        var previous = client.Rpc.NotificationHandler!;
        client.Rpc.NotificationHandler = async (method, p) =>
        {
            if (method == GorynychProtocol.MethodSessionState) await states.Writer.WriteAsync(p!.DeepClone().AsObject());
            await previous(method, p);
        };
        return states;
    }

    static async Task<JsonObject> Next(Channel<JsonObject> states, string expected)
    {
        var snapshot = await states.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.AreEqual(expected, State(snapshot));
        return snapshot;
    }

    [TestMethod]
    public async Task BackgroundOnlyUpdatesNotifyUnopenedObserverAndAreNotJournaled()
    {
        var agent = new HeldAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent.Adapter);
        await using var owner = await TestClient.ConnectAsync(conductor);
        await using var observer = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(owner);
        var states = Observe(observer);
        agent.Release.Release();
        await owner.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "persist" }] });
        await Next(states, "running");
        await Next(states, "idle");
        async Task Send(JsonNode? tasks) => await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaBackgroundWork] = tasks } } });
        await Send(new JsonArray(Terminal("a")));
        var busy = await Next(states, "idle");
        Assert.AreEqual(1, busy["background"]!.AsArray().Count);
        var listing = await observer.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject());
        Assert.AreEqual(1, listing!["sessions"]![0]!["_meta"]![GorynychProtocol.MetaSessionState]!["background"]!.AsArray().Count);
        await Send(new JsonArray());
        var empty = await Next(states, "idle");
        Assert.AreEqual((long)busy["revision"]! + 1, (long)empty["revision"]!);
        Assert.AreEqual(0, empty["background"]!.AsArray().Count);
        Assert.IsFalse(conductor.Feed(id).Journal.ReadAll().Any(u => u["_meta"]?[GorynychProtocol.MetaBackgroundWork] is not null));
    }

    [TestMethod]
    public async Task BackgroundMetadataDoesNotSwallowOtherLiveInformation()
    {
        var agent = new HeldAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent.Adapter);
        await using var owner = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(owner);
        var states = Observe(owner);
        await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["title"] = "Updated title",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(Terminal("task")),
                    [GorynychProtocol.MetaForegroundState] = "running", [GorynychProtocol.MetaAgentTurn] = "started" } } });
        var first = await Next(states, "idle");
        Assert.AreEqual(1, first["background"]!.AsArray().Count);
        var second = await Next(states, "running");
        Assert.AreEqual(1, second["background"]!.AsArray().Count);
        await LinkHarness.Until(() => conductor.Feed(id).Users == 3, "background, native and agent-turn pins");
        await LinkHarness.Until(() => owner.Snapshot().Any(update => (string?)update["title"] == "Updated title"), "mixed update forwarded");
        var title = owner.Snapshot().Last(update => (string?)update["title"] == "Updated title");
        Assert.IsNull(title["_meta"]?[GorynychProtocol.MetaBackgroundWork]);
    }

    [TestMethod]
    public async Task OnlyNativeProcessExitReleasesUnknownBackgroundPin()
    {
        var agent = new HeldAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent.Adapter);
        await using var owner = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(owner);
        var states = Observe(owner);
        async Task Send(JsonObject meta) => await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = meta } });
        await Send(new JsonObject { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(Terminal("task")) });
        await Next(states, "idle");
        await Send(new JsonObject { [GorynychProtocol.MetaBackgroundWork] = null });
        Assert.IsNull((await Next(states, "idle"))["background"]);
        Assert.AreEqual(1, conductor.Feed(id).Users, "a partial or failed observation cannot release the pin");
        await Send(new JsonObject { [GorynychProtocol.MetaBackgroundWork] = null, [GorynychProtocol.MetaBackgroundProcessExited] = true });
        await LinkHarness.Until(() => conductor.Feed(id).Users == 0, "native process exit releases pin");
        Assert.IsNull(conductor.Feed(id).Activity.Snapshot()["background"]);
    }

    [TestMethod]
    public async Task LostAdapterConnectionMakesBackgroundUnknownWithoutDroppingItsPin()
    {
        var agent = new HeldAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent.Adapter);
        await using var owner = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(owner);
        var states = Observe(owner);
        await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(Terminal("task")) } } });
        await Next(states, "idle");
        await agent.Adapter.Current.Rpc.DisposeAsync();
        Assert.IsNull((await Next(states, "idle"))["background"]);
        Assert.AreEqual(1, conductor.Feed(id).Users);
    }

    [TestMethod]
    public async Task ReplayedTurnMetadataIsNotARealTimeBackgroundObservation()
    {
        var agent = new HeldAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent.Adapter);
        await using var owner = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(owner);
        var feed = conductor.Feed(id);
        feed.BeginReplay(16);
        await conductor.OnAgentNotificationAsync(conductor.Agent("activity"), AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "user_message_chunk",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "historical" },
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(Terminal("old")) } } });
        Assert.AreEqual(1, feed.EndReplay().Updates.Count);
        Assert.IsNull(feed.Activity.Snapshot()["background"]);
        Assert.AreEqual(0, feed.Users);
    }

    [TestMethod]
    public async Task ConsecutivePrompts_NotifyAnUnopenedObserver_AndFailureReturnsIdle()
    {
        var agent = new HeldAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent.Adapter);
        await using var owner = await TestClient.ConnectAsync(conductor);
        await using var observer = await TestClient.ConnectAsync(conductor);
        Assert.AreEqual(1, (int)owner.Initialized!.AgentCapabilities!.Meta![GorynychProtocol.MetaSessionState]!);
        var states = Observe(observer);
        var id = await NewAsync(owner);
        long revision = 0;
        foreach (var text in new[] { "first", "second", "fail" })
        {
            var pending = owner.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = id,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) });
            await agent.Started.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            var running = await Next(states, "running");
            Assert.IsFalse(pending.IsCompleted, "v1 prompt stays pending while work runs");
            Assert.IsTrue((long)running["revision"]! > revision);
            var list = await observer.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject());
            Assert.AreEqual("running", State(list!["sessions"]![0]!["_meta"]![GorynychProtocol.MetaSessionState]!));
            agent.Release.Release();
            if (text == "fail") await Assert.ThrowsAsync<JsonRpcException>(async () => await pending);
            else await pending.WaitAsync(Wait);
            revision = (long)(await Next(states, "idle"))["revision"]!;
        }
    }

    [TestMethod]
    public async Task ServiceOpinion_AndOwnAgentTurn_ReachAnObserverWithoutAPrompt()
    {
        var agent = new HeldAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent.Adapter);
        await using var owner = await TestClient.ConnectAsync(conductor);
        await using var observer = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(owner);
        var states = Observe(observer);
        var opinion = observer.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject { ["sessionId"] = id, ["text"] = "opinion" });
        await agent.Started.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        await Next(states, "running");
        agent.Release.Release();
        await opinion.WaitAsync(Wait);
        await Next(states, "idle");
        async Task Mark(string mark) => await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgentTurn] = mark } } });
        await Mark("started");
        await Next(states, "running");
        var resumed = await observer.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath() });
        Assert.AreEqual("running", State(resumed!["_meta"]![GorynychProtocol.MetaSessionState]!));
        var refreshed = await Next(states, "running");
        Assert.AreEqual((long)resumed["_meta"]![GorynychProtocol.MetaSessionState]!["revision"]!, (long)refreshed["revision"]!);
        await Mark("ended");
        await Next(states, "idle");
        var loaded = await observer.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() });
        Assert.AreEqual("idle", State(loaded!["_meta"]![GorynychProtocol.MetaSessionState]!));
    }

    [TestMethod]
    public async Task NativeCodexStatus_ReportsWholeWorkflowBlocking_AndIsNotJournaled()
    {
        var fake = new FakeAppServer();
        var adapter = new CodexAdapter(new CodexAdapterOptions
        {
            Launch = fake.StartAsync,
            ClientVersion = "test",
            FindHolders = _ => new Dictionary<string, string>(),
            LocksDirectory = Path.Combine(TestDirs.New(), "no-native-locks"),
        });
        await using var conductor = TestDirs.Conductor(TestDirs.New(), adapter);
        await using var owner = await TestClient.ConnectAsync(conductor);
        var created = await owner.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
            new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() });
        var id = (string)created!["sessionId"]!;
        var states = Observe(owner);
        async Task Status(string type, params string[] flags) => await fake.Rpc!.SendNotificationAsync("thread/status/changed",
            new JsonObject { ["threadId"] = id, ["status"] = new JsonObject { ["type"] = type,
                ["activeFlags"] = new JsonArray(flags.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()) } });
        await Status("active");
        await Next(states, "running");
        // A request alone does not prove that every foreground operation is blocked.
        conductor.Feed(id).BeginAsking();
        Assert.AreEqual("running", State(conductor.Feed(id).Activity.Snapshot()));
        conductor.Feed(id).EndAsking();
        await Status("active", "waitingOnApproval");
        await Next(states, "requires_action");
        await Status("active");
        await Next(states, "running");
        await Status("idle");
        await Next(states, "idle");
        Assert.IsFalse(conductor.Feed(id).Journal.ReadAll().Any(u => u["_meta"]?[GorynychProtocol.MetaForegroundState] is not null));
    }

    [TestMethod]
    public async Task AgentExit_ClearsItsOwnWork_AndANewConductorUsesANewEpoch()
    {
        var agent = new HeldAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent.Adapter);
        await using var owner = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(owner);
        var states = Observe(owner);
        var feed = conductor.Feed(id);
        var epoch = (string)feed.Activity.Snapshot()["epoch"]!;
        feed.BeginAgentTurn();
        await Next(states, "running");
        await agent.Adapter.Current!.Rpc.DisposeAsync();
        await Next(states, "idle");
        await using var next = TestDirs.Conductor(TestDirs.New(), new HeldAgent().Adapter);
        Assert.AreNotEqual(epoch, (string)next.Feed(id).Activity.Snapshot()["epoch"]!);
    }
    [TestMethod]
    public async Task BridgeRestart_RefreshesTheEpochWithoutClosingTheClientConnection()
    {
        var data = TestDirs.New();
        var service = TestDirs.Conductor(data, new TurnAgent("turns"));
        await using var bridge = new LinkHarness(service);
        var states = Channel.CreateUnbounded<JsonObject>();
        var previous = bridge.Zed.NotificationHandler!;
        bridge.Zed.NotificationHandler = async (method, parameters) =>
        {
            if (method == GorynychProtocol.MethodSessionState)
                await states.Writer.WriteAsync(parameters!.DeepClone().AsObject());
            await previous(method, parameters);
        };
        await bridge.InitializeAsync().WaitAsync(Wait);
        var id = await bridge.NewSessionAsync().WaitAsync(Wait);
        await bridge.PromptAsync(id, "one").WaitAsync(Wait);
        await Next(states, "running");
        var initial = await Next(states, "idle");
        bridge.Service = null;
        await service.DisposeAsync();
        bridge.Break();
        await LinkHarness.Until(() => bridge.Log().Contains("lost the connection"), "bridge observes service loss");
        await using var next = TestDirs.Conductor(data, new TurnAgent("turns"));
        bridge.Service = next;
        var refreshed = await Next(states, "idle");
        Assert.AreNotEqual((string)initial["epoch"]!, (string)refreshed["epoch"]!);
        Assert.AreEqual(id, (string)refreshed["sessionId"]!);
        Assert.IsFalse(bridge.Zed.IsClosed, "stdio remains intact across the service restart");
        await bridge.PromptAsync(id, "two").WaitAsync(Wait);
        await Next(states, "running");
        await Next(states, "idle");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task HostRestart_RestoresBackgroundAfterReplayEviction(int unavailable)
    {
        var data = TestDirs.New();
        var agent = new HeldAgent();
        var hosts = new InProcessHosts(_ => agent.Adapter) { BufferBytes = 1000 };
        var service = hosts.Conductor(data, agent.Adapter);
        hosts.Service = service;
        await using var owner = await TestClient.ConnectAsync(service);
        var id = await NewAsync(owner);
        agent.Release.Release();
        await owner.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "persist" }] });
        var before = (long)hosts["activity"].Host.Report()["seq"]!;
        await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
            { [unavailable == 3 ? GorynychProtocol.MetaBackgroundKnown : GorynychProtocol.MetaBackgroundWork] = new JsonArray(Terminal("task")) } } });
        for (var i = 0; i < 2; i++)
            await agent.Adapter.Current.Update(id, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = new string('x', 4000) } });
        await LinkHarness.Until(() => (long)hosts["activity"].Host.Report()["seq"]! >= before + 3, "background marker evicted");
        await LinkHarness.Until(() => service.Feed(id).Activity.Snapshot()[unavailable == 3 ? "backgroundKnown" : "background"] is JsonArray { Count: 1 }, "background observed");
        if (unavailable is 1 or 2)
        {
            await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
                new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaBackgroundWork] = unavailable == 1 ? null : new JsonArray(new JsonObject { ["type"] = "terminal" }) } } });
            await LinkHarness.Until(() => service.Feed(id).Activity.Snapshot()["background"] is null, "background unavailable");
        }
        await HostTests.RestartAsync(service);
        await HostTests.GoneAsync(service, null, hosts);
        await using var next = HostTests.Next(data, hosts, null, agent.Adapter);
        try
        {
            await using var again = await TestClient.ConnectAsync(next);
            var response = await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume,
                new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath() }).WaitAsync(Wait);
            var snapshot = response!["_meta"]![GorynychProtocol.MetaSessionState]!;
            Assert.AreEqual("idle", State(snapshot));
            if (unavailable == 3)
            {
                Assert.IsNull(snapshot["background"]);
                Assert.AreEqual(1, snapshot["backgroundKnown"]!.AsArray().Count);
            }
            else if (unavailable != 0) Assert.IsNull(snapshot["background"]);
            else Assert.AreEqual(1, snapshot["background"]!.AsArray().Count);
            Assert.IsTrue(next.Feed(id).Users > 0);
        }
        finally { await hosts["activity"].Agent.DisposeAsync(); }
    }

    [TestMethod]
    public async Task HostReplayKeepsMixedInformationButNeverReplaysBackgroundSnapshot()
    {
        var data = TestDirs.New();
        var agent = new HeldAgent();
        var hosts = new InProcessHosts(_ => agent.Adapter);
        var service = hosts.Conductor(data, agent.Adapter);
        hosts.Service = service;
        await using var owner = await TestClient.ConnectAsync(service);
        var id = await NewAsync(owner);
        agent.Release.Release();
        await owner.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "persist" }] });
        await HostTests.RestartAsync(service);
        await HostTests.GoneAsync(service, null, hosts);
        var host = hosts["activity"].Host;
        var before = (long)host.Report()["seq"]!;
        async Task Send(JsonObject update) => await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = update });
        await Send(new JsonObject { ["sessionUpdate"] = "session_info_update", ["title"] = "While disconnected", ["_meta"] = new JsonObject
        { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(Terminal("old")) } });
        await Send(new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
        { [GorynychProtocol.MetaBackgroundWork] = new JsonArray() } });
        await LinkHarness.Until(() => (long)host.Report()["seq"]! >= before + 2, "host retained both observations");
        await using var next = HostTests.Next(data, hosts, null, agent.Adapter);
        try
        {
            await using var again = await TestClient.ConnectAsync(next);
            var response = await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume,
                new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath() }).WaitAsync(Wait);
            Assert.AreEqual(0, response!["_meta"]![GorynychProtocol.MetaSessionState]!["background"]!.AsArray().Count);
            var journal = next.Feed(id).Journal.ReadAll();
            Assert.IsTrue(journal.Any(update => (string?)update["title"] == "While disconnected"));
            Assert.IsFalse(journal.Any(update => update["_meta"]?[GorynychProtocol.MetaBackgroundWork] is not null));
        }
        finally { await hosts["activity"].Agent.DisposeAsync(); }
    }

    [TestMethod]
    public async Task MixedActivityCarrierMatchesHostSnapshotAfterReattachment()
    {
        var data = TestDirs.New();
        var agent = new HeldAgent();
        var hosts = new InProcessHosts(_ => agent.Adapter);
        var service = hosts.Conductor(data, agent.Adapter);
        hosts.Service = service;
        await using var owner = await TestClient.ConnectAsync(service);
        var id = await NewAsync(owner);
        agent.Release.Release();
        await owner.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "persist" }] });
        await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
            {
                [GorynychProtocol.MetaBackgroundWork] = new JsonArray(Terminal("task")),
                [GorynychProtocol.MetaForegroundState] = "idle",
                [GorynychProtocol.MetaAgentTurn] = "started",
            } } });
        await LinkHarness.Until(() => service.Feed(id).AgentTurnRunning && service.Feed(id).Activity.Snapshot()["background"] is JsonArray { Count: 1 }, "mixed live activity");
        Assert.AreEqual("running", State(service.Feed(id).Activity.Snapshot()));
        Assert.IsFalse(service.Feed(id).Journal.ReadAll().Any(update => (string?)update["_meta"]?[GorynychProtocol.MetaAgentTurn] == "started"));
        var kept = hosts["activity"].Host.Report()["sessionActivities"]![id]!;
        Assert.IsTrue((bool)kept["agentTurn"]!);
        Assert.AreEqual("idle", (string?)kept["foreground"]);
        Assert.AreEqual(1, kept["background"]!.AsArray().Count);
        await HostTests.RestartAsync(service);
        await HostTests.GoneAsync(service, null, hosts);
        await using var next = HostTests.Next(data, hosts, null, agent.Adapter);
        try
        {
            await using var again = await TestClient.ConnectAsync(next);
            var response = await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume,
                new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath() }).WaitAsync(Wait);
            var state = response!["_meta"]![GorynychProtocol.MetaSessionState]!;
            Assert.AreEqual("running", State(state));
            Assert.AreEqual(1, state["background"]!.AsArray().Count);
            Assert.IsTrue(next.Feed(id).AgentTurnRunning);
        }
        finally { await hosts["activity"].Agent.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostRestart_RestoresActivityAfterItsStartWasEvicted(bool native)
    {
        var data = TestDirs.New();
        var agent = new HeldAgent();
        var hosts = new InProcessHosts(_ => agent.Adapter) { BufferBytes = 1000 };
        var service = hosts.Conductor(data, agent.Adapter);
        hosts.Service = service;
        await using var owner = await TestClient.ConnectAsync(service);
        var id = await NewAsync(owner);
        agent.Release.Release();
        await owner.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "make durable" }] });
        async Task Mark(bool active) => await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update",
                ["_meta"] = native
                    ? new JsonObject { [GorynychProtocol.MetaForegroundState] = active ? "requires_action" : "idle" }
                    : new JsonObject { [GorynychProtocol.MetaAgentTurn] = active ? "started" : "ended" } } });
        var before = (long)hosts["activity"].Host.Report()["seq"]!;
        await Mark(true);
        for (var index = 0; index < 2; index++)
            await agent.Adapter.Current!.Update(id, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = new string('x', 4000) } });
        // Both large updates must be numbered before the restart: the tiny buffer
        // then retains only the last one, so the start marker has actually gone.
        await LinkHarness.Until(() => (long)hosts["activity"].Host.Report()["seq"]! >= before + 3, "start marker evicted");
        var expected = native ? "requires_action" : "running";
        await LinkHarness.Until(() => State(service.Feed(id).Activity.Snapshot()) == expected, "initial activity");
        var report = hosts["activity"].Host.Report();
        Assert.IsNotNull(report["sessionActivities"]?[id]);
        await HostTests.RestartAsync(service);
        await HostTests.GoneAsync(service, null, hosts);
        await using var next = HostTests.Next(data, hosts, null, agent.Adapter);
        try
        {
            await using var again = await TestClient.ConnectAsync(next);
            var response = await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume,
                new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath() }).WaitAsync(Wait);
            Assert.AreEqual(expected, State(response!["_meta"]![GorynychProtocol.MetaSessionState]!));
            Assert.IsTrue(next.Feed(id).TurnRunning, "native activity also prevents idle unload");
            await Mark(false);
            await LinkHarness.Until(() => State(next.Feed(id).Activity.Snapshot()) == "idle", "autonomous work ended");
        }
        finally { await hosts["activity"].Agent.DisposeAsync(); }
    }

}
