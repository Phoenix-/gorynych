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
        stored.Accept("uncertain", a.Id, b.Id, "do not duplicate me");
        stored.MarkStarted("uncertain", b.Id);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Prompt = (_, _, text) => { if (text.Contains("recover me", StringComparison.Ordinal)) delivered.TrySetResult(); return Task.CompletedTask; };
        a.Prompt = (_, _, text) => { if (text.Contains("processing is uncertain", StringComparison.Ordinal)) failure.TrySetResult(); return Task.CompletedTask; };
        await using var next = Create(directory, a, b);
        await delivered.Task.WaitAsync(Wait);
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
}
