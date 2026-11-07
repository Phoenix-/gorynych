using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Child sessions: a session spawns another through its peers server (<c>_gorynych/spawn</c>),
/// the child is an ordinary top-level session with its parent in <c>_meta</c>, and clients learn
/// about sessions that appear, change or go away from <c>_gorynych/sessions_changed</c>.
/// </summary>
[TestClass]
public sealed class SessionTreeTests
{
    static readonly string Cwd = Path.GetTempPath();

    static Conductor Conductor(TimeSpan? activityInterval = null, params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        ActivityNoticeInterval = activityInterval ?? TimeSpan.FromSeconds(10),
    }, adapters);

    static async Task<string> NewAsync(TestClient client, string agent) =>
        (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd,
            McpServers = [],
            Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        })).SessionId;

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static async Task<string> NewPromptedAsync(TestClient client, string agent, string text)
    {
        var id = await NewAsync(client, agent);
        await PromptAsync(client, id, text);
        return id;
    }

    /// <summary>What the peers MCP server sends for <c>spawn_session</c>.</summary>
    static Task<JsonNode?> SpawnAsync(TestClient client, string token, JsonObject args)
    {
        args["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token };
        return client.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, args);
    }

    static Task<ListSessionsResponse> ListAsync(TestClient client) =>
        client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());

    static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 250; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    static string[] Prompts(LifecycleAgent agent, string sessionId)
    {
        lock (agent.Prompts) return agent.Prompts.Where(p => p.SessionId == sessionId).Select(p => p.Text).ToArray();
    }

    [TestMethod]
    public async Task Spawn_AsksOnTheCallingSession_ThenCreatesAndPromptsTheChild_WithItsParentInTheLists()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        // Kimi titles a session from its first prompt; a name given to spawn_session wins.
        kimi.BeforePrompt = id => kimi.Adapter.Current!.Update(id, new SessionInfoUpdate { Title = Prompts(kimi, id)[0] });
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");

        var spawned = await SpawnAsync(zed, claude.Tokens[parent],
            new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test", ["title"] = "Flaky test" });
        var child = (string)spawned!["sessionId"]!;
        Assert.AreEqual("kimi-1", child);

        var ask = zed.Approvals.Single();
        Assert.AreEqual(parent, ask.SessionId, "the person is asked on the calling session");
        StringAssert.Contains(ask.ToolCall.Title, "Flaky test");
        Assert.AreEqual("Fix the flaky test", (string?)ask.ToolCall.RawInput?["prompt"]);
        CollectionAssert.AreEqual(new[] { "allow_once", "reject_once" }, ask.Options.Select(o => o.Kind.Value).ToArray(), "every spawn is asked");
        ZedRules.CheckPermission(ask);

        await conductor.SpawnedTurnsIdleAsync();
        CollectionAssert.AreEqual(new[] { "Fix the flaky test" }, Prompts(kimi, child), "the first prompt was started");
        Assert.IsTrue(kimi.Tokens.ContainsKey(child), "the child has the peers server too");
        Assert.IsFalse(conductor.Store.Get(child)!.Draft, "a spawned session is never a draft");

        var list = await ListAsync(zed);
        var childInfo = list.Sessions.Single(s => s.SessionId == child);
        Assert.AreEqual(parent, (string?)childInfo.Meta![GorynychProtocol.MetaParentSessionId]);
        Assert.AreEqual("Flaky test", childInfo.Title);
        Assert.IsNull(list.Sessions.Single(s => s.SessionId == parent).Meta![GorynychProtocol.MetaParentSessionId], "a session a client created has no parent");

        var peers = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject { ["token"] = claude.Tokens[parent] });
        var entry = peers!["sessions"]!.AsArray().Single(s => (string?)s!["session_id"] == child)!;
        Assert.AreEqual(parent, (string?)entry["parent_session_id"]);

        // The parent's thread shows the spawn as a tool call, finished.
        await Until(() => zed.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call_update" && (string?)u["status"] == "completed"), "the finished spawn card");
        var card = zed.Snapshot().Single(u => (string?)u["sessionUpdate"] == "tool_call");
        Assert.AreEqual(ask.ToolCall.ToolCallId, (string?)card["toolCallId"]);

        // A client that opens the child gets its history: parent, name, the spawn prompt, the answer.
        await using var other = await TestClient.ConnectAsync(conductor, "zed-2");
        await other.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = child, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        var replay = other.Snapshot();
        var infos = replay.Where(u => (string?)u["sessionUpdate"] == "session_info_update").ToArray();
        Assert.AreEqual(parent, (string?)infos[0]["_meta"]![GorynychProtocol.MetaParentSessionId]);
        CollectionAssert.AreEqual(new[] { "Flaky test" }, infos.Select(u => (string?)u["title"]).Distinct().ToArray(), "Kimi's own title does not replace the name");
        var asked = replay.Single(u => (string?)u["sessionUpdate"] == "user_message_chunk");
        Assert.AreEqual("spawn", (string?)asked["_meta"]![GorynychProtocol.MetaOrigin]);
        Assert.AreEqual("Fix the flaky test", (string?)asked["content"]!["text"]);
        Assert.AreEqual("ok", other.Text());
    }

    [TestMethod]
    public async Task Spawn_TheUserDeclines_NothingStarts()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        zed.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome
        {
            OptionId = r.Options.Single(o => o.Kind == PermissionOptionKind.RejectOnce).OptionId,
        });

        var declined = await Assert.ThrowsExactlyAsync<JsonRpcException>(() =>
            SpawnAsync(zed, claude.Tokens[parent], new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test" }));

        StringAssert.Contains(declined.Message, "declined");
        Assert.AreEqual(0, kimi.Adapter.Starts, "Kimi was not even started");
        CollectionAssert.AreEqual(new[] { parent }, (await ListAsync(zed)).Sessions.Select(s => s.SessionId).ToArray());
        await Until(() => zed.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call_update" && (string?)u["status"] == "failed"), "the failed spawn card");
    }

    [TestMethod]
    public async Task Spawn_RejectsUnknownCallersAgentsAndFolders()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        await using var conductor = Conductor(null, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var token = claude.Tokens[parent];

        var forged = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(zed, "forged", new JsonObject { ["agent"] = "claude", ["prompt"] = "x" }));
        Assert.AreEqual(JsonRpcException.InvalidRequest, forged.Code);
        var unknown = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(zed, token, new JsonObject { ["agent"] = "codex", ["prompt"] = "x" }));
        Assert.AreEqual(JsonRpcException.InvalidParams, unknown.Code);
        var folder = await Assert.ThrowsExactlyAsync<JsonRpcException>(() =>
            SpawnAsync(zed, token, new JsonObject { ["agent"] = "claude", ["prompt"] = "x", ["cwd"] = Path.Combine(Cwd, Guid.NewGuid().ToString("N")) }));
        Assert.AreEqual(JsonRpcException.InvalidParams, folder.Code);
        Assert.AreEqual(0, zed.Approvals.Count, "nothing to ask about");
    }

    [TestMethod]
    public async Task Spawn_ReturnsWhileTheFirstTurnRuns_AndAskAgentWaitsForIt()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kimi.BeforePrompt = _ => hold.Task;
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var token = claude.Tokens[parent];

        var spawned = await SpawnAsync(zed, token, new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test" }).WaitAsync(TimeSpan.FromSeconds(5));
        var child = (string)spawned!["sessionId"]!;
        await Until(() => Prompts(kimi, child).Length == 1, "the first prompt");

        var asking = zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion,
            new JsonObject { ["sessionId"] = child, ["text"] = "How is it going?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } });
        await Task.Delay(200);
        Assert.IsFalse(asking.IsCompleted);
        Assert.AreEqual(1, Prompts(kimi, child).Length, "the question waits for the first turn");

        hold.SetResult();
        var answer = await asking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("ok", (string?)answer!["text"]);
        StringAssert.Contains(Prompts(kimi, child)[1], "How is it going?");
    }

    [TestMethod]
    public async Task SessionsChanged_ReachesTheClientsWhoseScopeCoversTheSession_AndNeverAnnouncesDrafts()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: false, existing: ["kimi-native"]);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var all = await TestClient.ConnectAsync(conductor, "all");
        await using var kimiOnly = await TestClient.ConnectAsync(conductor, "kimi-only", agent: "kimi");
        await using var claudeOnly = await TestClient.ConnectAsync(conductor, "claude-only", agent: "claude");
        Assert.AreEqual(true, (bool?)all.Initialized!.Meta![GorynychProtocol.MetaSessionsChanged]);

        var draft = await NewAsync(all, "kimi");
        await all.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = draft }); // discarded
        var used = await NewPromptedAsync(all, "kimi", "Refactor the parser");
        await ListAsync(kimiOnly); // finds Kimi's own session
        await all.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = used });
        var parent = await NewPromptedAsync(all, "claude", "Coordinate the release");
        var spawned = await SpawnAsync(all, claude.Tokens[parent], new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test", ["title"] = "Flaky test" });
        var child = (string)spawned!["sessionId"]!;
        await Until(() => all.ChangeLog().Contains($"added {child}") && kimiOnly.ChangeLog().Contains($"added {child}"), "the spawned child's notice");
        await Task.Delay(100);

        var log = all.ChangeLog();
        CollectionAssert.IsSubsetOf(new[]
        {
            $"added {used}", $"updated {used}", $"removed {used}", "added kimi-native", $"added {parent}", $"added {child}",
        }, log, string.Join(", ", log));
        Assert.IsFalse(log.Any(l => l.EndsWith(" " + draft, StringComparison.Ordinal)), $"the draft was announced: {string.Join(", ", log)}");
        Assert.IsTrue(Array.IndexOf(log, $"added {used}") < Array.IndexOf(log, $"removed {used}"));
        Assert.IsTrue(kimiOnly.ChangeLog().All(l => l.Split(' ')[1].StartsWith("kimi", StringComparison.Ordinal)), string.Join(", ", kimiOnly.ChangeLog()));
        CollectionAssert.Contains(kimiOnly.ChangeLog(), $"removed {used}");
        Assert.IsTrue(claudeOnly.ChangeLog().All(l => l.EndsWith(" " + parent, StringComparison.Ordinal)), string.Join(", ", claudeOnly.ChangeLog()));
        CollectionAssert.Contains(claudeOnly.ChangeLog(), $"added {parent}");
    }

    [TestMethod]
    public async Task SessionsChanged_AnnouncesActivityAtMostOncePerInterval()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: false);
        await using var conductor = Conductor(TimeSpan.FromMilliseconds(400), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewPromptedAsync(zed, "kimi", "Write the docs");
        await Task.Delay(1000); // past the notices of the prompt, the title and the answer's activity
        int Updates() => zed.ChangeLog().Count(l => l == $"updated {id}");
        var before = Updates();

        for (var i = 0; i < 30; i++)
            await kimi.Adapter.Current!.Update(id, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "." } });
        await Until(() => Updates() > before, "the first activity notice");
        await Task.Delay(900);

        var announced = Updates() - before;
        Assert.IsTrue(announced is >= 2 and <= 3, $"{announced} activity notices for 30 updates: the first, then the last of the interval");
    }
}
