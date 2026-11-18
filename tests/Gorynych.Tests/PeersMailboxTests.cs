using System.Net;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class PeersMailboxTests
{
    [TestMethod]
    public void DiscoveryHasOnlyTheNewManagedMessagingTools()
    {
        var tools = PeersServer.Tools(true).ToDictionary(t => (string)t["name"]!);
        Assert.IsTrue(tools.ContainsKey("send_message"));
        Assert.IsTrue(tools.ContainsKey("delegate_task"));
        Assert.IsTrue(tools.ContainsKey("reply_message"));
        Assert.IsFalse(tools.ContainsKey("ask_agent"));
        Assert.IsFalse(tools.ContainsKey("reply"));
        CollectionAssert.AreEqual(new[] { "session_id", "text" }, tools["send_message"]["inputSchema"]!["required"]!.AsArray().Select(x => (string)x!).ToArray());
        CollectionAssert.AreEqual(new[] { "message_id", "text" }, tools["reply_message"]["inputSchema"]!["required"]!.AsArray().Select(x => (string)x!).ToArray());
        StringAssert.Contains((string)tools["send_message"]["description"]!, "not delivery");
        StringAssert.Contains(PeersServer.SystemPrompt, "grants no new authority");
        Assert.AreEqual("reply", (string?)PeersServer.Tools(false).Single()["name"]);
    }

    static PeersServer.ServiceRoute Route(Func<int, JsonRpcRequest, JsonRpcConnection, Task<JsonNode?>> handler, double seconds = 2)
    {
        var count = 0;
        return new PeersServer.ServiceRoute(_ =>
        {
            var n = Interlocked.Increment(ref count);
            var link = new DuplexLink();
            var server = new JsonRpcConnection(link.AgentEnd.Input, link.AgentEnd.Output, "mailbox-test-server");
            server.RequestHandler = (request, _) => handler(n, request, server);
            server.Start();
            var client = new JsonRpcConnection(link.ClientEnd.Input, link.ClientEnd.Output, "mailbox-test-client");
            client.Start();
            return Task.FromResult(client);
        }, "test") { RestartWait = TimeSpan.FromSeconds(seconds), RetryDelay = TimeSpan.FromMilliseconds(10) };
    }

    [TestMethod]
    [DataRow("send_message", "session_id", GorynychProtocol.MethodSendMessage, "sessionId")]
    [DataRow("delegate_task", "session_id", GorynychProtocol.MethodDelegateTask, "sessionId")]
    [DataRow("reply_message", "message_id", GorynychProtocol.MethodReplyMessage, "messageId")]
    public async Task RetriesLostResponseWithSameIdAndReturnsReceipt(string tool, string input, string method, string field)
    {
        var requests = new List<JsonObject>();
        var route = Route(async (n, request, server) =>
        {
            Assert.AreEqual(method, request.Method);
            lock (requests) requests.Add(request.Params!.DeepClone().AsObject());
            if (n == 1) { await server.DisposeAsync(); return null; }
            if (n == 2) throw new JsonRpcException(JsonRpcException.InternalError, "restarting", new JsonObject { [GorynychProtocol.MetaRestarting] = true });
            var receipt = new JsonObject { ["messageId"] = "accepted-id", ["status"] = "accepted" };
            if (tool == "delegate_task") receipt["assignmentId"] = "accepted-id";
            return receipt;
        });
        using var http = new HttpClient(new FailHttp());
        var result = await PeersServer.CallAsync(http, route, "token", "http://unused", tool,
            new JsonObject { [input] = "target", ["text"] = "hello" }, CancellationToken.None);
        Assert.AreEqual(false, (bool?)result["isError"]);
        var receipt = JsonNode.Parse((string)result["content"]![0]!["text"]!)!;
        Assert.AreEqual("accepted-id", (string?)receipt["messageId"]);
        Assert.AreEqual("accepted", (string?)receipt["status"]);
        Assert.AreEqual(3, requests.Count);
        var id = (string?)requests[0]["_meta"]![GorynychProtocol.MetaMessageRequestId];
        Assert.IsTrue(Guid.TryParse(id, out _));
        foreach (var request in requests)
        {
            Assert.AreEqual(id, (string?)request["_meta"]![GorynychProtocol.MetaMessageRequestId]);
            Assert.AreEqual("token", (string?)request["_meta"]![GorynychProtocol.MetaPeerToken]);
            Assert.AreEqual("target", (string?)request[field]);
        }
    }

    [TestMethod]
    public async Task UnknownAcceptanceAndInvalidArgumentsAreReported()
    {
        var route = Route(async (_, _, server) => { await server.DisposeAsync(); return null; }, seconds: 0.1);
        using var http = new HttpClient(new FailHttp());
        var result = await PeersServer.CallAsync(http, route, "token", null, "send_message",
            new JsonObject { ["session_id"] = "target", ["text"] = "hello" }, CancellationToken.None);
        Assert.AreEqual(true, (bool?)result["isError"]);
        StringAssert.Contains((string)result["content"]![0]!["text"]!, "Acceptance is unknown for request");
        var invalid = await PeersServer.CallAsync(http, route, "token", null, "reply_message", new JsonObject { ["text"] = "x" }, CancellationToken.None);
        Assert.AreEqual(true, (bool?)invalid["isError"]);
        StringAssert.Contains((string)invalid["content"]![0]!["text"]!, "message_id and text are required");
    }

    [TestMethod]
    public async Task CancellationStopsReconnectAndInvalidReceiptIsNotAccepted()
    {
        using var http = new HttpClient(new FailHttp());
        using var stop = new CancellationTokenSource();
        var attempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offline = new PeersServer.ServiceRoute(_ =>
        {
            attempting.TrySetResult();
            throw new IOException("offline");
        }, "test") { RestartWait = TimeSpan.FromSeconds(30), RetryDelay = TimeSpan.FromSeconds(10) };
        var pending = PeersServer.CallAsync(http, offline, "token", null, "send_message",
            new JsonObject { ["session_id"] = "target", ["text"] = "hello" }, stop.Token);
        await attempting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);

        var bad = Route((_, _, _) => Task.FromResult<JsonNode?>(new JsonObject { ["status"] = "accepted" }));
        var result = await PeersServer.CallAsync(http, bad, "token", null, "send_message",
            new JsonObject { ["session_id"] = "target", ["text"] = "hello" }, CancellationToken.None);
        Assert.AreEqual(true, (bool?)result["isError"]);
        StringAssert.Contains((string)result["content"]![0]!["text"]!, "invalid receipt");
    }

    [TestMethod]
    public async Task LegacyChannelReplyRemainsCallableWithoutBeingAdvertisedToPeers()
    {
        using var http = new HttpClient(new LegacyReplyHttp());
        var result = await PeersServer.CallAsync(http, null, null, "http://legacy/channel", "reply",
            new JsonObject { ["message_id"] = "legacy-id", ["text"] = "answer" }, CancellationToken.None);
        Assert.AreEqual(false, (bool?)result["isError"]);
        Assert.AreEqual("delivered", (string?)result["content"]![0]!["text"]);
        var unknown = await PeersServer.CallAsync(http, null, null, null, "missing_tool", null, CancellationToken.None);
        Assert.AreEqual(true, (bool?)unknown["isError"]);
    }

    sealed class LegacyReplyHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/channel/reply", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"delivered\":true}", System.Text.Encoding.UTF8, "application/json") });
        }
    }

    sealed class FailHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new AssertFailedException("New mailbox replies must not use the legacy HTTP endpoint");
    }
}
