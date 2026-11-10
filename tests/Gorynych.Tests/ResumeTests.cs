using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The service's side of the resumable link: positions in the journal, <c>session/resume</c> after
/// one, the turn state, the mark of a prompt's end, and the restart (docs/service-protocol.md,
/// "The resumable link").
/// </summary>
[TestClass]
public sealed class ResumeTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    static JsonObject Chunk(string text) => new()
    {
        ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text }, ["messageId"] = "m1",
    };

    [TestMethod]
    public void Positions_GrowWithEveryEntry_SurviveAReload_AndAReplacementGoesOnPastThem()
    {
        var path = Path.Combine(TestDirs.New(), SessionJournal.FileNameFor("s1"));
        var journal = new SessionJournal(path, 100);
        var first = Chunk("a");
        journal.AppendMany([first, Chunk("b")]);
        Assert.AreEqual(1, SessionJournal.PositionOf(first), "the entry passed in gets its position, for what is sent after it");
        Assert.AreEqual(2, journal.LastPosition);

        var again = new SessionJournal(path, 100);
        Assert.AreEqual(2, again.LastPosition);
        again.Append(Chunk("c"));
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, new SessionJournal(path, 100).ReadAll().Select(SessionJournal.PositionOf).ToArray());

        again.Replace([Chunk("x"), Chunk("y")], DateTimeOffset.UtcNow, mark: null, trimmed: false);
        CollectionAssert.AreEqual(new long[] { 4, 5 }, again.ReadAll().Select(SessionJournal.PositionOf).ToArray(),
            "a client gets a replacement as new entries, so they come after anything it has");
        Assert.AreEqual(5, new SessionJournal(path, 100).LastPosition);
    }

    [TestMethod]
    public void ReadAfter_GivesWhatFollowsAPosition_ButNoListOfCommands()
    {
        var path = Path.Combine(TestDirs.New(), SessionJournal.FileNameFor("s1"));
        var list = new JsonObject { ["sessionUpdate"] = "available_commands_update", ["availableCommands"] = new JsonArray() };
        File.WriteAllText(path, string.Concat(new[] { Chunk("a"), list, Chunk("b"), Chunk("c") }.Select(e => e.ToJsonString() + "\n")));
        var journal = new SessionJournal(path, 100);

        CollectionAssert.AreEqual(new[] { "b", "c" }, journal.ReadAfter(1).Select(e => (string?)e["content"]?["text"]).ToArray());
        Assert.AreEqual(3, journal.ReadAfter(0).Count, "from the start, but the list");
        Assert.AreEqual(0, journal.ReadAfter(4).Count);
        Assert.AreEqual(0, journal.ReadAfter(99).Count, "past the end: nothing");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task EveryUpdateCarriesItsPosition_AndAResumeAfterOneSendsWhatFollows_AndNothingElse()
    {
        var agent = new TurnAgent("turns");
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent);
        await using var a = await TestClient.ConnectAsync(conductor, "a");
        var sid = (await a.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath() })).SessionId;
        await Prompt(a, sid, "one");

        var positions = a.Snapshot().Select(SessionJournal.PositionOf).ToArray();
        Assert.IsTrue(positions.All(p => p > 0), "every update the client got is journaled, with its position");
        CollectionAssert.AreEqual(positions.Order().ToArray(), positions, "growing");
        Assert.IsFalse(a.Kinds().Contains("user_message_chunk"), "a client never gets its own prompt");
        var started = a.Snapshot().Single(u => (string?)u["content"]?["text"] == "started:one ");

        await using var b = await TestClient.ConnectAsync(conductor, "b");
        var resumed = await Resume(b, sid, After(SessionJournal.PositionOf(started)));
        CollectionAssert.AreEqual(new[] { "done:one " }, b.Snapshot().Select(u => (string?)u["content"]?["text"]).ToArray(), "what followed it, and only that");
        Assert.AreEqual(SessionJournal.FormatPosition(positions[^1]), (string?)resumed["_meta"]![GorynychProtocol.MetaPosition]);
        Assert.AreEqual("idle", (string?)resumed["_meta"]![GorynychProtocol.MetaTurn]);
        Assert.AreEqual(0, resumed["_meta"]![GorynychProtocol.MetaPrompts]!.AsArray().Count);

        await using var c = await TestClient.ConnectAsync(conductor, "c");
        await Resume(c, sid, new JsonObject { ["type"] = GorynychProtocol.ReplayFromStart });
        CollectionAssert.AreEqual(new[] { "user_message_chunk", "session_info_update", "agent_message_chunk", "agent_message_chunk" }, c.Kinds(),
            "from the start: the whole journal, the prompt with it");

        await using var d = await TestClient.ConnectAsync(conductor, "d");
        var plain = await d.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = sid, ["cwd"] = Path.GetTempPath() }).WaitAsync(Wait);
        Assert.AreEqual(0, d.Snapshot().Length, "a plain resume replays nothing");
        Assert.AreEqual(SessionJournal.FormatPosition(positions[^1]), (string?)plain!["_meta"]![GorynychProtocol.MetaPosition], "and says where the client is");

        await Prompt(a, sid, "two");
        await LinkHarness.Until(() => d.Snapshot().Length >= 3, "the live updates");
        Assert.IsTrue(d.Snapshot().All(u => SessionJournal.PositionOf(u) > positions[^1]), "what comes live follows the position it was given");

        foreach (var bad in new[]
                 {
                     new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = "x" },
                     new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = "-1" },
                     new JsonObject { ["type"] = "bogus" },
                 })
        {
            var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Resume(d, sid, bad));
            Assert.AreEqual(JsonRpcException.InvalidParams, e.Code, bad.ToJsonString());
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptWhoseClientWent_LeavesTheMarkOfItsEnd_AndAResumeMeanwhileSaysItIsInProgress()
    {
        var agent = new TurnAgent("turns") { Hold = true };
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent);
        var a = await TestClient.ConnectAsync(conductor, "a");
        var sid = (await a.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath() })).SessionId;
        _ = a.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, PromptParams(sid, "one", promptId: "p1"));
        await LinkHarness.Until(() => a.Text().Contains("started:one"), "the turn");
        await a.DisposeAsync(); // the client goes in the middle of the turn

        await using var b = await TestClient.ConnectAsync(conductor, "b");
        var resumed = await Resume(b, sid, new JsonObject { ["type"] = GorynychProtocol.ReplayFromStart });
        Assert.AreEqual("running", (string?)resumed["_meta"]![GorynychProtocol.MetaTurn]);
        CollectionAssert.AreEqual(new[] { "p1" }, resumed["_meta"]![GorynychProtocol.MetaPrompts]!.AsArray().Select(p => (string?)p).ToArray());
        Assert.AreEqual("p1", (string?)b.Snapshot().First()["_meta"]![GorynychProtocol.MetaPromptId], "the prompt's text carries its ID");

        agent.Release();
        await LinkHarness.Until(() => b.Snapshot().Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended"), "the mark");
        var mark = b.Snapshot().Single(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended");
        Assert.AreEqual("session_info_update", (string?)mark["sessionUpdate"]);
        Assert.AreEqual("p1", (string?)mark["_meta"]![GorynychProtocol.MetaPromptId]);
        Assert.AreEqual("end_turn", (string?)mark["_meta"]![GorynychProtocol.MetaStopReason]);
        Assert.IsTrue(conductor.Feed(sid).Journal.ReadAll().Any(e => (string?)e["_meta"]?[GorynychProtocol.MetaPromptId] == "p1" && e["sessionUpdate"]!.ToString() == "session_info_update"),
            "journaled: a client that comes back after the end finds it");
        await LinkHarness.Until(() => !conductor.Feed(sid).TurnRunning, "the end");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptWithAnId_LeavesTheMarkOfItsEnd_BeforeItsAnswer_EvenWithItsClientThere_AndOneWithoutAnIdNone()
    {
        var agent = new TurnAgent("turns");
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent);
        await using var a = await TestClient.ConnectAsync(conductor, "a");
        var sid = (await a.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath() })).SessionId;
        await Prompt(a, sid, "plain");
        Assert.IsFalse(conductor.Feed(sid).Journal.ReadAll().Any(e => (string?)e["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended"), "a plain client's answer says how it ended");

        var result = await a.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, PromptParams(sid, "one", promptId: "p1")).WaitAsync(Wait);

        Assert.AreEqual("end_turn", (string?)result!["stopReason"]);
        // A connection may be gone before the service notices, and its answer with it: the mark says how it ended.
        var mark = a.Snapshot().Last();
        Assert.AreEqual("p1", (string?)mark["_meta"]?[GorynychProtocol.MetaPromptId], "the client got it before the answer");
        Assert.AreEqual("end_turn", (string?)mark["_meta"]?[GorynychProtocol.MetaStopReason]);
        Assert.AreEqual(1, conductor.Feed(sid).Journal.ReadAll().Count(e => (string?)e["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARestart_TellsTheClients_RefusesNewTurns_WaitsForTheRunningOne_AndThenAsksToExit()
    {
        var agent = new TurnAgent("turns") { Hold = true };
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent);
        await using var a = await TestClient.ConnectAsync(conductor, "a");
        var sid = (await a.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath() })).SessionId;
        var other = (await a.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath() })).SessionId;
        var running = a.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, PromptParams(sid, "one"));
        await LinkHarness.Until(() => a.Text().Contains("started:one"), "the turn");

        await using var admin = await TestClient.ConnectAsync(conductor, "admin");
        var restart = admin.Rpc.SendRequestAsync(GorynychProtocol.MethodRestart, new JsonObject { ["wait"] = true, ["timeoutSeconds"] = 30 });
        await LinkHarness.Until(() => conductor.Draining, "the drain");
        await LinkHarness.Until(() => { lock (a.Notices) return a.Notices.Contains(GorynychProtocol.MethodRestarting); }, "the notice");

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => a.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, PromptParams(other, "two")));
        Assert.IsTrue((bool?)refused.ErrorData?[GorynychProtocol.MetaRestarting] == true, "not carried out, so a link sends it to the next service");
        await using (var late = await TestClient.ConnectAsync(conductor, "late"))
            await LinkHarness.Until(() => { lock (late.Notices) return late.Notices.Contains(GorynychProtocol.MethodRestarting); }, "the notice to a client that comes during the drain");
        var status = await admin.Rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject()).WaitAsync(Wait);
        Assert.IsTrue((bool)status!["restarting"]!);

        await Task.Delay(300);
        Assert.IsFalse(restart.IsCompleted, "it waits for the running turn");
        Assert.IsFalse(conductor.ShutdownRequested.IsCancellationRequested);
        agent.Release();
        Assert.AreEqual("end_turn", (string?)(await running.WaitAsync(Wait))!["stopReason"], "the turn ran to its end");
        var done = await restart.WaitAsync(Wait);
        Assert.AreEqual(0, (int)done!["cancelledTurns"]!);
        Assert.AreEqual(Environment.ProcessId, (int)done["processId"]!);
        await LinkHarness.Until(() => conductor.ShutdownRequested.IsCancellationRequested, "the exit");
        CollectionAssert.AreEqual(new[] { "one" }, agent.Prompts.ToArray(), "the refused prompt never reached the agent");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARestart_WaitsForAPromptStillOpeningItsSession()
    {
        var data = TestDirs.New();
        string sid;
        await using (var first = TestDirs.Conductor(data, new TurnAgent("turns")))
        await using (var a = await TestClient.ConnectAsync(first, "a"))
        {
            sid = (await a.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath() })).SessionId;
            await Prompt(a, sid, "one");
        }
        // The next service has to open the session in its agent first, which takes a while.
        var agent = new TurnAgent("turns") { ResumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var conductor = TestDirs.Conductor(data, agent);
        await using var b = await TestClient.ConnectAsync(conductor, "b");
        var prompt = b.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, PromptParams(sid, "two"));
        await LinkHarness.Until(() => agent.Resumes.Count == 1, "the session to be opening");
        Assert.IsTrue(conductor.Feed(sid).TurnRunning, "a prompt on its way to its turn counts");

        var restart = b.Rpc.SendRequestAsync(GorynychProtocol.MethodRestart, new JsonObject { ["wait"] = true, ["timeoutSeconds"] = 30 });
        await Task.Delay(300);
        Assert.IsFalse(restart.IsCompleted, "it waits for that prompt too");

        agent.ResumeGate.SetResult();
        Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual(0, (int)(await restart.WaitAsync(Wait))!["cancelledTurns"]!);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(false, 30.0, DisplayName = "no wait")]
    [DataRow(true, 0.3, DisplayName = "the wait runs out")]
    public async Task ARestartThatDoesNotWaitForTheTurn_CancelsIt(bool wait, double seconds)
    {
        var agent = new TurnAgent("turns") { Hold = true };
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent);
        await using var a = await TestClient.ConnectAsync(conductor, "a");
        var sid = (await a.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath() })).SessionId;
        var running = a.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, PromptParams(sid, "one"));
        await LinkHarness.Until(() => a.Text().Contains("started:one"), "the turn");

        var done = await a.Rpc.SendRequestAsync(GorynychProtocol.MethodRestart, new JsonObject { ["wait"] = wait, ["timeoutSeconds"] = seconds }).WaitAsync(Wait);

        Assert.AreEqual(1, (int)done!["cancelledTurns"]!);
        Assert.AreEqual("cancelled", (string?)(await running.WaitAsync(Wait))!["stopReason"], "the agent was asked to cancel, and its turn ended so");
        await LinkHarness.Until(() => conductor.ShutdownRequested.IsCancellationRequested, "the exit");
    }

    static JsonObject After(long position) => new() { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = SessionJournal.FormatPosition(position) };

    static async Task<JsonObject> Resume(TestClient client, string sid, JsonObject replayFrom) =>
        (JsonObject)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject
        {
            ["sessionId"] = sid, ["cwd"] = Path.GetTempPath(), ["_meta"] = new JsonObject { [GorynychProtocol.MetaReplayFrom] = replayFrom },
        }).WaitAsync(Wait))!;

    static JsonObject PromptParams(string sid, string text, string? promptId = null) => new()
    {
        ["sessionId"] = sid,
        ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["_meta"] = promptId is null ? null : new JsonObject { [GorynychProtocol.MetaPromptId] = promptId },
    };

    static async Task Prompt(TestClient client, string sid, string text)
    {
        var result = await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, PromptParams(sid, text)).WaitAsync(Wait);
        Assert.AreEqual("end_turn", (string?)result!["stopReason"]);
    }
}
