using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Service;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

public sealed partial class ClaudeHeadlessTests
{
    [TestMethod]
    [DataRow("disabled")]
    [DataRow("no_scope")]
    [DataRow("out_of_group")]
    [DataRow("background")]
    [DataRow("intervention")]
    [DataRow("timer")]
    [DataRow("quiet")]
    [DataRow("race")]
    [DataRow("late")]
    [DataRow("lost")]
    [DataRow("foreign_scope")]
    [DataRow("wrong_producer")]
    [DataRow("mutual")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task CompletionAutoUsesHostedGuardWithoutReplay(string scenario)
    {
        var dir = TestDirs.New(); var data = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        if (scenario == "background") File.WriteAllText(Path.Combine(dir, "observation-background-probe"), "");
        var metadata = new UiMetadataStore(Path.Combine(data, "ui-metadata.json"));
        metadata.Set("project", "group", "Group"); metadata.Set("project", "other", "Other");
        var instance = metadata.EnsureGroupInstance("project", "group")!;
        var otherInstance = metadata.EnsureGroupInstance("project", "other")!;
        var ledger = Path.Combine(dir, "auto-ledger.json"); var clock = new ManualTime();
        await using var launcher = new CompletionProcessHost(dir, ledger, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        Conductor Create(bool otherScope = false) => new(new ConductorOptions { DataDirectory = data, ServiceId = "auto-test", Time = clock,
            Hosts = launcher, HostToken = InProcessHosts.Token,
            CompletionConfiguration = scenario == "no_scope" ? null : otherScope ? ("project", "other", otherInstance) : ("project", "group", instance),
            CompletionAuto = scenario != "disabled", CompletionAutoSweepInterval = scenario == "timer" ? TimeSpan.FromSeconds(15) : TimeSpan.FromHours(1),
            TestCompletionLedgerPath = ledger, TestCompletionObservationTimeout = TimeSpan.FromMilliseconds(400) }, [adapter]);
        var service = Create(); launcher.Service = service; var client = await TestClient.ConnectAsync(service);
        var release = Path.Combine(dir, "release-completion-result");
        var holdsResult = scenario is "late" or "lost" or "foreign_scope" or "wrong_producer";
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var owner = await New(); var child = await New();
            service.Store.Update(owner, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = owner; r.ReportToParent = false; }, durable: true);
            async Task Group(string id, string group) => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                new JsonObject { ["sessionId"] = id, ["patch"] = new JsonObject {
                    [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "project", ["groupId"] = group } } });
            await Group(owner, "group"); await Group(child, scenario == "out_of_group" ? "other" : "group");
            var token = service.Store.Get(owner)!.PeerToken!;
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child,
                ["text"] = "initial delegation", ["_meta"] = new JsonObject {
                    [GorynychProtocol.MetaPeerToken] = token, [GorynychProtocol.MetaMessageRequestId] = "auto-assignment" } });
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("auto-assignment", owner)?["delivery"] == "delivered" &&
                service.Feed(child).TurnGate.CurrentCount == 1, "initial delivery").WaitAsync(TimeSpan.FromSeconds(20));
            var generation = (string)service.ReplyMailbox.FindAssignment("auto-assignment", owner)!["generation"]!;
            var route = service.Agents["claude"].RouteOf(child)!; Assert.AreEqual(launcher.Pid, route.ProcessId);
            await LinkHarness.Until(() => service.Feed(child).ClaudeObservationFor(route, "auto-assignment", generation) is not null,
                "trusted Claude observation").WaitAsync(TimeSpan.FromSeconds(20));
            var inputPath = Path.Combine(FakeRoot, child + ".inputs.jsonl");
            int Inputs() => File.ReadAllLines(inputPath).Length;
            Assert.AreEqual(1, Inputs());

            if (scenario == "timer")
            {
                // No explicit sweep: the TimeProvider timer drives the normal path.
                clock.Advance(TimeSpan.FromSeconds(60));
                await LinkHarness.Until(() => Inputs() == 2, "timer-driven reminder").WaitAsync(TimeSpan.FromSeconds(20));
                var timerClaim = service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation);
                Assert.IsNotNull(timerClaim);
                await service.SweepCompletionAutoAsync();
                Assert.AreEqual(2, Inputs());
                Assert.AreEqual("open", (string?)service.ReplyMailbox.FindAssignment("auto-assignment", owner)!["status"]);
                return;
            }

            if (scenario == "background")
            {
                File.WriteAllText(Path.Combine(dir, "emit-observation-background"), "");
                await LinkHarness.Until(() => service.Feed(child).HasBackgroundWork, "native background").WaitAsync(TimeSpan.FromSeconds(15));
            }
            if (scenario == "intervention") service.Feed(child).BeginAsking();

            string? other = null;
            if (scenario == "mutual")
            {
                other = await New();
                service.Store.Update(other, "claude", r => { r.Draft = false; r.ParentSessionId = owner; r.ReportToParent = false; }, durable: true);
                await Group(other, "group");
                service.ReplyMailbox.Accept("other-assignment", owner, other, "fixture", delegation: true);
                service.ReplyMailbox.MarkStarted("other-assignment", other); service.ReplyMailbox.MarkDelivered("other-assignment", other);
                async Task Wait(string assignment, string waiter, string recipient, string id)
                {
                    service.ReplyMailbox.Accept(id, waiter, recipient, "fixture", expectReply: true);
                    service.ReplyMailbox.MarkStarted(id, recipient); service.ReplyMailbox.MarkDelivered(id, recipient);
                    var a = service.ReplyMailbox.FindAssignment(assignment, waiter)!;
                    await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, new JsonObject {
                        ["source"] = "delegation", ["assignmentId"] = assignment, ["generation"] = (string?)a["generation"],
                        ["expectedRevision"] = 1, ["operationId"] = "op-" + id, ["action"] = "register", ["messageId"] = id,
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(waiter)!.PeerToken } });
                }
                await Wait("auto-assignment", child, other, "mutual-one");
                await Wait("other-assignment", other, child, "mutual-two");
            }
            clock.Advance(TimeSpan.FromSeconds(59));
            await service.SweepCompletionAutoAsync();
            Assert.AreEqual(1, Inputs(), "no input before 60 seconds");
            Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation));
            clock.Advance(TimeSpan.FromSeconds(1));

            if (holdsResult) File.WriteAllText(Path.Combine(dir, "hold-completion-result"), "");
            if (scenario == "race")
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var allow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                service.AfterCompletionOwnerClaimForTests = () => { entered.TrySetResult(); return allow.Task; };
                var auto = service.SweepCompletionAutoAsync();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    var held = service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation);
                    Assert.IsNotNull(held, "auto has committed the claim before manual admission");
                    var manual = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestCompletionReminder,
                        new JsonObject { ["assignmentId"] = "auto-assignment", ["generation"] = generation,
                            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } }).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.AreEqual("deferred", (string?)manual?["state"], manual?.ToJsonString());
                    Assert.AreEqual(held.AttemptId, service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation)?.AttemptId);
                    await service.SweepCompletionAutoAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.AreEqual(1, Inputs(), "manual and an overlapping sweep cannot write while the claim is held");
                }
                finally { allow.TrySetResult(); }
                await auto.WaitAsync(TimeSpan.FromSeconds(30));
            }
            else if (scenario == "lost")
            {
                var auto = service.SweepCompletionAutoAsync();
                await LinkHarness.Until(() => Inputs() == 2, "guarded input written").WaitAsync(TimeSpan.FromSeconds(20));
                // The native terminal is held; drop the actual host connection while
                // the automatic pass is waiting for its outcome.
                launcher.Service = null; launcher.BreakConnection();
                await LinkHarness.Until(() => route.Rpc.IsClosed, "host response lost").WaitAsync(TimeSpan.FromSeconds(15));
                await auto.WaitAsync(TimeSpan.FromSeconds(20));
            }
            else await service.SweepCompletionAutoAsync();

            if (scenario is "disabled" or "no_scope" or "out_of_group" or "background" or "intervention" or "mutual")
            {
                if (scenario == "intervention") service.Feed(child).EndAsking();
                Assert.AreEqual(1, Inputs()); Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation));
                if (scenario == "mutual")
                {
                    Assert.IsNotNull(other);
                    var otherGeneration = (string)service.ReplyMailbox.FindAssignment("other-assignment", owner)!["generation"]!;
                    Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("other-assignment", owner, other, otherGeneration));
                }
                return;
            }
            await LinkHarness.Until(() => Inputs() == 2, "guarded reminder reached the native process").WaitAsync(TimeSpan.FromSeconds(20));
            var claim = service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation);
            Assert.IsNotNull(claim); Assert.AreEqual(2, Inputs(), "one original prompt and one guarded reminder");
            Assert.AreEqual("open", (string?)service.ReplyMailbox.FindAssignment("auto-assignment", owner)!["status"]);

            if (holdsResult)
            {
                Assert.IsTrue(claim.Disposition is "handoff_started" or "uncertain", claim.Disposition);
                var saved = service.ReplyMailbox.ReadCompletionRecovery("auto-assignment", owner, child, generation, claim.AttemptId);
                Assert.IsNotNull(saved);
                if (scenario != "late")
                {
                    launcher.Service = null; launcher.BreakConnection();
                    await LinkHarness.Until(() => route.Rpc.IsClosed, "host disconnected").WaitAsync(TimeSpan.FromSeconds(15));
                    await service.SweepCompletionAutoAsync();
                    Assert.AreEqual(claim, service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation),
                        "a lost route cannot settle the attempt");
                    await client.DisposeAsync(); await service.DisposeAsync();
                    if (scenario == "wrong_producer")
                    {
                        // Inject a different producer binding into the persisted claim while the service is closed.
                        var mailboxPath = Path.Combine(data, "mailbox.json");
                        var mailbox = JsonNode.Parse(File.ReadAllText(mailboxPath))!;
                        mailbox["assignments"]!["auto-assignment"]!["completionReminder"]!["binding"]!["producerPid"] = launcher.Pid + 1;
                        File.WriteAllText(mailboxPath, mailbox.ToJsonString());
                    }
                    service = Create(otherScope: scenario == "foreign_scope");
                    launcher.Service = service; client = await TestClient.ConnectAsync(service);
                    await LinkHarness.Until(() => service.Agents["claude"].RouteOf(child) is { Current: true }, "host reattached").WaitAsync(TimeSpan.FromSeconds(20));
                }
                else
                {
                    await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, new JsonObject {
                        ["source"] = "delegation", ["assignmentId"] = "auto-assignment", ["generation"] = generation,
                        ["expectedRevision"] = 1, ["operationId"] = "auto-late-completed", ["outcome"] = "completed",
                        ["summary"] = "assignment completed before the native terminal arrived",
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(child)!.PeerToken } });
                }
                await service.SweepCompletionAutoAsync();
                var retained = service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation);
                Assert.IsNotNull(retained);
                Assert.AreEqual(claim.AttemptId, retained.AttemptId);
                Assert.IsTrue(retained.Disposition is "handoff_started" or "uncertain");
                File.WriteAllText(release, "");
                route = service.Agents["claude"].RouteOf(child)!;
                Assert.AreEqual(launcher.Pid, route.ProcessId);
                var shouldSettle = scenario is "late" or "lost";
                if (shouldSettle)
                {
                    // Only sweeps may retrieve and settle the late receipt in the positive cases.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (true)
                    {
                        await service.SweepCompletionAutoAsync(timeout.Token);
                        if (service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation)?.Disposition == "native_ended") break;
                        await Task.Delay(50, timeout.Token);
                    }
                }
                else
                {
                    var identity = CompletionHostLedger.Identity(saved.Source, saved.AssignmentId, saved.Owner, saved.Assignee,
                        saved.Generation, saved.OriginalRevision, saved.AttemptId, saved.Binding, saved.Stream, saved.Number);
                    await WaitForAutoTestTerminalAsync(route, identity);
                    await service.SweepCompletionAutoAsync();
                }
                await service.SweepCompletionAutoAsync();
                var after = service.ReplyMailbox.ReadCompletionReminder("auto-assignment", owner, child, generation);
                Assert.IsNotNull(after);
                Assert.AreEqual(claim.AttemptId, after.AttemptId);
                if (shouldSettle) Assert.AreEqual("native_ended", after.Disposition);
                else Assert.AreEqual(retained.Disposition, after.Disposition, "a foreign scope or producer mismatch keeps recovery pending");
                Assert.AreEqual(scenario == "late" ? "completed" : "open",
                    (string?)service.ReplyMailbox.FindAssignment("auto-assignment", owner)!["status"]);
                Assert.AreEqual(2, Inputs(), "every recovery pass must leave the native input count alone");
                Assert.AreEqual(1, InputCount(child, Guid.ParseExact(claim.AttemptId, "N").ToString("D")));
            }
            else
            {
                await service.SweepCompletionAutoAsync();
                Assert.AreEqual(2, Inputs(), "no repeated reminder");
            }
        }
        finally
        {
            File.WriteAllText(release, "");
            await client.DisposeAsync(); await service.DisposeAsync(); await launcher.CloseNativeGracefully();
        }
    }

    static async Task WaitForAutoTestTerminalAsync(HostLink route, JsonObject identity)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                var status = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedStatus,
                    new JsonObject { ["_meta"] = identity.DeepClone() }, timeout.Token);
                Assert.IsTrue(JsonNode.DeepEquals(status?["_meta"]?["identity"], identity));
                if ((string?)status?["_meta"]?["receipt"]?["cycleResult"] == "ended") return;
            }
            catch (JsonRpcException e) when (e.Message == "Guarded bridge busy") { }
            await Task.Delay(50, timeout.Token);
        }
    }

    [TestMethod]
    public void CompletionAutoFlagIsOptInAndPersisted()
    {
        var defaults = CommandLine.Parse(["serve"]);
        Assert.IsFalse(defaults.Flag("--completion-auto"));
        var enabled = CommandLine.Parse(["serve", "--completion-auto"]);
        Assert.IsTrue(enabled.Flag("--completion-auto"));
        CollectionAssert.Contains(enabled.PersistentServiceArguments(), "--completion-auto");
    }
}
