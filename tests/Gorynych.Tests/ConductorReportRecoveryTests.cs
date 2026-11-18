using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ConductorReportRecoveryTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SealedTurnSurvivesRestartAndReplayEvictionEvenWhenHostExits(bool exitHost)
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns") { Hold = true, Quiet = true };
        var hosts = new InProcessHosts(_ => agent) { BufferBytes = 1024 };
        var first = hosts.Conductor(directory, agent); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        first.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        first.Store.Update(parent, "turns", r => r.Archived = true);
        var prompt = client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = child,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "work" }) });
        await LinkHarness.Until(() => agent.Prompts.Contains("work"), "native turn started").WaitAsync(Wait);
        var id = first.ReportStorage.Recoverable().Items.Single().ReportId;
        var big = new string('x', 40000) + "😀";
        await agent.Current!.Update(child, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = big } });
        await LinkHarness.Until(() => HostTests.JournalText(first, child).Contains(big), "large output journaled").WaitAsync(Wait);
        await HostTests.RestartAsync(first);
        hosts.Service = null;
        await first.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
        await agent.Current!.Update(child, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "after restart" } });
        agent.Release();
        await LinkHarness.Until(() => first.ReportStorage.GetReport(id).ProducerEvidence == "sealed", "host sealed independently").WaitAsync(Wait);
        for (var n = 0; n < 8; n++)
            await agent.Current.Update("other", new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = new string('n', 1000) } });
        if (exitHost)
        {
            await hosts["turns"].Agent.DisposeAsync();
            await hosts["turns"].Run.WaitAsync(Wait);
        }
        await using var next = hosts.Conductor(directory, agent); hosts.Service = next;
        await LinkHarness.Until(() => next.ReportStorage.GetReport(id).ProducerAck && HostTests.EndMarks(next, child) == 1,
            "inventory imported sealed result and marked turn").WaitAsync(Wait);
        var report = next.ReportStorage.GetReport(id);
        Assert.AreEqual(parent, report.Identity.Parent); Assert.IsNotNull(report.Identity.HostId);
        string? cursor = null; var body = new System.Text.StringBuilder();
        do { var page = next.ReportStorage.Read(id, child, cursor); body.Append(page.Text); cursor = page.NextCursor; } while (cursor is not null);
        Assert.AreEqual(big + "after restart" + "done:work ", body.ToString());
        Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
        await next.ReconcileReportInventoryAsync();
        Assert.AreEqual(1, next.ReportStorage.List(child).Items.Count); Assert.AreEqual(1, HostTests.EndMarks(next, child));
        _ = prompt.Exception;
    }

    [TestMethod]
    public async Task BusyLiveProducerRemainsReservedDuringInventory()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent); await using var service = hosts.Conductor(directory, agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        service.Store.Update(parent, "turns", r => r.Archived = true);
        var prompt = HostTests.Prompt(client, child, "busy");
        try
        {
            await LinkHarness.Until(() => agent.Prompts.Contains("busy"), "live producer").WaitAsync(Wait);
            var id = service.ReportStorage.Recoverable().Items.Single().ReportId;
            await service.ReconcileReportInventoryAsync();
            var current = service.ReportStorage.GetReport(id);
            Assert.AreEqual("reserved", current.Status); Assert.IsFalse(current.ProducerAck);
            Assert.AreEqual("unconfirmed", current.ProducerEvidence); Assert.AreEqual(0, HostTests.EndMarks(service, child));
            Assert.IsFalse(prompt.IsCompleted); Assert.AreEqual(0, agent.Cancels);
        }
        finally { agent.Release(); }
        await prompt.WaitAsync(Wait);
    }
    [TestMethod]
    public async Task LiveLongTurnContinuesAfterReattachWithExactFinalText()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns") { Hold = true, Quiet = true };
        var hosts = new InProcessHosts(_ => agent) { BufferBytes = 1024 };
        var first = hosts.Conductor(directory, agent); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        first.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        first.Store.Update(parent, "turns", r => r.Archived = true);
        var prompt = HostTests.Prompt(client, child, "work");
        await LinkHarness.Until(() => agent.Prompts.Contains("work"), "native turn started").WaitAsync(Wait);
        var id = first.ReportStorage.Recoverable().Items.Single().ReportId;
        var big = new string('x', 40000) + "😀";
        await agent.Current!.Update(child, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = big } });
        await LinkHarness.Until(() => HostTests.JournalText(first, child).Contains(big), "large output journaled").WaitAsync(Wait);
        await HostTests.RestartAsync(first); hosts.Service = null;
        await first.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
        await agent.Current!.Update(child, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "middle" } });
        await using var next = hosts.Conductor(directory, agent); hosts.Service = next;
        await LinkHarness.Until(() => next.Feed(child).Adopted is not null, "live turn adopted").WaitAsync(Wait);
        await next.ReconcileReportInventoryAsync();
        Assert.IsFalse(next.ReportStorage.GetReport(id).ProducerAck);
        await agent.Current.Update(child, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "after" } });
        agent.Release();
        await LinkHarness.Until(() => next.ReportStorage.GetReport(id).ProducerAck && HostTests.EndMarks(next, child) == 1, "live result imported").WaitAsync(Wait);
        string? cursor = null; var body = new System.Text.StringBuilder();
        do { var page = next.ReportStorage.Read(id, child, cursor); body.Append(page.Text); cursor = page.NextCursor; } while (cursor is not null);
        Assert.AreEqual(big + "middle" + "after" + "done:work ", body.ToString());
        Assert.AreEqual(1, agent.Prompts.Count(p => p == "work")); Assert.AreEqual(0, agent.Cancels);
        _ = prompt.Exception;
    }

    [TestMethod]
    public async Task AdoptedReportOnlyTurnDoesNotReportToGrandparent()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns");
        var reportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hosts = new InProcessHosts(_ => agent); var first = hosts.Conductor(directory, agent); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first);
        var grandparent = await HostTests.NewSession(client); var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        first.Store.Update(parent, "turns", r => { r.ParentSessionId = grandparent; r.ReportToParent = true; });
        first.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        agent.During = async (_, sid, _) => { if (sid == parent) { reportStarted.TrySetResult(); await release.Task; } };
        try
        {
            await HostTests.Prompt(client, child, "work").WaitAsync(Wait);
            await reportStarted.Task.WaitAsync(Wait);
            await HostTests.RestartAsync(first); hosts.Service = null;
            await first.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
            await using var next = hosts.Conductor(directory, agent); hosts.Service = next;
            await LinkHarness.Until(() => next.Feed(parent).Adopted is not null, "report-only turn adopted").WaitAsync(Wait);
            Assert.AreEqual("report", next.Feed(parent).Adopted!.TurnKind);
            release.TrySetResult();
            await LinkHarness.Until(() => next.Feed(parent).Adopted is null, "report-only turn ended").WaitAsync(Wait);
            Assert.AreEqual(0, next.ReportStorage.List(parent).Items.Count(r => r.Identity.Child == parent));
            Assert.AreEqual(0, agent.EventsOf(grandparent).Count(e => e.StartsWith("prompt:", StringComparison.Ordinal)));
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    [DataRow("sealed")]
    [DataRow("failed")]
    [DataRow("unsealed")]
    [DataRow("corrupt")]
    [DataRow("gap-ended")]
    [DataRow("gap-cancelled")]
    [DataRow("gap-failed")]
    public async Task SeparateHostProcessExitReconcilesSealedOrHonestUnknown(string mode)
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns");
        // Manual inventory prevents startup recovery from racing the pre-import spool comparison.
        await using var service = new Conductor(new ConductorOptions { DataDirectory = directory, ServiceId = "process-probe" }, [agent]);
        const string child = "probe-child", parent = "probe-parent", prompt = "probe-prompt";
        service.Store.Update(parent, "turns", r => { r.Archived = true; r.Cwd = Path.GetTempPath(); });
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; r.Cwd = Path.GetTempPath(); });
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var root = output.Parent!.Parent!.Parent!.Parent!.FullName;
        var executable = "Gorynych.ReportHostProbe" + (OperatingSystem.IsWindows() ? ".exe" : "");
        var exe = Path.Combine(root, "Gorynych.ReportHostProbe", "bin", output.Parent.Name, output.Name, executable);
        var hostId = "probe-" + Guid.NewGuid().ToString("N");
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(directory, "reports"), hostId, child, prompt, mode is "unsealed" or "corrupt" ? "unsealed" : mode }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start test host");
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Assert.AreEqual("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(Wait));
            var identity = new ReportIdentity("process-turn-" + mode, child, parent, ReportCause.User, prompt, null, "message",
                hostId, "test-generation", process.Id, AgentHost.ProcessStartedAt(process.Id));
            var id = service.ReportStorage.Reserve(identity).ReportId;
            await process.StandardInput.WriteLineAsync(id); await process.StandardInput.FlushAsync();
            if (mode is "unsealed" or "corrupt")
            {
                Assert.AreEqual("STARTED", await process.StandardOutput.ReadLineAsync().WaitAsync(Wait));
                await LinkHarness.Until(() => File.Exists(service.ReportStorage.CapturePath(id)) && new FileInfo(service.ReportStorage.CapturePath(id)).Length > 0,
                    "host persisted partial output").WaitAsync(Wait);
                await process.StandardInput.WriteLineAsync("STOP"); await process.StandardInput.FlushAsync();
            }
            var gap = mode.StartsWith("gap-", StringComparison.Ordinal);
            Assert.AreEqual(gap ? "SEALED_NO_EVIDENCE" : "EXITED", await process.StandardOutput.ReadLineAsync().WaitAsync(Wait));
            await process.WaitForExitAsync().WaitAsync(Wait);
            Assert.AreEqual(gap ? 42 : 0, process.ExitCode, await stderr);
            var reopenedStore = new ReportStore(Path.Combine(directory, "reports"));
            string? sealedText = null, sealedOutcome = null, sealedError = null;
            var sealedHasText = false;
            if (gap)
            {
                using var candidate = reopenedStore.OpenCapture(id);
                var snapshot = candidate.Snapshot(); Assert.IsTrue(snapshot.Sealed);
                sealedText = snapshot.Text; sealedOutcome = snapshot.Outcome; sealedError = snapshot.Error; sealedHasText = snapshot.HasText;
            }
            if (mode == "corrupt") File.AppendAllText(service.ReportStorage.CapturePath(id), "torn");
            await service.ReconcileReportInventoryAsync();
            var recoveredStore = new ReportStore(Path.Combine(directory, "reports"));
            var info = recoveredStore.GetReport(id); Assert.IsTrue(info.ProducerAck);
            var result = recoveredStore.Read(id, child);
            if (mode == "sealed") { Assert.AreEqual("process-output-😀", result.Text); Assert.IsNull(result.IncompleteReason); Assert.AreEqual("ended", result.Outcome); }
            else if (mode == "failed") { Assert.AreEqual("ended", result.Outcome); StringAssert.Contains(result.IncompleteReason!, "seal failure"); }
            else if (gap)
            {
                Assert.AreEqual("process-output-😀", result.Text); Assert.IsTrue(result.HasText);
                Assert.AreEqual(sealedText, result.Text); Assert.AreEqual(sealedHasText, result.HasText);
                Assert.AreEqual(sealedOutcome, result.Outcome); Assert.AreEqual(sealedError, result.Error);
                Assert.AreEqual(mode[4..], result.Outcome);
                Assert.AreEqual(ReportStore.MissingProducerEvidence, result.CaptureConfidenceReason);
                Assert.AreEqual(ReportStore.MissingProducerEvidence, result.IncompleteReason);
                Assert.AreEqual("pending", result.Status);
                if (mode == "gap-failed") StringAssert.Contains(result.Error!, "native failure at seal");
                else Assert.IsNull(result.Error);
                await service.ReconcileReportInventoryAsync(); service.ImportReport(id);
                var repeated = new ReportStore(Path.Combine(directory, "reports")).Read(id, child);
                Assert.AreEqual(result.Text, repeated.Text); Assert.AreEqual(result.HasText, repeated.HasText);
                Assert.AreEqual(result.Outcome, repeated.Outcome); Assert.AreEqual(result.Error, repeated.Error);
                Assert.AreEqual(result.IncompleteReason, repeated.IncompleteReason); Assert.AreEqual(result.Status, repeated.Status);
                Assert.AreEqual(result.CaptureConfidenceReason, repeated.CaptureConfidenceReason);
            }
            else { Assert.AreEqual("unknown", result.Outcome); Assert.IsNotNull(result.IncompleteReason); }
            Assert.AreEqual(1, HostTests.EndMarks(service, child));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("STOP"); process.StandardInput.Close(); } catch (IOException) { }
                await process.WaitForExitAsync().WaitAsync(Wait);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestartRecoversAfterCommitBeforeAckOrAckBeforeJournal(bool journalFault)
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        var first = hosts.Conductor(directory, agent); hosts.Service = first; var client = await TestClient.ConnectAsync(first);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        first.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        first.Store.Update(parent, "turns", r => r.Archived = true);
        if (journalFault)
            first.Feed(child).Journal.BeforeWrite = updates =>
            {
                if (updates.Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended")) throw new IOException("end journal fault");
            };
        else agent.During = (_, _, _) =>
        {
            first.ReportStorage.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"producerAck\":true", StringComparison.Ordinal)) throw new IOException("ack fault");
                AtomicFile.WriteAllText(path, json);
            };
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await HostTests.Prompt(client, child, "work").WaitAsync(Wait));
        StringAssert.Contains(error.Message, journalFault ? "journal mark failed" : "durably imported");
        var info = first.ReportStorage.List(child).Items.Single();
        Assert.AreEqual("pending", info.Status); Assert.AreEqual(journalFault, info.ProducerAck);
        Assert.AreEqual(0, HostTests.EndMarks(first, child));
        await HostTests.RestartAsync(first); hosts.Service = null;
        await first.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
        await using var next = hosts.Conductor(directory, agent); hosts.Service = next;
        await LinkHarness.Until(() => next.ReportStorage.GetReport(info.ReportId).ProducerAck && HostTests.EndMarks(next, child) == 1,
            "import and journal recovered").WaitAsync(Wait);
        await next.ReconcileReportInventoryAsync();
        Assert.AreEqual(1, HostTests.EndMarks(next, child)); Assert.AreEqual(1, next.ReportStorage.List(child).Items.Count);
        Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
    }

    [TestMethod]
    public async Task LostProducerEvidenceCannotBecomeCompleteAfterRestart()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        var first = hosts.Conductor(directory, agent); hosts.Service = first; var client = await TestClient.ConnectAsync(first);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        first.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        first.Store.Update(parent, "turns", r => r.Archived = true);
        agent.During = (_, _, _) =>
        {
            first.ReportStorage.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"producerEvidence\":\"sealed\"", StringComparison.Ordinal)) throw new IOException("evidence write fault");
                AtomicFile.WriteAllText(path, json);
            };
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await HostTests.Prompt(client, child, "work").WaitAsync(Wait));
        StringAssert.Contains(error.Message, "durably imported");
        var pending = first.ReportStorage.Recoverable().Items.Single();
        Assert.AreEqual("unconfirmed", pending.ProducerEvidence);
        Assert.IsGreaterThan(0, (int)first.Status()["_meta"]!["gorynych/reportRecovery"]!["unconfirmed"]!);
        using (var capture = first.ReportStorage.OpenCapture(pending.ReportId)) Assert.IsTrue(capture.Snapshot().Sealed);
        await HostTests.RestartAsync(first); hosts.Service = null;
        await first.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
        await using var next = hosts.Conductor(directory, agent); hosts.Service = next;
        await next.ReconcileReportInventoryAsync();
        var still = next.ReportStorage.GetReport(pending.ReportId);
        Assert.AreEqual("reserved", still.Status); Assert.IsFalse(still.ProducerAck);
        Assert.ThrowsExactly<InvalidOperationException>(() => next.ImportReport(still.ReportId));
        Assert.AreEqual(0, HostTests.EndMarks(next, child));
    }

    [TestMethod]
    public async Task LiveReportNoteDoesNotEraseActiveUserTurnEligibility()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(directory, agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var grandparent = await HostTests.NewSession(client); var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(grandparent, "turns", r => r.Archived = true);
        service.Store.Update(parent, "turns", r => { r.ParentSessionId = grandparent; r.ReportToParent = true; });
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var noted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.During = async (_, sid, _) => { if (sid == parent) { held.TrySetResult(); await release.Task; } };
        agent.DuringNote = sid => { if (sid == parent) noted.TrySetResult(); return Task.CompletedTask; };
        var parentPrompt = HostTests.Prompt(client, parent, "real work");
        try
        {
            await held.Task.WaitAsync(Wait);
            // C3a's queued outbox does not yet use C2 live receipts. Inject a live note
            // directly to preserve the independent B3 eligibility regression.
            var route = service.Agents["turns"].RouteOf(parent)!;
            await route.Rpc.SendRequestAsync(GorynychProtocol.MethodNote, new JsonObject { ["sessionId"] = parent, ["text"] = "live report note" });
            await noted.Task.WaitAsync(Wait);
            await HostTests.Prompt(client, child, "child work").WaitAsync(Wait);
            Assert.IsFalse(parentPrompt.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await parentPrompt.WaitAsync(Wait);
        var report = service.ReportStorage.List(parent).Items.Single(r => r.Identity.Child == parent);
        Assert.AreEqual(ReportCause.User, report.Identity.Cause); Assert.IsTrue(report.ProducerAck);
        Assert.AreEqual(0, agent.Cancels);
    }

    [TestMethod]
    public async Task PreexistingLegacyTurnWithUnknownProvenanceIsVisibleAndNotInvented()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns") { Hold = true };
        var hosts = new InProcessHosts(_ => agent) { CaptureReports = false };
        var first = hosts.Conductor(directory, agent); hosts.Service = first; var client = await TestClient.ConnectAsync(first);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        first.Store.Update(parent, "turns", r => r.Archived = true);
        first.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        var rpc = first.Agents["turns"].RouteOf(child)!.Rpc;
        var oldPrompt = rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = child,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "old work" }),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "old-unobserved-prompt" } });
        await LinkHarness.Until(() => agent.Prompts.Contains("old work"), "preexisting turn").WaitAsync(Wait);
        await HostTests.RestartAsync(first); hosts.Service = null;
        await first.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
        await using var next = hosts.Conductor(directory, agent); hosts.Service = next;
        await LinkHarness.Until(() => next.Feed(child).Adopted is not null, "legacy turn adopted").WaitAsync(Wait);
        Assert.AreEqual("unknown", next.Feed(child).Adopted!.TurnKind);
        agent.Release();
        await LinkHarness.Until(() => next.Feed(child).Adopted is null, "legacy turn ended").WaitAsync(Wait);
        Assert.AreEqual(0, next.ReportStorage.List(child).Items.Count);
        Assert.IsTrue(next.Feed(child).Journal.ReadAll().OfType<JsonObject>().Any(u =>
            ((string?)u["_meta"]?[GorynychProtocol.MetaReportCapture]?["failure"])?.Contains("unknown provenance", StringComparison.Ordinal) == true));
        _ = oldPrompt.Exception;
    }

    [TestMethod]
    public async Task EndJournalEvidencePreventsRepublishingAfterJournalEviction()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        Conductor NewService() => new(new ConductorOptions { DataDirectory = directory, ServiceId = "small-journal",
            Hosts = hosts, HostToken = InProcessHosts.Token, JournalMaxEntries = 16 }, [agent]);
        var first = NewService(); hosts.Service = first; var client = await TestClient.ConnectAsync(first);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        first.Store.Update(parent, "turns", r => r.Archived = true);
        first.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        await HostTests.Prompt(client, child, "work").WaitAsync(Wait);
        var report = first.ReportStorage.List(child).Items.Single(); Assert.IsTrue(report.EndJournaled);
        for (var i = 0; i < 40; i++)
            await first.Feed(child).PublishAsync(new JsonObject { ["sessionUpdate"] = "session_info_update", ["title"] = "title-" + i }, null, () => { });
        Assert.AreEqual(0, HostTests.EndMarks(first, child), "bounded journal has evicted the end row");
        await HostTests.RestartAsync(first); hosts.Service = null;
        await first.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
        await using var next = NewService(); hosts.Service = next;
        await next.ReconcileReportInventoryAsync();
        Assert.IsTrue(next.ReportStorage.GetReport(report.ReportId).EndJournaled);
        Assert.AreEqual(0, HostTests.EndMarks(next, child), "inventory did not publish a duplicate end");
        Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
    }

    [TestMethod]
    public async Task EndEvidenceWriteFailureReplaysWithoutDuplicatingJournalMark()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(directory, agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(parent, "turns", r => r.Archived = true);
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        agent.During = (_, _, _) =>
        {
            service.ReportStorage.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"endJournaled\":true", StringComparison.Ordinal)) throw new IOException("end evidence fault");
                AtomicFile.WriteAllText(path, json);
            };
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await HostTests.Prompt(client, child, "work").WaitAsync(Wait));
        StringAssert.Contains(error.Message, "end evidence fault");
        var info = service.ReportStorage.List(child).Items.Single();
        Assert.IsTrue(info.ProducerAck); Assert.IsFalse(info.EndJournaled); Assert.AreEqual(1, HostTests.EndMarks(service, child));
        service.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
        var rpc = service.Agents["turns"].RouteOf(child)!.Rpc;
        await rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject { [child] = 0 } }).WaitAsync(Wait);
        Assert.IsTrue(service.ReportStorage.GetReport(info.ReportId).EndJournaled);
        Assert.AreEqual(1, HostTests.EndMarks(service, child)); Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
    }

    [TestMethod]
    public async Task AgentInitiatedTurnExposesObservationGapWithoutInventingAResult()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        await using var service = hosts.Conductor(directory, agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        await agent.Current!.Update(child, new SessionInfoUpdate { Meta = new JsonObject { [GorynychProtocol.MetaAgentTurn] = "started" } });
        await LinkHarness.Until(() => service.Feed(child).Journal.ReadAll().OfType<JsonObject>().Any(u =>
            ((string?)u["_meta"]?[GorynychProtocol.MetaReportCapture]?["failure"])?.Contains("agent-initiated", StringComparison.Ordinal) == true),
            "native turn observation gap").WaitAsync(Wait);
        await agent.Current.Update(child, new SessionInfoUpdate { Meta = new JsonObject { [GorynychProtocol.MetaAgentTurn] = "ended" } });
        Assert.AreEqual(0, service.ReportStorage.List(child).Items.Count);
        Assert.AreEqual(0, agent.Prompts.Count);
    }

    [TestMethod]
    public void OlderCleanupCannotReleaseANewerAdoptedTurn()
    {
        var feed = new SessionFeed("s", new SessionJournal(Path.Combine(TestDirs.New(), "journal"), 100));
        var old = new SessionFeed.AdoptedTurn(1, "prompt", "agent", 1, "old-host", "work", "old-report");
        var current = new SessionFeed.AdoptedTurn(2, "prompt", "agent", 1, "new-host", "work", "new-report");
        Assert.IsTrue(feed.Adopt(current));
        Assert.IsNull(feed.EndAdoptedIf(old)); Assert.AreSame(current, feed.Adopted);
        Assert.AreSame(current, feed.EndAdoptedIf(current));
    }

    [TestMethod]
    public async Task LiveHostBetweenSealAndEvidenceRemainsUnconfirmedUntilEvidenceCommits()
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var hosts = new InProcessHosts(_ => agent) { BeforeProducerEvidence = () =>
        {
            entered.Set(); if (!release.Wait(Wait)) throw new IOException("evidence barrier timed out");
        } };
        await using var service = hosts.Conductor(directory, agent); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(parent, "turns", r => r.Archived = true);
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        service.Store.Flush();
        // This second conductor has its own empty admittedReports and independent store instance.
        await using var recovery = new Conductor(new ConductorOptions { DataDirectory = directory, ServiceId = "recovery-only" }, [agent]);
        var prompt = HostTests.Prompt(client, child, "work");
        string? observedText = null;
        string? reportId = null;
        try
        {
            Assert.IsTrue(await Task.Run(() => entered.Wait(Wait)));
            reportId = service.ReportStorage.Recoverable().Items.Single().ReportId;
            using (var capture = recovery.ReportStorage.OpenCapture(reportId))
            { var snapshot = capture.Snapshot(); Assert.IsTrue(snapshot.Sealed); Assert.AreEqual("ended", snapshot.Outcome); observedText = snapshot.Text; }
            Assert.IsFalse(hosts["turns"].Run.IsCompleted, "the actual producer is still live");
            await recovery.ReconcileReportInventoryAsync();
            var pending = recovery.ReportStorage.GetReport(reportId);
            Assert.AreEqual("reserved", pending.Status); Assert.AreEqual("unconfirmed", pending.ProducerEvidence);
            Assert.IsFalse(pending.ProducerAck); Assert.AreEqual(0, HostTests.EndMarks(recovery, child));
            Assert.IsFalse(prompt.IsCompleted);
        }
        finally { release.Set(); }
        await prompt.WaitAsync(Wait);
        var result = recovery.ReportStorage.GetReport(reportId!);
        Assert.IsTrue(result.ProducerAck); Assert.AreEqual("sealed", result.ProducerEvidence);
        var stored = recovery.ReportStorage.Read(result.ReportId, child);
        Assert.AreEqual(observedText, stored.Text); Assert.AreEqual("ended", stored.Outcome);
        Assert.IsNull(stored.IncompleteReason); Assert.IsNull(stored.CaptureConfidenceReason);
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ActualAdapterHostSelfExitReleasesOnlySettledReceipt(bool settled)
    {
        var directory = TestDirs.New(); var agent = new TurnAgent("turns");
        var first = new Conductor(new ConductorOptions { DataDirectory = directory, ServiceId = "note-process" }, [agent]);
        const string child = "probe-child", parent = "probe-parent";
        first.Store.Update(parent, "turns", r => { r.Archived = true; r.Draft = false; r.Cwd = Path.GetTempPath(); });
        first.Store.Update(child, "turns", r => { r.Draft = false; r.Cwd = Path.GetTempPath(); });
        var id = first.ReportStorage.Reserve(new ReportIdentity("note-process-" + settled, child, parent, ReportCause.User, "source")).ReportId;
        using (var capture = first.ReportStorage.OpenCapture(id)) { capture.Append("immutable host result"); capture.Seal("ended"); first.ReportStorage.Commit(id, capture); }
        first.ReportStorage.AcknowledgeProducer(id);
        var output = new DirectoryInfo(AppContext.BaseDirectory); var root = output.Parent!.Parent!.Parent!.Parent!.FullName;
        var exe = Path.Combine(root, "Gorynych.ReportHostProbe", "bin", output.Parent.Name, output.Name,
            "Gorynych.ReportHostProbe" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var host = "note-probe-" + Guid.NewGuid().ToString("N");
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(directory, "reports"), host, parent, "unused", settled ? "note-settled" : "note-unsettled" }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start owned host");
        var stderr = process.StandardError.ReadToEndAsync(); var firstDisposed = false;
        try
        {
            Assert.AreEqual("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(Wait));
            var generation = new Gorynych.Service.Hosts.HostGeneration("note-probe", []).Key;
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("probe note")));
            var claim = first.ReportStorage.ClaimDelivery(id, parent, host, generation, "note", digest, process.Id, AgentHost.ProcessStartedAt(process.Id));
            first.ReportStorage.StartDelivery(id, claim.AttemptId);
            await process.StandardInput.WriteLineAsync(new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId }.ToJsonString());
            await process.StandardInput.FlushAsync(); Assert.AreEqual("NOTE_WRITTEN", await process.StandardOutput.ReadLineAsync().WaitAsync(Wait));
            var facts = first.ReportStorage.ReadNoteReceipt(id, parent, host, generation, claim.AttemptId)!;
            var uuid = (string)facts["nativeMessageId"]!; var revision = (long)facts["revision"]!;
            if (settled) Assert.IsTrue(first.ReportStorage.TrySettleNoteReceipt(id, parent, host, generation, claim.AttemptId, revision));
            await process.StandardInput.WriteLineAsync("exit"); await process.StandardInput.FlushAsync();
            Assert.AreEqual("SELF_EXIT", await process.StandardOutput.ReadLineAsync().WaitAsync(Wait));
            await process.WaitForExitAsync().WaitAsync(Wait); Assert.AreEqual(43, process.ExitCode, await stderr);
            await first.DisposeAsync(); firstDisposed = true;
            await using var next = new Conductor(new ConductorOptions { DataDirectory = directory, ServiceId = "note-process-next" }, [agent]);
            await LinkHarness.Until(() => settled ? !next.ReportStorage.NoteNeedsAck(id, parent, host, generation, claim.AttemptId) :
                next.ReportStorage.GetReport(id).Status == "uncertain", "fresh conductor recovered host exit").WaitAsync(Wait);
            var info = next.ReportStorage.GetReport(id);
            Assert.AreEqual(settled ? "completed" : "uncertain", info.Status);
            if (settled) Assert.AreEqual("confirmed_host_exit", info.Delivery?.ReceiptReleaseReason);
            Assert.AreEqual("immutable host result", next.ReportStorage.Read(id, child).Text);
            Assert.AreEqual(uuid, (string?)next.ReportStorage.ReadNoteReceipt(id, parent, host, generation, claim.AttemptId)?["nativeMessageId"]);
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(directory, "reports", "probe-note-inputs")).Length);
        }
        finally
        {
            if (!process.HasExited) { await process.StandardInput.WriteLineAsync("exit"); await process.StandardInput.FlushAsync(); await process.WaitForExitAsync().WaitAsync(Wait); }
            if (!firstDisposed) await first.DisposeAsync();
        }
    }

}
