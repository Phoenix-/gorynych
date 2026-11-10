using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Updating adapters on the fly (docs/service-protocol.md, "Updating adapters"): after a restart onto
/// another build, or with other adapter options, new sessions open in a host of the current
/// generation, an idle session moves there at once, a busy one after its turn, and a stale host exits
/// once it has no session left; no turn breaks, and the client sees no break.
/// </summary>
[TestClass]
public sealed class HostUpdateTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>A service with hosts of the build "build-1", a fake agent that closes sessions, and Zed on the link.</summary>
    sealed class Rig : IAsyncDisposable
    {
        public readonly string Data = TestDirs.New();
        public readonly TurnAgent Agent = new("turns") { Closes = true, Tag = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..3]).ToLowerInvariant() + "-" };
        public readonly InProcessHosts Hosts;
        public readonly LinkHarness Zed;
        public Conductor Service;
        readonly Dictionary<string, TaskCompletionSource> held = new(StringComparer.Ordinal);

        public Rig(bool perSession = false)
        {
            Hosts = new InProcessHosts(_ => Agent) { Generations = true };
            if (perSession) Hosts.SessionHosts.Add("turns");
            // A prompt whose text starts with "long" runs until Release(text).
            Agent.During = async (_, sid, ct) =>
            {
                TaskCompletionSource? gate;
                lock (held) held.TryGetValue(sid, out gate);
                if (gate is not null) await gate.Task.WaitAsync(ct);
            };
            Service = Hosts.Conductor(Data, Agent);
            Hosts.Service = Service;
            Zed = new LinkHarness(Service);
        }

        public async Task<string> SessionAsync() => await Zed.NewSessionAsync().WaitAsync(Wait);

        /// <summary>The next prompt in the session holds its turn until <see cref="Release"/>.</summary>
        public void Hold(string sid)
        {
            lock (held) held[sid] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release(string sid)
        {
            lock (held)
                if (held.Remove(sid, out var gate)) gate.SetResult();
        }

        /// <summary>The update: the hosts started from now on run <paramref name="build"/>, and the service restarts onto it.</summary>
        public async Task UpdateAsync(string build, Func<string, IReadOnlyList<string>>? options = null)
        {
            Hosts.Build = build;
            if (options is not null) Hosts.Options = options;
            await HostTests.RestartAsync(Service);
            await HostTests.GoneAsync(Service, Zed, Hosts);
            Service = HostTests.Next(Data, Hosts, Zed, Agent);
        }

        public AgentHost Turns => Service.Agents["turns"];

        public InProcessHosts.Running HostOf(string build) => Hosts.Of("turns").Single(h => h.Build == build && !h.Slot.Contains('.'));

        /// <summary>The build of the host the session was opened in last: each host runs one instance of the agent, in the order they started.</summary>
        public string BuildOf(string sid) => Hosts.Of("turns")[Agent.InstanceOf(sid)].Build;

        public async ValueTask DisposeAsync()
        {
            foreach (var sid in held.Keys.ToList()) Release(sid);
            await Zed.DisposeAsync();
            await Service.DisposeAsync();
        }
    }

    static async Task Prompt(Rig rig, string sid, string text) =>
        Assert.AreEqual("end_turn", (string?)(await rig.Zed.PromptAsync(sid, text).WaitAsync(Wait))!["stopReason"], text);

    static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnUpdate_MovesAnIdleSessionAtOnce_ABusyOneAfterItsTurn_AndNewSessionsOpenInTheNewHost()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var a = await rig.SessionAsync();
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");
        rig.Hold(a);
        var a1 = rig.Zed.PromptAsync(a, "a1");
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:a1"), "A's long turn");

        await rig.UpdateAsync("build-2");
        var old = rig.HostOf("build-1");
        // B is idle: it leaves the old host at once. A runs a turn there: it stays.
        await LinkHarness.Until(() => rig.Agent.EventsOf(b).Contains("closed:0:" + b), "B to leave the old host");
        Assert.IsFalse(old.Run.IsCompleted, "the old host still runs A's turn");
        Assert.AreEqual(0, rig.Agent.InstanceOf(a));

        // A new session opens in a host of the new build, and so does B at its next use.
        var c = await rig.SessionAsync();
        Assert.AreEqual("build-2", rig.BuildOf(c), "a new session opens in the new host");
        Assert.AreEqual(1, rig.Hosts.Of("turns").Count(h => h.Build == "build-2"), "one host of the new build");
        await Prompt(rig, b, "b2");
        Assert.AreEqual("build-2", rig.BuildOf(b));
        Assert.AreEqual(1, rig.Agent.Events.Count(e => e == "prompt:0:" + b), "no new turn in the old host: b1 ran there before the update, b2 in the new one");

        // A prompt that waits for A's turn; A's turn ends whole in the old host; then A moves, the
        // prompt runs in the new host, and the old host, empty, exits.
        var a2 = rig.Zed.PromptAsync(a, "a2");
        await Task.Delay(200);
        rig.Release(a);
        Assert.AreEqual("end_turn", (string?)(await a1.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("end_turn", (string?)(await a2.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("build-2", rig.BuildOf(a), "the prompt that waited behind the turn ran in the new host");
        Assert.AreEqual(0, await old.Run.WaitAsync(Wait), "the old host exits once it has no session");
        CollectionAssert.Contains(rig.Agent.EventsOf(a), "closed:0:" + a);
        Assert.IsTrue(rig.Turns.Links.All(l => l.Current), "only hosts of the new build are left");

        // Nothing doubled or lost on the way, for Zed and in the journals.
        var text = rig.Zed.Text();
        foreach (var part in new[] { "started:a1 ", "done:a1 ", "started:b2 ", "done:b2 ", "started:a2 ", "done:a2 " }) Assert.AreEqual(1, Count(text, part), part);
        Assert.AreEqual("started:a1 done:a1 started:a2 done:a2 ", HostTests.JournalText(rig.Service, a));
        Assert.AreEqual("started:b1 done:b1 started:b2 done:b2 ", HostTests.JournalText(rig.Service, b));
        Assert.AreEqual(2, HostTests.EndMarks(rig.Service, a));
        HostTests.NoBreakSeen(rig.Zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptThatComesWhileItsSessionMoves_WaitsForTheOldHostToLetGo_AndRunsInTheNew()
    {
        // As Codex: the old app-server answers the close once it let go of the thread's writer lock.
        await using var rig = new Rig();
        rig.Agent.CloseDelay = TimeSpan.FromMilliseconds(800);
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");

        await rig.UpdateAsync("build-2");
        await LinkHarness.Until(() => rig.Agent.EventsOf(b).Contains("close:0:" + b), "B's move to begin");
        var b2 = rig.Zed.PromptAsync(b, "b2"); // while the old host closes it
        Assert.AreEqual("end_turn", (string?)(await b2.WaitAsync(Wait))!["stopReason"]);

        var events = rig.Agent.EventsOf(b).SkipWhile(e => e != "close:0:" + b).ToArray();
        var newer = rig.Agent.InstanceOf(b);
        Assert.AreNotEqual(0, newer);
        CollectionAssert.AreEqual(new[] { "close:0:" + b, "closed:0:" + b, $"resume:{newer}:{b}", $"prompt:{newer}:{b}" }, events,
            "the old host let go of it before the new one opened it, and the prompt ran in the new one");
        Assert.AreEqual("started:b1 done:b1 started:b2 done:b2 ", HostTests.JournalText(rig.Service, b));
        HostTests.NoBreakSeen(rig.Zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASessionWhoseAgentAsksSomething_StaysInTheOldHost_UntilItIsAnswered()
    {
        await using var rig = new Rig();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Zed.Approve = async (_, _) =>
        {
            await answer.Task;
            return "allow";
        };
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");
        // The agent asks outside a turn (as Claude's terminal can), and the question waits across the update.
        var asked = rig.Agent.AgentOf(b).Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, new JsonObject
        {
            ["sessionId"] = b, ["toolCall"] = new JsonObject { ["toolCallId"] = "t1", ["title"] = "Write a file" },
            ["options"] = new JsonArray(new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" }),
        });
        await LinkHarness.Until(() => { lock (rig.Zed.Approvals) return rig.Zed.Approvals.Count == 1; }, "the question");

        await rig.UpdateAsync("build-2");
        await LinkHarness.Until(() => WaitingFor(rig, b) is not null, "the old host to be taken back with B");
        await Task.Delay(500);
        CollectionAssert.DoesNotContain(rig.Agent.EventsOf(b), "close:0:" + b, "not moved while its agent waits for an answer");
        StringAssert.Contains(WaitingFor(rig, b), "asks something");

        answer.SetResult();
        Assert.AreEqual("allow", (string?)(await asked.WaitAsync(Wait))!["outcome"]!["optionId"], "the answer reached the agent in the old host");
        await LinkHarness.Until(() => rig.Agent.EventsOf(b).Contains("closed:0:" + b), "B to move once answered");
        Assert.AreEqual(0, await rig.HostOf("build-1").Run.WaitAsync(Wait));
    }

    /// <summary>What <c>_gorynych/status</c> says keeps the session in a stale host, or null when no stale host has it.</summary>
    static string? WaitingFor(Rig rig, string sid) =>
        (rig.Service.Status()["agents"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(a => (a["hosts"] as JsonArray ?? []).OfType<JsonObject>())
            .Select(h => (string?)(h["waiting"] as JsonObject)?[sid]).FirstOrDefault(w => w is not null);

    /// <summary>A permission request of the agent's outside a turn, as Claude's terminal can ask one.</summary>
    static JsonObject Question(string sid) => new()
    {
        ["sessionId"] = sid, ["toolCall"] = new JsonObject { ["toolCallId"] = "t1", ["title"] = "Write a file" },
        ["options"] = new JsonArray(new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" }),
    };

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptWhileItsAgentAsksSomethingInAStaleHost_WaitsForTheAnswer_ThenRunsInTheNewHost()
    {
        // A stale host takes no new turn: the prompt waits for what holds the session there, then the session moves.
        await using var rig = new Rig();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Zed.Approve = async (_, _) =>
        {
            await answer.Task;
            return "allow";
        };
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");
        var asked = rig.Agent.AgentOf(b).Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, Question(b));
        await LinkHarness.Until(() => { lock (rig.Zed.Approvals) return rig.Zed.Approvals.Count == 1; }, "the question");

        await rig.UpdateAsync("build-2");
        await LinkHarness.Until(() => rig.Hosts.Log().Contains("1 questions asked again"), "the old host to ask again");
        var b2 = rig.Zed.PromptAsync(b, "b2");
        await Task.Delay(500);
        Assert.IsFalse(b2.IsCompleted, "the prompt waits while the agent asks");
        Assert.AreEqual(1, rig.Agent.Events.Count(e => e == "prompt:0:" + b), "and went to no host yet");

        answer.SetResult();
        await asked.WaitAsync(Wait);
        Assert.AreEqual("end_turn", (string?)(await b2.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("build-2", rig.BuildOf(b), "it ran in the new host");
        Assert.AreEqual(0, await rig.HostOf("build-1").Run.WaitAsync(Wait));
        Assert.AreEqual("started:b1 done:b1 started:b2 done:b2 ", HostTests.JournalText(rig.Service, b));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AQuestionThatArrivesWhileAMoveWaitsForTheSession_KeepsItInTheOldHost()
    {
        // The move waits for the session's gate first; the question comes behind it. It counts from
        // the moment it was read, not from when its handler gets the gate, so the move sees it.
        await using var rig = new Rig();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Zed.Approve = async (_, _) =>
        {
            await answer.Task;
            return "allow";
        };
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");
        rig.Hosts.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.UpdateAsync("build-2");
        var feed = rig.Service.Feed(b);
        await feed.LifeGate.WaitAsync().WaitAsync(Wait);
        rig.Hosts.ConnectGate.SetResult();
        await LinkHarness.Until(() => rig.Turns.Links.Any(l => !l.Current), "the old host to come back");
        await Task.Delay(300); // its idle sessions' moves wait for their gates now

        var asked = rig.Agent.AgentOf(b).Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, Question(b));
        await LinkHarness.Until(() => feed.Asking == 1, "the question to count as it arrives");
        feed.LifeGate.Release();
        await Task.Delay(500);
        CollectionAssert.DoesNotContain(rig.Agent.EventsOf(b), "close:0:" + b, "not moved while its agent waits for an answer");

        answer.SetResult();
        await asked.WaitAsync(Wait);
        await LinkHarness.Until(() => rig.Agent.EventsOf(b).Contains("closed:0:" + b), "B to move once answered");
        Assert.AreEqual(0, feed.Asking);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHostThatGoesBeforeItAsksItsQuestionsAgain_LeavesNoQuestionCounted()
    {
        // The old host has a question open, and goes after it told its state but before the next
        // service read the question it asks again: nothing counts it, so B is free to move later on.
        await using var rig = new Rig();
        rig.Zed.Approve = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "allow";
        };
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");
        _ = rig.Agent.AgentOf(b).Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, Question(b));
        await LinkHarness.Until(() => { lock (rig.Zed.Approvals) return rig.Zed.Approvals.Count == 1; }, "the question");

        rig.Hosts.HoldBack = line => line.Contains(AcpMethods.Client.SessionRequestPermission, StringComparison.Ordinal);
        await rig.UpdateAsync("build-2");
        var old = rig.HostOf("build-1");
        await LinkHarness.Until(() => rig.Hosts.Log().Contains("1 questions asked again"), "the old host to ask again");
        await old.Agent.DisposeAsync(); // it dies, with its agent, before the question gets through
        rig.Hosts.WireOf(old).Break();
        await LinkHarness.Until(() => rig.Turns.Links.All(l => l.Current), "the old host to be gone");
        rig.Hosts.HoldBack = null;

        Assert.AreEqual(0, rig.Service.Feed(b).Asking, "no question of a host that went counts");
        await Prompt(rig, b, "b2");
        Assert.AreEqual("build-2", rig.BuildOf(b));
        var then = rig.Agent.InstanceOf(b);
        await rig.UpdateAsync("build-3");
        await LinkHarness.Until(() => rig.Agent.EventsOf(b).Contains($"closed:{then}:{b}"), "B to move on the next update too");
        await Prompt(rig, b, "b3");
        Assert.AreEqual("build-3", rig.BuildOf(b));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ADraft_MovesAtOnce_AndOpensAfreshInTheNewHost()
    {
        // Kimi and DSH keep a draft, and the Claude adapter opens one afresh under its ID: it moves as any idle session.
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var d = await rig.SessionAsync();
        await rig.UpdateAsync("build-2");
        Assert.AreEqual(0, await rig.HostOf("build-1").Run.WaitAsync(Wait), "the old host goes once its draft moved");
        CollectionAssert.Contains(rig.Agent.EventsOf(d), "closed:0:" + d);
        await Prompt(rig, d, "d1");
        Assert.AreEqual("build-2", rig.BuildOf(d));
        var resumed = rig.Agent.Resumes.Last(r => (string?)r["sessionId"] == d);
        Assert.IsTrue((bool?)resumed["_meta"]?[GorynychProtocol.MetaDraft] == true, "opened as a draft, though its first prompt had ended it as one");
        Assert.AreEqual("started:d1 done:d1 ", HostTests.JournalText(rig.Service, d));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ADraftItsAgentCannotOpenElsewhere_StaysUntilItsFirstTurn_AndStatusSaysSo()
    {
        // Codex keeps nothing of a thread before its first turn: only the app-server that made it has it.
        await using var rig = new Rig();
        rig.Agent.DraftsNotDurable = true;
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var d = await rig.SessionAsync();
        await rig.UpdateAsync("build-2");
        await Task.Delay(500);
        CollectionAssert.DoesNotContain(rig.Agent.EventsOf(d), "close:0:" + d, "the draft stays");
        StringAssert.Contains(WaitingFor(rig, d), "draft");
        await Prompt(rig, d, "d1");
        Assert.AreEqual("build-1", rig.BuildOf(d), "its first turn runs where it was made");
        await LinkHarness.Until(() => rig.Agent.EventsOf(d).Contains("closed:0:" + d), "it moves once it had a turn");
        Assert.AreEqual(0, await rig.HostOf("build-1").Run.WaitAsync(Wait));
        await Prompt(rig, d, "d2");
        Assert.AreEqual("build-2", rig.BuildOf(d));
        Assert.IsNull(WaitingFor(rig, d));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AFailedClose_LeavesTheSessionToItsOldHost_UntilACloseSucceeds()
    {
        // As Codex's close when its app-server did not unload the thread in time: its writer lock may
        // still be held, so the session must not open in another host while the old one runs.
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var a = await rig.SessionAsync();
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");
        rig.Hold(a);
        var a1 = rig.Zed.PromptAsync(a, "a1"); // keeps the old host running
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:a1"), "A's long turn");
        rig.Agent.CloseFails = true;

        await rig.UpdateAsync("build-2");
        await LinkHarness.Until(() => rig.Agent.EventsOf(b).Contains("close-failed:0:" + b), "B's close to fail");
        await LinkHarness.Until(() => WaitingFor(rig, b)?.Contains("close failed") == true, "status to say why B is still there");
        await Prompt(rig, b, "b2");
        Assert.AreEqual("build-1", rig.BuildOf(b), "it opened in its old host again, and took its turn there");
        Assert.AreEqual(1, rig.Hosts.Of("turns").Count(h => h.Build == "build-1"));
        Assert.IsFalse(rig.Agent.EventsOf(b).Any(e => e.StartsWith("resume:", StringComparison.Ordinal) && !e.StartsWith("resume:0:", StringComparison.Ordinal)),
            "never opened in a host of the new build while the old one may hold it: " + string.Join(" ", rig.Agent.EventsOf(b)));

        rig.Agent.CloseFails = false;
        rig.Release(a);
        Assert.AreEqual("end_turn", (string?)(await a1.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual(0, await rig.HostOf("build-1").Run.WaitAsync(Wait), "once a close succeeds, the old host empties and exits");
        await Prompt(rig, b, "b3");
        Assert.AreEqual("build-2", rig.BuildOf(b));
        Assert.AreEqual("started:b1 done:b1 started:b2 done:b2 started:b3 done:b3 ", HostTests.JournalText(rig.Service, b));
        HostTests.NoBreakSeen(rig.Zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AStaleHostWhoseAgentCannotClose_GoesAsAWhole_AndAPromptInItsStop_RunsInTheNewHost()
    {
        await using var rig = new Rig();
        rig.Agent.Closes = false;
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var a = await rig.SessionAsync();
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");
        rig.Hold(a);
        var a1 = rig.Zed.PromptAsync(a, "a1");
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:a1"), "A's long turn");

        await rig.UpdateAsync("build-2");
        var old = rig.HostOf("build-1");
        await LinkHarness.Until(() => rig.Turns.Links.Any(l => !l.Current), "the old host to come back");
        await Task.Delay(300);
        Assert.IsFalse(old.Run.IsCompleted, "A's turn keeps the whole host");
        StringAssert.Contains(WaitingFor(rig, b), "cannot close");

        // The stop, once A's turn ends, holds every session's gates while its host_stop is on its way
        // (the old host reads nothing meanwhile); a prompt for B then waits, and runs in the new host.
        rig.Hosts.WireOf(old).PauseLink();
        rig.Release(a);
        Assert.AreEqual("end_turn", (string?)(await a1.WaitAsync(Wait))!["stopReason"]);
        var oldLink = rig.Turns.Links.Single(l => !l.Current);
        await LinkHarness.Until(() => oldLink.Stopping, "the stop of the old host");
        var b2 = rig.Zed.PromptAsync(b, "b2");
        Assert.AreEqual("end_turn", (string?)(await b2.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("build-2", rig.BuildOf(b), "the prompt that came during the stop ran in the new host");
        Assert.AreEqual(1, rig.Agent.Events.Count(e => e == "prompt:0:" + b), "no turn of B's in the old host after the update");
        rig.Hosts.WireOf(old).Go();
        Assert.AreEqual(0, await old.Run.WaitAsync(Wait));
        await Prompt(rig, a, "a2");
        Assert.AreEqual("build-2", rig.BuildOf(a));
        Assert.AreEqual("started:a1 done:a1 started:a2 done:a2 ", HostTests.JournalText(rig.Service, a));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHostThatCannotStart_HoldsBackOnlyHostsOfItsKind()
    {
        // Claude's hosts for a session fail to start: the next one fails at once with the same words,
        // but the adapter-wide host, which lists the sessions, starts as before.
        await using var rig = new Rig(perSession: true);
        rig.Hosts.FailLaunch = slot => slot.Contains('.');
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var first = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Zed.NewSessionAsync().WaitAsync(Wait));
        StringAssert.Contains(first.Message, "exited before it connected");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var again = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Zed.NewSessionAsync().WaitAsync(Wait));
        StringAssert.Contains(again.Message, "starts it again in");
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2), "the second waits for no host");

        await rig.Zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject()).WaitAsync(Wait); // on the adapter-wide host
        Assert.IsTrue(rig.Turns.Links.Any(l => !l.ForSession && l.Current), "the adapter-wide host started");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task OtherAdapterOptions_MakeTheHostsStale_AsAnUpdateDoes()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");

        await rig.UpdateAsync("build-1", _ => ["--claude-model", "opus"]); // the same build, another option
        Assert.AreEqual(0, await rig.HostOf("build-1").Run.WaitAsync(Wait), "the host with the old options goes once B moved");
        await Prompt(rig, b, "b2");
        var now = rig.Turns.Links.Single();
        Assert.IsTrue(now.Current);
        CollectionAssert.AreEqual(new[] { "--claude-model", "opus" }, now.Generation!.Options.ToArray());
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TwoUpdatesInARow_LeaveNoOldHost_OnceTheTurnsInThemEnd()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var a = await rig.SessionAsync();
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b0"); // no draft: a draft stays where it was made until its first turn
        rig.Hold(a);
        var a1 = rig.Zed.PromptAsync(a, "a1");
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:a1"), "A's long turn");

        await rig.UpdateAsync("build-2");
        rig.Hold(b);
        var b1 = rig.Zed.PromptAsync(b, "b1"); // moves B to the host of build-2 and runs there
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:b1"), "B's long turn");
        Assert.AreNotEqual(0, rig.Agent.InstanceOf(b));

        // Another update while the host of build-1 still runs A's turn and that of build-2 B's.
        await rig.UpdateAsync("build-3");
        var c = await rig.SessionAsync();
        Assert.AreEqual("build-3", rig.Hosts.Of("turns").Last(h => !h.Run.IsCompleted).Build);
        rig.Release(b);
        Assert.AreEqual("end_turn", (string?)(await b1.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual(0, await rig.HostOf("build-2").Run.WaitAsync(Wait), "the host of build-2 goes once B's turn ended and B moved");
        Assert.IsFalse(rig.HostOf("build-1").Run.IsCompleted, "A's turn still runs in the host of build-1");
        rig.Release(a);
        Assert.AreEqual("end_turn", (string?)(await a1.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual(0, await rig.HostOf("build-1").Run.WaitAsync(Wait));

        await Prompt(rig, a, "a2");
        await Prompt(rig, b, "b2");
        await Prompt(rig, c, "c1");
        foreach (var sid in new[] { a, b, c }) Assert.AreEqual("build-3", rig.BuildOf(sid), sid);
        Assert.IsTrue(rig.Turns.Links.All(l => l.Current && l.Generation!.Build == "build-3"));
        Assert.AreEqual(1, rig.Hosts.Alive("turns").Length);
        HostTests.NoBreakSeen(rig.Zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task WithAHostPerSession_EachSessionMovesAlone_AndEachOldHostExitsWithIt()
    {
        await using var rig = new Rig(perSession: true);
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var a = await rig.SessionAsync();
        var b = await rig.SessionAsync();
        Assert.AreNotEqual(rig.Agent.InstanceOf(a), rig.Agent.InstanceOf(b), "a host each");
        await Prompt(rig, b, "b1");
        rig.Hold(a);
        var a1 = rig.Zed.PromptAsync(a, "a1");
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:a1"), "A's long turn");

        await rig.UpdateAsync("build-2");
        var oldHosts = rig.Hosts.Of("turns").Where(h => h.Build == "build-1" && h.Slot.Contains('.')).ToArray();
        Assert.AreEqual(2, oldHosts.Length);
        // B's host goes as B moves; A's stays while A's turn runs.
        await LinkHarness.Until(() => oldHosts.Count(h => h.Run.IsCompleted) == 1, "B's old host to exit");
        var c = await rig.SessionAsync();
        Assert.AreEqual("build-2", rig.BuildOf(c), "C in a host of the new build, its own");
        rig.Release(a);
        Assert.AreEqual("end_turn", (string?)(await a1.WaitAsync(Wait))!["stopReason"]);
        await LinkHarness.Until(() => oldHosts.All(h => h.Run.IsCompleted), "A's old host to exit too");
        await Prompt(rig, a, "a2");
        await Prompt(rig, b, "b2");
        Assert.AreEqual("build-2", rig.BuildOf(a));
        Assert.AreEqual("build-2", rig.BuildOf(b));
        Assert.IsTrue(rig.Turns.Links.All(l => l.Current));
        Assert.AreEqual(3, rig.Turns.Links.Count(l => l.ForSession), "A, B and C, each in a host of its own");
        HostTests.NoBreakSeen(rig.Zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASessionOpenInAHostThatComesBackLate_IsWaitedFor_NotOpenedInASecond()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var b = await rig.SessionAsync();
        await Prompt(rig, b, "b1");
        rig.Hosts.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // the hosts come back late
        await rig.UpdateAsync("build-1"); // a restart onto the same build
        var b2 = rig.Zed.PromptAsync(b, "b2"); // its resume and prompt reach the next service before B's host is back
        await Task.Delay(800);
        Assert.IsFalse(b2.IsCompleted, "it waits for the host that has B open");
        Assert.AreEqual(1, rig.Agent.Instances.Count, "no second host opened B");
        rig.Hosts.ConnectGate.SetResult();
        Assert.AreEqual("end_turn", (string?)(await b2.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual(1, rig.Agent.Instances.Count);
        Assert.AreEqual(0, rig.Agent.Events.Count(e => e.StartsWith("resume:", StringComparison.Ordinal)), "B was open in its host all along");
    }
}
