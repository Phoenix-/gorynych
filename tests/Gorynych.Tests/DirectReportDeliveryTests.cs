using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class DirectReportDeliveryTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    static Task Until(Func<bool> check, string why) => LinkHarness.Until(check, why).WaitAsync(Wait);

    [TestMethod]
    public async Task DirectSpawnUserDelegationPeerReplyAndImmutableParent()
    {
        var directory = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = directory, ServiceId = "direct-reports",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        Assert.IsNull(service.Hosts);
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var other = await HostTests.NewSession(client);
        await HostTests.Prompt(client, parent, "parent");
        service.Store.Update(parent, "kimi", r => r.Archived = true);
        var spawned = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject { ["agent"] = "kimi", ["prompt"] = "first",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } }).WaitAsync(Wait);
        var child = (string)spawned!["sessionId"]!; var assignment = (string)spawned["assignmentId"]!;
        await Until(() => service.ReportStorage.List(child).Items.Any(x => x.Identity.Cause == ReportCause.Spawn && x.ProducerAck), "direct spawn result");
        var spawn = service.ReportStorage.List(child).Items.Single(x => x.Identity.Cause == ReportCause.Spawn);
        Assert.AreEqual(assignment, spawn.Identity.AssignmentId); Assert.IsNull(spawn.Identity.HostId);
        await HostTests.Prompt(client, child, "user work");
        var user = service.ReportStorage.List(child).Items.Single(x => x.Identity.Cause == ReportCause.User);
        Assert.IsNull(user.Identity.AssignmentId); Assert.IsNull(user.Identity.HostId);
        var userPage = service.ReportStorage.Read(user.ReportId, child);
        Assert.AreEqual("ended", userPage.Outcome); StringAssert.Contains(userPage.IncompleteReason, "In-process");
        Task<JsonNode?> Message(string method, string token, string key, string target, string id) => client.Rpc.SendRequestAsync(method,
            new JsonObject { [key] = target, ["text"] = "work " + id, ["_meta"] = new JsonObject {
                [GorynychProtocol.MetaPeerToken] = token, [GorynychProtocol.MetaMessageRequestId] = id } });
        await Message(GorynychProtocol.MethodDelegateTask, agent.Tokens[parent], "sessionId", child, "direct-delegate").WaitAsync(Wait);
        await Until(() => service.ReportStorage.List(child).Items.Any(x => x.Identity.MessageId == "direct-delegate" && x.ProducerAck), "direct delegation result");
        var delegated = service.ReportStorage.List(child).Items.Single(x => x.Identity.MessageId == "direct-delegate");
        Assert.AreEqual(ReportCause.Delegation, delegated.Identity.Cause); Assert.AreEqual("direct-delegate", delegated.Identity.AssignmentId);
        await Message(GorynychProtocol.MethodSendMessage, agent.Tokens[parent], "sessionId", child, "direct-peer").WaitAsync(Wait);
        await Until(() => service.ReportStorage.List(child).Items.Any(x => x.Identity.MessageId == "direct-peer" && x.ProducerAck), "direct peer result");
        var peer = service.ReportStorage.List(child).Items.Single(x => x.Identity.MessageId == "direct-peer");
        Assert.AreEqual(ReportCause.Peer, peer.Identity.Cause); Assert.IsNull(peer.Identity.AssignmentId);
        await Message(GorynychProtocol.MethodSendMessage, agent.Tokens[child], "sessionId", parent, "direct-original").WaitAsync(Wait);
        await Message(GorynychProtocol.MethodReplyMessage, agent.Tokens[parent], "messageId", "direct-original", "direct-reply").WaitAsync(Wait);
        await Until(() => service.ReportStorage.List(child).Items.Any(x => x.Identity.MessageId == "direct-reply" && x.ProducerAck), "direct reply result");
        var reply = service.ReportStorage.List(child).Items.Single(x => x.Identity.MessageId == "direct-reply");
        Assert.AreEqual(ReportCause.Reply, reply.Identity.Cause); Assert.IsNull(reply.Identity.AssignmentId);
        service.Store.Update(child, "kimi", r => r.ParentSessionId = other);
        Task<JsonNode?> Read(string token) => client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadReport, new JsonObject {
            ["reportId"] = spawn.ReportId, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } });
        var original = await Read(agent.Tokens[parent]); Assert.AreEqual(spawn.ReportId, (string?)original!["reportId"]);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Read(agent.Tokens[other]));
        var reopened = new ReportStore(Path.Combine(directory, "reports"));
        Assert.AreEqual("pending", reopened.GetReport(spawn.ReportId).Status);
        Assert.AreEqual(parent, reopened.GetReport(spawn.ReportId).Identity.Parent);
        Assert.IsNotNull(reopened.Read(spawn.ReportId, parent).IncompleteReason);
        var quiet = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject { ["agent"] = "kimi", ["prompt"] = "quiet", ["report"] = false,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } }).WaitAsync(Wait);
        var quietId = (string)quiet!["sessionId"]!;
        await Until(() => { lock (agent.Prompts) return agent.Prompts.Any(x => x.SessionId == quietId); }, "quiet direct prompt");
        await Until(() => !service.Feed(quietId).TurnRunning, "quiet direct turn ended");
        Assert.AreEqual(0, service.ReportStorage.List(quietId).Items.Count);
    }

    [TestMethod]
    public async Task DirectPendingSurvivesReopenAndReportTurnDoesNotRecurse()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns");
        var first = TestDirs.Conductor(directory, agent); var client = await TestClient.ConnectAsync(first);
        var grandparent = await HostTests.NewSession(client); var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        first.Store.Update(grandparent, "turns", r => r.Archived = true);
        first.Store.Update(parent, "turns", r => { r.Archived = true; r.ParentSessionId = grandparent; r.ReportToParent = true; });
        first.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        await HostTests.Prompt(client, child, "direct restart work");
        var id = first.ReportStorage.List(child).Items.Single().ReportId;
        await Until(() => first.ReportStorage.GetReport(id).DispositionReason?.Contains("archived") == true, "parent blocked");
        await first.DisposeAsync(); await client.DisposeAsync();
        await using var next = TestDirs.Conductor(directory, agent);
        await using var nextClient = await TestClient.ConnectAsync(next);
        Assert.AreEqual("pending", next.ReportStorage.GetReport(id).Status);
        await nextClient.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = parent, ["archived"] = false });
        await Until(() => next.ReportStorage.GetReport(id).Status == "completed", "direct report delivered after restart");
        Assert.AreEqual(0, next.ReportStorage.List(grandparent).Items.Count, "delivery is not a new child report");
        Assert.AreEqual(1, agent.Prompts.Count(x => x == "direct restart work"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectNativeFailureKeepsOutcomeErrorAndIncompleteText(bool oversized)
    {
        var agent = new TurnAgent("turns"); await using var service = TestDirs.Conductor(TestDirs.New(), agent);
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(parent, "turns", r => r.Archived = true);
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        var nativeError = oversized ? new string('x', 600) : "native direct failure";
        agent.During = (_, _, _) => Task.FromException(new JsonRpcException(JsonRpcException.InternalError, nativeError));
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await HostTests.Prompt(client, child, "direct fail"));
        StringAssert.Contains(error.Message, oversized ? "xxx" : nativeError);
        var info = service.ReportStorage.List(child).Items.Single();
        var result = service.ReportStorage.Read(info.ReportId, child);
        Assert.IsNull(info.Identity.HostId); Assert.AreEqual("failed", result.Outcome);
        if (oversized) { StringAssert.EndsWith(result.Error, " [truncated]"); Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(result.Error!) <= 512); }
        else Assert.AreEqual(nativeError, result.Error);
        StringAssert.Contains(result.IncompleteReason, "In-process");
        Assert.AreEqual(1, agent.Prompts.Count(x => x == "direct fail"));
    }

    [TestMethod]
    public async Task UnstartedSpawnWithPromptIdRecoversFailureEndRowAfterJournalFault()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns");
        var first = TestDirs.Conductor(directory, agent); var client = await TestClient.ConnectAsync(first);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        first.Store.Update(parent, "turns", r => { r.Archived = true; r.Draft = false; });
        first.Store.Update(child, "turns", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = true; });
        first.Store.Flush();
        first.Feed(child).Journal.BeforeWrite = updates =>
        {
            if (updates.Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended")) throw new IOException("injected end journal fault");
        };
        var identity = new ReportIdentity("spawn-unstarted:assignment-with-prompt", child, parent, ReportCause.Spawn, "service-spawn-prompt", "assignment-with-prompt");
        var id = first.ReportStorage.Reserve(identity, "native prompt was refused before start").ReportId;
        first.ImportReport(id); await first.ReconcileReportInventoryAsync();
        Assert.IsFalse(first.ReportStorage.GetReport(id).EndJournaled); Assert.AreEqual(0, HostTests.EndMarks(first, child));
        Assert.AreEqual(0, agent.Prompts.Count);
        await client.DisposeAsync(); await first.DisposeAsync();
        await using var next = TestDirs.Conductor(directory, agent);
        Assert.IsNotNull(next.Store.Get(parent), "parent persists after client detach");
        Assert.IsNotNull(next.Store.Get(child), "child persists after client detach");
        Assert.IsFalse(next.Store.Get(parent)!.Draft); Assert.IsFalse(next.Store.Get(child)!.Draft);
        await Until(() => next.ReportStorage.GetReport(id).EndJournaled && HostTests.EndMarks(next, child) == 1, "failed-start end row recovered");
        var mark = HostTests.Journal(next, child).Single(x => (string?)x["_meta"]?[GorynychProtocol.MetaPromptId] == identity.PromptId);
        Assert.IsNull(mark["_meta"]?[GorynychProtocol.MetaStopReason], "no fabricated end_turn");
        Assert.AreEqual("native prompt was refused before start", (string?)mark["_meta"]?[GorynychProtocol.MetaError]?["message"]);
        var result = next.ReportStorage.Read(id, child);
        Assert.AreEqual("not_started", result.Outcome); Assert.AreEqual("native prompt was refused before start", result.Error);
        await next.ReconcileReportInventoryAsync();
        Assert.AreEqual(1, HostTests.EndMarks(next, child)); Assert.AreEqual(1, next.ReportStorage.List(child).Items.Count);
        Assert.AreEqual(0, agent.Prompts.Count);
    }

    [TestMethod]
    public async Task DirectAdmissionAndImportFaultNeverRedispatchNativePrompt()
    {
        var agent = new TurnAgent("turns"); await using var service = TestDirs.Conductor(TestDirs.New(), agent);
        Assert.IsNull(service.Hosts); await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(parent, "turns", r => r.Archived = true);
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        service.ReportStorage.WriteManifest = (_, _) => throw new IOException("reserve fault");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await HostTests.Prompt(client, child, "blocked").WaitAsync(Wait));
        Assert.IsFalse(agent.Prompts.Contains("blocked"));
        service.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
        agent.During = (_, _, _) =>
        {
            service.ReportStorage.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"status\":\"pending\"", StringComparison.Ordinal)) throw new IOException("result fault");
                AtomicFile.WriteAllText(path, json);
            };
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await HostTests.Prompt(client, child, "one native call").WaitAsync(Wait));
        StringAssert.Contains(error.Message, "durably imported");
        Assert.AreEqual(1, agent.Prompts.Count(x => x == "one native call"));
        var info = service.ReportStorage.Recoverable().Items.Single(); Assert.AreEqual("reserved", info.Status); Assert.IsNull(info.Identity.HostId);
        Assert.AreEqual(1, (int?)service.Status()["_meta"]!["gorynych/reportRecovery"]!["unconfirmed"]);
        var reopened = new ReportStore(Path.Combine((string)service.Status()["dataDirectory"]!, "reports"));
        Assert.AreEqual("reserved", reopened.GetReport(info.ReportId).Status);
        await service.ReconcileReportInventoryAsync();
        Assert.AreEqual("reserved", service.ReportStorage.GetReport(info.ReportId).Status, "a live direct producer is not declared dead");
        Assert.AreEqual(1, agent.Prompts.Count(x => x == "one native call"));
        service.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
        service.ImportReport(info.ReportId);
        Assert.IsTrue(service.ReportStorage.GetReport(info.ReportId).ProducerAck);
        var result = service.ReportStorage.Read(info.ReportId, child);
        Assert.AreEqual("ended", result.Outcome); Assert.IsNotNull(result.IncompleteReason);
        Assert.AreEqual(1, agent.Prompts.Count(x => x == "one native call"));
    }
}
