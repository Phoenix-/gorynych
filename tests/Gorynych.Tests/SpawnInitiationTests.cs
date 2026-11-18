using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Who approves a spawn (docs/service-protocol.md, "Child sessions"): a spawn the agent says the
/// user asked for (<c>initiation: user_asked</c>) starts without the service's card when the
/// caller's turn began with the user's own prompt and no question from another session is open in
/// it; every other spawn is asked, and a <c>user_asked</c> one that is asked all the same tells the
/// agent why. A child never inherits its parent's full access; an explicit native mode is applied
/// before work, and full access from a restricted caller is approved with the spawn.
/// </summary>
[TestClass]
public sealed class SpawnInitiationTests
{
    static readonly string Cwd = Path.GetTempPath();

    static Conductor Conductor(params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
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

    /// <summary>What the peers MCP server sends for <c>spawn_session</c>.</summary>
    static Task<JsonNode?> SpawnAsync(TestClient client, string token, string? initiation, string? mode = null)
    {
        var args = new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } };
        if (initiation is not null) args["initiation"] = initiation;
        if (mode is not null) args["mode"] = mode;
        return client.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, args);
    }

    static Task<JsonNode?> AskAsync(TestClient client, string token, string target) =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = target, ["text"] = "Is it done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
        });

    static string[] SpawnCards(TestClient client)
    {
        lock (client.Approvals) return client.Approvals.Where(a => a.ToolCall.Title?.StartsWith("Start a", StringComparison.Ordinal) == true).Select(a => a.ToolCall.Title!).ToArray();
    }

    static string LastPrompt(LifecycleAgent agent)
    {
        lock (agent.Prompts) return agent.Prompts.Last().Text;
    }

    static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 250; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    /// <summary>Why a user_asked spawn is asked in a turn that another session's text came into.</summary>
    const string OtherText = "another session's text (a question or a report) came into this turn, or the turn did not start with a prompt from the user";

    static JsonObject AgentTurnMark(string mark) => new() { [GorynychProtocol.MetaAgentTurn] = mark };

    static readonly Func<RequestPermissionRequest, Task<RequestPermissionOutcome>> Start =
        _ => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "start" });

    // ---------------------------------------------------------------- in a turn the user started

    [TestMethod]
    [DataRow("user_asked", false, false, DisplayName = "user_asked, nothing open: started at once")]
    [DataRow("user_asked", true, true, DisplayName = "user_asked, a question open: asked")]
    [DataRow("proposal", false, true, DisplayName = "proposal: asked")]
    [DataRow("proposal", true, true, DisplayName = "proposal, a question open: asked")]
    [DataRow(null, false, true, DisplayName = "no initiation: a proposal, asked")]
    public async Task InTheUsersOwnTurn_OnlyAUserAskedSpawnWithNoQuestionOpen_StartsUnasked(string? initiation, bool questionOpen, bool asked)
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        zed.Approve = Start;
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var other = await NewPromptedAsync(zed, "kimi", "Review the notes");
        JsonNode? spawned = null;
        Task<JsonNode?>? question = null;
        var armed = true;
        claude.BeforePrompt = async id =>
        {
            if (id != parent || !armed) return;
            armed = false;
            if (questionOpen)
            {
                // Another session's question reaches the running turn through Claude's channel, unanswered yet.
                claude.HoldOpinions = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                question = AskAsync(zed, kimi.Tokens[other], parent);
                await Until(() => { lock (claude.Opinions) return claude.Opinions.Count == 1; }, "the question in the turn");
            }
            spawned = await SpawnAsync(zed, claude.Tokens[parent], initiation);
            claude.HoldOpinions?.SetResult();
        };

        await PromptAsync(zed, parent, "Start a Kimi session that fixes the flaky test");
        if (question is not null) await question;

        var child = (string)spawned!["sessionId"]!;
        Assert.AreEqual(asked ? 1 : 0, SpawnCards(zed).Length);
        Assert.AreEqual(initiation == "user_asked" && asked, spawned["note"] is not null, "a note only when user_asked was asked all the same");
        if (spawned["note"] is { } note)
            Assert.AreEqual($"The user was asked although you passed initiation user_asked, as {OtherText}.", (string?)note);
        await conductor.BackgroundTurnsIdleAsync();
        StringAssert.Contains(kimi.FirstBlocks(child)[1], asked ? "with spawn_session, approved by the user]" : "with spawn_session, at the user's request]");
        if (!asked)
            Assert.IsFalse(zed.Snapshot().Any(u => ((string?)u["toolCallId"])?.StartsWith("gorynych-spawn-", StringComparison.Ordinal) == true),
                "no card of the service's in the parent's thread: the agent's own tool call shows the spawn");
    }

    [TestMethod]
    public async Task AQuestionAnsweredEarlierInTheTurn_StillHoldsBackAUserAskedSpawn()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        zed.Approve = Start;
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var other = await NewPromptedAsync(zed, "kimi", "Review the notes");
        JsonNode? spawned = null;
        var armed = true;
        claude.BeforePrompt = async id =>
        {
            if (id != parent || !armed) return;
            armed = false;
            await AskAsync(zed, kimi.Tokens[other], parent); // reaches the running turn and is answered
            spawned = await SpawnAsync(zed, claude.Tokens[parent], "user_asked");
        };

        await PromptAsync(zed, parent, "Start a Kimi session that fixes the flaky test");

        Assert.AreEqual(1, claude.Opinions.Count, "the question came through the channel");
        Assert.AreEqual(1, SpawnCards(zed).Length, "its text stays in the turn, answered or not");
        Assert.AreEqual($"The user was asked although you passed initiation user_asked, as {OtherText}.", (string?)spawned!["note"]);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AChildsReportInTheUsersTurn_HoldsBackAUserAskedSpawn_AndTheNextTurnIsClearAgain()
    {
        var dir = TestDirs.New();
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var tests = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var fake = Path.Combine(tests, "Gorynych.FakeClaude", "bin", config, "net10.0", TestDirs.Exe("Gorynych.FakeClaude"));
        Assert.IsTrue(File.Exists(fake), "the isolated FakeClaude fixture must be built");
        var claude = new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "claude"), Executable = fake,
            ProjectsDirectory = Path.Combine(Path.GetTempPath(), "gorynych-fake-claude", "projects"),
            ClaudeStatePath = Path.Combine(dir, "no-claude-state.json"), ChannelServerCommand = ["gorynych-peers-not-started"],
            FetchCatalog = false, StartupTimeoutSeconds = 20, InterruptGraceMs = 3000, CloseTimeoutMs = 3000,
        });
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hosts = new InProcessHosts(name => name == "claude" ? claude : kimi.Adapter);
        await using var conductor = hosts.Conductor(dir, claude, kimi.Adapter); hosts.Service = conductor;
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = (await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" } })).SessionId;
        await PromptAsync(zed, parent, "warmup");
        var token = conductor.Store.Get(parent)!.PeerToken!;
        var route = conductor.Agents["claude"].RouteOf(parent)!;
        Assert.AreEqual(true, (bool?)route.Info.Meta?[GorynychProtocol.CapHostReportNote]?["durableRouting"], "actual parent host supports durable live notes");
        var child = await NewPromptedAsync(zed, "kimi", "child warmup");
        conductor.Store.Update(child, "kimi", record => { record.ParentSessionId = parent; record.ReportToParent = true; });

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        zed.Approve = async request =>
        {
            if (request.ToolCall.Title?.StartsWith("Start a", StringComparison.Ordinal) == true) return await Start(request);
            entered.TrySetResult(); await release.Task;
            return new SelectedPermissionOutcome { OptionId = request.Options[0].OptionId };
        };
        var before = SpawnCards(zed).Length;
        var turn = PromptAsync(zed, parent, "please write a file");
        JsonNode? spawned;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await PromptAsync(zed, child, "child work during the user's turn");
            await LinkHarness.Until(() => conductor.ReportStorage.List(child).Items.Any(x => x.Identity.Cause == ReportCause.User && x.Delivery is { Route: "note", DispatchStarted: true }),
                "the child's actual report enters the live note route").WaitAsync(TimeSpan.FromSeconds(15));
            var report = conductor.ReportStorage.List(child).Items.Single(x => x.Identity.Cause == ReportCause.User);
            var claim = report.Delivery!;
            await LinkHarness.Until(() =>
            {
                try { return (bool?)conductor.ReportStorage.ReadNoteReceipt(report.ReportId, parent, claim.HostId!, claim.HostGeneration!, claim.AttemptId)?["writeCompleted"] == true; }
                catch (InvalidOperationException) { return false; }
            }, "the host durably records native note input").WaitAsync(TimeSpan.FromSeconds(15));
            var uuid = (string)conductor.ReportStorage.ReadNoteReceipt(report.ReportId, parent, claim.HostId!, claim.HostGeneration!, claim.AttemptId)!["nativeMessageId"]!;
            var inputs = Path.Combine(Path.GetTempPath(), "gorynych-fake-claude", parent + ".inputs.jsonl");
            await LinkHarness.Until(() =>
            {
                try { return File.Exists(inputs) && File.ReadAllLines(inputs).Count(line => (string?)JsonNode.Parse(line)?["uuid"] == uuid) == 1; }
                catch (IOException) { return false; }
            }, "FakeClaude observes the report UUID once").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsFalse(turn.IsCompleted, "the user's native turn is still held at its real approval");
            Assert.IsTrue(conductor.Feed(parent).HasOtherSessionText, "live delivery marked the authority boundary");
            spawned = await SpawnAsync(zed, token, "user_asked");
            Assert.AreEqual(before + 1, SpawnCards(zed).Length, "the live report forces a spawn approval");
            Assert.AreEqual($"The user was asked although you passed initiation user_asked, as {OtherText}.", (string?)spawned!["note"]);
        }
        finally { release.TrySetResult(); await turn.WaitAsync(TimeSpan.FromSeconds(15)); }

        var spawnedChild = (string)spawned!["sessionId"]!;
        await LinkHarness.Until(() => conductor.ReportStorage.List(spawnedChild).Items.Any(x => x.Identity.Cause == ReportCause.Spawn && x.ProducerAck) &&
            conductor.ReportStorage.List(parent).Items.Where(x => x.Identity.Parent == parent).All(x => x.Status == "completed") &&
            !conductor.Feed(parent).TurnRunning && !conductor.Feed(parent).HasOtherSessionText, "all live report turns end and the marker clears").WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsFalse(conductor.Feed(parent).HasOtherSessionText, "the prior report does not taint the next user turn");

        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = PromptAsync(zed, parent, "please write another file");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var cards = SpawnCards(zed).Length;
            await SpawnAsync(zed, token, "user_asked");
            Assert.AreEqual(cards, SpawnCards(zed).Length, "a clean subsequent user turn does not need a spawn card");
        }
        finally { release.TrySetResult(); await next.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    [TestMethod]
    public async Task AUsersPromptJoiningATurnOfTheAgentsOwn_HoldsBackAUserAskedSpawn_UntilTheSessionIsIdle()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        zed.Approve = Start;
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var spawned = new List<JsonNode?>();
        var armed = 0;
        claude.BeforePrompt = async id =>
        {
            // The user's prompts only: a child's report reaches this parent (no channel) as a turn of its own.
            if (id != parent || armed == 0 || LastPrompt(claude).StartsWith("[Report", StringComparison.Ordinal)) return;
            armed--;
            spawned.Add(await SpawnAsync(zed, claude.Tokens[parent], "user_asked"));
        };
        // Claude begins a turn of its own (a channel message started it); it holds no turn gate.
        await claude.Adapter.Current!.Update(parent, new SessionInfoUpdate { Meta = AgentTurnMark("started") });
        await Until(() => conductor.Feed(parent).HasOtherSessionText, "the turn of its own");

        armed = 1;
        await PromptAsync(zed, parent, "Start a Kimi session that fixes the flaky test"); // joins that turn

        Assert.AreEqual(1, SpawnCards(zed).Length, "the user joined a turn they did not start");
        Assert.AreEqual($"The user was asked although you passed initiation user_asked, as {OtherText}.", (string?)spawned[0]!["note"]);
        Assert.IsTrue(conductor.Feed(parent).HasOtherSessionText, "the user's prompt ended, Claude's turn goes on");
        // The child it started reports its first turn to this parent when it likes; done before the next turn.
        await conductor.BackgroundTurnsIdleAsync();

        await claude.Adapter.Current!.Update(parent, new SessionInfoUpdate { Meta = AgentTurnMark("ended") });
        await Until(() => !conductor.Feed(parent).HasOtherSessionText, "idle again");
        armed = 1;
        await PromptAsync(zed, parent, "Start one more");

        Assert.AreEqual(1, SpawnCards(zed).Length, "a turn the user started, with nothing else in it");
        Assert.IsNull(spawned[1]!["note"]);
    }
    // ---------------------------------------------------------------- in any other turn

    [TestMethod]
    [DataRow("opinion", "user_asked", DisplayName = "a question from another session, user_asked")]
    [DataRow("opinion", "proposal", DisplayName = "a question from another session, proposal")]
    [DataRow("report", "user_asked", DisplayName = "a child's report, user_asked")]
    [DataRow("report", "proposal", DisplayName = "a child's report, proposal")]
    [DataRow("idle", "user_asked", DisplayName = "no client turn (the agent's own), user_asked")]
    [DataRow("idle", "proposal", DisplayName = "no client turn (the agent's own), proposal")]
    public async Task InATurnTheUserDidNotStart_EverySpawnIsAsked_AndUserAskedIsToldWhy(string origin, string initiation)
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        zed.Approve = Start;
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var other = await NewPromptedAsync(zed, "kimi", "Review the notes");
        JsonNode? spawned = null;
        var armed = false;
        var marker = origin == "opinion" ? "[Opinion request" : "[Report";
        claude.BeforePrompt = async id =>
        {
            if (id != parent || !armed || !LastPrompt(claude).StartsWith(marker, StringComparison.Ordinal)) return;
            armed = false;
            spawned = await SpawnAsync(zed, claude.Tokens[parent], initiation);
        };
        var before = SpawnCards(zed).Length;

        switch (origin)
        {
            case "opinion":
                armed = true;
                await AskAsync(zed, kimi.Tokens[other], parent);
                break;
            case "report":
                // Armed before the child starts: its finished turn is reported when it likes, as often
                // before the spawn's answer comes back as after it. That spawn is asked too (no client
                // turn), so its card comes first.
                before++;
                armed = true;
                await SpawnAsync(zed, claude.Tokens[parent], "proposal"); // a child, whose finished turn is reported as a turn
                await Until(() => spawned is not null, "the spawn from the report's turn");
                break;
            default:
                spawned = await SpawnAsync(zed, claude.Tokens[parent], initiation);
                break;
        }
        await conductor.BackgroundTurnsIdleAsync();

        Assert.IsNotNull(spawned);
        Assert.AreEqual(before + 1, SpawnCards(zed).Length, "asked");
        var why = origin switch
        {
            "opinion" => "this turn answers another session's question",
            _ => "this turn did not start with a prompt from the user",
        };
        Assert.AreEqual(initiation == "user_asked" ? $"The user was asked although you passed initiation user_asked, as {why}." : null, (string?)spawned["note"]);
        var card = zed.Snapshot().Last(u => (string?)u["sessionUpdate"] == "tool_call" && ((string?)u["toolCallId"])!.StartsWith("gorynych-spawn-", StringComparison.Ordinal));
        Assert.AreEqual(initiation == "user_asked", ((string?)card["content"]![0]!["content"]!["text"])!.Contains("The agent says you asked for this", StringComparison.Ordinal),
            "the card tells the person why they are asked although the agent says they asked");
    }

    [TestMethod]
    public async Task AUserAskedSpawn_AskedAndDeclined_SaysWhyItWasAsked()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        zed.Approve = _ => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "decline" });

        var declined = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(zed, claude.Tokens[parent], "user_asked"));

        Assert.AreEqual("The user declined to start the session. The user was asked although you passed initiation user_asked, " +
            "as this turn did not start with a prompt from the user.", declined.Message);
        var card = zed.Snapshot().First(u => (string?)u["sessionUpdate"] == "tool_call");
        StringAssert.Contains((string?)card["content"]![0]!["content"]!["text"],
            "The agent says you asked for this; Gorynych asks you all the same, as this turn did not start with a prompt from the user.");
        Assert.AreEqual(0, kimi.Prompts.Count, "nothing started");
    }

    [TestMethod]
    public async Task AnUnknownInitiation_FailsWithTheValues()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");

        var wrong = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(zed, claude.Tokens[parent], "offer"));

        Assert.AreEqual(JsonRpcException.InvalidParams, wrong.Code);
        Assert.AreEqual("No initiation \"offer\". Pass \"user_asked\" only when the user asked in this turn to start this session; otherwise \"proposal\".", wrong.Message);
        Assert.AreEqual(0, zed.Approvals.Count);
    }

    // ---------------------------------------------------------------- the child's mode

    [TestMethod]
    [DataRow("user_asked", "ask", false, 0)]
    [DataRow("user_asked", "full", false, 1)]
    [DataRow("user_asked", "full", true, 0)]
    [DataRow("proposal", "full", false, 1)]
    [DataRow("proposal", "full", true, 1)]
    public async Task ExplicitMode_IsAppliedBeforeWork_AndFullAccessIsConfirmedOnce(string initiation, string mode, bool parentFull, int approvals)
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { ReportsModes = true, HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true };
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        if (parentFull)
            await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                new JsonObject { ["sessionId"] = parent, ["configId"] = "mode", ["value"] = "full" });
        zed.Approve = request =>
        {
            Assert.AreEqual(0, kimi.Prompts.Count, "no work before approval");
            Assert.AreEqual(mode, (string?)request.ToolCall.RawInput?["mode"]);
            var card = zed.Snapshot().Last(u => (string?)u["toolCallId"] == request.ToolCall.ToolCallId && u["content"] is not null);
            StringAssert.Contains((string?)card["content"]![0]!["content"]!["text"], "Permissions:");
            return Start(request);
        };
        kimi.BeforePrompt = id =>
        {
            Assert.AreEqual(mode, kimi.Modes[id], "the first prompt sees the requested mode");
            return Task.CompletedTask;
        };
        JsonNode? spawned = null; var armed = 1; var spawnCalls = 0;
        claude.BeforePrompt = async id =>
        {
            if (id != parent || Interlocked.Exchange(ref armed, 0) == 0) return;
            Interlocked.Increment(ref spawnCalls);
            spawned = await SpawnAsync(zed, claude.Tokens[parent], initiation, mode);
        };

        await PromptAsync(zed, parent, "Start a Kimi coder");
        await conductor.BackgroundTurnsIdleAsync();

        var child = (string)spawned!["sessionId"]!;
        Assert.AreEqual(1, Volatile.Read(ref spawnCalls), "the user's turn initiated exactly one spawn even if a report turn followed");
        Assert.AreEqual(approvals, SpawnCards(zed).Length);
        CollectionAssert.AreEqual(new[] { AcpMethods.Agent.SessionSetConfigOption, AcpMethods.Agent.SessionPrompt },
            kimi.CallsFor(child).Where(m => m != AcpMethods.Agent.SessionNew).ToArray());
        Assert.AreEqual(mode == "full", Service.Conductor.IsFullAccess(conductor.Feed(child).State()));
        if (initiation == "user_asked" && !parentFull && mode == "full")
            StringAssert.Contains((string?)spawned["note"], "the child would have full access");
    }

    [TestMethod]
    [DataRow(true, "missing")]
    [DataRow(false, "full")]
    public async Task UnsupportedMode_FailsWithChoices_AndDiscardsTheChild(bool reportsModes, string mode)
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = reportsModes };
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate");

        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(zed, claude.Tokens[parent], "proposal", mode));

        StringAssert.Contains(error.Message, reportsModes ? "Available modes: ask, full" : "Available modes: none");
        Assert.AreEqual(0, kimi.Prompts.Count);
        Assert.AreEqual(0, zed.Approvals.Count);
        CollectionAssert.Contains(kimi.CallsFor("kimi-1").ToArray(), AcpMethods.Agent.SessionDelete);
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public async Task ExplicitMode_DeclinedOrCancelled_DisposesPreparedChildWithoutWork(bool cancelled, bool canDelete)
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: canDelete) { ReportsModes = true };
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate");
        zed.Approve = _ => Task.FromResult<RequestPermissionOutcome>(cancelled
            ? new RequestPermissionOutcomeCancelled() : new SelectedPermissionOutcome { OptionId = "decline" });

        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(zed, claude.Tokens[parent], "proposal", "full"));

        Assert.AreEqual(1, SpawnCards(zed).Length);
        Assert.AreEqual(0, kimi.Prompts.Count);
        if (canDelete) CollectionAssert.Contains(kimi.CallsFor("kimi-1").ToArray(), AcpMethods.Agent.SessionDelete);
        var listed = await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
        Assert.IsFalse(listed.Sessions.Any(s => s.SessionId == "kimi-1"), "a durable child without native deletion stays hidden");
    }

    [TestMethod]
    [DataRow("legacy")]
    [DataRow("grouped")]
    [DataRow("refused")]
    [DataRow("ignored")]
    [DataRow("missing-response")]
    public async Task NativeModeProtocol_RequiresSuccessfulSwitchBeforePublishingOrPrompting(string behavior)
    {
        var switched = false;
        var prompted = false;
        var closed = false;
        var legacy = behavior == "legacy";
        JsonObject State()
        {
            if (legacy) return new JsonObject
            {
                ["modes"] = new JsonObject
                {
                    ["currentModeId"] = switched ? "trusted" : "ask",
                    ["availableModes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "trusted", ["name"] = "Trusted", ["description"] = "Native full access",
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaFullAccess] = true },
                    }),
                },
            };
            return new JsonObject
            {
                ["configOptions"] = new JsonArray(new JsonObject
                {
                    ["id"] = "native-permissions", ["name"] = "Permissions", ["category"] = "mode", ["type"] = "select",
                    ["currentValue"] = switched ? "trusted" : "ask",
                    ["options"] = new JsonArray(new JsonObject
                    {
                        ["group"] = "native", ["name"] = "Native modes", ["options"] = new JsonArray(new JsonObject
                        {
                            ["value"] = "trusted", ["name"] = "Trusted", ["description"] = "Native full access",
                            ["_meta"] = new JsonObject { [GorynychProtocol.MetaFullAccess] = true },
                        }),
                    }),
                }),
            };
        }
        JsonNode? Handle(JsonRpcRequest request)
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init(close: true);
                case AcpMethods.Agent.SessionList: return AcpJson.ToNode(new ListSessionsResponse { Sessions = [] });
                case AcpMethods.Agent.SessionNew:
                    var state = State();
                    state["sessionId"] = "native-child";
                    return state;
                case AcpMethods.Agent.SessionSetConfigOption:
                case AcpMethods.Agent.SessionSetMode:
                    Assert.AreEqual(legacy ? AcpMethods.Agent.SessionSetMode : AcpMethods.Agent.SessionSetConfigOption, request.Method);
                    Assert.AreEqual("trusted", (string?)request.Params?[legacy ? "modeId" : "value"]);
                    if (!legacy) Assert.AreEqual("native-permissions", (string?)request.Params?["configId"]);
                    if (behavior == "refused") throw new JsonRpcException(JsonRpcException.InvalidRequest, "Cannot switch this session");
                    switched = behavior != "ignored";
                    return legacy || behavior == "missing-response" ? new JsonObject() : State();
                case AcpMethods.Agent.SessionPrompt:
                    Assert.IsTrue(switched);
                    prompted = true;
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                case AcpMethods.Agent.SessionClose:
                    closed = true;
                    return new JsonObject();
                default: throw new JsonRpcException(JsonRpcException.MethodNotFound, request.Method);
            }
        }
        var parentAgent = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var target = new ScriptedAdapter("native", (_, request, _) => Task.FromResult(Handle(request)));
        await using var conductor = Conductor(parentAgent.Adapter, target);
        await using var client = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(client, "claude", "Coordinate");
        client.Approve = request =>
        {
            Assert.IsFalse(prompted);
            var card = client.Snapshot().Last(u => (string?)u["toolCallId"] == request.ToolCall.ToolCallId && u["content"] is not null);
            var text = (string?)card["content"]![0]!["content"]!["text"];
            StringAssert.Contains(text, "Trusted (trusted)");
            StringAssert.Contains(text, "outside this folder without approval");
            return Start(request);
        };
        Task<JsonNode?> Spawn() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject
        {
            ["agent"] = "native", ["prompt"] = "Build and test", ["mode"] = "trusted", ["report"] = false,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = parentAgent.Tokens[parent] },
        });

        if (behavior is "legacy" or "grouped")
        {
            await Spawn();
            await conductor.BackgroundTurnsIdleAsync();
            Assert.IsTrue(prompted);
            Assert.IsTrue(Service.Conductor.IsFullAccess(conductor.Feed("native-child").State()));
            Assert.AreEqual(1, client.Approvals.Count);
        }
        else
        {
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(Spawn);
            StringAssert.Contains(error.Message, behavior == "refused" ? "Cannot switch" : "did not confirm");
            Assert.IsFalse(prompted);
            Assert.IsTrue(closed, "an unused session is closed even when native deletion is unavailable");
            Assert.AreEqual(0, client.Approvals.Count);
        }
    }

    [TestMethod]
    [DataRow("user_asked")]
    [DataRow("proposal")]
    public async Task AChildOfASessionInFullAccess_StartsInItsAgentsDefaultMode(string initiation)
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { ReportsModes = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true };
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        zed.Approve = Start;
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = parent, ["configId"] = "mode", ["value"] = "full" });
        JsonNode? spawned = null;
        var armed = true;
        claude.BeforePrompt = async id =>
        {
            if (id != parent || !armed) return;
            armed = false;
            spawned = await SpawnAsync(zed, claude.Tokens[parent], initiation);
        };

        await PromptAsync(zed, parent, "Start a Kimi session that fixes the flaky test");
        await conductor.BackgroundTurnsIdleAsync();

        var child = (string)spawned!["sessionId"]!;
        Assert.IsTrue(Service.Conductor.IsFullAccess(conductor.Feed(parent).State()), "the parent is in full access");
        var state = conductor.Feed(child).State();
        Assert.AreEqual("ask", (string?)state["configOptions"]![0]!["currentValue"], "the agent's default");
        Assert.IsFalse(Service.Conductor.IsFullAccess(state));
        lock (kimi.Modes) Assert.IsFalse(kimi.Modes.ContainsKey(child), "no mode was set for it");
        Assert.AreEqual(0, kimi.CallsFor(child).Count(m => m == AcpMethods.Agent.SessionSetConfigOption));
    }
}
