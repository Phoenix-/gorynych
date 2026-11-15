using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Management;
using Gorynych.Service;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

/// <summary>
/// Safe management stops (docs/service-protocol.md, "Management stop and start"): the service enters
/// maintenance before it checks, refuses while turns, queued prompts and approvals run, unloads idle
/// sessions, stops the hosts, remembers the stop, and refuses to resurrect an adapter until start.
/// </summary>
[TestClass]
public sealed class ManagementTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    sealed class Rig : IAsyncDisposable
    {
        public readonly string Data = TestDirs.New();
        public readonly TurnAgent Agent = new("turns") { Closes = true, Tag = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..3]).ToLowerInvariant() + "-" };
        public readonly InProcessHosts Hosts;
        public readonly LinkHarness Zed;
        public Conductor Service;

        public Rig()
        {
            Hosts = new InProcessHosts(_ => Agent) { Generations = true };
            Service = Hosts.Conductor(Data, Agent);
            Hosts.Service = Service;
            Zed = new LinkHarness(Service);
        }

        public async Task<string> SessionAsync() => await Zed.NewSessionAsync().WaitAsync(Wait);

        public async ValueTask DisposeAsync()
        {
            Agent.Release();
            try { await Zed.DisposeAsync(); } catch (Exception e) { Console.Error.WriteLine(e); }
            try { await Service.DisposeAsync(); } catch (Exception e) { Console.Error.WriteLine(e); }
        }
    }

    static Task<JsonNode?> StopAsync(Rig rig, bool all = false, bool wait = false, double timeoutSeconds = 5, params string[] agents) =>
        rig.Zed.Zed.SendRequestAsync(GorynychProtocol.MethodStop, new JsonObject
        {
            ["all"] = all,
            ["wait"] = wait,
            ["timeoutSeconds"] = timeoutSeconds,
            ["agents"] = new JsonArray(agents.Select(a => (JsonNode)a).ToArray()),
        });

    static Task<JsonNode?> StartAsync(Rig rig, bool all = false, params string[] agents) =>
        rig.Zed.Zed.SendRequestAsync(GorynychProtocol.MethodStart, new JsonObject
        {
            ["all"] = all,
            ["agents"] = new JsonArray(agents.Select(a => (JsonNode)a).ToArray()),
        });

    static async Task Until(Func<bool> condition, string what, int ms = 10_000)
    {
        for (var waited = 0; waited < ms; waited += 20)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AStopWithNoWork_UnloadsIdleSessions_StopsTheHost_AndStartRestoresAvailability()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var session = await rig.SessionAsync();
        Assert.AreEqual("end_turn", (string?)(await rig.Zed.PromptAsync(session, "hello").WaitAsync(Wait))!["stopReason"]);
        var firstHost = rig.Hosts.Alive("turns").Single();
        Assert.IsNotNull(rig.Service.Status()["sessions"]);

        var stopped = await StopAsync(rig, agents: ["turns"]);
        Assert.AreEqual(true, (bool?)stopped?["stopped"], stopped?.ToJsonString());
        Assert.IsTrue(rig.Service.IsStopped("turns"), "the adapter is stopped");
        Assert.IsTrue(ManagementState.Read(rig.Data).IsStopped("turns"), "and the stop is remembered on disk");
        Assert.IsTrue(rig.Agent.EventsOf(session).Any(e => e.StartsWith("closed:", StringComparison.Ordinal)), "the idle session was unloaded");
        Assert.AreEqual(0, await firstHost.Run.WaitAsync(Wait), "the host exited");

        // A new session of the stopped adapter fails, and the stopped state does not start a host.
        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Zed.NewSessionAsync().WaitAsync(Wait));
        Assert.AreEqual("turns", (string?)refused.ErrorData?[GorynychProtocol.MetaStopped], refused.Message);
        Assert.AreEqual(0, rig.Hosts.Alive("turns").Length, "no host was started for the refused work");

        var started = await StartAsync(rig, agents: ["turns"]);
        Assert.AreEqual(true, (bool?)started?["started"]);
        Assert.IsFalse(rig.Service.IsStopped("turns"));
        var again = await rig.SessionAsync();
        Assert.IsNotNull(again);
        Assert.AreEqual(1, rig.Hosts.Alive("turns").Length, "start restores availability, and the next use starts a host");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AStopRefusesWhileATurnRuns_AndLeavesTheAdapterUsable()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var session = await rig.SessionAsync();
        rig.Agent.Hold = true;
        var turn = rig.Zed.PromptAsync(session, "long");
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:long"), "the running turn");

        var refused = await StopAsync(rig, agents: ["turns"]);
        Assert.AreEqual(false, (bool?)refused?["stopped"], refused?.ToJsonString());
        Assert.IsFalse(rig.Service.IsStopped("turns"), "a refused stop calls maintenance off");
        StringAssert.Contains(refused!.ToJsonString(), "a turn is running");
        Assert.AreEqual(1, rig.Hosts.Alive("turns").Length, "the host still runs");

        rig.Agent.Release();
        Assert.AreEqual("end_turn", (string?)(await turn.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("end_turn", (string?)(await rig.Zed.PromptAsync(session, "after").WaitAsync(Wait))!["stopReason"],
            "the adapter serves again after the refusal");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AStopThatWaits_CountsAQueuedPrompt_AndSucceedsAfterBothTurnsEnd()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var session = await rig.SessionAsync();
        rig.Agent.Hold = true;
        var first = rig.Zed.PromptAsync(session, "first");
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:first"), "the first turn");
        var second = rig.Zed.PromptAsync(session, "second"); // queued behind the turn
        await Task.Delay(100);

        var stopping = StopAsync(rig, wait: true, timeoutSeconds: 10, agents: ["turns"]);
        await Task.Delay(200);
        Assert.IsTrue(rig.Service.IsStopping("turns"), "maintenance is entered while the stop waits");
        var refusedNew = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Zed.NewSessionAsync().WaitAsync(Wait));
        Assert.AreEqual("turns", (string?)refusedNew.ErrorData?[GorynychProtocol.MetaStopped],
            "work that arrives during the stop is refused before it can start");

        rig.Agent.Release();
        Assert.AreEqual("end_turn", (string?)(await first.WaitAsync(Wait))!["stopReason"]);
        Assert.AreEqual("end_turn", (string?)(await second.WaitAsync(Wait))!["stopReason"]);
        var stopped = await stopping.WaitAsync(Wait);
        Assert.AreEqual(true, (bool?)stopped?["stopped"], stopped?.ToJsonString());
        Assert.AreEqual(2, rig.Agent.EventsOf(session).Count(e => e.StartsWith("prompt:", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AStopThatTimesOut_RecoversToAUsableService_AndCancelsNothing()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var session = await rig.SessionAsync();
        rig.Agent.Hold = true;
        var turn = rig.Zed.PromptAsync(session, "still-running");
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:still-running"), "the running turn");

        var stopped = await StopAsync(rig, wait: true, timeoutSeconds: 1, agents: ["turns"]);
        Assert.AreEqual(false, (bool?)stopped?["stopped"], stopped?.ToJsonString());
        StringAssert.Contains(stopped!.ToJsonString(), "a turn is running");
        Assert.IsFalse(rig.Service.IsStopping("turns"), "a timeout calls maintenance off");
        Assert.IsFalse(rig.Service.IsStopped("turns"));
        Assert.AreEqual(1, rig.Hosts.Alive("turns").Length, "the host was not stopped");

        rig.Agent.Release();
        Assert.AreEqual("end_turn", (string?)(await turn.WaitAsync(Wait))!["stopReason"], "the turn was not cancelled");
        Assert.AreEqual("end_turn", (string?)(await rig.Zed.PromptAsync(session, "again").WaitAsync(Wait))!["stopReason"],
            "the service works again after the timeout");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APendingApproval_BlocksTheStop_UntilItIsAnswered()
    {
        await using var rig = new Rig();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Zed.Approve = async (_, _) => { await answer.Task; return "allow"; };
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var session = await rig.SessionAsync();
        Assert.AreEqual("end_turn", (string?)(await rig.Zed.PromptAsync(session, "b1").WaitAsync(Wait))!["stopReason"]);
        var asked = rig.Agent.AgentOf(session).Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, new JsonObject
        {
            ["sessionId"] = session, ["toolCall"] = new JsonObject { ["toolCallId"] = "t1", ["title"] = "Write a file" },
            ["options"] = new JsonArray(new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" }),
        });
        await LinkHarness.Until(() => { lock (rig.Zed.Approvals) return rig.Zed.Approvals.Count == 1; }, "the approval");

        var refused = await StopAsync(rig, agents: ["turns"]);
        Assert.AreEqual(false, (bool?)refused?["stopped"]);
        StringAssert.Contains(refused!.ToJsonString(), "approval or form");
        Assert.IsFalse(rig.Service.IsStopped("turns"));

        answer.SetResult();
        Assert.AreEqual("allow", (string?)(await asked.WaitAsync(Wait))!["outcome"]!["optionId"]);
        var stopped = await StopAsync(rig, agents: ["turns"]);
        Assert.AreEqual(true, (bool?)stopped?["stopped"], stopped?.ToJsonString());
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task StopIsIdempotent_AndAStopAllStopsHostsAndIsRemembered()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        await rig.SessionAsync();
        var first = await StopAsync(rig, all: true, agents: []);
        Assert.AreEqual(true, (bool?)first?["stopped"]);
        Assert.AreEqual(true, (bool?)first?["serviceExits"]);
        var state = ManagementState.Read(rig.Data);
        Assert.IsTrue(state.StoppedAll, "stop --all is remembered");
        Assert.IsTrue(state.IsStopped("turns"));
        Assert.IsTrue(rig.Service.ShutdownRequested.IsCancellationRequested, "stop --all asks the service to exit");
        Assert.IsTrue(rig.Service.StopHostsOnExit, "its hosts stop with it");

        // A repeated stop is a no-op that still reports success.
        var again = await StopAsync(rig, all: true, agents: []);
        Assert.AreEqual(true, (bool?)again?["stopped"]);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AServiceStartedWithARememberedStop_ServesListsButStartsNothing()
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Closes = true };
        var hosts = new InProcessHosts(_ => agent) { Generations = true };
        var service = new Conductor(new ConductorOptions
        {
            DataDirectory = data, ServiceId = "test-service", Hosts = hosts, HostToken = InProcessHosts.Token,
            HostGeneration = _ => new HostGeneration("build-1", []),
            StoppedAgents = new HashSet<string>(["turns"], StringComparer.Ordinal),
        }, [agent]);
        hosts.Service = service;
        await using var zed = new LinkHarness(service);
        await zed.InitializeAsync().WaitAsync(Wait);
        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.NewSessionAsync().WaitAsync(Wait));
        Assert.AreEqual("turns", (string?)refused.ErrorData?[GorynychProtocol.MetaStopped]);
        Assert.AreEqual(0, hosts.Launches, "a remembered stop starts no host");
        var status = service.Status();
        Assert.IsNotNull(status["sessions"], "status still works");
        Assert.IsNotNull(status["processPath"]);
        Assert.IsFalse((bool?)status["maintenance"]?["stoppedAll"] == true, "the in-memory stop is per agent, not stopped-all");
        await service.DisposeAsync();
        await zed.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Review_RestartRefusesWhileManagementStopIsWaiting()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var session = await rig.SessionAsync();
        rig.Agent.Hold = true;
        var turn = rig.Zed.PromptAsync(session, "held");
        await LinkHarness.Until(() => rig.Zed.Text().Contains("started:held"), "the running turn");
        var stopping = StopAsync(rig, wait: true, timeoutSeconds: 0.5, agents: ["turns"]);
        await Until(() => rig.Service.IsStopping("turns"), "maintenance barrier");
        var restart = await rig.Zed.Zed.SendRequestAsync(GorynychProtocol.MethodRestart,
            new JsonObject { ["wait"] = false, ["force"] = false }).WaitAsync(Wait);
        Assert.AreEqual(false, (bool?)restart?["restarted"], "Restart must not race a management stop.");
        await stopping.WaitAsync(Wait);
        rig.Agent.Release();
        await turn.WaitAsync(Wait);
    }

    [TestMethod]
    public async Task Review_AnOperationWithoutASessionBlocksStop()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        using (rig.Service.RegisterAgentOperation("turns"))
        {
            var refused = await StopAsync(rig, agents: ["turns"]);
            Assert.AreEqual(false, (bool?)refused?["stopped"]);
            Assert.IsFalse(rig.Service.IsStopped("turns"));
        }
        Assert.AreEqual(true, (bool?)(await StopAsync(rig, agents: ["turns"]))?["stopped"]);
        Assert.ThrowsExactly<JsonRpcException>(() => rig.Service.RegisterAgentOperation("turns"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Review_InFlightCreationWithoutAFeedBlocksStop()
    {
        await using var rig = new Rig();
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var gate = rig.Agent.SlowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creating = rig.SessionAsync();
        try
        {
            await Until(() => Volatile.Read(ref rig.Agent.News) > 0, "native session/new");
            Assert.AreEqual(false, (bool?)(await StopAsync(rig, agents: ["turns"]))?["stopped"]);
            Assert.AreEqual(1, rig.Hosts.Alive("turns").Length, "the creating host must survive");
        }
        finally { gate.TrySetResult(); }
        await creating.WaitAsync(Wait);
        Assert.AreEqual(true, (bool?)(await StopAsync(rig, agents: ["turns"]))?["stopped"]);
    }

    [TestMethod]
    public async Task Review_ANonDurableDraftBlocksStopUntilItsTabCloses()
    {
        await using var rig = new Rig();
        rig.Agent.DraftsNotDurable = true;
        await rig.Zed.InitializeAsync().WaitAsync(Wait);
        var session = await rig.SessionAsync();
        var refused = await StopAsync(rig, agents: ["turns"]);
        Assert.AreEqual(false, (bool?)refused?["stopped"]);
        StringAssert.Contains(refused!.ToJsonString(), "unsaved draft");
        await rig.Zed.Zed.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = session });
        Assert.AreEqual(true, (bool?)(await StopAsync(rig, agents: ["turns"]))?["stopped"]);
    }

    [TestMethod]
    public void ManagementState_RoundTrips_AndAMalformedFileMeansNothingStopped()
    {
        var data = TestDirs.New();
        var state = ManagementState.Empty.WithStopped(["codex", "claude"]);
        ManagementState.Write(data, state);
        var read = ManagementState.Read(data);
        Assert.IsTrue(read.IsStopped("codex"));
        Assert.IsTrue(read.IsStopped("claude"));
        Assert.IsFalse(read.IsStopped("kimi"));
        File.WriteAllText(ManagementState.PathIn(data), "{not json");
        Assert.AreEqual(ManagementState.Empty, ManagementState.Read(data));
    }

    [TestMethod]
    public void APidIsTrustedOnlyWithItsStartTime_SoAReusedPidIsNotWaitedFor()
    {
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        Assert.IsFalse(ManagementCommand.WaitForExitAsync(Environment.ProcessId, started, TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult(),
            "this process still runs");
        Assert.IsTrue(ManagementCommand.WaitForExitAsync(Environment.ProcessId, started.AddHours(-1), TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(),
            "a different start time means the PID was reused, so the process is not the one that was stopped");
    }
}
