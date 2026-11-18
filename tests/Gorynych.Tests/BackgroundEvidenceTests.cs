using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class BackgroundEvidenceTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    static JsonObject TaskItem(string id) => new() { ["type"] = "_claude_task",
        ["_meta"] = new JsonObject { ["gorynych/nativeTaskId"] = id, ["gorynych/nativeTaskType"] = "shell" } };

    [TestMethod]
    public void OnlyBoundedCompleteIdentifiedSnapshotsBecomeEvidence()
    {
        const string stream = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var parsed = BackgroundEvidence.FromSnapshot("host", 1, stream, new JsonArray(TaskItem("a"), TaskItem("b")));
        Assert.IsNotNull(parsed); CollectionAssert.AreEqual(new[] { "a", "b" }, parsed.Items);
        Assert.IsNull(BackgroundEvidence.FromSnapshot("host", 2, stream, null));
        Assert.IsNull(BackgroundEvidence.FromSnapshot("host", 2, "bad", new JsonArray()));
        Assert.IsNull(BackgroundEvidence.FromSnapshot("host", 2, stream, new JsonArray(TaskItem("a"), TaskItem("a"))));
        Assert.IsNull(BackgroundEvidence.FromSnapshot("host", 2, stream, new JsonArray(new JsonObject { ["type"] = "terminal", ["terminalId"] = "x" })));
        Assert.IsNull(BackgroundEvidence.FromSnapshot("host", 2, stream, new JsonArray(Enumerable.Range(0, 257).Select(i => (JsonNode)TaskItem(i.ToString())).ToArray())));
        Assert.IsNotNull(BackgroundEvidence.Parse(parsed.ToJson()));
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task HostReattachRetainsOrderedEvidenceAndReplacementStreamIsDistinct()
    {
        var data = TestDirs.New(); var agent = new TurnAgent("claude"); var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent); hosts.Service = service; var client = await TestClient.ConnectAsync(service);
        try
        {
            var sid = await HostTests.NewSession(client); service.Store.Update(sid, "claude", r => r.Draft = false, durable: true);
            const string firstStream = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string nextStream = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Task Update(JsonNode? background, string? stream, bool exited = false) => agent.AgentOf(sid).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
                new JsonObject { ["sessionId"] = sid, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                    { [GorynychProtocol.MetaBackgroundWork] = background, [GorynychProtocol.MetaBackgroundCompleteStream] = stream,
                      [GorynychProtocol.MetaBackgroundProcessExited] = exited } } });
            BackgroundEvidence? Current() => service.CurrentBackgroundEvidence(sid);
            await Update(new JsonArray(TaskItem("a"), TaskItem("b")), firstStream);
            await LinkHarness.Until(() => Current()?.Items.Length == 2, "initial evidence").WaitAsync(Wait);
            var firstRevision = Current()!.Revision;
            await agent.AgentOf(sid).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
            { ["sessionId"] = sid, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(TaskItem("a"), TaskItem("b")),
                  [GorynychProtocol.MetaBackgroundCompleteStream] = firstStream,
                  [GorynychProtocol.MetaHostBackgroundEvidence] = new JsonObject { ["hostId"] = "forged", ["stream"] = nextStream,
                      ["revision"] = firstRevision + 100, ["items"] = new JsonArray("forged") } } } });
            await LinkHarness.Until(() => Current()?.Revision > firstRevision, "host replaced forged metadata").WaitAsync(Wait);
            Assert.AreEqual(firstStream, Current()!.Stream); CollectionAssert.AreEqual(new[] { "a", "b" }, Current()!.Items);
            var prior = Current()!; var oldRoute = service.Agents["claude"].RouteOf(sid)!;
            Assert.AreEqual(hosts["claude"].Host.HostId, prior.HostId);
            var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodHostState,
                new JsonObject { ["sessionId"] = sid }).WaitAsync(Wait));
            Assert.AreEqual(JsonRpcException.MethodNotFound, refused.Code);
            var legacy = new HostLink(oldRoute.Rpc, new InitializeResponse { ProtocolVersion = 1 }) { HostId = prior.HostId };
            Assert.IsNull(service.Feed(sid).BackgroundEvidenceFor(legacy), "an old host has no evidence capability");
            var foreign = new HostLink(oldRoute.Rpc, oldRoute.Info) { HostId = "different-host" };
            Assert.IsNull(service.Feed(sid).BackgroundEvidenceFor(foreign), "evidence belongs to the actual host");
            // An adapter cannot forge the host's inventory notification.
            await agent.AgentOf(sid).Rpc.SendNotificationAsync(GorynychProtocol.MethodHostState, new JsonObject
            { ["sessionActivities"] = new JsonObject { [sid] = new JsonObject { ["backgroundEvidence"] = new JsonObject
                { ["hostId"] = prior.HostId, ["stream"] = nextStream, ["revision"] = prior.Revision + 100,
                  ["items"] = new JsonArray("forged") } } } });
            var hostBeforeBarrier = (long)hosts["claude"].Host.Report()["seq"]!;
            await agent.AgentOf(sid).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
            { ["sessionId"] = sid, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["title"] = "barrier" } });
            await LinkHarness.Until(() => (long)hosts["claude"].Host.Report()["seq"]! > hostBeforeBarrier, "forged notification processed").WaitAsync(Wait);
            Assert.AreEqual(firstStream, Current()!.Stream);
            await HostTests.RestartAsync(service); await client.DisposeAsync(); await HostTests.GoneAsync(service, null, hosts);
            Assert.IsNull(service.Feed(sid).BackgroundEvidenceFor(oldRoute));
            await Update(new JsonArray(TaskItem("b")), firstStream);
            var host = hosts["claude"].Host;
            await LinkHarness.Until(() => (long?)host.Report()["sessionActivities"]?[sid]?["backgroundEvidence"]?["revision"] > prior.Revision,
                "host retained evidence without conductor").WaitAsync(Wait);
            service = HostTests.Next(data, hosts, null, agent); client = await TestClient.ConnectAsync(service);
            await LinkHarness.Until(() => Current()?.Items.SequenceEqual(new[] { "b" }) == true, "reattached evidence").WaitAsync(Wait);
            var restored = Current()!; Assert.AreEqual(prior.HostId, restored.HostId); Assert.AreEqual(firstStream, restored.Stream);
            Assert.IsTrue(restored.Revision > prior.Revision); Assert.AreEqual(1, hosts.Launches);
            service.Feed(sid).ObserveBackgroundEvidence(service.Agents["claude"].RouteOf(sid)!.Rpc, prior.ToJson());
            Assert.AreEqual(restored.Revision, Current()!.Revision, "older snapshots cannot rewind the same stream");
            await Update(new JsonArray(TaskItem("a")), nextStream);
            await LinkHarness.Until(() => Current()?.Stream == nextStream, "replacement stream").WaitAsync(Wait);
            Assert.AreNotEqual(restored.Stream, Current()!.Stream);
            service.Feed(sid).ObserveBackgroundEvidence(service.Agents["claude"].RouteOf(sid)!.Rpc, restored.ToJson());
            Assert.AreEqual(nextStream, Current()!.Stream, "an older stream cannot replace a newer host observation");
            await Update(null, nextStream, exited: true);
            await LinkHarness.Until(() => Current() is null, "exit revokes evidence").WaitAsync(Wait);
            service.Feed(sid).ObserveBackgroundEvidence(service.Agents["claude"].RouteOf(sid)!.Rpc, restored.ToJson());
            Assert.IsNull(Current(), "an older snapshot cannot revive evidence after an unknown observation");
            await Update(new JsonArray(TaskItem("a")), nextStream);
            await LinkHarness.Until(() => Current() is not null, "new complete evidence").WaitAsync(Wait);
            await agent.AgentOf(sid).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
            { ["sessionId"] = sid, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaBackgroundKnown] = new JsonArray() } } });
            await LinkHarness.Until(() => Current() is null, "partial snapshot revokes evidence").WaitAsync(Wait);
            var beforeMalformed = (long)host.Report()["seq"]!;
            await Update(new JsonArray(new JsonObject { ["type"] = "_claude_task" }), nextStream);
            await LinkHarness.Until(() => (long)host.Report()["seq"]! > beforeMalformed, "malformed observation processed").WaitAsync(Wait);
            Assert.IsNull(Current());
            var beforeLegacy = (long)host.Report()["seq"]!;
            await Update(new JsonArray(TaskItem("a")), null);
            await LinkHarness.Until(() => (long)host.Report()["seq"]! > beforeLegacy, "legacy stream processed").WaitAsync(Wait);
            Assert.IsNull(Current()); Assert.AreEqual(0, agent.Prompts.Count);
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); hosts.BreakAll(); }
    }
    [TestMethod]
    [DataRow("unknown")]
    [DataRow("replacement")]
    [DataRow("same")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ReattachRetainsLiveEvidenceBeforeRouteAdmission(string scenario)
    {
        var data = TestDirs.New(); var agent = new TurnAgent("claude"); var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent); hosts.Service = service; var client = await TestClient.ConnectAsync(service);
        var taking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreignWire = new DuplexLink();
        await using var foreign = new JsonRpcConnection(foreignWire.AgentEnd.Input, foreignWire.AgentEnd.Output, "foreign-evidence");
        foreign.Start();
        try
        {
            var sid = await HostTests.NewSession(client); service.Store.Update(sid, "claude", r => r.Draft = false, durable: true);
            const string first = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; const string replacement = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Task Update(JsonNode? background, string? stream) => agent.AgentOf(sid).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
                new JsonObject { ["sessionId"] = sid, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                    { [GorynychProtocol.MetaBackgroundWork] = background, [GorynychProtocol.MetaBackgroundCompleteStream] = stream } } });
            await Update(new JsonArray(TaskItem("a"), TaskItem("b")), first);
            await LinkHarness.Until(() => service.CurrentBackgroundEvidence(sid)?.Items.Length == 2, "initial evidence").WaitAsync(Wait);
            var before = service.CurrentBackgroundEvidence(sid)!; var oldRoute = service.Agents["claude"].RouteOf(sid)!;
            await HostTests.RestartAsync(service); await client.DisposeAsync(); await HostTests.GoneAsync(service, null, hosts);
            hosts.TakingHost = async _ => { taking.TrySetResult(); await release.Task.WaitAsync(Wait); };
            service = HostTests.Next(data, hosts, null, agent); client = await TestClient.ConnectAsync(service);
            await taking.Task.WaitAsync(Wait);
            Assert.IsNull(service.Agents["claude"].RouteOf(sid), "snapshot received while route is withheld");
            var feed = service.Feed(sid);
            Assert.AreEqual(before.Revision, feed.ObservedBackgroundRevision, "retained snapshot was received first");
            Assert.IsNull(service.CurrentBackgroundEvidence(sid), "an unadmitted host never authorizes a read");
            var forged = new BackgroundEvidence(before.HostId, replacement, before.Revision + 1000, ["foreign"]).ToJson();
            service.Agents["claude"].ObserveBackgroundEvidence(sid, foreign, forged, feed);
            service.Agents["claude"].ObserveBackgroundEvidence(sid, oldRoute.Rpc, forged, feed);
            Assert.AreEqual(before.Revision, feed.ObservedBackgroundRevision, "foreign and closed links cannot change pending evidence");
            if (scenario == "unknown") await Update(null, first);
            else await Update(new JsonArray(TaskItem("b")), scenario == "replacement" ? replacement : first);
            await LinkHarness.Until(() => feed.ObservedBackgroundRevision > before.Revision, "new live evidence processed before route").WaitAsync(Wait);
            var advanced = feed.ObservedBackgroundRevision;
            Assert.IsNull(service.CurrentBackgroundEvidence(sid));
            release.TrySetResult();
            await LinkHarness.Until(() => service.Agents["claude"].RouteOf(sid) is not null, "host route admitted").WaitAsync(Wait);
            var observed = service.CurrentBackgroundEvidence(sid);
            if (scenario == "unknown") Assert.IsNull(observed, "newer unknown defeats the earlier complete snapshot");
            else
            {
                Assert.IsNotNull(observed); Assert.AreEqual(advanced, observed.Revision);
                Assert.AreEqual(scenario == "replacement" ? replacement : first, observed.Stream);
                CollectionAssert.AreEqual(new[] { "b" }, observed.Items);
            }
            service.Agents["claude"].ObserveBackgroundEvidence(sid, foreign, forged, feed);
            service.Agents["claude"].ObserveBackgroundEvidence(sid, oldRoute.Rpc, forged, feed);
            Assert.AreEqual(advanced, feed.ObservedBackgroundRevision, "late foreign or closed links cannot overwrite the active observation");
            var actualRoute = service.Agents["claude"].RouteOf(sid)!;
            service.Agents["claude"].ObserveBackgroundEvidence(sid, actualRoute.Rpc, before.ToJson(), feed);
            Assert.AreEqual(advanced, feed.ObservedBackgroundRevision, "old snapshots cannot rewind the admitted stream");
            if (scenario == "unknown") Assert.IsNull(service.CurrentBackgroundEvidence(sid));
            Assert.AreEqual(0, agent.Prompts.Count);
        }
        finally { release.TrySetResult(); hosts.TakingHost = null; await client.DisposeAsync(); await service.DisposeAsync(); hosts.BreakAll(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task SharedHostRouteRemovalDoesNotReviveOldObservation(bool pin)
    {
        var data = TestDirs.New(); var agent = new TurnAgent("claude"); var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent); hosts.Service = service; var client = await TestClient.ConnectAsync(service);
        try
        {
            var sid = await HostTests.NewSession(client); const string stream = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            Task Update() => agent.AgentOf(sid).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
                new JsonObject { ["sessionId"] = sid, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                    { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(TaskItem("a")), [GorynychProtocol.MetaBackgroundCompleteStream] = stream } } });
            await Update();
            await LinkHarness.Until(() => service.CurrentBackgroundEvidence(sid) is not null, "initial observation").WaitAsync(Wait);
            var old = service.CurrentBackgroundEvidence(sid)!; var host = service.Agents["claude"]; var link = host.RouteOf(sid)!;
            if (pin) host.Pin(sid, link); else host.MarkClosed(sid);
            Assert.IsNull(service.CurrentBackgroundEvidence(sid));
            host.MarkLive(sid, link.Rpc);
            Assert.AreSame(link, host.RouteOf(sid));
            Assert.IsNull(service.CurrentBackgroundEvidence(sid), "same RPC reopening cannot resurrect the old observation");
            host.ObserveBackgroundEvidence(sid, link.Rpc, old.ToJson(), service.Feed(sid));
            Assert.IsNull(service.CurrentBackgroundEvidence(sid), "equal revision remains invalidated");
            await Update();
            await LinkHarness.Until(() => service.CurrentBackgroundEvidence(sid)?.Revision > old.Revision, "fresh observation").WaitAsync(Wait);
            Assert.AreEqual(stream, service.CurrentBackgroundEvidence(sid)!.Stream);
            Assert.AreEqual(0, agent.Prompts.Count);
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); hosts.BreakAll(); }
    }

    [TestMethod]
    [DataRow("spawn")]
    [DataRow("delegation")]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task PassiveTaskContinuationUsesExactLiveEvidenceAcrossRestart(string ownerSource)
    {
        var data = TestDirs.New(); var agent = new TurnAgent("claude"); var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent); hosts.Service = service; var client = await TestClient.ConnectAsync(service);
        try
        {
            var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client); var outsider = await HostTests.NewSession(client);
            foreach (var id in new[] { parent, child, outsider }) service.Store.Update(id, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => r.ParentSessionId = parent, durable: true);
            if (ownerSource == "spawn") service.Store.CommitSpawnAssignment(child, "owner");
            else
            {
                service.ReplyMailbox.Accept("owner", parent, child, "work", delegation: true);
                Assert.IsTrue(service.ReplyMailbox.TryMarkStarted("owner", child));
                service.ReplyMailbox.MarkDelivered("owner", child);
            }
            JsonObject Owner() => ownerSource == "spawn" ? service.Store.FindSpawnAssignment("owner", child)! : service.ReplyMailbox.FindAssignment("owner", child)!;
            var ownerGeneration = (string)Owner()["generation"]!;
            const string stream = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; const string replacement = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Task Update(JsonNode? background, string? nativeStream) => agent.AgentOf(child).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
                new JsonObject { ["sessionId"] = child, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                    { [GorynychProtocol.MetaBackgroundWork] = background, [GorynychProtocol.MetaBackgroundCompleteStream] = nativeStream } } });
            JsonObject Meta(string actor) => new() { [GorynychProtocol.MetaPeerToken] = service.Store.Get(actor)!.PeerToken };
            string selectedReference = "";
            JsonObject Register(string actor, long revision = 1, string op = "register") => new() { ["source"] = ownerSource, ["assignmentId"] = "owner",
                ["generation"] = ownerGeneration, ["expectedRevision"] = revision, ["operationId"] = op, ["action"] = "register", ["backgroundRef"] = selectedReference, ["_meta"] = Meta(actor) };
            JsonObject Read(string actor) => new() { ["source"] = ownerSource, ["assignmentId"] = "owner", ["_meta"] = Meta(actor) };
            async Task<JsonNode?> Call(string method, JsonObject args) => await client.Rpc.SendRequestAsync(method, args.DeepClone()).WaitAsync(Wait);
            await Update(new JsonArray(TaskItem("a"), TaskItem("b")), stream);
            await LinkHarness.Until(() => service.CurrentBackgroundEvidence(child)?.Items.Length == 2, "two native tasks observed").WaitAsync(Wait);
            var own = await Call(GorynychProtocol.MethodListBackgroundTasks, new JsonObject { ["_meta"] = Meta(child) });
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, own!["items"]!.AsArray().Select(x => (string)x!["taskId"]!).ToArray());
            selectedReference = (string)own["items"]!.AsArray().Single(x => (string?)x?["taskId"] == "a")!["reference"]!;
            var oldReference = selectedReference;
            var other = await Call(GorynychProtocol.MethodListBackgroundTasks, new JsonObject { ["_meta"] = Meta(outsider) });
            Assert.AreEqual("unknown", (string?)other!["state"]); Assert.AreEqual(0, other["items"]!.AsArray().Count);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodListBackgroundTasks, new JsonObject { ["sessionId"] = child, ["_meta"] = Meta(outsider) }));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Register(parent)));
            var spoof = Register(child); spoof["hostId"] = "forged";
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, spoof));
            var original = Register(child);
            var registered = await Call(GorynychProtocol.MethodUpdateContinuation, original);
            Assert.AreEqual("waiting", (string?)registered!["continuation"]!["state"]);
            if (ownerSource == "delegation") Assert.AreEqual("waiting_background", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Read(child)))!["diagnostic"]!["reasonCode"]);
            var saved = Owner()["continuation"]!;
            var first = service.CurrentBackgroundEvidence(child)!;
            Assert.AreEqual(first.HostId, (string?)saved["hostId"]); Assert.AreEqual(first.Stream, (string?)saved["stream"]);
            Assert.AreEqual(first.Revision, (long?)saved["revision"]); Assert.AreEqual("a", (string?)saved["taskId"]); Assert.AreEqual("_claude_task", (string?)saved["nativeType"]);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, Read(outsider)));
            await HostTests.RestartAsync(service); await client.DisposeAsync(); await HostTests.GoneAsync(service, null, hosts);
            await Update(new JsonArray(TaskItem("b")), stream);
            var host = hosts["claude"].Host;
            await LinkHarness.Until(() => (long?)host.Report()["sessionActivities"]?[child]?["backgroundEvidence"]?["revision"] > first.Revision, "offline newer observation").WaitAsync(Wait);
            service = HostTests.Next(data, hosts, null, agent); client = await TestClient.ConnectAsync(service);
            await LinkHarness.Until(() => service.CurrentBackgroundEvidence(child)?.Revision > first.Revision, "reattached observation").WaitAsync(Wait);
            var ready = await Call(GorynychProtocol.MethodReadAssignment, Read(parent));
            Assert.AreEqual("ready", (string?)ready!["continuation"]!["state"]); Assert.AreEqual("open", (string?)ready["status"]); Assert.AreEqual(2L, (long?)ready["revision"]);
            if (ownerSource == "delegation") Assert.AreEqual("ready_to_inspect", (string?)ready["diagnostic"]!["reasonCode"]);
            Assert.AreEqual("b", service.CurrentBackgroundEvidence(child)!.Items.Single(), "unrelated task remains active");
            await Update(null, stream);
            await LinkHarness.Until(() => service.CurrentBackgroundEvidence(child) is null, "unknown observation").WaitAsync(Wait);
            Assert.AreEqual("unknown", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Read(child)))!["continuation"]!["state"]);
            Assert.AreEqual("unknown", (string?)(await Call(GorynychProtocol.MethodUpdateContinuation, original))!["continuation"]!["state"], "retained retry does not require current evidence");
            await Update(new JsonArray(TaskItem("a"), TaskItem("b")), replacement);
            await LinkHarness.Until(() => service.CurrentBackgroundEvidence(child)?.Stream == replacement, "replacement stream with same native ID").WaitAsync(Wait);
            Assert.AreEqual("unknown", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Read(parent)))!["continuation"]!["state"]);
            var newList = await Call(GorynychProtocol.MethodListBackgroundTasks, new JsonObject { ["_meta"] = Meta(child) });
            selectedReference = (string)newList!["items"]!.AsArray().Single(x => (string?)x?["taskId"] == "a")!["reference"]!;
            Assert.AreNotEqual(oldReference, selectedReference);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Register(child)));
            Assert.AreEqual("unknown", (string?)(await Call(GorynychProtocol.MethodUpdateContinuation, original))!["continuation"]!["state"], "exact old retry remains idempotent");
            var withdraw = new JsonObject { ["source"] = ownerSource, ["assignmentId"] = "owner", ["generation"] = ownerGeneration,
                ["expectedRevision"] = 2, ["operationId"] = "withdraw", ["action"] = "withdraw", ["_meta"] = Meta(child) };
            Assert.IsNull((await Call(GorynychProtocol.MethodUpdateContinuation, withdraw))!["continuation"]);
            var stale = Register(child, 3, "stale"); stale["backgroundRef"] = oldReference;
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, stale));
            var bare = Register(child, 3, "bare"); bare.Remove("backgroundRef"); bare["backgroundTaskId"] = "a";
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, bare));
            var observedRevision = service.CurrentBackgroundEvidence(child)!.Revision;
            await Update(new JsonArray(TaskItem("a"), TaskItem("b")), replacement);
            await LinkHarness.Until(() => service.CurrentBackgroundEvidence(child)?.Revision > observedRevision, "same-stream newer observation").WaitAsync(Wait);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Register(child, 3, "stale-revision")));
            var refreshed = await Call(GorynychProtocol.MethodListBackgroundTasks, new JsonObject { ["_meta"] = Meta(child) });
            selectedReference = (string)refreshed!["items"]!.AsArray().Single(x => (string?)x?["taskId"] == "a")!["reference"]!;
            var ownerHost = service.Agents["claude"]; var sameLink = ownerHost.RouteOf(child)!;
            service.ContinuationAfterLookupForTests = () =>
            {
                service.ContinuationAfterLookupForTests = null;
                ownerHost.MarkClosed(child); ownerHost.MarkLive(child, sameLink.Rpc);
            };
            var raced = Register(child, 3, "route-race");
            Assert.AreEqual("unknown", (string?)(await Call(GorynychProtocol.MethodUpdateContinuation, raced))!["continuation"]!["state"],
                "a route change after target lookup cannot publish stale positive evidence");
            Assert.AreEqual("unknown", (string?)(await Call(GorynychProtocol.MethodUpdateContinuation, raced))!["continuation"]!["state"],
                "the accepted operation is retryable while evidence is unknown");
            withdraw["expectedRevision"] = 4; withdraw["operationId"] = "withdraw-race";
            await Call(GorynychProtocol.MethodUpdateContinuation, withdraw);
            await Update(new JsonArray(TaskItem("a"), TaskItem("b")), replacement);
            await LinkHarness.Until(() => service.CurrentBackgroundEvidence(child)?.Items.Length == 2, "fresh observation after reopen").WaitAsync(Wait);
            var latest = await Call(GorynychProtocol.MethodListBackgroundTasks, new JsonObject { ["_meta"] = Meta(child) });
            var afterReopen = (string)latest!["items"]!.AsArray().Single(x => (string?)x?["taskId"] == "a")!["reference"]!;
            Assert.AreNotEqual(selectedReference, afterReopen);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Register(child, 5, "stale-revision")));
            selectedReference = afterReopen;
            await Call(GorynychProtocol.MethodUpdateContinuation, Register(child, 5, "new"));
            if (ownerSource == "spawn") service.Store.TransitionSpawnAssignment(child, "owner", parent, 6, "cancel", "cancelled", "stop", generation: ownerGeneration);
            else service.ReplyMailbox.TransitionAssignment("owner", parent, 6, "cancel", "cancelled", "stop", generation: ownerGeneration);
            var cancelled = await Call(GorynychProtocol.MethodReadAssignment, Read(parent));
            Assert.AreEqual("cancelled", (string?)cancelled!["status"]); Assert.IsNull(cancelled["continuation"]);
            Assert.AreEqual(2, service.CurrentBackgroundEvidence(child)!.Items.Length, "owner cancellation does not stop background work");
            Assert.AreEqual(0, agent.Prompts.Count, "no automatic turn or native dispatch");
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); hosts.BreakAll(); }
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ListedReferenceCannotBindSameTaskIdInReplacementStream()
    {
        var dir = TestDirs.New(); var agent = new TurnAgent("claude"); var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(dir, agent); hosts.Service = service; var client = await TestClient.ConnectAsync(service);
        try
        {
            var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
            service.ReplyMailbox.Accept("owner", parent, child, "work", delegation: true);
            var generation = (string)service.ReplyMailbox.FindAssignment("owner", child)!["generation"]!;
            const string oldStream = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", newStream = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Task Update(string? stream) => agent.AgentOf(child).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
                new JsonObject { ["sessionId"] = child, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                    { [GorynychProtocol.MetaBackgroundWork] = stream is null ? null : new JsonArray(TaskItem("same")), [GorynychProtocol.MetaBackgroundCompleteStream] = stream } } });
            JsonObject Meta() => new() { [GorynychProtocol.MetaPeerToken] = service.Store.Get(child)!.PeerToken };
            async Task<JsonNode?> Call(string method, JsonObject q) => await client.Rpc.SendRequestAsync(method, q.DeepClone()).WaitAsync(Wait);
            async Task<string> Reference() => (string)(await Call(GorynychProtocol.MethodListBackgroundTasks, new JsonObject { ["_meta"] = Meta() }))!["items"]![0]!["reference"]!;
            JsonObject Register(string reference, string operation = "register") => new() { ["source"] = "delegation", ["assignmentId"] = "owner",
                ["generation"] = generation, ["expectedRevision"] = 1, ["operationId"] = operation, ["action"] = "register", ["backgroundRef"] = reference, ["_meta"] = Meta() };
            await Update(oldStream); await LinkHarness.Until(() => service.CurrentBackgroundEvidence(child)?.Stream == oldStream, "old stream").WaitAsync(Wait);
            var oldReference = await Reference();
            await Update(newStream); await LinkHarness.Until(() => service.CurrentBackgroundEvidence(child)?.Stream == newStream, "replacement stream").WaitAsync(Wait);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Register(oldReference)));
            Assert.AreEqual(1L, (long?)service.ReplyMailbox.FindAssignment("owner", child)!["revision"]);
            var newReference = await Reference(); Assert.AreNotEqual(oldReference, newReference);
            var accepted = Register(newReference); Assert.AreEqual("waiting", (string?)(await Call(GorynychProtocol.MethodUpdateContinuation, accepted))!["continuation"]!["state"]);
            await Update(null); await LinkHarness.Until(() => service.CurrentBackgroundEvidence(child) is null, "evidence unavailable").WaitAsync(Wait);
            Assert.AreEqual("unknown", (string?)(await Call(GorynychProtocol.MethodUpdateContinuation, accepted))!["continuation"]!["state"]);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Register(oldReference)));
            var stored = service.ReplyMailbox.FindAssignment("owner", child)!;
            Assert.AreEqual(2L, (long?)stored["revision"]); Assert.AreEqual(newStream, (string?)stored["continuation"]!["stream"]);
            Assert.IsNull(stored["continuation"]!["selector"], "only verified server evidence is persisted in the continuation");
            Assert.AreEqual(0, agent.Prompts.Count);
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); hosts.BreakAll(); }
    }

}
