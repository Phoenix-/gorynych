using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

/// <summary>
/// Adapter hosts (docs/service-protocol.md, "Adapter hosts"): the adapters run in hosts that a
/// restart of the conductor leaves running, so a turn goes on across it, and the next conductor takes
/// back what the host kept for it.
/// </summary>
[TestClass]
public sealed class HostTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>A restart as <c>Gorynych.Service restart</c> asks for one, which would wait up to 30 s for turns.</summary>
    static async Task<JsonObject> RestartAsync(Conductor service)
    {
        JsonObject reply;
        await using (var admin = await TestClient.ConnectAsync(service, "admin"))
            reply = (JsonObject)(await admin.Rpc.SendRequestAsync(GorynychProtocol.MethodRestart,
                new JsonObject { ["wait"] = true, ["timeoutSeconds"] = 30 }).WaitAsync(Wait))!;
        await LinkHarness.Until(() => service.ShutdownRequested.IsCancellationRequested, "the exit");
        return reply;
    }

    /// <summary>The service exits, and for a while there is none: neither the link nor the hosts reach one.</summary>
    static async Task GoneAsync(Conductor service, LinkHarness? zed, InProcessHosts hosts)
    {
        if (zed is not null) zed.Service = null;
        hosts.Service = null;
        await service.DisposeAsync();
        zed?.Break();
    }

    static Conductor Next(string data, InProcessHosts hosts, LinkHarness? zed, IAgentAdapter adapter)
    {
        var next = hosts.Conductor(data, adapter);
        hosts.Service = next;
        if (zed is not null) zed.Service = next;
        return next;
    }

    static JsonObject[] Journal(Conductor service, string sid) => service.Feed(sid).Journal.ReadAll().OfType<JsonObject>().ToArray();

    static int EndMarks(Conductor service, string sid) => Journal(service, sid)
        .Count(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended" && u["_meta"]?[GorynychProtocol.MetaPromptId] is not null);

    static string JournalText(Conductor service, string sid) => string.Concat(Journal(service, sid)
        .Where(u => (string?)u["sessionUpdate"] == "agent_message_chunk").Select(u => (string?)u["content"]?["text"]));

    static int Count(JsonNode? array) => (array as JsonArray)?.Count ?? 0;

    static void NoBreakSeen(LinkHarness zed)
    {
        StringAssert.DoesNotMatch(zed.Log(), new System.Text.RegularExpressions.Regex("zed: .*unknown id"), "Zed got no answer it did not ask for");
        lock (zed.Notices) CollectionAssert.DoesNotContain(zed.Notices, GorynychProtocol.MethodRestarting, "the link keeps its own notices");
    }

    static Task<JsonNode?> Prompt(TestClient client, string sessionId, string text) => client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
    {
        ["sessionId"] = sessionId,
        ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    });

    static async Task<string> NewSession(TestClient client) =>
        (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() })
            .WaitAsync(Wait))!["sessionId"]!;

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(true, DisplayName = "the turn ends while no conductor runs")]
    [DataRow(false, DisplayName = "the turn ends after the next conductor took it back")]
    public async Task ATurn_GoesOnAcrossARestart_AndItsAnswerArrivesWhole(bool endsWhileNone)
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");

        await using (var admin = await TestClient.ConnectAsync(service, "admin"))
        {
            var status = await admin.Rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject());
            Assert.IsTrue((bool)status!["hosts"]!, "it says its adapters run in hosts");
            Assert.AreEqual(hosts["turns"].Host.HostId, (string?)status["agents"]![0]!["host"]);
        }
        var restart = await RestartAsync(service);
        Assert.IsTrue((bool)restart["restarted"]!);
        Assert.AreEqual(0, (int)restart["cancelledTurns"]!);
        Assert.AreEqual(1, (int)restart["turnsInHosts"]!);
        Assert.IsLessThan(5, (double)restart["waitedSeconds"]!, "it did not wait for the turn");
        await GoneAsync(service, zed, hosts);

        // With no conductor, the agent goes on, and its host keeps what it says.
        await agent.Current!.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "between " } });
        var host = hosts["turns"].Host;
        if (endsWhileNone)
        {
            agent.Release();
            await LinkHarness.Until(() => Count(host.Report()["turns"]) == 0, "the turn's end in the host");
        }

        await using var next = Next(data, hosts, zed, agent);
        if (!endsWhileNone)
        {
            await LinkHarness.Until(() => next.Feed(sid).Adopted is not null, "the turn taken back");
            await Task.Delay(200);
            Assert.IsFalse(prompt.IsCompleted, "held open while its turn runs");
            agent.Release();
        }
        Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("started:one between done:one ", zed.Text(), "every update once, none lost");
        CollectionAssert.AreEqual(new[] { "one" }, agent.Prompts.ToArray(), "the prompt reached the agent once");
        Assert.AreEqual(1, hosts.Launches, "the same host, with the same agent");
        Assert.AreEqual("started:one between done:one ", JournalText(next, sid));
        Assert.AreEqual(1, EndMarks(next, sid), "its end marked once");
        // Only what the old one had not journaled: the update without it, and the turn's end if it came then.
        StringAssert.Contains(hosts.Log(), endsWhileNone ? "3 updates sent again" : "1 updates sent again");
        NoBreakSeen(zed);

        // The session goes on in the next service; and once more, which doubles nothing.
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"]);
        await RestartAsync(next);
        await GoneAsync(next, zed, hosts);
        await using var third = Next(data, hosts, zed, agent);
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "three").WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("started:one between done:one started:two done:two started:three done:three ", JournalText(third, sid));
        Assert.AreEqual("started:one between done:one started:two done:two started:three done:three ", zed.Text());
        Assert.AreEqual(3, EndMarks(third, sid));
        Assert.AreEqual(1, hosts.Launches);
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ATurn_GoesOnAcrossTwoRestarts_AndEndsOnceInTheThirdService()
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");
        await RestartAsync(service);
        await GoneAsync(service, zed, hosts);
        var next = Next(data, hosts, zed, agent);
        await LinkHarness.Until(() => next.Feed(sid).Adopted is not null, "the turn taken back");

        // A service that took a turn back leaves it running for the next one, too.
        Assert.AreEqual(1, (int)(await RestartAsync(next))["turnsInHosts"]!);
        await GoneAsync(next, zed, hosts);
        Assert.AreEqual(0, EndMarks(next, sid));
        await using var third = Next(data, hosts, zed, agent);
        await LinkHarness.Until(() => third.Feed(sid).Adopted is not null, "the turn taken back again");
        agent.Release();
        Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("started:one done:one ", zed.Text());
        Assert.AreEqual(1, EndMarks(third, sid));
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAnswerTheOldConductorNeverRead_IsMarkedByTheNextOne()
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");

        // The turn ends as the conductor exits: what the host writes, it never reads.
        hosts.LastWire.PauseService();
        agent.Release();
        await LinkHarness.Until(() => Count(hosts["turns"].Host.Report()["turns"]) == 0, "the turn's end in the host");
        await RestartAsync(service);
        await GoneAsync(service, zed, hosts);
        Assert.AreEqual(0, EndMarks(service, sid), "the old conductor marked nothing: the turn did not end there");

        await using var next = Next(data, hosts, zed, agent);
        Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"], "from the end its host reported again");
        Assert.AreEqual("started:one done:one ", zed.Text());
        Assert.AreEqual("started:one done:one ", JournalText(next, sid));
        Assert.AreEqual(1, EndMarks(next, sid));
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(true, DisplayName = "answered while no conductor runs")]
    [DataRow(false, DisplayName = "answered after the next conductor asked again")]
    public async Task AnApprovalPendingAcrossARestart_IsShownOnce_AndItsAnswerReachesTheAgent(bool answeredWhileNone)
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
        var data = TestDirs.New();
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
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

        await RestartAsync(service);
        await GoneAsync(service, zed, hosts);
        if (answeredWhileNone)
        {
            answer.SetResult();
            await Task.Delay(200); // the answer reaches the link, which keeps it
        }
        await using var next = Next(data, hosts, zed, agent);
        if (!answeredWhileNone)
        {
            await LinkHarness.Until(() => hosts.Log().Contains("1 questions asked again"), "the host to ask again");
            await Task.Delay(200); // and the link joins it to the one Zed has
            answer.SetResult();
        }

        Assert.AreEqual("reject", await chosen.Task.WaitAsync(Wait), "the agent got Zed's answer");
        Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"]);
        lock (zed.Approvals) Assert.AreEqual(1, zed.Approvals.Count, "asked once, though two conductors asked");
        lock (zed.Notices) CollectionAssert.DoesNotContain(zed.Notices, AcpMethods.Protocol.CancelRequest, "nothing withdrawn");
        Assert.AreEqual(1, EndMarks(next, sid));
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptWaitingBehindARunningTurn_GoesToTheNextService_AndRunsAfterIt()
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var one = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");
        var two = zed.PromptAsync(sid, "two");
        await LinkHarness.Until(() => service.Feed(sid).PromptsInProgress().Length == 2, "the second prompt, waiting");

        var restart = await RestartAsync(service);
        Assert.IsLessThan(5, (double)restart["waitedSeconds"]!, "a restart with hosts does not wait for the turn before it either");
        await GoneAsync(service, zed, hosts);
        await using var next = Next(data, hosts, zed, agent);
        await LinkHarness.Until(() => next.Feed(sid).Adopted is not null, "the turn taken back");
        await Task.Delay(300);
        CollectionAssert.AreEqual(new[] { "one" }, agent.Prompts.ToArray(), "the second waits for the turn the host brought back");

        agent.Release();
        Assert.AreEqual("end_turn", (string?)(await one.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("end_turn", (string?)(await two.WaitAsync(Wait))!["stopReason"]);
        CollectionAssert.AreEqual(new[] { "one", "two" }, agent.Prompts.ToArray(), "each once");
        Assert.AreEqual("started:one done:one started:two done:two ", zed.Text());
        Assert.AreEqual(2, EndMarks(next, sid));
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(false, DisplayName = "idle")]
    [DataRow(true, DisplayName = "with a turn running")]
    public async Task AHostWithNoConductor_ExitsAfterItsWait_CancellingWhatRuns(bool busy)
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent)
        {
            OrphanIdle = TimeSpan.FromMilliseconds(300),
            OrphanBusy = busy ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30),
        };
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        Task<JsonNode?>? prompt = null;
        await using (var gui = await TestClient.ConnectAsync(service, "gui"))
        {
            var sid = await NewSession(gui);
            if (busy)
            {
                prompt = Prompt(gui, sid, "one");
                await LinkHarness.Until(() => gui.Text() == "started:one ", "the turn");
            }
            await RestartAsync(service);
        }
        await GoneAsync(service, null, hosts);
        var run = hosts["turns"].Run;
        if (busy)
        {
            await Task.Delay(1000);
            Assert.IsFalse(run.IsCompleted, "a host with a turn waits longer than an idle one");
        }
        Assert.AreEqual(2, await run.WaitAsync(Wait), "an orphan's exit code");
        StringAssert.Contains(hosts.Log(), busy ? "cancelling 1 turns and exiting" : "cancelling 0 turns and exiting");
        if (busy) CollectionAssert.AreEqual(new[] { "one" }, agent.Prompts.ToArray());
        Assert.AreEqual(busy ? 1 : 0, agent.Cancels, "its turn cancelled in the agent");
        Assert.IsTrue(agent.Current!.Rpc.Completion.IsCompleted, "its agent stopped with it");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(false, true, DisplayName = "a turn this conductor started")]
    [DataRow(true, true, DisplayName = "a turn it took back after a restart")]
    [DataRow(false, false, DisplayName = "the link breaks, and the host that comes back is stopped")]
    public async Task AHostThatGoes_EndsItsTurnsCancelled_WithTheirMarks_AndTheNextPromptStartsAnother(bool takenBack, bool dies)
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");
        var current = service;
        if (takenBack)
        {
            await RestartAsync(service);
            await GoneAsync(service, zed, hosts);
            current = Next(data, hosts, zed, agent);
            await LinkHarness.Until(() => current.Feed(sid).Adopted is not null, "the turn taken back");
        }
        try
        {
            var first = hosts["turns"];
            await hosts.KillAsync("turns", agentToo: dies);
            // One whose link broke comes back, but this service ended its turns as it saw it go: it is stopped.
            try { Assert.AreEqual(0, await first.Run.WaitAsync(Wait)); }
            catch (TimeoutException) { Assert.Fail("the host still runs:\n" + hosts.Log()); }
            Assert.AreEqual("cancelled", (string?)(await prompt.WaitAsync(Wait))!["stopReason"], "its turn died with it");
            Assert.AreEqual(1, EndMarks(current, sid));
            await LinkHarness.Until(() => !current.Feed(sid).TurnRunning, "the session free again");

            agent.Release();
            Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"], "in a new host");
            Assert.AreEqual(2, hosts.Launches);
            StringAssert.EndsWith(zed.Text(), "started:two done:two ");
            NoBreakSeen(zed);
        }
        finally
        {
            await current.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHostThatDiesWhileNoServiceRuns_LeavesItsTurnToBeEndedCancelled_ByTheNext()
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");
        await RestartAsync(service);
        await GoneAsync(service, zed, hosts);
        var first = hosts["turns"];
        await hosts.KillAsync("turns");
        await first.Run.WaitAsync(Wait);

        agent.Release();
        await using var next = Next(data, hosts, zed, agent);
        Assert.AreEqual("cancelled", (string?)(await prompt.WaitAsync(Wait))!["stopReason"], "its turn went with its host");
        Assert.AreEqual(1, EndMarks(next, sid));
        Assert.AreEqual(2, hosts.Launches, "a new host");
        Assert.AreEqual("end_turn", (string?)(await zed.PromptAsync(sid, "two").WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual(2, EndMarks(next, sid));
        NoBreakSeen(zed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHostThatGoesInTheMiddleOfItsHandshake_EndsTheTurnsItBroughtBack()
    {
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent);
        JsonObject Init(bool back) => new()
        {
            ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject(),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaHost] = new JsonObject { ["hostId"] = "h", ["link"] = 1, ["reattached"] = back } },
        };
        var first = new RawHost(service, Hello(hostId: "h1"), req => req.Method switch
        {
            AcpMethods.Agent.Initialize => Init(false),
            AcpMethods.Agent.SessionNew => new JsonObject { ["sessionId"] = "s1" },
            _ => new JsonObject(),
        });
        await LinkHarness.Until(() => service.Agents["turns"].HostId == "h1", "the first host");
        await using var gui = await TestClient.ConnectAsync(service, "gui");
        Assert.AreEqual("s1", await NewSession(gui));
        await first.DisposeAsync();
        await LinkHarness.Until(() => !service.Agents["turns"].IsRunning, "the first host gone");

        // One that comes back with a turn, and goes before it is taken.
        RawHost? second = null;
        second = new RawHost(service, Hello(hostId: "h2"), req =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize:
                    return Init(true);
                case GorynychProtocol.MethodHostState:
                    return new JsonObject
                    {
                        ["sessions"] = new JsonArray("s1"),
                        ["turns"] = new JsonArray(new JsonObject { ["sessionId"] = "s1", ["turn"] = 3, ["promptId"] = "p1" }),
                    };
                default:
                    _ = Task.Run(() => second!.DisposeAsync().AsTask()); // host_replay: the host is gone
                    return null;
            }
        });
        await LinkHarness.Until(() => EndMarks(service, "s1") == 1, "the turn's end");
        var mark = Journal(service, "s1").Last(u => (string?)u["_meta"]?[GorynychProtocol.MetaPromptId] == "p1");
        Assert.AreEqual("cancelled", (string?)mark["_meta"]![GorynychProtocol.MetaStopReason]);
        await LinkHarness.Until(() => !service.Feed("s1").TurnRunning && service.Feed("s1").Adopted is null, "the session free again");
        await second.DisposeAsync();

        await using var third = new RawHost(service, Hello(hostId: "h2"), req => req.Method == AcpMethods.Agent.Initialize ? Init(true) : new JsonObject());
        await LinkHarness.Until(() => { lock (third.Asked) return third.Asked.Contains(GorynychProtocol.MethodHostStop); }, "the host stopped as it came back");
        lock (third.Asked) CollectionAssert.DoesNotContain(third.Asked, AcpMethods.Agent.Initialize);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AShutdown_StopsTheHosts_AndTheirAgents()
    {
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(TestDirs.New(), agent);
        hosts.Service = service;
        await using (var gui = await TestClient.ConnectAsync(service, "gui"))
            await NewSession(gui);
        await service.DisposeAsync();
        Assert.AreEqual(0, await hosts["turns"].Run.WaitAsync(Wait), "stopped by its conductor");
        StringAssert.Contains(hosts.Log(), "the conductor stops the host");
        Assert.IsTrue(agent.Current!.Rpc.Completion.IsCompleted);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ThePeerTokensOfSessions_OutliveARestart()
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        string sid;
        await using (var gui = await TestClient.ConnectAsync(service, "gui"))
        {
            sid = await NewSession(gui);
            await Prompt(gui, sid, "one").WaitAsync(Wait); // no draft, which goes with its client
        }
        var token = service.Store.Get(sid)!.PeerToken;
        Assert.IsNotNull(token, "kept with the session");
        await RestartAsync(service);
        await GoneAsync(service, null, hosts);

        // The agent's peers server still holds the token: the next service knows it.
        await using var next = Next(data, hosts, null, agent);
        await using var peers = await TestClient.ConnectAsync(next, "peers");
        var reply = await peers.Rpc.SendRequestAsync(GorynychProtocol.MethodReply, new JsonObject
        {
            ["messageId"] = "none", ["text"] = "hello", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
        }).WaitAsync(Wait);
        Assert.IsFalse((bool)reply!["delivered"]!, "taken as the session's: there was just nothing to deliver");
        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => peers.Rpc.SendRequestAsync(GorynychProtocol.MethodReply, new JsonObject
        {
            ["messageId"] = "none", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = "not-a-token" },
        }).WaitAsync(Wait));
        StringAssert.Contains(refused.Message, "Unknown peer token");
    }

    /// <summary>A host of its own making on the host link: the hello it says, then what it answers.</summary>
    sealed class RawHost : IAsyncDisposable
    {
        readonly Wire wire;

        public RawHost(Conductor service, JsonObject hello, Func<JsonRpcRequest, JsonNode?>? answer = null)
        {
            wire = new Wire(service, "raw-host", host: true);
            Rpc = new JsonRpcConnection(wire.LinkEnd.Input, wire.LinkEnd.Output, "raw-host");
            Rpc.RequestHandler = (req, ct) =>
            {
                lock (Asked) Asked.Add(req.Method);
                return Task.FromResult<JsonNode?>(answer?.Invoke(req) ?? throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method));
            };
            Rpc.Start();
            Hello = Rpc.SendNotificationAsync(GorynychProtocol.MethodHostHello, hello);
        }

        public JsonRpcConnection Rpc { get; }
        public Task Hello { get; }
        public List<string> Asked { get; } = [];

        public async ValueTask DisposeAsync()
        {
            await Rpc.DisposeAsync();
            wire.Break();
        }
    }

    static JsonObject Hello(string token = InProcessHosts.Token, int link = GorynychProtocol.HostLinkVersion, string adapter = "turns", string? hostId = "old-host") => new()
    {
        ["token"] = token, ["hostId"] = hostId, ["adapter"] = adapter, ["link"] = link, ["pid"] = 1, ["version"] = "0.0.1-old",
    };

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow("token", DisplayName = "a token that is not the service's")]
    [DataRow("newer", DisplayName = "a newer host link")]
    [DataRow("older", DisplayName = "a host link older than any it takes")]
    [DataRow("adapter", DisplayName = "an adapter the service does not have")]
    [DataRow("id", DisplayName = "no host ID")]
    [DataRow("unhosted", DisplayName = "a service that runs its adapters itself")]
    public async Task AHost_IsRefused_UnlessItProvesItself_AndSpeaksALinkTheServiceTakes(string fault)
    {
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        await using var service = fault == "unhosted"
            ? new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "test-service", HostToken = InProcessHosts.Token }, [agent])
            : hosts.Conductor(TestDirs.New(), agent);
        var hello = fault switch
        {
            "token" => Hello(token: "a-guess"),
            "newer" => Hello(link: GorynychProtocol.HostLinkVersion + 1),
            "older" => Hello(link: GorynychProtocol.MinHostLinkVersion - 1),
            "adapter" => Hello(adapter: "nobody"),
            "id" => Hello(hostId: null),
            _ => Hello(),
        };
        await using var raw = new RawHost(service, hello, _ => new JsonObject());
        await raw.Rpc.Completion.WaitAsync(Wait);
        lock (raw.Asked) CollectionAssert.AreEqual(new[] { GorynychProtocol.MethodHostStop }, raw.Asked, "stopped, and not even initialized");
        Assert.IsNull(service.Agents["turns"].HostId);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHostOfAnotherVersion_OnTheSameLink_IsTakenBack_FromWhatItReports()
    {
        // As a host started by an older service would answer: the fields of host link 1 it has, no more.
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent);
        hosts.Service = service;
        await using var raw = new RawHost(service, Hello(), req => req.Method switch
        {
            AcpMethods.Agent.Initialize => new JsonObject
            {
                ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject(),
                ["agentInfo"] = new JsonObject { ["name"] = "old-agent", ["version"] = "0.0.1" },
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaHost] = new JsonObject { ["hostId"] = "old-host", ["link"] = 1, ["reattached"] = true } },
            },
            GorynychProtocol.MethodHostState => new JsonObject(),
            GorynychProtocol.MethodHostReplay => new JsonObject(),
            AcpMethods.Agent.SessionNew => new JsonObject { ["sessionId"] = "from-the-old-host" },
            _ => null,
        });
        await LinkHarness.Until(() => service.Agents["turns"].HostId == "old-host", "the host taken");
        await using var gui = await TestClient.ConnectAsync(service, "gui");
        Assert.AreEqual("from-the-old-host", await NewSession(gui), "requests go to it");
        Assert.AreEqual(0, hosts.Launches, "no host of its own started");
        lock (raw.Asked) CollectionAssert.AreEqual(new[] { AcpMethods.Agent.Initialize, GorynychProtocol.MethodHostState, GorynychProtocol.MethodHostReplay, AcpMethods.Agent.SessionNew }, raw.Asked);
        Assert.AreEqual("old-agent", (string?)service.Agents["turns"].Describe()["agentInfo"]?["name"]);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task WhatAHostSendsAtOrBeforeTheJournalsPlace_IsTakenOnce()
    {
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent);
        hosts.Service = service;
        await using var raw = new RawHost(service, Hello(), req => req.Method switch
        {
            AcpMethods.Agent.Initialize => new JsonObject { ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject() },
            _ => new JsonObject(),
        });
        await LinkHarness.Until(() => service.Agents["turns"].HostId == "old-host", "the host taken");
        Task Chunk(string text, int seq) => raw.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
        {
            ["sessionId"] = "s1",
            ["update"] = new JsonObject
            {
                ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaHostSeq] = $"old-host/{seq}" },
            },
        });
        Task Ended(int seq) => raw.Rpc.SendNotificationAsync(GorynychProtocol.MethodTurnEnded, new JsonObject
        {
            ["sessionId"] = "s1", ["promptId"] = "p1", ["turn"] = 1, ["result"] = new JsonObject { ["stopReason"] = "end_turn" },
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaHostSeq] = $"old-host/{seq}" },
        });
        await Chunk("a ", 5);
        await Chunk("a ", 5);
        await Chunk("b ", 4);
        await Chunk("c ", 6);
        await Ended(7);
        await Ended(7);
        await LinkHarness.Until(() => EndMarks(service, "s1") > 0, "the end");
        await Task.Delay(200);
        Assert.AreEqual("a c ", JournalText(service, "s1"));
        Assert.AreEqual(1, EndMarks(service, "s1"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task WhatAServiceTookWithoutJournalingIt_IsNotSentToTheNextOneAgain()
    {
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        var data = TestDirs.New();
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        var first = new RawHost(service, Hello(), req => req.Method switch
        {
            AcpMethods.Agent.Initialize => new JsonObject { ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject() },
            AcpMethods.Agent.SessionNew => new JsonObject { ["sessionId"] = "s1" },
            _ => new JsonObject(),
        });
        await LinkHarness.Until(() => service.Agents["turns"].HostId == "old-host", "the host taken");
        await using (var gui = await TestClient.ConnectAsync(service, "gui"))
        {
            Assert.AreEqual("s1", await NewSession(gui));
            Task Update(int seq, JsonObject update)
            {
                update["_meta"] = new JsonObject { [GorynychProtocol.MetaHostSeq] = $"old-host/{seq}" };
                return first.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject { ["sessionId"] = "s1", ["update"] = update });
            }
            await Update(6, new JsonObject { ["sessionUpdate"] = "session_info_update", ["title"] = "Tests" });
            // The list of commands is the session's state, not its history: taken, and not journaled.
            await Update(7, new JsonObject { ["sessionUpdate"] = "available_commands_update", ["availableCommands"] = new JsonArray() });
            await LinkHarness.Until(() => service.Store.Get("s1")?.Title == "Tests", "the title");
            await Task.Delay(200);
            Assert.AreEqual(6, service.Feed("s1").Journal.LastHostSeq("old-host"), "the journal ends at the title");
            await Prompt(gui, "s1", "keep it").WaitAsync(Wait); // no draft, which goes with its client
        }
        await RestartAsync(service);
        hosts.Service = null;
        await service.DisposeAsync();
        await first.DisposeAsync();

        await using var next = hosts.Conductor(data, agent);
        JsonNode? after = null;
        await using var back = new RawHost(next, Hello(), req =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize:
                    return new JsonObject
                    {
                        ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject(),
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaHost] = new JsonObject { ["hostId"] = "old-host", ["link"] = 1, ["reattached"] = true } },
                    };
                case GorynychProtocol.MethodHostState:
                    return new JsonObject { ["sessions"] = new JsonArray("s1"), ["buffered"] = new JsonArray("s1") };
                case GorynychProtocol.MethodHostReplay:
                    after = req.Params?["after"]?.DeepClone();
                    return new JsonObject();
                default:
                    return new JsonObject();
            }
        });
        await LinkHarness.Until(() => after is not null, "the replay asked for");
        Assert.AreEqual(7, (long)after!["s1"]!, "after what the old service took, though its journal ends at 6");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow("{\"token\":{},\"hostId\":\"h\",\"adapter\":\"turns\",\"link\":1}", DisplayName = "a token that is no string")]
    [DataRow("{\"token\":\"x\",\"hostId\":[1],\"adapter\":\"turns\",\"link\":1}", DisplayName = "a host ID that is no string")]
    [DataRow("{\"token\":\"x\",\"hostId\":\"h\",\"adapter\":7,\"link\":\"one\"}", DisplayName = "an adapter and a link of the wrong kinds")]
    public async Task AHelloOfTheWrongShape_IsRefused_AndItsConnectionClosed(string hello)
    {
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent);
        await using var raw = new RawHost(service, JsonNode.Parse(hello)!.AsObject(), _ => new JsonObject());
        await raw.Rpc.Completion.WaitAsync(Wait);
        lock (raw.Asked) CollectionAssert.DoesNotContain(raw.Asked, AcpMethods.Agent.Initialize);
        Assert.IsNull(service.Agents["turns"].HostId);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ATurnLeftToAHostThatDied_IsStillEndedAfterAServiceInBetweenThatNoHostReached()
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");
        zed.ConnectGate = new TaskCompletionSource(); // Zed stays away until the third service
        await RestartAsync(service);
        await GoneAsync(service, zed, hosts);
        var first = hosts["turns"];
        await hosts.KillAsync("turns");
        await first.Run.WaitAsync(Wait);
        agent.Release();

        // A service in between reads the session, so it has its feed, but no host of the adapter comes to it.
        var between = Next(data, hosts, zed, agent);
        await using (var reader = await TestClient.ConnectAsync(between, "reader"))
            await reader.Rpc.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = sid }).WaitAsync(Wait);
        Assert.AreEqual(1, hosts.Launches, "no host started for it");
        await RestartAsync(between);
        await GoneAsync(between, zed, hosts);
        Assert.AreEqual(1, between.Store.Get(sid)!.HostPrompts?.Count, "still kept");

        await using var third = Next(data, hosts, zed, agent);
        zed.ConnectGate.SetResult();
        Assert.AreEqual("cancelled", (string?)(await prompt.WaitAsync(Wait))!["stopReason"], "ended by the service the next host came to");
        Assert.AreEqual(1, EndMarks(third, sid));
        Assert.IsNull(third.Store.Get(sid)!.HostPrompts);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnEndAlreadyMarked_IsNotMarkedAgain_WhateverReportsItLater()
    {
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent);
        JsonObject Init(bool back) => new()
        {
            ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject(),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaHost] = new JsonObject { ["hostId"] = "h", ["link"] = 1, ["reattached"] = back } },
        };
        var first = new RawHost(service, Hello(hostId: "h1"), req => req.Method switch
        {
            AcpMethods.Agent.Initialize => Init(false),
            AcpMethods.Agent.SessionNew => new JsonObject { ["sessionId"] = "s1" },
            _ => new JsonObject(),
        });
        await LinkHarness.Until(() => service.Agents["turns"].HostId == "h1", "the first host");
        await using var gui = await TestClient.ConnectAsync(service, "gui");
        Assert.AreEqual("s1", await NewSession(gui));
        await first.DisposeAsync();
        await LinkHarness.Until(() => !service.Agents["turns"].IsRunning, "the first host gone");

        // A turn a service before left to the adapter's host; this host does not report it, so it is
        // ended cancelled, and the end the host reports after all is not marked again.
        service.Store.Update("s1", "turns", r => r.HostPrompts = ["p1"]);
        await using var second = new RawHost(service, Hello(hostId: "h2"), req => req.Method switch
        {
            AcpMethods.Agent.Initialize => Init(true),
            GorynychProtocol.MethodHostState => new JsonObject { ["sessions"] = new JsonArray("s1") },
            _ => new JsonObject(),
        });
        await LinkHarness.Until(() => service.Agents["turns"].HostId == "h2", "the second host");
        Assert.AreEqual(1, EndMarks(service, "s1"));
        Task Ended(int seq, string stop) => second.Rpc.SendNotificationAsync(GorynychProtocol.MethodTurnEnded, new JsonObject
        {
            ["sessionId"] = "s1", ["promptId"] = "p1", ["turn"] = 4, ["result"] = new JsonObject { ["stopReason"] = stop },
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaHostSeq] = $"h2/{seq}" },
        });
        await Ended(9, "end_turn");
        await Ended(10, "end_turn"); // and a second report, under another number
        await Task.Delay(300);
        Assert.AreEqual(1, EndMarks(service, "s1"));
        var mark = Journal(service, "s1").Single(u => (string?)u["_meta"]?[GorynychProtocol.MetaPromptId] == "p1" && u["_meta"]?[GorynychProtocol.MetaAgentTurn] is not null);
        Assert.AreEqual("cancelled", (string?)mark["_meta"]![GorynychProtocol.MetaStopReason]);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AServiceThatExitsInTheMiddleOfAHandshake_EndsNoTurnOfThatHost()
    {
        var agent = new TurnAgent("turns");
        var hosts = new InProcessHosts(_ => agent);
        var data = TestDirs.New();
        var service = hosts.Conductor(data, agent);
        service.Store.Update("s1", "turns", r => r.Title = "kept");
        await using var raw = new RawHost(service, Hello(hostId: "h2"), req =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize:
                    return new JsonObject
                    {
                        ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject(),
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaHost] = new JsonObject { ["hostId"] = "h2", ["link"] = 1, ["reattached"] = true } },
                    };
                case GorynychProtocol.MethodHostState:
                    return new JsonObject
                    {
                        ["sessions"] = new JsonArray("s1"),
                        ["turns"] = new JsonArray(new JsonObject { ["sessionId"] = "s1", ["turn"] = 3, ["promptId"] = "p1" }),
                    };
                default:
                    service.RequestShutdown(); // host_replay: the service exits meanwhile
                    Thread.Sleep(500);
                    return new JsonObject();
            }
        });
        await LinkHarness.Until(() => service.ShutdownRequested.IsCancellationRequested, "the exit");
        await Task.Delay(500);
        Assert.AreEqual(0, EndMarks(service, "s1"), "the host lives on with its turn, for the next service");
        await service.DisposeAsync();
        CollectionAssert.AreEqual(new[] { "p1" }, service.Store.Get("s1")!.HostPrompts!.ToArray(), "kept for the next one");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task UpdatesPastTheBuffersBytes_AreLetGo_AndTheJournalSaysItIsTrimmed()
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent) { BufferBytes = 4096 };
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var sid = await zed.NewSessionAsync().WaitAsync(Wait);
        var prompt = zed.PromptAsync(sid, "one");
        await LinkHarness.Until(() => zed.Text() == "started:one ", "the turn");
        await RestartAsync(service);
        await GoneAsync(service, zed, hosts);
        for (var i = 0; i < 10; i++)
            await agent.Current!.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = new string((char)('a' + i), 1000) } });
        await using var next = Next(data, hosts, zed, agent);
        await LinkHarness.Until(() => next.Feed(sid).Journal.Trimmed, "the journal to say it lacks some");
        StringAssert.EndsWith(JournalText(next, sid), new string('j', 1000), "the newest are there");
        agent.Release();
        Assert.AreEqual("end_turn", (string?)(await prompt.WaitAsync(Wait))!["stopReason"]);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AQuestionPastTheLimit_IsRefusedToTheAgent_WhileTheOthersWait()
    {
        var agent = new TurnAgent("turns");
        var instance = await agent.StartAsync(CancellationToken.None);
        var host = new AdapterHost(instance, _ => throw new IOException("no conductor"), new AdapterHostOptions
        {
            Adapter = "turns", Token = InProcessHosts.Token, QuestionLimit = 1, RetryDelays = [TimeSpan.FromMilliseconds(50)],
        });
        var run = host.RunAsync();
        Task<JsonNode?> Ask(string tool) => agent.Current!.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, new JsonObject
        {
            ["sessionId"] = "s1", ["toolCall"] = new JsonObject { ["toolCallId"] = tool, ["title"] = tool },
            ["options"] = new JsonArray(new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" }),
        });
        var waits = Ask("t1");
        await Task.Delay(200);
        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Ask("t2").WaitAsync(Wait));
        StringAssert.Contains(refused.Message, "1 questions of this agent waiting");
        Assert.IsFalse(waits.IsCompleted, "the first waits for a conductor");
        Assert.AreEqual(1, (int)host.Report()["requests"]!);
        await instance.DisposeAsync();
        await run.WaitAsync(Wait);
    }

    /// <summary>A host the test starts by hand: it may never connect, or exit before it does.</summary>
    sealed class HandLauncher : IHostLauncher
    {
        public int Launches;
        readonly System.Diagnostics.Stopwatch since = new();

        /// <summary>How long a host it starts runs, without ever connecting.</summary>
        public TimeSpan AliveFor { get; init; }

        public bool IsAlive(string adapter) => Launches > 0 && since.Elapsed < AliveFor;

        public void Launch(string adapter)
        {
            Interlocked.Increment(ref Launches);
            since.Restart();
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHostThatExitsBeforeItConnects_FailsTheRequestAtOnce_AndIsNotStartedAgainForAWhile()
    {
        var agent = new TurnAgent("turns");
        var launcher = new HandLauncher { AliveFor = TimeSpan.FromSeconds(1) };
        await using var service = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(), ServiceId = "test-service", Hosts = launcher, HostToken = InProcessHosts.Token,
        }, [agent]);
        await using var gui = await TestClient.ConnectAsync(service, "gui");
        var took = System.Diagnostics.Stopwatch.StartNew();
        var failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => NewSession(gui));
        Assert.IsLessThan(10, took.Elapsed.TotalSeconds, "not the 30 seconds of a host that never comes");
        StringAssert.Contains(failed.Message, "The turns host exited before it connected to Gorynych; its log is in Gorynych's logs folder (host-turns-*.log).");
        var again = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => NewSession(gui));
        StringAssert.Contains(again.Message, "Gorynych starts it again in");
        Assert.AreEqual(1, launcher.Launches, "not started again meanwhile");
    }

    [TestMethod]
    public void TheHostToken_IsTheUsersAlone_AndAnOldOneIsMadeSo()
    {
        var data = TestDirs.New();
        var token = HostNames.EnsureToken(data);
        Assert.AreEqual(64, token.Length);
        Assert.AreEqual(token, HostNames.EnsureToken(data), "kept");
        var path = HostNames.TokenPath(data);
        AssertOwnerOnly(path);

        // One an earlier version wrote, with the folder's rights.
        var old = TestDirs.New();
        File.WriteAllText(HostNames.TokenPath(old), new string('a', 64));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(HostNames.TokenPath(old), (UnixFileMode)0b110_100_100);
        Assert.AreEqual(new string('a', 64), HostNames.EnsureToken(old));
        AssertOwnerOnly(HostNames.TokenPath(old));
    }

    static void AssertOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            return;
        }
        var security = new FileInfo(path).GetAccessControl();
        Assert.IsTrue(security.AreAccessRulesProtected, "nothing inherited from the folder");
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToArray();
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.AreEqual(1, rules.Length, string.Join(", ", rules.Select(r => $"{r.IdentityReference} {r.FileSystemRights}")));
        Assert.AreEqual(me.User, rules[0].IdentityReference);
        Assert.AreEqual(System.Security.AccessControl.AccessControlType.Allow, rules[0].AccessControlType);
    }

    [TestMethod]
    public void TheHostCommand_NeedsAnAdapter_AndTheServiceTakesHostsOnOrOff()
    {
        var line = CommandLine.Parse(["host", "--adapter", "claude", "--pipe", "p", "--data", "d", "--claude-transport", "tui", "--no-peers"]);
        Assert.AreEqual("host", line.Command);
        Assert.AreEqual("claude", line.Value("--adapter"));
        Assert.IsTrue(line.Flag("--no-peers"));
        StringAssert.Contains(Assert.ThrowsExactly<UsageException>(() => CommandLine.Parse(["host"])).Message, "host needs --adapter");
        StringAssert.Contains(Assert.ThrowsExactly<UsageException>(() => CommandLine.Parse(["host", "--adapter", "zed"])).Message, "--adapter takes claude, kimi, codex, dsh");
        StringAssert.Contains(Assert.ThrowsExactly<UsageException>(() => CommandLine.Parse(["host", "--adapter", "kimi", "--default-agent", "kimi"])).Message, "host does not take '--default-agent'");
        Assert.AreEqual("off", CommandLine.Parse(["--hosts", "off"]).Value("--hosts"));
        StringAssert.Contains(Assert.ThrowsExactly<UsageException>(() => CommandLine.Parse(["--hosts", "maybe"])).Message, "--hosts takes on or off");
        StringAssert.Contains(CommandLine.Usage, "--hosts on|off");
    }
}
