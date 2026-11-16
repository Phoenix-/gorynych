using System.Text.Json.Nodes;
using System.Diagnostics;
using System.IO.Pipes;
using System.Collections.Concurrent;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Outside;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class OutsideMailboxTests
{
    const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    static Task<JsonNode?> Call(TestClient client, string? token, string tool, JsonObject args, bool modern = false, string? id = null, CancellationToken ct = default)
    {
        var inner = new JsonObject { ["name"] = tool, ["arguments"] = args };
        if (modern) inner["_meta"] = new JsonObject { [OutsideTools.MetaProtocolVersion] = OutsideTools.ModernVersion };
        var meta = new JsonObject { [GorynychProtocol.MetaOutsideMailbox] = token, [GorynychProtocol.MetaMessageRequestId] = id ?? Guid.NewGuid().ToString("N") };
        return client.Rpc.SendRequestAsync(GorynychProtocol.MethodMcpMessage, new JsonObject { ["serverId"] = "gorynych-outside",
            ["requestId"] = Guid.NewGuid().ToString("N"), ["method"] = "tools/call", ["params"] = inner, ["_meta"] = meta }, ct);
    }

    static JsonNode Payload(JsonNode? response) => JsonNode.Parse((string)response!["result"]!["content"]![0]!["text"]!)!;

    [TestMethod]
    public void BothErasExposePullMailbox()
    {
        foreach (var modern in new[] { false, true })
        {
            var tools = OutsideTools.ToolsList(modern, false)["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToArray();
            CollectionAssert.AreEquivalent(new[] { "list_sessions", "read_session", "send_message", "reply_message", "receive_messages" }, tools);
            Assert.AreEqual(modern ? "complete" : null, (string?)OutsideTools.ToolsList(modern, false)["resultType"]);
        }
    }

    [TestMethod]
    public async Task IdentityReplyPagingAndLongPollCancellation()
    {
        var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "outside-test" }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(conductor);
        var session = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Path.GetTempPath(), McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" } })).SessionId;
        var requestId = Guid.NewGuid().ToString("N");
        var args = new JsonObject { ["session_id"] = session, ["text"] = "hello", ["sender"] = "forged" };
        var first = await Call(client, A, "send_message", args, id: requestId);
        Assert.AreEqual("accepted", (string?)Payload(first)["status"]);
        var again = await Call(client, A, "send_message", args.DeepClone().AsObject(), id: requestId, modern: true);
        Assert.AreEqual(requestId, (string?)Payload(again)["messageId"]);
        var invalid = await Call(client, null, "receive_messages", []);
        Assert.AreEqual(true, (bool?)invalid!["result"]!["isError"]);
        await conductor.AcceptMailboxAsync(session, requestId, "answer", Guid.NewGuid().ToString("N"), true, CancellationToken.None);
        var page = Payload(await Call(client, A, "receive_messages", new JsonObject { ["limit"] = 1 }, modern: true));
        Assert.AreEqual(1, page["messages"]!.AsArray().Count);
        Assert.IsNull(page["messages"]![0]!["recipient"]);
        var cursor = (long)page["nextCursor"]!;
        Assert.AreEqual(page.ToJsonString(), Payload(await Call(client, A, "receive_messages", new JsonObject { ["limit"] = 1 })).ToJsonString());
        Assert.AreEqual(0, Payload(await Call(client, B, "receive_messages", []))["messages"]!.AsArray().Count);
        var stolen = await Call(client, B, "reply_message", new JsonObject { ["message_id"] = (string)page["messages"]![0]!["id"]!, ["text"] = "forged" });
        Assert.AreEqual(true, (bool?)stolen!["result"]!["isError"]);
        var waiting = Call(client, A, "receive_messages", new JsonObject { ["after"] = cursor, ["wait_ms"] = 10000 });
        await Task.Delay(50);
        Assert.IsFalse(waiting.IsCompleted, "external reception blocks while its mailbox is empty");
        await conductor.AcceptMailboxAsync(session, requestId, "next answer", Guid.NewGuid().ToString("N"), true, CancellationToken.None);
        var awakened = Payload(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("next answer", (string?)awakened["messages"]![0]!["text"]);
        cursor = (long)awakened["nextCursor"]!;
        var reply = await Call(client, A, "reply_message", new JsonObject { ["message_id"] = (string)page["messages"]![0]!["id"]!, ["text"] = "follow-up" }, modern: true);
        Assert.AreEqual("accepted", (string?)Payload(reply)["status"]);
        var badWait = await Call(client, A, "receive_messages", new JsonObject { ["wait_ms"] = 60001 });
        Assert.AreEqual(true, (bool?)badWait!["result"]!["isError"]);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await Call(client, A, "receive_messages",
            new JsonObject { ["after"] = cursor, ["wait_ms"] = 60000 }, ct: cancel.Token));
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ShimReconnectKeepsPrivateIdentityAndRequestId()
    {
        if (Ancestry.OfCurrentProcess().Inside) Assert.Inconclusive("Run this test in the isolated runner outside Gorynych ancestry.");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gorynych.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        var exe = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Gorynych.Outside", "bin", "Release"),
            TestDirs.Exe("Gorynych.Outside"), SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        var pipe = "outside-mailbox-test-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(TestDirs.New(), "identity");
        var requests = new List<JsonObject>();
        var events = new ConcurrentQueue<string>();
        var ready = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var listening = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    var index = i;
                    using var stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    events.Enqueue($"listener {index} ready");
                    ready[index].TrySetResult();
                    await stream.WaitForConnectionAsync(stop.Token);
                    events.Enqueue($"listener {index} connected");
                    await using var rpc = new JsonRpcConnection(stream, stream, "outside-mailbox-fake");
                    rpc.Log = message => events.Enqueue($"listener {index}: {message}");
                    rpc.RequestHandler = async (req, _) =>
                    {
                        events.Enqueue($"listener {index} method {req.Method}");
                        if (req.Method == AcpMethods.Agent.Initialize) return ScriptedAgent.Init();
                        lock (requests) requests.Add(req.Params!.DeepClone().AsObject());
                        if (index == 0) { await rpc.DisposeAsync(); return null; }
                        return new JsonObject { ["result"] = OutsideTools.ToolResult("{\"messageId\":\"ok\",\"status\":\"accepted\"}", false, false) };
                    };
                    rpc.Start();
                    await rpc.Completion.WaitAsync(stop.Token);
                    events.Enqueue($"listener {index} closed");
                }
            }
            catch (Exception e) { events.Enqueue($"listener failed: {e.GetType().Name}: {e.Message}"); throw; }
        }, stop.Token);
        var psi = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--pipe", pipe, "--service", Path.Combine(TestDirs.New(), "missing.exe"), "--log", TestDirs.New(), "--mailbox-file", path }) psi.ArgumentList.Add(arg);
        Process? process = null, nextProcess = null;
        JsonRpcConnection? host = null, nextHost = null;
        try
        {
        await ready[0].Task.WaitAsync(stop.Token);
        process = Process.Start(psi)!;
        host = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, "outside-mailbox-host");
        host.Start();
        var result = await host.SendRequestAsync("tools/call", new JsonObject { ["name"] = "send_message", ["arguments"] = new JsonObject
        { ["session_id"] = "target", ["text"] = "hello", ["sender"] = "forged" }, ["_meta"] = new JsonObject { [GorynychProtocol.MetaOutsideMailbox] = B } }, stop.Token);
        Assert.AreEqual(false, (bool?)result!["isError"]);
        Assert.AreEqual(2, requests.Count);
        var token = File.ReadAllText(path);
        Assert.AreEqual(32, token.Length);
        Assert.AreNotEqual(B, token);
        foreach (var request in requests)
        {
            Assert.AreEqual(token, (string?)request["_meta"]![GorynychProtocol.MetaOutsideMailbox]);
            Assert.AreEqual((string?)requests[0]["_meta"]![GorynychProtocol.MetaMessageRequestId], (string?)request["_meta"]![GorynychProtocol.MetaMessageRequestId]);
        }
        await host.DisposeAsync();
        if (!process.WaitForExit(5000)) process.Kill();
        await ready[2].Task.WaitAsync(stop.Token);
        nextProcess = Process.Start(psi)!;
        nextHost = new JsonRpcConnection(nextProcess.StandardOutput.BaseStream, nextProcess.StandardInput.BaseStream, "outside-mailbox-host-restarted");
        nextHost.Start();
        var again = await nextHost.SendRequestAsync("tools/call", new JsonObject { ["name"] = "send_message", ["arguments"] = new JsonObject
        { ["session_id"] = "target", ["text"] = "next" } }, stop.Token);
        Assert.AreEqual(false, (bool?)again!["isError"]);
        Assert.AreEqual(3, requests.Count);
        Assert.AreEqual(token, (string?)requests[2]["_meta"]![GorynychProtocol.MetaOutsideMailbox]);
        Assert.AreNotEqual((string?)requests[0]["_meta"]![GorynychProtocol.MetaMessageRequestId], (string?)requests[2]["_meta"]![GorynychProtocol.MetaMessageRequestId]);
        await nextHost.DisposeAsync();
        await listening.WaitAsync(stop.Token);
        if (!nextProcess.WaitForExit(5000)) nextProcess.Kill();
        }
        catch (Exception e)
        {
            throw new AssertFailedException($"Shim reconnect failed: {e}\nFixture events:\n{string.Join("\n", events)}", e);
        }
        finally
        {
            stop.Cancel();
            if (nextHost is not null) await nextHost.DisposeAsync();
            if (host is not null) await host.DisposeAsync();
            foreach (var owned in new[] { nextProcess, process })
            {
                if (owned is null) continue;
                try { if (!owned.HasExited) { owned.Kill(); await owned.WaitForExitAsync(); } }
                finally { owned.Dispose(); }
            }
            try { await listening; } catch (Exception) { /* reported above on failure */ }
        }
    }
}
