using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class AssignmentApiTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ScopedIdentityAclPagesAndRetainedReportRetry()
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "assignment-api",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
        var parent = await New(); var child = await New(); var otherParent = await New(); var otherChild = await New();
        service.Store.Update(child, "kimi", r => r.ParentSessionId = parent);
        service.Store.CommitSpawnAssignment(child, "shared-id");
        service.ReplyMailbox.Accept("shared-id", otherParent, otherChild, "delegated work", delegation: true);
        service.ReplyMailbox.Accept("z-next", parent, child, "next work", delegation: true);
        var spawnGeneration = (string)service.Store.FindSpawnAssignment("shared-id", parent)!["generation"]!;
        var delegationGeneration = (string)service.ReplyMailbox.FindAssignment("shared-id", otherChild)!["generation"]!;
        JsonObject Args(string who, string source, string id) => new() { ["source"] = source, ["assignmentId"] = id,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[who] } };
        async Task<JsonNode?> Call(string method, JsonObject args) => await client.Rpc.SendRequestAsync(method, args.DeepClone()).WaitAsync(Wait);
        var cap = await client.Rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
        Assert.AreEqual(true, (bool?)cap!["agentCapabilities"]!["_meta"]![GorynychProtocol.CapAssignments]);
        Assert.AreEqual("spawn", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Args(parent, "spawn", "shared-id")))!["source"]);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, Args(parent, "delegation", "shared-id")));
        Assert.AreEqual("delegation", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Args(otherChild, "delegation", "shared-id")))!["source"]);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, Args(otherChild, "spawn", "shared-id")));
        var missingSource = Args(parent, "spawn", "shared-id"); missingSource.Remove("source");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, missingSource));
        var spoof = Args(parent, "spawn", "shared-id"); spoof["actor"] = child;
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, spoof));
        var tokenless = Args(parent, "spawn", "shared-id"); tokenless.Remove("_meta");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, tokenless));
        var tokenlessCancel = Args(parent, "spawn", "shared-id"); tokenlessCancel.Remove("_meta");
        tokenlessCancel["actor"] = parent; tokenlessCancel["expectedRevision"] = 1; tokenlessCancel["operationId"] = "spoof";
        tokenlessCancel["outcome"] = "cancelled"; tokenlessCancel["summary"] = "stop";
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateAssignment, tokenlessCancel));
        var spawnOutcome = Args(child, "spawn", "shared-id"); spawnOutcome["generation"] = spawnGeneration; spawnOutcome["expectedRevision"] = 1; spawnOutcome["operationId"] = "spawn-block";
        spawnOutcome["outcome"] = "blocked"; spawnOutcome["summary"] = "waiting for input";
        Assert.AreEqual("blocked", (string?)(await Call(GorynychProtocol.MethodUpdateAssignment, spawnOutcome))!["status"]);
        spawnOutcome["expectedRevision"] = 2; spawnOutcome["operationId"] = "spawn-open"; spawnOutcome["outcome"] = "open"; spawnOutcome["summary"] = "";
        Assert.AreEqual("open", (string?)(await Call(GorynychProtocol.MethodUpdateAssignment, spawnOutcome))!["status"]);
        spawnOutcome["expectedRevision"] = 3; spawnOutcome["operationId"] = "spawn-complete"; spawnOutcome["outcome"] = "completed"; spawnOutcome["summary"] = "complete without report";
        Assert.IsNull((await Call(GorynychProtocol.MethodUpdateAssignment, spawnOutcome))!["outcomeReportId"]);
        var list = new JsonObject { ["limit"] = 1, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } };
        var first = await Call(GorynychProtocol.MethodListAssignments, list);
        Assert.AreEqual("delegation", (string?)first!["items"]![0]!["source"]);
        var cursor = (string)first["nextCursor"]!;
        service.ReplyMailbox.TransitionAssignment("z-next", parent, 1, "cancel", "cancelled", "stop", generation: (string)service.ReplyMailbox.FindAssignment("z-next", parent)!["generation"]!);
        service.ReplyMailbox.Accept("later", parent, child, "new work", delegation: true);
        list["cursor"] = cursor; var second = await Call(GorynychProtocol.MethodListAssignments, list);
        Assert.AreEqual("spawn", (string?)second!["items"]![0]!["source"]);
        var stolenCursor = new JsonObject { ["cursor"] = cursor, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[otherParent] } };
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodListAssignments, stolenCursor));
        var wrongCause = service.ReportStorage.Reserve(new ReportIdentity("wrong-cause", otherChild, otherParent, ReportCause.Spawn, AssignmentId: "shared-id", AssignmentGeneration: delegationGeneration)).ReportId;
        using (var capture = service.ReportStorage.OpenCapture(wrongCause)) { capture.Append("wrong"); capture.Seal("ended"); service.ReportStorage.Commit(wrongCause, capture); }
        var legacy = service.ReportStorage.Reserve(new ReportIdentity("legacy-report", otherChild, otherParent, ReportCause.Delegation, AssignmentId: "shared-id")).ReportId;
        using (var capture = service.ReportStorage.OpenCapture(legacy)) { capture.Append("legacy remains readable"); capture.Seal("ended"); service.ReportStorage.Commit(legacy, capture); }
        var good = service.ReportStorage.Reserve(new ReportIdentity("right-cause", otherChild, otherParent, ReportCause.Delegation, AssignmentId: "shared-id", AssignmentGeneration: delegationGeneration)).ReportId;
        using (var capture = service.ReportStorage.OpenCapture(good)) { capture.Append("saved result"); capture.Seal("ended"); service.ReportStorage.Commit(good, capture); }
        JsonObject Update(string who, string report) { var q = Args(who, "delegation", "shared-id"); q["generation"] = delegationGeneration; q["expectedRevision"] = 1; q["operationId"] = "complete-op"; q["outcome"] = "completed"; q["summary"] = "done"; q["reportId"] = report; return q; }
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateAssignment, Update(otherChild, wrongCause)));
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateAssignment, Update(otherChild, legacy)));
        var legacyPage = await Call(GorynychProtocol.MethodReadReport, new JsonObject { ["reportId"] = legacy,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[otherChild] } });
        Assert.AreEqual("legacy remains readable", (string?)legacyPage!["text"]);
        var staleGeneration = Update(otherChild, good); staleGeneration["generation"] = spawnGeneration;
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateAssignment, staleGeneration));
        Assert.AreEqual("open", (string?)service.ReplyMailbox.FindAssignment("shared-id", otherChild)!["status"]);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateAssignment, Update(otherParent, good)));
        var assignmentWriter = service.ReplyMailbox.WriteFile;
        service.ReplyMailbox.WriteFile = (_, _) => throw new IOException("assignment write fault");
        try { await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateAssignment, Update(otherChild, good))); }
        finally { service.ReplyMailbox.WriteFile = assignmentWriter; }
        Assert.AreEqual("open", (string?)service.ReplyMailbox.FindAssignment("shared-id", otherChild)!["status"]);
        var completed = await Call(GorynychProtocol.MethodUpdateAssignment, Update(otherChild, good));
        Assert.AreEqual("completed", (string?)completed!["status"]); Assert.AreEqual(good, (string?)completed["outcomeReportId"]);
        Assert.IsNull(completed["operations"]); Assert.IsNull(completed["fingerprint"]); Assert.IsNull(completed["reportId"]);
        foreach (var report in new[] { wrongCause, good }) { service.ReportStorage.SetDisposition(report, "failed", "test retention"); service.ReportStorage.AcknowledgeProducer(report); }
        service.ReportStorage.Now = () => DateTimeOffset.UtcNow.AddDays(31);
        service.ReportStorage.Reserve(new ReportIdentity("eviction", child, parent, ReportCause.User));
        Assert.ThrowsExactly<KeyNotFoundException>(() => service.ReportStorage.GetReport(good));
        var retry = await Call(GorynychProtocol.MethodUpdateAssignment, Update(otherChild, good));
        Assert.AreEqual("completed", (string?)retry!["status"]); Assert.AreEqual(good, (string?)retry["outcomeReportId"]);
        var stale = Update(otherChild, good); stale["operationId"] = "new-operation";
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateAssignment, stale));
        service.Store.Update(otherChild, "kimi", r => r.Deleted = true);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, Args(otherChild, "delegation", "shared-id")));
        Assert.AreEqual("completed", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Args(otherParent, "delegation", "shared-id")))!["status"]);
    }
    [TestMethod]
    public async Task AssignmentCursorSurvivesEvictionOfPriorPage()
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "assignment-pages",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
        var parent = await New(); var a = await New(); var b = await New(); var c = await New();
        service.Store.SpawnAssignmentCapacity = 2;
        foreach (var child in new[] { a, b, c }) service.Store.Update(child, "kimi", r => r.ParentSessionId = parent);
        service.Store.CommitSpawnAssignment(a, "a"); service.Store.CommitSpawnAssignment(b, "b");
        service.Store.TransitionSpawnAssignment(a, "a", parent, 1, "cancel", "cancelled", "done", generation: (string)service.Store.Get(a)!.SpawnAssignment!["generation"]!);
        JsonObject List(string? cursor = null) { var value = new JsonObject { ["limit"] = 1, ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } }; if (cursor is not null) value["cursor"] = cursor; return value; }
        var first = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListAssignments, List()).WaitAsync(Wait);
        Assert.AreEqual("a", (string?)first!["items"]![0]!["assignmentId"]);
        var cursor = (string)first["nextCursor"]!;
        service.Store.CommitSpawnAssignment(c, "c");
        Assert.IsNull(service.Store.FindSpawnAssignment("a", parent));
        var next = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListAssignments, List(cursor)).WaitAsync(Wait);
        Assert.AreEqual("b", (string?)next!["items"]![0]!["assignmentId"]);
        var last = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListAssignments, List((string)next["nextCursor"]!)).WaitAsync(Wait);
        Assert.AreEqual("c", (string?)last!["items"]![0]!["assignmentId"]);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ReusedIdRejectsOldPublicMutationAndReportAcrossRestart()
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        Conductor Next() => new(new ConductorOptions { DataDirectory = dir, ServiceId = "assignment-generation",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        var service = Next(); var client = await TestClient.ConnectAsync(service);
        try
        {
            async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
            var parent = await New(); var child = await New();
            foreach (var id in new[] { parent, child }) service.Store.Update(id, "kimi", r => r.Draft = false, durable: true);
            service.ReplyMailbox.CapacityForTests = 1; service.ReplyMailbox.AssignmentCapacityForTests = 1;
            service.ReplyMailbox.Accept("same", parent, child, "old work", delegation: true);
            var oldGeneration = (string)service.ReplyMailbox.FindAssignment("same", child)!["generation"]!;
            string Publish(string turn, string generation)
            {
                var report = service.ReportStorage.Reserve(new ReportIdentity(turn, child, parent, ReportCause.Delegation,
                    AssignmentId: "same", AssignmentGeneration: generation)).ReportId;
                using var capture = service.ReportStorage.OpenCapture(report); capture.Append("exact"); capture.Seal("ended");
                service.ReportStorage.Commit(report, capture); return report;
            }
            var oldReport = Publish("old-turn", oldGeneration);
            service.ReplyMailbox.TransitionAssignment("same", parent, 1, "old-cancel", "cancelled", "stop", generation: oldGeneration);
            service.ReplyMailbox.Accept("filler", parent, child, "filler", delegation: true);
            service.ReplyMailbox.TransitionAssignment("filler", parent, 1, "filler-cancel", "cancelled", "stop", generation: (string)service.ReplyMailbox.FindAssignment("filler", parent)!["generation"]!);
            service.ReplyMailbox.Accept("same", parent, child, "new work", delegation: true);
            var generation = (string)service.ReplyMailbox.FindAssignment("same", child)!["generation"]!;
            Assert.AreNotEqual(oldGeneration, generation);
            JsonObject Update(string gen, string? report, string op) => new() { ["source"] = "delegation", ["assignmentId"] = "same",
                ["generation"] = gen, ["expectedRevision"] = 1, ["operationId"] = op, ["outcome"] = "completed", ["summary"] = "done",
                ["reportId"] = report, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } };
            var stale = Update(oldGeneration, oldReport, "stale");
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, stale).WaitAsync(Wait));
            var wrongReport = Update(generation, oldReport, "wrong-report");
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, wrongReport).WaitAsync(Wait));
            Assert.AreEqual("open", (string?)service.ReplyMailbox.FindAssignment("same", child)!["status"]);
            var freshReport = Publish("new-turn", generation);
            var request = Update(generation, freshReport, "fresh");
            var completed = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, request.DeepClone()).WaitAsync(Wait);
            Assert.AreEqual("completed", (string?)completed!["status"]);
            service.ReplyMailbox.MarkStarted("same", child); service.ReplyMailbox.MarkDelivered("same", child);
            await client.DisposeAsync(); await service.DisposeAsync();
            service = Next(); client = await TestClient.ConnectAsync(service);
            var retry = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, request.DeepClone()).WaitAsync(Wait);
            Assert.AreEqual(generation, (string?)retry!["generation"]); Assert.AreEqual("completed", (string?)retry["status"]);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, Update(oldGeneration, oldReport, "stale")).WaitAsync(Wait));
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); }
    }

}
