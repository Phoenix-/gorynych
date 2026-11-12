using System.IO.Pipelines;
using System.Text;
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
        kimi.BeforePrompt = id => kimi.Adapter.Current!.Update(id, new SessionInfoUpdate { Title = kimi.FirstBlocks(id)[0] });
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

        await conductor.BackgroundTurnsIdleAsync();
        Assert.AreEqual("Fix the flaky test", kimi.FirstBlocks(child)[0], "the first prompt was started, its task first");
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
        CollectionAssert.AreEqual(new[] { "Flaky test" }, infos.Select(u => (string?)u["title"]).OfType<string>().Distinct().ToArray(), "Kimi's own title does not replace the name");
        // The spawned turn's end is marked, as a client's prompt's is (for the AHP face).
        var end = infos.Single(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended");
        StringAssert.StartsWith((string?)end["_meta"]![GorynychProtocol.MetaPromptId], "service-");
        Assert.AreEqual("end_turn", (string?)end["_meta"]![GorynychProtocol.MetaStopReason]);
        var asked = replay.Single(u => (string?)u["sessionUpdate"] == "user_message_chunk");
        Assert.AreEqual("spawn", (string?)asked["_meta"]![GorynychProtocol.MetaOrigin]);
        Assert.IsFalse(string.IsNullOrEmpty((string?)asked["messageId"]), "the service names the first prompt it writes");
        StringAssert.StartsWith((string?)asked["content"]!["text"], "Fix the flaky test\n\n[About this session · ", "the journal keeps what the agent got");
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
        Assert.AreEqual(true, (bool?)all.Initialized!.AgentCapabilities!.Meta![GorynychProtocol.MetaSessionsChanged]);
        Assert.AreEqual(true, (bool?)all.Initialized!.Meta![GorynychProtocol.MetaSessionsChanged], "the deprecated place, for one release");
        Assert.IsNotNull(all.Initialized!.Meta![GorynychProtocol.MetaServiceId], "data stays in the result's _meta");

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
        var time = new ManualTime();
        var interval = TimeSpan.FromSeconds(10);
        await using var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(),
            ServiceId = "t",
            ActivityNoticeInterval = interval,
            Time = time,
        }, [kimi.Adapter]);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewPromptedAsync(zed, "kimi", "Write the docs");
        int Updates() => zed.ChangeLog().Count(l => l == $"updated {id}");
        // Notices keep their order, so once a later one has arrived, every one before it has too.
        async Task Delivered()
        {
            var marker = await NewPromptedAsync(zed, "kimi", "marker");
            await Until(() => zed.ChangeLog().Contains($"added {marker}"), "the marker's notice");
        }
        Task Update() => kimi.Adapter.Current!.Update(id, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "." } });

        time.Advance(interval); // the end of the prompt's own interval
        time.Advance(interval); // and a quiet one after it
        await Delivered();
        var before = Updates();

        for (var i = 0; i < 30; i++) await Update();
        await Delivered();
        Assert.AreEqual(1, Updates() - before, "the first activity is announced at once");

        time.Advance(interval);
        await Delivered();
        Assert.AreEqual(2, Updates() - before, "the rest of the interval in one notice, at its end");

        await Update(); // inside the interval that notice began
        await Delivered();
        Assert.AreEqual(2, Updates() - before, "at most once per interval");
        time.Advance(interval);
        await Delivered();
        Assert.AreEqual(3, Updates() - before);

        time.Advance(interval);
        await Delivered();
        Assert.AreEqual(3, Updates() - before, "no activity, no notice");
    }

    static string[] Reports(LifecycleAgent parentAgent, string parent) =>
        Prompts(parentAgent, parent).Where(p => p.StartsWith("[Report · from session_id ", StringComparison.Ordinal)).ToArray();

    [TestMethod]
    public async Task Report_EachTurnOfTheChild_ReachesAParentWithoutAChannel_AsAMarkedTurn()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var claude = new LifecycleAgent("claude", canDelete: false);
        // The child thinks aloud, runs a tool, then answers: only the answer is its final message.
        claude.BeforePrompt = async id =>
        {
            await claude.Adapter.Current!.Update(id, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "Looking around." } });
            await claude.Adapter.Current!.Update(id, new ToolCall { ToolCallId = "t1", Title = "Read", Status = ToolCallStatus.Completed });
        };
        await using var conductor = Conductor(null, kimi.Adapter, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "kimi", "Coordinate the release");
        var spawned = await SpawnAsync(zed, kimi.Tokens[parent], new JsonObject { ["agent"] = "claude", ["prompt"] = "Write the notes", ["title"] = "Notes" });
        var child = (string)spawned!["sessionId"]!;

        await conductor.BackgroundTurnsIdleAsync();
        var report = Reports(kimi, parent).Single();
        StringAssert.StartsWith(report, $"[Report · from session_id {child} (claude, \"Notes\") · your child session · its turn ended]\nok\n");
        Assert.IsFalse(report.Contains("Looking around.", StringComparison.Ordinal), report);
        var told = zed.Snapshot().Last(u => (string?)u["sessionUpdate"] == "user_message_chunk");
        Assert.AreEqual("report", (string?)told["_meta"]![GorynychProtocol.MetaOrigin]);
        Assert.IsFalse(string.IsNullOrEmpty((string?)told["messageId"]), "the service names the report it writes");
        Assert.AreEqual("child_claude", (string?)told["_meta"]![GorynychProtocol.MetaSender]);
        Assert.AreEqual(child, (string?)told["_meta"]![GorynychProtocol.MetaFromSessionId]);

        // The person prompts the child directly: another report.
        await using var other = await TestClient.ConnectAsync(conductor, "zed-child");
        await PromptAsync(other, child, "Add the migration step");
        await conductor.BackgroundTurnsIdleAsync();
        Assert.AreEqual(2, Reports(kimi, parent).Length);

        // The parent asking the child is not a turn to report.
        await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion,
            new JsonObject { ["sessionId"] = child, ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Tokens[parent] } });
        await conductor.BackgroundTurnsIdleAsync();
        Assert.AreEqual(2, Reports(kimi, parent).Length);
    }

    [TestMethod]
    public async Task Report_ToAParentWithAChannel_IsANote_AndReportFalseSendsNone()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var token = claude.Tokens[parent];

        var reported = (string)(await SpawnAsync(zed, token, new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test" }))!["sessionId"]!;
        var quiet = (string)(await SpawnAsync(zed, token, new JsonObject { ["agent"] = "kimi", ["prompt"] = "Bump the version", ["report"] = false }))!["sessionId"]!;
        await conductor.BackgroundTurnsIdleAsync();

        var note = claude.Notes.Single();
        Assert.AreEqual(parent, (string?)note["sessionId"]);
        Assert.AreEqual("child_kimi", (string?)note["sender"]);
        Assert.AreEqual(reported, (string?)note["fromSessionId"]);
        Assert.AreEqual("child_kimi", (string?)note["_meta"]![GorynychProtocol.MetaSender], "the _meta for the message Claude will report");
        Assert.AreEqual(reported, (string?)note["_meta"]![GorynychProtocol.MetaFromSessionId]);
        StringAssert.StartsWith((string?)note["text"], $"[Report · from session_id {reported} (kimi, \"Fix the flaky test\") · your child session · its turn ended]\nok\n");
        CollectionAssert.AreEqual(new[] { "Coordinate the release" }, Prompts(claude, parent), "a note is not a turn");
        Assert.IsFalse(conductor.Store.Get(quiet)!.ReportToParent);
    }

    // ---------------------------------------------------------------- review fixes

    [TestMethod]
    public async Task SessionsChanged_AClientThatStopsReading_DelaysNoOtherClient()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: false);
        await using var conductor = Conductor(null, kimi.Adapter);
        // A client that initializes and then never reads again; its pipe holds about 1 KB.
        var toService = new Pipe();
        var toStuck = new Pipe(new PipeOptions(pauseWriterThreshold: 1024, resumeWriterThreshold: 512));
        var stuckServed = conductor.ServeClientAsync(toService.Reader.AsStream(), toStuck.Writer.AsStream(), "stuck");
        await toService.Writer.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}\n"));
        using (var reader = new StreamReader(toStuck.Reader.AsStream(leaveOpen: true), leaveOpen: true))
            StringAssert.Contains(await reader.ReadLineAsync(), "\"result\"");
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewPromptedAsync(zed, "kimi", "Write the docs");

        // Thirty renames: ten times what the stuck client's pipe can hold.
        for (var i = 0; i < 30; i++)
            await kimi.Adapter.Current!.Update(id, new SessionInfoUpdate { Title = $"Docs, take {i}" });

        await Until(() => zed.ChangeLog().Count(l => l == $"updated {id}") >= 30, "every rename, well within the stuck client's 15 s");
        toService.Writer.Complete();
        await stuckServed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Spawn_TheFirstTurnFailsBeforeItStarts_TheParentIsToldAnyway()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        // Hold the child's first turn before it opens the session, and let its agent die meanwhile.
        var childFeed = conductor.Feed("kimi-1");
        await childFeed.LifeGate.WaitAsync();

        var spawned = await SpawnAsync(zed, claude.Tokens[parent], new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test", ["title"] = "Flaky test" });
        var child = (string)spawned!["sessionId"]!;
        Assert.AreEqual("kimi-1", child);
        kimi.BeforeResume = _ => throw new JsonRpcException(JsonRpcException.InternalError, "Kimi lost the session");
        await kimi.Adapter.Current!.Rpc.DisposeAsync();
        await Until(() => !conductor.Agents["kimi"].IsRunning, "the agent's exit");
        childFeed.LifeGate.Release();

        await conductor.BackgroundTurnsIdleAsync();
        var report = Reports(claude, parent).Single();
        StringAssert.StartsWith(report, $"[Report · from session_id {child} (kimi, \"Flaky test\") · your child session · its turn failed: Kimi lost the session]\n(no final message)\n");
        Assert.AreEqual(0, Prompts(kimi, child).Length, "the turn never reached the agent");
    }

    [TestMethod]
    public async Task Report_ATurnThatEndsWithAToolCall_HasNoFinalMessage_AndALongOneIsCutWhole()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var claude = new LifecycleAgent("claude", canDelete: false) { Answer = null };
        claude.BeforePrompt = async id =>
        {
            if (Prompts(claude, id).Length > 1) return;
            await claude.Adapter.Current!.Update(id, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "Looking around." } });
            await claude.Adapter.Current!.Update(id, new ToolCall { ToolCallId = "t1", Title = "Read", Status = ToolCallStatus.Completed });
        };
        await using var conductor = Conductor(null, kimi.Adapter, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "kimi", "Coordinate the release");
        var child = (string)(await SpawnAsync(zed, kimi.Tokens[parent], new JsonObject { ["agent"] = "claude", ["prompt"] = "Write the notes", ["title"] = "Notes" }))!["sessionId"]!;

        await conductor.BackgroundTurnsIdleAsync();
        var report = Reports(kimi, parent).Single();
        StringAssert.StartsWith(report, $"[Report · from session_id {child} (claude, \"Notes\") · your child session · its turn ended]\n(no final message)\n");
        Assert.IsFalse(report.Contains("Looking around.", StringComparison.Ordinal), "the text before the last tool call is not the final message");

        // A final message longer than a report carries is cut, never inside a surrogate pair.
        claude.Answer = new string('a', 1999) + "😀" + new string('b', 100_000);
        await using var other = await TestClient.ConnectAsync(conductor, "zed-child");
        await PromptAsync(other, child, "Add the migration step");
        await conductor.BackgroundTurnsIdleAsync();
        var cut = Reports(kimi, parent)[1];
        StringAssert.Contains(cut, "\n" + new string('a', 1999) + "…\n");
        Assert.IsFalse(cut.Contains('b'), "nothing past the cut");
    }

    [TestMethod]
    public async Task SessionsChanged_AddedCountsForTheInterval_AndNothingFollowsRemoved()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kimi.BeforePrompt = _ => hold.Task;
        await using var conductor = Conductor(TimeSpan.FromMilliseconds(1500), claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var child = (string)(await SpawnAsync(zed, claude.Tokens[parent], new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test", ["title"] = "Flaky test" }))!["sessionId"]!;
        await Until(() => zed.ChangeLog().Contains($"added {child}") && Prompts(kimi, child).Length == 1, "the child and its first prompt");

        // The first prompt's activity right after "added" waits for the end of the interval.
        await Task.Delay(300);
        Assert.IsFalse(zed.ChangeLog().Contains($"updated {child}"), string.Join(", ", zed.ChangeLog()));

        // Deleted before that notice is due: nothing follows "removed".
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = child });
        hold.SetResult();
        await Task.Delay(1800);
        var mine = zed.ChangeLog().Where(l => l.EndsWith(" " + child, StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEqual(new[] { $"added {child}", $"removed {child}" }, mine, string.Join(", ", mine));
    }

    [TestMethod]
    public async Task Spawn_TheCallerIsDeletedWhileThePersonDecides_NothingStarts()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var token = claude.Tokens[parent];
        zed.Approve = async r =>
        {
            await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = parent });
            return new SelectedPermissionOutcome { OptionId = "start" };
        };

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() =>
            SpawnAsync(zed, token, new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test" }));

        Assert.AreEqual(JsonRpcException.InvalidRequest, refused.Code);
        StringAssert.Contains(refused.Message, "deleted");
        Assert.AreEqual(0, kimi.Adapter.Starts, "Kimi was not even started");
    }

    [TestMethod]
    public async Task Spawn_TheCallersTurnIsCancelled_NothingStarts()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        // ACP: a client that cancels a turn answers its pending approvals with "cancelled".
        zed.Approve = _ => Task.FromResult<RequestPermissionOutcome>(new RequestPermissionOutcomeCancelled());

        var cancelled = await Assert.ThrowsExactlyAsync<JsonRpcException>(() =>
            SpawnAsync(zed, claude.Tokens[parent], new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test" }));

        Assert.AreEqual(JsonRpcException.RequestCancelled, cancelled.Code);
        Assert.AreEqual(0, kimi.Adapter.Starts);
        await Until(() => zed.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call_update" && (string?)u["status"] == "failed"), "the failed spawn card");
    }

    [TestMethod]
    public async Task Spawn_AChildThatFailsBeforeItIsListed_IsDeletedAgain()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        // The child's journal cannot be written, so its parent cannot be recorded in it.
        var journal = Path.Combine((string)conductor.Status()["dataDirectory"]!, "journal", SessionJournal.FileNameFor("kimi-1"));
        JsonRpcException failed;
        using (new FileStream(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() =>
                SpawnAsync(zed, claude.Tokens[parent], new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test" }));

        Assert.AreEqual(JsonRpcException.InternalError, failed.Code, failed.Message);
        CollectionAssert.AreEqual(new[] { AcpMethods.Agent.SessionNew, AcpMethods.Agent.SessionClose, AcpMethods.Agent.SessionDelete }, kimi.CallsFor("kimi-1"));
        Assert.IsNull(conductor.Store.Get("kimi-1"));
        Assert.IsFalse(zed.ChangeLog().Any(l => l.EndsWith(" kimi-1", StringComparison.Ordinal)), string.Join(", ", zed.ChangeLog()));
        CollectionAssert.AreEqual(new[] { parent }, (await ListAsync(zed)).Sessions.Select(s => s.SessionId).ToArray());
        await Until(() => zed.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call_update" && (string?)u["status"] == "failed"), "the failed spawn card");
    }

    [TestMethod]
    public async Task Spawn_WithdrawnWhileTheAgentCreatesTheChild_TheChildIsDeletedAgain()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        using var withdraw = new CancellationTokenSource();
        kimi.BeforeNew = async id =>
        {
            if (id != "kimi-1") return;
            withdraw.Cancel(); // the caller's tool call is cancelled while Kimi creates the session
            await Task.Delay(300);
        };
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var args = new JsonObject
        {
            ["agent"] = "kimi",
            ["prompt"] = "Fix the flaky test",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = claude.Tokens[parent] },
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => zed.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, args, withdraw.Token));

        await Until(() => kimi.CallsFor("kimi-1").Contains(AcpMethods.Agent.SessionDelete), "the withdrawn child's deletion");
        await conductor.CleanupsIdleAsync();
        CollectionAssert.AreEqual(new[] { AcpMethods.Agent.SessionNew, AcpMethods.Agent.SessionClose, AcpMethods.Agent.SessionDelete }, kimi.CallsFor("kimi-1"));
        Assert.IsNull(conductor.Store.Get("kimi-1"));
        Assert.AreEqual(0, Prompts(kimi, "kimi-1").Length);
        Assert.IsFalse(zed.ChangeLog().Any(l => l.EndsWith(" kimi-1", StringComparison.Ordinal)), string.Join(", ", zed.ChangeLog()));
    }

    [TestMethod]
    public async Task Report_ToAClaudeParentThatIsNotRunning_IsATurn()
    {
        // The service opens the parent with session/resume, which starts no Claude process, so the adapter refuses the note.
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true, Running = false };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");

        var child = (string)(await SpawnAsync(zed, claude.Tokens[parent], new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test" }))!["sessionId"]!;
        await conductor.BackgroundTurnsIdleAsync();

        Assert.AreEqual(0, claude.Notes.Count);
        StringAssert.StartsWith(Reports(claude, parent).Single(), $"[Report · from session_id {child} ");
    }

    [TestMethod]
    public async Task Lists_SpellEveryFolderOneWay()
    {
        var windows = OperatingSystem.IsWindows();
        var kimi = new LifecycleAgent("kimi", canDelete: false, existing: ["kimi-native"], titles: new() { ["kimi-native"] = "Native" })
        {
            ListCwd = windows ? "c:/Work/Repo/" : "/work/repo/",
        };
        var expected = windows ? @"C:\Work\Repo" : "/work/repo";
        await using var conductor = Conductor(null, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");

        var listed = (await ListAsync(zed)).Sessions.Single(s => s.SessionId == "kimi-native");
        Assert.AreEqual(expected, listed.Cwd);
        Assert.AreEqual(expected, conductor.Store.Get("kimi-native")!.Cwd);
        var peers = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject());
        Assert.AreEqual(expected, (string?)peers!["sessions"]!.AsArray().Single(s => (string?)s!["session_id"] == "kimi-native")!["cwd"]);

        // A client's own folder, with a trailing separator, is kept without it.
        var mine = await NewAsync(zed, "kimi");
        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Cwd), conductor.Store.Get(mine)!.Cwd);
        Assert.IsTrue(Cwd.EndsWith(Path.DirectorySeparatorChar), "the test needs a folder spelled with a trailing separator");
    }

    // ---------------------------------------------------------------- what agents are told

    [TestMethod]
    public async Task Spawn_TheChildIsToldAfterItsTask_WhoStartedIt_BySessionId()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var token = claude.Tokens[parent];

        var child = (string)(await SpawnAsync(zed, token, new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test" }))!["sessionId"]!;
        var quiet = (string)(await SpawnAsync(zed, token, new JsonObject { ["agent"] = "kimi", ["prompt"] = "Bump the version", ["report"] = false }))!["sessionId"]!;
        await conductor.BackgroundTurnsIdleAsync();

        var context =
            $"[About this session · started by session_id {parent} (claude, \"Coordinate the release\") with spawn_session, approved by the user]\n" +
            $"Answer in your final message: when your turn ends, Gorynych sends that message to session_id {parent} as a report by itself. " +
            "Do not use reply or ask_agent for it. The report carries only that last message, your text after your last tool call, " +
            "and is cut at 2000 characters, so put anything longer into a file and name the file in that message. " +
            $"To ask it something, use ask_agent with session_id \"{parent}\". The user can also prompt you here directly.";
        CollectionAssert.AreEqual(new[] { "Fix the flaky test", context }, kimi.FirstBlocks(child), "the task first, then where it came from");
        var quietContext = kimi.FirstBlocks(quiet)[1];
        Assert.AreEqual(
            $"[About this session · started by session_id {parent} (claude, \"Coordinate the release\") with spawn_session, approved by the user]\n" +
            "Your turns are not reported to that session; it asks you with ask_agent when it wants to know something. " +
            $"To ask it something, use ask_agent with session_id \"{parent}\". The user can also prompt you here directly.", quietContext);
        Assert.IsFalse(context.Contains("The task above", StringComparison.Ordinal), "the header already says where the task came from");

        // The list leaves out the child's own entry and so its parent_session_id: the first line names the parent instead.
        var listing = PeersServer.ListAgentsText(await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject { ["token"] = kimi.Tokens[child] }));
        StringAssert.StartsWith(listing, $"You are session_id {child} (kimi, \"Fix the flaky test\"), started by session_id {parent} " +
            "(claude, \"Coordinate the release\") with spawn_session.\n");
        StringAssert.StartsWith(PeersServer.ListAgentsText(await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject { ["token"] = token })),
            $"You are session_id {parent} (claude, \"Coordinate the release\").\n", "without a parent the line stays as it was");
        Assert.AreEqual("Fix the flaky test", conductor.Store.Get(child)!.Title, "the task titles the session, not what follows it");

        // The journal keeps what the agent got, as one message; the label for GUIs stays.
        await using var other = await TestClient.ConnectAsync(conductor, "zed-2");
        await other.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = child, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        var told = other.Snapshot().Single(u => (string?)u["sessionUpdate"] == "user_message_chunk");
        Assert.AreEqual($"Fix the flaky test\n\n{context}", (string?)told["content"]!["text"]);
        Assert.AreEqual("parent_claude", (string?)told["_meta"]![GorynychProtocol.MetaSender]);
        Assert.IsFalse(Prompts(kimi, child).Single().Contains("parent_claude", StringComparison.Ordinal), "no label that looks like a session ID");
    }

    [TestMethod]
    public async Task PeerTools_AnUnknownSessionOrAgentOrFolder_FailsWithWhatToPassInstead()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var other = await NewPromptedAsync(zed, "kimi", "Review the \"API\"");
        var token = claude.Tokens[asker];
        Task<JsonNode?> Ask(string target) => zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion,
            new JsonObject { ["sessionId"] = target, ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } });
        var askable = $"Sessions you can ask:\n- session_id {other} (kimi, \"Review the 'API'\")";

        var unknown = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Ask("peer_claude"));
        Assert.AreEqual(JsonRpcException.ResourceNotFound, unknown.Code);
        Assert.AreEqual($"No session \"peer_claude\". Pass an exact session_id from list_agents. {askable}", unknown.Message);

        var self = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Ask(asker));
        Assert.AreEqual(JsonRpcException.InvalidParams, self.Code);
        Assert.AreEqual($"\"{asker}\" is this session's own session_id; ask_agent asks another session. {askable}", self.Message);

        var agent = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(zed, token, new JsonObject { ["agent"] = "codex", ["prompt"] = "x" }));
        Assert.AreEqual(JsonRpcException.InvalidParams, agent.Code);
        Assert.AreEqual("No agent \"codex\". Pass one of: claude, kimi.", agent.Message);

        var missing = FolderPath.Normalize(Path.Combine(Cwd, "no-such-folder-" + Guid.NewGuid().ToString("N")));
        var folder = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(zed, token, new JsonObject { ["agent"] = "kimi", ["prompt"] = "x", ["cwd"] = missing }));
        Assert.AreEqual(JsonRpcException.InvalidParams, folder.Code);
        Assert.AreEqual($"No such folder: {missing}. Pass an existing folder, or leave cwd out to work in this session's folder, {FolderPath.Normalize(Cwd)}.", folder.Message);

        // A client that asks has no list_agents: it gets the plain error.
        var plain = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion,
            new JsonObject { ["sessionId"] = "peer_claude", ["text"] = "Done?" }));
        Assert.AreEqual("Unknown session peer_claude", plain.Message);
    }

    [TestMethod]
    public async Task PeerTools_WithNoOtherSession_TheErrorSaysSo()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        await using var conductor = Conductor(null, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        await NewAsync(zed, "claude"); // a draft is not offered

        var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion,
            new JsonObject { ["sessionId"] = "peer_kimi", ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = claude.Tokens[asker] } }));
        Assert.AreEqual("No session \"peer_kimi\". Pass an exact session_id from list_agents. There is no other session to ask now.", e.Message);
    }

    [TestMethod]
    public async Task Ask_ThroughClaudesChannel_NamesTheAsker_AndANotRunningClaudeIsAskedInATurn()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asked = await NewPromptedAsync(zed, "claude", "Write the notes");
        var asker = await NewPromptedAsync(zed, "kimi", "Coordinate the release");
        Task<JsonNode?> Ask() => zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion,
            new JsonObject { ["sessionId"] = asked, ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Tokens[asker] } });

        await Ask();
        var opinion = claude.Opinions.Single();
        Assert.AreEqual($"[Opinion request · from session_id {asker} (kimi, \"Coordinate the release\")]\nDone?", (string?)opinion["text"]);
        Assert.AreEqual("peer_kimi", (string?)opinion["sender"], "the label for GUIs stays");

        // Claude's process is not running: the question becomes a turn, which starts it (WakeTests).
        claude.Running = false;
        Assert.AreEqual("ok", (string?)(await Ask())!["text"]);
        Assert.AreEqual(1, claude.Opinions.Count, "the channel refused it");
        StringAssert.StartsWith(Prompts(claude, asked).Last(), $"[Opinion request · from session_id {asker} (kimi, \"Coordinate the release\") · message_id m");
    }
}
