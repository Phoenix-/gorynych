using System.Text.Json.Nodes;
using System.Diagnostics;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Adapters.Codex;

namespace Gorynych.Tests;

[TestClass]
public sealed class AssignmentDiagnosticsTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PassiveDiagnosticsDoNotAttributeUnrelatedWorkAndSurviveRestart()
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        Conductor Next() => new(new ConductorOptions { DataDirectory = dir, ServiceId = "diagnostic-test",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        var service = Next(); var client = await TestClient.ConnectAsync(service);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
            var parent = await New(); var child = await New(); var peer = await New(); var outsider = await New();
            foreach (var id in new[] { parent, child, peer, outsider }) service.Store.Update(id, "kimi", r => r.Draft = false, durable: true);
            service.ReplyMailbox.Accept("a", parent, child, "one", delegation: true);
            service.ReplyMailbox.Accept("b", parent, child, "two", delegation: true);
            var generation = (string)service.ReplyMailbox.FindAssignment("a", child)!["generation"]!;
            var otherGeneration = (string)service.ReplyMailbox.FindAssignment("b", child)!["generation"]!;
            JsonObject Meta(string actor) => new() { [GorynychProtocol.MetaPeerToken] = agent.Tokens[actor] };
            JsonObject Read(string actor, string id) => new() { ["source"] = "delegation", ["assignmentId"] = id, ["_meta"] = Meta(actor) };
            async Task<JsonNode?> Call(string method, JsonObject q) => await client.Rpc.SendRequestAsync(method, q.DeepClone()).WaitAsync(Wait);
            async Task<string?> Code(string id) => (string?)(await Call(GorynychProtocol.MethodReadAssignment, Read(parent, id)))!["diagnostic"]!["reasonCode"];
            var init = await client.Rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
            Assert.AreEqual(true, (bool?)init!["agentCapabilities"]!["_meta"]![GorynychProtocol.CapAssignmentDiagnostics]);
            Assert.AreEqual("assignment_queued", await Code("a"));
            Assert.IsTrue(service.ReplyMailbox.TryMarkStarted("a", child));
            Assert.AreEqual("assignment_handoff_unconfirmed", await Code("a"));
            service.ReplyMailbox.MarkDelivered("a", child);
            Assert.AreEqual("no_correlated_execution", await Code("a"));
            var managedClients = new List<TestClient>();
            var managedRoute = new PeersServer.ServiceRoute(async _ =>
            {
                var connection = await TestClient.ConnectAsync(service, "managed-diagnostic");
                managedClients.Add(connection); return connection.Rpc;
            }, "in-process");
            try
            {
                using var http = new HttpClient();
                var mcpRead = await PeersServer.CallAsync(http, managedRoute, agent.Tokens[parent], null, "read_assignment",
                    new JsonObject { ["source"] = "delegation", ["assignment_id"] = "a" }, CancellationToken.None).WaitAsync(Wait);
                Assert.AreEqual(false, (bool?)mcpRead["isError"]);
                var body = JsonNode.Parse((string)mcpRead["content"]![0]!["text"]!)!;
                Assert.AreEqual("no_correlated_execution", (string?)body["diagnostic"]!["reasonCode"]);
                var denied = await PeersServer.CallAsync(http, managedRoute, agent.Tokens[outsider], null, "read_assignment",
                    new JsonObject { ["source"] = "delegation", ["assignment_id"] = "a" }, CancellationToken.None).WaitAsync(Wait);
                Assert.AreEqual(true, (bool?)denied["isError"]);
            }
            finally { foreach (var connection in managedClients) await connection.DisposeAsync(); }
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, Read(outsider, "a")));
            var forbidden = await Call(GorynychProtocol.MethodListAssignments, new JsonObject { ["_meta"] = Meta(outsider) });
            Assert.AreEqual(0, forbidden!["items"]!.AsArray().Count);
            var list = await Call(GorynychProtocol.MethodListAssignments, new JsonObject { ["_meta"] = Meta(parent) });
            Assert.AreEqual(2, list!["items"]!.AsArray().Count);
            Assert.IsTrue(list["items"]!.AsArray().All(x => x?["diagnostic"] is JsonObject));
            Assert.AreEqual(0, agent.Prompts.Count);
            agent.BeforePrompt = async id => { if (id == child) { entered.TrySetResult(); await release.Task.WaitAsync(Wait); } };
            var prompt = client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = child,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "unrelated user work" }) });
            await entered.Task.WaitAsync(Wait);
            await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
            { ["sessionId"] = child, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(new JsonObject { ["type"] = "terminal", ["terminalId"] = "unrelated" }) } } });
            await LinkHarness.Until(() => service.Feed(child).Activity.Snapshot()["background"] is JsonArray a && a.Count == 1, "unrelated background").WaitAsync(Wait);
            var during = await Call(GorynychProtocol.MethodReadAssignment, Read(parent, "a"));
            Assert.AreEqual("no_correlated_execution", (string?)during!["diagnostic"]!["reasonCode"]);
            Assert.AreEqual("unknown", (string?)during["diagnostic"]!["execution"]);
            Assert.AreEqual("running", (string?)during["diagnostic"]!["sessionActivity"]!["state"]);
            Assert.AreEqual("present", (string?)during["diagnostic"]!["sessionActivity"]!["background"]);
            Assert.AreEqual("assignment_queued", await Code("b"));
            release.TrySetResult(); await prompt.WaitAsync(Wait); agent.BeforePrompt = null;
            Assert.AreEqual("no_correlated_execution", await Code("a"), "idle after an unrelated turn is not an abandoned-work proof");
            await agent.Adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
            { ["sessionId"] = child, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaBackgroundWork] = new JsonArray() } } });
            await LinkHarness.Until(() => !service.Feed(child).HasBackgroundWork, "background released").WaitAsync(Wait);
            service.ReplyMailbox.Accept("wait", child, peer, "question", expectReply: true);
            var register = new JsonObject { ["source"] = "delegation", ["assignmentId"] = "a", ["generation"] = generation,
                ["expectedRevision"] = 1, ["operationId"] = "wait", ["action"] = "register", ["messageId"] = "wait", ["_meta"] = Meta(child) };
            await Call(GorynychProtocol.MethodUpdateContinuation, register);
            Assert.AreEqual("dependency_queued", await Code("a"));
            service.ReplyMailbox.MarkStarted("wait", peer);
            Assert.AreEqual("dependency_handoff_unconfirmed", await Code("a"));
            service.ReplyMailbox.MarkDelivered("wait", peer);
            Assert.AreEqual("waiting_reply", await Code("a"));
            service.ReplyMailbox.Accept("reply", peer, child, "answer", replyTo: "wait");
            Assert.AreEqual("reply_delivery_queued", await Code("a"));
            service.ReplyMailbox.MarkStarted("reply", child);
            Assert.AreEqual("reply_delivery_unconfirmed", await Code("a"));
            service.ReplyMailbox.MarkFailed("reply", child, "unknown native handoff", uncertain: true);
            Assert.AreEqual("reply_delivery_unconfirmed", await Code("a"));
            service.ReplyMailbox.Accept("c", parent, child, "three", delegation: true);
            var cGeneration = (string)service.ReplyMailbox.FindAssignment("c", child)!["generation"]!;
            service.ReplyMailbox.MarkStarted("c", child); service.ReplyMailbox.MarkDelivered("c", child);
            service.ReplyMailbox.Accept("wait2", child, peer, "second question", expectReply: true);
            service.ReplyMailbox.MarkStarted("wait2", peer); service.ReplyMailbox.MarkDelivered("wait2", peer);
            service.ReplyMailbox.Accept("reply2", peer, child, "second answer", replyTo: "wait2");
            service.ReplyMailbox.MarkStarted("reply2", child); service.ReplyMailbox.MarkDelivered("reply2", child);
            var register2 = new JsonObject { ["source"] = "delegation", ["assignmentId"] = "c", ["generation"] = cGeneration,
                ["expectedRevision"] = 1, ["operationId"] = "wait2", ["action"] = "register", ["messageId"] = "wait2", ["_meta"] = Meta(child) };
            await Call(GorynychProtocol.MethodUpdateContinuation, register2);
            Assert.AreEqual("ready_to_inspect", await Code("c"));
            service.ReplyMailbox.TransitionAssignment("c", child, 2, "complete-c", "completed", "done", generation: cGeneration);
            Assert.AreEqual("terminal_completed", await Code("c"));
            service.ReplyMailbox.Accept("d", parent, child, "four", delegation: true);
            var dGeneration = (string)service.ReplyMailbox.FindAssignment("d", child)!["generation"]!;
            service.ReplyMailbox.MarkStarted("d", child); service.ReplyMailbox.MarkDelivered("d", child);
            service.ReplyMailbox.Accept("target", child, peer, "child work", delegation: true);
            var targetGeneration = (string)service.ReplyMailbox.FindAssignment("target", child)!["generation"]!;
            var childWait = new JsonObject { ["source"] = "delegation", ["assignmentId"] = "d", ["generation"] = dGeneration,
                ["expectedRevision"] = 1, ["operationId"] = "child", ["action"] = "register", ["targetSource"] = "delegation",
                ["targetAssignmentId"] = "target", ["targetGeneration"] = targetGeneration, ["_meta"] = Meta(child) };
            await Call(GorynychProtocol.MethodUpdateContinuation, childWait);
            Assert.AreEqual("dependency_queued", await Code("d"));
            service.ReplyMailbox.MarkStarted("target", peer); service.ReplyMailbox.MarkDelivered("target", peer);
            Assert.AreEqual("waiting_child", await Code("d"));
            service.ReplyMailbox.TransitionAssignment("target", peer, 1, "block-target", "blocked", "waiting", generation: targetGeneration);
            Assert.AreEqual("dependency_needs_attention", await Code("d"));
            service.ReplyMailbox.TransitionAssignment("target", peer, 2, "finish-target", "failed", "cannot finish", generation: targetGeneration);
            Assert.AreEqual("ready_to_inspect", await Code("d"));
            service.ReplyMailbox.TransitionAssignment("d", parent, 2, "cancel-d", "cancelled", "stop", generation: dGeneration);
            service.ReplyMailbox.TransitionAssignment("b", parent, 1, "cancel", "cancelled", "stop", generation: otherGeneration);
            Assert.AreEqual("terminal_cancelled", await Code("b"));
            await client.DisposeAsync(); await service.DisposeAsync(); service = Next(); client = await TestClient.ConnectAsync(service);
            var recovered = await Call(GorynychProtocol.MethodReadAssignment, Read(parent, "a"));
            Assert.AreEqual("reply_delivery_unconfirmed", (string?)recovered!["diagnostic"]!["reasonCode"]);
            Assert.AreEqual("unavailable", (string?)recovered["diagnostic"]!["sessionActivity"]!["route"]);
            Assert.AreEqual(1, agent.Prompts.Count, "only the explicitly requested unrelated user turn was dispatched");
            service.ReplyMailbox.TransitionAssignment("a", child, 2, "block", "blocked", "waiting for a person", generation: generation);
            Assert.AreEqual("explicit_blocked", await Code("a"));
            service.ReplyMailbox.TransitionAssignment("a", child, 3, "fail", "failed", "cannot finish", generation: generation);
            Assert.AreEqual("terminal_failed", await Code("a"));
            Assert.AreEqual("terminal_cancelled", await Code("b")); Assert.AreEqual(1, agent.Prompts.Count);
        }
        finally { release.TrySetResult(); await client.DisposeAsync(); await service.DisposeAsync(); }
    }
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MutationRetriesDoNotExposeChangingDiagnostics()
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "diagnostic-mutation",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
        var parent = await New(); var child = await New(); var peer = await New();
        service.ReplyMailbox.Accept("outcome", parent, child, "work", delegation: true);
        service.ReplyMailbox.Accept("continuation", parent, child, "work", delegation: true);
        service.ReplyMailbox.Accept("wait", child, peer, "question", expectReply: true);
        JsonObject Meta() => new() { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] };
        string Generation(string id) => (string)service.ReplyMailbox.FindAssignment(id, child)!["generation"]!;
        var outcome = new JsonObject { ["source"] = "delegation", ["assignmentId"] = "outcome", ["generation"] = Generation("outcome"),
            ["expectedRevision"] = 1, ["operationId"] = "block", ["outcome"] = "blocked", ["summary"] = "waiting", ["_meta"] = Meta() };
        var continuation = new JsonObject { ["source"] = "delegation", ["assignmentId"] = "continuation", ["generation"] = Generation("continuation"),
            ["expectedRevision"] = 1, ["operationId"] = "register", ["action"] = "register", ["messageId"] = "wait", ["_meta"] = Meta() };
        async Task<JsonNode?> Call(string method, JsonObject args) => await client.Rpc.SendRequestAsync(method, args.DeepClone()).WaitAsync(Wait);
        var firstOutcome = await Call(GorynychProtocol.MethodUpdateAssignment, outcome);
        var firstContinuation = await Call(GorynychProtocol.MethodUpdateContinuation, continuation);
        Assert.IsNull(firstOutcome!["diagnostic"]); Assert.IsNull(firstContinuation!["diagnostic"]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.BeforePrompt = async id => { if (id == child) { entered.TrySetResult(); await release.Task.WaitAsync(Wait); } };
        var prompt = client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = child,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "unrelated" }) });
        try
        {
            await entered.Task.WaitAsync(Wait);
            var read = await Call(GorynychProtocol.MethodReadAssignment, new JsonObject { ["source"] = "delegation",
                ["assignmentId"] = "outcome", ["_meta"] = Meta() });
            Assert.AreEqual("running", (string?)read!["diagnostic"]!["sessionActivity"]!["state"]);
            Assert.IsTrue(JsonNode.DeepEquals(firstOutcome, await Call(GorynychProtocol.MethodUpdateAssignment, outcome)));
            Assert.IsTrue(JsonNode.DeepEquals(firstContinuation, await Call(GorynychProtocol.MethodUpdateContinuation, continuation)));
        }
        finally { release.TrySetResult(); await prompt.WaitAsync(Wait); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task HostedDelegationTerminalIsExactAndLostOnRouteReopen(bool report, bool replaceDuringTurn)
    {
        var agent = new LifecycleAgent("kimi", canDelete: true);
        var hosts = new InProcessHosts(_ => agent.Adapter);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "diagnostic-hosted",
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(child, "kimi", r => { r.ParentSessionId = parent; r.ReportToParent = report; });
        service.Store.Update(parent, "kimi", r => r.Archived = true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.BeforePrompt = async id => { if (id == child) { entered.TrySetResult(); await release.Task.WaitAsync(Wait); } };
        JsonObject Auth() => new() { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] };
        async Task<JsonNode?> Read(string id) => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadAssignment,
            new JsonObject { ["source"] = "delegation", ["assignmentId"] = id, ["_meta"] = Auth() }).WaitAsync(Wait);
        try
        {
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "exact work",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent], [GorynychProtocol.MetaMessageRequestId] = "exact" } }).WaitAsync(Wait);
            await entered.Task.WaitAsync(Wait);
            service.ReplyMailbox.Accept("other", parent, child, "other work", delegation: true);
            service.ReplyMailbox.MarkStarted("other", child); service.ReplyMailbox.MarkDelivered("other", child);
            Assert.AreEqual("unknown", (string?)(await Read("exact"))!["diagnostic"]!["initialTurn"]);
            Assert.AreEqual("unknown", (string?)(await Read("other"))!["diagnostic"]!["initialTurn"]);
            if (replaceDuringTurn)
            {
                var inFlightRpc = service.Agents["kimi"].RouteOf(child)!.Rpc;
                service.Agents["kimi"].MarkClosed(child); service.Agents["kimi"].MarkLive(child, inFlightRpc);
            }
            release.TrySetResult();
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("exact", parent)?["delivery"] == "delivered", "exact delivery").WaitAsync(Wait);
            if (replaceDuringTurn)
            {
                await LinkHarness.Until(() => service.Feed(child).TurnGate.CurrentCount == 1, "mailbox turn released").WaitAsync(Wait);
                Assert.AreEqual("unknown", (string?)(await Read("exact"))!["diagnostic"]!["initialTurn"]);
                return;
            }
            await LinkHarness.Until(() => service.Feed(child).HasInitialAssignmentTurn("exact",
                (string)service.ReplyMailbox.FindAssignment("exact", parent)!["generation"]!, service.Agents["kimi"].RouteOf(child)!.Rpc), "exact response evidence").WaitAsync(Wait);
            Assert.AreEqual("terminal_observed", (string?)(await Read("exact"))!["diagnostic"]!["initialTurn"]);
            Assert.AreEqual("unknown", (string?)(await Read("other"))!["diagnostic"]!["initialTurn"]);
            var original = service.ReplyMailbox.FindAssignment("exact", parent)!;
            Assert.IsFalse(service.Feed(child).HasInitialAssignmentTurn("exact", Guid.NewGuid().ToString("N"), service.Agents["kimi"].RouteOf(child)!.Rpc));
            var rpc = service.Agents["kimi"].RouteOf(child)!.Rpc;
            service.Agents["kimi"].MarkClosed(child); service.Agents["kimi"].MarkLive(child, rpc);
            Assert.AreEqual("unknown", (string?)(await Read("exact"))!["diagnostic"]!["initialTurn"], "same RPC reopen cannot revive the old route epoch");
            Assert.AreEqual("delivered", (string?)original["delivery"]);
            if (!report) Assert.AreEqual(0, service.ReportStorage.List(child).Items.Count);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task HostedRpcFailureDoesNotBecomeTerminalObserved()
    {
        var agent = new LifecycleAgent("kimi", canDelete: true); var hosts = new InProcessHosts(_ => agent.Adapter);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "diagnostic-failed",
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        hosts.Service = service; await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(child, "kimi", r => { r.ParentSessionId = parent; r.ReportToParent = false; });
        agent.BeforePrompt = id => id == child ? Task.FromException(new JsonRpcException(JsonRpcException.InternalError, "native failure")) : Task.CompletedTask;
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "fails",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent], [GorynychProtocol.MetaMessageRequestId] = "failed" } }).WaitAsync(Wait);
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("failed", parent)?["delivery"] == "failed", "failed native delivery").WaitAsync(Wait);
        var read = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadAssignment, new JsonObject { ["source"] = "delegation", ["assignmentId"] = "failed",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } }).WaitAsync(Wait);
        Assert.AreEqual("unknown", (string?)read!["diagnostic"]!["initialTurn"]);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task HostedCodexStrictLoadedIdleRequiresLaterLiveObservation(int variant)
    {
        var fake = new FakeAppServer();
        var adapter = new CodexAdapter(new CodexAdapterOptions { Launch = fake.StartAsync, ClientVersion = "test",
            FindHolders = _ => new Dictionary<string, string>(), LocksDirectory = Path.Combine(TestDirs.New(), "no-native-locks") });
        var hosts = new InProcessHosts(_ => adapter); var data = TestDirs.New();
        var service = hosts.Conductor(data, adapter); hosts.Service = service;
        var client = await TestClient.ConnectAsync(service);
        var responseArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (variant == 3) service.AfterMailboxResponseForTests = async () => { responseArrived.TrySetResult(); await publish.Task.WaitAsync(Wait); };
        try
        {
            var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
            service.Store.Update(child, "codex", r => { r.ParentSessionId = parent; r.ReportToParent = false; });
            service.Store.Update(parent, "codex", r => r.Archived = true);
            var token = service.Store.Get(parent)!.PeerToken;
            async Task<JsonNode?> Read() => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadAssignment,
                new JsonObject { ["source"] = "delegation", ["assignmentId"] = "strict-idle", ["_meta"] = new JsonObject
                    { [GorynychProtocol.MetaPeerToken] = token } }).WaitAsync(Wait);
            async Task Status(string type, string[]? flags = null)
            {
                var route = service.Agents["codex"].RouteOf(child)!; var prior = service.Feed(child).HostCursor(route.HostId!);
                await fake.Rpc!.SendNotificationAsync("thread/status/changed", new JsonObject { ["threadId"] = child,
                    ["status"] = new JsonObject { ["type"] = type, ["activeFlags"] = new JsonArray((flags ?? []).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) } });
                await LinkHarness.Until(() => service.Feed(child).HostCursor(route.HostId!) > prior, "native status observed").WaitAsync(Wait);
            }
            await Status("idle"); // precedes the initial turn and must not carry over
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "assignment",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token, [GorynychProtocol.MetaMessageRequestId] = "strict-idle" } }).WaitAsync(Wait);
            if (variant == 3)
            {
                await responseArrived.Task.WaitAsync(Wait);
                service.Feed(child).BeginAsking(); service.Feed(child).EndAsking();
                publish.TrySetResult();
            }
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("strict-idle", parent)?["delivery"] == "delivered", "delegation delivered").WaitAsync(Wait);
            await LinkHarness.Until(() => service.Feed(child).TurnGate.CurrentCount == 1, "turn finished").WaitAsync(Wait);
            Assert.AreEqual("terminal_observed", (string?)(await Read())!["diagnostic"]!["initialTurn"]);
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"]);
            Assert.AreEqual("no_correlated_execution", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
            await service.Agents["codex"].RouteOf(child)!.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay,
                new JsonObject { ["after"] = new JsonObject { [child] = 0 } }).WaitAsync(Wait);
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"], "old idle replay cannot become later evidence");
            var historyRoute = service.Agents["codex"].RouteOf(child)!.Rpc;
            service.Feed(child).BeginHistoryImport(historyRoute);
            try { await Status("idle"); }
            finally { service.Feed(child).EndHistoryImport(historyRoute); }
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"], "history-import updates are not live evidence");
            await Status("idle");
            if (variant == 3)
            {
                Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"], "completed permission between receipt and publication must not be forgotten");
                Assert.AreEqual("no_correlated_execution", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
                Assert.AreEqual("terminal_observed", (string?)(await Read())!["diagnostic"]!["initialTurn"]);
                return;
            }
            Assert.AreEqual("loaded_idle_observed", (string?)(await Read())!["diagnostic"]!["foregroundIdle"]);
            Assert.AreEqual("open_without_recorded_continuation", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["execution"]);
            if (variant is 8 or 9)
            {
                var generation = (string)service.ReplyMailbox.FindAssignment("strict-idle", child)!["generation"]!;
                JsonObject? target = null;
                if (variant == 8)
                {
                    service.ReplyMailbox.Accept("race-wait", child, parent, "waiting", expectReply: true);
                    service.ReplyMailbox.MarkStarted("race-wait", parent); service.ReplyMailbox.MarkDelivered("race-wait", parent);
                    target = service.ReplyMailbox.ContinuationWait("race-wait", child)!; target["kind"] = "reply_wait";
                    target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
                }
                service.AfterDiagnosticProofForTests = () =>
                {
                    service.AfterDiagnosticProofForTests = null;
                    if (variant == 8) service.ReplyMailbox.UpdateContinuation("strict-idle", child, generation, 1, "race-register", "register", target);
                    else service.ReplyMailbox.TransitionAssignment("strict-idle", parent, 1, "race-cancel", "cancelled", "stop", generation: generation);
                };
                var stale = await Read();
                Assert.AreEqual(1L, (long?)stale!["revision"], "read retains its earlier durable row");
                Assert.AreEqual("no_correlated_execution", (string?)stale["diagnostic"]!["reasonCode"], "stale snapshot cannot yield the actionable reason");
                Assert.AreEqual(variant == 8 ? "waiting_reply" : "terminal_cancelled", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
                Assert.AreEqual(1, fake.All("turn/start").Count(c => (string?)c["threadId"] == child));
                return;
            }
            if (variant == 6)
            {
                var generation = (string)service.ReplyMailbox.FindAssignment("strict-idle", child)!["generation"]!;
                service.ReplyMailbox.Accept("question", child, parent, "waiting", expectReply: true);
                service.ReplyMailbox.MarkStarted("question", parent); service.ReplyMailbox.MarkDelivered("question", parent);
                async Task<JsonNode?> Change(string op, string action, long rev)
                {
                    var args = new JsonObject { ["source"] = "delegation", ["assignmentId"] = "strict-idle", ["generation"] = generation,
                        ["expectedRevision"] = rev, ["operationId"] = op, ["action"] = action,
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(child)!.PeerToken } };
                    if (action == "register") args["messageId"] = "question";
                    return await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, args).WaitAsync(Wait);
                }
                var registered = await Change("register", "register", 1);
                Assert.IsNull(registered!["diagnostic"]);
                Assert.AreEqual("waiting_reply", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
                service.ReplyMailbox.Accept("answer", parent, child, "answer", replyTo: "question");
                Assert.AreEqual("reply_delivery_queued", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
                service.ReplyMailbox.MarkStarted("answer", child); service.ReplyMailbox.MarkDelivered("answer", child);
                Assert.AreEqual("ready_to_inspect", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
                await Change("withdraw", "withdraw", 2);
                Assert.AreEqual("open_without_recorded_continuation", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
                service.ReplyMailbox.Accept("other", parent, child, "other", delegation: true);
                service.ReplyMailbox.MarkStarted("other", child); service.ReplyMailbox.MarkDelivered("other", child);
                var other = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadAssignment, new JsonObject { ["source"] = "delegation",
                    ["assignmentId"] = "other", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } }).WaitAsync(Wait);
                Assert.AreEqual("no_correlated_execution", (string?)other!["diagnostic"]!["reasonCode"]);
                var list = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListAssignments, new JsonObject { ["_meta"] = new JsonObject
                    { [GorynychProtocol.MetaPeerToken] = token } }).WaitAsync(Wait);
                Assert.AreEqual("open_without_recorded_continuation", (string?)list!["items"]!.AsArray().Single(x => (string?)x?["assignmentId"] == "strict-idle")!["diagnostic"]!["reasonCode"]);
                Assert.AreEqual(1, fake.All("turn/start").Count(c => (string?)c["threadId"] == child), "diagnostic and continuation reads do not issue native input");
                service.ReplyMailbox.TransitionAssignment("strict-idle", child, 3, "blocked", "blocked", "waiting for user", generation: generation);
                Assert.AreEqual("explicit_blocked", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
                return;
            }
            if (variant == 7)
            {
                var generation = (string)service.ReplyMailbox.FindAssignment("strict-idle", parent)!["generation"]!;
                service.ReplyMailbox.TransitionAssignment("strict-idle", parent, 1, "cancel", "cancelled", "stop", generation: generation);
                Assert.AreEqual("terminal_cancelled", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
                return;
            }
            if (variant == 1)
            {
                var history = service.Agents["codex"].RouteOf(child)!.Rpc;
                service.Feed(child).BeginHistoryImport(history);
                try { await Status("notLoaded"); }
                finally { service.Feed(child).EndHistoryImport(history); }
                await Status("idle");
                Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"], "unload during history and late same-link idle cannot restore evidence");
                return;
            }
            if (variant == 4)
            {
                await Status("systemError"); await Status("idle");
                Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"], "system error revokes the old terminal correlation");
                return;
            }
            if (variant == 5)
            {
                var route = service.Agents["codex"].RouteOf(child)!; var prior = service.Feed(child).HostCursor(route.HostId!);
                await fake.Rpc!.SendNotificationAsync("thread/closed", new JsonObject { ["threadId"] = child });
                await LinkHarness.Until(() => service.Feed(child).HostCursor(route.HostId!) > prior, "native closed observed").WaitAsync(Wait);
                await Status("idle");
                Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"], "closed thread cannot restore its old terminal correlation");
                return;
            }
            if (variant == 2)
            {
                service.Feed(child).BeginAsking(); service.Feed(child).EndAsking();
                await Status("idle");
                Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"], "permission intervention is a conflicting event");
                return;
            }
            var hold = fake.HoldTurnStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unrelated = client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = child,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "unrelated user turn" }) });
            try
            {
                await LinkHarness.Until(() => fake.All("turn/start").Count(c => (string?)c["threadId"] == child) == 2, "unrelated native input").WaitAsync(Wait);
                Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"]);
            }
            finally { hold.TrySetResult(); await unrelated.WaitAsync(Wait); fake.HoldTurnStart = null; }
            await Status("idle");
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"], "later user turn blocks correlation");
            await Status("active");
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"]);
            await Status("idle"); Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"], "later unrelated activity invalidates this terminal's eligibility");
            service.Feed(child).BeginAsking(); service.Feed(child).EndAsking();
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"]);
            var host = service.Agents["codex"]; var rpc = host.RouteOf(child)!.Rpc; host.MarkClosed(child); host.MarkLive(child, rpc);
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"]);
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["initialTurn"]);
            Assert.AreEqual("open", (string?)(await Read())!["status"]);
            Assert.AreEqual(2, fake.All("turn/start").Count(c => (string?)c["threadId"] == child));
            await HostTests.RestartAsync(service); await client.DisposeAsync(); await HostTests.GoneAsync(service, null, hosts);
            service = HostTests.Next(data, hosts, null, adapter); client = await TestClient.ConnectAsync(service);
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["foregroundIdle"]);
            Assert.AreEqual("unknown", (string?)(await Read())!["diagnostic"]!["initialTurn"]);
            Assert.AreEqual("no_correlated_execution", (string?)(await Read())!["diagnostic"]!["reasonCode"]);
            Assert.AreEqual(2, fake.All("turn/start").Count(c => (string?)c["threadId"] == child));
        }
        finally { publish.TrySetResult(); await client.DisposeAsync(); await service.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task FailedOrUncertainDeliveryDoesNotImplyExecution(bool uncertain)
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "diagnostic-failure",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
        var parent = await New(); var child = await New();
        service.ReplyMailbox.Accept("failed", parent, child, "work", delegation: true);
        service.ReplyMailbox.MarkStarted("failed", child); service.ReplyMailbox.MarkFailed("failed", child, "transport", uncertain: uncertain);
        var result = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadAssignment, new JsonObject { ["source"] = "delegation",
            ["assignmentId"] = "failed", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } }).WaitAsync(Wait);
        Assert.AreEqual(uncertain ? "assignment_handoff_unconfirmed" : "assignment_delivery_failed", (string?)result!["diagnostic"]!["reasonCode"]);
        Assert.AreEqual("unknown", (string?)result["diagnostic"]!["execution"]); Assert.AreEqual("open", (string?)result["status"]);
        Assert.AreEqual(0, agent.Prompts.Count);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task RealManagedMcpReturnsOnlyParticipantDiagnostics()
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "diagnostic-mcp",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
        var parent = await New(); var child = await New();
        service.ReplyMailbox.Accept("mcp-diagnostic", parent, child, "work", delegation: true);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gorynych.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        var exe = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Gorynych.Service", "bin"), TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories).First();
        var pipe = "diagnostic-mcp-" + Guid.NewGuid().ToString("N"); using var stop = new CancellationTokenSource();
        var listening = PipeListener.RunAsync(service, pipe, stop.Token);
        var start = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "peers", "--pipe", pipe, "--token", agent.Tokens[parent] }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start test MCP process");
        try
        {
            await using var mcp = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, "diagnostic-mcp-test"); mcp.Start();
            var initialized = await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() }).WaitAsync(Wait);
            Assert.IsNotNull(initialized!["capabilities"]!["tools"]);
            var tools = await mcp.SendRequestAsync("tools/list", new JsonObject()).WaitAsync(Wait);
            var tool = tools!["tools"]!.AsArray().Single(t => (string?)t?["name"] == "read_assignment");
            StringAssert.Contains((string?)tool!["description"], "diagnostic");
            async Task<JsonNode?> Call(JsonObject args) => await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "read_assignment", ["arguments"] = args }).WaitAsync(Wait);
            var answer = await Call(new JsonObject { ["source"] = "delegation", ["assignment_id"] = "mcp-diagnostic" });
            Assert.AreEqual(false, (bool?)answer!["isError"]);
            var body = JsonNode.Parse((string)answer["content"]![0]!["text"]!)!;
            Assert.AreEqual("assignment_queued", (string?)body["diagnostic"]!["reasonCode"]);
            var spoof = await Call(new JsonObject { ["source"] = "delegation", ["assignment_id"] = "mcp-diagnostic", ["actor"] = child });
            Assert.AreEqual(true, (bool?)spoof!["isError"]); Assert.AreEqual(0, agent.Prompts.Count);
        }
        finally
        {
            try { await process.WaitForExitAsync().WaitAsync(Wait); }
            finally { stop.Cancel(); await listening; }
        }
    }

}
