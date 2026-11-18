using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ConductorReportDeliveryTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    static async Task Until(Func<bool> check, string description) => await LinkHarness.Until(check, description).WaitAsync(Wait);
    static string Seed(Conductor service, string child, string parent, string text, string turn = "turn")
    {
        var id = service.ReportStorage.Reserve(new ReportIdentity(turn, child, parent, ReportCause.Delegation,
            "prompt-" + turn, "assignment", "message")).ReportId;
        using (var capture = service.ReportStorage.OpenCapture(id))
        { capture.Append(text); capture.Seal("ended"); service.ReportStorage.Commit(id, capture); }
        service.ReportStorage.AcknowledgeProducer(id);
        return id;
    }
    static async Task UntilReport(Conductor service, string id, Func<ReportInfo, bool> check, string description)
    {
        try { await Until(() => check(service.ReportStorage.GetReport(id)), description); }
        catch (Exception e) { var item = service.ReportStorage.GetReport(id); throw new AssertFailedException($"{description}: status={item.Status}, reason={item.DispositionReason}, delivery={item.Delivery}, health={service.Status()["_meta"]!["gorynych/reportRecovery"]}", e); }
    }
    static int ReportPrompts(TurnAgent agent) => agent.Prompts.Count(p => p.StartsWith("[Report ·", StringComparison.Ordinal));

    [TestMethod]
    public async Task ArchivedParentIsNotWokenAndRestoreDeliversOriginalParentOnce()
    {
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client); var other = await HostTests.NewSession(client);
        service.Store.Update(parent, "turns", r => r.Archived = true);
        service.Store.Update(child, "turns", r => { r.ParentSessionId = other; r.ReportToParent = true; });
        var id = Seed(service, child, parent, new string('x', 1999) + "😀tail");
        service.QueueReportDeliveries();
        await Until(() => service.ReportStorage.GetReport(id).DispositionReason?.Contains("archived") == true, "archived blocked");
        Assert.AreEqual(0, ReportPrompts(agent)); Assert.AreEqual("pending", service.ReportStorage.GetReport(id).Status);
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = parent, ["archived"] = false });
        await UntilReport(service, id, item => item.Status == "completed", "report delivered");
        var prompt = agent.Prompts.Single(p => p.StartsWith("[Report ·", StringComparison.Ordinal));
        StringAssert.Contains(prompt, id); StringAssert.Contains(prompt, "read_report"); StringAssert.Contains(prompt, "assignment");
        var preview = prompt[(prompt.IndexOf('\n') + 1)..];
        Assert.IsTrue(preview.Length <= 2000); Assert.IsFalse(char.IsHighSurrogate(preview[^1]));
        Assert.AreEqual(parent, service.ReportStorage.GetReport(id).Delivery!.Parent);
        service.QueueReportDeliveries(); await Until(() => !service.ReportDeliveryRunning(id), "idempotent delivery worker exits");
        Assert.AreEqual(1, ReportPrompts(agent)); Assert.AreEqual(0, service.ReportStorage.List(other).Items.Count);
    }

    [TestMethod]
    public async Task FailedClaimDoesNotDispatchAndCanRecover()
    {
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var id = Seed(service, child, parent, "answer");
        service.ReportStorage.WriteManifest = (path, json) =>
        {
            if (json.Contains("\"status\":\"claimed\"", StringComparison.Ordinal)) throw new IOException("injected claim fault");
            AtomicFile.WriteAllText(path, json);
        };
        service.QueueReportDeliveries();
        await UntilReport(service, id, item => item.DispositionReason?.Contains("IOException") == true, "claim fault visible");
        Assert.AreEqual(0, ReportPrompts(agent)); Assert.AreEqual("pending", service.ReportStorage.GetReport(id).Status);
        service.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
        service.QueueReportDeliveries();
        await UntilReport(service, id, item => item.Status == "completed", "retry succeeded");
        Assert.AreEqual(1, ReportPrompts(agent));
    }

    [TestMethod]
    public async Task DeletedParentFailsWithoutWakeAndChildCanRead()
    {
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var id = Seed(service, child, parent, "retained result");
        service.Store.Update(parent, "turns", r => r.Deleted = true);
        service.QueueReportDeliveries();
        await Until(() => service.ReportStorage.GetReport(id).Status == "failed", "deleted parent failed");
        Assert.AreEqual(0, ReportPrompts(agent)); Assert.AreEqual("retained result", service.ReportStorage.Read(id, child).Text);
    }

    [TestMethod]
    public async Task BusyParentDefersClaimUntilItsTurnEnds()
    {
        var agent = new TurnAgent("turns") { Hold = true }; var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var running = HostTests.Prompt(client, parent, "busy");
        await Until(() => agent.Prompts.Contains("busy"), "busy started");
        var id = Seed(service, child, parent, "result"); service.QueueReportDeliveries();
        await Until(() => service.Feed(parent).Users > 0, "delivery waits on parent");
        Assert.AreEqual("pending", service.ReportStorage.GetReport(id).Status); Assert.AreEqual(0, ReportPrompts(agent));
        agent.Release(); await running.WaitAsync(Wait);
        await UntilReport(service, id, item => item.Status == "completed", "delivery after busy turn");
        Assert.AreEqual(1, ReportPrompts(agent));
    }

    [TestMethod]
    public async Task InProcessAdapterStillPublishesIncompleteReport()
    {
        var agent = new TurnAgent("turns");
        await using var service = TestDirs.Conductor(TestDirs.New(), agent);
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(parent, "turns", r => r.Archived = true);
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        await HostTests.Prompt(client, child, "work").WaitAsync(Wait);
        var info = service.ReportStorage.List(child).Items.Single();
        Assert.AreEqual(ReportCause.User, info.Identity.Cause); Assert.IsNull(info.Identity.AssignmentId);
        var result = service.ReportStorage.Read(info.ReportId, child);
        StringAssert.Contains(result.IncompleteReason, "In-process"); Assert.AreEqual("ended", result.Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestartRecoversUnstartedClaimAndNeverReplaysStartedClaim(bool started)
    {
        var dir = TestDirs.New(); var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        var first = hosts.Conductor(dir, agent); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        await HostTests.Prompt(client, parent, "warmup");
        serviceArchive(first, parent);
        var id = Seed(first, child, parent, "restart result", "restart-" + started);
        var claim = first.ReportStorage.ClaimDelivery(id, parent, "host", "generation");
        if (started) first.ReportStorage.StartDelivery(id, claim.AttemptId);
        await HostTests.RestartAsync(first); hosts.Service = null;
        await first.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
        await using var next = hosts.Conductor(dir, agent); hosts.Service = next;
        if (started)
        {
            await UntilReport(next, id, x => x.Status == "uncertain", "started claim uncertain");
            Assert.AreEqual(0, ReportPrompts(agent));
            next.QueueReportDeliveries(); Assert.AreEqual("uncertain", next.ReportStorage.GetReport(id).Status);
        }
        else
        {
            await UntilReport(next, id, x => x.Status == "pending", "unstarted claim released");
            await using var restoredClient = await TestClient.ConnectAsync(next);
            await restoredClient.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = parent, ["archived"] = false });
            await UntilReport(next, id, x => x.Status == "completed", "restored report delivered");
            Assert.AreEqual(1, ReportPrompts(agent));
        }
        static void serviceArchive(Conductor service, string parent) => service.Store.Update(parent, "turns", r => r.Archived = true);
    }

    [TestMethod]
    public async Task PendingWithoutScheduledWorkerIsInventoriedAfterRestart()
    {
        var dir = TestDirs.New(); var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        var first = hosts.Conductor(dir, agent); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        await HostTests.Prompt(client, parent, "warmup");
        var id = Seed(first, child, parent, "pending across restart", "pending-restart");
        Assert.AreEqual("pending", first.ReportStorage.GetReport(id).Status);
        await HostTests.RestartAsync(first); hosts.Service = null;
        await first.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
        await using var next = hosts.Conductor(dir, agent); hosts.Service = next;
        await UntilReport(next, id, x => x.Status == "completed", "startup inventories the unscheduled pending report");
        Assert.AreEqual(1, ReportPrompts(agent));
    }

    [TestMethod]
    public async Task FailedTerminalPublicationRetainsReplayUntilHealed()
    {
        var agent = new TurnAgent("turns") { Hold = true }; var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var id = Seed(service, child, parent, "result", "terminal-fault");
        try
        {
            service.QueueReportDeliveries();
            await Until(() => ReportPrompts(agent) == 1, "delivery started");
            service.ReportStorage.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"status\":\"completed\"", StringComparison.Ordinal)) throw new IOException("terminal publication fault");
                AtomicFile.WriteAllText(path, json);
            };
            agent.Release();
            await UntilReport(service, id, x => x.Status == "uncertain", "failed publication remains uncertain");
            await Until(() => !service.ReportDeliveryRunning(id), "failed worker exits");
            var rpc = service.Agents["turns"].RouteOf(parent)!.Rpc;
            var after = new JsonObject { [parent] = 0 };
            await rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = after.DeepClone() }).WaitAsync(Wait);
            Assert.AreEqual("uncertain", service.ReportStorage.GetReport(id).Status);
            service.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
            await rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = after.DeepClone() }).WaitAsync(Wait);
            await UntilReport(service, id, x => x.Status == "completed", "terminal replay settled delivery");
            Assert.AreEqual(1, ReportPrompts(agent)); Assert.AreEqual(1, HostTests.EndMarks(service, parent));
        }
        finally { agent.Release(); }
    }

    [TestMethod]
    public async Task TerminalReplaysAfterLaterEventAndFreshConductor()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent); var first = hosts.Conductor(directory, agent); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first); Conductor? next = null; var firstDisposed = false;
        var releaseNewTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
            var native = agent.AgentOf(parent); var priorHandler = native.Rpc.RequestHandler!;
            native.Rpc.RequestHandler = async (request, ct) =>
            {
                var result = await priorHandler(request, ct);
                if (request.Method == AcpMethods.Agent.SessionPrompt && (string?)request.Params?["sessionId"] == parent && result is JsonObject obj)
                    obj["_meta"] = new JsonObject { [GorynychProtocol.MetaHistoryMark] = "old-delivery-mark" };
                return result;
            };
            var id = Seed(first, child, parent, "result", "later-cursor"); first.QueueReportDeliveries();
            await Until(() => ReportPrompts(agent) == 1, "delivery started");
            first.ReportStorage.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"status\":\"completed\"", StringComparison.Ordinal)) throw new IOException("terminal disposition fault");
                AtomicFile.WriteAllText(path, json);
            };
            agent.Release();
            await UntilReport(first, id, x => x.Status == "uncertain", "terminal failure retained");
            await Until(() => !first.ReportDeliveryRunning(id), "delivery worker ended");
            var link = first.Agents["turns"].RouteOf(parent)!; var hostId = link.HostId!;
            var before = first.Feed(parent).HostCursor(hostId);
            await native.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject { ["sessionId"] = parent,
                ["update"] = new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "later unrelated output" },
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaHistoryMark] = "new-native-mark" } } });
            await Until(() => first.Feed(parent).HostCursor(hostId) > before && first.Feed(parent).Journal.NativeMark == "new-native-mark", "later host event advanced cursor and history mark");
            agent.During = (_, sid, _) => sid == parent ? releaseNewTurn.Task : Task.CompletedTask;
            _ = HostTests.Prompt(client, parent, "new parent work");
            await Until(() => agent.Prompts.Contains("new parent work"), "new parent turn started");
            first.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
            await HostTests.RestartAsync(first); hosts.Service = null;
            await first.DisposeAsync(); await client.DisposeAsync(); firstDisposed = true; hosts.BreakAll();
            next = hosts.Conductor(directory, agent); hosts.Service = next;
            await UntilReport(next, id, x => x.Status == "completed", "retained real terminal replayed after restart");
            Assert.IsNotNull(next.Feed(parent).Adopted, "old terminal cannot end the newer adopted work");
            Assert.IsTrue(next.Feed(parent).TurnRunning, "new turn remains active");
            Assert.AreEqual("new-native-mark", next.Feed(parent).Journal.NativeMark, "older terminal cannot roll back native history");
            Assert.AreEqual(1, ReportPrompts(agent)); Assert.AreEqual(1, HostTests.EndMarks(next, parent));
            next.QueueReportDeliveries(); Assert.AreEqual("completed", next.ReportStorage.GetReport(id).Status);
        }
        finally
        {
            releaseNewTurn.TrySetResult(); agent.Release();
            if (next is not null) await next.DisposeAsync();
            if (!firstDisposed) { await client.DisposeAsync(); await first.DisposeAsync(); }
            hosts.BreakAll();
        }
    }

    [TestMethod]
    [DataRow("completed")]
    [DataRow("failed")]
    public async Task OldTerminalBypassRequiresEveryIdentityAndUnresolvedClaim(string resolvedStatus)
    {
        var agent = new TurnAgent("turns"); await using var service = TestDirs.Conductor(TestDirs.New(), agent);
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var other = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var id = Seed(service, child, parent, "result", "strict-terminal");
        var claim = service.ReportStorage.ClaimDelivery(id, parent, "host-id", "generation");
        service.ReportStorage.StartDelivery(id, claim.AttemptId);
        service.ReportStorage.SetDisposition(id, "uncertain", "handoff uncertain");
        JsonObject Terminal(string report, string attempt) => new() { ["_meta"] = new JsonObject {
            [GorynychProtocol.MetaReportDelivery] = new JsonObject { ["reportId"] = report, ["attemptId"] = attempt } } };
        Assert.IsTrue(service.HasUnresolvedDeliveryTerminal(Terminal(id, claim.AttemptId), parent, claim.PromptId, "host-id/3"));
        Assert.IsFalse(service.HasUnresolvedDeliveryTerminal(Terminal(id, claim.AttemptId), other, claim.PromptId, "host-id/3"));
        Assert.IsFalse(service.HasUnresolvedDeliveryTerminal(Terminal(id, claim.AttemptId), parent, "other-prompt", "host-id/3"));
        Assert.IsFalse(service.HasUnresolvedDeliveryTerminal(Terminal(id, claim.AttemptId), parent, claim.PromptId, "other-host/3"));
        Assert.IsFalse(service.HasUnresolvedDeliveryTerminal(Terminal(id, "other-attempt"), parent, claim.PromptId, "host-id/3"));
        Assert.IsFalse(service.HasUnresolvedDeliveryTerminal(Terminal("other-report", claim.AttemptId), parent, claim.PromptId, "host-id/3"));
        Assert.IsFalse(service.HasUnresolvedDeliveryTerminal(new JsonObject(), parent, claim.PromptId, "host-id/3"));
        service.ReportStorage.SetDisposition(id, resolvedStatus, "matching terminal evidence");
        Assert.IsFalse(service.HasUnresolvedDeliveryTerminal(Terminal(id, claim.AttemptId), parent, claim.PromptId, "host-id/3"));
        Assert.AreEqual(resolvedStatus, service.ReportStorage.GetReport(id).Status);
    }

    [TestMethod]
    public async Task BusyRecipientCannotOccupySlotsNeededByReadyRecipient()
    {
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var busyParent = await HostTests.NewSession(client); var readyParent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.During = (_, sid, _) => sid == busyParent ? release.Task : Task.CompletedTask;
        var busy = HostTests.Prompt(client, busyParent, "hold parent");
        try
        {
            await Until(() => agent.Prompts.Contains("hold parent"), "busy parent started");
            var ids = Enumerable.Range(0, 8).Select(i => Seed(service, child, busyParent, "blocked", "busy-" + i)).ToArray();
            service.QueueReportDeliveries();
            await Until(() => ids.All(id => service.ReportDeliveryRunning(id)), "eight busy-parent reports wait");
            var ready = Seed(service, child, readyParent, "ready", "ready-parent"); service.QueueReportDeliveries();
            await UntilReport(service, ready, x => x.Status == "completed", "ready parent completed while other parent busy");
            Assert.IsFalse(busy.IsCompleted); Assert.AreEqual(1, ReportPrompts(agent));
            Assert.IsTrue(ids.All(id => service.ReportStorage.GetReport(id).Status == "pending"));
        }
        finally { release.TrySetResult(); await busy.WaitAsync(Wait); }
    }

    [TestMethod]
    public async Task ReadOnlyChangeWhileWaitingForForwardGateReleasesUnstartedClaim()
    {
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var feed = service.Feed(parent); var id = Seed(service, child, parent, "result", "mode-recheck");
        await feed.ForwardGate.WaitAsync().WaitAsync(Wait); var held = true;
        try
        {
            service.QueueReportDeliveries();
            await UntilReport(service, id, x => x.Status == "claimed", "claim waits before native input");
            feed.SetReadOnly(new SessionFeed.ReadOnlyState("changed while waiting", null));
            feed.ForwardGate.Release(); held = false;
            await UntilReport(service, id, x => x.Status == "pending" && x.DispositionReason?.Contains("read-only") == true, "unstarted claim released");
            await Until(() => !service.ReportDeliveryRunning(id), "worker exited");
            Assert.AreEqual(0, ReportPrompts(agent)); Assert.IsFalse(service.ReportStorage.GetReport(id).Delivery!.DispatchStarted);
            feed.SetReadOnly(null); service.QueueReportDeliveries();
            await UntilReport(service, id, x => x.Status == "completed", "writable recipient receives report");
            Assert.AreEqual(1, ReportPrompts(agent));
        }
        finally { if (held) feed.ForwardGate.Release(); }
    }

    [TestMethod]
    public async Task LostHostResponseAfterNativeStartIsUncertainAndNotRepeated()
    {
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        await HostTests.Prompt(client, parent, "warmup");
        var id = Seed(service, child, parent, "uncertain result");
        agent.Hold = true;
        try
        {
            service.QueueReportDeliveries();
            await Until(() => ReportPrompts(agent) == 1, "parent native prompt started");
            hosts.BreakAll();
            await UntilReport(service, id, x => x.Status == "uncertain", "lost response retained as uncertain");
            service.QueueReportDeliveries();
            Assert.AreEqual("uncertain", service.ReportStorage.GetReport(id).Status);
            Assert.AreEqual(1, ReportPrompts(agent));
            // The disconnected host may not reconnect to this service. Keep uncertainty;
            // a later correlated terminal, if available, is a separate recovery boundary.
            Assert.AreEqual(1, ReportPrompts(agent));
        }
        finally { agent.Release(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedOrCancelledParentTurnDoesNotCompleteDelivery(bool cancel)
    {
        var agent = new TurnAgent("turns") { Hold = cancel }; var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        if (!cancel) agent.During = (_, sid, _) => sid == parent
            ? Task.FromException(new JsonRpcException(JsonRpcException.InternalError, "native report failure")) : Task.CompletedTask;
        var id = Seed(service, child, parent, "result", "parent-failure-" + cancel);
        try
        {
            service.QueueReportDeliveries();
            if (cancel)
            {
                await Until(() => ReportPrompts(agent) == 1, "parent report started");
                await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = parent });
            }
            await UntilReport(service, id, x => x.Status == "failed", "parent failure retained");
            Assert.AreEqual(1, ReportPrompts(agent));
            Assert.AreEqual("result", service.ReportStorage.Read(id, child).Text);
        }
        finally { agent.Release(); }
    }

    [TestMethod]
    public async Task PreStartFailureKeepsStableAttemptAndDoesNotDuplicateJournalOrNativeInput()
    {
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var id = Seed(service, child, parent, "result", "prestart");
        service.BeforeReportDeliveryStart = () => throw new IOException("local prestart fault");
        service.QueueReportDeliveries();
        await UntilReport(service, id, x => x.Status == "pending" && x.Delivery is not null && x.DispositionReason is not null, "unstarted claim released");
        await Until(() => !service.ReportDeliveryRunning(id), "failed worker exits");
        var attempt = service.ReportStorage.GetReport(id).Delivery!.AttemptId;
        Assert.AreEqual(0, ReportPrompts(agent));
        service.BeforeReportDeliveryStart = null; service.QueueReportDeliveries();
        await UntilReport(service, id, x => x.Status == "completed", "unstarted claim retried");
        Assert.AreEqual(attempt, service.ReportStorage.GetReport(id).Delivery!.AttemptId);
        var rows = service.Feed(parent).Journal.ReadAll().OfType<JsonObject>().Where(x =>
            (string?)x["messageId"] == "report-" + id && (string?)x["sessionUpdate"] == "user_message_chunk");
        Assert.AreEqual(1, rows.Count()); Assert.AreEqual(1, ReportPrompts(agent));
    }

    [TestMethod]
    public async Task CorrelatedHostTerminalSettlesUncertainClaimWithoutRedispatch()
    {
        var agent = new TurnAgent("turns") { Hold = true }; var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var id = Seed(service, child, parent, "result", "terminal-evidence");
        try
        {
            service.QueueReportDeliveries();
            await Until(() => ReportPrompts(agent) == 1, "delivery native prompt started");
            service.ReportStorage.SetDisposition(id, "uncertain", "simulated lost response");
            agent.Release();
            await UntilReport(service, id, x => x.Status == "completed", "correlated host terminal settled uncertainty");
            var info = service.ReportStorage.GetReport(id);
            StringAssert.Contains(info.DispositionReason, "host terminal"); Assert.AreEqual(1, ReportPrompts(agent));
        }
        finally { agent.Release(); }
    }

    [TestMethod]
    public async Task AdoptedReportOnlyTurnDoesNotReportToGrandparentAndTerminalSettlesClaim()
    {
        var dir = TestDirs.New(); var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent); var first = hosts.Conductor(dir, agent); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first); Conductor? next = null; var firstDisposed = false;
        try
        {
            var grandparent = await HostTests.NewSession(client); var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
            first.Store.Update(grandparent, "turns", r => r.Archived = true);
            first.Store.Update(parent, "turns", r => { r.ParentSessionId = grandparent; r.ReportToParent = true; });
            var id = Seed(first, child, parent, "adopted report", "adoption");
            first.QueueReportDeliveries();
            await Until(() => ReportPrompts(agent) == 1, "queued report started");
            await HostTests.RestartAsync(first); hosts.Service = null;
            await first.DisposeAsync(); await client.DisposeAsync(); firstDisposed = true; hosts.BreakAll();
            next = hosts.Conductor(dir, agent); hosts.Service = next;
            await Until(() => next.Feed(parent).Adopted is not null, "report-only turn adopted");
            agent.Release();
            await UntilReport(next, id, x => x.Status == "completed", "adopted host terminal settled delivery");
            Assert.AreEqual(0, next.ReportStorage.List(grandparent).Items.Count, "the report-only turn is never reported upward");
            Assert.AreEqual(1, ReportPrompts(agent));
        }
        finally
        {
            agent.Release();
            if (next is not null) await next.DisposeAsync();
            if (!firstDisposed) { await client.DisposeAsync(); await first.DisposeAsync(); }
            hosts.BreakAll();
        }
    }

    [TestMethod]
    public async Task ReceiptOnlyOlderHostUsesQueuedRouteWithoutNoteHandoff()
    {
        var agent = new TurnAgent("turns") { ReceiptOnlyCapability = true }; var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var route = service.Agents["turns"].RouteOf(parent)!;
        Assert.AreEqual(true, (bool?)route.Info.Meta?[GorynychProtocol.CapHostReportNote]?["supported"]);
        Assert.AreEqual(false, (bool?)route.Info.Meta?[GorynychProtocol.CapHostReportNote]?["durableRouting"]);
        var id = Seed(service, child, parent, "legacy host result", "legacy-note-capability"); service.QueueReportDeliveries();
        await UntilReport(service, id, x => x.Status == "completed", "older receipt-only host queued delivery");
        Assert.AreEqual("queued", service.ReportStorage.GetReport(id).Delivery?.Route);
        Assert.AreEqual(1, ReportPrompts(agent));
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task SettledReceiptCanReleasePinOnlyAfterExactHostProcessExit()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "$null = [Console]::In.ReadLine()" }) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new AssertFailedException("Could not start owned process fixture");
        var pid = process.Id; var start = process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        var id = Seed(service, child, parent, "retained", "host-exit-release"); var store = service.ReportStorage;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, parent, "owned-host", "generation", "note", digest, pid, start);
        store.StartDelivery(id, claim.AttemptId); store.BeginHostNote(id, parent, "owned-host", "generation", claim.AttemptId, "note");
        store.RecordNoteReceipt(id, parent, "owned-host", "generation", new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId,
            ["nativeMessageId"] = Guid.NewGuid().ToString(), ["revision"] = 1L, ["writeStarted"] = true, ["taken"] = true, ["cycleResult"] = "ended" });
        Assert.IsTrue(store.TrySettleNoteReceipt(id, parent, "owned-host", "generation", claim.AttemptId, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordNoteHostExitRelease(id, parent, "owned-host", "generation", claim.AttemptId, pid, "wrong start", 1));
        var unknownId = Seed(service, child, parent, "unknown", "host-exit-unknown");
        var unknown = store.ClaimDelivery(unknownId, parent, "owned-host", "generation", "note", digest, pid, start);
        store.StartDelivery(unknownId, unknown.AttemptId); store.BeginHostNote(unknownId, parent, "owned-host", "generation", unknown.AttemptId, "note");
        try
        {
            var actual = service.Agents["turns"].RouteOf(parent)!;
            var disconnected = new HostLink(actual.Rpc, actual.Info) { HostId = "owned-host", ProcessId = pid, ProcessStart = start };
            service.OnAgentStopped(service.Agents["turns"], disconnected, []);
            service.QueueReportDeliveries(parent);
            await Until(() => !service.ReportDeliveryRunning(id), "live process check completed");
            Assert.IsTrue(store.NoteNeedsAck(id, parent, "owned-host", "generation", claim.AttemptId), "live process or disconnect is not proof of exit");
            await process.StandardInput.WriteLineAsync("exit"); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(Wait);
            await Until(() => !store.NoteNeedsAck(id, parent, "owned-host", "generation", claim.AttemptId), "host-exit event released ledger pin");
            var result = store.GetReport(id);
            Assert.AreEqual("completed", result.Status); Assert.AreEqual("confirmed_host_exit", result.Delivery?.ReceiptReleaseReason);
            Assert.AreEqual("retained", store.Read(id, child).Text); Assert.AreEqual(0, ReportPrompts(agent));
            await Until(() => !service.ReportDeliveryRunning(unknownId), "unknown note reconciliation completed");
            Assert.AreEqual("uncertain", store.GetReport(unknownId).Status, "host exit alone is not evidence of native input or result");
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited) await process.WaitForExitAsync().WaitAsync(Wait);
        }
    }

}
