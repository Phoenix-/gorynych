using System.Diagnostics;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Acp.Link;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The resumable link (<see cref="ResumableLink"/>) between a plain ACP client and the service, as
/// the stdio bridge runs it for Zed: the connection drops, or the service restarts, and the client
/// sees no break.
/// </summary>
[TestClass]
public sealed class ResumableLinkTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    static Conductor Service(string data, IAgentAdapter agent) => TestDirs.Conductor(data, agent);

    static void NoBreakSeen(LinkHarness zed)
    {
        StringAssert.DoesNotMatch(zed.Log(), new System.Text.RegularExpressions.Regex("zed: .*unknown id"), "Zed got no answer it did not ask for (a second initialize)");
        lock (zed.Notices) CollectionAssert.DoesNotContain(zed.Notices, GorynychProtocol.MethodRestarting, "the link keeps its own notices");
        lock (zed.Updates)
            Assert.IsFalse(zed.Updates.Any(u => u["_meta"]?[GorynychProtocol.MetaPromptId] is not null), "nor the marks of its prompts, nor their text");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(true, DisplayName = "the turn ends while the connection is down")]
    [DataRow(false, DisplayName = "the turn ends after the link resumed")]
    public async Task APromptInFlight_StaysOpenAcrossADrop_AndIsAnsweredWhenItsTurnEnds(bool endsWhileDown)
    {
        var agent = new TurnAgent("turns") { Hold = true };
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");

        zed.ConnectGate = new TaskCompletionSource();
        zed.Break();
        await LinkHarness.Until(() => zed.Log().Contains("lost the connection"), "the link to see it");
        if (endsWhileDown)
        {
            agent.Release();
            await LinkHarness.Until(() => !service.Feed(sid).TurnRunning, "the turn's end, with no client");
        }
        zed.ConnectGate.SetResult();
        await LinkHarness.Until(() => zed.Log().Contains("connected again"), "the reconnect");
        if (!endsWhileDown)
        {
            Assert.IsFalse(prompt.IsCompleted, "held open while its turn runs");
            agent.Release();
        }

        Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"], "answered as its turn ended");
        Assert.AreEqual("started:one done:one ", zed.Text(), "every update once, none lost");
        NoBreakSeen(zed);
        Assert.AreEqual(2, zed.Connects);

        // The connection is whole again.
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("started:one done:one started:two done:two ", zed.Text());
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(true, DisplayName = "answered while the connection is down")]
    [DataRow(false, DisplayName = "answered after the link resumed")]
    public async Task AQuestionInFlight_IsShownOnce_AndItsAnswerReachesTheAgent(bool answeredWhileDown)
    {
        var chosen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new TurnAgent("turns")
        {
            During = async (a, sid, ct) =>
            {
                var r = await a.Rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission, new RequestPermissionRequest
                {
                    SessionId = sid,
                    ToolCall = new ToolCallUpdate { ToolCallId = "t1", Title = "Write a file" },
                    Options =
                    [
                        new PermissionOption { OptionId = "allow", Name = "Allow", Kind = PermissionOptionKind.AllowOnce },
                        new PermissionOption { OptionId = "reject", Name = "Reject", Kind = PermissionOptionKind.RejectOnce },
                    ],
                }, ct);
                chosen.TrySetResult(r.Outcome is SelectedPermissionOutcome s ? s.OptionId : "none");
            },
        };
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        zed.Approve = async (r, ct) =>
        {
            await answer.Task;
            return "reject";
        };
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => { lock (zed.Approvals) return zed.Approvals.Count == 1; }, "the question");

        zed.ConnectGate = new TaskCompletionSource();
        zed.Break();
        await LinkHarness.Until(() => zed.Log().Contains("lost the connection"), "the link to see it");
        if (answeredWhileDown)
        {
            answer.SetResult();
            await Task.Delay(200); // the answer reaches the link, which keeps it
        }
        zed.ConnectGate.SetResult();
        await LinkHarness.Until(() => zed.Log().Contains("connected again"), "the reconnect");
        if (!answeredWhileDown)
        {
            await Task.Delay(200); // the service asks again, and the link joins it to the one Zed has
            answer.SetResult();
        }

        Assert.AreEqual("reject", await chosen.Task.WaitAsync(Wait), "the agent got Zed's answer");
        Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"]);
        lock (zed.Approvals) Assert.AreEqual(1, zed.Approvals.Count, "asked once, though the service asked twice");
        lock (zed.Notices) CollectionAssert.DoesNotContain(zed.Notices, AcpMethods.Protocol.CancelRequest, "nothing withdrawn");
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptThatFailsWhileTheConnectionIsDown_EndsWithTheAgentsError()
    {
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new TurnAgent("turns")
        {
            During = async (_, _, _) =>
            {
                await fail.Task;
                throw new JsonRpcException(-32042, "the agent fell over");
            },
        };
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");

        zed.ConnectGate = new TaskCompletionSource();
        zed.Break();
        await LinkHarness.Until(() => zed.Log().Contains("lost the connection"), "the link to see it");
        fail.SetResult();
        await LinkHarness.Until(() => !service.Feed(sid).TurnRunning, "the failed turn's end");
        zed.ConnectGate.SetResult();

        var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => prompt.WaitAsync(Wait));
        Assert.AreEqual(-32042, e.Code);
        Assert.AreEqual("the agent fell over", e.Message, "as the agent said it, from the mark its end left");
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AfterARestart_TheLinkResumesTheSession_AndTheNextPromptRunsThere()
    {
        var data = TestDirs.New();
        var first = new TurnAgent("turns");
        var service = Service(data, first);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "one").WaitAsync(Wait))!["stopReason"]);

        // The service exits; for a while there is no pipe; then the next one starts on the same data.
        zed.Service = null;
        await service.DisposeAsync();
        zed.Break();
        await LinkHarness.Until(() => zed.Log().Contains("lost the connection"), "the link to see it");
        var list = zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject());
        await Task.Delay(300);
        Assert.IsFalse(list.IsCompleted, "a request waits for the next service");
        var second = new TurnAgent("turns");
        await using var next = Service(data, second);
        zed.Service = next;

        await list.WaitAsync(Wait);
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("started:one done:one started:two done:two ", zed.Text(), "nothing again, nothing lost");
        CollectionAssert.AreEqual(new[] { "two" }, second.Prompts.ToArray());
        Assert.IsTrue(next.Feed(sid).Journal.ReadAll().Count(e => (string?)e["sessionUpdate"] == "user_message_chunk") == 2, "one journal, both turns");
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARestartThatWaits_LetsTheRunningTurnEnd_AndANewPromptWaitsForTheNextService()
    {
        var data = TestDirs.New();
        var first = new TurnAgent("turns") { Hold = true };
        var service = Service(data, first);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var other = await zed.NewSessionAsync().WaitAsync(Wait);
        var running = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text().Contains("started:one"), "the turn");

        await using var admin = await TestClient.ConnectAsync(service, "admin");
        var restart = admin.Rpc.SendRequestAsync(GorynychProtocol.MethodRestart, new JsonObject { ["wait"] = true, ["timeoutSeconds"] = 30 });
        await LinkHarness.Until(() => zed.Log().Contains("restarting"), "the link to hear of it");
        var held = zed.PromptAsync(other, "two");
        await Task.Delay(300);
        Assert.IsFalse(held.IsCompleted);
        Assert.IsFalse(first.Prompts.Contains("two"), "a new prompt does not reach the service that restarts");
        Assert.IsFalse(zed.Heard().Contains("\"gorynych/restarting\":true"), "the link held it: the service had nothing to refuse");

        first.Release();
        Assert.AreEqual("end_turn", (string?)(await running.WaitAsync(Wait))!["stopReason"], "the running turn ended in the old service");
        Assert.AreEqual(0, (int)(await restart.WaitAsync(Wait))!["cancelledTurns"]!);
        await LinkHarness.Until(() => service.ShutdownRequested.IsCancellationRequested, "the exit");
        zed.Service = null;
        await service.DisposeAsync();
        zed.Break();

        var second = new TurnAgent("turns");
        await using var next = Service(data, second);
        zed.Service = next;
        Assert.AreEqual("end_turn", (string?)(await held.WaitAsync(Wait))!["stopReason"], "the held prompt ran in the next service");
        CollectionAssert.AreEqual(new[] { "two" }, second.Prompts.ToArray());
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AForcedRestart_EndsThePromptCancelled_AndTheSessionGoesOnInTheNextService()
    {
        var data = TestDirs.New();
        var first = new TurnAgent("turns") { Hold = true };
        var service = Service(data, first);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var running = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text().Contains("started:one"), "the turn");

        await using (var admin = await TestClient.ConnectAsync(service, "admin"))
            Assert.AreEqual(1, (int)(await admin.Rpc.SendRequestAsync(GorynychProtocol.MethodRestart,
                new JsonObject { ["wait"] = false, ["force"] = true }).WaitAsync(Wait))!["cancelledTurns"]!);
        Assert.AreEqual("cancelled", (string?)(await running.WaitAsync(Wait))!["stopReason"]);
        zed.Service = null;
        await service.DisposeAsync();
        zed.Break();
        await using var next = Service(data, new TurnAgent("turns"));
        zed.Service = next;

        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"]);
        StringAssert.EndsWith(zed.Text(), "started:two done:two ");
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ACalledOffRestart_SendsWhatTheLinkHeld_ToTheSameService_AndOnlyHeldSessionsWaited()
    {
        var agent = new TurnAgent("turns") { Hold = true };
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var busy = await zed.NewSessionAsync().WaitAsync(Wait);
        var other = await zed.NewSessionAsync().WaitAsync(Wait);
        var running = zed.PromptAsync(busy, "one");
        await LinkHarness.Until(() => zed.Text().Contains("started:one"), "the turn");

        await using var admin = await TestClient.ConnectAsync(service, "admin");
        var restart = admin.Rpc.SendRequestAsync(GorynychProtocol.MethodRestart, new JsonObject { ["wait"] = true, ["timeoutSeconds"] = 1.5 });
        await LinkHarness.Until(() => zed.Log().Contains("restarting"), "the link to hear of it");
        var held = zed.PromptAsync(other, "two");
        var behind = zed.Zed.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = other });
        // The rest the draining service still serves: a new thread opens, the list comes.
        Assert.IsNotNull(await zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject()).WaitAsync(TimeSpan.FromSeconds(1)));
        await zed.NewSessionAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(held.IsCompleted);
        Assert.IsFalse(behind.IsCompleted, "what follows a held prompt in its session waits behind it");

        Assert.IsFalse((bool)(await restart.WaitAsync(Wait))!["restarted"]!, "the turn outlasted the wait");
        await LinkHarness.Until(() => zed.Log().Contains("called its restart off"), "the link to hear of it");
        await behind.WaitAsync(Wait);
        agent.Release();

        Assert.AreEqual("end_turn", (string?)(await running.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("end_turn", (string?)(await held.WaitAsync(Wait))!["stopReason"], "the held prompt ran in the service that stayed");
        CollectionAssert.AreEquivalent(new[] { "one", "two" }, agent.Prompts.ToArray());
        Assert.AreEqual(1, zed.Connects, "it never went");
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptARestartRefused_GoesAgainToTheSameService_WhenTheRestartIsCalledOff()
    {
        var agent = new TurnAgent("turns") { Hold = true };
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var busy = await zed.NewSessionAsync().WaitAsync(Wait);
        var other = await zed.NewSessionAsync().WaitAsync(Wait);
        var running = zed.PromptAsync(busy, "one");
        await LinkHarness.Until(() => zed.Text().Contains("started:one"), "the turn");
        zed.Current.PauseLink(); // it hears nothing of the restart in time

        await using var admin = await TestClient.ConnectAsync(service, "admin");
        var restart = admin.Rpc.SendRequestAsync(GorynychProtocol.MethodRestart, new JsonObject { ["wait"] = true, ["timeoutSeconds"] = 0.5 });
        await LinkHarness.Until(() => service.Draining, "the drain");
        var refused = zed.PromptAsync(other, "two");
        await LinkHarness.Until(() => zed.Heard().Contains("\"gorynych/restarting\":true"), "the refusal");
        Assert.IsFalse((bool)(await restart.WaitAsync(Wait))!["restarted"]!);
        zed.Current.Go(); // the notice, the refusal and the call-off reach it now, in whatever order

        agent.Release();
        Assert.AreEqual("end_turn", (string?)(await refused.WaitAsync(Wait))!["stopReason"], "sent again to the service that stayed, without waiting for another");
        Assert.AreEqual("end_turn", (string?)(await running.WaitAsync(Wait))!["stopReason"]);
        CollectionAssert.AreEquivalent(new[] { "one", "two" }, agent.Prompts.ToArray());
        Assert.AreEqual(1, zed.Connects);
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptInFlightWhenTheServiceDies_EndsCancelled_ForTheNextServiceHasNoTurnForIt()
    {
        var data = TestDirs.New();
        var service = Service(data, new TurnAgent("turns") { Hold = true });
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var running = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text().Contains("started:one"), "the turn");

        // Gone without a restart: its agents with it. Its prompt's end is journaled before the next one starts.
        zed.Service = null;
        zed.Break();
        await LinkHarness.Until(() => service.Feed(sid).Clients().Length == 0, "the old service to see its client go");
        await service.DisposeAsync();
        await LinkHarness.Until(() => !service.Feed(sid).TurnRunning, "the old service's prompt to end");
        Assert.AreEqual("cancelled", (string?)service.Feed(sid).Journal.ReadAll()
            .LastOrDefault(e => e["_meta"]?[GorynychProtocol.MetaPromptId] is not null && (string?)e["sessionUpdate"] == "session_info_update")
            ?["_meta"]?[GorynychProtocol.MetaStopReason], "cut off with its agent: cancelled, not an error");
        await using var next = Service(data, new TurnAgent("turns"));
        zed.Service = next;

        Assert.AreEqual("cancelled", (string?)(await running.WaitAsync(Wait))!["stopReason"], "cut off with its agent, whether the old service left a mark of it or not");
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"]);
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptsOwnText_IsNotShownToItsClient_WhenAResumeReplaysIt()
    {
        var agent = new TurnAgent("turns");
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        await zed.PromptAsync(sid, "one").WaitAsync(Wait);
        // The next turn says nothing before the drop: the client's last position is before its prompt's text.
        agent.Quiet = true;
        agent.Hold = true;
        var prompt = zed.PromptAsync(sid, "two");
        await LinkHarness.Until(() => agent.Prompts.Contains("two"), "the turn");

        zed.Break();
        await LinkHarness.Until(() => zed.Log().Contains("connected again"), "the reconnect");
        agent.Release();

        Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"]);
        StringAssert.Contains(zed.Heard(), "\"text\":\"two\"", "the resume did send the prompt's text from the journal");
        Assert.IsFalse(zed.Kinds().Contains("user_message_chunk"), "Zed has the prompt already; it never gets it back");
        Assert.AreEqual("started:one done:one done:two ", zed.Text());
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARequestOnItsWayThatMayRunTwice_GoesAgain_AndOneThatMayNot_EndsWithAnError()
    {
        var agent = new TurnAgent("turns");
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        agent.SlowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject());
        var created = zed.NewSessionAsync();
        await LinkHarness.Until(() => Volatile.Read(ref agent.Lists) >= 1 && Volatile.Read(ref agent.News) == 1, "both in the agent");

        zed.Break();
        var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => created.WaitAsync(Wait));
        StringAssert.Contains(e.Message, "may or may not have been carried out");
        await LinkHarness.Until(() => zed.Log().Contains("connected again"), "the reconnect");
        agent.SlowGate.SetResult();

        Assert.IsNotNull((await list.WaitAsync(Wait))!["sessions"], "listed by the service it reached again");
        Assert.IsTrue(Volatile.Read(ref agent.Lists) >= 2, "asked again");
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ADraftOutlivesARestart_AndItsAgentIsToldItIsOne()
    {
        var data = TestDirs.New();
        var service = Service(data, new TurnAgent("turns"));
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        zed.Service = null;
        zed.Break();
        await service.DisposeAsync();
        var agent = new TurnAgent("turns");
        await using var next = Service(data, agent);
        zed.Service = next;

        await LinkHarness.Until(() => zed.Log().Contains("connected again"), "the reconnect");
        var resumed = agent.Resumes.Single();
        Assert.AreEqual(sid, (string?)resumed["sessionId"]);
        Assert.IsTrue((bool?)resumed["_meta"]?[GorynychProtocol.MetaDraft] == true, "an agent that wrote nothing of it can open it afresh");
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "one").WaitAsync(Wait))!["stopReason"]);
        Assert.IsFalse(agent.Resumes.Skip(1).Any(r => r["_meta"]?[GorynychProtocol.MetaDraft] is not null), "no longer a draft");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptTheServiceNeverGot_EndsCancelled_AndTheSessionGoesOn()
    {
        var agent = new TurnAgent("turns");
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        zed.Current.PauseService();
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Sent().Contains("\"text\":\"one\""), "the prompt on its way");

        zed.Break(); // the service never read it

        Assert.AreEqual("cancelled", (string?)(await prompt.WaitAsync(Wait))!["stopReason"], "not in progress there, and no mark of it: it runs nowhere");
        Assert.AreEqual(0, agent.Prompts.Count);
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"]);
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptTheRestartingServiceRefused_GoesToTheNextOne()
    {
        var data = TestDirs.New();
        var first = new TurnAgent("turns");
        var service = Service(data, first);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        await zed.PromptAsync(sid, "one").WaitAsync(Wait);
        zed.Current.PauseLink(); // it does not hear of the restart in time

        await using (var admin = await TestClient.ConnectAsync(service, "admin"))
        {
            var restart = admin.Rpc.SendRequestAsync(GorynychProtocol.MethodRestart, new JsonObject { ["wait"] = true, ["timeoutSeconds"] = 30 });
            await LinkHarness.Until(() => service.Draining, "the drain");
            var prompt = zed.PromptAsync(sid, "two");
            await LinkHarness.Until(() => zed.Heard().Contains("\"gorynych/restarting\":true"), "the refusal");
            zed.Current.Go();
            await restart.WaitAsync(Wait);
            await LinkHarness.Until(() => service.ShutdownRequested.IsCancellationRequested, "the exit");
            zed.Service = null;
            await service.DisposeAsync();
            zed.Break();
            var second = new TurnAgent("turns");
            await using var next = Service(data, second);
            zed.Service = next;

            Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"], "not carried out there, so sent to the next one");
            CollectionAssert.AreEqual(new[] { "two" }, second.Prompts.ToArray());
            CollectionAssert.AreEqual(new[] { "one" }, first.Prompts.ToArray());
        }
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APlainResume_TakesItsPositionFromTheAnswer_SoAReconnectReplaysNothing()
    {
        await using var service = Service(TestDirs.New(), new TurnAgent("turns"));
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        await zed.PromptAsync(sid, "one").WaitAsync(Wait);
        await zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sid }).WaitAsync(Wait);
        // Opened again with no history, as Zed does for a thread it shows already.
        await zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject
        {
            ["sessionId"] = sid, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray(),
        }).WaitAsync(Wait);
        int before;
        lock (zed.Updates) before = zed.Updates.Count;

        zed.Break();
        await LinkHarness.Until(() => zed.Log().Contains("connected again; 1 session resumed"), "the reconnect");
        await Task.Delay(200);

        lock (zed.Updates) Assert.AreEqual(before, zed.Updates.Count, "the journal it never asked for is not sent now");
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"]);
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ALoadTheClientAsksFor_ShowsItsOwnPrompts_AsAnyHistory()
    {
        await using var service = Service(TestDirs.New(), new TurnAgent("turns"));
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        await zed.PromptAsync(sid, "one").WaitAsync(Wait);
        await zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sid }).WaitAsync(Wait);
        lock (zed.Updates) zed.Updates.Clear();

        // Zed drops a thread and loads it again later, through the same bridge.
        await zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject
        {
            ["sessionId"] = sid, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray(),
        }).WaitAsync(Wait);

        var user = zed.Kinds().Count(k => k == "user_message_chunk");
        Assert.AreEqual(1, user, "the history shows what the person wrote, though this bridge sent it");
        Assert.AreEqual("started:one done:one ", zed.Text());
        lock (zed.Updates) Assert.IsFalse(zed.Updates.Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended"), "the marks stay with the link");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ACancelSentWhileTheConnectionIsDown_StopsTheTurnThatRunsOn()
    {
        var agent = new TurnAgent("turns") { Hold = true };
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");

        zed.ConnectGate = new TaskCompletionSource();
        zed.Break();
        await LinkHarness.Until(() => zed.Log().Contains("lost the connection"), "the link to see it");
        await zed.Zed.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sid });
        await Task.Delay(200);
        zed.ConnectGate.SetResult();

        Assert.AreEqual("cancelled", (string?)(await prompt.WaitAsync(Wait))!["stopReason"], "the cancel reached the turn in the service, which ran on");
        Assert.AreEqual("started:one ", zed.Text(), "it never got to its end");
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ADraftIsKeptForAClientOnTheLink_WhoseConnectionDropped_AndItsFirstPromptRuns()
    {
        var agent = new TurnAgent("turns");
        await using var service = Service(TestDirs.New(), agent);
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait); // Zed opens one for every new thread

        zed.Break();
        await LinkHarness.Until(() => zed.Log().Contains("connected again"), "the reconnect");
        await Task.Delay(300); // a plain client's draft is discarded by now

        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "one").WaitAsync(Wait))!["stopReason"]);
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheLinkGivesUp_WhenNoServiceComesBack_AndWhatWaitedEndsWithAnError()
    {
        var service = Service(TestDirs.New(), new TurnAgent("turns") { Hold = true });
        await using var zed = new LinkHarness(service, new ResumableLinkOptions
        {
            RetryDelays = [TimeSpan.FromMilliseconds(100)],
            GiveUpAfter = TimeSpan.FromSeconds(1),
        });
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var running = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text().Contains("started:one"), "the turn");
        zed.Service = null;
        zed.Break();
        await service.DisposeAsync();
        var list = zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject());

        Assert.AreEqual(1, await zed.Running.WaitAsync(Wait), "it ends, saying it failed");
        var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => running.WaitAsync(Wait));
        StringAssert.Contains(e.Message, "lost");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => list.WaitAsync(Wait));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ALinkToAServiceThatDoesNotResume_EndsWithTheConnection_AsAPipeWould()
    {
        var pair = new DuplexLink();
        var (si, so) = pair.AgentEnd;
        var old = new JsonRpcConnection(si, so, "old-service")
        {
            RequestHandler = (req, ct) => Task.FromResult(AcpJson.ToNode(new InitializeResponse { ProtocolVersion = 1, AgentCapabilities = new AgentCapabilities() })),
        };
        old.Start();
        var (ci, co) = pair.ClientEnd;
        var zedPair = new DuplexLink();
        var zed = new JsonRpcConnection(zedPair.ClientEnd.Input, zedPair.ClientEnd.Output, "zed");
        zed.Start();
        var connects = 0;
        var link = new ResumableLink(zedPair.AgentEnd.Input, zedPair.AgentEnd.Output, _ => { connects++; throw new IOException("no"); });
        var running = link.RunAsync((ci, co));
        await zed.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 }).WaitAsync(Wait);

        await old.DisposeAsync();

        Assert.AreEqual(0, await running.WaitAsync(Wait));
        Assert.AreEqual(0, connects, "no attempt to connect again");
        await zed.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ACancelOfAPromptThatWaitsToGoOut_EndsItThere()
    {
        var data = TestDirs.New();
        var service = Service(data, new TurnAgent("turns"));
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        zed.Service = null;
        zed.Break();
        await LinkHarness.Until(() => zed.Log().Contains("lost the connection"), "the link to see it");

        var waiting = zed.PromptAsync(sid, "one");
        await zed.Zed.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sid });

        Assert.AreEqual("cancelled", (string?)(await waiting.WaitAsync(Wait))!["stopReason"], "it never went out");
        var agent = new TurnAgent("turns");
        await service.DisposeAsync();
        await using var next = Service(data, agent);
        zed.Service = next;
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"]);
        CollectionAssert.AreEqual(new[] { "two" }, agent.Prompts.ToArray(), "the cancelled one never ran");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Restart_StopsTheService_AndTheBridgeStartsTheNextOne_AsOneConnection()
    {
        var pipe = "gorynych-restart-" + Guid.NewGuid().ToString("N")[..8];
        if (Outside.ServiceLaunch.IsRemoteLogon()) Assert.Inconclusive("from an ssh login or session 0 the bridge starts no service without the logon task");
        var started = new List<int>();
        try
        {
            await using var zed = new PlainClient("acp", "--agent", "kimi", "--pipe", pipe, "--data", TestDirs.New());
            var init = await zed.InitializeAsync();
            Assert.AreEqual(GorynychProtocol.LinkVersion, (int?)init!["agentCapabilities"]!["_meta"]![GorynychProtocol.CapLink]);
            var before = (int)(await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject()).WaitAsync(Wait))!["processId"]!;
            started.Add(before);

            var (code, stderr) = await RunAsync("restart", "--pipe", pipe, "--no-wait");

            Assert.AreEqual(0, code, stderr);
            StringAssert.Contains(stderr, "the next bridge or the logon task starts the new one");
            var after = (int)(await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject()).WaitAsync(Wait))!["processId"]!;
            started.Add(after);
            Assert.AreNotEqual(before, after, "the bridge started the next service and went on");
            var sessions = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject()).WaitAsync(Wait);
            Assert.IsNotNull(sessions!["sessions"]);
        }
        finally
        {
            // Whatever failed, no service of this test outlives it (it would hold the build's files).
            await RunAsync("shutdown", "--pipe", pipe);
            foreach (var id in started)
                try
                {
                    using var left = Process.GetProcessById(id);
                    left.Kill();
                }
                catch (ArgumentException) { } // gone
        }
    }

    static async Task<(int Code, string Stderr)> RunAsync(params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(BridgeTests.ServiceExe(), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        })!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(Wait * 3);
        return (process.ExitCode, await stderr);
    }

    [TestMethod]
    public void TheRestartCommand_TakesATimeoutInMinutes_AndNoWait()
    {
        var line = CommandLine.Parse(["restart", "--pipe", "p", "--timeout", "2.5", "--no-wait", "--force"]);
        Assert.AreEqual("restart", line.Command);
        Assert.AreEqual(TimeSpan.FromMinutes(2.5), CommandLine.MinutesOf(line.Value("--timeout")));
        Assert.IsTrue(line.Flag("--no-wait"));
        Assert.IsTrue(line.Flag("--force"));
        StringAssert.Contains(Assert.ThrowsExactly<UsageException>(() => CommandLine.Parse(["restart", "--timeout", "-1"])).Message, "--timeout takes a number of minutes");
        StringAssert.Contains(Assert.ThrowsExactly<UsageException>(() => CommandLine.Parse(["restart", "--console"])).Message, "restart does not take '--console'");
        StringAssert.Contains(CommandLine.Usage, "Gorynych.Service restart");
    }
}
