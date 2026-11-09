using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

/// <summary>
/// The headless Claude adapter's model catalog (kept across restarts, fetched at start, shown to every
/// open session) and its Full access mode (docs/adapters.md, "Mode, model and effort selectors").
/// </summary>
public sealed partial class ClaudeHeadlessTests
{
    const string OtherCatalog = """{"models":[{"value":"old","resolvedModel":"claude-old","displayName":"Old","supportedEffortLevels":["low"]}]}""";

    static string CatalogFile(string dir) => Path.Combine(dir, "claude", "catalog.json");

    static void KeepCatalog(string dir, string text)
    {
        Directory.CreateDirectory(Path.Combine(dir, "claude"));
        File.WriteAllText(CatalogFile(dir), text);
    }

    static async Task<NewSessionResponse> NewRawAsync(TestClient client, string dir) =>
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" },
        });

    static Task<SetSessionConfigOptionResponse> SetModeAsync(TestClient client, string sessionId, string mode) =>
        client.Rpc.RequestAsync<SetSessionConfigOptionRequest, SetSessionConfigOptionResponse>(AcpMethods.Agent.SessionSetConfigOption,
            new SetSessionConfigOptionRequestValueId { SessionId = sessionId, ConfigId = "mode", Value = mode });

    static string[] Ids(IEnumerable<SessionConfigOption>? options) => options?.Select(o => o.Id).ToArray() ?? [];

    static JsonObject ModeOptionOf(IEnumerable<SessionConfigOption> options) =>
        AcpJson.ToNode(options.Single(o => o.Id == "mode"))!.AsObject();

    static JsonObject[] OptionUpdates(TestClient client, string sessionId)
    {
        lock (client.Updates)
            return client.SessionUpdates.Where(u => u.SessionId == sessionId && (string?)u.Update["sessionUpdate"] == "config_option_update")
                .Select(u => u.Update).ToArray();
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AKeptCatalog_ShowsAllThreeSelectors_BeforeAnyLaunch()
    {
        var dir = TestDirs.New();
        KeepCatalog(dir, """{"models":[{"value":"default","resolvedModel":"claude-fake-1","displayName":"Default","supportedEffortLevels":["low","high"]}]}""");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);

        var created = await NewRawAsync(client, dir);

        CollectionAssert.AreEquivalent(new[] { "mode", "model", "reasoning_effort" }, Ids(created.ConfigOptions));
        Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, created.SessionId + ".args.json")), "no Claude was launched for it");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ABrokenCatalogFile_GivesNoCatalog_AndNoCrash()
    {
        var dir = TestDirs.New();
        KeepCatalog(dir, "{ not json");
        var log = new List<string>();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, log, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);

        var created = await NewRawAsync(client, dir);

        CollectionAssert.AreEqual(new[] { "mode" }, Ids(created.ConfigOptions));
        lock (log) Assert.IsTrue(log.Any(l => l.Contains("catalog.json unreadable", StringComparison.Ordinal)), string.Join(" | ", log));
        await PromptAsync(client, created.SessionId, "hello");
        StringAssert.Contains(File.ReadAllText(CatalogFile(dir)), "claude-fake-2", "the next launch's catalog replaces it");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task WithNoCatalogKept_ItIsFetchedAtStart_WithoutAPromptOrAQuestion_AndReachesTheOpenSession()
    {
        var dir = TestDirs.New();
        var started = DateTime.UtcNow.AddSeconds(-1);
        var log = new List<string>();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, log)));
        await using var client = await TestClient.ConnectAsync(conductor);

        var first = await NewRawAsync(client, dir); // starts the adapter, which fetches in the background

        await Until(() => File.Exists(CatalogFile(dir)) && OptionUpdates(client, first.SessionId).Length > 0);
        StringAssert.Contains(File.ReadAllText(CatalogFile(dir)), "claude-fake-2");
        CollectionAssert.AreEquivalent(new[] { "mode", "model", "reasoning_effort" }, OptionUpdates(client, first.SessionId).Last()["configOptions"]!.AsArray()
            .Select(o => (string)o!["id"]!).ToArray(), "the open session got its selectors without a prompt");
        CollectionAssert.AreEquivalent(new[] { "mode", "model", "reasoning_effort" }, Ids((await NewRawAsync(client, dir)).ConfigOptions));
        Assert.AreEqual(0, client.Approvals.Count, "no folder-trust question: the fetch runs in the adapter's own folder");
        Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, first.SessionId + ".args.json")), "the session itself was not launched");
        var fetch = Directory.GetFiles(FakeRoot, "*.args.json").Where(f => File.GetLastWriteTimeUtc(f) >= started)
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!.AsArray().Select(a => (string)a!).ToList())
            .Single(a => a.Contains("--no-session-persistence") && a[a.IndexOf("--settings") + 1].StartsWith(Path.Combine(dir, "claude", "catalog"), StringComparison.OrdinalIgnoreCase));
        CollectionAssert.IsSubsetOf(new[] { "-p", "--permission-prompt-tool=stdio", "--settings" }, fetch, "launched as every session is");
        Assert.AreEqual("default", fetch[fetch.IndexOf("--permission-mode") + 1]);
        Assert.AreEqual("local", fetch[fetch.IndexOf("--setting-sources") + 1], "none of the person's settings files, so none of their hooks");
        CollectionAssert.Contains(fetch, "--strict-mcp-config", "no MCP server of theirs");
        Assert.AreEqual("""{"mcpServers":{}}""", File.ReadAllText(fetch[fetch.IndexOf("--mcp-config") + 1]));
        lock (log) Assert.IsTrue(log.Any(l => l.Contains("fetched the model catalog", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ACatalogFileOfTheWrongShape_GivesNoCatalog_AndNoCrash()
    {
        var dir = TestDirs.New();
        KeepCatalog(dir, """{"models":[{"value":"a","displayName":7,"supportsAutoMode":"yes","supportedEffortLevels":{"x":1}}]}""");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);

        CollectionAssert.AreEqual(new[] { "mode" }, Ids((await NewRawAsync(client, dir)).ConfigOptions));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ACatalogThatCouldNotBeSaved_IsSavedWithTheNextLaunch()
    {
        var dir = TestDirs.New();
        var stuck = Directory.CreateDirectory(CatalogFile(dir) + ".tmp"); // the file cannot be replaced
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        await PromptAsync(client, await NewAsync(client, dir), "hello");
        Assert.IsFalse(File.Exists(CatalogFile(dir)));

        stuck.Delete();
        await PromptAsync(client, await NewAsync(client, dir), "hello");

        StringAssert.Contains(File.ReadAllText(CatalogFile(dir)), "claude-fake-2", "the same catalog, written now");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AFetchOffThePlan_KeepsNoCatalog()
    {
        var dir = TestDirs.New();
        var log = new List<string>();
        Directory.CreateDirectory(Path.Combine(dir, "claude", "catalog"));
        File.WriteAllText(Path.Combine(dir, "claude", "catalog", "api-key"), ""); // the fake's account then shows an API key
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, log)));
        await using var client = await TestClient.ConnectAsync(conductor);

        await NewRawAsync(client, dir);

        await Until(() => { lock (log) return log.Any(l => l.Contains("no model catalog fetched", StringComparison.Ordinal)); });
        lock (log) Assert.IsTrue(log.Any(l => l.Contains("no model catalog fetched", StringComparison.Ordinal)), string.Join(" | ", log));
        Assert.IsFalse(File.Exists(CatalogFile(dir)));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AChangedCatalog_ReachesEveryOpenSession_AndIsKept()
    {
        var dir = TestDirs.New();
        KeepCatalog(dir, OtherCatalog);
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var idle = (await NewRawAsync(client, dir)).SessionId;
        var running = await NewAsync(client, dir);

        await PromptAsync(client, running, "hello"); // its launch brings the fake's own catalog

        await Until(() => OptionUpdates(client, idle).Length > 0);
        var models = OptionUpdates(client, idle).Last()["configOptions"]!.AsArray().Single(o => (string?)o!["id"] == "model")!["options"]!.AsArray();
        CollectionAssert.Contains(models.Select(m => (string)m!["value"]!).ToArray(), "fast", "the session nobody prompted got the new catalog");
        StringAssert.Contains(File.ReadAllText(CatalogFile(dir)), "claude-fake-2");

        var before = OptionUpdates(client, idle).Length;
        var another = await NewAsync(client, dir);
        await PromptAsync(client, another, "hi");
        await Task.Delay(300);
        Assert.AreEqual(before, OptionUpdates(client, idle).Length, "the same catalog again is news to nobody");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task FullAccess_StartsClaudeAgainForTheNextTurn_AsksNothing_AndLeavingItHoldsAtOnce()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        await PromptAsync(client, id, "hello");
        var asked = client.Approvals.Count; // the folder-trust question

        var full = await SetModeAsync(client, id, ClaudeOptions.FullAccess);

        var option = ModeOptionOf(full.ConfigOptions);
        Assert.AreEqual(ClaudeOptions.FullAccess, (string?)option["currentValue"]);
        var entry = option["options"]!.AsArray().Single(o => (string?)o!["value"] == ClaudeOptions.FullAccess)!;
        Assert.AreEqual("Full access", (string?)entry["name"]);
        Assert.IsFalse(Controls(id).Any(c => (string?)c["subtype"] == "set_permission_mode"), "not switched inside the running Claude");
        StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "claude", "sessions.json")), ClaudeOptions.FullAccess, "kept in the session's record");

        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, id, "write")).StopReason);
        Assert.AreEqual(asked, client.Approvals.Count, "nothing asked in full access");
        var args = LaunchArgs(id);
        Assert.AreEqual(ClaudeOptions.FullAccess, args[args.IndexOf("--permission-mode") + 1]);
        CollectionAssert.DoesNotContain(args, "--allow-dangerously-skip-permissions");
        CollectionAssert.DoesNotContain(args, "--dangerously-skip-permissions");
        Assert.AreEqual(2, Controls(id).Count(c => (string?)c["subtype"] == "initialize"), "a second Claude, started in full access");

        var back = await SetModeAsync(client, id, "default");

        Assert.AreEqual("default", (string?)ModeOptionOf(back.ConfigOptions)["currentValue"]);
        Assert.AreEqual("default", (string?)Controls(id).Last(c => (string?)c["subtype"] == "set_permission_mode")["mode"], "left inside the running Claude");
        Assert.AreEqual(2, Controls(id).Count(c => (string?)c["subtype"] == "initialize"), "no new Claude for it");
        await PromptAsync(client, id, "write");
        Assert.AreEqual(asked + 1, client.Approvals.Count, "the next write asks again");
    }

    static string[] AskedTools(TestClient client, int from) => client.Approvals.Skip(from).Select(a => a.ToolCall.Title ?? "").ToArray();

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow("default")]
    [DataRow("acceptEdits")]
    [DataRow("plan")]
    [DataRow("auto")]
    [DataRow(ClaudeOptions.FullAccess)]
    public async Task TheGorynychTools_RunUnasked_InEveryMode(string mode)
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        await PromptAsync(client, id, "hello");
        await SetModeAsync(client, id, mode);
        var before = client.Approvals.Count;

        await PromptAsync(client, id, "gorynych tools");

        StringAssert.Contains(client.Text(), "ask_agent:allow archive_session:allow restore_session:allow");
        Assert.AreEqual(before, client.Approvals.Count, "nothing asked: " + string.Join(" | ", AskedTools(client, before)));
        var args = LaunchArgs(id);
        var permissions = JsonNode.Parse(File.ReadAllText(args[args.IndexOf("--settings") + 1]))!["permissions"]!;
        CollectionAssert.AreEquivalent(
            new[] { "mcp__gorynych__reply", "mcp__gorynych__list_agents", "mcp__gorynych__ask_agent", "mcp__gorynych__archive_session", "mcp__gorynych__restore_session" },
            permissions["allow"]!.AsArray().Select(r => (string?)r).ToArray(), "allowed by the launch, so auto's classifier does not judge them; spawn_session is not");
        CollectionAssert.AreEqual(new[] { "mcp__gorynych-outside" }, permissions["deny"]!.AsArray().Select(r => (string?)r).ToArray());
        Assert.IsTrue(Controls(id).Where(c => (string?)c["subtype"] == "initialize").All(c => c["hooks"] is null), "no hooks");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow("default")]
    [DataRow(ClaudeOptions.FullAccess)]
    public async Task ThePersonsDenyRule_StillStopsAGorynychTool(string mode)
    {
        var dir = TestDirs.New();
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(Path.Combine(dir, ".claude", "settings.json"), """{"permissions":{"deny":["mcp__gorynych__ask_agent"]}}""");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        await PromptAsync(client, id, "hello");
        await SetModeAsync(client, id, mode);
        var before = client.Approvals.Count;

        await PromptAsync(client, id, "gorynych tools");

        StringAssert.Contains(client.Text(), "ask_agent:deny archive_session:allow restore_session:allow");
        Assert.AreEqual(before, client.Approvals.Count);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AChildOfASessionInFullAccess_StartsInAsk()
    {
        var dir = TestDirs.New();
        await using var conductor = new Gorynych.Service.Conductor(new Gorynych.Service.ConductorOptions
        {
            DataDirectory = dir, ServiceId = "test-service", PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        }, [new ClaudeAdapter(Options(dir, fetchCatalog: false))]);
        await using var client = await TestClient.ConnectAsync(conductor);
        var parent = await NewAsync(client, dir);
        await SetModeAsync(client, parent, ClaudeOptions.FullAccess);
        await PromptAsync(client, parent, "hello");
        var args = LaunchArgs(parent);
        var peers = JsonNode.Parse(File.ReadAllText(args[args.IndexOf("--mcp-config") + 1]))!["mcpServers"]!["gorynych"]!["args"]!.AsArray().Select(a => (string)a!).ToList();

        var spawned = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject
        {
            ["agent"] = "claude", ["prompt"] = "hello", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = peers[peers.IndexOf("--token") + 1] },
        });
        await conductor.BackgroundTurnsIdleAsync();

        var child = (string)spawned!["sessionId"]!;
        var childArgs = LaunchArgs(child);
        Assert.AreEqual("default", childArgs[childArgs.IndexOf("--permission-mode") + 1], "the child's own first launch, in Ask, not its parent's full access");
        Assert.AreEqual(ClaudeOptions.FullAccess, args[args.IndexOf("--permission-mode") + 1]);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task APersistedFullAccess_AfterARestart_IsReportedMarked_AndStartsInIt()
    {
        var dir = TestDirs.New();
        string id;
        await using (var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false))))
        {
            await using var client = await TestClient.ConnectAsync(conductor);
            id = await NewAsync(client, dir);
            await PromptAsync(client, id, "hello");
            await SetModeAsync(client, id, ClaudeOptions.FullAccess);
        }
        await using (var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false))))
        {
            await using var client = await TestClient.ConnectAsync(conductor);

            var resumed = await client.Rpc.RequestAsync<ResumeSessionRequest, ResumeSessionResponse>(AcpMethods.Agent.SessionResume,
                new ResumeSessionRequest { SessionId = id, Cwd = dir });

            var option = ModeOptionOf(resumed.ConfigOptions!);
            Assert.AreEqual(ClaudeOptions.FullAccess, (string?)option["currentValue"], "the mode it is loaded in, before Claude runs");
            var marked = option["options"]!.AsArray().Where(o => o!["_meta"]?[GorynychProtocol.MetaFullAccess] is JsonValue v && v.GetValue<bool>())
                .Select(o => (string?)o!["value"]).ToArray();
            CollectionAssert.AreEqual(new[] { ClaudeOptions.FullAccess }, marked, "so the service knows to ask before a session outside it asks this one");
            await PromptAsync(client, id, "gorynych tools");
            var args = LaunchArgs(id);
            Assert.AreEqual(ClaudeOptions.FullAccess, args[args.IndexOf("--permission-mode") + 1], "resumed in full access");
            Assert.AreEqual(0, client.Approvals.Count, "nothing asked: " + string.Join(" | ", AskedTools(client, 0)));
        }
    }
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task LeavingFullAccess_WhenClaudeRefuses_StopsIt_AndTheNextTurnAsks()
    {
        var dir = TestDirs.New();
        var log = new List<string>();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, log, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        await SetModeAsync(client, id, ClaudeOptions.FullAccess);
        await PromptAsync(client, id, "hello");
        File.WriteAllText(Path.Combine(dir, "no-leave"), "");

        var back = await SetModeAsync(client, id, "default");

        Assert.AreEqual("default", (string?)ModeOptionOf(back.ConfigOptions)["currentValue"]);
        lock (log) Assert.IsTrue(log.Any(l => l.Contains("did not leave full access", StringComparison.Ordinal)), string.Join(" | ", log));
        File.Delete(Path.Combine(dir, "no-leave"));
        var before = client.Approvals.Count;
        await PromptAsync(client, id, "write");
        var args = LaunchArgs(id);
        Assert.AreEqual("default", args[args.IndexOf("--permission-mode") + 1], "a new Claude, in Ask");
        Assert.AreEqual(before + 1, client.Approvals.Count, "the write asks");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AClaudeThatStartsInFullAccessUnasked_IsStopped()
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "force-bypass"), "");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(client, id, "write"));

        StringAssert.Contains(refused.Message, "did not choose");
        Assert.IsFalse(File.Exists(Path.Combine(dir, "fake.txt")), "nothing ran");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AChosenModelTheCatalogDropped_StaysTheSessionsModel()
    {
        var dir = TestDirs.New();
        var other = TestDirs.New();
        File.WriteAllText(Path.Combine(other, "models-small"), "");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        await PromptAsync(client, id, "hello");
        await client.Rpc.RequestAsync<SetSessionConfigOptionRequest, SetSessionConfigOptionResponse>(AcpMethods.Agent.SessionSetConfigOption,
            new SetSessionConfigOptionRequestValueId { SessionId = id, ConfigId = "model", Value = "fast" });
        var shown = OptionUpdates(client, id).Length;

        var elsewhere = await NewAsync(client, other);
        await PromptAsync(client, elsewhere, "hello"); // its catalog has no "fast"

        await Until(() => OptionUpdates(client, id).Length > shown);
        var model = OptionUpdates(client, id).Last()["configOptions"]!.AsArray().Single(o => (string?)o!["id"] == "model")!;
        Assert.AreEqual("fast", (string?)model["currentValue"], "still the model the session runs on");
        CollectionAssert.AreEquivalent(new[] { "default", "fast" }, model["options"]!.AsArray().Select(o => (string)o!["value"]!).ToArray());
        Assert.IsFalse(File.ReadAllText(CatalogFile(dir)).Contains("claude-fake-2", StringComparison.Ordinal), "the catalog kept is the smaller one");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task FullAccess_WaitsForTheRunningTurn()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        await PromptAsync(client, id, "hello");
        var slow = PromptAsync(client, id, "slow hold");
        await Until(() => client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call"));
        Assert.IsFalse(slow.IsCompleted, "the turn still runs");

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SetModeAsync(client, id, ClaudeOptions.FullAccess));

        StringAssert.Contains(refused.Message, "once it has ended");
        await slow;
        Assert.AreEqual(ClaudeOptions.FullAccess, (string?)ModeOptionOf((await SetModeAsync(client, id, ClaudeOptions.FullAccess)).ConfigOptions)["currentValue"]);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AClaudeThatDoesNotStartInFullAccess_FailsLoudly()
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "no-bypass"), "");
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);
        await SetModeAsync(client, id, ClaudeOptions.FullAccess);

        var failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(client, id, "write"));

        StringAssert.Contains(failed.Message, "instead of full access");
        Assert.IsFalse(File.Exists(Path.Combine(dir, "fake.txt")), "nothing ran");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheAdaptersDefaultMode_IsNeverFullAccess()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(Options(dir, fetchCatalog: false, permissionMode: ClaudeOptions.FullAccess)));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = await NewAsync(client, dir);

        await PromptAsync(client, id, "write");

        var args = LaunchArgs(id);
        Assert.AreEqual("default", args[args.IndexOf("--permission-mode") + 1]);
        Assert.AreEqual(2, client.Approvals.Count, "trust and the write: it asks");
    }
}
