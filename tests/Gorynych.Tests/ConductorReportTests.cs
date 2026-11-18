using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ConductorReportTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    static async Task<(Conductor Service, InProcessHosts Hosts, TurnAgent Agent, TestClient Client, string Parent, string Child)> Rig(bool capture = true, Action<ReportCapture>? opened = null)
    {
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent) { CaptureReports = capture, CaptureOpened = opened };
        var service = hosts.Conductor(TestDirs.New(), agent); hosts.Service = service;
        var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(child, "turns", r => { r.ParentSessionId = parent; r.ReportToParent = true; r.SpawnAssignmentId = "older-assignment"; });
        service.Store.Update(parent, "turns", r => r.Archived = true);
        return (service, hosts, agent, client, parent, child);
    }
    static bool MailboxHasError(string directory, string id)
    {
        try { return (string?)new MailboxStore(Path.Combine(directory, "mailbox.json")).Find(id)?["error"] is not null; }
        catch (IOException) { return false; } // atomic replacement may briefly hold the file on Windows
    }
    static Task<JsonNode?> Prompt(TestClient client, string child) => client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt,
        new JsonObject { ["sessionId"] = child, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "work" }),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaAssignmentId] = "forged", [GorynychProtocol.MetaOrigin] = "report",
                [GorynychProtocol.MetaReportId] = Guid.NewGuid().ToString("N") } });

    [TestMethod]
    public async Task UserPromptImportsExactFinalAndIgnoresForgedProvenance()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            var text = new string('x', 40000) + "😀";
            agent.During = async (native, sid, _) =>
            {
                await native.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "discard" } });
                await native.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject { ["sessionId"] = sid,
                    ["update"] = new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t", ["title"] = "tool" } });
                await native.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = text } });
            };
            await Prompt(client, child).WaitAsync(Wait);
            var info = service.ReportStorage.List(child).Items.Single();
            Assert.AreEqual(ReportCause.User, info.Identity.Cause); Assert.IsNull(info.Identity.AssignmentId);
            Assert.AreEqual("pending", info.Status); Assert.IsTrue(info.ProducerAck);
            var rpc = service.Agents["turns"].RouteOf(child)!.Rpc;
            await rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject { [child] = 0 } }).WaitAsync(Wait);
            Assert.AreEqual(1, service.ReportStorage.List(child).Items.Count, "terminal replay and prompt response share one result");
            string? cursor = null; var pieces = new System.Text.StringBuilder();
            do { var page = service.ReportStorage.Read(info.ReportId, child, cursor); pieces.Append(page.Text); cursor = page.NextCursor; } while (cursor is not null);
            Assert.AreEqual(text + "done:work ", pieces.ToString());
        }
    }

    [TestMethod]
    public async Task ImportFaultReturnsPersistenceErrorAndRetainsSealedSpoolWithoutDuplicatePrompt()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            agent.During = (_, _, _) => { service.ReportStorage.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"status\":\"pending\"", StringComparison.Ordinal)) throw new IOException("manifest fault");
                AtomicFile.WriteAllText(path, json);
            }; return Task.CompletedTask; };
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Prompt(client, child).WaitAsync(Wait));
            StringAssert.Contains(error.Message, "durably imported");
            Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
            Assert.AreEqual(0, HostTests.EndMarks(service, child));
            service.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
            var pending = service.ReportStorage.Recoverable().Items.Single();
            using (var capture = service.ReportStorage.OpenCapture(pending.ReportId))
            { Assert.IsTrue(capture.Snapshot().Sealed); Assert.AreEqual("ended", capture.Snapshot().Outcome); }
            service.ImportReport(pending.ReportId);
            service.ImportReport(pending.ReportId);
            var imported = service.ReportStorage.List(child).Items.Single();
            Assert.AreEqual(pending.ReportId, imported.ReportId); Assert.IsTrue(imported.ProducerAck); Assert.AreEqual("pending", imported.Status);
        }
    }

    [TestMethod]
    public async Task ReservationFaultDoesNotDispatchAndReportFalseSkipsReservation()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            service.ReportStorage.WriteManifest = (_, _) => throw new IOException("capacity fault");
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Prompt(client, child).WaitAsync(Wait));
            StringAssert.Contains(error.Message, "no native prompt was sent");
            Assert.IsFalse(agent.Prompts.Contains("work"));
            service.Store.Update(child, "turns", r => r.ReportToParent = false);
            await Prompt(client, child).WaitAsync(Wait);
            Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
            Assert.AreEqual(0, service.ReportStorage.List(child).Items.Count);
        }
    }
    [TestMethod]
    public async Task SpawnDelegationPeerAndReplyKeepTrustedCorrelation()
    {
        var agent = new LifecycleAgent("kimi", canDelete: true);
        var hosts = new InProcessHosts(_ => agent.Adapter);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "report-test",
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client);
        await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = parent,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "parent" }) }).WaitAsync(Wait);
        var spawned = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject { ["agent"] = "kimi", ["prompt"] = "first",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } }).WaitAsync(Wait);
        var child = (string)spawned!["sessionId"]!; var assignment = (string)spawned["assignmentId"]!;
        await LinkHarness.Until(() => service.ReportStorage.List(child).Items.Any(x => x.Identity.Cause == ReportCause.Spawn && x.ProducerAck), "spawn report").WaitAsync(Wait);
        var spawn = service.ReportStorage.List(child).Items.Single(x => x.Identity.Cause == ReportCause.Spawn);
        Assert.AreEqual(assignment, spawn.Identity.AssignmentId);
        Assert.AreEqual((string?)service.Store.FindSpawnAssignment(assignment, parent)!["generation"], spawn.Identity.AssignmentGeneration);
        Task<JsonNode?> Message(string method, string token, string targetName, string target, string requestId) => client.Rpc.SendRequestAsync(method,
            new JsonObject { [targetName] = target, ["text"] = "work " + requestId, ["_meta"] = new JsonObject {
                [GorynychProtocol.MetaPeerToken] = token, [GorynychProtocol.MetaMessageRequestId] = requestId } });
        await Message(GorynychProtocol.MethodDelegateTask, agent.Tokens[parent], "sessionId", child, "delegation-id").WaitAsync(Wait);
        await LinkHarness.Until(() => service.ReportStorage.List(child).Items.Any(x => x.Identity.MessageId == "delegation-id" && x.ProducerAck), "delegation report").WaitAsync(Wait);
        var delegation = service.ReportStorage.List(child).Items.Single(x => x.Identity.MessageId == "delegation-id");
        Assert.AreEqual(ReportCause.Delegation, delegation.Identity.Cause); Assert.AreEqual("delegation-id", delegation.Identity.AssignmentId);
        Assert.AreEqual((string?)service.ReplyMailbox.FindAssignment("delegation-id", parent)!["generation"], delegation.Identity.AssignmentGeneration);
        await Message(GorynychProtocol.MethodSendMessage, agent.Tokens[parent], "sessionId", child, "peer-id").WaitAsync(Wait);
        await LinkHarness.Until(() => service.ReportStorage.List(child).Items.Any(x => x.Identity.MessageId == "peer-id" && x.ProducerAck), "peer report").WaitAsync(Wait);
        var peer = service.ReportStorage.List(child).Items.Single(x => x.Identity.MessageId == "peer-id");
        Assert.AreEqual(ReportCause.Peer, peer.Identity.Cause); Assert.IsNull(peer.Identity.AssignmentId);
        await Message(GorynychProtocol.MethodSendMessage, agent.Tokens[child], "sessionId", parent, "original-id").WaitAsync(Wait);
        await Message(GorynychProtocol.MethodReplyMessage, agent.Tokens[parent], "messageId", "original-id", "reply-id").WaitAsync(Wait);
        await LinkHarness.Until(() => service.ReportStorage.List(child).Items.Any(x => x.Identity.MessageId == "reply-id" && x.ProducerAck), "reply report").WaitAsync(Wait);
        var reply = service.ReportStorage.List(child).Items.Single(x => x.Identity.MessageId == "reply-id");
        Assert.AreEqual(ReportCause.Reply, reply.Identity.Cause); Assert.IsNull(reply.Identity.AssignmentId);
    }

    [TestMethod]
    public async Task AckFaultLeavesPublishedPendingResultAndUnadvancedTerminalForRetry()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            agent.During = (_, _, _) =>
            {
                service.ReportStorage.WriteManifest = (path, json) =>
                {
                    if (json.Contains("\"producerAck\":true", StringComparison.Ordinal)) throw new IOException("ACK fault");
                    AtomicFile.WriteAllText(path, json);
                };
                return Task.CompletedTask;
            };
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Prompt(client, child).WaitAsync(Wait));
            StringAssert.Contains(error.Message, "ACK fault");
            var info = service.ReportStorage.List(child).Items.Single();
            Assert.AreEqual("pending", info.Status); Assert.IsFalse(info.ProducerAck);
            Assert.AreEqual(0, HostTests.EndMarks(service, child));
            service.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => service.ImportReport(info.ReportId)))).WaitAsync(Wait);
            Assert.IsTrue(service.ReportStorage.GetReport(info.ReportId).ProducerAck);
            Assert.AreEqual(1, service.ReportStorage.List(child).Items.Count);
            Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
        }
    }

    [TestMethod]
    public async Task NativeFailureIsImportedBeforeErrorResponse()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            agent.During = (_, _, _) => throw new JsonRpcException(JsonRpcException.InternalError, "native failed");
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Prompt(client, child).WaitAsync(Wait));
            StringAssert.Contains(error.Message, "native failed");
            var info = service.ReportStorage.List(child).Items.Single();
            Assert.IsTrue(info.ProducerAck); var result = service.ReportStorage.Read(info.ReportId, child);
            Assert.AreEqual("failed", result.Outcome); StringAssert.Contains(result.Error!, "native failed");
        }
    }

    [TestMethod]
    public async Task ReportOnlyParentTurnDoesNotGenerateAnotherResult()
    {
        var (service, _, agent, client, parent, child) = await Rig();
        await using (service) await using (client)
        {
            service.Store.Update(parent, "turns", r => { r.Archived = false; r.ParentSessionId = "grandparent"; r.ReportToParent = true; });
            await Prompt(client, child).WaitAsync(Wait);
            await LinkHarness.Until(() => agent.Prompts.Any(p => p.StartsWith("[Report", StringComparison.Ordinal)), "parent report prompt").WaitAsync(Wait);
            await LinkHarness.Until(() => !service.Feed(parent).TurnRunning, "parent report end").WaitAsync(Wait);
            Assert.AreEqual(0, service.ReportStorage.List(parent).Items.Count(x => x.Identity.Child == parent));
        }
    }

    [TestMethod]
    public async Task NativeCancellationIsImportedBeforeResponse()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            agent.Hold = true;
            var prompt = Prompt(client, child);
            await LinkHarness.Until(() => agent.Prompts.Contains("work"), "native turn started").WaitAsync(Wait);
            await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = child });
            var response = await prompt.WaitAsync(Wait);
            Assert.AreEqual("cancelled", (string?)response?["stopReason"]);
            var info = service.ReportStorage.List(child).Items.Single();
            Assert.IsTrue(info.ProducerAck);
            Assert.AreEqual("cancelled", service.ReportStorage.Read(info.ReportId, child).Outcome);
        }
    }

    [TestMethod]
    public async Task ExhaustedReservationBudgetRefusesBeforeNativeDispatch()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            for (var i = 0; i < 15; i++) service.ReportStorage.Reserve(new ReportIdentity("blocked-" + i, "other", "parent", ReportCause.User, "p" + i));
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Prompt(client, child).WaitAsync(Wait));
            StringAssert.Contains(error.Message, "no native prompt was sent");
            Assert.IsFalse(agent.Prompts.Contains("work"));
        }
    }

    [TestMethod]
    public async Task LegacyHostWithoutCapturePublishesExplicitIncompleteResult()
    {
        var (service, _, agent, client, _, child) = await Rig(capture: false);
        await using (service) await using (client)
        {
            await Prompt(client, child).WaitAsync(Wait);
            Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
            var report = service.ReportStorage.List(child).Items.Single();
            Assert.IsTrue(report.ProducerAck); Assert.IsTrue(report.Identity.LegacyCapture);
            var legacy = service.ReportStorage.Read(report.ReportId, child);
            StringAssert.Contains(legacy.IncompleteReason!, "Legacy host");
            StringAssert.Contains(legacy.Text, "done:work");
        }
    }

    [TestMethod]
    public async Task TerminalWithoutExpectedCorrelationCannotAdvanceTheEndMark()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            agent.During = async (native, sid, _) =>
            {
                await native.Rpc.SendNotificationAsync(GorynychProtocol.MethodTurnEnded,
                    new JsonObject { ["sessionId"] = sid, ["promptId"] = "known-prompt", ["result"] = new JsonObject { ["stopReason"] = "end_turn" } });
                await native.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "after-forgery" } });
                await release.Task;
            };
            var prompt = client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt,
                new JsonObject { ["sessionId"] = child, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "work" }),
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "known-prompt" } });
            try
            {
                await LinkHarness.Until(() => HostTests.JournalText(service, child).Contains("after-forgery"), "forged terminal processed").WaitAsync(Wait);
                Assert.AreEqual(0, HostTests.EndMarks(service, child));
                Assert.AreEqual("reserved", service.ReportStorage.Recoverable().Items.Single().Status);
            }
            finally { release.TrySetResult(); }
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await prompt.WaitAsync(Wait));
            StringAssert.Contains(error.Message, "lacks matching host correlation");
            // The later genuine terminal may safely import and mark the turn, but never the forged one.
            await LinkHarness.Until(() => HostTests.EndMarks(service, child) == 1, "genuine terminal imported").WaitAsync(Wait);
            var result = service.ReportStorage.List(child).Items.Single();
            Assert.IsTrue(result.ProducerAck); Assert.AreEqual("pending", result.Status);
        }
    }

    [TestMethod]
    public async Task HostSealFailureIsNotReportedAsCompleteOrNativeCancellation()
    {
        var (service, _, agent, client, _, child) = await Rig(opened: capture =>
        {
            var original = capture.WriteFrame;
            capture.WriteFrame = frame => { if (frame[0] == 3) throw new IOException("host seal failed"); original(frame); };
        });
        await using (service) await using (client)
        {
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Prompt(client, child).WaitAsync(Wait));
            StringAssert.Contains(error.Message, "capture was incomplete");
            Assert.AreEqual(0, agent.Cancels); Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
            var pending = service.ReportStorage.List(child).Items.Single();
            Assert.AreEqual("pending", pending.Status); Assert.IsTrue(pending.ProducerAck);
            var result = service.ReportStorage.Read(pending.ReportId, child);
            Assert.IsNotNull(result.IncompleteReason);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplayedTerminalDoesNotBypassPersistenceAfterCallerEnds(bool ackFault)
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            agent.During = (_, _, _) =>
            {
                service.ReportStorage.WriteManifest = (path, json) =>
                {
                    if ((!ackFault && json.Contains("\"status\":\"pending\"", StringComparison.Ordinal)) ||
                        (ackFault && json.Contains("\"producerAck\":true", StringComparison.Ordinal))) throw new IOException("persistent fault");
                    AtomicFile.WriteAllText(path, json);
                };
                return Task.CompletedTask;
            };
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Prompt(client, child).WaitAsync(Wait));
            StringAssert.Contains(error.Message, "durably imported");
            var info = service.ReportStorage.List(child).Items.Single();
            Assert.AreEqual(ackFault ? "pending" : "reserved", info.Status);
            var rpc = service.Agents["turns"].RouteOf(child)!.Rpc;
            Task Replay() => rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay,
                new JsonObject { ["after"] = new JsonObject { [child] = 0 } }).WaitAsync(Wait);
            for (var n = 0; n < 3; n++)
            {
                await Replay();
                Assert.AreEqual(0, HostTests.EndMarks(service, child));
                Assert.IsFalse(service.ReportStorage.GetReport(info.ReportId).ProducerAck);
            }
            service.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
            await Replay(); await Replay();
            Assert.AreEqual(1, HostTests.EndMarks(service, child));
            Assert.IsTrue(service.ReportStorage.GetReport(info.ReportId).ProducerAck);
            Assert.AreEqual(1, service.ReportStorage.List(child).Items.Count);
            Assert.AreEqual(1, agent.Prompts.Count(p => p == "work"));
        }
    }

    [TestMethod]
    public async Task RepeatedTrustedHostRefusalsReleaseOnlyNewReservations()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            agent.ResumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rpc = service.Agents["turns"].RouteOf(child)!.Rpc;
            var history = rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = child });
            try
            {
                await LinkHarness.Until(() => agent.Resumes.Count > 0, "held native history").WaitAsync(Wait);
                for (var n = 0; n < 18; n++)
                {
                    var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Prompt(client, child).WaitAsync(Wait));
                    StringAssert.Contains(error.Message, "history operation");
                }
                Assert.IsFalse(agent.Prompts.Contains("work"));
                Assert.AreEqual(0, service.ReportStorage.Recoverable().Items.Count);
                Assert.AreEqual(0, service.ReportStorage.List(child).Items.Count);
            }
            finally { agent.ResumeGate.TrySetResult(); }
            await history.WaitAsync(Wait);
        }
    }

    [TestMethod]
    public async Task LocalMailboxPreDispatchFaultReleasesRepeatedNewReservations()
    {
        var directory = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        var hosts = new InProcessHosts(_ => agent.Adapter);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = directory, ServiceId = "pre-dispatch",
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        hosts.Service = service; await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(child, "kimi", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        service.BeforeMailboxMarkStarted = () => throw new IOException("local pre-start fault");
        for (var n = 0; n < 18; n++)
        {
            var id = "pre-start-" + n;
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodSendMessage, new JsonObject { ["sessionId"] = child, ["text"] = "work",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent], [GorynychProtocol.MetaMessageRequestId] = id } }).WaitAsync(Wait);
            await LinkHarness.Until(() => MailboxHasError(directory, id), "mailbox failure persisted").WaitAsync(Wait);
        }
        Assert.AreEqual(0, service.ReportStorage.List(child).Items.Count);
        lock (agent.Prompts) Assert.AreEqual(0, agent.Prompts.Count(p => p.SessionId == child));
    }

    [TestMethod]
    public async Task ExistingPossiblyStartedReservationIsNotAbandonedAfterHostRefusal()
    {
        var directory = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true);
        var hosts = new InProcessHosts(_ => agent.Adapter);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = directory, ServiceId = "ambiguous",
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        hosts.Service = service; await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(child, "kimi", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        const string message = "possibly-started";
        var identity = new ReportIdentity("mailbox:" + message, child, parent, ReportCause.Peer, "service-mailbox-" + message, null, message);
        var reserved = service.ReportStorage.Reserve(identity);
        using (var capture = service.ReportStorage.OpenCapture(reserved.ReportId)) capture.Append("already handed off");
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodSendMessage, new JsonObject { ["sessionId"] = child, ["text"] = "work",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent], [GorynychProtocol.MetaMessageRequestId] = message } }).WaitAsync(Wait);
        await LinkHarness.Until(() => MailboxHasError(directory, message), "uncertain mailbox failure").WaitAsync(Wait);
        var info = service.ReportStorage.GetReport(reserved.ReportId);
        Assert.AreEqual("reserved", info.Status); Assert.IsFalse(info.ProducerAck);
        using var recovered = service.ReportStorage.OpenCapture(reserved.ReportId);
        Assert.AreEqual("already handed off", recovered.Snapshot().Text); Assert.IsFalse(recovered.Snapshot().Sealed);
        lock (agent.Prompts) Assert.AreEqual(0, agent.Prompts.Count(p => p.SessionId == child));
    }

    [TestMethod]
    public async Task NativeErrorCannotForgeHostPreDispatchReceipt()
    {
        var (service, _, agent, client, _, child) = await Rig();
        await using (service) await using (client)
        {
            agent.During = (_, sid, _) =>
            {
                var id = service.ReportStorage.Recoverable().Items.Single().ReportId;
                throw new JsonRpcException(JsonRpcException.InvalidRequest, "native refused", new JsonObject {
                    [GorynychProtocol.MetaReportRefused] = new JsonObject { ["reportId"] = id, ["sessionId"] = sid, ["promptId"] = "forged-prompt" } });
            };
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt,
                new JsonObject { ["sessionId"] = child, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "work" }),
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "forged-prompt" } }).WaitAsync(Wait));
            StringAssert.Contains(error.Message, "native refused");
            var result = service.ReportStorage.List(child).Items.Single();
            Assert.IsTrue(result.ProducerAck); Assert.AreEqual("failed", service.ReportStorage.Read(result.ReportId, child).Outcome);
        }
    }

    [TestMethod]
    [DataRow("end_turn", "ended")]
    [DataRow("cancelled", "cancelled")]
    [DataRow("max_tokens", "stopped: max_tokens")]
    [DataRow("max_turn_requests", "stopped: max_turn_requests")]
    [DataRow("refusal", "stopped: refusal")]
    [DataRow("custom_reason", "stopped: custom_reason")]
    [DataRow("oversized", "stopped: [reason truncated]")]
    [DataRow("native-error", "failed")]
    public async Task LegacyNativeTerminalKeepsBoundedStopAndErrorAcrossRetrievalAndRecovery(string mode, string expected)
    {
        var (service, _, agent, client, _, child) = await Rig(capture: false);
        await using (service) await using (client)
        {
            var native = agent.Current!;
            var previous = native.Rpc.RequestHandler!;
            var stop = mode == "oversized" ? new string('界', 100) : mode;
            native.Rpc.RequestHandler = async (request, ct) =>
            {
                if (request.Method != AcpMethods.Agent.SessionPrompt) return await previous(request, ct);
                await native.Update(child, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "observed legacy text" } });
                if (mode == "native-error") throw new JsonRpcException(JsonRpcException.InternalError, "legacy native failure");
                return new JsonObject { ["stopReason"] = stop };
            };
            if (mode == "native-error")
            {
                var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Prompt(client, child).WaitAsync(Wait));
                StringAssert.Contains(error.Message, "legacy native failure");
            }
            else
            {
                var response = await Prompt(client, child).WaitAsync(Wait);
                Assert.AreEqual(stop, (string?)response?["stopReason"]);
            }
            var info = service.ReportStorage.List(child).Items.Single();
            Assert.IsTrue(info.ProducerAck); Assert.IsTrue(info.EndJournaled); Assert.IsTrue(info.Identity.LegacyCapture);
            var page = service.ReportStorage.Read(info.ReportId, child);
            Assert.AreEqual(expected, page.Outcome); Assert.AreEqual("pending", page.Status);
            Assert.AreEqual("observed legacy text", page.Text); Assert.IsNotNull(page.IncompleteReason);
            if (mode == "native-error") Assert.AreEqual("legacy native failure", page.Error); else Assert.IsNull(page.Error);
            await service.ReconcileReportInventoryAsync();
            var reopened = new ReportStore(Path.Combine((string)service.Status()["dataDirectory"]!, "reports"));
            var reread = reopened.Read(info.ReportId, child);
            Assert.AreEqual(page.Outcome, reread.Outcome); Assert.AreEqual(page.Error, reread.Error);
            Assert.AreEqual(page.Text, reread.Text); Assert.AreEqual(page.IncompleteReason, reread.IncompleteReason);
            Assert.AreEqual(1, reopened.List(child).Items.Count); Assert.AreEqual(1, HostTests.EndMarks(service, child));
        }
    }

    [TestMethod]
    public async Task ReportRetrievalUsesAuthenticatedImmutableParticipantsAndExactPages()
    {
        var (service, _, agent, client, parent, child) = await Rig();
        await using (service) await using (client)
        {
            var sibling = await HostTests.NewSession(client);
            var grandparent = await HostTests.NewSession(client);
            service.Store.Update(parent, "turns", r => r.ParentSessionId = grandparent);
            var text = new string('x', 33000) + "😀終";
            agent.During = async (native, sid, _) => await native.Update(sid,
                new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = text } });
            await Prompt(client, child).WaitAsync(Wait);
            var info = service.ReportStorage.List(child).Items.Single();
            string Token(string sid) => service.Store.Get(sid)!.PeerToken!;
            Task<JsonNode?> Read(string sid, string id, string? cursor = null, int limit = 4096) => client.Rpc.SendRequestAsync(
                GorynychProtocol.MethodReadReport, new JsonObject { ["reportId"] = id, ["limit"] = limit,
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(sid) },
                    ["cursor"] = cursor ?? id + ":0" });
            var result = new System.Text.StringBuilder(); string? next = null;
            do { var page = await Read(parent, info.ReportId, next); result.Append((string?)page!["text"]); next = (string?)page["nextCursor"]; } while (next is not null);
            Assert.AreEqual("started:work " + text + "done:work ", result.ToString());
            var listed = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListReports, new JsonObject
            { ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(parent) } });
            Assert.AreEqual(info.ReportId, (string?)listed!["items"]![0]!["reportId"]);
            Assert.IsNull(listed["items"]![0]!["text"]);
            var status = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject());
            Assert.AreEqual(1, (int?)status!["_meta"]!["gorynych/reportDelivery"]!["blocked"]);
            var preview = service.ReportEnvelope(info.ReportId, parent);
            StringAssert.Contains(preview, "read_report"); StringAssert.Contains(preview, info.ReportId);
            StringAssert.Contains(preview, "preview truncated: True");
            Assert.IsTrue(preview[(preview.IndexOf('\n') + 1)..].Length <= 2000);
            var first = await Read(parent, info.ReportId);
            Assert.AreEqual("user", (string?)first!["identity"]!["cause"]);
            Assert.AreEqual("pending", (string?)first["deliveryStatus"]);
            Assert.IsTrue((bool)first["hasText"]!);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Read(sibling, info.ReportId));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Read(grandparent, info.ReportId));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadReport,
                new JsonObject { ["reportId"] = info.ReportId, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = "invalid-token" } }));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadReport,
                new JsonObject { ["reportId"] = info.ReportId, ["sessionId"] = parent,
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(sibling) } }));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Read(parent, Guid.NewGuid().ToString("N")));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Read(parent, info.ReportId, "bad"));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Read(parent, info.ReportId, null, 2));
            var originalFirstText = (string?)first["text"];
            agent.During = (_, _, _) => Task.CompletedTask;
            await Prompt(client, child).WaitAsync(Wait);
            var later = service.ReportStorage.List(child).Items.Single(x => x.ReportId != info.ReportId);
            Assert.AreEqual(originalFirstText, (string?)(await Read(parent, info.ReportId))!["text"]);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Read(parent, later.ReportId, (string?)first["nextCursor"]));
            var directory = (string)service.Status()["dataDirectory"]!;
            await using (var recovery = new Conductor(new ConductorOptions { DataDirectory = directory, ServiceId = "retrieval-restart" }, [agent]))
            await using (var recoveryClient = await TestClient.ConnectAsync(recovery))
            {
                var afterRestart = await recoveryClient.Rpc.SendRequestAsync(GorynychProtocol.MethodReadReport, new JsonObject
                { ["reportId"] = info.ReportId, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(parent) } });
                Assert.AreEqual(originalFirstText, (string?)afterRestart!["text"]);
            }
            var emptyId = service.ReportStorage.Reserve(new ReportIdentity("empty-manual", child, parent, ReportCause.User)).ReportId;
            using (var capture = service.ReportStorage.OpenCapture(emptyId))
            { capture.Append(""); capture.Seal("ended"); service.ReportStorage.Commit(emptyId, capture); }
            var absentId = service.ReportStorage.Reserve(new ReportIdentity("absent-manual", child, parent, ReportCause.User)).ReportId;
            using (var capture = service.ReportStorage.OpenCapture(absentId))
            { capture.SealIncomplete("final text unavailable", "failed", "native error"); service.ReportStorage.Commit(absentId, capture); }
            var empty = await Read(parent, emptyId); var absent = await Read(parent, absentId);
            Assert.AreEqual("", (string?)empty!["text"]); Assert.AreEqual(true, (bool?)empty["hasText"]);
            Assert.AreEqual(false, (bool?)absent!["hasText"]); Assert.AreEqual("failed", (string?)absent["outcome"]);
            Assert.AreEqual("native error", (string?)absent["error"]);
            Assert.AreEqual("final text unavailable", (string?)absent["incompleteReason"]);
            var firstList = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListReports, new JsonObject
                { ["limit"] = 1, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(parent) } });
            var listCursor = (string?)firstList!["nextCursor"]; Assert.IsNotNull(listCursor);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListReports,
                new JsonObject { ["cursor"] = listCursor, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(child) } }));
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodListReports,
                new JsonObject { ["limit"] = 101, ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(parent) } }));
            service.Store.Update(child, "turns", r => r.ParentSessionId = sibling);
            Assert.IsNotNull(await Read(parent, info.ReportId));
            service.Store.Update(parent, "turns", r => r.Deleted = true);
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await Read(parent, info.ReportId));
        }
    }
}
