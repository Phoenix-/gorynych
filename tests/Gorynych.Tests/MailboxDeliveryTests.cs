using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class MailboxDeliveryTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    sealed class Peer(string name)
    {
        public string Id => name + "-session";
        public string Token = "";
        public readonly ConcurrentQueue<string> Prompts = new();
        public Func<ScriptedAgent, PromptRequest, string, Task> Prompt = (_, _, _) => Task.CompletedTask;
        public ScriptedAdapter Adapter() => new(name, async (agent, request, ct) =>
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init(resume: false, load: true);
                case AcpMethods.Agent.SessionList: return AcpJson.ToNode(new ListSessionsResponse { Sessions = [] });
                case AcpMethods.Agent.SessionNew:
                    var args = request.Params!["mcpServers"]!.AsArray().First(x => (string?)x!["name"] == "gorynych")!["args"]!.AsArray();
                    Token = (string)args[args.Count - 1]!;
                    return AcpJson.ToNode(new NewSessionResponse { SessionId = Id });
                case AcpMethods.Agent.SessionLoad: return AcpJson.ToNode(new LoadSessionResponse());
                case AcpMethods.Agent.SessionPrompt:
                    var p = request.ParamsAs<PromptRequest>();
                    var text = ((TextContent)p.Prompt[0]).Text;
                    Prompts.Enqueue(text);
                    await Prompt(agent, p, text);
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                default: throw new JsonRpcException(JsonRpcException.MethodNotFound, request.Method);
            }
        });
    }

    static Conductor Create(string directory, params Peer[] peers) => new(new ConductorOptions
    { DataDirectory = directory, ServiceId = "mailbox-test", PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, peers.Select(p => p.Adapter()));

    static async Task New(TestClient client, params Peer[] peers)
    {
        foreach (var p in peers)
            await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = Path.GetTempPath(), Meta = new JsonObject { [GorynychProtocol.MetaAgent] = p.Id.Replace("-session", "") } });
    }

    static Task<JsonNode?> Send(TestClient client, Peer sender, string target, string text, string? id = null, bool reply = false) =>
        client.Rpc.SendRequestAsync(reply ? GorynychProtocol.MethodReplyMessage : GorynychProtocol.MethodSendMessage, new JsonObject
        {
            [reply ? "messageId" : "sessionId"] = target, ["text"] = text,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = sender.Token,
                [GorynychProtocol.MetaMessageRequestId] = id ?? Guid.NewGuid().ToString("N") },
        });

    [TestMethod]
    public async Task DelegationWaitsForBusyTurnWithoutCancellingIt()
    {
        var parent = new Peer("alpha"); var child = new Peer("beta");
        await using var service = Create(TestDirs.New(), parent, child);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, parent, child);
        service.Store.Update(child.Id, "beta", r => r.ParentSessionId = parent.Id);
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? promptAssignment = null;
        child.Prompt = async (_, prompt, text) =>
        {
            if (text == "busy") { busy.TrySetResult(); await release.Task; }
            else if (text.Contains("assignment_id task-1", StringComparison.Ordinal))
            {
                promptAssignment = (string?)prompt.Meta?[GorynychProtocol.MetaAssignmentId];
                delivered.TrySetResult();
            }
        };
        var running = client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = child.Id, Prompt = [new TextContent { Text = "busy" }] });
        try
        {
            await busy.Task.WaitAsync(Wait);
            var receipt = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject
            {
                ["sessionId"] = child.Id, ["text"] = "Implement the change",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = parent.Token,
                    [GorynychProtocol.MetaMessageRequestId] = "task-1" },
            });
            Assert.AreEqual("task-1", (string?)receipt!["assignmentId"]);
            Assert.IsFalse(running.IsCompleted);
            Assert.AreEqual(1, child.Prompts.Count);
            release.TrySetResult();
            Assert.AreEqual(StopReason.EndTurn, (await running.WaitAsync(Wait)).StopReason);
            await delivered.Task.WaitAsync(Wait);
            Assert.AreEqual("task-1", promptAssignment, "adapter receives assignment metadata");
            await using var replayClient = await TestClient.ConnectAsync(service);
            await replayClient.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject
                { ["sessionId"] = child.Id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() });
            var journal = replayClient.Snapshot().Single(u =>
                (string?)u["_meta"]?[GorynychProtocol.MetaMailboxMessage] == "task-1" &&
                (string?)u["sessionUpdate"] == "user_message_chunk");
            Assert.AreEqual("task-1", (string?)journal["_meta"]?[GorynychProtocol.MetaAssignmentId]);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CancelBeforeDelegationClaimSuppressesJournalAndKeepsNextMessageDeliverable()
    {
        var parent = new Peer("alpha"); var child = new Peer("beta");
        await using var service = Create(TestDirs.New(), parent, child);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, parent, child);
        service.Store.Update(child.Id, "beta", r => { r.ParentSessionId = parent.Id; r.ReportToParent = false; });
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        service.BeforeMailboxMarkStarted = () => { entered.Set(); if (!release.Wait(Wait)) throw new IOException("claim barrier timed out"); };
        try
        {
            var receipt = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject
            { ["sessionId"] = child.Id, ["text"] = "cancelled task", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaPeerToken] = parent.Token, [GorynychProtocol.MetaMessageRequestId] = "cancel-task" } }).WaitAsync(Wait);
            Assert.AreEqual("cancel-task", (string?)receipt!["assignmentId"]);
            Assert.IsTrue(await Task.Run(() => entered.Wait(Wait)), "worker reached the pre-claim barrier");
            var cancelled = service.ReplyMailbox.TransitionAssignment("cancel-task", parent.Id, 1, "cancel-op", "cancelled", "stop", generation: (string)service.ReplyMailbox.FindAssignment("cancel-task", parent.Id)!["generation"]!);
            Assert.AreEqual("suppressed", (string?)cancelled["delivery"]);
            await Send(client, parent, child.Id, "next ordinary", "next").WaitAsync(Wait);
            service.BeforeMailboxMarkStarted = null;
        }
        finally { release.Set(); }
        await LinkHarness.Until(() => child.Prompts.Any(p => p.Contains("next ordinary", StringComparison.Ordinal)), "next message delivered").WaitAsync(Wait);
        Assert.IsFalse(child.Prompts.Any(p => p.Contains("cancelled task", StringComparison.Ordinal)));
        var entries = service.Feed(child.Id).Journal.ReadAll();
        Assert.IsFalse(entries.Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaMailboxMessage] == "cancel-task"), "suppressed delegation is not journaled as a turn");
        Assert.AreEqual("cancelled", (string?)service.ReplyMailbox.FindAssignment("cancel-task", child.Id)!["status"]);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CancelAfterDelegationClaimDoesNotCancelNativeTurnOrLoseDelivery()
    {
        var parent = new Peer("alpha"); var child = new Peer("beta");
        await using var service = Create(TestDirs.New(), parent, child);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, parent, child);
        service.Store.Update(child.Id, "beta", r => { r.ParentSessionId = parent.Id; r.ReportToParent = false; });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellations = 0;
        child.Prompt = async (agent, _, text) =>
        {
            agent.Rpc.NotificationHandler = (method, _) => { if (method == AcpMethods.Agent.SessionCancel) Interlocked.Increment(ref cancellations); return Task.CompletedTask; };
            if (text.Contains("claimed-task", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task; }
        };
        try
        {
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject
            { ["sessionId"] = child.Id, ["text"] = "work after claim", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaPeerToken] = parent.Token, [GorynychProtocol.MetaMessageRequestId] = "claimed-task" } }).WaitAsync(Wait);
            await entered.Task.WaitAsync(Wait);
            Assert.AreEqual("started", (string?)service.ReplyMailbox.FindAssignment("claimed-task", parent.Id)!["delivery"]);
            service.ReplyMailbox.TransitionAssignment("claimed-task", parent.Id, 1, "cancel-op", "cancelled", "stop", generation: (string)service.ReplyMailbox.FindAssignment("claimed-task", parent.Id)!["generation"]!);
            Assert.AreEqual("started", (string?)service.ReplyMailbox.FindAssignment("claimed-task", parent.Id)!["delivery"]);
        }
        finally { release.TrySetResult(); }
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("claimed-task", child.Id)?["delivery"] == "delivered", "native turn completes normally").WaitAsync(Wait);
        Assert.AreEqual("cancelled", (string?)service.ReplyMailbox.FindAssignment("claimed-task", child.Id)!["status"]);
        Assert.AreEqual(1, child.Prompts.Count(p => p.Contains("claimed-task", StringComparison.Ordinal)));
        Assert.AreEqual(0, Volatile.Read(ref cancellations), "assignment cancellation does not send session/cancel");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ReusedDelegationIdDispatchesDistinctPromptAndReport()
    {
        var parent = new Peer("alpha"); var child = new Peer("beta");
        await using var service = Create(TestDirs.New(), parent, child);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, parent, child);
        service.Store.Update(child.Id, "beta", r => { r.ParentSessionId = parent.Id; r.ReportToParent = true; });
        service.ReplyMailbox.CapacityForTests = 1; service.ReplyMailbox.AssignmentCapacityForTests = 1;
        Task<JsonNode?> Delegate(string text) => client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject
        { ["sessionId"] = child.Id, ["text"] = text, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = parent.Token,
            [GorynychProtocol.MetaMessageRequestId] = "same" } });
        await Delegate("first incarnation").WaitAsync(Wait);
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("same", parent.Id)?["delivery"] == "delivered" &&
            service.ReportStorage.List(child.Id).Items.Any(r => r.Identity.MessageId == "same" && r.ProducerAck), "first delegation and report").WaitAsync(Wait);
        var old = service.ReplyMailbox.FindAssignment("same", parent.Id)!; var oldGeneration = (string)old["generation"]!;
        var oldReport = service.ReportStorage.List(child.Id).Items.Single(r => r.Identity.MessageId == "same");
        Assert.AreEqual(oldGeneration, oldReport.Identity.AssignmentGeneration);
        service.ReplyMailbox.TransitionAssignment("same", parent.Id, 1, "cancel-old", "cancelled", "done", generation: oldGeneration);
        service.ReplyMailbox.Accept("filler", parent.Id, child.Id, "filler", delegation: true);
        service.ReplyMailbox.TransitionAssignment("filler", parent.Id, 1, "cancel-filler", "cancelled", "stop", generation: (string)service.ReplyMailbox.FindAssignment("filler", parent.Id)!["generation"]!);
        await Delegate("second incarnation").WaitAsync(Wait);
        var generation = (string)service.ReplyMailbox.FindAssignment("same", child.Id)!["generation"]!;
        Assert.AreNotEqual(oldGeneration, generation);
        await Delegate("second incarnation").WaitAsync(Wait);
        await LinkHarness.Until(() => service.ReportStorage.List(child.Id).Items.Count(r => r.Identity.MessageId == "same" && r.ProducerAck) == 2,
            "both distinct reports published").WaitAsync(Wait);
        var reports = service.ReportStorage.List(child.Id).Items.Where(r => r.Identity.MessageId == "same").ToArray();
        Assert.AreEqual(2, reports.Select(r => r.Identity.PromptId).Distinct().Count());
        Assert.AreEqual(2, reports.Select(r => r.Identity.TurnKey).Distinct().Count());
        Assert.IsTrue(reports.Any(r => r.Identity.AssignmentGeneration == generation));
        Assert.AreEqual(1, child.Prompts.Count(p => p.Contains("first incarnation", StringComparison.Ordinal)));
        Assert.AreEqual(1, child.Prompts.Count(p => p.Contains("second incarnation", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task OrdinaryCompositeIdsCannotCollideWithDelegationPromptOrReportKeys()
    {
        var parent = new Peer("alpha"); var child = new Peer("beta");
        await using var service = Create(TestDirs.New(), parent, child);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, parent, child);
        service.Store.Update(child.Id, "beta", r => { r.ParentSessionId = parent.Id; r.ReportToParent = true; });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Prompt = async (_, _, text) => { if (text == "busy") { entered.TrySetResult(); await release.Task; } };
        var busy = client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = child.Id, Prompt = [new TextContent { Text = "busy" }] });
        try
        {
            await entered.Task.WaitAsync(Wait);
            var receipt = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject
            { ["sessionId"] = child.Id, ["text"] = "delegated body", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaPeerToken] = parent.Token, [GorynychProtocol.MetaMessageRequestId] = "same" } }).WaitAsync(Wait);
            Assert.AreEqual("same", (string?)receipt!["assignmentId"]);
            var generation = (string)service.ReplyMailbox.FindAssignment("same", parent.Id)!["generation"]!;
            var oldPromptCollision = "same-" + generation;
            var oldTurnCollision = "same:" + generation;
            await Send(client, parent, child.Id, "ordinary prompt collision", oldPromptCollision).WaitAsync(Wait);
            await Send(client, parent, child.Id, "ordinary report collision", oldTurnCollision).WaitAsync(Wait);
            release.TrySetResult(); await busy.WaitAsync(Wait);
            await LinkHarness.Until(() => service.ReportStorage.List(child.Id).Items.Count(r => r.ProducerAck &&
                r.Identity.MessageId is { } id && (id == "same" || id == oldPromptCollision || id == oldTurnCollision)) == 3,
                "all three independent reports published").WaitAsync(Wait);
            var reports = service.ReportStorage.List(child.Id).Items.Where(r => r.Identity.MessageId is { } id &&
                (id == "same" || id == oldPromptCollision || id == oldTurnCollision)).ToArray();
            Assert.AreEqual(3, reports.Select(r => r.Identity.PromptId).Distinct().Count());
            Assert.AreEqual(3, reports.Select(r => r.Identity.TurnKey).Distinct().Count());
            Assert.AreEqual(ReportCause.Delegation, reports.Single(r => r.Identity.MessageId == "same").Identity.Cause);
            Assert.AreEqual(generation, reports.Single(r => r.Identity.MessageId == "same").Identity.AssignmentGeneration);
            Assert.AreEqual(1, child.Prompts.Count(p => p.Contains("delegated body", StringComparison.Ordinal)));
            Assert.AreEqual(1, child.Prompts.Count(p => p.Contains("ordinary prompt collision", StringComparison.Ordinal)));
            Assert.AreEqual(1, child.Prompts.Count(p => p.Contains("ordinary report collision", StringComparison.Ordinal)));
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task BusyRecipient_DoesNotBlockSend_AndReplyWakesOriginalSender()
    {
        var a = new Peer("alpha"); var b = new Peer("beta");
        await using var service = Create(TestDirs.New(), a, b);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, a, b);
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string original = "original";
        b.Prompt = async (_, _, text) =>
        {
            if (text == "busy") { busy.SetResult(); await release.Task; }
            else await Send(client, b, original, "review complete", "answer", reply: true);
        };
        a.Prompt = (_, _, text) =>
        {
            if (text.Contains("reply_to original", StringComparison.Ordinal)) replied.TrySetResult();
            return Task.CompletedTask;
        };
        var running = client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = b.Id, Prompt = [new TextContent { Text = "busy" }] });
        try
        {
            await busy.Task.WaitAsync(Wait);
            var receipt = await Send(client, a, b.Id, "please review", original).WaitAsync(Wait);
            Assert.AreEqual("accepted", (string?)receipt!["status"]);
            Assert.IsFalse(running.IsCompleted);
            Assert.AreEqual(1, b.Prompts.Count);
            release.SetResult();
            await running.WaitAsync(Wait);
            await replied.Task.WaitAsync(Wait);
            var delivered = b.Prompts.Last();
            StringAssert.Contains(delivered, "message_id original");
            StringAssert.Contains(delivered, "reply_message");
            Assert.IsFalse(delivered.Contains("Do not call ask_agent", StringComparison.Ordinal));
            await Send(client, a, b.Id, "please review", original).WaitAsync(Wait);
            Assert.AreEqual(2, b.Prompts.Count, "retry does not enqueue the same request twice");
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task RequestReplyRecordsFastCorrelatedResponseAndExcludesOtherPeers()
    {
        var a = new Peer("alpha"); var b = new Peer("beta"); var c = new Peer("gamma");
        await using var service = Create(TestDirs.New(), a, b, c);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, a, b, c);
        var replied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Prompt = async (_, prompt, text) =>
        {
            if (!text.Contains("message_id question-1", StringComparison.Ordinal)) return;
            Assert.AreEqual(true, (bool?)prompt.Meta?[GorynychProtocol.MetaExpectsReply]);
            await Send(client, b, "question-1", "answer", "reply-1", reply: true);
            replied.TrySetResult();
        };
        var receipt = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestReply, new JsonObject
        {
            ["sessionId"] = b.Id, ["text"] = "question", ["_meta"] = new JsonObject
            {
                [GorynychProtocol.MetaPeerToken] = a.Token,
                [GorynychProtocol.MetaMessageRequestId] = "question-1",
            },
        }).WaitAsync(Wait);
        Assert.AreEqual(true, (bool?)receipt!["replyExpected"]);
        await replied.Task.WaitAsync(Wait);
        async Task<JsonNode?> List(Peer peer) => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListReplyWaits,
            new JsonObject { ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = peer.Token } }).WaitAsync(Wait);
        foreach (var participant in new[] { a, b })
        {
            var item = (await List(participant))!["items"]![0]!;
            Assert.AreEqual("replied", (string?)item["state"]);
            Assert.AreEqual("reply-1", (string?)item["replyId"]);
        }
        Assert.AreEqual(0, (await List(c))!["items"]!.AsArray().Count);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodCancelReplyWait,
            new JsonObject { ["messageId"] = "question-1", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = c.Token } }));
    }

    [TestMethod]
    public async Task ReviewerCanMessageAnotherReviewer_AndForeignReplyIsRejected()
    {
        var a = new Peer("alpha"); var b = new Peer("beta"); var c = new Peer("gamma");
        await using var service = Create(TestDirs.New(), a, b, c);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, a, b, c);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Prompt = async (_, _, _) => { await Send(client, b, c.Id, "discuss finding", "reviewer-message"); };
        c.Prompt = (_, _, _) => { received.TrySetResult(); return Task.CompletedTask; };
        await Send(client, a, b.Id, "review", "review-request");
        await received.Task.WaitAsync(Wait);
        var forged = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Send(client, c, "review-request", "forged", reply: true));
        Assert.AreEqual(JsonRpcException.InvalidParams, forged.Code);
    }

    [TestMethod]
    public async Task PendingMessage_IsRecoveredAfterRestart_AndAnInterruptedHandoffIsNotReplayed()
    {
        var directory = TestDirs.New();
        var a = new Peer("alpha"); var b = new Peer("beta");
        await using (var first = Create(directory, a, b))
        await using (var client = await TestClient.ConnectAsync(first))
        {
            await New(client, a, b);
            foreach (var p in new[] { a, b })
                await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                    new PromptRequest { SessionId = p.Id, Prompt = [new TextContent { Text = "create native history" }] });
        }
        var stored = new MailboxStore(Path.Combine(directory, "mailbox.json"));
        stored.Accept("pending", a.Id, b.Id, "recover me");
        stored.Accept("pending-delegation", a.Id, b.Id, "recover assignment", delegation: true);
        stored.Accept("uncertain", a.Id, b.Id, "do not duplicate me");
        stored.MarkStarted("uncertain", b.Id);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Prompt = (_, _, text) => { if (text.Contains("recover me", StringComparison.Ordinal)) delivered.TrySetResult();
            if (text.Contains("assignment_id pending-delegation", StringComparison.Ordinal)) delegated.TrySetResult(); return Task.CompletedTask; };
        a.Prompt = (_, _, text) => { if (text.Contains("processing is uncertain", StringComparison.Ordinal)) failure.TrySetResult(); return Task.CompletedTask; };
        await using var next = Create(directory, a, b);
        await delivered.Task.WaitAsync(Wait);
        await delegated.Task.WaitAsync(Wait);
        await failure.Task.WaitAsync(Wait);
        Assert.IsFalse(b.Prompts.Any(p => p.Contains("do not duplicate me", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ArrivalsAtTurnEnd_AreNotStranded()
    {
        var a = new Peer("alpha"); var b = new Peer("beta");
        await using var service = Create(TestDirs.New(), a, b);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, a, b);
        var count = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Prompt = (_, _, _) => { if (Interlocked.Increment(ref count) == 50) done.TrySetResult(); return Task.CompletedTask; };
        for (var i = 0; i < 50; i++) await Send(client, a, b.Id, "message " + i, "turn-race-" + i);
        await done.Task.WaitAsync(Wait);
        Assert.AreEqual(50, count);
        Assert.AreEqual(50, b.Prompts.Distinct().Count());
    }

    [TestMethod]
    public async Task HandoffAlone_DoesNotClearTheDurableUncertaintyClaim()
    {
        var directory = TestDirs.New();
        var a = new Peer("alpha"); var b = new Peer("beta");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Prompt = async (_, _, _) => { entered.TrySetResult(); await release.Task; };
        await using var service = Create(directory, a, b);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, a, b);
        try
        {
            await Send(client, a, b.Id, "held native processing", "handoff");
            await entered.Task.WaitAsync(Wait);
            var saved = new MailboxStore(Path.Combine(directory, "mailbox.json")).Find("handoff")!;
            Assert.AreEqual(true, (bool?)saved["started"]);
            Assert.AreEqual(false, (bool?)saved["delivered"], "transport write must not erase restart uncertainty before adapter response");
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task RetainedReplyReceipt_CanBeRetriedAfterItsOriginalIsEvicted()
    {
        var directory = TestDirs.New();
        var b = new Peer("beta");
        await using (var first = Create(directory, b))
        await using (var client = await TestClient.ConnectAsync(first))
        {
            await New(client, b);
            await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = b.Id, Prompt = [new TextContent { Text = "create native history" }] });
        }
        var stored = new MailboxStore(Path.Combine(directory, "mailbox.json"), 2);
        const string outside = "outside:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        stored.Accept("original", outside, b.Id, "review");
        stored.MarkDelivered("original", b.Id);
        stored.Accept("retained-reply", b.Id, outside, "answer", "original");
        stored.Accept("filler", b.Id, outside, "next");
        Assert.IsNull(stored.Find("original"));
        await using var next = Create(directory, b);
        await using var back = await TestClient.ConnectAsync(next);
        var receipt = await Send(back, b, "original", "answer", "retained-reply", reply: true);
        Assert.AreEqual("accepted", (string?)receipt!["status"]);
    }

    [TestMethod]
    public async Task FailedMailboxTurn_ReportsItsOutcomeToTheChildsParent()
    {
        var a = new Peer("alpha"); var b = new Peer("beta");
        await using var service = Create(TestDirs.New(), a, b);
        await using var client = await TestClient.ConnectAsync(service);
        await New(client, a, b);
        service.Store.Update(b.Id, "beta", r => { r.ParentSessionId = a.Id; r.ReportToParent = true; });
        var report = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Prompt = (_, _, text) =>
        {
            if (text.StartsWith("[Report", StringComparison.Ordinal) && text.Contains("responder failed", StringComparison.Ordinal)) report.TrySetResult();
            return Task.CompletedTask;
        };
        b.Prompt = (_, _, _) => throw new JsonRpcException(JsonRpcException.InternalError, "responder failed");
        await Send(client, a, b.Id, "review", "failed-child-turn");
        await report.Task.WaitAsync(Wait);
    }

    [TestMethod]
    public async Task SendAndReply_KeepTheFullAccessApprovalBoundary()
    {
        var a = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        var b = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true };
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "permissions",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [a.Adapter, b.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> Make(string agent) => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Path.GetTempPath(), Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent } })).SessionId;
        Task Mode(string id, string mode) => client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = id, ["configId"] = "mode", ["value"] = mode });
        Task<JsonNode?> Message(string token, string target, string id, bool reply) => client.Rpc.SendRequestAsync(
            reply ? GorynychProtocol.MethodReplyMessage : GorynychProtocol.MethodSendMessage, new JsonObject
            { [reply ? "messageId" : "sessionId"] = target, ["text"] = "read-only review", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaPeerToken] = token, [GorynychProtocol.MetaMessageRequestId] = id } });
        var aid = await Make("dsh"); var bid = await Make("kimi");
        await Mode(aid, "full"); await Mode(bid, "ask");
        await Message(a.Tokens[aid], bid, "permission-original", false);
        Assert.AreEqual(0, client.Approvals.Count);
        client.Approve = _ => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "decline" });
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Message(b.Tokens[bid], "permission-original", "denied-reply", true));
        Assert.AreEqual(1, client.Approvals.Count, "a reply into full access needs the same approval as a new message");
        await Mode(aid, "ask"); await Mode(bid, "full");
        client.Approve = _ => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "ask" });
        await Message(a.Tokens[aid], bid, "allowed-send", false);
        await LinkHarness.Until(() => { lock (b.Prompts) return b.Prompts.Count >= 2; }, "approved message delivered");
        Assert.AreEqual(2, client.Approvals.Count, "consent is preserved across the delivery queue");
    }

    [TestMethod]
    public async Task QueuedDelegationRechecksModeBeforeDelivery()
    {
        var parent = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        var child = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true };
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "delegation-mode",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [parent.Adapter, child.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> Make(string agent) => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Path.GetTempPath(), Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent } })).SessionId;
        Task Mode(string id, string mode) => client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = id, ["configId"] = "mode", ["value"] = mode });
        var parentId = await Make("dsh"); var childId = await Make("kimi");
        service.Store.Update(childId, "kimi", r => { r.ParentSessionId = parentId; r.ReportToParent = false; });
        await Mode(parentId, "ask"); await Mode(childId, "ask");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.DuringPrompt = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var busy = client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = childId, Prompt = [new TextContent { Text = "busy" }] });
        try
        {
            await entered.Task.WaitAsync(Wait);
            var result = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject
            {
                ["sessionId"] = childId, ["text"] = "authorized work",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = parent.Tokens[parentId],
                    [GorynychProtocol.MetaMessageRequestId] = "mode-delegation" },
            });
            Assert.AreEqual("accepted", (string?)result!["status"]);
            Assert.AreEqual(0, client.Approvals.Count);
            await Mode(childId, "full");
            client.Approve = _ => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "decline" });
            release.TrySetResult();
            await busy.WaitAsync(Wait);
            await LinkHarness.Until(() => client.Approvals.Count == 1, "delivery-time approval");
            await LinkHarness.Until(() => { lock (parent.Prompts) return parent.Prompts.Any(p => p.Text.Contains("mode-delegation", StringComparison.Ordinal)); }, "delivery failure notice");
            lock (child.Prompts) Assert.IsFalse(child.Prompts.Any(p => p.Text.Contains("mode-delegation", StringComparison.Ordinal)));
        }
        finally { release.TrySetResult(); }
    }
}
