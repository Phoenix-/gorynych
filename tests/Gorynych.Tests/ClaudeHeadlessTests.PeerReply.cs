using System.Collections.Concurrent;
using Gorynych.Adapters.Claude;
using Gorynych.Acp.Hosting;
using Gorynych.Service;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;
using System.Diagnostics;

namespace Gorynych.Tests;

public sealed partial class ClaudeHeadlessTests
{
    static int SafePeerInputCount(string path)
    {
        try { return File.ReadAllLines(path).Length; }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task PeerOptInRealServiceAndLauncherUseOnlyFakeClaude(bool enabled, bool explicitReply)
    {
        var data = TestDirs.New(); var cwd = TestDirs.New(); var bin = TestDirs.New(); DataFolder.Claim(data);
        var executable = Environment.GetEnvironmentVariable("GORYNYCH_TEST_SERVICE") ?? throw new AssertInconclusiveException("GORYNYCH_TEST_SERVICE is required");
        var fake = FakeClaude();
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(fake)!)) File.Copy(path, Path.Combine(bin, Path.GetFileName(path)), true);
        var stub = Path.Combine(bin, TestDirs.Exe("claude")); File.Copy(fake, stub, true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CollectionAssert.AreEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fake)), System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(stub)));
        var extensions = OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries) : [""];
        var selected = extensions.Select(ext => Path.Combine(bin, "claude" + ext)).FirstOrDefault(File.Exists);
        Assert.IsTrue(string.Equals(Path.GetFullPath(stub), Path.GetFullPath(selected!), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "the child PATH selects this exact stub first");
        var pipe = "peer-exec-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--data", data, "--pipe", pipe, "--hosts", "on", "--no-channel" }) start.ArgumentList.Add(arg);
        if (enabled) start.ArgumentList.Add("--peer-turn-replies");
        start.Environment["PATH"] = bin + Path.PathSeparator + start.Environment["PATH"];
        using var process = Process.Start(start)!;
        await using var stream = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await stream.ConnectAsync(15000);
        await using var rpc = new JsonRpcConnection(stream, stream, "peer-exec");
        rpc.RequestHandler = (request, _) =>
        {
            if (request.Method != AcpMethods.Client.SessionRequestPermission || (string?)request.Params?["toolCall"]?["toolCallId"] != "trust-folder" ||
                (string?)request.Params?["toolCall"]?["locations"]?[0]?["path"] != cwd)
                throw new JsonRpcException(JsonRpcException.InvalidRequest, "Unexpected fixture permission");
            return Task.FromResult<JsonNode?>(new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = "allow" } });
        };
        rpc.Start();
        Process? peer = null; JsonRpcConnection? mcp = null;
        try
        {
            await rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
            async Task<string> New() => (string)(await rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                new JsonObject { ["cwd"] = cwd, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
            var sender = await New(); var receiver = await New();
            string? token = null;
            await LinkHarness.Until(() => { token = new SessionStore(Path.Combine(data, "sessions.json")).Get(sender)?.PeerToken; return token is not null; },
                "sender credentials persisted").WaitAsync(TimeSpan.FromSeconds(20));
            var peerStart = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "peers", "--pipe", pipe, "--token", token! }) peerStart.ArgumentList.Add(arg);
            peer = Process.Start(peerStart)!; mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "exec-peer"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            var sent = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "send_message", ["arguments"] = new JsonObject { ["session_id"] = receiver, ["text"] = explicitReply ? "peer-real-reply-probe" : "ordinary peer message" } });
            var id = (string)JsonNode.Parse((string)sent!["content"]![0]!["text"]!)!["messageId"]!;
            JsonObject? source = null;
            await LinkHarness.Until(() => { try { source = new MailboxStore(Path.Combine(data, "mailbox.json")).Find(id); }
                catch (IOException) { return false; }
                return enabled ? (string?)source?["peerTurn"]?["state"] == (explicitReply ? "explicit_reply_accepted" : "automatic_reply_accepted") && source?["peerTurn"]?["retentionAck"] is not null : (bool?)source?["delivered"] == true; },
                "production launcher peer result").WaitAsync(TimeSpan.FromSeconds(40));
            Assert.AreEqual(enabled, source?["peerTurn"] is not null);
            if (enabled)
            {
                var reports = new ReportStore(Path.Combine(data, "reports"));
                ReportInfo? report = null;
                await LinkHarness.Until(() => { try { report = reports.GetReport((string)source!["peerTurn"]!["retentionAck"]!); return report.PeerMailboxAck; } catch (IOException) { return false; } }, "report ACK").WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsNotNull(report);
                Assert.IsTrue(report.Identity.HostProcessId is > 0, "report names its producer PID");
                Assert.AreNotEqual(process.Id, report.Identity.HostProcessId, "a separate AdapterHost produced the report");
                Assert.IsTrue(report.PeerMailboxAck); Assert.IsTrue(reports.Read(report.ReportId, sender).HasText);
            }
            await LinkHarness.Until(() => File.Exists(Path.Combine(FakeRoot, receiver + ".inputs.jsonl")), "fake receiver input").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, receiver + ".inputs.jsonl")).Length);
            if (enabled)
            {
                var senderInputs = Path.Combine(FakeRoot, sender + ".inputs.jsonl");
                await LinkHarness.Until(() => SafePeerInputCount(senderInputs) == 1, "exactly one fake sender input").WaitAsync(TimeSpan.FromSeconds(20));
                await LinkHarness.Until(() => { try { var snapshot = new MailboxStore(Path.Combine(data, "mailbox.json"));
                    return snapshot.Read(sender, 0, 100, false).Count(e => (string?)e["replyTo"] == id) == 1; }
                    catch (IOException) { return false; } }, "exactly one durable reply").WaitAsync(TimeSpan.FromSeconds(15));
            }
            else Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, sender + ".inputs.jsonl")));
        }
        finally
        {
            if (mcp is not null) await mcp.DisposeAsync();
            if (peer is not null) { await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); }
            await rpc.SendRequestAsync(GorynychProtocol.MethodShutdown, new JsonObject());
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        Assert.AreEqual(0, process.ExitCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PeerOptInServiceExecutablePersistsLaunchOption(bool enabled)
    {
        var data = TestDirs.New(); DataFolder.Claim(data);
        var executable = Environment.GetEnvironmentVariable("GORYNYCH_TEST_SERVICE") ?? throw new AssertInconclusiveException("GORYNYCH_TEST_SERVICE is required");
        var pipe = "peer-launch-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--data", data, "--pipe", pipe, "--hosts", "on", "--no-channel" }) start.ArgumentList.Add(arg);
        if (enabled) start.ArgumentList.Add("--peer-turn-replies");
        using var process = Process.Start(start)!;
        await using var stream = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await stream.ConnectAsync(15000);
        await using var rpc = new JsonRpcConnection(stream, stream, "peer-launch"); rpc.Start();
        try
        {
            await rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
            Assert.AreEqual(enabled, ServiceOptions.Read(data).Contains("--peer-turn-replies"));
        }
        finally { await rpc.SendRequestAsync(GorynychProtocol.MethodShutdown, new JsonObject()); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        Assert.AreEqual(0, process.ExitCode);
    }

    [TestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(true, true, false, false)]
    [DataRow(true, false, true, false)]
    [DataRow(true, false, false, true)]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task PeerOptInDisabledOrUnsupportedKeepsLegacyDelivery(bool enabled, bool unsupported, bool shadow, bool noPeers)
    {
        var dir = TestDirs.New(); var data = TestDirs.New();
        if (unsupported) File.WriteAllText(Path.Combine(dir, "disable-peer-cap"), "");
        var serviceExe = Environment.GetEnvironmentVariable("GORYNYCH_TEST_SERVICE") ?? throw new AssertInconclusiveException("GORYNYCH_TEST_SERVICE is required");
        var pipe = "peer-legacy-" + Guid.NewGuid().ToString("N");
        await using var launcher = new CompletionProcessHost(dir, data, FakeClaude(), peer: true);
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "peer-legacy", Hosts = launcher,
            HostToken = InProcessHosts.Token, PeerTurnReplies = enabled, PeerServerCommand = noPeers ? null : [serviceExe, "peers", "--pipe", pipe, "--peer-turn-replies"] }, [adapter]);
        launcher.Service = service; using var stop = new CancellationTokenSource(); var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        await using var client = await TestClient.ConnectAsync(service); Process? peer = null; JsonRpcConnection? mcp = null;
        try
        {
            var sender = await NewAsync(client, dir);
            var receiver = shadow ? (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject {
                ["cwd"] = dir, ["mcpServers"] = new JsonArray(new JsonObject { ["name"] = "gorynych", ["command"] = "customer-owned-command", ["args"] = new JsonArray(), ["env"] = new JsonArray() }) }))!["sessionId"]!
                : await NewAsync(client, dir);
            service.Store.Update(sender, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            service.Store.Update(receiver, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            var start = new ProcessStartInfo(serviceExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "peers", "--pipe", pipe, "--token", service.Store.Get(sender)!.PeerToken! }) start.ArgumentList.Add(arg);
            peer = Process.Start(start)!; mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "legacy-peer"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            var sent = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "send_message", ["arguments"] = new JsonObject { ["session_id"] = receiver, ["text"] = "legacy message" } });
            var id = (string)JsonNode.Parse((string)sent!["content"]![0]!["text"]!)!["messageId"]!;
            await LinkHarness.Until(() => (bool?)service.ReplyMailbox.Find(id)?["delivered"] == true && service.Feed(receiver).TurnGate.CurrentCount == 1,
                "legacy delivery completed").WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsNull(service.ReplyMailbox.Find(id)?["peerTurn"]);
            if (shadow)
            {
                var args = LaunchArgs(receiver); var configPath = args[args.IndexOf("--mcp-config") + 1];
                var config = JsonNode.Parse(File.ReadAllText(configPath));
                Assert.AreEqual("customer-owned-command", (string?)config?["mcpServers"]?["gorynych"]?["command"]);
            }
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, receiver + ".inputs.jsonl")).Length);
            Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, sender + ".inputs.jsonl")));
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
    public void PreAcceptRefusalRequiresExactVersionAndIdentity()
    {
        var id = Guid.NewGuid().ToString("N");
        var proof = new JsonObject { ["kind"] = "gorynych.reply.pre_accept_refusal", ["version"] = 1,
            ["messageId"] = id, ["reason"] = "text_required", ["error"] = "message_id and text are required" };
        StreamJson.ToolResultDetail Detail(JsonNode? structured, string? text) => new("call", "tool", true, structured, text);
        Assert.IsTrue(HeadlessSession.IsExactPreAcceptRefusal(Detail(proof, null), id));
        Assert.IsTrue(HeadlessSession.IsExactPreAcceptRefusal(Detail(null, proof.ToJsonString()), id));
        Assert.IsFalse(HeadlessSession.IsExactPreAcceptRefusal(Detail(null, "message_id and text are required"), id));
        Assert.IsFalse(HeadlessSession.IsExactPreAcceptRefusal(Detail(null, proof.ToJsonString()), Guid.NewGuid().ToString("N")));
        proof["version"] = 2;
        Assert.IsFalse(HeadlessSession.IsExactPreAcceptRefusal(Detail(proof, null), id));
        Assert.IsFalse(HeadlessSession.IsExactPreAcceptRefusal(Detail(null, "{broken"), id));
    }

    [TestMethod]
    [DataRow("failed")]
    [DataRow("cancelled")]
    [DataRow("stopped: max_tokens")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PeerNonSuccessTerminalSettlesWithoutMarkerOrWake(string outcome)
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "peer-nonsuccess",
            TestPeerTurnRouting = true }, [adapter]);
        var row = service.ReplyMailbox.Accept("request", "sender", "worker", "question");
        var sequence = (long)row["sequence"]!;
        var route = new PeerTurnIdentity("request", sequence, "sender", "worker", $"service-peer-{sequence}-request");
        Assert.IsTrue(service.ReplyMailbox.TryMarkStarted("request", "worker", sequence, route));
        service.ReplyMailbox.MarkDelivered("request", "worker");
        var identity = new ReportIdentity($"peer:{sequence}:request", "worker", null, ReportCause.Peer,
            PromptId: route.PromptId, MessageId: route.MessageId, HostId: "host", HostGeneration: "generation", PeerRoute: route);
        var id = service.ReportStorage.Reserve(identity).ReportId;
        using (var capture = service.ReportStorage.OpenCapture(id)) capture.Seal(outcome);
        service.ReportStorage.RecordProducerEvidence(id, "host", outcome, null, null);
        using (var capture = service.ReportStorage.OpenCapture(id)) service.ReportStorage.Commit(id, capture);
        service.ReportStorage.AcknowledgeProducer(id); service.QueueReportDeliveries();
        await LinkHarness.Until(() => service.ReportStorage.GetReport(id).PeerMailboxAck, "failed or cancelled peer retained and acknowledged").WaitAsync(TimeSpan.FromSeconds(10));
        var source = service.ReplyMailbox.Read("worker", 0, 20, false).Single(e => (string?)e["id"] == "request");
        Assert.AreEqual(outcome.StartsWith("stopped:", StringComparison.Ordinal) ? "stopped" : outcome, (string?)source["peerTurn"]?["state"]);
        Assert.AreEqual(outcome, (string?)source["peerTurn"]?["outcome"]);
        Assert.IsNull(source["peerTurn"]?["settlementPin"]); Assert.AreEqual(id, (string?)source["peerTurn"]?["retentionAck"]);
        Assert.AreEqual(0, service.ReplyMailbox.Read("sender", 0, 20, false).Count);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PeerUnconfirmedHandoffStaysOpenButLostAcceptedProofClosesUnknown(bool provenTerminal)
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "peer-terminal-proof",
            TestPeerTurnRouting = true }, [adapter]);
        var row = service.ReplyMailbox.Accept("request", "sender", "worker", "question"); var sequence = (long)row["sequence"]!;
        var route = new PeerTurnIdentity("request", sequence, "sender", "worker", $"service-peer-{sequence}-request");
        Assert.IsTrue(service.ReplyMailbox.TryMarkStarted("request", "worker", sequence, route)); service.ReplyMailbox.MarkDelivered("request", "worker");
        var identity = new ReportIdentity($"peer:{sequence}:request", "worker", null, ReportCause.Peer,
            PromptId: route.PromptId, MessageId: route.MessageId, HostId: "host", HostGeneration: "generation", PeerRoute: route);
        var id = service.ReportStorage.Reserve(identity).ReportId;
        using (var capture = service.ReportStorage.OpenCapture(id)) { capture.Append("available text"); capture.Seal("ended"); }
        if (provenTerminal)
            service.ReportStorage.RecordProducerEvidence(id, "host", "ended", null, null,
                new PeerProducerEvidence(route.PromptId, "01234567-89ab-cdef-0123-456789abcdef", "stream", "0123456789abcdef0123456789abcdef"));
        using (var capture = service.ReportStorage.OpenCapture(id))
        {
            if (provenTerminal) service.ReportStorage.Commit(id, capture);
            else service.ReportStorage.CommitUnconfirmed(id, capture);
        }
        service.ReportStorage.AcknowledgeProducer(id); service.QueueReportDeliveries();
        await LinkHarness.Until(() => !service.ReportDeliveryRunning(id), "peer report evaluation finished").WaitAsync(TimeSpan.FromSeconds(10));
        var source = service.ReplyMailbox.Read("worker", 0, 20, false).Single(e => (string?)e["id"] == "request");
        var report = service.ReportStorage.GetReport(id);
        if (provenTerminal)
        {
            Assert.AreEqual("outcome_unknown", (string?)source["peerTurn"]?["state"]);
            Assert.AreEqual("accepted_unconfirmed", (string?)source["peerTurn"]?["unknownReason"]);
            Assert.AreEqual("retained", report.Status); Assert.IsTrue(report.PeerMailboxAck);
        }
        else
        {
            Assert.AreEqual("started", (string?)source["peerTurn"]?["state"]);
            Assert.AreEqual("pending", report.Status); Assert.IsFalse(report.PeerMailboxAck);
        }
        Assert.AreEqual(0, service.ReplyMailbox.Read("sender", 0, 20, false).Count);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("malformed")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task UnknownPeerTerminalClosesWithoutWakeAcrossSeparateHostReopen(string marker)
    {
        var dir = TestDirs.New(); var data = TestDirs.New();
        if (marker == "malformed") File.WriteAllText(Path.Combine(dir, "malformed-peer-wire"), "");
        var serviceExe = Environment.GetEnvironmentVariable("GORYNYCH_TEST_SERVICE") ?? throw new AssertInconclusiveException("GORYNYCH_TEST_SERVICE is required");
        var pipe = "peer-unknown-" + Guid.NewGuid().ToString("N");
        await using var launcher = new CompletionProcessHost(dir, data, FakeClaude(), peer: true);
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        Conductor NewService() => new(new ConductorOptions { DataDirectory = data, ServiceId = "peer-unknown",
            Hosts = launcher, HostToken = InProcessHosts.Token, TestPeerTurnRouting = true,
            PeerServerCommand = [serviceExe, "peers", "--pipe", pipe, "--peer-turn-replies"] }, [adapter]);
        var service = NewService(); launcher.Service = service;
        using var stop = new CancellationTokenSource(); var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        var client = await TestClient.ConnectAsync(service); Process? peer = null; JsonRpcConnection? mcp = null; var nativeClosed = false;
        try
        {
            var sender = await NewAsync(client, dir); var parent = await NewAsync(client, dir); var receiver = await NewAsync(client, dir);
            service.Store.Update(sender, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            service.Store.Update(parent, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            service.Store.Update(receiver, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = true; }, durable: true);
            var start = new ProcessStartInfo(serviceExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "peers", "--pipe", pipe, "--token", service.Store.Get(sender)!.PeerToken! }) start.ArgumentList.Add(arg);
            peer = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture peers");
            mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "unknown-sender-peer"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            var sent = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "send_message", ["arguments"] = new JsonObject {
                ["session_id"] = receiver, ["text"] = marker == "missing" ? "peer-native-malformed-probe" : "ordinary peer message" } }).WaitAsync(TimeSpan.FromSeconds(30));
            var sourceId = (string)JsonNode.Parse((string)sent!["content"]![0]!["text"]!)!["messageId"]!;
            ReportInfo? saved = null;
            await LinkHarness.Until(() => { saved = service.ReportStorage.Inventory(null, 100).Items.SingleOrDefault(r => r.Identity.PeerRoute?.MessageId == sourceId);
                return saved?.PeerMailboxAck == true; }, "unknown terminal retained and acknowledged").WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsNotNull(saved); Assert.AreEqual("ended", saved.Outcome); Assert.AreEqual("sealed", saved.ProducerEvidence); Assert.IsNull(saved.PeerEvidence);
            Assert.IsTrue(service.ReportStorage.Read(saved.ReportId, sender).HasText, "available full report remains readable");
            var source = service.ReplyMailbox.Read(receiver, 0, 50, false).Single(e => (string?)e["id"] == sourceId);
            Assert.AreEqual("outcome_unknown", (string?)source["peerTurn"]?["state"]); Assert.AreEqual("marker_unknown", (string?)source["peerTurn"]?["unknownReason"]);
            Assert.IsNull(source["peerTurn"]?["settlementPin"]); Assert.AreEqual(saved.ReportId, (string?)source["peerTurn"]?["retentionAck"]);
            Assert.AreEqual(0, service.ReplyMailbox.Read(sender, 0, 50, false).Count(e => (string?)e["replyTo"] == sourceId));
            Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, sender + ".inputs.jsonl")));
            Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, parent + ".inputs.jsonl")));
            var original = service.Agents["claude"].RouteOf(receiver)!; Assert.AreEqual(launcher.Pid, original.ProcessId);
            await mcp.DisposeAsync(); mcp = null; await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); peer = null;
            stop.Cancel(); try { await listener; } catch (OperationCanceledException) { }
            launcher.Service = null; launcher.BreakConnection();
            await LinkHarness.Until(() => original.Rpc.IsClosed, "original route disconnected").WaitAsync(TimeSpan.FromSeconds(15));
            await client.DisposeAsync(); await service.DisposeAsync();
            await using var reopened = NewService(); launcher.Service = reopened;
            using var reopenedStop = new CancellationTokenSource(); var reopenedListener = PipeListener.RunAsync(reopened, pipe, reopenedStop.Token);
            try
            {
                await LinkHarness.Until(() => reopened.Agents["claude"].Links.Any(l => l.HostId == original.HostId && !l.Rpc.IsClosed),
                    "same host reattached").WaitAsync(TimeSpan.FromSeconds(20));
                Assert.IsTrue(reopened.ReportStorage.GetReport(saved.ReportId).PeerMailboxAck);
                Assert.AreEqual(0, reopened.ReplyMailbox.PeerSettlementPins().Count);
                Assert.AreEqual(0, reopened.ReplyMailbox.Read(sender, 0, 50, false).Count(e => (string?)e["replyTo"] == sourceId));
                Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, receiver + ".inputs.jsonl")).Length, "no native resend");
                Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, sender + ".inputs.jsonl"))); Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, parent + ".inputs.jsonl")));
            }
            finally { await launcher.CloseNativeGracefully(); nativeClosed = true; reopenedStop.Cancel(); try { await reopenedListener; } catch (OperationCanceledException) { } }
        }
        finally
        {
            if (mcp is not null) await mcp.DisposeAsync();
            if (peer is not null) { await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); }
            if (!stop.IsCancellationRequested) { stop.Cancel(); try { await listener; } catch (OperationCanceledException) { } }
            if (!nativeClosed) await launcher.CloseNativeGracefully();
            await client.DisposeAsync(); await service.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow("capture-failed")]
    [DataRow("exit-unconfirmed")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task PeerCaptureFailureAndExitRecoverAcrossSeparateHostReopen(string marker)
    {
        var dir = TestDirs.New(); var data = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, marker == "capture-failed" ? "fail-peer-capture" : "fail-peer-evidence"), "");
        var serviceExe = Environment.GetEnvironmentVariable("GORYNYCH_TEST_SERVICE") ?? throw new AssertInconclusiveException("GORYNYCH_TEST_SERVICE is required");
        var pipe = "peer-unknown-" + Guid.NewGuid().ToString("N");
        await using var launcher = new CompletionProcessHost(dir, data, FakeClaude(), peer: true);
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        Conductor NewService(bool enabled = true) => new(new ConductorOptions { DataDirectory = data, ServiceId = "peer-unknown",
            Hosts = launcher, HostToken = InProcessHosts.Token, PeerTurnReplies = enabled,
            PeerServerCommand = [serviceExe, "peers", "--pipe", pipe, "--peer-turn-replies"] }, [adapter]);
        var service = NewService(); launcher.Service = service;
        using var stop = new CancellationTokenSource(); var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        var client = await TestClient.ConnectAsync(service); Process? peer = null; JsonRpcConnection? mcp = null; var nativeClosed = false;
        try
        {
            var sender = await NewAsync(client, dir); var parent = await NewAsync(client, dir); var receiver = await NewAsync(client, dir);
            service.Store.Update(sender, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            service.Store.Update(parent, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            service.Store.Update(receiver, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = true; }, durable: true);
            var start = new ProcessStartInfo(serviceExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "peers", "--pipe", pipe, "--token", service.Store.Get(sender)!.PeerToken! }) start.ArgumentList.Add(arg);
            peer = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture peers");
            mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "unknown-sender-peer"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            var sent = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "send_message", ["arguments"] = new JsonObject {
                ["session_id"] = receiver, ["text"] = "ordinary peer message" } }).WaitAsync(TimeSpan.FromSeconds(30));
            var sourceId = (string)JsonNode.Parse((string)sent!["content"]![0]!["text"]!)!["messageId"]!;
            ReportInfo? saved = null;
            if (marker == "exit-unconfirmed")
            {
                await LinkHarness.Until(() => File.Exists(Path.Combine(dir, "peer-evidence-failed")) &&
                    service.ReportStorage.Inventory(null, 100).Items.Any(r => r.Identity.PeerRoute?.MessageId == sourceId && r.Status == "reserved"),
                    "producer evidence fault reached").WaitAsync(TimeSpan.FromSeconds(30));
                await service.ReconcileReportInventoryAsync();
                saved = service.ReportStorage.Inventory(null, 100).Items.Single(r => r.Identity.PeerRoute?.MessageId == sourceId);
                Assert.AreEqual("reserved", saved.Status, "a live producer is not exit proof");
                Assert.AreEqual("started", (string?)service.ReplyMailbox.Read(receiver, 0, 50, false).Single(e => (string?)e["id"] == sourceId)["peerTurn"]?["state"]);
            }
            else
            {
                await LinkHarness.Until(() => { saved = service.ReportStorage.Inventory(null, 100).Items.SingleOrDefault(r => r.Identity.PeerRoute?.MessageId == sourceId);
                    return saved?.PeerMailboxAck == true; }, "failed capture retained").WaitAsync(TimeSpan.FromSeconds(30));
                Assert.AreEqual("failed", saved!.ProducerEvidence);
            }
            var original = service.Agents["claude"].RouteOf(receiver)!; Assert.AreEqual(launcher.Pid, original.ProcessId);
            await mcp.DisposeAsync(); mcp = null; await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); peer = null;
            stop.Cancel(); try { await listener; } catch (OperationCanceledException) { }
            if (marker == "exit-unconfirmed") { await launcher.CloseNativeGracefully(); nativeClosed = true; }
            launcher.Service = null; launcher.BreakConnection();
            await LinkHarness.Until(() => original.Rpc.IsClosed, "original route disconnected").WaitAsync(TimeSpan.FromSeconds(15));
            await client.DisposeAsync(); await service.DisposeAsync();
            await using var reopened = NewService(enabled: false); launcher.Service = reopened;
            using var reopenedStop = new CancellationTokenSource(); var reopenedListener = PipeListener.RunAsync(reopened, pipe, reopenedStop.Token);
            try
            {
                if (marker == "exit-unconfirmed")
                {
                    await reopened.ReconcileReportInventoryAsync();
                    await LinkHarness.Until(() => reopened.ReportStorage.GetReport(saved!.ReportId).PeerMailboxAck, "exit recovery retained").WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.IsTrue(reopened.ReportStorage.GetReport(saved!.ReportId).ExactProducerExit);
                }
                else
                    await LinkHarness.Until(() => reopened.Agents["claude"].Links.Any(l => l.HostId == original.HostId && !l.Rpc.IsClosed),
                        "same host reattached").WaitAsync(TimeSpan.FromSeconds(20));
                var source = reopened.ReplyMailbox.Read(receiver, 0, 50, false).Single(e => (string?)e["id"] == sourceId);
                Assert.AreEqual("outcome_unknown", (string?)source["peerTurn"]?["state"]);
                Assert.AreEqual(marker == "exit-unconfirmed" ? "producer_exited" : "producer_capture_failed", (string?)source["peerTurn"]?["unknownReason"]);
                Assert.IsTrue(reopened.ReportStorage.GetReport(saved!.ReportId).PeerMailboxAck);
                Assert.AreEqual(0, reopened.ReplyMailbox.PeerSettlementPins().Count);
                Assert.AreEqual(0, reopened.ReplyMailbox.Read(sender, 0, 50, false).Count(e => (string?)e["replyTo"] == sourceId));
                Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, receiver + ".inputs.jsonl")).Length, "no native resend");
                Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, sender + ".inputs.jsonl"))); Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, parent + ".inputs.jsonl")));
            }
            finally { if (!nativeClosed) { await launcher.CloseNativeGracefully(); nativeClosed = true; } reopenedStop.Cancel(); try { await reopenedListener; } catch (OperationCanceledException) { } }
        }
        finally
        {
            if (mcp is not null) await mcp.DisposeAsync();
            if (peer is not null) { await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); }
            if (!stop.IsCancellationRequested) { stop.Cancel(); try { await listener; } catch (OperationCanceledException) { } }
            if (!nativeClosed) await launcher.CloseNativeGracefully();
            await client.DisposeAsync(); await service.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow("before-retained")]
    [DataRow("before-ack")]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PeerRetentionRecoversAfterSeparateHostRestart(string phase) => RunPeerRetentionRestart(phase, false);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task PeerRetentionSetupFailurePreservesOriginalAndStopsOwnedHost()
    {
        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => RunPeerRetentionRestart("before-ack", true));
        StringAssert.Contains(failure.Message, "fixture setup sentinel");
        Assert.IsFalse(failure.Data.Contains("peer fixture cleanup failure"), "cleanup succeeded without masking the setup failure");
    }

    async Task RunPeerRetentionRestart(string phase, bool failSetup)
    {
        var dir = TestDirs.New(); var data = TestDirs.New();
        var serviceExe = Environment.GetEnvironmentVariable("GORYNYCH_TEST_SERVICE") ?? throw new AssertInconclusiveException("GORYNYCH_TEST_SERVICE is required");
        var pipe = "peer-restart-" + Guid.NewGuid().ToString("N");
        var launcher = new CompletionProcessHost(dir, data, FakeClaude(), peer: true);
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        Conductor NewService() => new(new ConductorOptions { DataDirectory = data, ServiceId = "peer-restart",
            Hosts = launcher, HostToken = InProcessHosts.Token, TestPeerTurnRouting = true,
            PeerServerCommand = [serviceExe, "peers", "--pipe", pipe, "--peer-turn-replies"] }, [adapter]);
        var service = NewService(); launcher.Service = service;
        using var stop = new CancellationTokenSource(); var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        var client = await TestClient.ConnectAsync(service); Process? peer = null; JsonRpcConnection? mcp = null;
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Fail() { fault.TrySetResult(); throw new IOException("fixture retention interruption"); }
        if (phase == "before-retained") service.AfterPeerMailboxSettlementForTests = Fail;
        else service.BeforePeerRetentionAckForTests = Fail;
        string? sourceId = null, sender = null, receiver = null, parent = null, reportId = null; var nativeClosed = false;
        Exception? primary = null;
        try
        {
            sender = await NewAsync(client, dir); parent = await NewAsync(client, dir); receiver = await NewAsync(client, dir);
            service.Store.Update(sender, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            service.Store.Update(parent, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            service.Store.Update(receiver, "claude", r => { r.Draft = false; r.ParentSessionId = parent; r.ReportToParent = true; }, durable: true);
            var start = new ProcessStartInfo(serviceExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "peers", "--pipe", pipe, "--token", service.Store.Get(sender)!.PeerToken! }) start.ArgumentList.Add(arg);
            peer = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture peers");
            mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "restart-sender-peer"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            var sent = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "send_message", ["arguments"] = new JsonObject {
                ["session_id"] = receiver, ["text"] = "ordinary peer message" } }).WaitAsync(TimeSpan.FromSeconds(30));
            sourceId = (string)JsonNode.Parse((string)sent!["content"]![0]!["text"]!)!["messageId"]!;
            await fault.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (failSetup) Assert.Fail("fixture setup sentinel");
            var saved = service.ReportStorage.Inventory(null, 100).Items.Single(r => r.Identity.PeerRoute?.MessageId == sourceId);
            reportId = saved.ReportId; Assert.AreEqual(phase == "before-retained" ? "pending" : "retained", saved.Status);
            Assert.IsFalse(saved.PeerMailboxAck);
            var senderInput = Path.Combine(FakeRoot, sender + ".inputs.jsonl");
            await LinkHarness.Until(() => SafePeerInputCount(senderInput) == 1 &&
                service.Feed(sender).TurnGate.CurrentCount == 1, "original sender input before restart").WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(1, service.ReplyMailbox.PeerSettlementPins().Count);
            await mcp.DisposeAsync(); mcp = null; await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); peer = null;
            stop.Cancel(); try { await listener; } catch (OperationCanceledException) { }
            var originalRoute = service.Agents["claude"].RouteOf(receiver)!;
            launcher.Service = null; launcher.BreakConnection();
            await LinkHarness.Until(() => originalRoute.Rpc.IsClosed, "old route disconnected").WaitAsync(TimeSpan.FromSeconds(15));
            await client.DisposeAsync(); await service.DisposeAsync();
            await using var reopened = NewService(); launcher.Service = reopened;
            using var reopenedStop = new CancellationTokenSource(); var reopenedListener = PipeListener.RunAsync(reopened, pipe, reopenedStop.Token);
            Exception? reopenedFailure = null;
            try
            {
                await LinkHarness.Until(() => reopened.ReportStorage.GetReport(reportId).PeerMailboxAck &&
                    reopened.ReplyMailbox.PeerSettlementPins().Count == 0, "retained mailbox ACK after reopen").WaitAsync(TimeSpan.FromSeconds(30));
                Assert.AreEqual(1, reopened.ReplyMailbox.Read(sender, 0, 50, false).Count(e => (string?)e["replyTo"] == sourceId));
                Assert.AreEqual(1, File.ReadAllLines(senderInput).Length, "recovery does not resend sender input");
                Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, receiver + ".inputs.jsonl")).Length, "recovery does not repeat native work");
                Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, parent + ".inputs.jsonl")));
            }
            catch (Exception e) { reopenedFailure = e; throw; }
            finally
            {
                try { await launcher.CloseNativeGracefully(); nativeClosed = true; }
                catch (Exception e) when (reopenedFailure is not null) { reopenedFailure.Data["peer fixture cleanup failure"] = e; }
                reopenedStop.Cancel(); try { await reopenedListener; } catch (OperationCanceledException) { }
            }
        }
        catch (Exception e) { primary = e; throw; }
        finally
        {
            var failures = new List<Exception>();
            async Task Cleanup(Func<Task> action) { try { await action(); } catch (Exception e) { failures.Add(e); } }
            if (mcp is not null) await Cleanup(async () => await mcp.DisposeAsync());
            if (peer is not null) await Cleanup(async () => { await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); peer.Dispose(); });
            if (!stop.IsCancellationRequested) { stop.Cancel(); await Cleanup(async () => { try { await listener; } catch (OperationCanceledException) { } }); }
            if (!nativeClosed) await Cleanup(async () => await launcher.CloseNativeGracefully());
            await Cleanup(async () => await client.DisposeAsync());
            await Cleanup(async () => await service.DisposeAsync());
            await Cleanup(async () => await launcher.DisposeAsync());
            if (failures.Count > 0)
            {
                var cleanup = new AggregateException("Peer fixture cleanup failed", failures);
                if (primary is not null) primary.Data["peer fixture cleanup failure"] = cleanup;
                else throw cleanup;
            }
        }
    }

    [TestMethod]
    [DataRow(false, true, true, false, false, false)]
    [DataRow(true, true, true, false, false, false)]
    [DataRow(false, true, false, false, false, false)]
    [DataRow(false, false, false, false, false, false)]
    [DataRow(false, true, true, true, false, false)]
    [DataRow(false, true, true, false, true, false)]
    [DataRow(false, true, true, false, false, true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ManagedSendRoutesPeerResultThroughSeparateHost(bool explicitReply, bool hasParent, bool reportToParent, bool failRetainedOnce, bool refusal, bool deferAdmission)
    {
        var admissions = 0; var admissionOpen = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dir = TestDirs.New(); var data = TestDirs.New();
        var serviceExe = Environment.GetEnvironmentVariable("GORYNYCH_TEST_SERVICE") ?? throw new AssertInconclusiveException("GORYNYCH_TEST_SERVICE is required");
        var pipe = "peer-route-" + Guid.NewGuid().ToString("N");
        await using var launcher = new CompletionProcessHost(dir, data, FakeClaude(), peer: true);
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "peer-route",
            Hosts = launcher, HostToken = InProcessHosts.Token, PeerTurnReplies = true,
            DeferPeerAdmissionForTests = deferAdmission ? () => { Interlocked.Increment(ref admissions); entered.TrySetResult(); return Volatile.Read(ref admissionOpen) == 0; } : null,
            PeerServerCommand = [serviceExe, "peers", "--pipe", pipe, "--peer-turn-replies"] }, [adapter]);
        launcher.Service = service; using var stop = new CancellationTokenSource();
        var injected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failRetained = failRetainedOnce ? 1 : 0; var writeManifest = service.ReportStorage.WriteManifest;
        service.ReportStorage.WriteManifest = (path, json) =>
        {
            if (json.Contains("\"status\":\"retained\"", StringComparison.Ordinal) && Interlocked.Exchange(ref failRetained, 0) == 1)
            { injected.TrySetResult(); throw new IOException("fixture retained publication failure"); }
            writeManifest(path, json);
        };
        var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        await using var client = await TestClient.ConnectAsync(service);
        Process? peer = null; JsonRpcConnection? mcp = null;
        try
        {
            var sender = await NewAsync(client, dir); var parent = await NewAsync(client, dir); var receiver = await NewAsync(client, dir);
            service.Store.Update(sender, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            service.Store.Update(parent, "claude", r => { r.Draft = false; r.ReportToParent = false; }, durable: true);
            service.Store.Update(receiver, "claude", r => { r.Draft = false; r.ParentSessionId = hasParent ? parent : null; r.ReportToParent = reportToParent; }, durable: true);
            var start = new ProcessStartInfo(serviceExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "peers", "--pipe", pipe, "--token", service.Store.Get(sender)!.PeerToken! }) start.ArgumentList.Add(arg);
            peer = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture peers");
            mcp = new JsonRpcConnection(peer.StandardOutput.BaseStream, peer.StandardInput.BaseStream, "sender-peer-test"); mcp.Start();
            await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            var sent = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "send_message", ["arguments"] = new JsonObject {
                ["session_id"] = receiver, ["text"] = explicitReply ? "peer-real-reply-probe" : refusal ? "peer-real-refusal-probe" : "ordinary peer message" } }).WaitAsync(TimeSpan.FromSeconds(30));
            var receipt = JsonNode.Parse((string)sent!["content"]![0]!["text"]!)!;
            Assert.AreEqual("accepted", (string?)receipt["status"]); var sourceId = (string)receipt["messageId"]!;
            if (deferAdmission)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Task.Delay(250);
                Assert.AreEqual(1, Volatile.Read(ref admissions), "retry is bounded");
                Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, receiver + ".inputs.jsonl")), "head remains unsent");
                Assert.IsFalse((bool?)service.ReplyMailbox.Find(sourceId)?["delivered"] == true);
                Interlocked.Exchange(ref admissionOpen, 1);
            }
            if (failRetainedOnce)
            {
                await injected.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var inputBeforeRetry = Path.Combine(FakeRoot, sender + ".inputs.jsonl");
                await LinkHarness.Until(() => File.Exists(inputBeforeRetry) && File.ReadAllLines(inputBeforeRetry).Length == 1,
                    "durable outbound delivered despite failed report disposition").WaitAsync(TimeSpan.FromSeconds(30));
                service.QueueReportDeliveries();
            }
            ReportInfo? saved = null;
            await LinkHarness.Until(() =>
            {
                saved = service.ReportStorage.Inventory(null, 100).Items.SingleOrDefault(r => r.Identity.PeerRoute?.MessageId == sourceId);
                return saved?.PeerMailboxAck == true && service.ReplyMailbox.Read(sender, 0, 50, false)
                    .Count(e => (string?)e["replyTo"] == sourceId) == 1;
            }, "peer result settled").WaitAsync(TimeSpan.FromSeconds(40));
            Assert.IsNotNull(saved); Assert.IsNotNull(saved.PeerEvidence); Assert.AreEqual(launcher.Pid, service.Agents["claude"].RouteOf(receiver)!.ProcessId);
            var replies = service.ReplyMailbox.Read(sender, 0, 50, false).Where(e => (string?)e["replyTo"] == sourceId).ToArray();
            Assert.AreEqual(1, replies.Length); Assert.AreEqual(!explicitReply, (bool?)replies[0]["peerAutoReply"] == true);
            if (explicitReply) Assert.AreEqual((string?)replies[0]["id"], saved.PeerEvidence.AcceptedMessageId);
            else Assert.IsNull(saved.PeerEvidence.AcceptedMessageId);
            Assert.AreEqual("peer:" + saved.Identity.PeerRoute!.Sequence + ":" + sourceId, saved.Identity.TurnKey);
            Assert.AreEqual(sender, saved.Identity.PeerRoute.Sender); Assert.AreEqual(receiver, saved.Identity.PeerRoute.Recipient);
            Assert.AreEqual(hasParent && reportToParent ? parent : null, saved.Identity.Parent);
            Assert.IsTrue(service.ReportStorage.Read(saved.ReportId, sender).HasText);
            if (hasParent && reportToParent) Assert.IsTrue(service.ReportStorage.Read(saved.ReportId, parent).HasText);
            else Assert.ThrowsExactly<UnauthorizedAccessException>(() => service.ReportStorage.Read(saved.ReportId, parent));
            var senderInput = Path.Combine(FakeRoot, sender + ".inputs.jsonl");
            await LinkHarness.Until(() => SafePeerInputCount(senderInput) == 1 &&
                service.Feed(sender).TurnGate.CurrentCount == 1 && service.ReplyMailbox.ReadManagedQueue(sender, 8).Count == 0 &&
                service.ReplyMailbox.ReadManagedQueue(receiver, 8).Count == 0, "sender turn delivered").WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, parent + ".inputs.jsonl")), "distinct parent is not woken");
            Assert.AreEqual(1, File.ReadAllLines(senderInput).Length, "exactly one sender input, no loop");
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(FakeRoot, receiver + ".inputs.jsonl")).Length, "one deliberate receiver input");
            var source = service.ReplyMailbox.Read(receiver, 0, 50, false).Single(e => (string?)e["id"] == sourceId);
            Assert.IsNull(source["peerTurn"]?["settlementPin"]);
            Assert.AreEqual(saved.ReportId, (string?)source["peerTurn"]?["retentionAck"]);
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
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task SeparateHostPersistsActualManagedMcpReplyMarker(bool reply)
    {
        var dir = TestDirs.New(); var data = TestDirs.New();
        var serviceExe = Environment.GetEnvironmentVariable("GORYNYCH_TEST_SERVICE");
        if (string.IsNullOrWhiteSpace(serviceExe))
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gorynych.slnx"))) root = root.Parent;
            Assert.IsNotNull(root);
            var staged = Path.Combine(root.FullName, ".artifacts", "bin", "Gorynych.Service", "release", TestDirs.Exe("Gorynych.Service"));
            serviceExe = File.Exists(staged) ? staged : Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Gorynych.Service", "bin"),
                TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories).First();
        }
        var pipe = "peer-real-mcp-" + Guid.NewGuid().ToString("N");
        await using var launcher = new CompletionProcessHost(dir, data, FakeClaude(), peer: true);
        var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false, liveNotes: true));
        await using var service = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "peer-os-host",
            Hosts = launcher, HostToken = InProcessHosts.Token, PeerServerCommand = [serviceExe, "peers", "--pipe", pipe, "--peer-turn-replies"] }, [adapter]);
        launcher.Service = service; using var stop = new CancellationTokenSource();
        var listener = PipeListener.RunAsync(service, pipe, stop.Token);
        await using var client = await TestClient.ConnectAsync(service);
        try
        {
            var sender = await NewAsync(client, dir); var receiver = await NewAsync(client, dir);
            service.Store.Update(sender, "claude", r => r.Draft = false, durable: true);
            service.Store.Update(receiver, "claude", r => r.Draft = false, durable: true);
            await PromptAsync(client, receiver, "warm up").WaitAsync(TimeSpan.FromSeconds(30));
            var link = service.Agents["claude"].RouteOf(receiver)!;
            Assert.AreEqual(launcher.Pid, link.ProcessId); Assert.AreNotEqual(Environment.ProcessId, link.ProcessId);
            var original = service.ReplyMailbox.Accept("request", sender, receiver, "question");
            var peerRoute = new PeerTurnIdentity("request", (long)original["sequence"]!, sender, receiver,
                $"service-peer-{(long)original["sequence"]!}-request");
            Assert.IsTrue(service.ReplyMailbox.TryMarkStarted("request", receiver, peerRoute.Sequence, peerRoute));
            service.ReplyMailbox.MarkDelivered("request", receiver);
            var identity = new ReportIdentity($"peer:{peerRoute.Sequence}:request", receiver, null, ReportCause.Peer,
                PromptId: peerRoute.PromptId, MessageId: "request", HostId: link.HostId, HostGeneration: link.Generation!.Key, PeerRoute: peerRoute);
            var report = service.ReportStorage.Reserve(identity).ReportId;
            var response = await link.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = receiver,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply ? "peer-real-reply-probe" : "ordinary prompt" }),
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = peerRoute.PromptId, [GorynychProtocol.MetaReportId] = report } }).WaitAsync(TimeSpan.FromSeconds(40));
            Assert.AreEqual("end_turn", (string?)response?["stopReason"]);
            var saved = new ReportStore(Path.Combine(data, "reports")).GetReport(report);
            Assert.AreEqual("sealed", saved.ProducerEvidence); Assert.IsNotNull(saved.PeerEvidence);
            Assert.AreEqual(peerRoute.PromptId, saved.PeerEvidence.PromptId);
            if (reply)
            {
                var outgoing = saved.PeerEvidence.AcceptedMessageId; Assert.IsNotNull(outgoing);
                var accepted = service.ReplyMailbox.Read(sender, 0, 50, false).Single(e => (string?)e["id"] == outgoing);
                Assert.AreEqual(receiver, (string?)accepted["sender"]); Assert.AreEqual(sender, (string?)accepted["recipient"]);
                Assert.AreEqual("request", (string?)accepted["replyTo"]); Assert.IsTrue((long)accepted["sequence"]! > peerRoute.Sequence);
            }
            else Assert.IsNull(saved.PeerEvidence.AcceptedMessageId, "complete marker proves no matching reply");
        }
        finally
        {
            stop.Cancel(); try { await listener; } catch (OperationCanceledException) { }
            await launcher.CloseNativeGracefully();
        }
    }

    [TestMethod]
    [DataRow(true, false, false, "")]
    [DataRow(false, false, false, "")]
    [DataRow(true, true, false, "")]
    [DataRow(true, false, true, "")]
    [DataRow(true, false, false, "nativeUuid")]
    [DataRow(true, false, false, "acceptedMessageId")]
    [DataRow(true, false, false, "stream")]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task HostedPromptPersistsNegotiatedPeerProducerMarker(bool supported, bool malformed, bool noReply, string invalidWire)
    {
        var dir = TestDirs.New();
        var options = new ClaudeAdapterOptions { DataDirectory = Path.Combine(dir, "claude"), Executable = FakeClaude(),
            ProjectsDirectory = Path.Combine(FakeRoot, "projects"), ClaudeStatePath = Path.Combine(dir, "no-claude-state.json"),
            FetchCatalog = false, AutoTitle = false, DisableForceKillForTests = true, CloseTimeoutMs = 3000,
            HostedPeerTerminal = supported,
            PeerTerminalWireOverrideForTests = invalidWire.Length == 0 ? null : marker => { marker[invalidWire] = invalidWire == "stream" ? "" : "invalid"; return marker; } };
        var adapter = new ClaudeAdapter(options); var hosts = new InProcessHosts(_ => adapter) { CaptureReports = true, PeerTerminalForTests = true };
        await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var id = await NewAsync(client, dir);
        var publicResponse = await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = id,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "peer-native-receipt-probe" }),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerTerminal] = new JsonObject { ["sourceMessageId"] = "request" } } }).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsNull(publicResponse?["_meta"]?[GorynychProtocol.MetaPeerTerminal], "public metadata cannot request private evidence");
        var link = service.Agents["claude"].RouteOf(id)!;
        var peerRoute = new PeerTurnIdentity("request", 17, "sender", id, "service-peer-17-request");
        var identity = new ReportIdentity("peer:17:request", id, null, ReportCause.Peer, PromptId: peerRoute.PromptId,
            MessageId: peerRoute.MessageId, HostId: link.HostId, HostGeneration: link.Generation!.Key, PeerRoute: peerRoute);
        var report = service.ReportStorage.Reserve(identity).ReportId;
        var response = await link.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = id,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = malformed ? "peer-native-malformed-probe" : noReply ? "ordinary prompt" : "peer-native-receipt-probe" }),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = peerRoute.PromptId,
                [GorynychProtocol.MetaReportId] = report,
                [GorynychProtocol.MetaPeerTerminal] = new JsonObject { ["sourceMessageId"] = "foreign" } } }).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual("end_turn", (string?)response?["stopReason"]);
        Assert.IsNull(response?["_meta"]?[GorynychProtocol.MetaPeerTerminal], "host strips private response evidence");
        var saved = service.ReportStorage.GetReport(report);
        Assert.AreEqual("sealed", saved.ProducerEvidence);
        if (!supported || malformed || invalidWire.Length > 0)
        {
            Assert.IsNull(saved.PeerEvidence, "unsupported or incomplete adapter evidence remains unknown");
            var after = await PromptAsync(client, id, "ordinary prompt").WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(StopReason.EndTurn, after.StopReason, "host response pump remains usable");
            return;
        }
        Assert.IsNotNull(saved.PeerEvidence); Assert.AreEqual(peerRoute.PromptId, saved.PeerEvidence.PromptId);
        Assert.AreEqual(noReply ? null : "0123456789abcdef0123456789abcdef", saved.PeerEvidence.AcceptedMessageId);
        Assert.IsTrue(Guid.TryParseExact(saved.PeerEvidence.NativeUuid, "D", out _));
        Assert.IsFalse(string.IsNullOrWhiteSpace(saved.PeerEvidence.Stream));
        var persisted = File.ReadAllText(Path.Combine(dir, "reports", "reports.json"));
        StringAssert.Contains(persisted, "producerPeerEvidence");
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task HostedFakeClaudeEmitsOnlyForegroundNativeReplyWitness()
    {
        var dir = TestDirs.New();
        var seen = new ConcurrentQueue<ClaudePeerReplyWitness>();
        var completed = new TaskCompletionSource<ClaudePeerTerminalEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalCount = 0;
        var options = new ClaudeAdapterOptions { DataDirectory = Path.Combine(dir, "claude"), Executable = FakeClaude(),
            ProjectsDirectory = Path.Combine(FakeRoot, "projects"), ClaudeStatePath = Path.Combine(dir, "no-claude-state.json"),
            FetchCatalog = false, AutoTitle = false, DisableForceKillForTests = true, CloseTimeoutMs = 3000,
            PeerReplyWitnessForTests = w => seen.Enqueue(w), PeerTerminalForTests = e => { Interlocked.Increment(ref terminalCount); completed.TrySetResult(e); } };
        var adapter = new ClaudeAdapter(options); var hosts = new InProcessHosts(_ => adapter);
        await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var id = await NewAsync(client, dir);
        await PromptAsync(client, id, "peer-native-receipt-probe").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(2, seen.Count, "both replies in one frame are retained; replay, subagent and foreign tools are excluded");
        Assert.IsTrue(seen.TryDequeue(out var witness)); Assert.IsNotNull(witness);
        Assert.AreEqual(id, witness.SessionId); Assert.IsFalse(string.IsNullOrWhiteSpace(witness.PromptId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(witness.NativeUuid)); Assert.IsFalse(string.IsNullOrWhiteSpace(witness.Stream));
        Assert.AreEqual("request", witness.TriggerMessageId);
        Assert.AreEqual("0123456789abcdef0123456789abcdef", witness.AcceptedMessageId);
        Assert.IsTrue(seen.TryDequeue(out var later)); Assert.IsNotNull(later);
        Assert.AreEqual("older", later.TriggerMessageId);
        Assert.AreEqual("fedcba9876543210fedcba9876543210", later.AcceptedMessageId);
        var terminal = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(id, terminal.SessionId); Assert.AreEqual(witness.PromptId, terminal.PromptId);
        Assert.AreEqual(witness.NativeUuid, terminal.NativeUuid);
        CollectionAssert.AreEqual(new[] { "request", "older" }, terminal.Replies.Select(r => r.TriggerMessageId).ToArray());
        await PromptAsync(client, id, "peer-native-malformed-probe").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(1, Volatile.Read(ref terminalCount), "malformed reply result is unknown, never proof of absence");
    }
}
