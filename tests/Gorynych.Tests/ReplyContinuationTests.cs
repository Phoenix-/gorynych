using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ReplyContinuationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task RetainedWaitReconcilesAcrossRestartAndOwnerCancellation(bool uncertain)
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        Conductor Next() => new(new ConductorOptions { DataDirectory = dir, ServiceId = "continuation-test",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        var service = Next(); var client = await TestClient.ConnectAsync(service);
        try
        {
            async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
            var parent = await New(); var child = await New(); var peer = await New();
            foreach (var id in new[] { parent, child, peer }) service.Store.Update(id, "kimi", r => r.Draft = false, durable: true);
            service.ReplyMailbox.Accept("task", parent, child, "task", delegation: true);
            service.ReplyMailbox.Accept("wait", child, peer, "question", expectReply: true);
            service.ReplyMailbox.MarkStarted("wait", peer); service.ReplyMailbox.MarkDelivered("wait", peer);
            var generation = (string)service.ReplyMailbox.FindAssignment("task", child)!["generation"]!;
            JsonObject Request(string who, string action, long revision, string operation, string? message = null)
            {
                var q = new JsonObject { ["source"] = "delegation", ["assignmentId"] = "task", ["generation"] = generation,
                    ["expectedRevision"] = revision, ["operationId"] = operation, ["action"] = action,
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[who] } };
                if (message is not null) q["messageId"] = message; return q;
            }
            async Task<JsonNode?> Call(string method, JsonObject q) => await client.Rpc.SendRequestAsync(method, q.DeepClone()).WaitAsync(TimeSpan.FromSeconds(10));
            JsonObject Read(string who) => new() { ["source"] = "delegation", ["assignmentId"] = "task",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[who] } };
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Request(parent, "register", 1, "bad", "wait")));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Request(child, "register", 1, "bad", "missing")));
            var request = Request(child, "register", 1, "register", "wait");
            var registered = await Call(GorynychProtocol.MethodUpdateContinuation, request);
            Assert.AreEqual("waiting", (string?)registered!["continuation"]!["state"]);
            Assert.AreEqual(2L, (long?)registered["revision"]);
            Assert.AreEqual(2L, (long?)(await Call(GorynychProtocol.MethodUpdateContinuation, request))!["revision"]);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Request(child, "withdraw", 1, "stale")));
            Assert.AreEqual("waiting", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Read(parent)))!["continuation"]!["state"]);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, Read(peer)));
            await client.DisposeAsync(); await service.DisposeAsync();
            service = Next(); client = await TestClient.ConnectAsync(service);
            Assert.AreEqual("waiting", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Read(child)))!["continuation"]!["state"]);
            service.ReplyMailbox.Accept("answer", peer, child, "answer", replyTo: "wait");
            service.ReplyMailbox.MarkStarted("answer", child); service.ReplyMailbox.MarkFailed("answer", child, "transport lost", uncertain: uncertain);
            await client.DisposeAsync(); await service.DisposeAsync();
            service = Next(); client = await TestClient.ConnectAsync(service);
            var afterReply = (await Call(GorynychProtocol.MethodReadAssignment, Read(parent)))!["continuation"]!;
            Assert.AreEqual("ready", (string?)afterReply["state"]);
            Assert.AreEqual("delivered", (string?)afterReply["requestDelivery"]);
            Assert.AreEqual(uncertain ? "uncertain" : "failed", (string?)afterReply["replyDelivery"]);
            var cancelled = service.ReplyMailbox.TransitionAssignment("task", parent, 2, "cancel", "cancelled", "stop", generation: generation);
            Assert.IsNull(cancelled["continuation"]);
            Assert.AreEqual("replied", (string?)service.ReplyMailbox.ListReplyWaits(child, 0, 10)["items"]![0]!["state"]);
            Assert.IsNull((await Call(GorynychProtocol.MethodReadAssignment, Read(child)))!["continuation"]);
            service.Store.Update(child, "kimi", r => r.ParentSessionId = parent, durable: true);
            service.Store.CommitSpawnAssignment(child, "spawn-task");
            var spawnGeneration = (string)service.Store.FindSpawnAssignment("spawn-task", child)!["generation"]!;
            JsonObject SpawnRequest(string action, long revision, string operation, string? message = null)
            {
                var q = new JsonObject { ["source"] = "spawn", ["assignmentId"] = "spawn-task", ["generation"] = spawnGeneration,
                    ["expectedRevision"] = revision, ["operationId"] = operation, ["action"] = action,
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } };
                if (message is not null) q["messageId"] = message; return q;
            }
            var spawn = await Call(GorynychProtocol.MethodUpdateContinuation, SpawnRequest("register", 1, "spawn-register", "wait"));
            Assert.AreEqual("ready", (string?)spawn!["continuation"]!["state"]);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, SpawnRequest("register", 2, "duplicate", "wait")));
            Assert.IsNull((await Call(GorynychProtocol.MethodUpdateContinuation, SpawnRequest("withdraw", 2, "spawn-withdraw")))!["continuation"]);

        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); }
    }
    [TestMethod]
    public void WriteFaultAndWithdrawAreAtomicAndReplayable()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path);
        store.Accept("task", "parent", "child", "work", delegation: true);
        store.Accept("wait", "child", "peer", "question", expectReply: true);
        var generation = (string)store.FindAssignment("task", "child")!["generation"]!;
        var target = store.ContinuationWait("wait", "child")!; target["kind"] = "reply_wait";
        target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
        var writer = store.WriteFile; store.WriteFile = (_, _) => throw new IOException("injected");
        Assert.ThrowsExactly<IOException>(() => store.UpdateContinuation("task", "child", generation, 1, "register", "register", target));
        Assert.IsNull(store.FindAssignment("task", "child")!["continuation"]);
        store.WriteFile = writer; store.UpdateContinuation("task", "child", generation, 1, "register", "register", target);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.UpdateContinuation("task", "parent", generation, 2, "withdraw", "withdraw", null));
        store = new MailboxStore(path);
        Assert.AreEqual(2L, (long)store.UpdateContinuation("task", "child", generation, 1, "register", "register", target)["revision"]!);
        Assert.AreEqual(3L, (long)store.UpdateContinuation("task", "child", generation, 2, "withdraw", "withdraw", null)["revision"]!);
        store = new MailboxStore(path);
        Assert.IsNull(store.FindAssignment("task", "child")!["continuation"]);
        Assert.AreEqual(3L, (long)store.UpdateContinuation("task", "child", generation, 2, "withdraw", "withdraw", null)["revision"]!);
        Assert.AreEqual("waiting", (string?)store.ContinuationWait("wait", "child")!["state"]);
    }

    [TestMethod]
    public void WaitSequenceDoesNotFollowAnEvictedAndReusedId()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, waitCapacity: 1);
        store.Accept("wait", "child", "peer", "first", expectReply: true);
        var sequence = (long)store.ContinuationWait("wait", "child")!["sequence"]!;
        store.CancelReplyWait("wait", "child"); store.MarkStarted("wait", "peer"); store.MarkDelivered("wait", "peer");
        store.Accept("other", "child", "peer", "other", expectReply: true);
        Assert.IsNull(store.ContinuationWait("wait", "child", sequence));
        store.CancelReplyWait("other", "child"); store.MarkStarted("other", "peer"); store.MarkDelivered("other", "peer");
        store.Accept("wait", "child", "peer", "new", expectReply: true);
        store = new MailboxStore(path, capacity: 1, waitCapacity: 1);
        Assert.IsNull(store.ContinuationWait("wait", "child", sequence));
        Assert.AreNotEqual(sequence, (long)store.ContinuationWait("wait", "child")!["sequence"]!);
        Assert.IsNull(store.ContinuationWait("wait", "peer"));
    }

    [TestMethod]
    public void SameStoreChecksWaitAtCommitButRetriedReceiptSurvivesEviction()
    {
        var store = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"), capacity: 3, waitCapacity: 1);
        store.Accept("owner", "parent", "child", "work", delegation: true);
        store.Accept("wait", "child", "peer", "question", expectReply: true);
        var gen = (string)store.FindAssignment("owner", "child")!["generation"]!;
        var target = store.ContinuationWait("wait", "child")!; target["kind"] = "reply_wait";
        target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
        store.CancelReplyWait("wait", "child"); store.MarkStarted("wait", "peer"); store.MarkDelivered("wait", "peer");
        store.Accept("replacement", "child", "peer", "new question", expectReply: true);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.UpdateContinuation("owner", "child", gen, 1, "old", "register", target));
        Assert.AreEqual(1L, (long)store.FindAssignment("owner", "child")!["revision"]!);
        var next = store.ContinuationWait("replacement", "child")!; next["kind"] = "reply_wait";
        next.Remove("state"); next.Remove("requestDelivery"); next.Remove("replyDelivery");
        store.UpdateContinuation("owner", "child", gen, 1, "registered", "register", next);
        store.CancelReplyWait("replacement", "child"); store.MarkStarted("replacement", "peer"); store.MarkDelivered("replacement", "peer");
        store.Accept("third", "child", "peer", "third", expectReply: true);
        Assert.AreEqual(2L, (long)store.UpdateContinuation("owner", "child", gen, 1, "registered", "register", next)["revision"]!);
        store.TransitionAssignment("owner", "parent", 2, "cancel", "cancelled", "stop", generation: gen);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.UpdateContinuation("owner", "child", gen, 3, "late", "register", next));
        Assert.IsNull(store.FindAssignment("owner", "child")!["continuation"]);
    }

    [TestMethod]
    [DataRow("delegation", "delegation")]
    [DataRow("delegation", "spawn")]
    [DataRow("spawn", "delegation")]
    [DataRow("spawn", "spawn")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ChildTargetIsScopedAndReconcilesAfterRestart(string ownerSource, string targetSource)
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        Conductor Next() => new(new ConductorOptions { DataDirectory = dir, ServiceId = "child-continuation",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        var service = Next(); var client = await TestClient.ConnectAsync(service);
        try
        {
            async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
            var parent = await New(); var child = await New(); var grandchild = await New(); var stranger = await New();
            foreach (var id in new[] { parent, child, grandchild, stranger }) service.Store.Update(id, "kimi", r => r.Draft = false, durable: true);
            service.Store.Update(child, "kimi", r => r.ParentSessionId = parent, durable: true);
            service.Store.Update(grandchild, "kimi", r => r.ParentSessionId = child, durable: true);
            if (ownerSource == "spawn") service.Store.CommitSpawnAssignment(child, "owner");
            else service.ReplyMailbox.Accept("owner", parent, child, "owner", delegation: true);
            if (targetSource == "spawn") service.Store.CommitSpawnAssignment(grandchild, "target");
            else service.ReplyMailbox.Accept("target", child, grandchild, "target", delegation: true);
            JsonObject Find(string source, string id, string actor) => source == "spawn" ? service.Store.FindSpawnAssignment(id, actor)! : service.ReplyMailbox.FindAssignment(id, actor)!;
            var generation = (string)Find(ownerSource, "owner", child)["generation"]!;
            var targetGeneration = (string)Find(targetSource, "target", child)["generation"]!;
            JsonObject Register(string who, string targetGen) => new() { ["source"] = ownerSource, ["assignmentId"] = "owner", ["generation"] = generation,
                ["expectedRevision"] = 1, ["operationId"] = "register-child", ["action"] = "register", ["targetSource"] = targetSource,
                ["targetAssignmentId"] = "target", ["targetGeneration"] = targetGen,
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[who] } };
            JsonObject Read(string who) => new() { ["source"] = ownerSource, ["assignmentId"] = "owner",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[who] } };
            async Task<JsonNode?> Call(string method, JsonObject q) => await client.Rpc.SendRequestAsync(method, q.DeepClone()).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Register(parent, targetGeneration)));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Register(child, Guid.NewGuid().ToString("N"))));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodReadAssignment, Read(stranger)));
            var request = Register(child, targetGeneration);
            Assert.AreEqual("waiting", (string?)(await Call(GorynychProtocol.MethodUpdateContinuation, request))!["continuation"]!["state"]);
            if (targetSource == "spawn") service.Store.TransitionSpawnAssignment(grandchild, "target", grandchild, 1, "block", "blocked", "waiting", generation: targetGeneration);
            else service.ReplyMailbox.TransitionAssignment("target", grandchild, 1, "block", "blocked", "waiting", generation: targetGeneration);
            var blocked = (await Call(GorynychProtocol.MethodReadAssignment, Read(parent)))!["continuation"]!;
            Assert.AreEqual("blocked", (string?)blocked["state"]); Assert.AreEqual("Child assignment is blocked", (string?)blocked["reason"]);
            await client.DisposeAsync(); await service.DisposeAsync(); service = Next(); client = await TestClient.ConnectAsync(service);
            Assert.AreEqual("blocked", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Read(child)))!["continuation"]!["state"]);
            if (targetSource == "spawn") service.Store.TransitionSpawnAssignment(grandchild, "target", grandchild, 2, "finish", "failed", "failure", generation: targetGeneration);
            else service.ReplyMailbox.TransitionAssignment("target", grandchild, 2, "finish", "failed", "failure", generation: targetGeneration);
            var ready = await Call(GorynychProtocol.MethodReadAssignment, Read(parent));
            Assert.AreEqual("ready", (string?)ready!["continuation"]!["state"]); Assert.AreEqual("failed", (string?)ready["continuation"]!["reason"]);
            Assert.AreEqual("open", (string?)ready["status"]);
            if (ownerSource == "spawn") service.Store.TransitionSpawnAssignment(child, "owner", parent, 2, "cancel", "cancelled", "stop", generation: generation);
            else service.ReplyMailbox.TransitionAssignment("owner", parent, 2, "cancel", "cancelled", "stop", generation: generation);
            Assert.IsNull((await Call(GorynychProtocol.MethodReadAssignment, Read(child)))!["continuation"]);
            Assert.AreEqual("failed", (string?)Find(targetSource, "target", child)["status"]);
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow("spawn")]
    [DataRow("delegation")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ChildGenerationCannotFollowAnEvictedAndReusedId(string ownerSource)
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        Conductor Next() => new(new ConductorOptions { DataDirectory = dir, ServiceId = "child-reuse",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        var service = Next(); var client = await TestClient.ConnectAsync(service);
        try
        {
            async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
            var parent = await New(); var child = await New(); var grandchild = await New();
            foreach (var id in new[] { parent, child, grandchild }) service.Store.Update(id, "kimi", r => r.Draft = false, durable: true);
            service.Store.Update(child, "kimi", r => r.ParentSessionId = parent, durable: true);
            service.ReplyMailbox.AssignmentCapacityForTests = ownerSource == "spawn" ? 1 : 2;
            service.ReplyMailbox.CapacityForTests = ownerSource == "spawn" ? 1 : 2;
            if (ownerSource == "spawn") service.Store.CommitSpawnAssignment(child, "owner");
            else service.ReplyMailbox.Accept("owner", parent, child, "owner", delegation: true);
            service.ReplyMailbox.Accept("target", child, grandchild, "first", delegation: true);
            var owner = ownerSource == "spawn" ? service.Store.FindSpawnAssignment("owner", child)! : service.ReplyMailbox.FindAssignment("owner", child)!;
            var ownerGen = (string)owner["generation"]!; var oldGen = (string)service.ReplyMailbox.FindAssignment("target", child)!["generation"]!;
            JsonObject Register(string generation, long revision, string operation) => new() { ["source"] = ownerSource, ["assignmentId"] = "owner", ["generation"] = ownerGen,
                ["expectedRevision"] = revision, ["operationId"] = operation, ["action"] = "register", ["targetSource"] = "delegation",
                ["targetAssignmentId"] = "target", ["targetGeneration"] = generation,
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } };
            JsonObject Read() => new() { ["source"] = ownerSource, ["assignmentId"] = "owner",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } };
            async Task<JsonNode?> Call(string method, JsonObject q) => await client.Rpc.SendRequestAsync(method, q.DeepClone()).WaitAsync(TimeSpan.FromSeconds(10));
            var original = Register(oldGen, 1, "register"); await Call(GorynychProtocol.MethodUpdateContinuation, original);
            service.ReplyMailbox.TransitionAssignment("target", child, 1, "cancel-first", "cancelled", "stop", generation: oldGen);
            service.ReplyMailbox.Accept("filler", child, grandchild, "filler", delegation: true);
            var fillerGen = (string)service.ReplyMailbox.FindAssignment("filler", child)!["generation"]!;
            service.ReplyMailbox.TransitionAssignment("filler", child, 1, "cancel-filler", "cancelled", "stop", generation: fillerGen);
            service.ReplyMailbox.Accept("target", child, grandchild, "new", delegation: true);
            var newGen = (string)service.ReplyMailbox.FindAssignment("target", child)!["generation"]!; Assert.AreNotEqual(oldGen, newGen);
            await client.DisposeAsync(); await service.DisposeAsync(); service = Next(); client = await TestClient.ConnectAsync(service);
            Assert.AreEqual("unavailable", (string?)(await Call(GorynychProtocol.MethodReadAssignment, Read()))!["continuation"]!["state"]);
            Assert.AreEqual("unavailable", (string?)(await Call(GorynychProtocol.MethodUpdateContinuation, original))!["continuation"]!["state"]);
            var withdraw = new JsonObject { ["source"] = ownerSource, ["assignmentId"] = "owner", ["generation"] = ownerGen,
                ["expectedRevision"] = 2, ["operationId"] = "withdraw", ["action"] = "withdraw", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } };
            await Call(GorynychProtocol.MethodUpdateContinuation, withdraw);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodUpdateContinuation, Register(oldGen, 3, "stale")));
            Assert.AreEqual("waiting", (string?)(await Call(GorynychProtocol.MethodUpdateContinuation, Register(newGen, 3, "fresh")))!["continuation"]!["state"]);
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow("spawn")]
    [DataRow("delegation")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TargetEvictionAndOwnerCancellationBetweenLookupAndCommit(string ownerSource)
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "continuation-race",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
        var parent = await New(); var child = await New(); var grandchild = await New();
        service.Store.Update(child, "kimi", r => r.ParentSessionId = parent, durable: true);
        service.ReplyMailbox.AssignmentCapacityForTests = ownerSource == "spawn" ? 1 : 2;
        if (ownerSource == "spawn") service.Store.CommitSpawnAssignment(child, "owner");
        else service.ReplyMailbox.Accept("owner", parent, child, "work", delegation: true);
        service.ReplyMailbox.Accept("target", child, grandchild, "target", delegation: true);
        var owner = ownerSource == "spawn" ? service.Store.FindSpawnAssignment("owner", child)! : service.ReplyMailbox.FindAssignment("owner", child)!;
        var generation = (string)owner["generation"]!; var targetGeneration = (string)service.ReplyMailbox.FindAssignment("target", child)!["generation"]!;
        JsonObject Request(string targetId, string targetGen, string op) => new() { ["source"] = ownerSource, ["assignmentId"] = "owner", ["generation"] = generation,
            ["expectedRevision"] = 1, ["operationId"] = op, ["action"] = "register", ["targetSource"] = "delegation", ["targetAssignmentId"] = targetId,
            ["targetGeneration"] = targetGen, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } };
        service.ContinuationAfterLookupForTests = () =>
        {
            service.ContinuationAfterLookupForTests = null;
            service.ReplyMailbox.TransitionAssignment("target", child, 1, "target-cancel", "cancelled", "done", generation: targetGeneration);
            service.ReplyMailbox.Accept("replacement", child, grandchild, "replacement", delegation: true);
        };
        var task = client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, Request("target", targetGeneration, "race"));
        if (ownerSource == "delegation") await Assert.ThrowsExactlyAsync<JsonRpcException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        else
        {
            var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("unavailable", (string?)result!["continuation"]!["state"]);
            // Withdraw so the next operation can race cancellation of the owner.
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, new JsonObject { ["source"] = ownerSource,
                ["assignmentId"] = "owner", ["generation"] = generation, ["expectedRevision"] = 2, ["operationId"] = "withdraw",
                ["action"] = "withdraw", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        var replacementGeneration = (string)service.ReplyMailbox.FindAssignment("replacement", child)!["generation"]!;
        var ownerRevision = ownerSource == "spawn" ? 3L : 1L;
        service.ContinuationAfterLookupForTests = () =>
        {
            service.ContinuationAfterLookupForTests = null;
            if (ownerSource == "spawn") service.Store.TransitionSpawnAssignment(child, "owner", parent, ownerRevision, "cancel-owner", "cancelled", "done", generation: generation);
            else service.ReplyMailbox.TransitionAssignment("owner", parent, ownerRevision, "cancel-owner", "cancelled", "done", generation: generation);
        };
        var next = Request("replacement", replacementGeneration, "after-cancel"); next["expectedRevision"] = ownerRevision;
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, next).WaitAsync(TimeSpan.FromSeconds(10)));
        var final = ownerSource == "spawn" ? service.Store.FindSpawnAssignment("owner", child)! : service.ReplyMailbox.FindAssignment("owner", child)!;
        Assert.AreEqual("cancelled", (string?)final["status"]); Assert.IsNull(final["continuation"]);
        Assert.AreEqual("open", (string?)service.ReplyMailbox.FindAssignment("replacement", child)!["status"]);
    }

    [TestMethod]
    [DataRow("spawn")]
    [DataRow("delegation")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ChildFinishesBetweenLookupAndRegistration(string ownerSource)
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "continuation-completion-race",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
        var parent = await New(); var child = await New(); var grandchild = await New();
        service.Store.Update(child, "kimi", r => r.ParentSessionId = parent, durable: true);
        if (ownerSource == "spawn") service.Store.CommitSpawnAssignment(child, "owner");
        else service.ReplyMailbox.Accept("owner", parent, child, "work", delegation: true);
        service.ReplyMailbox.Accept("target", child, grandchild, "target", delegation: true);
        var owner = ownerSource == "spawn" ? service.Store.FindSpawnAssignment("owner", child)! : service.ReplyMailbox.FindAssignment("owner", child)!;
        var targetGeneration = (string)service.ReplyMailbox.FindAssignment("target", child)!["generation"]!;
        service.ContinuationAfterLookupForTests = () =>
        {
            service.ContinuationAfterLookupForTests = null;
            service.ReplyMailbox.TransitionAssignment("target", grandchild, 1, "complete", "completed", "done", generation: targetGeneration);
        };
        var result = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, new JsonObject { ["source"] = ownerSource,
            ["assignmentId"] = "owner", ["generation"] = (string)owner["generation"]!, ["expectedRevision"] = 1,
            ["operationId"] = "register", ["action"] = "register", ["targetSource"] = "delegation", ["targetAssignmentId"] = "target",
            ["targetGeneration"] = targetGeneration, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("ready", (string?)result!["continuation"]!["state"]);
        Assert.AreEqual("completed", (string?)result["continuation"]!["reason"]);
        Assert.AreEqual("open", (string?)result["status"]);
    }

    [TestMethod]
    [DataRow("spawn", false)]
    [DataRow("spawn", true)]
    [DataRow("delegation", false)]
    [DataRow("delegation", true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ChildDeliveryFailureAndUncertaintyRemainVisibleAfterRestart(string source, bool uncertain)
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        Conductor Next() => new(new ConductorOptions { DataDirectory = dir, ServiceId = "continuation-child-delivery",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        var service = Next(); var client = await TestClient.ConnectAsync(service);
        try
        {
            async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
            var parent = await New(); var child = await New(); var grandchild = await New();
            foreach (var id in new[] { parent, child, grandchild }) service.Store.Update(id, "kimi", r => r.Draft = false, durable: true);
            service.Store.Update(grandchild, "kimi", r => r.ParentSessionId = child, durable: true);
            service.ReplyMailbox.Accept("owner", parent, child, "owner", delegation: true);
            if (source == "spawn") service.Store.CommitSpawnAssignment(grandchild, "target");
            else service.ReplyMailbox.Accept("target", child, grandchild, "target", delegation: true);
            var generation = (string)service.ReplyMailbox.FindAssignment("owner", child)!["generation"]!;
            JsonObject Target() => source == "spawn" ? service.Store.FindSpawnAssignment("target", child)! : service.ReplyMailbox.FindAssignment("target", child)!;
            var targetGeneration = (string)Target()["generation"]!;
            var request = new JsonObject { ["source"] = "delegation", ["assignmentId"] = "owner", ["generation"] = generation,
                ["expectedRevision"] = 1, ["operationId"] = "register", ["action"] = "register", ["targetSource"] = source,
                ["targetAssignmentId"] = "target", ["targetGeneration"] = targetGeneration,
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } };
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, request).WaitAsync(TimeSpan.FromSeconds(10));
            if (source == "spawn")
            {
                if (uncertain) Assert.IsTrue(service.Store.ClaimSpawnAssignment(grandchild, "target", "prompt"));
            }
            else
            {
                Assert.IsTrue(service.ReplyMailbox.TryMarkStarted("target", grandchild));
                Assert.IsTrue(service.ReplyMailbox.MarkFailed("target", grandchild, "delivery failed", uncertain: uncertain));
            }
            await client.DisposeAsync(); await service.DisposeAsync(); service = Next(); client = await TestClient.ConnectAsync(service);
            var read = new JsonObject { ["source"] = "delegation", ["assignmentId"] = "owner",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } };
            async Task<JsonNode?> Read() => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadAssignment, read.DeepClone()).WaitAsync(TimeSpan.FromSeconds(10));
            var result = await Read();
            var expectedDelivery = uncertain ? "uncertain" : source == "spawn" ? "not_started" : "failed";
            Assert.AreEqual("open", (string?)result!["status"]); Assert.AreEqual(2L, (long?)result["revision"]);
            Assert.AreEqual(uncertain ? "unknown" : "undeliverable", (string?)result["continuation"]!["state"]);
            Assert.AreEqual(expectedDelivery, (string?)result["continuation"]!["delivery"]);
            StringAssert.Contains((string?)result["continuation"]!["reason"], uncertain ? "uncertain" : expectedDelivery);
            Assert.AreEqual("open", (string?)Target()["status"]);
            if (source == "spawn") Assert.IsFalse(service.Store.ClaimSpawnAssignment(grandchild, "target", "retry"));
            lock (agent.Prompts) Assert.AreEqual(0, agent.Prompts.Count, "recovery and reads must not dispatch or replay native input");
            if (source == "spawn") service.Store.TransitionSpawnAssignment(grandchild, "target", grandchild, 1, "finish", "failed", "outcome", generation: targetGeneration);
            else service.ReplyMailbox.TransitionAssignment("target", grandchild, 1, "finish", "failed", "outcome", generation: targetGeneration);
            var terminal = await Read();
            Assert.AreEqual("ready", (string?)terminal!["continuation"]!["state"]);
            Assert.AreEqual(expectedDelivery, (string?)terminal["continuation"]!["delivery"]);
            Assert.AreEqual("failed", (string?)terminal["continuation"]!["reason"]);
            Assert.AreEqual("open", (string?)terminal["status"]); Assert.AreEqual(2L, (long?)terminal["revision"]);
            lock (agent.Prompts) Assert.AreEqual(0, agent.Prompts.Count);
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); }
    }

}
