using System.Diagnostics;
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
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task GroupObservationIsScopedAndNeverDispatches()
    {
        var dir = TestDirs.New(); var data = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        File.WriteAllText(Path.Combine(dir, "observation-background-probe"), "");
        var metadata = new UiMetadataStore(Path.Combine(data, "ui-metadata.json"));
        metadata.Set("project", "group", "Group");
        var instance = metadata.EnsureGroupInstance("project", "group")!;
        var ledgerPath = Path.Combine(dir, "observation-ledger.json"); var clock = new ManualTime();
        var pipe = "group-observation-" + Guid.NewGuid().ToString("N");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Gorynych.slnx"))) repository = repository.Parent;
        var serviceExe = Directory.EnumerateFiles(Path.Combine(repository!.FullName, "src", "Gorynych.Service", "bin"),
            TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        await using var launcher = new CompletionProcessHost(dir, ledgerPath, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "observe-host", Time = clock,
            Hosts = launcher, HostToken = InProcessHosts.Token, CompletionObservationConfiguration = ("project", "group", instance),
            TestCompletionLedgerPath = ledgerPath, PeerServerCommand = [serviceExe, "peers", "--pipe", pipe] }, [adapter]);
        launcher.Service = service;
        using var stop = new CancellationTokenSource(); var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        await using var client = await TestClient.ConnectAsync(service);
        Process? peer = null; JsonRpcConnection? mcp = null;
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var children = new string[6];
            for (var i = 0; i < children.Length; i++) children[i] = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            foreach (var child in children) service.Store.Update(child, "claude", r =>
                { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = false; }, durable: true);
            foreach (var sid in children.Prepend(parent)) await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch,
                new JsonObject { ["sessionId"] = sid, ["patch"] = new JsonObject {
                    [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" } } });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = children[5], ["text"] = "initial delegation",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken,
                    [GorynychProtocol.MetaMessageRequestId] = "observed" } });
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("observed", parent)?["delivery"] == "delivered" &&
                service.Feed(children[5]).TurnGate.CurrentCount == 1, "delegation settled").WaitAsync(TimeSpan.FromSeconds(20));
            for (var i = 0; i < 5; i++)
            {
                var id = "fixture-" + i;
                service.ReplyMailbox.Accept(id, parent, children[i], "private fixture", delegation: true);
                service.ReplyMailbox.MarkStarted(id, children[i]); service.ReplyMailbox.MarkDelivered(id, children[i]);
            }
            async Task RegisterWait(int ownerIndex, int otherIndex, string wait)
            {
                service.ReplyMailbox.Accept(wait, children[ownerIndex], children[otherIndex], "private wait", expectReply: true);
                service.ReplyMailbox.MarkStarted(wait, children[otherIndex]);
                service.ReplyMailbox.MarkDelivered(wait, children[otherIndex]);
                var assignment = service.ReplyMailbox.FindAssignment("fixture-" + ownerIndex, parent)!;
                await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, new JsonObject {
                    ["source"] = "delegation", ["assignmentId"] = "fixture-" + ownerIndex,
                    ["generation"] = (string?)assignment["generation"], ["expectedRevision"] = 1,
                    ["operationId"] = "register-" + wait, ["action"] = "register", ["messageId"] = wait,
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(children[ownerIndex])!.PeerToken } });
            }
            await RegisterWait(2, 3, "wait-2-3"); await RegisterWait(3, 2, "wait-3-2");
            var blocked = service.ReplyMailbox.FindAssignment("fixture-0", parent)!;
            service.ReplyMailbox.TransitionAssignment("fixture-0", children[0], 1, "blocked-op", "blocked",
                "private reason", generation: (string)blocked["generation"]!);
            var cancelled = service.ReplyMailbox.FindAssignment("fixture-1", parent)!;
            service.ReplyMailbox.TransitionAssignment("fixture-1", parent, 1, "cancel-op", "cancelled",
                "fixture cancelled", generation: (string)cancelled["generation"]!);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodObserveCompletionGroup, new JsonObject()));
            var psi = new ProcessStartInfo(serviceExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "peers", "--pipe", pipe, "--token", service.Store.Get(parent)!.PeerToken! }) psi.ArgumentList.Add(a);
            peer = Process.Start(psi)!; mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "observation-mcp"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            clock.Advance(TimeSpan.FromSeconds(5));
            var before = children.Select(c => File.Exists(Path.Combine(FakeRoot, c + ".inputs.jsonl"))
                ? File.ReadAllLines(Path.Combine(FakeRoot, c + ".inputs.jsonl")).Length : 0).ToArray();
            var snapshot = service.ReplyMailbox.AssignmentViews(parent).Select(x => x.ToJsonString()).Order().ToArray();
            var collected = new List<JsonNode>(); string? cursor = null;
            do
            {
                var args = new JsonObject { ["limit"] = 2 }; if (cursor is not null) args["cursor"] = cursor;
                var result = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "observe_completion_group", ["arguments"] = args });
                var body = JsonNode.Parse((string)result!["content"]![0]!["text"]!)!;
                Assert.AreEqual("observed", (string?)body["state"], body.ToJsonString());
                collected.AddRange(body["items"]!.AsArray().Select(x => x!.DeepClone()));
                cursor = (string?)body["nextCursor"];
            } while (cursor is not null);
            Assert.AreEqual(6, collected.Count);
            Assert.AreEqual("candidate", (string?)collected.Single(x => (string?)x["assignmentId"] == "observed")["decision"],
                string.Join("\n", collected.Select(x => x.ToJsonString())));
            Assert.AreEqual("explicit_blocked", (string?)collected.Single(x => (string?)x["assignmentId"] == "fixture-0")["reason"]);
            Assert.AreEqual("waiting_reply", (string?)collected.Single(x => (string?)x["assignmentId"] == "fixture-2")["reason"]);
            Assert.AreEqual("waiting_reply", (string?)collected.Single(x => (string?)x["assignmentId"] == "fixture-3")["reason"]);
            Assert.IsTrue(collected.Single(x => (string?)x["assignmentId"] == "fixture-1")["reason"]!.ToString().Contains("terminal"));
            Assert.AreEqual("defer", (string?)collected.Single(x => (string?)x["assignmentId"] == "fixture-4")["decision"]);
            Assert.IsFalse(collected.Any(x => x.ToJsonString().Contains("private fixture") || x.ToJsonString().Contains("private reason")));
            CollectionAssert.AreEqual(snapshot, service.ReplyMailbox.AssignmentViews(parent).Select(x => x.ToJsonString()).Order().ToArray());
            CollectionAssert.AreEqual(before, children.Select(c => File.Exists(Path.Combine(FakeRoot, c + ".inputs.jsonl"))
                ? File.ReadAllLines(Path.Combine(FakeRoot, c + ".inputs.jsonl")).Length : 0).ToArray());
            Assert.AreEqual(1, before.Sum());
            // A claim retained from a different selected group is global, even though
            // the observation scope and candidate have changed.
            var other = service.ReplyMailbox.FindAssignment("fixture-4", parent)!;
            var route = service.Agents["claude"].RouteOf(children[5])!;
            var binding = new CompletionReminderBinding("old-project", "old-group", Guid.NewGuid().ToString("N"), "old-prompt",
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), 1, new string('A', 64),
                route.HostId!, route.Generation!.Key, route.ProcessId, route.CompletionProcessStart);
            var oldAttempt = Guid.NewGuid().ToString("N");
            Assert.IsTrue(new CompletionHostLedger(ledgerPath).ClaimFromMailbox(service.ReplyMailbox, "delegation", "fixture-4",
                parent, children[4], (string)other["generation"]!, 1, oldAttempt, binding).Acquired);
            var pendingSnapshot = service.ReplyMailbox.AssignmentViews(parent).Select(x => x.ToJsonString()).Order().ToArray();
            var pendingView = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodObserveCompletionGroup, new JsonObject {
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken } });
            var pendingRow = pendingView!["items"]!.AsArray().Single(x => (string?)x?["assignmentId"] == "observed")!;
            Assert.AreEqual("previous_handoff_pending_or_unknown", (string?)pendingRow["reason"]);
            CollectionAssert.AreEqual(pendingSnapshot, service.ReplyMailbox.AssignmentViews(parent).Select(x => x.ToJsonString()).Order().ToArray());
            Assert.IsTrue(service.ReplyMailbox.SettleCompletionReminder("delegation", "fixture-4", parent, children[4],
                (string)other["generation"]!, 1, oldAttempt, CompletionReminderSettlement.ProvenNotSent));
            // This settlement is fixture setup, not part of the observation pass.
            snapshot = service.ReplyMailbox.AssignmentViews(parent).Select(x => x.ToJsonString()).Order().ToArray();

            service.AfterCompletionGroupPullForTests = () =>
            {
                service.Feed(children[5]).Active(clock.GetUtcNow());
                return Task.CompletedTask;
            };
            var tooRecent = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodObserveCompletionGroup, new JsonObject {
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken } });
            Assert.AreEqual("defer", (string?)tooRecent!["items"]!.AsArray().Single(x =>
                (string?)x?["assignmentId"] == "observed")!["decision"], "fresh activity after pull suppresses candidate");
            service.AfterCompletionGroupPullForTests = null; clock.Advance(TimeSpan.FromSeconds(5));

            // Hold the advisory read after its private pull. A real FakeClaude stdout
            // event reaches the host and conductor before the final evidence recheck.
            var pullSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishPull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.AfterCompletionGroupPullForTests = () => { pullSeen.TrySetResult(); return finishPull.Task; };
            var changedRead = client.Rpc.SendRequestAsync(GorynychProtocol.MethodObserveCompletionGroup, new JsonObject {
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken } });
            try
            {
                await pullSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
                File.WriteAllText(Path.Combine(dir, "emit-observation-background"), "");
                await LinkHarness.Until(() => service.Feed(children[5]).HasBackgroundWork, "native background transition").WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { finishPull.TrySetResult(); }
            var changed = await changedRead.WaitAsync(TimeSpan.FromSeconds(15));
            service.AfterCompletionGroupPullForTests = null;
            var changedRow = changed!["items"]!.AsArray().Single(x => (string?)x?["assignmentId"] == "observed")!;
            Assert.AreEqual("defer", (string?)changedRow["decision"]);
            Assert.AreEqual("evidence_or_assignment_changed", (string?)changedRow["reason"]);
            CollectionAssert.AreEqual(before, children.Select(c => File.Exists(Path.Combine(FakeRoot, c + ".inputs.jsonl"))
                ? File.ReadAllLines(Path.Combine(FakeRoot, c + ".inputs.jsonl")).Length : 0).ToArray());
            CollectionAssert.AreEqual(snapshot, service.ReplyMailbox.AssignmentViews(parent).Select(x => x.ToJsonString()).Order().ToArray());

            var childView = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodObserveCompletionGroup, new JsonObject {
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(children[4])!.PeerToken } });
            Assert.AreEqual(0, childView!["items"]!.AsArray().Count, "another member cannot inspect the coordinator assignments");
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodGroupsDelete, new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" });
            var disabled = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodObserveCompletionGroup, new JsonObject {
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken } });
            Assert.AreEqual("disabled", (string?)disabled!["state"]);
            Assert.AreEqual(0, disabled["items"]!.AsArray().Count);
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
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task GroupObservationReattachesWithoutOpeningOrSending()
    {
        var dir = TestDirs.New(); var data = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "background-supported"), "");
        var metadata = new UiMetadataStore(Path.Combine(data, "ui-metadata.json")); metadata.Set("project", "group", "Group");
        var instance = metadata.EnsureGroupInstance("project", "group")!;
        var ledger = Path.Combine(dir, "restart-ledger.json"); var clock = new ManualTime();
        await using var launcher = new CompletionProcessHost(dir, ledger, FakeClaude());
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        Conductor Create() => new(new ConductorOptions { DataDirectory = data, ServiceId = "observe-restart", Time = clock,
            Hosts = launcher, HostToken = InProcessHosts.Token, CompletionObservationConfiguration = ("project", "group", instance),
            TestCompletionLedgerPath = ledger }, [adapter]);
        var service = Create(); launcher.Service = service;
        var client = await TestClient.ConnectAsync(service);
        try
        {
            async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var parent = await New(); var child = await New(); var other = await New();
            service.Store.Update(parent, "claude", r => r.Draft = false, durable: true);
            foreach (var id in new[] { child, other })
                service.Store.Update(id, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = false; }, durable: true);
            foreach (var id in new[] { parent, child, other })
                await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesPatch, new JsonObject { ["sessionId"] = id,
                    ["patch"] = new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "project", ["groupId"] = "group" } } });
            var token = service.Store.Get(parent)!.PeerToken!;
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token, [GorynychProtocol.MetaMessageRequestId] = "original" } });
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("original", parent)?["delivery"] == "delivered" &&
                service.Feed(child).TurnGate.CurrentCount == 1, "original terminal").WaitAsync(TimeSpan.FromSeconds(20));
            service.ReplyMailbox.Accept("waiting", parent, other, "fixture", delegation: true);
            service.ReplyMailbox.MarkStarted("waiting", other); service.ReplyMailbox.MarkDelivered("waiting", other);
            service.ReplyMailbox.Accept("reply", other, child, "fixture", expectReply: true);
            service.ReplyMailbox.MarkStarted("reply", child); service.ReplyMailbox.MarkDelivered("reply", child);
            var generation = (string)service.ReplyMailbox.FindAssignment("waiting", parent)!["generation"]!;
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, new JsonObject { ["source"] = "delegation",
                ["assignmentId"] = "waiting", ["generation"] = generation, ["expectedRevision"] = 1,
                ["operationId"] = "wait", ["action"] = "register", ["messageId"] = "reply",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(other)!.PeerToken } });
            var saved = service.ReplyMailbox.AssignmentViews(parent).Select(x => x.ToJsonString()).Order().ToArray();
            var inputPath = Path.Combine(FakeRoot, child + ".inputs.jsonl"); var count = File.ReadAllLines(inputPath).Length;
            var oldRoute = service.Agents["claude"].RouteOf(child)!; var oldPid = launcher.Pid;
            await HostTests.RestartAsync(service);
            launcher.Service = null; launcher.BreakConnection();
            await client.DisposeAsync(); await service.DisposeAsync();
            service = Create(); launcher.Service = service;
            client = await TestClient.ConnectAsync(service);
            await LinkHarness.Until(() => service.Agents["claude"].RouteOf(child) is { Current: true }, "host reattached").WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual(oldPid, launcher.Pid); Assert.AreEqual(oldRoute.HostId, service.Agents["claude"].RouteOf(child)!.HostId);
            var view = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodObserveCompletionGroup, new JsonObject {
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } });
            var items = view!["items"]!.AsArray();
            Assert.AreEqual("waiting_reply", (string?)items.Single(x => (string?)x?["assignmentId"] == "waiting")!["reason"]);
            Assert.AreEqual("defer", (string?)items.Single(x => (string?)x?["assignmentId"] == "original")!["decision"]);
            CollectionAssert.AreEqual(saved, service.ReplyMailbox.AssignmentViews(parent).Select(x => x.ToJsonString()).Order().ToArray());
            Assert.AreEqual(count, File.ReadAllLines(inputPath).Length);
        }
        finally { await client.DisposeAsync(); await service.DisposeAsync(); await launcher.CloseNativeGracefully(); }
    }

    [TestMethod]
    public void CompletionObservationIdentityMustBeCanonical()
    {
        var id = "abcdefab-cdef-abcd-efab-cdefabcdefab";
        Assert.IsTrue(Conductor.ValidCompletionObservationIdentity(id, 0));
        Assert.IsFalse(Conductor.ValidCompletionObservationIdentity(id, -1));
        Assert.IsFalse(Conductor.ValidCompletionObservationIdentity(id.Replace("-", ""), 1));
        Assert.IsFalse(Conductor.ValidCompletionObservationIdentity(id.ToUpperInvariant(), 1));
        Assert.IsFalse(Conductor.ValidCompletionObservationIdentity(null, 1));
    }
}
