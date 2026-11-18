using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class SubagentRoutingTests
{
    const string Root = "root";
    const string Child = "child";
    const string Grandchild = "grandchild";
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void ASecondRootCannotClaimAnAnnouncedChild()
    {
        var routes = new SubagentRouting();
        Assert.IsFalse(routes.Announce(Child, Grandchild, Root));
        Assert.IsTrue(routes.Announce(Root, Child, Root));
        Assert.IsTrue(routes.Announce(Child, Grandchild, Root));
        Assert.IsFalse(routes.Announce("another root", Child, "another root"));
        Assert.IsFalse(routes.Announce(Grandchild, Root, Root));
        Assert.AreEqual(Root, routes.RootOf(Grandchild));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CatchUpHistoryCannotOverwriteLiveState(bool replace)
    {
        var adapter = new ScriptedAdapter("codex", AgentRequestAsync);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "test" }, [adapter]);
        await using var client = await TestClient.ConnectAsync(service, "modern", subagents: true);
        await client.Rpc.SendRequestAsync("session/new", Session());
        await using var legacy = await TestClient.ConnectAsync(service, "legacy");
        await legacy.Rpc.SendRequestAsync("session/resume", Session());
        var feed = service.Feed(Root);
        var source = service.Agents["codex"].RouteOf(Root)!.Rpc;
        var running = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = Child,
            ["state"] = new JsonObject { ["state"] = "running" } };
        await feed.PublishAsync(running, null, () => { }, stateSource: source);
        var history = new List<JsonObject> { new() { ["sessionUpdate"] = "subagent_update", ["sessionId"] = Child,
            ["title"] = "historical", ["state"] = new JsonObject { ["state"] = "idle", ["stopReason"] = "end_turn" } } };
        if (replace) await feed.ReplaceHistoryAsync(history, DateTimeOffset.UtcNow, null, false);
        else await feed.AppendHistoryAsync(history);
        await WaitForAsync(() => client.Snapshot().Any(x => (string?)x["title"] == "historical"));
        var updates = client.SessionUpdates.Where(x => (string?)x.Update["title"] == "historical").ToArray();
        Assert.AreEqual(1, updates.Length);
        Assert.IsNull(updates[0].Update["state"]);
        Assert.IsFalse(legacy.Kinds().Contains("subagent_update"));
        Assert.IsTrue(feed.Journal.ReadAll().Any(x => (string?)x["state"]?["state"] == "idle"));
    }

    [TestMethod]
    public async Task StreamingImportWithoutPreviousTurnsDoesNotPublishHistoricalState()
    {
        var adapter = new ScriptedAdapter("codex", AgentRequestAsync);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "test" }, [adapter]);
        await using var client = await TestClient.ConnectAsync(service, "modern", subagents: true);
        await client.Rpc.SendRequestAsync("session/new", Session());
        var host = service.Agents["codex"];
        var source = host.RouteOf(Root)!.Rpc;
        var feed = service.Feed(Root);
        Assert.IsFalse(feed.Journal.HasTurns);
        feed.BeginHistoryImport(source);
        try
        {
            await service.OnAgentNotificationAsync(host, "session/update", new JsonObject
            {
                ["sessionId"] = Root, ["update"] = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = Child,
                    ["title"] = "imported", ["state"] = new JsonObject { ["state"] = "idle", ["stopReason"] = "end_turn" } },
            }, source);
        }
        finally { feed.EndHistoryImport(source); }
        await WaitForAsync(() => client.Snapshot().Any(x => (string?)x["title"] == "imported"));
        Assert.IsNull(client.Snapshot().Single(x => (string?)x["title"] == "imported")["state"]);
        Assert.IsTrue(feed.Journal.ReadAll().Any(x => (string?)x["state"]?["state"] == "idle"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LiveStateDuringSameConnectionCatchUpIsNotTreatedAsHistory(bool collect)
    {
        var adapter = new ScriptedAdapter("codex", AgentRequestAsync);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "test" }, [adapter]);
        await using var client = await TestClient.ConnectAsync(service, "modern", subagents: true);
        await client.Rpc.SendRequestAsync("session/new", Session());
        await using var legacy = await TestClient.ConnectAsync(service, "legacy");
        await legacy.Rpc.SendRequestAsync("session/resume", Session());
        var host = service.Agents["codex"];
        var link = host.RouteOf(Root)!;
        var feed = service.Feed(Root);
        JsonObject State(string state, bool live) => new()
        {
            ["sessionId"] = Root,
            ["update"] = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = Child,
                ["state"] = new JsonObject { ["state"] = state },
                ["_meta"] = live ? new JsonObject { [GorynychProtocol.MetaSubagentLiveState] = true } : null },
        };
        feed.BeginHistoryImport(link.Rpc);
        if (collect) feed.BeginReplay(feed.Journal.MaxEntries);
        try
        {
            await service.OnAgentNotificationAsync(host, "session/update", State("running", true), link.Rpc);
            await service.OnAgentNotificationAsync(host, "session/update", State("idle", false), link.Rpc);
        }
        finally { feed.EndHistoryImport(link.Rpc); }
        if (collect)
        {
            var history = feed.EndReplay().Updates;
            Assert.AreEqual(1, history.Count);
            Assert.AreEqual("idle", (string?)history[0]["state"]?["state"]);
            await feed.AppendHistoryAsync(history);
        }
        await feed.PublishAsync(new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["messageId"] = "barrier",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = "barrier" } }, null, () => { });
        await WaitForAsync(() => client.Snapshot().Any(x => (string?)x["messageId"] == "barrier"));
        var states = client.Snapshot().Where(x => (string?)x["sessionId"] == Child && x["state"] is not null).ToArray();
        Assert.AreEqual(1, states.Length);
        Assert.AreEqual("running", (string?)states[0]["state"]?["state"]);
        Assert.IsNull(states[0]["_meta"]?[GorynychProtocol.MetaSubagentLiveState]);
        Assert.IsFalse(legacy.Kinds().Contains("subagent_update"));
        Assert.IsFalse(feed.Journal.ReadAll().Any(x => x["_meta"]?[GorynychProtocol.MetaSubagentLiveState] is not null));
        service.OnAgentStopped(host, link, [Root]);
        await WaitForAsync(() => client.Snapshot().Any(x => (string?)x["state"]?["state"] == "unknown"));
    }

    [TestMethod]
    public async Task LostHostOnlyWithdrawsItsOwnLatestNestedStates()
    {
        var adapter = new ScriptedAdapter("codex", AgentRequestAsync);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "test" }, [adapter]);
        await using var client = await TestClient.ConnectAsync(service, "modern", subagents: true);
        await client.Rpc.SendRequestAsync("session/new", Session());
        await using var legacy = await TestClient.ConnectAsync(service, "legacy");
        await legacy.Rpc.SendRequestAsync("session/resume", Session());
        var feed = service.Feed(Root);
        var old = service.Agents["codex"].RouteOf(Root)!;
        JsonObject State(string child, string? parent = null)
        {
            var update = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = child,
                ["state"] = new JsonObject { ["state"] = "running" } };
            if (parent is not null) SubagentRouting.SetSession(update, parent);
            return update;
        }
        await feed.PublishAsync(State(Child), null, () => { }, stateSource: old.Rpc);
        await feed.PublishAsync(State(Grandchild, Child), null, () => { }, stateSource: old.Rpc);
        service.OnAgentStopped(service.Agents["codex"], old, [Root]);
        await WaitForAsync(() => client.Snapshot().Count(x => (string?)x["state"]?["state"] == "unknown") == 2);
        var unknown = client.SessionUpdates.Where(x => (string?)x.Update["state"]?["state"] == "unknown").ToArray();
        CollectionAssert.AreEqual(new[] { Root, Child }, unknown.Select(x => x.SessionId).ToArray());
        CollectionAssert.AreEqual(new[] { Child, Grandchild }, unknown.Select(x => (string?)x.Update["sessionId"]).ToArray());
        Assert.IsFalse(legacy.Kinds().Contains("subagent_update"));
        Assert.IsFalse(feed.Journal.ReadAll().Any(x => (string?)x["state"]?["state"] == "unknown"));
        await feed.SubagentHostLostAsync(old.Rpc);
        Assert.AreEqual(2, client.SessionUpdates.Count(x => (string?)x.Update["state"]?["state"] == "unknown"));

        // A replacement host's later observation must survive a delayed old-host callback.
        var link = new DuplexLink();
        var (input, output) = link.ClientEnd;
        await using var replacement = new JsonRpcConnection(input, output, "replacement");
        await feed.PublishAsync(State(Child), null, () => { }, stateSource: old.Rpc);
        await feed.PublishAsync(State(Child), null, () => { }, stateSource: replacement);
        await feed.SubagentHostLostAsync(old.Rpc);
        await feed.PublishAsync(new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["messageId"] = "barrier",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = "barrier" } }, null, () => { });
        await WaitForAsync(() => client.Snapshot().Any(x => (string?)x["messageId"] == "barrier"));
        Assert.AreEqual(2, client.SessionUpdates.Count(x => (string?)x.Update["state"]?["state"] == "unknown"));
        await feed.SubagentHostLostAsync(replacement);
        await WaitForAsync(() => client.Snapshot().Count(x => (string?)x["state"]?["state"] == "unknown") == 3);
        Assert.AreEqual(3, client.SessionUpdates.Count(x => (string?)x.Update["state"]?["state"] == "unknown"));
    }

    static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("claude")]
    public async Task HostCursorsRestoresNestedJournalRoutes(string adapterName)
    {
        var dir = TestDirs.New();
        var options = new ConductorOptions { DataDirectory = dir, ServiceId = "test" };
        var firstAdapter = new ScriptedAdapter(adapterName, AgentRequestAsync);
        await using (var first = new Conductor(options, [firstAdapter]))
        {
            await using var client = await TestClient.ConnectAsync(first, "first", subagents: true);
            await client.Rpc.SendRequestAsync("session/new", Session());
            await client.Rpc.SendRequestAsync("session/prompt", new JsonObject { ["sessionId"] = Root,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "delegate" }) }).WaitAsync(Wait);
        }
        var restoredAdapter = new ScriptedAdapter(adapterName, AgentRequestAsync);
        var wrongAdapter = new ScriptedAdapter(adapterName == "codex" ? "claude" : "codex", AgentRequestAsync);
        await using var restored = new Conductor(options, [restoredAdapter, wrongAdapter]);
        Assert.IsNull(restored.RootOfSubagent(Grandchild));
        restored.HostCursors("host", [Root]);
        Assert.AreEqual(Root, restored.RootOfSubagent(Child));
        Assert.AreEqual(Root, restored.RootOfSubagent(Grandchild));
        await using var reader = await TestClient.ConnectAsync(restored, "reader", subagents: true);
        await reader.Rpc.SendRequestAsync("session/resume", Session());
        var notification = new JsonObject { ["sessionId"] = Grandchild, ["update"] = new JsonObject {
            ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "after restart" } } };
        await restored.OnAgentNotificationAsync(restored.Agents[wrongAdapter.Name], "session/update", (JsonObject)notification.DeepClone());
        Assert.IsFalse(reader.SessionUpdates.Any(x => (string?)x.Update["content"]?["text"] == "after restart"));
        await restored.OnAgentNotificationAsync(restored.Agents[adapterName], "session/update", notification);
        await WaitForAsync(() => reader.SessionUpdates.Any(x => x.SessionId == Grandchild && (string?)x.Update["content"]?["text"] == "after restart"));
    }

    [TestMethod]
    public void ChildReplayDeduplicatesItsStreamWithoutUsingRootMessageIds()
    {
        var rootMessage = AcpJson.ToNode<SessionUpdate>(new SessionUpdateAgentMessageChunk
        { MessageId = "message", Content = new TextContent { Text = "root" } })!.AsObject();
        JsonObject ChildMessage(string id, string text)
        {
            var child = (JsonObject)rootMessage.DeepClone();
            child["content"]!["text"] = text;
            SubagentRouting.SetSession(child, id);
            return child;
        }
        var first = ChildMessage(Child, "live chunk");
        var replay = new[] { ChildMessage(Child, "live chunk in a full message"), ChildMessage(Grandchild, "grandchild") };
        var unseen = SubagentRouting.NewHistory(Root, [rootMessage, first], replay);
        CollectionAssert.AreEqual(new[] { Child, Grandchild }, unseen.Select(SubagentRouting.SessionOf).ToArray());
        Assert.AreEqual(" in a full message", (string?)unseen[0]["content"]?["text"]);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("claude")]
    public async Task LiveAndReplayUseChildIdsOnlyForCapableClients(string adapterName)
    {
        var adapter = new ScriptedAdapter(adapterName, AgentRequestAsync);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "test" }, [adapter]);
        await using var modern = await TestClient.ConnectAsync(service, "modern", subagents: true);
        await modern.Rpc.SendRequestAsync("session/new", new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() });
        await using var legacy = await TestClient.ConnectAsync(service, "legacy");
        await legacy.Rpc.SendRequestAsync("session/resume", Session());
        var outcome = await modern.Rpc.SendRequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = Root,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "delegate" }),
        }).WaitAsync(Wait);
        Assert.AreEqual("end_turn", (string?)outcome?["stopReason"]);
        Assert.AreEqual(Grandchild, modern.Approvals.Single().SessionId);
        Assert.AreEqual(0, legacy.Approvals.Count);
        AssertPreviewOrder(modern);
        AssertLegacy(legacy);
        await Assert.ThrowsAsync<JsonRpcException>(() => modern.Rpc.SendRequestAsync("session/load", Session(Child)));

        await modern.DisposeAsync();
        await AskAsync(adapter.Current!).WaitAsync(Wait);
        Assert.AreEqual(Root, legacy.Approvals.Single().SessionId);

        await using var replay = await TestClient.ConnectAsync(service, "replay", subagents: true);
        await replay.Rpc.SendRequestAsync("session/load", Session()).WaitAsync(Wait);
        AssertPreviewOrder(replay);
        await using var legacyReplay = await TestClient.ConnectAsync(service, "legacy replay");
        await legacyReplay.Rpc.SendRequestAsync("session/load", Session()).WaitAsync(Wait);
        AssertLegacy(legacyReplay);
    }

    [TestMethod]
    [DataRow("session/load")]
    [DataRow("session/resume")]
    public async Task CurrentChildStateFollowsTheReplayResponse(string method)
    {
        JsonObject State(bool current) => new()
        {
            ["sessionUpdate"] = "subagent_update",
            ["sessionId"] = Child,
            ["state"] = new JsonObject { ["state"] = "idle", ["stopReason"] = "end_turn" },
            ["_meta"] = current ? new JsonObject { [GorynychProtocol.MetaSubagentSnapshot] = true } : null,
        };
        var adapter = new ScriptedAdapter("codex", async (agent, request, ct) =>
        {
            if (request.Method == "session/prompt")
            {
                await agent.Update(Root, new SubagentUpdate { SessionId = Child });
                await agent.Update(Root, new SessionUpdate.Unknown("subagent_update", State(false)));
                return new JsonObject { ["stopReason"] = "end_turn" };
            }
            if (request.Method == GorynychProtocol.MethodRefreshSubagentStates)
            {
                await agent.Update(Root, new SessionUpdate.Unknown("subagent_update", State(true)));
                return new JsonObject();
            }
            return await AgentRequestAsync(agent, request, ct);
        });
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "test" }, [adapter]);
        await using var author = await TestClient.ConnectAsync(service, "author", subagents: true);
        await author.Rpc.SendRequestAsync("session/new", Session());
        await author.Rpc.SendRequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = Root, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "delegate" }),
        });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var order = new ConcurrentQueue<string>();
            var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reader = await TestClient.ConnectAsync(service, "reader", subagents: true);
            reader.Rpc.Trace = (direction, line) =>
            {
                if (direction != "<") return;
                var packet = JsonNode.Parse(line);
                if (packet?["result"] is not null) order.Enqueue("result");
                if (packet?["params"]?["update"] is not JsonObject update || (string?)update["state"]?["state"] != "idle") return;
                order.Enqueue(update["_meta"]?[GorynychProtocol.MetaSubagentSnapshot] is null ? "state" : "private marker");
                if (order.Contains("result")) confirmed.TrySetResult();
            };
            var session = Session();
            if (method == "session/resume")
                session["_meta"] = new JsonObject
                { [GorynychProtocol.MetaReplayFrom] = new JsonObject { ["type"] = "start" } };
            await reader.Rpc.SendRequestAsync(method, session).WaitAsync(Wait);
            await confirmed.Task.WaitAsync(Wait);
            CollectionAssert.AreEqual(new[] { "state", "result", "state" }, order.ToArray());
        }
    }

    static void AssertPreviewOrder(TestClient client)
    {
        var updates = client.SessionUpdates.Where(entry => (string?)entry.Update["sessionUpdate"] == "subagent_update"
            || entry.SessionId == Grandchild).ToArray();
        CollectionAssert.AreEqual(new[] { Root, Child, Grandchild }, updates.Select(entry => entry.SessionId).ToArray());
        Assert.AreEqual(Child, (string?)updates[0].Update["sessionId"]);
        Assert.AreEqual(Grandchild, (string?)updates[1].Update["sessionId"]);
        Assert.AreEqual("child text", (string?)updates[2].Update["content"]?["text"]);
        Assert.IsNull(updates[2].Update["_meta"]?[SubagentRouting.MetaSession]);
        var task = client.Snapshot().Single(update => (string?)update["sessionUpdate"] == "session_message");
        Assert.AreEqual(Child, (string?)task["recipientSessionId"]);
        var tool = client.Snapshot().Single(update => (string?)update["sessionUpdate"] == "tool_call");
        Assert.AreEqual(Child, (string?)tool["content"]?[0]?["sessionId"]);
    }

    static void AssertLegacy(TestClient client)
    {
        Assert.IsTrue(client.SessionUpdates.All(entry => entry.SessionId == Root));
        Assert.IsFalse(client.Kinds().Any(kind => kind is "subagent_update" or "session_message"));
        var tool = client.Snapshot().Single(update => (string?)update["sessionUpdate"] == "tool_call");
        Assert.AreEqual(0, tool["content"]!.AsArray().Count);
    }

    static JsonObject Session(string id = Root) => new()
    {
        ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray(),
    };

    static Task<JsonNode?> AskAsync(ScriptedAgent agent) => agent.Rpc.SendRequestAsync("session/request_permission", new JsonObject
    {
        ["sessionId"] = Grandchild,
        ["toolCall"] = new JsonObject { ["toolCallId"] = "check", ["title"] = "Check" },
        ["options"] = new JsonArray(new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" }),
    });

    static async Task<JsonNode?> AgentRequestAsync(ScriptedAgent agent, JsonRpcRequest request, CancellationToken ct)
    {
        switch (request.Method)
        {
            case "initialize": return ScriptedAgent.Init(load: true);
            case "session/new": return new JsonObject { ["sessionId"] = Root };
            case "session/resume" or "session/load": return new JsonObject();
            case "session/prompt":
                await agent.Update(Root, new SubagentUpdate { SessionId = Child });
                await agent.Update(Root, new SessionMessage { MessageId = "task", SenderSessionId = Root,
                    RecipientSessionId = Child, Content = [new TextContent { Text = "Child task" }] });
                await agent.Update(Root, new ToolCall { ToolCallId = "spawn", Title = "Subagent", Status = ToolCallStatus.Completed,
                    Content = [new SessionReference { SessionId = Child }] });
                await agent.Update(Child, new SubagentUpdate { SessionId = Grandchild });
                await agent.Update(Grandchild, new SessionUpdateAgentMessageChunk { MessageId = "message", Content = new TextContent { Text = "child text" } });
                await AskAsync(agent).WaitAsync(Wait, ct);
                return new JsonObject { ["stopReason"] = "end_turn" };
            default: throw new JsonRpcException(JsonRpcException.MethodNotFound, request.Method);
        }
    }
}
