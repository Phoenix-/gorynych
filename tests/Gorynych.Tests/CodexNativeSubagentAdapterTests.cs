using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex;

namespace Gorynych.Tests;

[TestClass]
public sealed class CodexNativeSubagentAdapterTests
{
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task ReplayAnnouncesChildBeforeItsTranscript(bool capable, bool fromMark)
    {
        var fake = new FakeAppServer();
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var root = fake.Existing("parent", cwd);
        root.Items.Insert(1, ("turn-0", new JsonObject { ["type"] = "subAgentActivity", ["id"] = "spawn",
            ["kind"] = "started", ["agentThreadId"] = "child", ["agentPath"] = "/root/child" }));
        fake.Existing("child", cwd);
        await using var agent = await new CodexAdapter(new CodexAdapterOptions
        {
            Launch = fake.StartAsync, FindHolders = _ => new Dictionary<string, string>(),
            LocksDirectory = Path.Combine(cwd, "codex-no-locks"),
        }).StartAsync(CancellationToken.None);
        var events = new ConcurrentQueue<JsonNode>();
        await using var client = new JsonRpcConnection(agent.Input, agent.Output, "native-replay-test")
        { NotificationHandler = (_, p) => { if (p is not null) events.Enqueue(p.DeepClone()); return Task.CompletedTask; } };
        client.Start();
        await client.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = capable ? new JsonObject { ["subagents"] = new JsonObject() } : new JsonObject() });
        var original = fake.Rpc!.RequestHandler!;
        JsonObject ChildThread() => new() { ["id"] = "child", ["parentThreadId"] = "parent", ["cwd"] = cwd };
        fake.Rpc.RequestHandler = (r, ct) => r.Method switch
        {
            "thread/read" when (string?)r.Params?["threadId"] == "child" =>
                Task.FromResult<JsonNode?>(new JsonObject { ["thread"] = ChildThread() }),
            "thread/list" when (string?)r.Params?["ancestorThreadId"] == "parent" =>
                Task.FromResult<JsonNode?>(new JsonObject { ["data"] = new JsonArray(ChildThread()) }),
            _ => original(r, ct),
        };
        var open = new JsonObject { ["sessionId"] = "parent", ["cwd"] = cwd, ["mcpServers"] = new JsonArray() };
        if (fromMark) open["_meta"] = new JsonObject
        {
            [GorynychProtocol.MetaReplayFrom] = new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = "turn-0" },
        };
        await client.SendRequestAsync(fromMark ? "session/resume" : "session/load", open);
        await client.SendRequestAsync(GorynychProtocol.MethodRefreshSubagentStates, new JsonObject { ["sessionId"] = "parent" });
        Assert.IsFalse(events.Any(n => n["update"]?["_meta"]?[GorynychProtocol.MetaSubagentSnapshot] is not null),
            "Replayed turns do not become current observations");
        var snapshot = events.ToArray();
        var announce = Array.FindIndex(snapshot, n => (string?)n["update"]?["sessionUpdate"] == "subagent_update");
        var incoming = Array.FindIndex(snapshot, n => (string?)n["sessionId"] == "child" && (string?)n["update"]?["sessionUpdate"] == "session_message");
        var text = Array.FindIndex(snapshot, n => (string?)n["sessionId"] == "child" && (string?)n["update"]?["sessionUpdate"] == "agent_message_chunk");
        if (capable)
        {
            Assert.IsTrue(announce >= 0 && incoming > announce && text > incoming);
            Assert.IsNull(snapshot[incoming]["update"]?["senderSessionId"]);
        }
        else
        {
            Assert.AreEqual(-1, announce);
            Assert.AreEqual(-1, incoming);
            Assert.AreEqual(-1, text);
            Assert.IsTrue(snapshot.Any(n => (string?)n["sessionId"] == "parent" && (string?)n["update"]?["sessionUpdate"] == "tool_call"));
        }
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    public async Task AssociationPrecedesChildOutputAndApproval(bool capable, bool internalMarkers)
    {
        var fake = new FakeAppServer();
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var agent = await new CodexAdapter(new CodexAdapterOptions
        {
            Launch = fake.StartAsync, FindHolders = _ => new Dictionary<string, string>(),
            LocksDirectory = Path.Combine(cwd, "codex-no-locks"),
        }).StartAsync(CancellationToken.None);
        await using var owned = agent;
        var messages = new ConcurrentQueue<JsonNode>();
        await using var client = new JsonRpcConnection(agent.Input, agent.Output, "native-child-test")
        {
            NotificationHandler = (_, p) => { if (p is not null) messages.Enqueue(p.DeepClone()); return Task.CompletedTask; },
            RequestHandler = (r, _) =>
            {
                messages.Enqueue(r.Params!.DeepClone());
                return Task.FromResult<JsonNode?>(new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = "decline" } });
            },
        };
        client.Start();
        await client.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = capable ? new JsonObject { ["subagents"] = new JsonObject() } : new JsonObject(),
            ["_meta"] = internalMarkers ? new JsonObject { [GorynychProtocol.MetaSubagentLiveState] = true } : null });
        var created = await client.SendRequestAsync("session/new", new JsonObject { ["cwd"] = cwd, ["mcpServers"] = new JsonArray() });
        var parent = (string)created!["sessionId"]!;
        var original = fake.Rpc!.RequestHandler!;
        var childReads = 0;
        fake.Rpc.RequestHandler = (r, ct) =>
        {
            if (r.Method != "thread/read" || (string?)r.Params?["threadId"] != "child") return original(r, ct);
            // The first read below tests direct load; the first read during announcement races
            // the rollout header, as observed from Codex 0.170.0.
            if (Interlocked.Increment(ref childReads) == 2)
                throw new JsonRpcException(JsonRpcException.InternalError, "failed to read session metadata: rollout at test is empty");
            return Task.FromResult<JsonNode?>(new JsonObject { ["thread"] = new JsonObject { ["id"] = "child", ["parentThreadId"] = parent, ["cwd"] = cwd } });
        };
        await Assert.ThrowsAsync<JsonRpcException>(() => client.SendRequestAsync("session/load", new JsonObject
        { ["sessionId"] = "child", ["cwd"] = cwd, ["mcpServers"] = new JsonArray() }));
        await fake.Rpc.SendNotificationAsync("item/started", new JsonObject
        {
            ["threadId"] = parent, ["turnId"] = "t", ["item"] = new JsonObject
            { ["type"] = "subAgentActivity", ["id"] = "call", ["kind"] = "started", ["agentThreadId"] = "child", ["agentPath"] = "/root/child" },
        });
        await fake.Rpc.SendNotificationAsync("item/agentMessage/delta", new JsonObject
        { ["threadId"] = "child", ["turnId"] = "ct", ["itemId"] = "m", ["delta"] = "hello" });
        await fake.Rpc.SendNotificationAsync("thread/status/changed", new JsonObject
        { ["threadId"] = "child", ["status"] = new JsonObject { ["type"] = "active", ["activeFlags"] = new JsonArray("waitingOnApproval") } });
        var decision = await fake.Rpc.SendRequestAsync("item/commandExecution/requestApproval", new JsonObject
        { ["threadId"] = "child", ["turnId"] = "ct", ["itemId"] = "tool", ["command"] = "example", ["cwd"] = cwd }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("decline", (string?)decision?["decision"]);
        var snapshot = messages.ToArray();
        var announcement = Array.FindIndex(snapshot, n => (string?)n["update"]?["sessionUpdate"] == "subagent_update");
        var text = Array.FindIndex(snapshot, n => (string?)n["sessionId"] == "child" && (string?)n["update"]?["sessionUpdate"] == "agent_message_chunk");
        var approval = Array.FindIndex(snapshot, n => (string?)n["sessionId"] == (capable ? "child" : parent) && n["toolCall"] is not null);
        if (capable)
        {
            var waiting = Array.FindIndex(snapshot, n => (string?)n["update"]?["state"]?["state"] == "requires_action");
            Assert.IsTrue(announcement >= 0 && text > announcement && waiting > text && approval > waiting);
            Assert.AreEqual(internalMarkers, (bool?)snapshot[waiting]["update"]?["_meta"]?[GorynychProtocol.MetaSubagentLiveState] == true);
            await client.SendRequestAsync(GorynychProtocol.MethodRefreshSubagentStates, new JsonObject { ["sessionId"] = "unrelated" });
            Assert.IsFalse(messages.Any(n => n["update"]?["_meta"]?[GorynychProtocol.MetaSubagentSnapshot] is not null));
            await client.SendRequestAsync(GorynychProtocol.MethodRefreshSubagentStates, new JsonObject { ["sessionId"] = parent });
            var current = messages.Single(n => n["update"]?["_meta"]?[GorynychProtocol.MetaSubagentSnapshot] is not null);
            Assert.AreEqual("requires_action", (string?)current["update"]?["state"]?["state"]);
        }
        else
        {
            Assert.AreEqual(-1, announcement);
            Assert.AreEqual(-1, text);
            Assert.IsTrue(approval >= 0);
            Assert.IsTrue(snapshot.Any(n => (string?)n["sessionId"] == parent && (string?)n["update"]?["sessionUpdate"] == "tool_call"));
        }
        Assert.AreEqual(3, childReads);
        if (capable) Assert.AreEqual(parent, (string?)snapshot[announcement]["sessionId"]);
        await Assert.ThrowsAsync<JsonRpcException>(() => client.SendRequestAsync("session/prompt", new JsonObject
        { ["sessionId"] = "child", ["prompt"] = new JsonArray() }));
    }
}
