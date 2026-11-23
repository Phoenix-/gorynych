using System.Text.Json.Nodes;
using Gorynych.Adapters.Codex;
using Gorynych.Adapters.Codex.Protocol;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Acp;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class CodexPeerReplyWitnessTests
{
    const string Accepted = "0123456789abcdef0123456789abcdef";
    static CodexThread Thread() => new("session", "cwd", new CodexMap.CodexSettings("model", "low", null, null, "user"));
    static McpToolCallThreadItem Call(string server = "gorynych", string tool = "reply_message", string trigger = "request") => new()
    {
        Id = "call-1", Server = server, Tool = tool, Arguments = new JsonObject { ["message_id"] = trigger },
        Status = McpToolCallStatus.Completed, Result = new McpToolCallResult { Content = [new JsonObject
        { ["type"] = "text", ["text"] = "{\"messageId\":\"" + Accepted + "\",\"status\":\"accepted\"}" }] },
    };

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LiveFakeAppServerMcpEventsKeepExactPromptAndNativeTurn()
    {
        var fake = new FakeAppServer();
        var seen = new TaskCompletionSource<CodexPeerReplyWitness>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new CodexAdapterOptions { Launch = fake.StartAsync, ClientVersion = "test", PeerReplyWitnessForTests = w => seen.TrySetResult(w),
            FindHolders = _ => new Dictionary<string, string>(), LocksDirectory = Path.Combine(TestDirs.New(), "locks") };
        await using var agent = await new CodexAdapter(options).StartAsync(CancellationToken.None);
        await using var rpc = new JsonRpcConnection(agent.Input, agent.Output, "peer-witness-test") { NotificationHandler = (_, _) => Task.CompletedTask };
        rpc.Start();
        var session = (string)(await rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
        // Synthetic accepted mailbox record; this test does not claim an actual MCP exchange.
        var box = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"));
        var source = box.Accept("request", "sender", session, "question");
        var turn = new PeerTurnIdentity("request", (long)source["sequence"]!, "sender", session, "exact-prompt");
        Assert.IsTrue(box.TryMarkStarted("request", session, turn.Sequence, turn)); box.MarkDelivered("request", session);
        box.Accept(Accepted, session, "sender", "answer", replyTo: "request");
        var result = await rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = session,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "peer-receipt" }),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "exact-prompt" } }).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual("end_turn", (string?)result!["stopReason"]);
        var witness = await seen.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(session, witness.SessionId); Assert.AreEqual("exact-prompt", witness.PromptId);
        Assert.AreEqual(fake.LastTurnId, witness.NativeTurnId); Assert.AreEqual("request", witness.TriggerMessageId);
        Assert.AreEqual(Accepted, witness.AcceptedMessageId);
        Assert.AreEqual("explicit_reply_accepted", (string?)box.SettlePeerTurn(turn, "ended", "answer", witness.AcceptedMessageId, producerEvidenceComplete: true)["state"]);
    }

    [TestMethod]
    public void WitnessRequiresSameNamedNativeTurnAndBothItemEdges()
    {
        var t = Thread(); var done = t.Begin(1, "prompt")!; var call = Call();
        t.BeginPeerReplyEvidence("native", call);
        Assert.IsNull(t.CompletePeerReplyEvidence("native", call), "events before naming are not attributed later");
        t.Named("native"); t.BeginPeerReplyEvidence("native", call);
        Assert.IsNull(t.CompletePeerReplyEvidence("foreign", call));
        t.BeginPeerReplyEvidence("native", call);
        var witness = t.CompletePeerReplyEvidence("native", call); Assert.IsNotNull(witness);
        Assert.AreEqual("prompt", witness.PromptId); Assert.AreEqual("native", witness.NativeTurnId);
        Assert.AreEqual("request", witness.TriggerMessageId); Assert.AreEqual(Accepted, witness.AcceptedMessageId);
        Assert.IsNull(t.CompletePeerReplyEvidence("native", call), "duplicate terminal is not reissued");
        t.End(done); var next = t.Begin(2, "next")!; t.Named("later");
        Assert.IsNull(t.CompletePeerReplyEvidence("native", call), "old turn cannot attach to a new prompt"); t.End(next);
    }

    [TestMethod]
    public void OnlySuccessfulExactGorynychReplyReceiptCanBeObserved()
    {
        var t = Thread(); var done = t.Begin(1, "prompt")!; t.Named("native");
        foreach (var bad in new[] { Call("other"), Call(tool: "send_message"), Call(trigger: "other") })
        {
            t.BeginPeerReplyEvidence("native", Call());
            Assert.IsNull(t.CompletePeerReplyEvidence("native", bad));
        }
        foreach (var json in new[] { "{}", "{\"status\":\"accepted\",\"messageId\":\"bad\"}",
            "{\"status\":\"accepted\",\"status\":\"accepted\",\"messageId\":\"" + Accepted + "\"}",
            "{\"status\":3,\"messageId\":\"" + Accepted + "\"}" })
        {
            var call = Call(); call.Result!.Content[0]!["text"] = json;
            t.BeginPeerReplyEvidence("native", call); Assert.IsNull(t.CompletePeerReplyEvidence("native", call));
        }
        t.BeginPeerReplyEvidence("native", Call()); t.Stop(2);
        Assert.IsNull(t.CompletePeerReplyEvidence("native", Call()), "cancelled turn fails closed");
        t.End(done);
    }
}
