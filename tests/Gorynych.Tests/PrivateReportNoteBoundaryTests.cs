using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class PrivateReportNoteBoundaryTests
{
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ExternalAcpCannotForwardPrivateReportNoteMethods()
    {
        var agent = new LifecycleAgent("kimi", canDelete: true);
        var hosts = new InProcessHosts(_ => agent.Adapter);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "private-boundary",
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var session = await HostTests.NewSession(client);
        await HostTests.Prompt(client, session, "warmup");
        var token = agent.Tokens[session];
        var report = Guid.NewGuid().ToString("N"); var attempt = Guid.NewGuid().ToString("N");
        JsonObject Args() => new() { ["sessionId"] = session, ["text"] = "must never reach native input", ["_meta"] = new JsonObject
        { [GorynychProtocol.MetaPeerToken] = token, [GorynychProtocol.MetaReportNote] = new JsonObject
            { ["reportId"] = report, ["attemptId"] = attempt } } };
        async Task Refused()
        {
            foreach (var method in new[] { GorynychProtocol.MethodReportNote, GorynychProtocol.MethodReportNoteStatus, GorynychProtocol.MethodReportNoteAck, GorynychProtocol.MethodHostReportNoteAck, GorynychProtocol.MethodHostReportNote, GorynychProtocol.MethodHostReportNoteStatus, GorynychProtocol.MethodHostReportNoteReceipt, GorynychProtocol.MethodCodexProvisionalIdle, GorynychProtocol.MethodHostCodexProvisionalIdle })
            {
                var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(method, Args()));
                Assert.AreEqual(JsonRpcException.MethodNotFound, error.Code);
            }
        }
        int NativeCalls() { lock (agent.Adapter.Methods) return agent.Adapter.Methods.Count(m => m is GorynychProtocol.MethodReportNote or GorynychProtocol.MethodReportNoteStatus or GorynychProtocol.MethodReportNoteAck or GorynychProtocol.MethodHostReportNoteAck or GorynychProtocol.MethodHostReportNote or GorynychProtocol.MethodHostReportNoteStatus or GorynychProtocol.MethodHostReportNoteReceipt or GorynychProtocol.MethodCodexProvisionalIdle or GorynychProtocol.MethodHostCodexProvisionalIdle); }
        var updated = service.Store.Get(session)!.UpdatedAt; var users = service.Feed(session).Users;
        var prompts = agent.Prompts.Count;
        await Refused();
        var guarded = new[] { GorynychProtocol.MethodCompletionGuardedSubmit, GorynychProtocol.MethodCompletionGuardedStatus,
            GorynychProtocol.MethodCompletionGuardedAck, GorynychProtocol.MethodCompletionGuardedRetire, GorynychProtocol.MethodCompletionGuardedAbort,
            GorynychProtocol.MethodHostCompletionGuardedSubmit, GorynychProtocol.MethodHostCompletionGuardedStatus,
            GorynychProtocol.MethodHostCompletionGuardedAck, GorynychProtocol.MethodHostCompletionGuardedRetire, GorynychProtocol.MethodHostCompletionGuardedAbort,
            GorynychProtocol.MethodClaudePromptObservation, GorynychProtocol.MethodHostClaudePromptObservation };
        foreach (var method in guarded)
        {
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(method, Args()));
            Assert.AreEqual(JsonRpcException.MethodNotFound, error.Code);
            await client.Rpc.SendNotificationAsync(method, Args());
        }
        await client.Rpc.SendNotificationAsync(GorynychProtocol.MethodCodexProvisionalIdle, Args());
        await client.Rpc.SendNotificationAsync(GorynychProtocol.MethodHostCodexProvisionalIdle, Args());
        await client.Rpc.SendNotificationAsync(GorynychProtocol.MethodHostReportNoteReceipt, Args());
        await client.Rpc.SendNotificationAsync(GorynychProtocol.MethodHostReportNoteAck, Args());
        await client.Rpc.SendNotificationAsync(GorynychProtocol.MethodCompletionGuardedRetire, Args());
        await client.Rpc.SendNotificationAsync(GorynychProtocol.MethodHostCompletionGuardedRetire, Args());
        await client.Rpc.SendNotificationAsync(GorynychProtocol.MethodReportNoteAck, Args());
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject());
        Assert.AreEqual(0, NativeCalls(), "private requests and notification never reached the native adapter");
        lock (agent.Adapter.Methods) Assert.IsFalse(agent.Adapter.Methods.Any(m => guarded.Contains(m)), "guarded methods never reached the adapter");
        Assert.AreEqual(prompts, agent.Prompts.Count); Assert.AreEqual(users, service.Feed(session).Users);
        Assert.AreEqual(updated, service.Store.Get(session)!.UpdatedAt);

        // The guarded methods are still routable over the trusted host RPC. This fake does not
        // implement them; its method log proves they crossed that boundary and stopped there.
        var route = service.Agents["kimi"].RouteOf(session)!;
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedStatus, Args()));
        lock (agent.Adapter.Methods) Assert.IsFalse(agent.Adapter.Methods.Any(m => guarded.Contains(m)), "unsupported host must not forward guarded RPC");
        var direct = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodReportNoteStatus, Args()));
        Assert.AreEqual(JsonRpcException.MethodNotFound, direct.Code); Assert.AreEqual(1, NativeCalls());

        // The separate managed read/list ACL remains exposed and can retrieve a real stored ID.
        var id = service.ReportStorage.Reserve(new ReportIdentity("private-boundary-result", "other-child", session, ReportCause.User)).ReportId;
        using (var capture = service.ReportStorage.OpenCapture(id))
        { capture.Append("retained text"); capture.Seal("ended"); service.ReportStorage.Commit(id, capture); }
        var read = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadReport, new JsonObject { ["reportId"] = id,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } });
        Assert.AreEqual("retained text", (string?)read!["text"]);
        var list = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListReports, new JsonObject
            { ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } });
        Assert.IsTrue(list!["items"]!.AsArray().Any(r => (string?)r!["reportId"] == id));

        await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = session });
        var afterClose = service.Store.Get(session)!.UpdatedAt; var nativeBefore = NativeCalls();
        await Refused();
        Assert.AreEqual(nativeBefore, NativeCalls(), "a closed real session was not reopened to service the private request");
        Assert.AreEqual(afterClose, service.Store.Get(session)!.UpdatedAt);
    }
}
