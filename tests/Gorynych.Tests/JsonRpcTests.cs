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
}
