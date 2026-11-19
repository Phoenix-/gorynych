using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class AssignmentTeamDiagnosticsTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task SevenSessionTeamHasScopedPassiveReasonsAcrossRestart()
    {
        var fake = new FakeAppServer();
        var adapter = new CodexAdapter(new CodexAdapterOptions { Launch = fake.StartAsync, ClientVersion = "test",
            FindHolders = _ => new Dictionary<string, string>(), LocksDirectory = Path.Combine(TestDirs.New(), "no-native-locks") });
        var hosts = new InProcessHosts(_ => adapter); var data = TestDirs.New();
        var service = hosts.Conductor(data, adapter); hosts.Service = service;
        var client = await TestClient.ConnectAsync(service);
        try
        {
            var coordinator = await HostTests.NewSession(client);
            var members = new string[6];
            for (var i = 0; i < members.Length; i++) members[i] = await HostTests.NewSession(client);
            var reviewer1 = members[0]; var reviewer2 = members[1];
            var worker1 = members[2]; var worker2 = members[3]; var worker3 = members[4]; var worker4 = members[5];
            foreach (var id in members) service.Store.Update(id, "codex", r => { r.Draft = false; r.ParentSessionId = coordinator; r.ReportToParent = false; }, durable: true);
            service.Store.Update(coordinator, "codex", r => r.Draft = false, durable: true);
            string Token(string id) => service.Store.Get(id)!.PeerToken ?? throw new InvalidOperationException("Peer token unavailable");
            JsonObject Meta(string id) => new() { [GorynychProtocol.MetaPeerToken] = Token(id) };
            async Task<JsonNode?> Call(string method, JsonObject args) => await client.Rpc.SendRequestAsync(method, args).WaitAsync(Wait);
            async Task<JsonNode?> Read(string id) => await Call(GorynychProtocol.MethodReadAssignment,
                new JsonObject { ["source"] = "delegation", ["assignmentId"] = id, ["_meta"] = Meta(coordinator) });
            async Task<JsonNode?> List() => await Call(GorynychProtocol.MethodListAssignments, new JsonObject { ["_meta"] = Meta(coordinator) });
            string Generation(string id, string participant) => (string)service.ReplyMailbox.FindAssignment(id, participant)!["generation"]!;
            void FixtureAssignment(string id, string parent, string child)
            {
                service.ReplyMailbox.Accept(id, parent, child, "fixture work", delegation: true);
                service.ReplyMailbox.MarkStarted(id, child); service.ReplyMailbox.MarkDelivered(id, child);
            }
            void FixtureWait(string id, string from, string to)
            {
                service.ReplyMailbox.Accept(id, from, to, "fixture question", expectReply: true);
                service.ReplyMailbox.MarkStarted(id, to); service.ReplyMailbox.MarkDelivered(id, to);
            }
            async Task RegisterReply(string assignment, string actor, string wait)
            {
                await Call(GorynychProtocol.MethodUpdateContinuation, new JsonObject { ["source"] = "delegation", ["assignmentId"] = assignment,
                    ["generation"] = Generation(assignment, actor), ["expectedRevision"] = 1, ["operationId"] = "continuation-" + assignment,
                    ["action"] = "register", ["messageId"] = wait, ["_meta"] = Meta(actor) });
            }
            await Call(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = worker4, ["text"] = "only intentional native prompt",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(coordinator), [GorynychProtocol.MetaMessageRequestId] = "worker4" } });
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("worker4", coordinator)?["delivery"] == "delivered", "worker4 delivery").WaitAsync(Wait);
            await LinkHarness.Until(() => service.Feed(worker4).TurnGate.CurrentCount == 1, "worker4 terminal").WaitAsync(Wait);
            var route = service.Agents["codex"].RouteOf(worker4)!; var before = service.Feed(worker4).HostCursor(route.HostId!);
            await fake.Rpc!.SendNotificationAsync("thread/status/changed", new JsonObject { ["threadId"] = worker4,
                ["status"] = new JsonObject { ["type"] = "idle" } });
            await LinkHarness.Until(() => service.Feed(worker4).HostCursor(route.HostId!) > before, "worker4 strict idle").WaitAsync(Wait);
            FixtureAssignment("review1", coordinator, reviewer1); FixtureAssignment("review2", coordinator, reviewer2);
            FixtureAssignment("worker1", coordinator, worker1); FixtureAssignment("worker2", coordinator, worker2); FixtureAssignment("worker3", coordinator, worker3);
            FixtureWait("r1-to-r2", reviewer1, reviewer2); FixtureWait("r2-to-r1", reviewer2, reviewer1);
            await RegisterReply("review1", reviewer1, "r1-to-r2"); await RegisterReply("review2", reviewer2, "r2-to-r1");
            service.ReplyMailbox.TransitionAssignment("worker1", worker1, 1, "user-action", "blocked", "Waiting for user action", generation: Generation("worker1", worker1));
            FixtureAssignment("private-w2-w3", worker2, worker3);
            await Call(GorynychProtocol.MethodUpdateContinuation, new JsonObject { ["source"] = "delegation", ["assignmentId"] = "worker2",
                ["generation"] = Generation("worker2", worker2), ["expectedRevision"] = 1, ["operationId"] = "child-wait", ["action"] = "register",
                ["targetSource"] = "delegation", ["targetAssignmentId"] = "private-w2-w3", ["targetGeneration"] = Generation("private-w2-w3", worker2),
                ["_meta"] = Meta(worker2) });
            async Task Verify(bool fresh)
            {
                var page = await List(); var items = page!["items"]!.AsArray(); Assert.AreEqual(6, items.Count);
                var expected = new Dictionary<string, string> { ["review1"] = "waiting_reply", ["review2"] = "waiting_reply",
                    ["worker1"] = "explicit_blocked", ["worker2"] = "waiting_child", ["worker3"] = "no_correlated_execution",
                    ["worker4"] = fresh ? "no_correlated_execution" : "open_without_recorded_continuation" };
                foreach (var pair in expected)
                {
                    var item = items.Single(x => (string?)x?["assignmentId"] == pair.Key)!;
                    Assert.AreEqual(pair.Value, (string?)item["diagnostic"]!["reasonCode"], pair.Key);
                    Assert.AreEqual(pair.Value, (string?)(await Read(pair.Key))!["diagnostic"]!["reasonCode"], pair.Key);
                }
                Assert.IsFalse(items.Any(x => (string?)x?["assignmentId"] == "private-w2-w3"));
                await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Read("private-w2-w3"));
                Assert.AreEqual("blocked", (string?)(await Read("worker1"))!["status"]);
                Assert.AreEqual("r1-to-r2", (string?)(await Read("review1"))!["continuation"]!["messageId"]);
                Assert.AreEqual("r2-to-r1", (string?)(await Read("review2"))!["continuation"]!["messageId"]);
                var inputs = fake.All("turn/start");
                Assert.AreEqual(1, inputs.Length, "diagnostic reads and fixture setup do not dispatch native prompts");
                Assert.AreEqual(worker4, (string?)inputs[0]["threadId"]);
                StringAssert.Contains(inputs[0]["input"]!.ToJsonString(), "only intentional native prompt");
            }
            await Verify(false);
            await HostTests.RestartAsync(service); await client.DisposeAsync(); await HostTests.GoneAsync(service, null, hosts);
            service = HostTests.Next(data, hosts, null, adapter); client = await TestClient.ConnectAsync(service);
            await Verify(true);
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); }
    }
}
