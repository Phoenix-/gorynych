using System.Diagnostics;
using System.IO.Pipes;
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
    sealed class CompletionProcessHost(string cwd, string ledger, string fake, bool native = false) : IHostLauncher, IAsyncDisposable
    {
        readonly string pipe = "gorynych-completion-" + Guid.NewGuid().ToString("N");
        readonly CancellationTokenSource stop = new();
        Process? process; Task? server; NamedPipeServerStream? activeConnection;
        public Conductor? Service { get; set; }
        public int Pid => process?.Id ?? 0;
        public void BreakConnection() => Volatile.Read(ref activeConnection)?.Dispose();
        public bool IsAlive(string adapter, string slot) => process is { HasExited: false };
        public void Launch(string adapter, string slot)
        {
            if (process is not null) throw new InvalidOperationException("test launches only one host");
            server = Serve();
            process = ReportHostProbe.Start(native ? "completion-host-native" : "completion-host", pipe, cwd, fake, ledger, slot);
        }
        async Task Serve()
        {
            while (!stop.IsCancellationRequested)
            {
                await using var connection = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await connection.WaitForConnectionAsync(stop.Token);
                    Volatile.Write(ref activeConnection, connection);
                    if (Service is { } service)
                    {
                        await service.ServeHostAsync(connection, connection, "completion-process-host");
                        // Running excludes stale generations; keep the accepted pipe alive
                        // for either a current or an admitted stale host.
                        if (service.Agents["claude"].Links.LastOrDefault() is { } link)
                            await link.Rpc.Completion.WaitAsync(stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (IOException) when (!stop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (!stop.IsCancellationRequested) { }
                finally { Interlocked.CompareExchange(ref activeConnection, null, connection); }
            }
        }
        public async Task CloseNativeGracefully()
        {
            if (process is null) return;
            Assert.AreEqual("HOST_STARTED", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            await process.StandardInput.WriteLineAsync("CLOSE_NATIVE"); await process.StandardInput.FlushAsync();
            Assert.AreEqual("NATIVE_EXITED", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25)), "no forced cleanup");
        }
        public async Task WaitExited() { if (process is not null) { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.AreEqual(0, process.ExitCode); } }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            if (server is not null) try { await server.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { }
            if (process is not null) { await WaitExited(); process.Dispose(); }
            stop.Dispose();
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task PublishedClaimCrossesSeparateHostedClaudeAndAcknowledgesOnce()
    {
        var dir = TestDirs.New(); var data = TestDirs.New(); File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var ledgerPath = Path.Combine(dir, "shared-ledger.json"); var ledger = new CompletionHostLedger(ledgerPath);
        await using var launcher = new CompletionProcessHost(dir, ledgerPath, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "private-host",
            Hosts = launcher, HostToken = InProcessHosts.Token, PeerServerCommand = ["fake-peers", "--pipe", "unused"] }, [adapter]);
        launcher.Service = service; await using var client = await TestClient.ConnectAsync(service);
        try
        {
        async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
            new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
        var parent = await New(); var child = await New();
        service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
        service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = false; }, durable: true);
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken,
                [GorynychProtocol.MetaMessageRequestId] = "private-assignment" } });
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("private-assignment", parent)?["delivery"] == "delivered" &&
            service.Feed(child).TurnGate.CurrentCount == 1, "delegation settled").WaitAsync(TimeSpan.FromSeconds(20));
        var route = service.Agents["claude"].RouteOf(child)!;
        Assert.AreEqual(launcher.Pid, route.ProcessId); Assert.AreNotEqual(Environment.ProcessId, route.ProcessId);
        var generation = (string)service.ReplyMailbox.FindAssignment("private-assignment", parent)!["generation"]!;
        var prompt = "service-delegation-" + generation + "-private-assignment";
        var observation = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostClaudePromptObservation,
            new JsonObject { ["_meta"] = new JsonObject { ["sessionId"] = child, ["promptId"] = prompt } });
        var witness = observation!["_meta"]!; Assert.AreEqual("observed", (string?)witness["state"], observation.ToJsonString());
        const string reminder = "Please record an explicit outcome or continuation.";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reminder)));
        var binding = new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), prompt,
            (string)witness["nativeUuid"]!, (string)witness["stream"]!, (long)witness["revision"]!, digest,
            route.HostId!, route.Generation!.Key, route.ProcessId, route.CompletionProcessStart);
        var attempt = Guid.NewGuid().ToString("N");
        var claim = ledger.ClaimFromMailbox(service.ReplyMailbox, "delegation", "private-assignment", parent, child, generation, 1, attempt, binding);
        Assert.IsTrue(claim.Acquired); var record = claim.Claim!;
        var identity = CompletionHostLedger.Identity("delegation", "private-assignment", parent, child, generation, 1, attempt, binding, record.Stream!, record.Number);
        var fields = identity.DeepClone().AsObject(); fields["text"] = reminder;
        var bad = fields.DeepClone().AsObject(); bad["ledgerNumber"] = record.Number + 1;
        await Assert.ThrowsExactlyAsync<Gorynych.Acp.JsonRpc.JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = bad }));
        var missing = fields.DeepClone().AsObject(); var missingAttempt = Guid.NewGuid().ToString("N"); missing["attemptId"] = missingAttempt;
        await Assert.ThrowsExactlyAsync<Gorynych.Acp.JsonRpc.JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = missing }));
        Assert.AreEqual(0, InputCount(child, Guid.ParseExact(missingAttempt, "N").ToString("D")));
        var wrongProducer = fields.DeepClone().AsObject();
        wrongProducer["producerStart"] = "2001-01-01T00:00:00.0000000Z";
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(
            GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = wrongProducer }));
        Assert.AreEqual(0, InputCount(child, Guid.ParseExact(attempt, "N").ToString("D")),
            "a mismatched producer cannot dispatch the admitted attempt");
        var sent = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = fields.DeepClone() });
        Assert.AreEqual("observed", (string?)sent?["_meta"]?["adapterState"], sent?.ToJsonString());
        var wrongStatus = identity.DeepClone().AsObject(); wrongStatus["digest"] = new string('F', 64);
        await Assert.ThrowsExactlyAsync<Gorynych.Acp.JsonRpc.JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedStatus, new JsonObject { ["_meta"] = wrongStatus }));
        var uuid = Guid.ParseExact(attempt, "N").ToString("D");
        await LinkHarness.Until(() => InputCount(child, uuid) == 1, "one guarded native input").WaitAsync(TimeSpan.FromSeconds(20));
        JsonNode? status = null;
        for (var i = 0; i < 60; i++)
        {
            status = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedStatus, new JsonObject { ["_meta"] = identity.DeepClone() });
            if ((string?)status?["_meta"]?["receipt"]?["cycleResult"] == "ended") break;
            await Task.Delay(50);
        }
        Assert.AreEqual("ended", (string?)status?["_meta"]?["receipt"]?["cycleResult"], status?.ToJsonString());
        var ackFields = identity.DeepClone().AsObject(); ackFields["receiptRevision"] = (long)status!["_meta"]!["receipt"]!["revision"]!;
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire,
            new JsonObject { ["_meta"] = ackFields.DeepClone() }));
        var ack = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedAck, new JsonObject { ["_meta"] = ackFields });
        Assert.AreEqual("acknowledged", (string?)ack?["_meta"]?["adapterState"], ack?.ToJsonString());
        Assert.IsFalse(ledger.CompactWithDurableConsumer(record.Stream!, record.Number, attempt, identity, _ => true), "durable ACK alone retains the host row");
        var wrongRetire = ackFields.DeepClone().AsObject(); wrongRetire["receiptRevision"] = (long)wrongRetire["receiptRevision"]! + 1;
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire,
            new JsonObject { ["_meta"] = wrongRetire }));
        var retired = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire, new JsonObject { ["_meta"] = ackFields.DeepClone() });
        Assert.AreEqual("retired", (string?)retired?["_meta"]?["adapterState"], retired?.ToJsonString());
        var repeatedRetire = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire, new JsonObject { ["_meta"] = ackFields.DeepClone() });
        Assert.AreEqual("retired", (string?)repeatedRetire?["_meta"]?["adapterState"]);
        Assert.IsTrue(ledger.CompactWithDurableConsumer(record.Stream!, record.Number, attempt, identity,
            outcome => outcome == "native_ended" && service.ReplyMailbox.SettleCompletionReminder("delegation", "private-assignment", parent, child,
                generation, 1, attempt, CompletionReminderSettlement.NativeEnded)));
        await Assert.ThrowsExactlyAsync<Gorynych.Acp.JsonRpc.JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = fields.DeepClone() }));
        var reopenedLedger = new CompletionHostLedger(ledgerPath);
        Assert.AreEqual(CompletionHostLedger.Admission.Stale, reopenedLedger.ReadAdmission(record.Stream!, record.Number, attempt, identity));
        Assert.IsFalse(reopenedLedger.BeginForward(record.Stream!, record.Number, attempt, identity));
        Assert.AreEqual(1, InputCount(child, uuid));
        Assert.AreEqual(2, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length, "one original prompt and one reminder");
        Assert.AreEqual("open", (string?)service.ReplyMailbox.FindAssignment("private-assignment", parent)!["status"]);
        }
        finally { await launcher.CloseNativeGracefully(); }
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ManagedOwnerCompletionUsesSeparateHostedClaude(bool oldGroupPending)
    {
        var dir = TestDirs.New(); var data = TestDirs.New(); File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var metadata = new UiMetadataStore(Path.Combine(data, "ui-metadata.json")); metadata.Set("project", "group", "Group");
        var instance = metadata.EnsureGroupInstance("project", "group")!;
        var ledgerPath = Path.Combine(dir, "owner-ledger.json"); var clock = new ManualTime();
        var pipe = "completion-mcp-" + Guid.NewGuid().ToString("N");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Gorynych.slnx"))) repository = repository.Parent;
        var serviceExe = Directory.EnumerateFiles(Path.Combine(repository!.FullName, "src", "Gorynych.Service", "bin"),
            TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        await using var launcher = new CompletionProcessHost(dir, ledgerPath, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "owner-host", Time = clock,
            Hosts = launcher, HostToken = InProcessHosts.Token, CompletionConfiguration = ("project", "group", instance), CompletionAuto = true,
            TestCompletionLedgerPath = ledgerPath,
            PeerServerCommand = [serviceExe, "peers", "--pipe", pipe] }, [adapter]);
        launcher.Service = service;
        using var stop = new CancellationTokenSource(); var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        await using var client = await TestClient.ConnectAsync(service);
        Process? peer = null; JsonRpcConnection? mcp = null;
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = false; }, durable: true);
            foreach (var sid in new[] { parent, child }) await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                new JsonObject { ["sessionId"] = sid, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" } } });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken, [GorynychProtocol.MetaMessageRequestId] = "owner-assignment" } });
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("owner-assignment", parent)?["delivery"] == "delivered" &&
                service.Feed(child).TurnGate.CurrentCount == 1, "owner delegation settled").WaitAsync(TimeSpan.FromSeconds(20));
            var generation = (string)service.ReplyMailbox.FindAssignment("owner-assignment", parent)!["generation"]!;
            Assert.AreEqual(launcher.Pid, service.Agents["claude"].RouteOf(child)!.ProcessId);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestCompletionReminder,
                new JsonObject { ["assignmentId"] = "owner-assignment", ["generation"] = generation }));
            var foreign = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestCompletionReminder,
                new JsonObject { ["assignmentId"] = "owner-assignment", ["generation"] = generation,
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(child)!.PeerToken } });
            Assert.AreEqual("deferred", (string?)foreign?["state"]);
            Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("owner-assignment", parent, child, generation));
            var psi = new ProcessStartInfo(serviceExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "peers", "--pipe", pipe, "--token", service.Store.Get(parent)!.PeerToken! }) psi.ArgumentList.Add(a);
            peer = Process.Start(psi)!; mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "completion-mcp"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            clock.Advance(TimeSpan.FromSeconds(5));
            var stale = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "request_completion_reminder",
                ["arguments"] = new JsonObject { ["assignment_id"] = "owner-assignment", ["generation"] = Guid.NewGuid().ToString("N") } });
            var staleBody = JsonNode.Parse((string)stale!["content"]![0]!["text"]!)!;
            Assert.AreEqual("deferred", (string?)staleBody["state"]);
            Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("owner-assignment", parent, child, generation));
            if (oldGroupPending)
            {
                // The previous group is a synthetic durable claim; the new group's witness and
                // all dispatch decisions still use the actual hosted FakeClaude path.
                service.ReplyMailbox.Accept("old-group-assignment", parent, child, "old", delegation: true);
                service.ReplyMailbox.MarkStarted("old-group-assignment", child); service.ReplyMailbox.MarkDelivered("old-group-assignment", child);
                var oldGeneration = (string)service.ReplyMailbox.FindAssignment("old-group-assignment", parent)!["generation"]!;
                var route = service.Agents["claude"].RouteOf(child)!;
                // Simulate an old Linux timestamp with the current PID (which may be reused).
                // Neither may authorize recovery; the admitted route is only held
                // conservatively so its potentially unique receipt is not destroyed.
                var legacy = OperatingSystem.IsLinux();
                var oldBinding = new CompletionReminderBinding("old-project", "old-group", Guid.NewGuid().ToString("N"), "old-prompt",
                    Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), 1, new string('A', 64),
                    route.HostId!, route.Generation!.Key, route.ProcessId,
                    legacy ? "2001-01-01T00:00:00.0000000Z" : route.CompletionProcessStart);
                Assert.IsTrue(new CompletionHostLedger(ledgerPath).ClaimFromMailbox(service.ReplyMailbox, "delegation", "old-group-assignment",
                    parent, child, oldGeneration, 1, Guid.NewGuid().ToString("N"), oldBinding).Acquired);
                Assert.IsTrue(service.CompletionPendingOnRoute(child, route, out var cleanupClaim));
                if (legacy)
                {
                    Assert.IsNull(cleanupClaim, "legacy veto must not authorize exact recovery");
                    var foreignRoute = new HostLink(route.Rpc, route.Info) { HostId = "foreign-host",
                        Generation = route.Generation, ProcessId = route.ProcessId, CompletionProcessStart = route.CompletionProcessStart };
                    Assert.IsFalse(service.CompletionPendingOnRoute(child, foreignRoute, out _), "other hosts do not inherit the veto");
                }
            }
            var result = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "request_completion_reminder",
                ["arguments"] = new JsonObject { ["assignment_id"] = "owner-assignment", ["generation"] = generation } });
            var body = JsonNode.Parse((string)result!["content"]![0]!["text"]!)!;
            if (oldGroupPending)
            {
                Assert.AreEqual("deferred", (string?)body["state"], body.ToJsonString());
                Assert.AreEqual("busy", (string?)body["reason"]);
                Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("owner-assignment", parent, child, generation));
                Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
                if (OperatingSystem.IsLinux())
                {
                    var original = service.Agents["claude"].RouteOf(child)!;
                    launcher.Service = null; launcher.BreakConnection();
                    await LinkHarness.Until(() => original.Rpc.IsClosed, "legacy route disconnected").WaitAsync(TimeSpan.FromSeconds(15));
                    await client.DisposeAsync(); await service.DisposeAsync();
                    await using var reopened = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "owner-host", Time = clock,
                        Hosts = launcher, HostToken = InProcessHosts.Token, TestCompletionLedgerPath = ledgerPath,
                        HostGeneration = _ => new HostGeneration("new-build", []) }, [adapter]);
                    launcher.Service = reopened;
                    await using var after = await TestClient.ConnectAsync(reopened);
                    await LinkHarness.Until(() => reopened.Agents["claude"].RouteOf(child) is not null,
                        "legacy host reattached").WaitAsync(TimeSpan.FromSeconds(20));
                    var legacyRoute = reopened.Agents["claude"].RouteOf(child)!;
                    Assert.IsFalse(legacyRoute.Current); Assert.AreEqual(original.HostId, legacyRoute.HostId);
                    Assert.AreEqual(Conductor.Move.Stay, await reopened.TryMoveAsync(child, false, CancellationToken.None),
                        "legacy claim must veto automatic migration");
                    Assert.IsTrue(reopened.CompletionPendingOnRoute(child, legacyRoute, out var authority));
                    Assert.IsNull(authority, "veto cannot authorize recovery");
                    var old = reopened.ReplyMailbox.FindAssignment("old-group-assignment", parent)!;
                    var recover = await after.Rpc.SendRequestAsync(GorynychProtocol.MethodRecoverCompletionReminder, new JsonObject {
                        ["assignmentId"] = "old-group-assignment", ["generation"] = (string?)old["generation"],
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = reopened.Store.Get(parent)!.PeerToken } });
                    Assert.AreEqual("unresolved", (string?)recover!["state"]);
                    Assert.AreEqual(Conductor.Move.Done, await reopened.TryMoveAsync(parent, false, CancellationToken.None));
                    Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
                }
                return;
            }
            Assert.AreEqual("settled", (string?)body["state"], body.ToJsonString());
            Assert.AreEqual("native_ended", (string?)body["disposition"]);
            var retry = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "request_completion_reminder",
                ["arguments"] = new JsonObject { ["assignment_id"] = "owner-assignment", ["generation"] = generation } });
            var retryBody = JsonNode.Parse((string)retry!["content"]![0]!["text"]!)!;
            Assert.AreEqual("deferred", (string?)retryBody["state"], retryBody.ToJsonString());
            Assert.AreEqual(2, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
            var ownerInput = Path.Combine(FakeRoot, parent + ".inputs.jsonl");
            await LinkHarness.Until(() => File.Exists(ownerInput) && File.ReadAllLines(ownerInput).Length == 1 &&
                service.ReplyMailbox.ReadManagedQueue(parent, 8).Count == 0, "one completion escalation delivered").WaitAsync(TimeSpan.FromSeconds(20));
            StringAssert.Contains(File.ReadAllText(ownerInput), "The completion reminder turn");
            Assert.AreEqual(1, File.ReadAllLines(ownerInput).Length, "one owner notice");
            Assert.AreEqual("open", (string?)service.ReplyMailbox.FindAssignment("owner-assignment", parent)!["status"]);
        }
        finally
        {
            if (mcp is not null) await mcp.DisposeAsync();
            if (peer is not null) { await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); }
            stop.Cancel(); try { await listener; } catch (OperationCanceledException) { }
            await launcher.CloseNativeGracefully();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task LiveRouteEpochInvalidationTriggersFallbackWithoutAutomaticReminder(bool reportEnabled)
    {
        var dir = TestDirs.New(); var data = TestDirs.New(); File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var metadata = new UiMetadataStore(Path.Combine(data, "ui-metadata.json")); metadata.Set("project", "group", "Group");
        var instance = metadata.EnsureGroupInstance("project", "group")!;
        var ledger = Path.Combine(dir, "ledger.json"); var clock = new ManualTime();
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true, completionGuard: true));
        var hosts = new InProcessHosts(_ => adapter) { HostCompletionLedger = new CompletionHostLedger(ledger) };
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "route-loss", Time = clock,
            Hosts = hosts, HostToken = InProcessHosts.Token, CompletionConfiguration = ("project", "group", instance),
            CompletionAuto = true, TestCompletionLedgerPath = ledger }, [adapter]);
        hosts.Service = service; await using var client = await TestClient.ConnectAsync(service);
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = reportEnabled; }, durable: true);
            foreach (var sid in new[] { parent, child }) await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                new JsonObject { ["sessionId"] = sid, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" } } });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken, [GorynychProtocol.MetaMessageRequestId] = "route-task" } });
            await LinkHarness.Until(() => service.ReplyMailbox.FindAssignment("route-task", parent)?["coordinatorRouteEpoch"] is not null,
                "initial route evidence").WaitAsync(TimeSpan.FromSeconds(20));
            var saved = service.ReplyMailbox.FindAssignment("route-task", parent)!;
            var generation = (string)saved["generation"]!; var old = service.Agents["claude"].RouteOf(child)!;
            var oldEpoch = (long)saved["coordinatorRouteEpoch"]!;
            Assert.AreEqual(old.HostId, (string?)saved["coordinatorHostId"]);
            service.Store.Update(parent, "claude", r => r.Archived = true, durable: true);
            // Simulate the exact source discontinuity on the live route. The same
            // host ID and generation remain in place; only its epoch is invalidated.
            service.Feed(child).InvalidateAssignmentEvidence(old.Rpc);
            var current = service.Agents["claude"].RouteOf(child)!;
            Assert.AreEqual(old.HostId, current.HostId); Assert.AreEqual(old.Generation!.Key, current.Generation!.Key);
            Assert.AreNotEqual(oldEpoch, service.Feed(child).AssignmentEvidenceEpoch);
            clock.Advance(TimeSpan.FromSeconds(61)); await service.SweepCompletionAutoAsync();
            var notices = service.ReplyMailbox.ReadManagedQueue(parent, 8);
            Assert.AreEqual(reportEnabled ? 1 : 0, notices.Count);
            if (reportEnabled) Assert.AreEqual("unsupervised", (string?)notices.Single()["kind"]);
            Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("route-task", parent, child, generation));
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
        }
        finally { hosts.Service = null; }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task SameOriginalHostReattachesOnNewRouteAndRecoversFallback()
    {
        var dir = TestDirs.New(); var data = TestDirs.New(); File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var metadata = new UiMetadataStore(Path.Combine(data, "ui-metadata.json")); metadata.Set("project", "group", "Group");
        var instance = metadata.EnsureGroupInstance("project", "group")!; var ledger = Path.Combine(dir, "ledger.json"); var clock = new ManualTime();
        await using var launcher = new CompletionProcessHost(dir, ledger, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        Conductor Create() => new(new ConductorOptions { DataDirectory = data, ServiceId = "fallback-reattach", Time = clock,
            Hosts = launcher, HostToken = InProcessHosts.Token, CompletionConfiguration = ("project", "group", instance),
            CompletionAuto = true, TestCompletionLedgerPath = ledger }, [adapter]);
        var service = Create(); launcher.Service = service; var client = await TestClient.ConnectAsync(service);
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = true; }, durable: true);
            foreach (var sid in new[] { parent, child }) await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                new JsonObject { ["sessionId"] = sid, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" } } });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken, [GorynychProtocol.MetaMessageRequestId] = "reattach-task" } });
            await LinkHarness.Until(() => service.ReplyMailbox.FindAssignment("reattach-task", parent)?["coordinatorRouteEpoch"] is not null &&
                service.Feed(child).TurnGate.CurrentCount == 1, "initial supervised terminal").WaitAsync(TimeSpan.FromSeconds(20));
            var saved = service.ReplyMailbox.FindAssignment("reattach-task", parent)!; var generation = (string)saved["generation"]!;
            var old = service.Agents["claude"].RouteOf(child)!; var pid = launcher.Pid;
            Assert.AreEqual(0, service.ReplyMailbox.ReadManagedQueue(parent, 8).Count);
            service.Store.Update(parent, "claude", r => r.Archived = true, durable: true);
            await HostTests.RestartAsync(service); launcher.Service = null; launcher.BreakConnection();
            await client.DisposeAsync(); await service.DisposeAsync();
            service = Create(); launcher.Service = service; client = await TestClient.ConnectAsync(service);
            await LinkHarness.Until(() => service.Agents["claude"].RouteOf(child) is { Current: true }, "original host reattached").WaitAsync(TimeSpan.FromSeconds(20));
            var current = service.Agents["claude"].RouteOf(child)!;
            Assert.AreNotSame(old.Rpc, current.Rpc); Assert.AreEqual(pid, launcher.Pid);
            Assert.AreEqual(old.HostId, current.HostId); Assert.AreEqual(old.Generation!.Key, current.Generation!.Key);
            var notice = service.ReplyMailbox.ReadManagedQueue(parent, 8).Single(); Assert.AreEqual("unsupervised", (string?)notice["kind"]);
            clock.Advance(TimeSpan.FromSeconds(61)); await service.SweepCompletionAutoAsync();
            Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("reattach-task", parent, child, generation));
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); await launcher.CloseNativeGracefully(); }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ReportAckBeforeTerminalBoundaryDoesNotInventRouteEpoch()
    {
        var dir = TestDirs.New(); var data = TestDirs.New(); File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var metadata = new UiMetadataStore(Path.Combine(data, "ui-metadata.json")); metadata.Set("project", "group", "Group");
        var instance = metadata.EnsureGroupInstance("project", "group")!; var ledger = Path.Combine(dir, "ledger.json");
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true, completionGuard: true));
        var hosts = new InProcessHosts(_ => adapter) { HostCompletionLedger = new CompletionHostLedger(ledger) };
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "ack-before-boundary",
            Hosts = hosts, HostToken = InProcessHosts.Token, CompletionConfiguration = ("project", "group", instance),
            CompletionAuto = true, TestCompletionLedgerPath = ledger }, [adapter]);
        hosts.Service = service; await using var client = await TestClient.ConnectAsync(service);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AfterMailboxResponseForTests = async () => { arrived.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(20)); };
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = true; }, durable: true);
            foreach (var sid in new[] { parent, child }) await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                new JsonObject { ["sessionId"] = sid, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" } } });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken, [GorynychProtocol.MetaMessageRequestId] = "ack-task" } });
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await LinkHarness.Until(() => service.ReportStorage.List(child).Items.Any(r => r.Identity.AssignmentId == "ack-task" && r.ProducerAck && r.Status == "retained"),
                "producer acknowledgement retained").WaitAsync(TimeSpan.FromSeconds(20));
            var pre = service.ReplyMailbox.FindAssignment("ack-task", parent)!;
            Assert.IsNotNull(pre["latestReportId"]); Assert.IsNull(pre["coordinatorInitialTurnSettled"]); Assert.IsNull(pre["coordinatorRouteEpoch"]);
            await service.SweepCompletionAutoAsync();
            Assert.AreEqual(0, service.ReplyMailbox.ReadManagedQueue(parent, 8).Count);
            Assert.IsNull(service.ReplyMailbox.FindAssignment("ack-task", parent)!["completionReminder"]);
            release.TrySetResult();
            await LinkHarness.Until(() => service.ReplyMailbox.FindAssignment("ack-task", parent)?["coordinatorRouteEpoch"] is not null,
                "exact boundary published").WaitAsync(TimeSpan.FromSeconds(20));
            await service.SweepCompletionAutoAsync();
            Assert.AreEqual(0, service.ReplyMailbox.ReadManagedQueue(parent, 8).Count);
            var row = service.ReplyMailbox.FindAssignment("ack-task", parent)!;
            Assert.AreEqual(service.Feed(child).AssignmentEvidenceEpoch, (long?)row["coordinatorRouteEpoch"]);
            Assert.IsNull(row["completionReminder"]);
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
        }
        finally { release.TrySetResult(); hosts.Service = null; }
    }

    [TestMethod]
    [DataRow("membership")]
    [DataRow("group_delete")]
    [DataRow("mode")]
    [DataRow("archive")]
    [DataRow("cancel")]
    [DataRow("continuation")]
    [DataRow("lost")]
    [DataRow("close")]
    [DataRow("committed")]
    [DataRow("stop_unresolved")]
    [DataRow("restart_unresolved")]
    [DataRow("user_cancel")]
    [DataRow("reopen")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ManagedOwnerMembershipChangeAbortsBeforeForward(string mutation)
    {
        var dir = TestDirs.New(); var data = TestDirs.New(); File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var metadata = new UiMetadataStore(Path.Combine(data, "ui-metadata.json")); metadata.Set("project", "group", "Group");
        var instance = metadata.EnsureGroupInstance("project", "group")!;
        var ledgerPath = Path.Combine(dir, "owner-ledger.json"); var clock = new ManualTime();
        var pipe = "completion-mcp-" + Guid.NewGuid().ToString("N");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Gorynych.slnx"))) repository = repository.Parent;
        var serviceExe = Directory.EnumerateFiles(Path.Combine(repository!.FullName, "src", "Gorynych.Service", "bin"),
            TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        await using var launcher = new CompletionProcessHost(dir, ledgerPath, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "owner-host", Time = clock,
            Hosts = launcher, HostToken = InProcessHosts.Token, CompletionConfiguration = ("project", "group", instance),
            TestCompletionLedgerPath = ledgerPath,
            PeerServerCommand = [serviceExe, "peers", "--pipe", pipe] }, [adapter]);
        launcher.Service = service;
        using var stop = new CancellationTokenSource(); var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        await using var client = await TestClient.ConnectAsync(service);
        Process? peer = null; JsonRpcConnection? mcp = null; var nativeClosed = false;
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = false; }, durable: true);
            foreach (var sid in new[] { parent, child }) await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                new JsonObject { ["sessionId"] = sid, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" } } });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken, [GorynychProtocol.MetaMessageRequestId] = "owner-assignment" } });
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("owner-assignment", parent)?["delivery"] == "delivered" &&
                service.Feed(child).TurnGate.CurrentCount == 1, "owner delegation settled").WaitAsync(TimeSpan.FromSeconds(20));
            var generation = (string)service.ReplyMailbox.FindAssignment("owner-assignment", parent)!["generation"]!;
            Assert.AreEqual(launcher.Pid, service.Agents["claude"].RouteOf(child)!.ProcessId);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestCompletionReminder,
                new JsonObject { ["assignmentId"] = "owner-assignment", ["generation"] = generation }));
            var foreign = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestCompletionReminder,
                new JsonObject { ["assignmentId"] = "owner-assignment", ["generation"] = generation,
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(child)!.PeerToken } });
            Assert.AreEqual("deferred", (string?)foreign?["state"]);
            Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("owner-assignment", parent, child, generation));
            var psi = new ProcessStartInfo(serviceExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "peers", "--pipe", pipe, "--token", service.Store.Get(parent)!.PeerToken! }) psi.ArgumentList.Add(a);
            peer = Process.Start(psi)!; mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "completion-mcp"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            clock.Advance(TimeSpan.FromSeconds(5));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (mutation == "committed") File.WriteAllText(Path.Combine(dir, "hold-completion-result"), "");
            else service.AfterCompletionOwnerClaimForTests = () => { entered.TrySetResult(); return release.Task; };
            var pending = mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "request_completion_reminder",
                ["arguments"] = new JsonObject { ["assignment_id"] = "owner-assignment", ["generation"] = generation } });
            if (mutation == "committed") await LinkHarness.Until(() =>
            {
                var claim = service.ReplyMailbox.ReadCompletionReminder("owner-assignment", parent, child, generation);
                return claim is not null && (bool?)new CompletionHostLedger(ledgerPath).ReadAttempt(claim.AttemptId)?["receipt"]?["committed"] == true;
            }, "durable committed receipt").WaitAsync(TimeSpan.FromSeconds(15));
            else await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                if (mutation == "reopen")
                {
                    var originalHost = service.Agents["claude"].RouteOf(child)!;
                    launcher.Service = null; launcher.BreakConnection();
                    await LinkHarness.Until(() => originalHost.Rpc.IsClosed, "original conductor disconnected").WaitAsync(TimeSpan.FromSeconds(15));
                    release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(15));
                    await client.DisposeAsync(); await service.DisposeAsync();
                    await using var reopened = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "owner-host", Time = clock,
                        Hosts = launcher, HostToken = InProcessHosts.Token, TestCompletionLedgerPath = ledgerPath }, [adapter]);
                    launcher.Service = reopened;
                    await using var after = await TestClient.ConnectAsync(reopened);
                    await LinkHarness.Until(() => reopened.Agents["claude"].RouteOf(child) is { Current: true }, "original host reattached").WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.AreEqual(originalHost.HostId, reopened.Agents["claude"].RouteOf(child)!.HostId);
                    Assert.IsFalse(reopened.CompletionOwnerTriggerAvailable);
                    await after.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                        new JsonObject { ["sessionId"] = parent, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = null } });
                    var settled = reopened.ReplyMailbox.ReadCompletionReminder("owner-assignment", parent, child, generation)!;
                    Assert.AreEqual("not_sent", settled.Disposition);
                    Assert.IsNull(new CompletionHostLedger(ledgerPath).ReadAttempt(settled.AttemptId));
                    Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
                    await launcher.CloseNativeGracefully(); nativeClosed = true;
                    return;
                }
                if (mutation == "user_cancel")
                {
                    var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var cancelRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    service.BeforeCompletionPolicyAbortForTests = () => { cancelEntered.TrySetResult(); return cancelRelease.Task; };
                    try
                    {
                        await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = child });
                        await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        release.TrySetResult();
                        var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(15));
                        var cancelledBody = JsonNode.Parse((string)cancelled!["content"]![0]!["text"]!)!;
                        Assert.AreEqual("uncertain", (string?)cancelledBody["state"]);
                        Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
                    }
                    finally { cancelRelease.TrySetResult(); }
                    await LinkHarness.Until(() => service.ReplyMailbox.ReadCompletionReminder("owner-assignment", parent, child, generation)?.Disposition == "not_sent",
                        "cancel abort settled").WaitAsync(TimeSpan.FromSeconds(15));
                    return;
                }
                if (mutation is "stop_unresolved" or "restart_unresolved")
                {
                    service.CompletionPolicyAbortResultForTests = _ => "unresolved";
                    var method = mutation == "stop_unresolved" ? GorynychProtocol.MethodStop : GorynychProtocol.MethodRestart;
                    var parameters = mutation == "stop_unresolved" ? new JsonObject { ["agents"] = new JsonArray("claude"), ["wait"] = false } : new JsonObject { ["wait"] = false };
                    await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(method, parameters));
                    await client.Rpc.SendRequestAsync(GorynychProtocol.MethodGroupsSet,
                        new JsonObject { ["projectKey"] = "other", ["groupId"] = "other", ["name"] = "Other" });
                    service.CompletionPolicyAbortResultForTests = null;
                    await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                        new JsonObject { ["sessionId"] = parent, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = null } });
                }
                else if (mutation == "lost")
                {
                    service.CompletionPolicyAbortResultForTests = _ => "unresolved";
                    var patch = new JsonObject { ["sessionId"] = parent, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = null } };
                    await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch, patch.DeepClone()));
                    await client.Rpc.SendRequestAsync(GorynychProtocol.MethodGroupsSet,
                        new JsonObject { ["projectKey"] = "other", ["groupId"] = "other", ["name"] = "Other" });
                    service.CompletionPolicyAbortResultForTests = null;
                    await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch, patch.DeepClone());
                }
                else if (mutation is "membership" or "committed") await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                    new JsonObject { ["sessionId"] = parent, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = null } });
                else if (mutation == "group_delete") await client.Rpc.SendRequestAsync(GorynychProtocol.MethodGroupsDelete,
                    new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" });
                else if (mutation == "mode") await SetModeAsync(client, child, "default");
                else if (mutation == "archive") await client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive,
                    new JsonObject { ["sessionId"] = parent, ["archived"] = true });
                else if (mutation == "close") await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose,
                    new JsonObject { ["sessionId"] = parent });
                else if (mutation == "continuation")
                {
                    service.ReplyMailbox.Accept("owner-wait", child, parent, "question", expectReply: true);
                    service.ReplyMailbox.MarkStarted("owner-wait", parent); service.ReplyMailbox.MarkDelivered("owner-wait", parent);
                    await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, new JsonObject {
                        ["source"] = "delegation", ["assignmentId"] = "owner-assignment", ["generation"] = generation,
                        ["expectedRevision"] = 1, ["operationId"] = "register-wait", ["action"] = "register", ["messageId"] = "owner-wait",
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(child)!.PeerToken } });
                }
                else await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, new JsonObject {
                    ["source"] = "delegation", ["assignmentId"] = "owner-assignment", ["generation"] = generation,
                    ["expectedRevision"] = 1, ["operationId"] = "cancel-owner", ["outcome"] = "cancelled", ["summary"] = "cancelled",
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken } });
                var disposition = service.ReplyMailbox.ReadCompletionReminder("owner-assignment", parent, child, generation)?.Disposition;
                if (mutation == "committed") Assert.AreEqual("handoff_started", disposition);
                else Assert.AreEqual("not_sent", disposition);
            }
            finally
            {
                release.TrySetResult();
                if (mutation == "committed") File.WriteAllText(Path.Combine(dir, "release-completion-result"), "");
            }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            var body = JsonNode.Parse((string)result!["content"]![0]!["text"]!)!;
            Assert.AreEqual(mutation == "committed" ? "settled" : "uncertain", (string?)body["state"], body.ToJsonString());
            Assert.AreEqual(mutation == "committed" ? 2 : 1, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
            Assert.AreEqual(mutation == "cancel" ? "cancelled" : "open", (string?)service.ReplyMailbox.FindAssignment("owner-assignment", parent)!["status"]);

        }
        finally
        {
            if (mcp is not null) await mcp.DisposeAsync();
            if (peer is not null) { await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); }
            stop.Cancel(); try { await listener; } catch (OperationCanceledException) { }
            if (!nativeClosed) await launcher.CloseNativeGracefully();
        }
    }

    [TestMethod]
    [Timeout(240000, CooperativeCancellation = true)]
    public async Task SeparateHostRetiresMoreThanSixtyFourAttemptsWithoutReplay()
    {
        var dir = TestDirs.New(); var data = TestDirs.New(); File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var ledgerPath = Path.Combine(dir, "retire-ledger.json"); var ledger = new CompletionHostLedger(ledgerPath);
        await using var launcher = new CompletionProcessHost(dir, ledgerPath, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "retire-host",
            Hosts = launcher, HostToken = InProcessHosts.Token, PeerServerCommand = ["fake-peers", "--pipe", "unused"] }, [adapter]);
        launcher.Service = service; await using var client = await TestClient.ConnectAsync(service);
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = false; }, durable: true);
            JsonObject? oldSubmit = null; string? firstStream = null, firstAttempt = null; long firstNumber = 0; JsonObject? firstIdentity = null;
            const string reminder = "Please record an explicit outcome or continuation.";
            for (var n = 0; n < 66; n++)
            {
                var id = "retire-assignment-" + n;
                await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken,
                        [GorynychProtocol.MetaMessageRequestId] = id } });
                await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment(id, parent)?["delivery"] == "delivered" &&
                    service.Feed(child).TurnGate.CurrentCount == 1, "delegation settled").WaitAsync(TimeSpan.FromSeconds(20));
                var route = service.Agents["claude"].RouteOf(child)!;
                var generation = (string)service.ReplyMailbox.FindAssignment(id, parent)!["generation"]!;
                var prompt = "service-delegation-" + generation + "-" + id;
                var observation = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostClaudePromptObservation,
                    new JsonObject { ["_meta"] = new JsonObject { ["sessionId"] = child, ["promptId"] = prompt } });
                var witness = observation!["_meta"]!; Assert.AreEqual("observed", (string?)witness["state"], $"iteration {n}: {observation}");
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reminder)));
                var binding = new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), prompt,
                    (string)witness["nativeUuid"]!, (string)witness["stream"]!, (long)witness["revision"]!, digest,
                    route.HostId!, route.Generation!.Key, route.ProcessId, route.CompletionProcessStart);
                var attempt = Guid.NewGuid().ToString("N");
                var claim = ledger.ClaimFromMailbox(service.ReplyMailbox, "delegation", id, parent, child, generation, 1, attempt, binding);
                Assert.IsTrue(claim.Acquired, $"iteration {n}"); var record = claim.Claim!;
                var identity = CompletionHostLedger.Identity("delegation", id, parent, child, generation, 1, attempt, binding, record.Stream!, record.Number);
                var fields = identity.DeepClone().AsObject(); fields["text"] = reminder;
                if (n == 0) { oldSubmit = fields.DeepClone().AsObject(); firstStream = record.Stream; firstNumber = record.Number; firstAttempt = attempt; firstIdentity = identity.DeepClone().AsObject(); }
                await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = fields });
                JsonNode? status = null;
                for (var j = 0; j < 80; j++)
                {
                    status = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedStatus, new JsonObject { ["_meta"] = identity.DeepClone() });
                    if ((string?)status?["_meta"]?["receipt"]?["cycleResult"] == "ended") break;
                    await Task.Delay(25);
                }
                Assert.AreEqual("ended", (string?)status?["_meta"]?["receipt"]?["cycleResult"], $"iteration {n}: {status}");
                var ackFields = identity.DeepClone().AsObject(); ackFields["receiptRevision"] = (long)status!["_meta"]!["receipt"]!["revision"]!;
                // Fail after the adapter ACK but before the host can durably record it.
                if (n == 0)
                {
                    File.WriteAllText(Path.Combine(dir, "fail-completion-ack-record"), "");
                    await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedAck,
                        new JsonObject { ["_meta"] = ackFields.DeepClone() }));
                    Assert.IsNull(ledger.ReadAttempt(attempt)?["ackRevision"]);
                    var retainedStatus = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedStatus,
                        new JsonObject { ["_meta"] = identity.DeepClone() });
                    Assert.AreEqual(true, (bool?)retainedStatus?["_meta"]?["receipt"]?["committed"]);
                    Assert.AreEqual(false, (bool?)retainedStatus?["_meta"]?["receipt"]?["noInput"]);
                }
                // Also discard a successful response and verify durable retry.
                await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedAck, new JsonObject { ["_meta"] = ackFields.DeepClone() });
                var ack = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedAck, new JsonObject { ["_meta"] = ackFields.DeepClone() });
                Assert.AreEqual((long?)ackFields["receiptRevision"], (long?)ack?["_meta"]?["ackRevision"], $"iteration {n}");
                Assert.IsFalse(ledger.CompactWithDurableConsumer(record.Stream!, record.Number, attempt, identity, _ => true));
                if (n == 0)
                {
                    File.WriteAllText(Path.Combine(dir, "fail-completion-retire-record"), "");
                    await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire,
                        new JsonObject { ["_meta"] = ackFields.DeepClone() }));
                    Assert.IsNull(ledger.ReadAttempt(attempt)?["retiredRevision"]);
                    Assert.IsFalse(ledger.CompactWithDurableConsumer(record.Stream!, record.Number, attempt, identity, _ => true));
                    await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit,
                        new JsonObject { ["_meta"] = fields.DeepClone() }));
                }
                await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire, new JsonObject { ["_meta"] = ackFields.DeepClone() });
                var retire = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire, new JsonObject { ["_meta"] = ackFields.DeepClone() });
                Assert.AreEqual((long?)ackFields["receiptRevision"], (long?)retire?["_meta"]?["retiredRevision"], $"iteration {n}");
                Assert.IsTrue(ledger.CompactWithDurableConsumer(record.Stream!, record.Number, attempt, identity, outcome =>
                    outcome == "native_ended" && service.ReplyMailbox.SettleCompletionReminder("delegation", id, parent, child, generation, 1, attempt, CompletionReminderSettlement.NativeEnded)), $"iteration {n}");
                Assert.AreEqual(1, InputCount(child, Guid.ParseExact(attempt, "N").ToString("D")), $"iteration {n}");
            }
            var reopened = new CompletionHostLedger(ledgerPath);
            Assert.AreEqual(CompletionHostLedger.Admission.Stale, reopened.ReadAdmission(firstStream!, firstNumber, firstAttempt!, firstIdentity!));
            var currentRoute = service.Agents["claude"].RouteOf(child)!;
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => currentRoute.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = oldSubmit }));
            Assert.AreEqual(1, InputCount(child, Guid.ParseExact(firstAttempt!, "N").ToString("D")));
            Assert.AreEqual(132, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
        }
        finally { await launcher.CloseNativeGracefully(); }
    }

    [TestMethod]
    [DataRow("ack")]
    [DataRow("abort")]
    [DataRow("raw")]
    [DataRow("missing")]
    [DataRow("mismatch")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task OwnerRecoversExactExitedHostWithoutOptIn(string scenario)
    {
        var dir = TestDirs.New(); var data = TestDirs.New(); File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var ledgerPath = Path.Combine(dir, "recovery-ledger.json"); var ledger = new CompletionHostLedger(ledgerPath);
        await using var launcher = new CompletionProcessHost(dir, ledgerPath, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "recovery-host",
            Hosts = launcher, HostToken = InProcessHosts.Token, TestCompletionLedgerPath = ledgerPath,
            PeerServerCommand = ["fake-peers", "--pipe", "unused"] }, [adapter]);
        launcher.Service = service; var client = await TestClient.ConnectAsync(service); bool nativeClosed = false;
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = false; }, durable: true);
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken,
                    [GorynychProtocol.MetaMessageRequestId] = "recovery-assignment" } });
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("recovery-assignment", parent)?["delivery"] == "delivered" &&
                service.Feed(child).TurnGate.CurrentCount == 1, "recovery delegation settled").WaitAsync(TimeSpan.FromSeconds(20));
            var route = service.Agents["claude"].RouteOf(child)!; Assert.AreEqual(launcher.Pid, route.ProcessId);
            var generation = (string)service.ReplyMailbox.FindAssignment("recovery-assignment", parent)!["generation"]!;
            var prompt = "service-delegation-" + generation + "-recovery-assignment";
            var observed = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostClaudePromptObservation,
                new JsonObject { ["_meta"] = new JsonObject { ["sessionId"] = child, ["promptId"] = prompt } });
            var witness = observed!["_meta"]!; Assert.AreEqual("observed", (string?)witness["state"]);
            const string text = "Please record an explicit outcome or continuation.";
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            var binding = new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), prompt,
                (string)witness["nativeUuid"]!, (string)witness["stream"]!, (long)witness["revision"]!, digest,
                route.HostId!, route.Generation!.Key, route.ProcessId, route.CompletionProcessStart);
            var attempt = Guid.NewGuid().ToString("N");
            var claim = ledger.ClaimFromMailbox(service.ReplyMailbox, "delegation", "recovery-assignment", parent, child, generation, 1, attempt, binding).Claim!;
            var identity = CompletionHostLedger.Identity("delegation", "recovery-assignment", parent, child, generation, 1, attempt, binding, claim.Stream!, claim.Number);
            JsonObject Query(string token) => new() { ["assignmentId"] = "recovery-assignment", ["generation"] = generation,
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } };
            var token = service.Store.Get(parent)!.PeerToken!;
            Assert.AreEqual("unresolved", (string?)(await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRecoverCompletionReminder, Query(token)))?["state"], "live producer is not exit proof");
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodRecoverCompletionReminder, Query(service.Store.Get(child)!.PeerToken!)));
            if (scenario == "abort")
            {
                var verdict = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedAbort, new JsonObject { ["_meta"] = identity.DeepClone() });
                Assert.AreEqual("confirmed_precommit", (string?)verdict?["_meta"]?["abortVerdict"]);
            }
            else
            {
                var fields = identity.DeepClone().AsObject(); fields["text"] = text;
                await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = fields });
                JsonNode? status = null;
                for (var j = 0; j < 80; j++)
                {
                    status = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedStatus, new JsonObject { ["_meta"] = identity.DeepClone() });
                    if ((string?)status?["_meta"]?["receipt"]?["cycleResult"] == "ended") break;
                    await Task.Delay(25);
                }
                Assert.AreEqual("ended", (string?)status?["_meta"]?["receipt"]?["cycleResult"]);
                if (scenario == "ack")
                {
                    var ack = identity.DeepClone().AsObject(); ack["receiptRevision"] = (long)status!["_meta"]!["receipt"]!["revision"]!;
                    var response = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedAck, new JsonObject { ["_meta"] = ack });
                    Assert.IsNotNull(response?["_meta"]?["ackRevision"]); Assert.IsNull(response?["_meta"]?["retiredRevision"]);
                }
            }
            var before = File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length;
            Assert.AreEqual(scenario == "abort" ? 1 : 2, before);
            await launcher.CloseNativeGracefully(); nativeClosed = true;
            // Native EOF can let the owned host exit before its stop request arrives.
            try { await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostStop, new JsonObject()); }
            catch (IOException) { /* WaitExited below still requires a graceful zero exit. */ }
            await launcher.WaitExited();
            if (scenario == "missing") File.Delete(ledgerPath);
            if (scenario == "mismatch")
            {
                var tampered = JsonNode.Parse(File.ReadAllText(ledgerPath))!.AsObject();
                tampered["rows"]![attempt]!["identity"]!["ownerId"] = "other-owner";
                File.WriteAllText(ledgerPath, tampered.ToJsonString());
            }
            await client.DisposeAsync(); await service.DisposeAsync(); launcher.Service = null;
            await using var reopened = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "recovery-host",
                TestCompletionLedgerPath = ledgerPath }, [adapter]);
            await using var after = await TestClient.ConnectAsync(reopened);
            var recovered = await after.Rpc.SendRequestAsync(GorynychProtocol.MethodRecoverCompletionReminder, Query(token));
            var expected = scenario == "ack" ? "native_ended" : scenario == "abort" ? "not_sent" :
                scenario is "missing" or "mismatch" ? "unresolved" : "unknown_closed";
            Assert.AreEqual(expected, (string?)recovered?["state"], recovered?.ToJsonString());
            var disposition = reopened.ReplyMailbox.ReadCompletionReminder("recovery-assignment", parent, child, generation)?.Disposition;
            if (expected == "unresolved") Assert.IsTrue(disposition is "uncertain" or "handoff_started");
            else Assert.AreEqual(expected, disposition);
            Assert.AreEqual("open", (string?)reopened.ReplyMailbox.FindAssignment("recovery-assignment", parent)?["status"]);
            Assert.AreEqual(before, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
            if (expected != "unresolved") Assert.AreEqual(CompletionHostLedger.Admission.Stale, new CompletionHostLedger(ledgerPath).ReadAdmission(claim.Stream!, claim.Number, attempt, identity));
            if (scenario == "missing") Assert.IsFalse(File.Exists(ledgerPath), "missing used ledger was not recreated");
            if (scenario == "raw") Assert.IsNotNull(reopened.ReplyMailbox.ReadCompletionUnknownReceipt(parent, "recovery-assignment", child, generation, 1, attempt));
        }
        finally
        {
            if (!nativeClosed) await launcher.CloseNativeGracefully();
            await client.DisposeAsync(); await service.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task SeparateHostRetiresAdapterAbortsBeforeDelayedSubmit()
    {
        var dir = TestDirs.New(); var data = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        File.WriteAllText(Path.Combine(dir, "small-guarded-cap"), "");
        var ledgerPath = Path.Combine(dir, "abort-ledger.json"); var ledger = new CompletionHostLedger(ledgerPath);
        await using var launcher = new CompletionProcessHost(dir, ledgerPath, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "abort-host",
            Hosts = launcher, HostToken = InProcessHosts.Token, PeerServerCommand = ["fake-peers", "--pipe", "unused"] }, [adapter]);
        launcher.Service = service; await using var client = await TestClient.ConnectAsync(service);
        var releasePath = Path.Combine(dir, "release-guarded-write");
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = false; }, durable: true);
            const string text = "Please record an explicit outcome or continuation.";
            for (var n = 0; n < 4; n++)
            {
                var enteredPath = Path.Combine(dir, "guarded-write-entered");
                File.Delete(enteredPath); File.Delete(releasePath);
                File.WriteAllText(Path.Combine(dir, "hold-guarded-write"), "");
                var id = "abort-assignment-" + n;
                await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken,
                        [GorynychProtocol.MetaMessageRequestId] = id } });
                await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment(id, parent)?["delivery"] == "delivered" &&
                    service.Feed(child).TurnGate.CurrentCount == 1, "delegation settled").WaitAsync(TimeSpan.FromSeconds(20));
                var route = service.Agents["claude"].RouteOf(child)!;
                var generation = (string)service.ReplyMailbox.FindAssignment(id, parent)!["generation"]!;
                var prompt = "service-delegation-" + generation + "-" + id;
                var observation = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostClaudePromptObservation,
                    new JsonObject { ["_meta"] = new JsonObject { ["sessionId"] = child, ["promptId"] = prompt } });
                var witness = observation!["_meta"]!; Assert.AreEqual("observed", (string?)witness["state"]);
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
                var binding = new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), prompt,
                    (string)witness["nativeUuid"]!, (string)witness["stream"]!, (long)witness["revision"]!, digest,
                    route.HostId!, route.Generation!.Key, route.ProcessId, route.CompletionProcessStart);
                var attempt = Guid.NewGuid().ToString("N");
                var claim = ledger.ClaimFromMailbox(service.ReplyMailbox, "delegation", id, parent, child, generation, 1, attempt, binding);
                Assert.IsTrue(claim.Acquired); var record = claim.Claim!;
                var identity = CompletionHostLedger.Identity("delegation", id, parent, child, generation, 1, attempt, binding, record.Stream!, record.Number);
                var fields = identity.DeepClone().AsObject(); fields["text"] = text;
                var pending = route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = fields.DeepClone() });
                try
                {
                    await LinkHarness.Until(() => File.Exists(enteredPath), "adapter submit held before writer").WaitAsync(TimeSpan.FromSeconds(15));
                    var abort = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedAbort,
                        new JsonObject { ["_meta"] = identity.DeepClone() });
                    Assert.AreEqual("confirmed_precommit", (string?)abort?["_meta"]?["abortVerdict"], abort?.ToJsonString());
                    Assert.AreEqual("adapter", (string?)abort?["_meta"]?["abortOrigin"]);
                    var revision = (long)abort!["_meta"]!["abortRevision"]!;
                    Assert.IsFalse(ledger.CompactWithDurableConsumer(record.Stream!, record.Number, attempt, identity, _ => true));
                    var retireArgs = identity.DeepClone().AsObject(); retireArgs["receiptRevision"] = revision;
                    if (n == 0)
                    {
                        File.WriteAllText(Path.Combine(dir, "fail-completion-retire-record"), "");
                        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire,
                            new JsonObject { ["_meta"] = retireArgs.DeepClone() }));
                        Assert.IsNull(ledger.ReadAttempt(attempt)?["retiredRevision"]);
                    }
                    var retired = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire,
                        new JsonObject { ["_meta"] = retireArgs.DeepClone() });
                    Assert.AreEqual(revision, (long?)retired?["_meta"]?["retiredRevision"]);
                    File.WriteAllText(releasePath, "");
                    await pending.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.AreEqual(0, InputCount(child, Guid.ParseExact(attempt, "N").ToString("D")), "held submit must not write after retirement");
                    Assert.IsTrue(ledger.CompactWithDurableConsumer(record.Stream!, record.Number, attempt, identity, outcome =>
                        outcome == "not_sent" && service.ReplyMailbox.SettleCompletionReminder("delegation", id, parent, child, generation, 1, attempt, CompletionReminderSettlement.ProvenNotSent)));
                    await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit,
                        new JsonObject { ["_meta"] = fields.DeepClone() }));
                }
                finally { File.WriteAllText(releasePath, ""); }
            }
            Assert.AreEqual(4, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length, "only deliberate initial inputs");
        }
        finally { File.WriteAllText(releasePath, ""); await launcher.CloseNativeGracefully(); }
    }

    [TestMethod]
    [DataRow("enabled")]
    [DataRow("disabled")]
    [DataRow("partial")]
    [DataRow("wrong-instance")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ServiceExecutableCompletionLaunchScope(string configuration)
    {
        var data = TestDirs.New(); DataFolder.Claim(data);
        var meta = new UiMetadataStore(Path.Combine(data, "ui-metadata.json"));
        meta.Set("project", "group", "Group"); var instance = meta.EnsureGroupInstance("project", "group")!;
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Gorynych.slnx"))) repo = repo.Parent;
        var executable = Directory.EnumerateFiles(Path.Combine(repo!.FullName, "src", "Gorynych.Service", "bin"),
            TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        var pipe = "completion-launch-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "--data", data, "--pipe", pipe, "--hosts", "on", "--no-channel" }) start.ArgumentList.Add(value);
        if (configuration != "disabled") { start.ArgumentList.Add("--completion-project"); start.ArgumentList.Add("project"); }
        if (configuration is "enabled" or "wrong-instance")
        {
            start.ArgumentList.Add("--completion-group"); start.ArgumentList.Add("group");
            start.ArgumentList.Add("--completion-instance"); start.ArgumentList.Add(configuration == "enabled" ? instance : Guid.NewGuid().ToString("N"));
        }
        using var process = Process.Start(start)!;
        await using var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(15000);
        await using var rpc = new JsonRpcConnection(stream, stream, "completion-launch"); rpc.Start();
        var initialized = await rpc.SendRequestAsync(AcpMethods.Agent.Initialize,
            new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
        Assert.AreEqual(configuration == "enabled", (bool?)initialized?["agentCapabilities"]?["_meta"]?[GorynychProtocol.CapCompletionOwnerTrigger]);
        var persisted = ServiceOptions.Read(data).ToArray();
        Assert.AreEqual(configuration != "disabled", persisted.Contains("--completion-project"));
        Assert.AreEqual(configuration is "enabled" or "wrong-instance", persisted.Contains("--completion-instance"));
        if (configuration == "enabled") CollectionAssert.IsSubsetOf(new[] { "--completion-project", "project", "--completion-group", "group", "--completion-instance", instance }, persisted);
        await rpc.SendRequestAsync(GorynychProtocol.MethodShutdown, new JsonObject());
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(0, process.ExitCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task OwnerReconcilesLateLiveTerminalAfterReopenWithoutOptIn(bool staleBuild)
    {
        var dir = TestDirs.New(); var data = TestDirs.New(); File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var metadata = new UiMetadataStore(Path.Combine(data, "ui-metadata.json")); metadata.Set("project", "group", "Group");
        var instance = metadata.EnsureGroupInstance("project", "group")!; var ledgerPath = Path.Combine(dir, "live-recovery-ledger.json");
        var clock = new ManualTime(); var pipe = "completion-live-recovery-" + Guid.NewGuid().ToString("N");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Gorynych.slnx"))) repository = repository.Parent;
        var serviceExe = Directory.EnumerateFiles(Path.Combine(repository!.FullName, "src", "Gorynych.Service", "bin"),
            TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        var launcher = new CompletionProcessHost(dir, ledgerPath, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "live-recovery", Time = clock,
            Hosts = launcher, HostToken = InProcessHosts.Token, CompletionConfiguration = ("project", "group", instance),
            TestCompletionLedgerPath = ledgerPath, TestCompletionObservationTimeout = TimeSpan.FromMilliseconds(400),
            PeerServerCommand = [serviceExe, "peers", "--pipe", pipe] }, [adapter]);
        launcher.Service = service; using var stop = new CancellationTokenSource(); var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        var client = await TestClient.ConnectAsync(service); Process? peer = null; JsonRpcConnection? mcp = null; var nativeClosed = false;
        var releasePath = Path.Combine(dir, "release-completion-result");
        Exception? originalFailure = null;
        async Task Cleanup(Func<Task> action)
        {
            try { await action(); }
            catch (Exception e) when (originalFailure is not null)
            { Console.Error.WriteLine($"Cleanup after test failure: {e}"); }
        }
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(child, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = false; }, durable: true);
            foreach (var sid in new[] { parent, child }) await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                new JsonObject { ["sessionId"] = sid, ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" } } });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial delegation",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken, [GorynychProtocol.MetaMessageRequestId] = "late-assignment" } });
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("late-assignment", parent)?["delivery"] == "delivered" &&
                service.Feed(child).TurnGate.CurrentCount == 1, "late delegation settled").WaitAsync(TimeSpan.FromSeconds(20));
            var generation = (string)service.ReplyMailbox.FindAssignment("late-assignment", parent)!["generation"]!;
            var ownerToken = service.Store.Get(parent)!.PeerToken!; var childToken = service.Store.Get(child)!.PeerToken!;
            var oldRoute = service.Agents["claude"].RouteOf(child)!; Assert.AreEqual(launcher.Pid, oldRoute.ProcessId);
            var psi = new ProcessStartInfo(serviceExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "peers", "--pipe", pipe, "--token", ownerToken }) psi.ArgumentList.Add(a);
            peer = Process.Start(psi)!; mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "late-mcp"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            clock.Advance(TimeSpan.FromSeconds(5)); File.WriteAllText(Path.Combine(dir, "hold-completion-result"), "");
            var initial = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "request_completion_reminder",
                ["arguments"] = new JsonObject { ["assignment_id"] = "late-assignment", ["generation"] = generation } });
            var initialBody = JsonNode.Parse((string)initial!["content"]![0]!["text"]!)!;
            Assert.AreEqual("uncertain", (string?)initialBody["state"], initialBody.ToJsonString());
            Assert.AreEqual(true, (bool?)initialBody["nativeTaken"], initialBody.ToJsonString());
            var attempt = (string)initialBody["attemptId"]!;
            Assert.AreEqual(1, InputCount(child, Guid.ParseExact(attempt, "N").ToString("D")));
            JsonObject Recovery(string token) => new() { ["assignmentId"] = "late-assignment", ["generation"] = generation,
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } };
            var before = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRecoverCompletionReminder, Recovery(ownerToken));
            Assert.AreEqual("unresolved", (string?)before?["state"]); Assert.AreEqual(true, (bool?)before?["nativeTaken"]);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodRecoverCompletionReminder, Recovery(childToken)));
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, new JsonObject { ["source"] = "delegation",
                ["assignmentId"] = "late-assignment", ["generation"] = generation, ["expectedRevision"] = 1, ["operationId"] = "late-complete",
                ["outcome"] = "completed", ["summary"] = "completed before native terminal",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = childToken } });
            var ownerInputPath = Path.Combine(FakeRoot, parent + ".inputs.jsonl");
            await LinkHarness.Until(() =>
            {
                var notice = service.ReplyMailbox.FindAssignment("late-assignment", parent)?["coordinatorNotice"];
                var feed = service.Feed(parent);
                return (string?)notice?["delivery"] == "delivered" && feed.TurnGate.CurrentCount == 1 &&
                    !feed.AgentTurnRunning && feed.Asking == 0 && File.Exists(ownerInputPath);
            }, "the exact owner outcome notice completed before reconnect").WaitAsync(TimeSpan.FromSeconds(20));
            var ownerInputs = File.ReadAllLines(ownerInputPath);
            Assert.AreEqual(1, ownerInputs.Length, "exactly one owner notice was sent");
            StringAssert.Contains(ownerInputs[0], "late-assignment");
            StringAssert.Contains(ownerInputs[0], "completed before native terminal");
            launcher.Service = null; launcher.BreakConnection();
            await LinkHarness.Until(() => oldRoute.Rpc.IsClosed, "old recovery route disconnected").WaitAsync(TimeSpan.FromSeconds(15));
            await client.DisposeAsync(); await service.DisposeAsync();
            var reopened = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "live-recovery", Time = clock,
                Hosts = launcher, HostToken = InProcessHosts.Token, TestCompletionLedgerPath = ledgerPath,
                HostGeneration = staleBuild ? _ => new HostGeneration("build-2", []) : null }, [adapter]);
            // Make the transient Wait path deterministic: the stale route may
            // reattach while an owner turn still has its gate.
            var parentGateHeld = false; TestClient? after = null;
            try
            {
                if (staleBuild) { await reopened.Feed(parent).TurnGate.WaitAsync(); parentGateHeld = true; }
                launcher.Service = reopened; after = await TestClient.ConnectAsync(reopened);
                await LinkHarness.Until(() => reopened.Agents["claude"].RouteOf(child) is not null, "original host reattached").WaitAsync(TimeSpan.FromSeconds(20));
                var reattached = reopened.Agents["claude"].RouteOf(child)!;
                Assert.AreEqual(oldRoute.HostId, reattached.HostId); Assert.AreEqual(oldRoute.ProcessId, reattached.ProcessId);
                Assert.AreEqual(!staleBuild, reattached.Current, "the original host must have an actually stale generation");
                Assert.IsFalse(reopened.CompletionOwnerTriggerAvailable);
                if (staleBuild)
                {
                    Assert.AreEqual(Conductor.Move.Stay, await reopened.TryMoveAsync(child, false, CancellationToken.None),
                        "unresolved exact producer must not be migrated");
                    Assert.AreEqual(oldRoute.HostId, reopened.Agents["claude"].RouteOf(child)?.HostId);
                    Assert.AreEqual(Conductor.Move.Wait, await reopened.TryMoveAsync(parent, false, CancellationToken.None),
                        "a held owner turn is a transient wait, not a successful move");
                    Assert.AreSame(reattached, reopened.Agents["claude"].RouteOf(parent), "the waiting owner remains on the original route");
                    reopened.Feed(parent).TurnGate.Release(); parentGateHeld = false;
                    // A concurrent inbox or stale-migration worker can transiently hold
                    // the owner's turn gate. Wait for that exact gate and native turn;
                    // Wait itself is never accepted as successful migration.
                    using var parentMoveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var parentFeed = reopened.Feed(parent);
                    while (true)
                    {
                        var moved = await reopened.TryMoveAsync(parent, false, parentMoveTimeout.Token);
                        if (moved == Conductor.Move.Done) break;
                        Assert.AreEqual(Conductor.Move.Wait, moved, "the settled owner must not be pinned");
                        await parentFeed.TurnGate.WaitAsync(parentMoveTimeout.Token); parentFeed.TurnGate.Release();
                        await LinkHarness.Until(() => !parentFeed.AgentTurnRunning && parentFeed.Asking == 0,
                            "owner native turn is idle").WaitAsync(parentMoveTimeout.Token);
                        await Task.Yield();
                    }
                    Assert.IsFalse(ReferenceEquals(reattached, reopened.Agents["claude"].RouteOf(parent)),
                        "the unrelated owner must leave the original stale route");
                    Assert.AreEqual(1, File.ReadAllLines(ownerInputPath).Length, "owner notice is not replayed during migration");
                }
                File.WriteAllText(Path.Combine(dir, "fail-completion-ack-record"), "");
                File.WriteAllText(Path.Combine(dir, "fail-completion-retire-record"), "");
                File.WriteAllText(releasePath, "");
                JsonNode? recovered = null; var sawTakenWhileUnresolved = (bool?)before?["nativeTaken"] == true;
                for (var i = 0; i < 80; i++)
                {
                    recovered = await after.Rpc.SendRequestAsync(GorynychProtocol.MethodRecoverCompletionReminder, Recovery(ownerToken));
                    if ((string?)recovered?["state"] == "settled" ||
                        staleBuild && (string?)recovered?["state"] == "native_ended") break;
                    if ((bool?)recovered?["nativeTaken"] == true) sawTakenWhileUnresolved = true;
                    await Task.Delay(50);
                }
                if (staleBuild && (string?)recovered?["state"] == "native_ended")
                {
                    // Automatic stale migration may have already settled this exact claim.
                    var durable = reopened.ReplyMailbox.ReadCompletionReminder("late-assignment", parent, child, generation);
                    Assert.IsNotNull(durable);
                    Assert.AreEqual(attempt, durable.AttemptId);
                    Assert.AreEqual(1L, durable.AssignmentRevision);
                    Assert.AreEqual("native_ended", durable.Disposition);
                }
                else
                {
                    Assert.AreEqual("settled", (string?)recovered?["state"], recovered?.ToJsonString());
                    Assert.AreEqual("native_ended", (string?)recovered?["disposition"]);
                }
                Assert.IsTrue(sawTakenWhileUnresolved, "native echo remains visible while acknowledgement is unresolved");
                Assert.IsFalse(File.Exists(Path.Combine(dir, "fail-completion-ack-record")), "host ACK persistence fault was exercised");
                Assert.IsFalse(File.Exists(Path.Combine(dir, "fail-completion-retire-record")), "host retire persistence fault was exercised");
                var repeated = await after.Rpc.SendRequestAsync(GorynychProtocol.MethodRecoverCompletionReminder, Recovery(ownerToken));
                Assert.AreEqual("native_ended", (string?)repeated?["state"]);
                Assert.AreEqual("completed", (string?)reopened.ReplyMailbox.FindAssignment("late-assignment", parent)?["status"]);
                Assert.AreEqual("native_ended", reopened.ReplyMailbox.ReadCompletionReminder("late-assignment", parent, child, generation)?.Disposition);
                Assert.AreEqual(1, InputCount(child, Guid.ParseExact(attempt, "N").ToString("D")));
                Assert.AreEqual(2, File.ReadAllLines(Path.Combine(FakeRoot, child + ".inputs.jsonl")).Length);
                Assert.AreEqual(1, File.ReadAllLines(ownerInputPath).Length, "recovery must not replay the owner notice");
                if (staleBuild)
                {
                    Assert.AreEqual(Conductor.Move.Done, await reopened.TryMoveAsync(child, false, CancellationToken.None),
                        "after exact settlement stale migration may proceed");
                    Assert.IsFalse(ReferenceEquals(reattached, reopened.Agents["claude"].RouteOf(child)),
                        "the child must no longer use the retiring route");
                    await launcher.WaitExited();
                }
                else await launcher.CloseNativeGracefully();
                nativeClosed = true;
            }
            catch (Exception e) { originalFailure = e; throw; }
            finally
            {
                if (parentGateHeld) reopened.Feed(parent).TurnGate.Release();
                if (after is not null) await Cleanup(async () => await after.DisposeAsync());
                await Cleanup(async () => await reopened.DisposeAsync());
            }
        }
        catch (Exception e) { originalFailure = e; throw; }
        finally
        {
            await Cleanup(() => { File.WriteAllText(releasePath, ""); return Task.CompletedTask; });
            if (mcp is not null) await Cleanup(async () => await mcp.DisposeAsync());
            if (peer is not null) await Cleanup(async () => { await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); });
            stop.Cancel(); await Cleanup(async () => { try { await listener; } catch (OperationCanceledException) { } });
            if (!nativeClosed) await Cleanup(async () =>
            {
                if (!launcher.IsAlive("claude", "")) await launcher.WaitExited();
                else await launcher.CloseNativeGracefully();
            });
            await Cleanup(async () => await client.DisposeAsync());
            await Cleanup(async () => await service.DisposeAsync());
            await Cleanup(async () => await launcher.DisposeAsync());
        }
    }

}
