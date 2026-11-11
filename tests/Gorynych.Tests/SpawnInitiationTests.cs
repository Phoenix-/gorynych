using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Who approves a spawn (docs/service-protocol.md, "Child sessions"): a spawn the agent says the
/// user asked for (<c>initiation: user_asked</c>) starts without the service's card when the
/// caller's turn began with the user's own prompt and no question from another session is open in
/// it; every other spawn is asked, and a <c>user_asked</c> one that is asked all the same tells the
/// agent why. A child never takes its parent's full access.
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
    static Task<JsonNode?> SpawnAsync(TestClient client, string token, string? initiation)
    {
        var args = new JsonObject { ["agent"] = "kimi", ["prompt"] = "Fix the flaky test", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } };
        if (initiation is not null) args["initiation"] = initiation;
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
    public async Task AChildsReportInTheUsersTurn_HoldsBackAUserAskedSpawn_AndTheNextTurnIsClearAgain()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        zed.Approve = Start;
        var parent = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var child = (string)(await SpawnAsync(zed, claude.Tokens[parent], "proposal"))!["sessionId"]!;
        await conductor.BackgroundTurnsIdleAsync();
        await Until(() => { lock (claude.Notes) return claude.Notes.Count == 1; }, "the first turn's report, as a note");
        await PromptAsync(zed, parent, "Thanks"); // a turn of the user's ends: nothing of the report is left
        Assert.IsFalse(conductor.Feed(parent).HasOtherSessionText);
        JsonNode? spawned = null;
        var armed = true;
        claude.BeforePrompt = async id =>
        {
            if (id != parent || !armed) return;
            armed = false;
            await PromptAsync(zed, child, "One more thing"); // its end is reported into this turn
            await Until(() => { lock (claude.Notes) return claude.Notes.Count == 2; }, "the report in the turn");
            spawned = await SpawnAsync(zed, claude.Tokens[parent], "user_asked");
            // The session it starts reports its first turn too: into this turn, not after it, where
            // the report would rightly mark the idle parent again.
            await Until(() => { lock (claude.Notes) return claude.Notes.Count == 3; }, "the started session's report in the turn");
        };
        var before = SpawnCards(zed).Length;

        await PromptAsync(zed, parent, "Start a Kimi session that writes the docs");

        Assert.AreEqual(before + 1, SpawnCards(zed).Length, "a report holds another agent's text");
        Assert.AreEqual($"The user was asked although you passed initiation user_asked, as {OtherText}.", (string?)spawned!["note"]);
        Assert.IsFalse(conductor.Feed(parent).HasOtherSessionText, "cleared when the turn ended");
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
