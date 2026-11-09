using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Tests;

[TestClass]
public sealed class JsonRpcTests
{
    static (JsonRpcConnection Client, JsonRpcConnection Agent) Link()
    {
        var link = new DuplexLink();
        var (ci, co) = link.ClientEnd;
        var (ai, ao) = link.AgentEnd;
        return (new JsonRpcConnection(ci, co, "client"), new JsonRpcConnection(ai, ao, "agent"));
    }

    [TestMethod]
    public async Task Request_ReturnsResult_AndNotificationsArriveFirst()
    {
        var (client, agent) = Link();
        var seen = new List<string>();
        agent.RequestHandler = async (req, ct) =>
        {
            Assert.AreEqual("session/prompt", req.Method);
            var sid = req.ParamsAs<PromptRequest>().SessionId;
            for (var i = 0; i < 3; i++)
                await agent.SendUpdateAsync(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = $"part{i}" } });
            return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
        };
        client.NotificationHandler = (method, p) =>
        {
            var n = AcpJson.FromNode<SessionNotification>(p)!;
            seen.Add(((TextContent)((SessionUpdateAgentMessageChunk)n.Update).Content).Text);
            return Task.CompletedTask;
        };
        client.Start();
        agent.Start();
        var result = await client.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = "s1", Prompt = [new TextContent { Text = "hello" }] });
        Assert.AreEqual(StopReason.EndTurn, result.StopReason);
        CollectionAssert.AreEqual(new[] { "part0", "part1", "part2" }, seen);
        await client.DisposeAsync();
        await agent.DisposeAsync();
    }

    [TestMethod]
    public async Task Errors_AndMissingMethods_ComeBackAsJsonRpcErrors()
    {
        var (client, agent) = Link();
        agent.RequestHandler = (req, ct) => req.Method == "boom"
            ? throw new JsonRpcException(JsonRpcException.ResourceNotFound, "no such session")
            : throw new JsonRpcException(JsonRpcException.MethodNotFound, "nope");
        client.Start();
        agent.Start();
        var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.SendRequestAsync("boom", null));
        Assert.AreEqual(JsonRpcException.ResourceNotFound, e.Code);
        var m = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.SendRequestAsync("other", new JsonObject()));
        Assert.AreEqual(JsonRpcException.MethodNotFound, m.Code);
        await client.DisposeAsync();
        await agent.DisposeAsync();
    }

    [TestMethod]
    public async Task Cancellation_SendsCancelRequest_AndCancelsTheHandler()
    {
        var (client, agent) = Link();
        var handlerCancelled = new TaskCompletionSource();
        agent.RequestHandler = async (req, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { handlerCancelled.TrySetResult(); throw; }
            return null;
        };
        client.Start();
        agent.Start();
        using var cts = new CancellationTokenSource();
        var call = client.SendRequestAsync("slow", null, cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => call);
        await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
        await agent.DisposeAsync();
    }

    [TestMethod]
    public async Task PendingRequests_FailWhenThePeerGoesAway()
    {
        var (client, agent) = Link();
        agent.RequestHandler = async (req, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; };
        client.Start();
        agent.Start();
        var call = client.SendRequestAsync("slow", null);
        await Task.Delay(50);
        await agent.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ACancelledRequest_CancelsTheHandler_WithTheConnectionsOwnCancelNotification()
    {
        // ACP's $/cancel_request by default; the peers server speaks MCP, whose notification is notifications/cancelled.
        foreach (var name in new[] { AcpMethods.Protocol.CancelRequest, "notifications/cancelled" })
        {
            var link = new DuplexLink();
            var (ci, co) = link.ClientEnd;
            var (ai, ao) = link.AgentEnd;
            await using var client = new JsonRpcConnection(ci, co, "client") { CancelNotification = name };
            await using var agent = new JsonRpcConnection(ai, ao, "agent") { CancelNotification = name };
            var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            agent.RequestHandler = async (req, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { handlerCancelled.TrySetResult(); throw; }
                return null;
            };
            client.Start();
            agent.Start();
            using var cts = new CancellationTokenSource();
            var call = client.SendRequestAsync("tools/call", new JsonObject());
            var cancelled = client.SendRequestAsync("tools/call", new JsonObject(), cts.Token);
            await Task.Delay(50);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
            await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(call.IsCompleted, $"{name} cancels only the request it names");
        }
    }

    /// <summary>
    /// Every write on a connection disposed before or while it writes fails as a closed connection
    /// (an IOException), the failure callers take for "the peer is gone": the service then asks the
    /// next client, so an approval is not lost to a client that left just as it was asked
    /// (ConductorTests.PendingApproval_WaitsForTheNextClient). Not a cancellation, and not the
    /// disposed object a write met when it came after DisposeAsync had disposed its token source.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WritesOnADisposedConnection_FailAsAClosedConnection()
    {
        var link = new DuplexLink();
        var (ci, _) = link.ClientEnd;
        var output = new StuckStream();
        var rpc = new JsonRpcConnection(ci, output, "client");
        rpc.Start();
        var stuck = rpc.SendNotificationAsync("stuck", null);
        await output.Writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var request = rpc.SendRequestAsync("waiting", null);
        var notification = rpc.SendNotificationAsync("waiting", null);

        await rpc.DisposeAsync();

        await Assert.ThrowsExactlyAsync<IOException>(() => stuck.WaitAsync(TimeSpan.FromSeconds(10)), "cut off while writing");
        await Assert.ThrowsExactlyAsync<IOException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)), "a request waiting to write");
        await Assert.ThrowsExactlyAsync<IOException>(() => notification.WaitAsync(TimeSpan.FromSeconds(10)), "a notification waiting to write");
        await Assert.ThrowsExactlyAsync<IOException>(() => rpc.SendNotificationAsync("after", null), "a notification after");
        await Assert.ThrowsExactlyAsync<IOException>(() => rpc.SendRequestAsync("after", null), "a request after");
    }

    /// <summary>An output that takes nothing: a write waits until it is cancelled.</summary>
    sealed class StuckStream : Stream
    {
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writing.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
