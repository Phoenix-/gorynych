using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Service turns (opinions and reports) for a session that is not loaded in its agent, or whose
/// agent is not running (Claude): the service loads or starts it for the turn, and closes it
/// again after its last service turn, unless a client has it open by then
/// (docs/adapter-contract.md, "Service turns").
/// </summary>
[TestClass]
public sealed class WakeTests
{
    static readonly string Cwd = Path.GetTempPath();
    const string Resume = AcpMethods.Agent.SessionResume;
    const string Prompt = AcpMethods.Agent.SessionPrompt;
    const string Close = AcpMethods.Agent.SessionClose;
    const string Opinion = GorynychProtocol.MethodOpinion;
    const string Note = GorynychProtocol.MethodNote;

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

    static Task CloseAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId });

    static Task<JsonNode?> AskAsync(TestClient client, string token, string target) =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion,
            new JsonObject { ["sessionId"] = target, ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } });

    /// <summary>
    /// A session one client made and prompted, then left: with <c>session/close</c>
    /// (<paramref name="close"/>) or by going away without it. Either way it is closed in its agent.
    /// </summary>
    static async Task<string> LeftSessionAsync(Conductor conductor, string agent, string text, bool close)
    {
        string id;
        await using (var owner = await TestClient.ConnectAsync(conductor, "owner"))
        {
            id = await NewPromptedAsync(owner, agent, text);
            if (close) await CloseAsync(owner, id);
        }
        await conductor.CleanupsIdleAsync();
        Assert.IsFalse(conductor.Agents[agent].IsLive(id), "closed in its agent once its client left");
        return id;
    }

    /// <summary>The calls for the session from <paramref name="first"/> on.</summary>
    static string[] CallsFrom(LifecycleAgent agent, string sessionId, string first)
    {
        var calls = agent.CallsFor(sessionId);
        return calls[Array.LastIndexOf(calls, first)..];
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

    // ---------------------------------------------------------------- wake, then close again

    [TestMethod]
    public async Task AskAgent_ToAKimiSessionThatIsNotLoaded_LoadsIt_AsksInATurn_AndClosesItAgain_StillArchived()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        var target = await LeftSessionAsync(conductor, "kimi", "Fix the flaky test", close: true);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = target, ["archived"] = true });
        Assert.IsFalse(conductor.Agents["kimi"].IsLive(target));

        var answer = await AskAsync(zed, claude.Tokens[asker], target);

        Assert.AreEqual("ok", (string?)answer!["text"]);
        await Until(() => kimi.CallsFor(target).Last() == Close, "the close after the turn");
        CollectionAssert.AreEqual(new[] { Resume, Opinion, Prompt, Close }, CallsFrom(kimi, target, Resume));
        // The agent records the close before the service hears its answer and marks the session closed.
        await Until(() => !conductor.Agents["kimi"].IsLive(target), "unloaded again");
        Assert.IsTrue(conductor.Store.Get(target)!.Archived, "an archived session is woken the same way and stays archived");
    }

    [TestMethod]
    public async Task AskAgent_ToAClaudeSessionThatIsNotRunning_StartsItInATurn_AndClosesItAgain()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true, Running = false };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        // Its client went away without closing it, and its Claude process does not start until a prompt.
        var target = await LeftSessionAsync(conductor, "claude", "Write the notes", close: false);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "kimi", "Coordinate the release");

        var answer = await AskAsync(zed, kimi.Tokens[asker], target);

        Assert.AreEqual("ok", (string?)answer!["text"], "asked in a turn: the turn's text, since this fake does not call reply");
        StringAssert.StartsWith(claude.Prompts.Last().Text, $"[Opinion request · from session_id {asker} (kimi, \"Coordinate the release\") · message_id m");
        await Until(() => claude.CallsFor(target).Last() == Close, "the close after the turn");
        CollectionAssert.AreEqual(new[] { Resume, Opinion, Prompt, Close }, CallsFrom(claude, target, Resume));
        await Until(() => !conductor.Agents["claude"].IsLive(target), "unloaded again");
    }

    [TestMethod]
    public async Task AServiceTurn_LeavesASessionThatWasAlreadyLoaded_AsItWas()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        // A client has both open: Kimi has one loaded, and Claude's process runs for the other.
        await using var owner = await TestClient.ConnectAsync(conductor, "owner");
        var loadedKimi = await NewPromptedAsync(owner, "kimi", "Fix the flaky test");
        var runningClaude = await NewPromptedAsync(owner, "claude", "Write the notes");
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "kimi", "Coordinate the release");

        await AskAsync(zed, kimi.Tokens[asker], loadedKimi);   // a turn
        await AskAsync(zed, kimi.Tokens[asker], runningClaude); // through the channel
        await Task.Delay(300);
        await conductor.CleanupsIdleAsync();

        Assert.AreEqual(Prompt, kimi.CallsFor(loadedKimi).Last(), "not closed");
        Assert.AreEqual(Opinion, claude.CallsFor(runningClaude).Last(), "not closed");
        Assert.IsTrue(conductor.Agents["kimi"].IsLive(loadedKimi) && conductor.Agents["claude"].IsLive(runningClaude));
    }

    [TestMethod]
    public async Task AClientThatOpensTheWokenSessionMeanwhile_KeepsItOpen_UntilItGoes()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        var target = await LeftSessionAsync(conductor, "kimi", "Fix the flaky test", close: true);
        kimi.BeforePrompt = id => id == target ? hold.Task : Task.CompletedTask;
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the release");

        var asking = AskAsync(zed, claude.Tokens[asker], target);
        await Until(() => kimi.CallsFor(target).Last() == Prompt, "the question's turn");
        await using (var other = await TestClient.ConnectAsync(conductor, "zed-2"))
        {
            await other.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = target, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
            hold.SetResult();
            await asking;
            await Task.Delay(300);
            await conductor.CleanupsIdleAsync();
            Assert.AreEqual(Prompt, kimi.CallsFor(target).Last(), "not closed while the client has it open");
            Assert.IsTrue(conductor.Agents["kimi"].IsLive(target));
        }
        await conductor.CleanupsIdleAsync(); // gone without closing it: that lets go of it all the same

        Assert.AreEqual(Close, kimi.CallsFor(target).Last());
        Assert.IsFalse(conductor.Agents["kimi"].IsLive(target));
    }

    [TestMethod]
    public async Task AnotherServiceTurnQueued_TheSessionIsClosedAfterTheLastOne()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        var target = await LeftSessionAsync(conductor, "kimi", "Fix the flaky test", close: true);
        var held = 0;
        kimi.BeforePrompt = id => id == target && Interlocked.Increment(ref held) == 1 ? hold.Task : Task.CompletedTask;
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "claude", "Coordinate the release");
        var token = claude.Tokens[asker];

        var first = AskAsync(zed, token, target);
        await Until(() => kimi.CallsFor(target).Last() == Prompt, "the first question's turn");
        var second = AskAsync(zed, token, target);
        await Until(() => kimi.CallsFor(target).Last() == Opinion, "the second question, waiting for the turn");
        hold.SetResult();
        await Task.WhenAll(first, second);
        await Until(() => kimi.CallsFor(target).Last() == Close, "the close after the last turn");
        await Task.Delay(200);

        CollectionAssert.AreEqual(new[] { Resume, Opinion, Prompt, Opinion, Prompt, Close }, CallsFrom(kimi, target, Resume));
    }

    [TestMethod]
    public async Task Report_ToAParentThatIsNotLoadedOrNotRunning_WakesIt_AndClosesItAgain()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true, Running = false };
        var holdChildren = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(kimi.Adapter, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var kimiParent = await NewPromptedAsync(zed, "kimi", "Coordinate the release");
        var claudeParent = await NewPromptedAsync(zed, "claude", "Plan the docs");
        string? kimiChild = null, claudeChild = null;
        kimi.BeforePrompt = id => id == claudeChild ? holdChildren.Task : Task.CompletedTask;
        claude.BeforePrompt = id => id == kimiChild ? holdChildren.Task : Task.CompletedTask;
        kimiChild = (string)(await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject
        {
            ["agent"] = "claude", ["prompt"] = "Write the notes", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Tokens[kimiParent] },
        }))!["sessionId"]!;
        claudeChild = (string)(await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject
        {
            ["agent"] = "kimi", ["prompt"] = "Fix the flaky test", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = claude.Tokens[claudeParent] },
        }))!["sessionId"]!;
        await CloseAsync(zed, kimiParent);   // unloaded in Kimi
        await CloseAsync(zed, claudeParent); // Claude's process gone

        holdChildren.SetResult();
        await conductor.BackgroundTurnsIdleAsync();
        await Until(() => kimi.CallsFor(kimiParent).Last() == Close && claude.CallsFor(claudeParent).Last() == Close, "both parents closed again");

        CollectionAssert.AreEqual(new[] { Resume, Note, Prompt, Close }, CallsFrom(kimi, kimiParent, Resume));
        CollectionAssert.AreEqual(new[] { Resume, Note, Prompt, Close }, CallsFrom(claude, claudeParent, Resume));
        StringAssert.StartsWith(claude.Prompts.Last(p => p.SessionId == claudeParent).Text, $"[Report · from session_id {claudeChild} ");
    }

    // ---------------------------------------------------------------- a real failure to start

    [TestMethod]
    public async Task AskAgent_ASessionThatCannotBeStarted_FailsWithWhatToDoInstead_AndIsClosedAgain()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true, Running = false };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        var unresumable = await LeftSessionAsync(conductor, "kimi", "Fix the flaky test", close: true);
        var unstartable = await LeftSessionAsync(conductor, "claude", "Write the notes", close: false);
        kimi.BeforeResume = id => id == unresumable ? throw new JsonRpcException(JsonRpcException.InternalError, "Kimi lost the session") : Task.CompletedTask;
        claude.BeforePrompt = id => id == unstartable ? throw new JsonRpcException(JsonRpcException.InternalError, "Claude did not start: a login screen") : Task.CompletedTask;
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var asker = await NewPromptedAsync(zed, "kimi", "Coordinate the release");
        const string Hint = " Ask another session from list_agents, or ask the user to open that session and check it.";

        var notResumed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => AskAsync(zed, kimi.Tokens[asker], unresumable));
        Assert.AreEqual($"session_id {unresumable} (kimi, \"Fix the flaky test\") could not be started to answer: Kimi lost the session." + Hint, notResumed.Message);
        var notStarted = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => AskAsync(zed, kimi.Tokens[asker], unstartable));
        Assert.AreEqual($"session_id {unstartable} (claude, \"Write the notes\") could not be started to answer: Claude did not start: a login screen." + Hint, notStarted.Message);

        await Until(() => claude.CallsFor(unstartable).Last() == Close, "the failed session closed again");
        Assert.IsFalse(conductor.Agents["kimi"].IsLive(unresumable));
    }
}
