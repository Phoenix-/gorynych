using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class CompletionNoticeApiTests
{
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task OnlyAuthenticatedOwnerCanReadAndAcknowledgeRetainedNotice()
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "notice-acl",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> New() => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
        var owner = await New(); var child = await New(); var foreign = await New();
        var mailbox = service.ReplyMailbox;
        mailbox.Accept("task", owner, child, "work", delegation: true);
        mailbox.MarkStarted("task", child); mailbox.MarkDelivered("task", child);
        var generation = (string)mailbox.FindAssignment("task", owner)!["generation"]!;
        var attempt = Guid.NewGuid().ToString("N");
        Assert.IsTrue(mailbox.ClaimCompletionReminder("delegation", "task", owner, child, generation, 1, attempt, new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), "prompt", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), 1, new string('A', 64), "host", "generation", null, null)).Acquired);
        Assert.IsTrue(mailbox.CloseCompletionReminderUnknown("delegation", "task", owner, child, generation, 1, attempt));
        JsonObject Auth(string who) => new() { ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[who] } };
        async Task<JsonNode?> Call(string method, JsonObject query) => await client.Rpc.SendRequestAsync(method, query).WaitAsync(TimeSpan.FromSeconds(20));
        var mine = await Call(GorynychProtocol.MethodListCompletionNotices, Auth(owner));
        Assert.AreEqual(1, mine!["items"]!.AsArray().Count);
        Assert.AreEqual(0, (await Call(GorynychProtocol.MethodListCompletionNotices, Auth(child)))!["items"]!.AsArray().Count);
        Assert.AreEqual(0, (await Call(GorynychProtocol.MethodListCompletionNotices, Auth(foreign)))!["items"]!.AsArray().Count);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodListCompletionNotices, new JsonObject { ["owner"] = owner }));
        JsonObject Ack(string who) { var q = Auth(who); q["assignmentId"] = "task"; q["assignee"] = child; q["generation"] = generation; q["revision"] = 1L; q["attemptId"] = attempt; return q; }
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodAckCompletionNotice, Ack(child)));
        var spoof = Ack(foreign); spoof["owner"] = owner;
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodAckCompletionNotice, spoof));
        var stale = Ack(owner); stale["generation"] = Guid.NewGuid().ToString("N");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Call(GorynychProtocol.MethodAckCompletionNotice, stale));
        Assert.AreEqual(true, (bool?)(await Call(GorynychProtocol.MethodAckCompletionNotice, Ack(owner)))!["acknowledged"]);
        Assert.AreEqual(true, (bool?)(await Call(GorynychProtocol.MethodAckCompletionNotice, Ack(owner)))!["acknowledged"]);
        Assert.AreEqual(0, (await Call(GorynychProtocol.MethodListCompletionNotices, Auth(owner)))!["items"]!.AsArray().Count);
        Assert.AreEqual("open", (string?)mailbox.FindAssignment("task", owner)!["status"]);
    }
}
