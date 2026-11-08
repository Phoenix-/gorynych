using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The archive: a flag in the service's overlay (no harness offers one through ACP), set by
/// <c>_gorynych/archive</c> from a client or by the peers tools <c>archive_session</c> and
/// <c>restore_session</c>. Out of sight, not locked.
/// </summary>
[TestClass]
public sealed class ArchiveTests
{
    static readonly string Cwd = Path.GetTempPath();

    static Conductor Conductor(TimeSpan? activityInterval, params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        ActivityNoticeInterval = activityInterval ?? TimeSpan.FromSeconds(10),
    }, adapters);

    static async Task<string> NewPromptedAsync(TestClient client, string agent, string text)
    {
        var id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd,
            McpServers = [],
            Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        })).SessionId;
        await PromptAsync(client, id, text);
        return id;
    }

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    /// <summary><c>_gorynych/archive</c> as a client sends it, or, with a token, as the peers server does.</summary>
    static Task<JsonNode?> ArchiveAsync(TestClient client, string sessionId, bool archived, string? token = null)
    {
        var request = new JsonObject { ["sessionId"] = sessionId, ["archived"] = archived };
        if (token is not null) request["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token };
        return client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, request);
    }

    static Task<ListSessionsResponse> ListAsync(TestClient client, bool includeArchived = false) =>
        client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest
        {
            Meta = includeArchived ? new JsonObject { [GorynychProtocol.MetaIncludeArchived] = true } : null,
        });

    static string[] Ids(ListSessionsResponse list) => list.Sessions.Select(s => s.SessionId).ToArray();

    static Task<JsonNode?> AskAsync(TestClient client, string token, string target) =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion,
            new JsonObject { ["sessionId"] = target, ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } });

    static Task<JsonNode?> SpawnAsync(TestClient client, string token, string agent, string prompt) =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject
        {
            ["agent"] = agent,
            ["prompt"] = prompt,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
        });

    /// <summary>The archive flags the client got in <c>session_info_update._meta</c>, in order.</summary>
    static bool?[] ArchiveFlags(TestClient client, string sessionId) => client.Snapshot()
        .Where(u => (string?)u["sessionUpdate"] == "session_info_update" && u["_meta"] is JsonObject meta && meta.ContainsKey(GorynychProtocol.MetaArchived))
        .Select(u => (bool?)u["_meta"]![GorynychProtocol.MetaArchived])
        .ToArray();

    static string[] Prompts(LifecycleAgent agent, string sessionId)
    {
        lock (agent.Prompts) return agent.Prompts.Where(p => p.SessionId == sessionId).Select(p => p.Text).ToArray();
    }

    static async Task Until(Func<bool> condition, string what, TimeSpan? within = null)
    {
        var until = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < until)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    // ---------------------------------------------------------------- the client contract

    [TestMethod]
    public async Task Archive_IsAdvertised_AndSessionListLeavesArchivedSessionsOutUnlessAsked()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await using var kimiOnly = await TestClient.ConnectAsync(conductor, "kimi-only", agent: "kimi");
        Assert.AreEqual(true, (bool?)zed.Initialized!.AgentCapabilities!.Meta![GorynychProtocol.MetaArchive]);
        Assert.AreEqual(true, (bool?)zed.Initialized!.Meta![GorynychProtocol.MetaArchive], "the deprecated place, for one release");
        var kept = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var archived = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");

        var result = await ArchiveAsync(zed, archived, true);

        Assert.AreEqual("{}", result!.ToJsonString(), "a client gets an empty result");
        CollectionAssert.AreEqual(new[] { kept }, Ids(await ListAsync(zed)), "left out by default, though Kimi still lists it");
        var all = (await ListAsync(zed, includeArchived: true)).Sessions;
        CollectionAssert.AreEquivalent(new[] { kept, archived }, all.Select(s => s.SessionId).ToArray());
        Assert.AreEqual(true, (bool?)all.Single(s => s.SessionId == archived).Meta![GorynychProtocol.MetaArchived]);
        Assert.IsFalse(all.Single(s => s.SessionId == kept).Meta!.ContainsKey(GorynychProtocol.MetaArchived), "absent, not false, when not archived");
        Assert.AreEqual(0, (await ListAsync(kimiOnly)).Sessions.Count, "a connection scoped to one agent filters the same way");
        CollectionAssert.AreEqual(new[] { archived }, Ids(await ListAsync(kimiOnly, includeArchived: true)));

        var dataDirectory = (string)conductor.Status()["dataDirectory"]!;
        Assert.IsTrue(new SessionStore(Path.Combine(dataDirectory, "sessions.json")).Get(archived)!.Archived, "kept in the overlay on disk");
    }

    [TestMethod]
    public async Task Archive_IsAnnouncedAtOnceAsUpdated_AndItsClientsGetTheFlagExplicitly()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var claude = new LifecycleAgent("claude", canDelete: false);
        // Activity is announced at most once a minute here: the archive's notice must not wait for that.
        await using var conductor = Conductor(TimeSpan.FromMinutes(1), kimi.Adapter, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await using var claudeOnly = await TestClient.ConnectAsync(conductor, "claude-only", agent: "claude");
        var id = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        await Task.Delay(100);
        int Updates() => zed.ChangeLog().Count(l => l == $"updated {id}");
        var before = Updates();

        await ArchiveAsync(zed, id, true);
        await Until(() => Updates() == before + 1, "the archive's notice", TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new bool?[] { true }, ArchiveFlags(zed, id));

        await ArchiveAsync(zed, id, true); // nothing changes: no notice, no update
        await ArchiveAsync(zed, id, false);
        await Until(() => Updates() == before + 2, "the restore's notice", TimeSpan.FromSeconds(2));
        await Task.Delay(200);

        Assert.AreEqual(before + 2, Updates(), string.Join(", ", zed.ChangeLog()));
        CollectionAssert.AreEqual(new bool?[] { true, false }, ArchiveFlags(zed, id), "an explicit false on restore");
        Assert.IsFalse(zed.ChangeLog().Contains($"removed {id}"), "archived is updated, not removed");
        Assert.IsFalse(claudeOnly.ChangeLog().Any(l => l.EndsWith(" " + id, StringComparison.Ordinal)), string.Join(", ", claudeOnly.ChangeLog()));

        // A client that loads the session later replays the same updates.
        await using var other = await TestClient.ConnectAsync(conductor, "zed-2");
        await other.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        CollectionAssert.AreEqual(new bool?[] { true, false }, ArchiveFlags(other, id));
    }

    [TestMethod]
    public async Task Archive_OfASessionTheServiceNeverJournaled_LeavesItsHistoryToItsAgent()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true, existing: ["kimi-native"], titles: new() { ["kimi-native"] = "From Kimi's own CLI" });
        await using var conductor = Conductor(null, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");

        await ArchiveAsync(zed, "kimi-native", true); // found through Kimi's list

        Assert.IsTrue(conductor.Store.Get("kimi-native")!.Archived);
        Assert.AreEqual(0, conductor.Feed("kimi-native").Journal.Count, "a journal holding only the flag would stop session/load from asking the agent");
        Assert.AreEqual(true, (bool?)(await ListAsync(zed, includeArchived: true)).Sessions.Single().Meta![GorynychProtocol.MetaArchived]);
    }

    [TestMethod]
    public async Task Archive_IsOnlyAFlag_NothingIsClosed_AndNothingButArchiveClearsIt()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        await using var conductor = Conductor(null, kimi.Adapter, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var archivedKimi = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        var archivedClaude = await NewPromptedAsync(zed, "claude", "Write the notes");
        var calls = kimi.CallsFor(archivedKimi);

        await ArchiveAsync(zed, archivedKimi, true);
        await ArchiveAsync(zed, archivedClaude, true);

        CollectionAssert.AreEqual(calls, kimi.CallsFor(archivedKimi), "archiving sends the agent nothing");
        Assert.IsTrue(conductor.Agents["kimi"].IsLive(archivedKimi), "and leaves the session open in it");

        // The user prompts it, another session asks it (as a turn, and through Claude's channel), its agent renames it.
        await PromptAsync(zed, archivedKimi, "One more thing");
        var token = claude.Tokens[asker];
        Assert.AreEqual("ok", (string?)(await AskAsync(zed, token, archivedKimi))!["text"]);
        await AskAsync(zed, token, archivedClaude);
        Assert.AreEqual(1, claude.Opinions.Count);
        await kimi.Adapter.Current!.Update(archivedKimi, new SessionInfoUpdate { Title = "Renamed by Kimi" });
        await Until(() => conductor.Store.Get(archivedKimi)!.Title == "Renamed by Kimi", "the new title");

        Assert.IsTrue(conductor.Store.Get(archivedKimi)!.Archived, "still archived");
        Assert.IsTrue(conductor.Store.Get(archivedClaude)!.Archived, "still archived");
        CollectionAssert.AreEqual(new[] { asker }, Ids(await ListAsync(zed)));

        var unknown = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => ArchiveAsync(zed, "no-such-session", true));
        Assert.AreEqual(JsonRpcException.ResourceNotFound, unknown.Code);
        Assert.AreEqual("Unknown session no-such-session", unknown.Message, "a client gets the plain error");
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = archivedClaude });
        var deleted = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => ArchiveAsync(zed, archivedClaude, false));
        Assert.AreEqual(JsonRpcException.ResourceNotFound, deleted.Code);
        var noFlag = await Assert.ThrowsExactlyAsync<JsonRpcException>(() =>
            zed.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = asker }));
        Assert.AreEqual(JsonRpcException.InvalidParams, noFlag.Code);
    }

    // ---------------------------------------------------------------- the peer tools

    [TestMethod]
    public async Task ListAgents_LeavesArchivedSessionsOut_UnlessAskedTo_AndSoDoesTheHintOfAnError()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var open = await NewPromptedAsync(zed, "kimi", "Review the API");
        var archived = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        var token = claude.Tokens[asker];
        await ArchiveAsync(zed, archived, true);
        async Task<JsonArray> Peers(bool includeArchived)
        {
            var query = new JsonObject { ["token"] = token };
            if (includeArchived) query["includeArchived"] = true;
            return (await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, query))!["sessions"]!.AsArray();
        }

        var shown = await Peers(includeArchived: false);
        CollectionAssert.AreEqual(new[] { open }, shown.Select(s => (string?)s!["session_id"]).ToArray());
        Assert.IsFalse(shown[0]!.AsObject().ContainsKey("archived"), "no flag when archived ones are not asked for");
        var all = await Peers(includeArchived: true);
        Assert.AreEqual(false, (bool?)all.Single(s => (string?)s!["session_id"] == open)!["archived"]);
        Assert.AreEqual(true, (bool?)all.Single(s => (string?)s!["session_id"] == archived)!["archived"]);

        var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => AskAsync(zed, token, "peer_kimi"));
        Assert.AreEqual($"No session \"peer_kimi\". Pass an exact session_id from list_agents. Sessions you can ask:\n- session_id {open} (kimi, \"Review the API\")", e.Message);
    }

    [TestMethod]
    public async Task ListAgents_EndsWithHowManyArchivedSessionsItLeavesOut()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var sessions = new List<string>();
        for (var i = 1; i <= 3; i++) sessions.Add(await NewPromptedAsync(zed, "kimi", $"Task {i}"));
        var token = claude.Tokens[asker];
        async Task<string> ListAgents(bool includeArchived = false)
        {
            var query = new JsonObject { ["token"] = token };
            if (includeArchived) query["includeArchived"] = true;
            return PeersServer.ListAgentsText(await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, query));
        }
        const string One = "1 archived session is not shown; pass include_archived: true to list it.";

        Assert.IsFalse((await ListAgents()).Contains("archived session", StringComparison.Ordinal), "nothing when none is archived");

        await ArchiveAsync(zed, sessions[0], true);
        var text = await ListAgents();
        StringAssert.EndsWith(text, "]\n" + One);
        Assert.AreEqual(One, text.Split('\n')[^1]);
        StringAssert.StartsWith(text, $"You are session_id {asker} (claude, \"Coordinate the release\").\n{PeersServer.StatusLegend}\n[");

        await ArchiveAsync(zed, sessions[1], true);
        await ArchiveAsync(zed, sessions[2], true);
        StringAssert.EndsWith(await ListAgents(), $"\n{PeersServer.NoOtherSession}\n3 archived sessions are not shown; pass include_archived: true to list them.",
            "all three archived: no list, and how many it leaves out");
        StringAssert.EndsWith(await ListAgents(includeArchived: true), "]", "nothing when the archived ones are listed");

        // The caller itself is not in the list, so it is not counted either.
        await ArchiveAsync(zed, asker, true);
        StringAssert.EndsWith(await ListAgents(), $"\n{PeersServer.NoOtherSession}\n3 archived sessions are not shown; pass include_archived: true to list them.");
        await ArchiveAsync(zed, sessions[1], false);
        await ArchiveAsync(zed, sessions[2], false);
        StringAssert.EndsWith(await ListAgents(), "]\n" + One);
    }

    [TestMethod]
    public async Task ListAgents_NamesTheCallerFirst_WhichTheListLeavesOut()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the \"release\"");
        var other = await NewPromptedAsync(zed, "kimi", "Review the API");

        var peers = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject { ["token"] = claude.Tokens[asker] });

        Assert.AreEqual($"session_id {asker} (claude, \"Coordinate the 'release'\")", (string?)peers!["caller"], "named as every header names a session");
        Assert.AreEqual($"You are session_id {asker} (claude, \"Coordinate the 'release'\").", PeersServer.YouAre((string)peers["caller"]!));
        CollectionAssert.AreEqual(new[] { other }, peers["sessions"]!.AsArray().Select(s => (string?)s!["session_id"]).ToArray(), "the list still leaves the caller out");
        var anonymous = await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject());
        Assert.IsFalse(anonymous!.AsObject().ContainsKey("caller"), "a client without a token is nobody's session");
    }

    [TestMethod]
    public async Task ArchiveAndRestoreSession_SayWhatHappened_AndOnAnUnknownSessionWhatToPassInstead()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var other = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        var token = claude.Tokens[asker];
        static string Text(JsonNode? result) => (string)result!["text"]!;
        var who = $"session_id {other} (kimi, \"Fix the flaky test\")";
        var self = $"session_id {asker} (claude, \"Coordinate the release\") (this session)";

        Assert.AreEqual($"Archived {who}. It is now hidden from list_agents and from the user's session list; nothing in it is lost. " +
            "ask_agent still reaches it, and restore_session brings it back.", Text(await ArchiveAsync(zed, other, true, token)));
        Assert.AreEqual($"{who} was already archived, so nothing changed. ask_agent still reaches it, and restore_session brings it back.",
            Text(await ArchiveAsync(zed, other, true, token)));
        Assert.AreEqual($"Restored {who}. It is back in list_agents and in the user's session list.", Text(await ArchiveAsync(zed, other, false, token)));
        Assert.AreEqual($"{who} is not archived, so nothing changed. It is in list_agents and in the user's session list.",
            Text(await ArchiveAsync(zed, other, false, token)));

        // Its own session too.
        StringAssert.StartsWith(Text(await ArchiveAsync(zed, asker, true, token)), $"Archived {self}. ");
        Assert.IsTrue(conductor.Store.Get(asker)!.Archived);
        StringAssert.StartsWith(Text(await ArchiveAsync(zed, asker, false, token)), $"Restored {self}. ");

        // An unknown session: archive lists what it can archive, the caller's own first; restore lists the archived ones.
        var archive = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => ArchiveAsync(zed, "peer_kimi", true, token));
        Assert.AreEqual(JsonRpcException.ResourceNotFound, archive.Code);
        Assert.AreEqual($"No session \"peer_kimi\". Pass an exact session_id from list_agents. Sessions you can archive:\n- {self}\n- {who}", archive.Message);
        var none = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => ArchiveAsync(zed, "peer_kimi", false, token));
        Assert.AreEqual("No session \"peer_kimi\". Pass an exact session_id from list_agents with include_archived: true. No session is archived now.", none.Message);
        await ArchiveAsync(zed, other, true);
        var restore = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => ArchiveAsync(zed, "peer_kimi", false, token));
        Assert.AreEqual($"No session \"peer_kimi\". Pass an exact session_id from list_agents with include_archived: true. Archived sessions:\n- {who}", restore.Message);

        var forged = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => ArchiveAsync(zed, other, false, "forged"));
        Assert.AreEqual(JsonRpcException.InvalidRequest, forged.Code);
    }

    [TestMethod]
    public void ArchiveAndRestoreSession_AreNotPreAllowedInClaude()
    {
        // The service side (Kimi's approvals) is checked in OpinionTurnTests; the Claude adapter allows only these in its hook.
        CollectionAssert.AreEquivalent(new[] { "mcp__gorynych__reply", "mcp__gorynych__list_agents", "mcp__gorynych__spawn_session" },
            ChannelHost.PreAllowedTools.ToArray());
    }

    // ---------------------------------------------------------------- reports

    [TestMethod]
    public async Task Report_ToAnArchivedParent_IsDropped_NeitherATurnNorAChannelNote()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        await using var conductor = Conductor(null, kimi.Adapter, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var kimiParent = await NewPromptedAsync(zed, "kimi", "Coordinate the release");
        var claudeParent = await NewPromptedAsync(zed, "claude", "Plan the docs");
        await ArchiveAsync(zed, kimiParent, true);
        await ArchiveAsync(zed, claudeParent, true);

        // An archived session can still spawn; its children's turns are not reported to it.
        var toKimi = (string)(await SpawnAsync(zed, kimi.Tokens[kimiParent], "claude", "Write the notes"))!["sessionId"]!;
        var toClaude = (string)(await SpawnAsync(zed, claude.Tokens[claudeParent], "kimi", "Fix the flaky test"))!["sessionId"]!;
        await conductor.BackgroundTurnsIdleAsync();

        CollectionAssert.AreEqual(new[] { "Coordinate the release" }, Prompts(kimi, kimiParent), "no report turn");
        Assert.AreEqual(0, claude.Notes.Count, "no channel note");
        CollectionAssert.AreEqual(new[] { "Plan the docs" }, Prompts(claude, claudeParent));
        Assert.IsTrue(conductor.Store.Get(kimiParent)!.Archived && conductor.Store.Get(claudeParent)!.Archived);

        // Restored, the next turns are reported again.
        await ArchiveAsync(zed, kimiParent, false);
        await ArchiveAsync(zed, claudeParent, false);
        await using var other = await TestClient.ConnectAsync(conductor, "zed-children");
        await PromptAsync(other, toKimi, "Add the migration step");
        await PromptAsync(other, toClaude, "Add a test");
        await conductor.BackgroundTurnsIdleAsync();

        StringAssert.StartsWith(Prompts(kimi, kimiParent).Last(), $"[Report · from session_id {toKimi} ");
        StringAssert.StartsWith((string?)claude.Notes.Single()["text"], $"[Report · from session_id {toClaude} ");
    }
}
