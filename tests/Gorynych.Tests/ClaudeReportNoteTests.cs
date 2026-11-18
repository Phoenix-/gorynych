using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Service;

namespace Gorynych.Tests;

public sealed partial class ClaudeHeadlessTests
{
    sealed class ReportNoteRig(AgentInstance owner, JsonRpcConnection rpc, string dir, string session,
        ConcurrentQueue<JsonObject> updates) : IAsyncDisposable
    {
        internal string Dir => dir;
        internal string Session => session;
        internal JsonRpcConnection Rpc => rpc;
        internal ConcurrentQueue<JsonObject> Updates => updates;
        internal Task<JsonNode?> Prompt(string text) => rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt,
            new JsonObject { ["sessionId"] = session, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) });
        internal Task<JsonNode?> Send(string report, string attempt, string text, string? target = null) => rpc.SendRequestAsync(GorynychProtocol.MethodReportNote,
            new JsonObject { ["sessionId"] = target ?? session, ["text"] = text, ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = report, ["attemptId"] = attempt } } });
        internal async Task<JsonNode> Status(string report, string attempt, string? target = null) => (await rpc.SendRequestAsync(GorynychProtocol.MethodReportNoteStatus,
            new JsonObject { ["sessionId"] = target ?? session, ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = report, ["attemptId"] = attempt } } }))!["_meta"]![GorynychProtocol.MetaReportNote]!;
        public async ValueTask DisposeAsync() { File.WriteAllText(Path.Combine(dir, "release-duplicate-events"), ""); File.WriteAllText(Path.Combine(dir, "release-report-echo"), ""); File.WriteAllText(Path.Combine(dir, "release-contradiction-response"), ""); File.WriteAllText(Path.Combine(dir, "release-late-cancelled"), ""); File.WriteAllText(Path.Combine(dir, "release-report-result"), ""); await rpc.DisposeAsync(); await owner.DisposeAsync(); }
    }

    static async Task<ReportNoteRig> NoteRig(int limit = 256, int stallMs = 1500, Func<Task>? afterWrite = null, int globalLimit = 4096)
    {
        var dir = TestDirs.New();
        var owner = await new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false, stallMs: stallMs, reportNoteLimit: limit, afterReportNoteWrite: afterWrite, reportNoteGlobalLimit: globalLimit)).StartAsync(CancellationToken.None);
        var updates = new ConcurrentQueue<JsonObject>();
        var rpc = new JsonRpcConnection(owner.Input, owner.Output, "report-note-test")
        {
            RequestHandler = (_, _) => Task.FromResult<JsonNode?>(AcpJson.ToNode(new RequestPermissionResponse
                { Outcome = new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId } })),
            NotificationHandler = (_, p) => { if (p?["update"] is JsonObject update) updates.Enqueue(update.DeepClone().AsObject()); return Task.CompletedTask; },
        };
        rpc.Start();
        var init = await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
        Assert.AreEqual(true, (bool?)init.Meta?[GorynychProtocol.CapReportNote]);
        var session = (await rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, McpServers = [] })).SessionId;
        var rig = new ReportNoteRig(owner, rpc, dir, session, updates);
        await rig.Prompt("warmup");
        return rig;
    }

    static string NewReportId() => Guid.NewGuid().ToString("N");
    static JsonNode Receipt(JsonNode? response) => response!["_meta"]![GorynychProtocol.MetaReportNote]!;
    static int InputCount(string session, string uuid)
    {
        try
        {
            var path = Path.Combine(FakeRoot, session + ".inputs.jsonl");
            return File.Exists(path) ? File.ReadAllLines(path).Count(l => (string?)JsonNode.Parse(l)?["uuid"] == uuid) : 0;
        }
        catch (IOException) { return -1; }
    }
    static async Task<JsonNode> WaitStatus(ReportNoteRig rig, string report, string attempt, Func<JsonNode, bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(10); JsonNode status;
        do { status = await rig.Status(report, attempt); if (predicate(status)) return status; await Task.Delay(20); } while (DateTime.UtcNow < until);
        Assert.Fail("Timed out waiting for receipt: " + status.ToJsonString()); return status;
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_DeduplicatesAndTracksOnlyItsNativeEchoAndCycle()
    {
        await using var rig = await NoteRig();
        var report = NewReportId(); var attempt = NewReportId();
        var slow = rig.Prompt("slow hold");
        await Until(() => rig.Updates.Any(u => (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        var initial = Receipt(await rig.Send(report, attempt, "receipt-hold-after-echo"));
        var uuid = (string)initial["nativeMessageId"]!;
        Assert.AreEqual(true, (bool?)initial["writeStarted"]); Assert.AreEqual(true, (bool?)initial["writeCompleted"]);
        Assert.AreEqual(false, (bool?)initial["taken"]); Assert.IsNull(initial["cycleResult"]);
        var duplicate = Receipt(await rig.Send(report, attempt, "receipt-hold-after-echo"));
        Assert.AreEqual(uuid, (string?)duplicate["nativeMessageId"]);
        var upper = Receipt(await rig.Send(report.ToUpperInvariant(), attempt.ToUpperInvariant(), "receipt-hold-after-echo"));
        Assert.AreEqual(uuid, (string?)upper["nativeMessageId"], "GUID spellings identify the same attempt");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Send(report, attempt, "changed"));
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Send(NewReportId(), attempt, "receipt-hold-after-echo"));
        Assert.AreEqual(1, InputCount(rig.Session, uuid));
        await slow;
        var taken = await WaitStatus(rig, report, attempt, s => (bool?)s["taken"] == true);
        Assert.AreEqual("taken", (string?)taken["state"]); Assert.IsNull(taken["cycleResult"], "previous cycle did not take this UUID");
        File.WriteAllText(Path.Combine(rig.Dir, "release-report-result"), "");
        var result = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "cycle_result");
        Assert.AreEqual("ended", (string?)result["cycleResult"]); Assert.AreEqual("end_turn", (string?)result["nativeStopReason"]);
        Assert.AreEqual("success", (string?)result["nativeSubtype"]); Assert.AreEqual(1, InputCount(rig.Session, uuid));
        Assert.AreEqual(uuid, (string?)(await rig.Status(report, attempt))["nativeMessageId"]);
        Assert.AreEqual("unknown", (string?)(await rig.Status(report, NewReportId()))["state"]);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_ExplicitCancelAndCapacityNeverReplayInput()
    {
        await using var rig = await NoteRig(limit: 1, stallMs: 250);
        var report = NewReportId(); var attempt = NewReportId();
        var slow = rig.Prompt("slow hold");
        await Until(() => rig.Updates.Any(u => (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        var receipt = Receipt(await rig.Send(report, attempt, "queued report"));
        var uuid = (string)receipt["nativeMessageId"]!;
        var full = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Send(NewReportId(), NewReportId(), "other"));
        StringAssert.Contains(full.Message, "capacity");
        await rig.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = rig.Session });
        await slow;
        var dropped = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "dropped");
        Assert.AreEqual(false, (bool?)dropped["taken"]); StringAssert.Contains((string?)dropped["dropReason"], "cancelled");
        Assert.AreEqual(1, InputCount(rig.Session, uuid));
        var retry = Receipt(await rig.Send(report, attempt, "queued report"));
        Assert.AreEqual("dropped", (string?)retry["state"]); Assert.AreEqual(1, InputCount(rig.Session, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow("receipt-exit-before-echo", false)]
    [DataRow("receipt-exit-after-echo", true)]
    public async Task ReportNote_ProcessExitIsUncertainAndKeepsObservedEcho(string text, bool expectedTaken)
    {
        await using var rig = await NoteRig();
        var report = NewReportId(); var attempt = NewReportId();
        var initial = Receipt(await rig.Send(report, attempt, text)); var uuid = (string)initial["nativeMessageId"]!;
        var uncertain = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "uncertain");
        Assert.AreEqual(expectedTaken, (bool?)uncertain["taken"]); Assert.IsNull(uncertain["cycleResult"]);
        Assert.AreEqual(uuid, (string?)Receipt(await rig.Send(report, attempt, text))["nativeMessageId"]);
        Assert.AreEqual(1, InputCount(rig.Session, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_NativeFailedResultIsSeparateFromTakenAndWrite()
    {
        await using var rig = await NoteRig();
        var report = NewReportId(); var attempt = NewReportId();
        await rig.Send(report, attempt, "receipt-fail-after-echo");
        var status = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "cycle_result");
        Assert.AreEqual(true, (bool?)status["taken"]); Assert.AreEqual("failed", (string?)status["cycleResult"]);
        StringAssert.Contains((string?)status["nativeError"], "fake receipt failure");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_StallIsUncertainAndPinnedUntilExplicitEvidence()
    {
        await using var rig = await NoteRig(limit: 1, stallMs: 200);
        var report = NewReportId(); var attempt = NewReportId();
        var initial = Receipt(await rig.Send(report, attempt, "swallow this report"));
        var uuid = (string)initial["nativeMessageId"]!;
        var status = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "uncertain");
        Assert.AreEqual(false, (bool?)status["taken"]); Assert.IsNull(status["cycleResult"]);
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Send(NewReportId(), NewReportId(), "new report"));
        StringAssert.Contains(error.Message, "capacity");
        Assert.AreEqual(uuid, (string?)Receipt(await rig.Send(report, attempt, "swallow this report"))["nativeMessageId"]);
        Assert.AreEqual(1, InputCount(rig.Session, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_CancelledCycleKeepsTakenEvidence()
    {
        await using var rig = await NoteRig();
        var report = NewReportId(); var attempt = NewReportId();
        var slow = rig.Prompt("slow work");
        await Until(() => rig.Updates.Any(u => (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        await rig.Send(report, attempt, "joined report");
        await WaitStatus(rig, report, attempt, s => (bool?)s["taken"] == true);
        await rig.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = rig.Session });
        await slow;
        var result = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "cycle_result");
        Assert.AreEqual(true, (bool?)result["taken"]); Assert.AreEqual("cancelled", (string?)result["cycleResult"]);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_ConflictingSessionAndUnsupportedTransportRefuseBeforeWrite()
    {
        await using var rig = await NoteRig();
        var report = NewReportId(); var attempt = NewReportId();
        var initial = Receipt(await rig.Send(report, attempt, "receipt-hold-after-echo"));
        var uuid = (string)initial["nativeMessageId"]!;
        var other = (await rig.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = rig.Dir, McpServers = [] })).SessionId;
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Send(report, attempt, "receipt-hold-after-echo", other));
        await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await rig.Status(report, attempt, other));
        Assert.AreEqual(1, InputCount(rig.Session, uuid)); Assert.AreEqual(0, InputCount(other, uuid));
        File.WriteAllText(Path.Combine(rig.Dir, "release-report-result"), "");

        var dir = TestDirs.New();
        await using var unsupported = await new ClaudeAdapter(Options(dir, liveNotes: false, fetchCatalog: false)).StartAsync(CancellationToken.None);
        await using var rpc = new JsonRpcConnection(unsupported.Input, unsupported.Output, "unsupported-report-note"); rpc.Start();
        var init = await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
        Assert.AreEqual(false, (bool?)init.Meta?[GorynychProtocol.CapReportNote]);
        var denied = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rpc.SendRequestAsync(GorynychProtocol.MethodReportNote,
            new JsonObject { ["sessionId"] = "missing", ["text"] = "x", ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = new JsonObject
                { ["reportId"] = report, ["attemptId"] = attempt } } }));
        Assert.AreEqual(JsonRpcException.MethodNotFound, denied.Code);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_SurvivesConductorReconnectToSameLiveHost()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter);
        var first = hosts.Conductor(dir, adapter); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first);
        Conductor? next = null; var firstDisposed = false;
        try
        {
        var id = await NewAsync(client, dir);
        await PromptAsync(client, id, "warmup");
        var route = first.Agents["claude"].RouteOf(id)!;
        Assert.AreEqual(true, (bool?)route.Info.Meta?[GorynychProtocol.CapReportNote]);
        var report = NewReportId(); var attempt = NewReportId();
        JsonObject Input(bool withText)
        {
            var input = new JsonObject { ["sessionId"] = id,
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = report, ["attemptId"] = attempt } } };
            if (withText) input["text"] = "receipt-hold-after-echo";
            return input;
        }
        var receipt = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodReportNote, Input(true)));
        var uuid = (string)receipt["nativeMessageId"]!;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonNode before;
        do
        {
            before = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodReportNoteStatus, Input(false)));
            if ((bool?)before["taken"] == true) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);
        Assert.AreEqual(true, (bool?)before["taken"]);
        await HostTests.RestartAsync(first); hosts.Service = null;
        await first.DisposeAsync(); await client.DisposeAsync(); firstDisposed = true; hosts.BreakAll();
        next = hosts.Conductor(dir, adapter); hosts.Service = next;
        await LinkHarness.Until(() => next.Agents["claude"].RouteOf(id) is not null, "host reattached").WaitAsync(TimeSpan.FromSeconds(15));
        var reattached = next.Agents["claude"].RouteOf(id)!;
        Assert.AreEqual(true, (bool?)reattached.Info.Meta?[GorynychProtocol.CapReportNote]);
        var status = Receipt(await reattached.Rpc.SendRequestAsync(GorynychProtocol.MethodReportNoteStatus, Input(false)));
        Assert.AreEqual(uuid, (string?)status["nativeMessageId"]); Assert.AreEqual(true, (bool?)status["taken"]);
        Assert.IsNull(status["cycleResult"]); Assert.AreEqual(1, InputCount(id, uuid));
        File.WriteAllText(Path.Combine(dir, "release-report-result"), "");
        deadline = DateTime.UtcNow.AddSeconds(15);
        do
        {
            status = Receipt(await reattached.Rpc.SendRequestAsync(GorynychProtocol.MethodReportNoteStatus, Input(false)));
            if ((string?)status["state"] == "cycle_result") break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);
        Assert.AreEqual("ended", (string?)status["cycleResult"]);
        Assert.AreEqual(1, InputCount(id, uuid));
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "release-report-result"), "");
            if (next is not null) await next.DisposeAsync();
            if (!firstDisposed) { await client.DisposeAsync(); await first.DisposeAsync(); }
            hosts.BreakAll();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_LostWriteConfirmationRemainsUncertainUntilNativeEcho()
    {
        await using var rig = await NoteRig(afterWrite: () => throw new IOException("injected post-write confirmation failure"));
        var report = NewReportId(); var attempt = NewReportId();
        var first = Receipt(await rig.Send(report, attempt, "receipt-hold-before-echo"));
        var uuid = (string)first["nativeMessageId"]!;
        Assert.AreEqual("uncertain", (string?)first["state"]); Assert.AreEqual(true, (bool?)first["writeStarted"]);
        Assert.AreEqual(false, (bool?)first["writeCompleted"]); Assert.AreEqual(false, (bool?)first["taken"]);
        Assert.AreEqual(uuid, (string?)Receipt(await rig.Send(report, attempt, "receipt-hold-before-echo"))["nativeMessageId"]);
        // The input write can return before the fake consumes and closes its input log.
        await LinkHarness.Until(() => InputCount(rig.Session, uuid) == 1, "native fake observed exactly one report input").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(1, InputCount(rig.Session, uuid));
        File.WriteAllText(Path.Combine(rig.Dir, "release-report-echo"), "");
        var result = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "cycle_result");
        Assert.AreEqual(true, (bool?)result["taken"]); Assert.AreEqual("ended", (string?)result["cycleResult"]);
        Assert.AreEqual(false, (bool?)result["writeCompleted"], "the echo does not invent a write confirmation");
        Assert.AreEqual(1, InputCount(rig.Session, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_AdapterRestartReturnsUnknownWithoutRedispatch()
    {
        var rig = await NoteRig(); var dir = rig.Dir; var session = rig.Session;
        var report = NewReportId(); var attempt = NewReportId();
        var first = Receipt(await rig.Send(report, attempt, "receipt-hold-after-echo"));
        var uuid = (string)first["nativeMessageId"]!;
        await WaitStatus(rig, report, attempt, s => (bool?)s["taken"] == true);
        await rig.DisposeAsync();
        await using var adapter = await new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false)).StartAsync(CancellationToken.None);
        await using var rpc = new JsonRpcConnection(adapter.Input, adapter.Output, "restarted-adapter"); rpc.Start();
        await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
        var unknown = Receipt(await rpc.SendRequestAsync(GorynychProtocol.MethodReportNoteStatus, new JsonObject { ["sessionId"] = session,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = report, ["attemptId"] = attempt } } }));
        Assert.AreEqual("unknown", (string?)unknown["state"]);
        Assert.AreEqual(1, InputCount(session, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_ClosingSessionKeepsUnresolvedReceiptUntilAdapterExit()
    {
        await using var rig = await NoteRig(stallMs: 150);
        var report = NewReportId(); var attempt = NewReportId();
        var initial = Receipt(await rig.Send(report, attempt, "swallow retained receipt"));
        var uuid = (string)initial["nativeMessageId"]!;
        await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "uncertain");
        await rig.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = rig.Session });
        var afterClose = await rig.Status(report, attempt);
        Assert.AreEqual("uncertain", (string?)afterClose["state"]);
        Assert.AreEqual(uuid, (string?)afterClose["nativeMessageId"]);
        var retry = Receipt(await rig.Send(report, attempt, "swallow retained receipt"));
        Assert.AreEqual(uuid, (string?)retry["nativeMessageId"]); Assert.AreEqual(1, InputCount(rig.Session, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_ResolvedEntriesEvictButUnresolvedClosedSessionConsumesGlobalCapacity()
    {
        await using (var rig = await NoteRig(limit: 1))
        {
            var report = NewReportId(); var first = NewReportId();
            await rig.Send(report, first, "resolved report");
            await WaitStatus(rig, report, first, s => (string?)s["state"] == "cycle_result");
            await rig.Send(report, NewReportId(), "another report");
            Assert.AreEqual("unknown", (string?)(await rig.Status(report, first))["state"], "resolved retention is bounded");
        }
        await using (var rig = await NoteRig(globalLimit: 1, stallMs: 100))
        {
            var report = NewReportId(); var first = NewReportId();
            await rig.Send(report, first, "swallow global pin");
            await WaitStatus(rig, report, first, s => (string?)s["state"] == "uncertain");
            await rig.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = rig.Session });
            var other = (await rig.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = rig.Dir, McpServers = [] })).SessionId;
            await rig.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = other,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "warmup" }) });
            var full = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Send(report, NewReportId(), "second", other));
            StringAssert.Contains(full.Message, "capacity");
            Assert.AreEqual("uncertain", (string?)(await rig.Status(report, first))["state"]);
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_StallThenLateEchoAndMatchingResultRefineWithoutRewrite()
    {
        await using var rig = await NoteRig(stallMs: 120);
        var report = NewReportId(); var attempt = NewReportId();
        var initial = Receipt(await rig.Send(report, attempt, "receipt-hold-before-echo"));
        var uuid = (string)initial["nativeMessageId"]!;
        var uncertain = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "uncertain");
        Assert.AreEqual(false, (bool?)uncertain["taken"]); Assert.AreEqual(true, (bool?)uncertain["writeCompleted"]);
        File.WriteAllText(Path.Combine(rig.Dir, "release-report-echo"), "");
        var result = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "cycle_result");
        Assert.AreEqual(true, (bool?)result["taken"]); Assert.AreEqual(false, (bool?)result["nativeCancelled"]);
        Assert.AreEqual("ended", (string?)result["cycleResult"]); Assert.AreEqual(1, InputCount(rig.Session, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_OtherResultAndOtherUuidCannotCompleteUntakenAttempt()
    {
        await using var rig = await NoteRig(stallMs: 120);
        var report = NewReportId(); var first = NewReportId(); var second = NewReportId();
        var firstReceipt = Receipt(await rig.Send(report, first, "receipt-result-without-echo"));
        var firstUuid = (string)firstReceipt["nativeMessageId"]!;
        var missing = await WaitStatus(rig, report, first, s => (string?)s["state"] == "uncertain");
        Assert.AreEqual(false, (bool?)missing["taken"]); Assert.IsNull(missing["cycleResult"]);
        var otherReceipt = Receipt(await rig.Send(report, second, "unrelated native note"));
        Assert.AreNotEqual(firstUuid, (string?)otherReceipt["nativeMessageId"]);
        await WaitStatus(rig, report, second, s => (string?)s["state"] == "cycle_result");
        var after = await rig.Status(report, first);
        Assert.AreEqual("uncertain", (string?)after["state"]); Assert.AreEqual(false, (bool?)after["taken"]);
        Assert.IsNull(after["cycleResult"]); Assert.AreEqual(1, InputCount(rig.Session, firstUuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_CancelledBeforeTakenThenLateEchoIsPinnedContradiction()
    {
        await using var rig = await NoteRig(limit: 1);
        File.WriteAllText(Path.Combine(rig.Dir, "receipt-late-cancelled"), "");
        File.WriteAllText(Path.Combine(rig.Dir, "receipt-duplicate-events"), "");
        var report = NewReportId(); var attempt = NewReportId();
        var slow = rig.Prompt("slow hold");
        await Until(() => rig.Updates.Any(u => (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        var initial = Receipt(await rig.Send(report, attempt, "queued contradictory report"));
        var uuid = (string)initial["nativeMessageId"]!;
        await rig.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = rig.Session });
        await slow;
        var dropped = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "dropped");
        Assert.AreEqual(true, (bool?)dropped["nativeCancelled"]); Assert.AreEqual(false, (bool?)dropped["taken"]);
        File.WriteAllText(Path.Combine(rig.Dir, "release-late-cancelled"), "");
        var conflict = await WaitStatus(rig, report, attempt, s => (bool?)s["contradiction"] == true && s["cycleResult"] is not null);
        Assert.AreEqual("uncertain", (string?)conflict["state"]); Assert.AreEqual(true, (bool?)conflict["taken"]);
        Assert.AreEqual(true, (bool?)conflict["nativeCancelled"]); Assert.AreEqual("ended", (string?)conflict["cycleResult"]);
        Assert.IsNull(conflict["nativeError"]);
        File.WriteAllText(Path.Combine(rig.Dir, "release-duplicate-events"), "");
        await Until(() => File.Exists(Path.Combine(rig.Dir, "duplicate-events-sent")));
        Assert.IsTrue(File.Exists(Path.Combine(rig.Dir, "duplicate-events-sent")));
        await rig.Prompt("native stream barrier");
        var repeated = await rig.Status(report, attempt);
        Assert.AreEqual("uncertain", (string?)repeated["state"]); Assert.AreEqual("ended", (string?)repeated["cycleResult"]);
        Assert.IsNull(repeated["nativeError"], "a later result must not replace the first matching result");
        StringAssert.Contains((string?)conflict["uncertainty"], "reconcile");
        var full = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Send(report, NewReportId(), "must not fallback"));
        StringAssert.Contains(full.Message, "capacity");
        Assert.AreEqual(1, InputCount(rig.Session, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_CancelledAfterTakenNeverErasesEchoOrAllowsFallback()
    {
        await using var rig = await NoteRig(limit: 1);
        var report = NewReportId(); var attempt = NewReportId();
        var slow = rig.Prompt("slow work");
        await Until(() => rig.Updates.Any(u => (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        var initial = Receipt(await rig.Send(report, attempt, "joined contradictory report"));
        var uuid = (string)initial["nativeMessageId"]!;
        await WaitStatus(rig, report, attempt, s => (bool?)s["taken"] == true);
        File.WriteAllText(Path.Combine(rig.Dir, "hold-contradiction-response"), "");
        await rig.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = rig.Session });
        await slow;
        var processed = await WaitStatus(rig, report, attempt, s => s["cycleResult"] is not null);
        Assert.AreEqual("uncertain", (string?)processed["state"]); Assert.AreEqual(false, (bool?)processed["nativeCancelled"]);
        Assert.AreEqual(true, (bool?)processed["cancelReceiptPending"]);
        var contender = rig.Send(report, NewReportId(), "must not evict while interrupt is pending");
        await Task.Delay(50); Assert.IsFalse(contender.IsCompleted, "native input is serialized behind the pending interrupt receipt");
        File.WriteAllText(Path.Combine(rig.Dir, "release-contradiction-response"), "");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => contender);
        var conflict = await WaitStatus(rig, report, attempt, s => (bool?)s["contradiction"] == true && s["cycleResult"] is not null);
        Assert.AreEqual("uncertain", (string?)conflict["state"]); Assert.AreEqual(true, (bool?)conflict["nativeCancelled"]);
        Assert.AreEqual(true, (bool?)conflict["taken"]); Assert.AreEqual("cancelled", (string?)conflict["cycleResult"]);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Send(report, NewReportId(), "must not fallback"));
        Assert.AreEqual(uuid, (string?)Receipt(await rig.Send(report, attempt, "joined contradictory report"))["nativeMessageId"]);
        Assert.AreEqual(1, InputCount(rig.Session, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReportNote_MissingNativeCancelListStaysUncertainAfterResult()
    {
        await using var rig = await NoteRig(limit: 1);
        File.WriteAllText(Path.Combine(rig.Dir, "receipt-malformed-interrupt"), "");
        var report = NewReportId(); var attempt = NewReportId();
        var slow = rig.Prompt("slow work");
        await Until(() => rig.Updates.Any(u => (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        await rig.Send(report, attempt, "joined receipt without cancel list");
        await WaitStatus(rig, report, attempt, s => (bool?)s["taken"] == true);
        await rig.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = rig.Session });
        await slow;
        var status = await WaitStatus(rig, report, attempt, s => (bool?)s["cancelReceiptPending"] == true && s["cycleResult"] is not null);
        Assert.AreEqual("uncertain", (string?)status["state"]); Assert.AreEqual(true, (bool?)status["taken"]);
        Assert.AreEqual(false, (bool?)status["nativeCancelled"]);
        StringAssert.Contains((string?)status["uncertainty"], "interrupt");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Send(report, NewReportId(), "must not replay"));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow("slow own report", "cancelled")]
    [DataRow("slow receipt-success-after-interrupt", "ended")]
    public async Task ReportNote_OwnTurnRetainsCancelAndNativeOutcome(string text, string expected)
    {
        await using var rig = await NoteRig();
        var report = NewReportId(); var attempt = NewReportId();
        var receipt = Receipt(await rig.Send(report, attempt, text));
        var uuid = (string)receipt["nativeMessageId"]!;
        await WaitStatus(rig, report, attempt, s => (bool?)s["taken"] == true);
        await Until(() => rig.Updates.Any(u => (string?)u["title"] is { } t && t.Contains("sleep", StringComparison.Ordinal)));
        await rig.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = rig.Session });
        var result = await WaitStatus(rig, report, attempt, s => (string?)s["state"] == "cycle_result");
        Assert.AreEqual(true, (bool?)result["cancelRequested"], "the native turn belongs to the note, not a client prompt");
        Assert.AreEqual(expected, (string?)result["cycleResult"]);
        if (expected == "cancelled") Assert.IsNotNull(result["nativeError"]); else Assert.IsNull(result["nativeError"]);
        Assert.AreEqual(1, InputCount(rig.Session, uuid));
    }
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task HostTrackedReceiptSurvivesConductorRestartWithoutSecondNativeInput()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter) { BufferBytes = 256 };
        var first = hosts.Conductor(dir, adapter); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first); Conductor? next = null; var firstDisposed = false;
        try
        {
            var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
            var route = first.Agents["claude"].RouteOf(parent)!;
            Assert.AreEqual(true, (bool?)route.Info.Meta?[GorynychProtocol.CapHostReportNote]?["supported"]);
            var store = first.ReportStorage;
            var report = store.Reserve(new ReportIdentity("host-note", "child", parent, ReportCause.User, "child-prompt")).ReportId;
            using (var capture = store.OpenCapture(report)) { capture.Append("source result"); capture.Seal("ended"); store.Commit(report, capture); }
            store.AcknowledgeProducer(report);
            var text = "receipt-hold-after-echo";
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            var claim = store.ClaimDelivery(report, parent, route.HostId!, route.Generation!.Key, "note", digest);
            store.StartDelivery(report, claim.AttemptId);
            var request = new JsonObject { ["sessionId"] = parent, ["text"] = text,
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = report, ["attemptId"] = claim.AttemptId } } };
            var inputFile = Path.Combine(FakeRoot, parent + ".inputs.jsonl"); var beforeRefusal = File.ReadAllLines(inputFile).Length;
            var wrong = request.DeepClone().AsObject(); wrong["text"] = "different contents";
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, wrong));
            store.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"hostHandoffStarted\":true", StringComparison.Ordinal)) throw new IOException("host claim fault");
                AtomicFile.WriteAllText(path, json);
            };
            try
            {
                await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request.DeepClone()));
                Assert.ThrowsExactly<InvalidOperationException>(() => store.ReadNoteReceipt(report, parent, route.HostId!, route.Generation!.Key, claim.AttemptId));
            }
            finally { store.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json); }
            Assert.AreEqual(beforeRefusal, File.ReadAllLines(inputFile).Length, "new invalid/failed claim caused no native input");
            var response = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request));
            var uuid = (string)response["nativeMessageId"]!;
            await LinkHarness.Until(() => (bool?)store.ReadNoteReceipt(report, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["taken"] == true,
                "host durably recorded native echo").WaitAsync(TimeSpan.FromSeconds(15));
            var duplicate = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request.DeepClone()));
            Assert.AreEqual(uuid, (string?)duplicate["nativeMessageId"]);
            await LinkHarness.Until(() => InputCount(parent, uuid) == 1, "first native input observed").WaitAsync(TimeSpan.FromSeconds(15));
            FileStream? exclusiveManifest = null;
            await LinkHarness.Until(() =>
            {
                try { exclusiveManifest = new FileStream(Path.Combine(dir, "reports", "reports.json"), FileMode.Open, FileAccess.Read, FileShare.None); return true; }
                catch (IOException) { return false; }
            }, "exclusive manifest fault acquired").WaitAsync(TimeSpan.FromSeconds(15));
            using (exclusiveManifest)
            {
                var unavailable = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request.DeepClone()));
                StringAssert.Contains(unavailable.Message, "prior native handoff may have occurred");
                StringAssert.Contains(unavailable.Message, "Do not retry or fall back");
                Assert.IsFalse(unavailable.Message.Contains("refused before native dispatch", StringComparison.Ordinal));
            }
            var liveStatus = new JsonObject { ["sessionId"] = parent, ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = report, ["attemptId"] = claim.AttemptId } } };
            var afterHeal = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNoteStatus, liveStatus));
            Assert.AreEqual(uuid, (string?)afterHeal["nativeMessageId"]); Assert.AreEqual(true, (bool?)afterHeal["taken"]);
            Assert.AreEqual(1, InputCount(parent, uuid));
            var replay = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject { [parent] = 0 } });
            Assert.IsTrue((replay?["trimmed"] as JsonArray)!.Any(x => (string?)x == parent), "bounded host replay was evicted");
            await HostTests.RestartAsync(first); hosts.Service = null;
            await first.DisposeAsync(); await client.DisposeAsync(); firstDisposed = true; hosts.BreakAll();
            next = hosts.Conductor(dir, adapter); hosts.Service = next;
            await LinkHarness.Until(() => next.Agents["claude"].RouteOf(parent) is not null, "host reattached").WaitAsync(TimeSpan.FromSeconds(15));
            var reattached = next.Agents["claude"].RouteOf(parent)!;
            Assert.AreEqual(route.HostId, reattached.HostId);
            var kept = next.ReportStorage.ReadNoteReceipt(report, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)!;
            Assert.AreEqual(uuid, (string?)kept["nativeMessageId"]); Assert.AreEqual(true, (bool?)kept["taken"]);
            var receiptFaults = 0;
            store.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"cycleResult\":\"ended\"", StringComparison.Ordinal))
                { Interlocked.Increment(ref receiptFaults); throw new IOException("receipt publication fault"); }
                AtomicFile.WriteAllText(path, json);
            };
            try
            {
                File.WriteAllText(Path.Combine(dir, "release-report-result"), "");
                await LinkHarness.Until(() => Volatile.Read(ref receiptFaults) > 0 &&
                    (string?)next.Status()["_meta"]?["gorynych/reportRecovery"]?["lastFailure"] is not null,
                    "injected receipt fault and visible conductor health").WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsNull(next.ReportStorage.ReadNoteReceipt(report, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["cycleResult"]);
            }
            finally { store.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json); }
            var statusRequest = new JsonObject { ["sessionId"] = parent, ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = report, ["attemptId"] = claim.AttemptId } } };
            var refreshed = Receipt(await reattached.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNoteStatus, statusRequest));
            Assert.AreEqual("ended", (string?)refreshed["cycleResult"]);
            Assert.AreEqual("ended", (string?)next.ReportStorage.ReadNoteReceipt(report, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["cycleResult"]);
            await LinkHarness.Until(() => InputCount(parent, uuid) == 1, "native input exactly once").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, InputCount(parent, uuid));
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "release-report-result"), "");
            if (next is not null) await next.DisposeAsync();
            if (!firstDisposed) { await client.DisposeAsync(); await first.DisposeAsync(); }
            hosts.BreakAll();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow("receipt-fail-after-echo", "failed", true)]
    [DataRow("receipt-exit-before-echo", null, false)]
    [DataRow("receipt-exit-after-echo", null, true)]
    public async Task HostPersistsNativeFailureOrExitFacts(string text, string? result, bool taken)
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var route = service.Agents["claude"].RouteOf(parent)!; var store = service.ReportStorage;
        var report = store.Reserve(new ReportIdentity("host-native-" + text, "child", parent, ReportCause.User, "child-prompt")).ReportId;
        using (var capture = store.OpenCapture(report)) { capture.Append("result"); capture.Seal("ended"); store.Commit(report, capture); }
        store.AcknowledgeProducer(report);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        var claim = store.ClaimDelivery(report, parent, route.HostId!, route.Generation!.Key, "note", digest);
        store.StartDelivery(report, claim.AttemptId);
        var request = new JsonObject { ["sessionId"] = parent, ["text"] = text, ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = report, ["attemptId"] = claim.AttemptId } } };
        var response = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request));
        var uuid = (string)response["nativeMessageId"]!;
        JsonObject? facts = null;
        await LinkHarness.Until(() =>
        {
            facts = store.ReadNoteReceipt(report, parent, route.HostId!, route.Generation!.Key, claim.AttemptId);
            return facts is not null && (result is null ? (string?)facts["uncertainty"] is not null : (string?)facts["cycleResult"] == result);
        }, "native result or exit fact persisted").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(taken, (bool?)facts!["taken"]); Assert.AreEqual(result, (string?)facts["cycleResult"]);
        if (result == "failed") StringAssert.Contains((string?)facts["nativeError"], "fake receipt failure");
        await LinkHarness.Until(() => InputCount(parent, uuid) == 1, "native input exactly once").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(1, InputCount(parent, uuid));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task HostPersistsLateEchoAndResultWithoutAnyConductor()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter) { BufferBytes = 256 };
        var first = hosts.Conductor(dir, adapter); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first); Conductor? next = null; var firstDisposed = false;
        try
        {
            var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
            var route = first.Agents["claude"].RouteOf(parent)!; var store = first.ReportStorage;
            var id = store.Reserve(new ReportIdentity("host-disconnected", "child", parent, ReportCause.User, "child-prompt")).ReportId;
            using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
            store.AcknowledgeProducer(id);
            var text = "receipt-hold-before-echo";
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            var claim = store.ClaimDelivery(id, parent, route.HostId!, route.Generation!.Key, "note", digest);
            store.StartDelivery(id, claim.AttemptId);
            var request = new JsonObject { ["sessionId"] = parent, ["text"] = text, ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId } } };
            var initial = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request));
            var uuid = (string)initial["nativeMessageId"]!;
            await LinkHarness.Until(() => (bool?)store.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["writeCompleted"] == true,
                "written fact recorded before disconnect").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(false, (bool?)store.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["taken"]);
            await HostTests.RestartAsync(first); hosts.Service = null;
            await first.DisposeAsync(); await client.DisposeAsync(); firstDisposed = true; hosts.BreakAll();
            File.WriteAllText(Path.Combine(dir, "release-report-echo"), "");
            await LinkHarness.Until(() => (string?)store.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["cycleResult"] == "ended",
                "late native result persisted with no conductor").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsFalse(hosts["claude"].Run.IsCompleted, "the producing host remains alive");
            next = hosts.Conductor(dir, adapter); hosts.Service = next;
            await LinkHarness.Until(() => next.Agents["claude"].RouteOf(parent) is not null, "host reattached").WaitAsync(TimeSpan.FromSeconds(15));
            var saved = next.ReportStorage.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)!;
            Assert.AreEqual(uuid, (string?)saved["nativeMessageId"]); Assert.AreEqual(true, (bool?)saved["taken"]);
            Assert.AreEqual("ended", (string?)saved["cycleResult"]);
            var duplicate = Receipt(await next.Agents["claude"].RouteOf(parent)!.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request.DeepClone()));
            Assert.AreEqual(uuid, (string?)duplicate["nativeMessageId"]);
            await LinkHarness.Until(() => InputCount(parent, uuid) == 1, "native input exactly once").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, InputCount(parent, uuid));
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "release-report-echo"), "");
            if (next is not null) await next.DisposeAsync();
            if (!firstDisposed) { await client.DisposeAsync(); await first.DisposeAsync(); }
            hosts.BreakAll();
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task HostTrackedResolvedReceiptRemainsBoundedUntilFutureAck()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false, reportNoteLimit: 1));
        var store = new ReportStore(Path.Combine(dir, "host-protocol-reports"));
        var hosts = new InProcessHosts(_ => adapter) { HostReportStore = store };
        await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var route = service.Agents["claude"].RouteOf(parent)!;
        (string Id, ReportDeliveryClaim Claim, JsonObject Request) Prepare(string key, string text)
        {
            var id = store.Reserve(new ReportIdentity(key, "child", parent, ReportCause.User, "child-prompt")).ReportId;
            using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
            store.AcknowledgeProducer(id);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            var claim = store.ClaimDelivery(id, parent, route.HostId!, route.Generation!.Key, "note", digest);
            store.StartDelivery(id, claim.AttemptId);
            return (id, claim, new JsonObject { ["sessionId"] = parent, ["text"] = text, ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId } } });
        }
        var first = Prepare("pin-first", "receipt-hold-after-echo");
        try
        {
            var response = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, first.Request));
            var uuid = (string)response["nativeMessageId"]!;
            await LinkHarness.Until(() => (bool?)store.ReadNoteReceipt(first.Id, parent, route.HostId!, route.Generation!.Key, first.Claim.AttemptId)?["taken"] == true,
                "first tracked echo").WaitAsync(TimeSpan.FromSeconds(15));
            await LinkHarness.Until(() => InputCount(parent, uuid) == 1, "first input observed").WaitAsync(TimeSpan.FromSeconds(15));
            var inputFile = Path.Combine(FakeRoot, parent + ".inputs.jsonl"); var before = File.ReadAllLines(inputFile).Length;
            var second = Prepare("pin-second", "another report");
            var refusal = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, second.Request.DeepClone()));
            Assert.AreEqual("receipt_capacity", (string?)refusal["noInputProof"]);
            Assert.AreEqual("receipt_capacity", store.ReadNoteNoInput(second.Id, parent, route.HostId!, route.Generation!.Key, second.Claim.AttemptId));
            var duplicate = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, second.Request.DeepClone()));
            Assert.AreEqual("receipt_capacity", (string?)duplicate["noInputProof"]);
            Assert.AreEqual(before, File.ReadAllLines(inputFile).Length, "capacity refusal wrote no second input");
            File.WriteAllText(Path.Combine(dir, "release-report-result"), "");
            await LinkHarness.Until(() => (string?)store.ReadNoteReceipt(first.Id, parent, route.HostId!, route.Generation!.Key,
                first.Claim.AttemptId)?["cycleResult"] == "ended", "first tracked result").WaitAsync(TimeSpan.FromSeconds(15));
            var facts = store.ReadNoteReceipt(first.Id, parent, route.HostId!, route.Generation!.Key, first.Claim.AttemptId)!;
            var revision = (long)facts["revision"]!;
            Assert.IsTrue(store.TrySettleNoteReceipt(first.Id, parent, route.HostId!, route.Generation!.Key, first.Claim.AttemptId, revision));
            Assert.IsTrue(store.NoteNeedsAck(first.Id, parent, route.HostId!, route.Generation!.Key, first.Claim.AttemptId));
            var ack = new JsonObject { ["sessionId"] = parent, ["_meta"] = new JsonObject {
                [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = first.Id, ["attemptId"] = first.Claim.AttemptId,
                    ["nativeMessageId"] = uuid, ["revision"] = revision } } };
            var acked = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNoteAck, ack.DeepClone()));
            Assert.AreEqual(true, (bool?)acked["acknowledged"]);
            Assert.IsFalse(store.NoteNeedsAck(first.Id, parent, route.HostId!, route.Generation!.Key, first.Claim.AttemptId));
            var repeated = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNoteAck, ack.DeepClone()));
            Assert.AreEqual(true, (bool?)repeated["acknowledged"]);
            var third = Prepare("pin-third", "new report after ack");
            var thirdReceipt = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, third.Request));
            var thirdUuid = (string)thirdReceipt["nativeMessageId"]!;
            await LinkHarness.Until(() => InputCount(parent, thirdUuid) == 1, "input after ACK released capacity").WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { File.WriteAllText(Path.Combine(dir, "release-report-result"), ""); }
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task HostReturnsDurableNoInputProofWhenNativeHasNotStarted()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var store = new ReportStore(Path.Combine(dir, "host-protocol-reports"));
        var hosts = new InProcessHosts(_ => adapter) { HostReportStore = store };
        await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await NewAsync(client, dir);
        var route = service.Agents["claude"].RouteOf(parent)!;
        var id = store.Reserve(new ReportIdentity("host-cold", "child", parent, ReportCause.User, "child-prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        const string text = "cold report";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        var claim = store.ClaimDelivery(id, parent, route.HostId!, route.Generation!.Key, "note", digest);
        store.StartDelivery(id, claim.AttemptId);
        var request = new JsonObject { ["sessionId"] = parent, ["text"] = text, ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId } } };
        var proof = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request.DeepClone()));
        Assert.AreEqual("native_not_running", (string?)proof["noInputProof"]);
        Assert.AreEqual("native_not_running", store.ReadNoteNoInput(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId));
        var duplicate = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request.DeepClone()));
        Assert.AreEqual("native_not_running", (string?)duplicate["noInputProof"]);
        Assert.IsFalse(File.Exists(Path.Combine(FakeRoot, parent + ".inputs.jsonl")));
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ActiveUserTurnReceivesDurableReportNoteBeforeForegroundEnds()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var grandparent = await NewAsync(client, dir);
        var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        service.Store.Update(parent, "claude", record => { record.ParentSessionId = grandparent; record.ReportToParent = true; });
        var prompt = client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = parent,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "slow hold user work" }) });
        try
        {
            await LinkHarness.Until(() => service.Feed(parent).TurnRunning, "user turn running").WaitAsync(TimeSpan.FromSeconds(15));
            var store = service.ReportStorage;
            var id = store.Reserve(new ReportIdentity("live-dispatch", "child", parent, ReportCause.User, "child-prompt")).ReportId;
            using (var capture = store.OpenCapture(id)) { capture.Append("the child result"); capture.Seal("ended"); store.Commit(id, capture); }
            store.AcknowledgeProducer(id); service.QueueReportDeliveries(parent);
            await LinkHarness.Until(() => store.GetReport(id).Delivery?.Route == "note", "live note claim").WaitAsync(TimeSpan.FromSeconds(15));
            var route = service.Agents["claude"].RouteOf(parent)!; var claim = store.GetReport(id).Delivery!;
            string? uuid = null;
            await LinkHarness.Until(() =>
            {
                uuid = (string?)store.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["nativeMessageId"];
                return uuid is not null && InputCount(parent, uuid) == 1;
            }, "native report input observed during user turn").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsFalse(prompt.IsCompleted, "native input did not wait for the active user turn");
            await LinkHarness.Until(() => store.GetReport(id).Status == "completed", "matching native note cycle result").WaitAsync(TimeSpan.FromSeconds(15));
            await LinkHarness.Until(() => !store.NoteNeedsAck(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId),
                "durable receipt ACK").WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { await prompt; }
        await LinkHarness.Until(() => service.ReportStorage.Inventory(null, 100).Items.Any(r => r.Identity.Child == parent &&
            r.Identity.Parent == grandparent && r.Identity.Cause == ReportCause.User && r.Status != "reserved"),
            "original user work remains eligible for its parent report").WaitAsync(TimeSpan.FromSeconds(15));
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task RepeatedTrackedNotesReleaseSmallLedgerAfterDurableAck()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false, reportNoteLimit: 1, reportNoteGlobalLimit: 2));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var route = service.Agents["claude"].RouteOf(parent)!; var store = service.ReportStorage;
        for (var i = 0; i < 5; i++)
        {
            var id = store.Reserve(new ReportIdentity("sequence-" + i, "child", parent, ReportCause.User, "prompt-" + i)).ReportId;
            using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
            store.AcknowledgeProducer(id); var text = "sequential report " + i;
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            var claim = store.ClaimDelivery(id, parent, route.HostId!, route.Generation!.Key, "note", digest);
            store.StartDelivery(id, claim.AttemptId);
            var request = new JsonObject { ["sessionId"] = parent, ["text"] = text, ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId } } };
            var receipt = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request));
            var uuid = (string)receipt["nativeMessageId"]!;
            await LinkHarness.Until(() => store.GetReport(id).Status == "completed" &&
                !store.NoteNeedsAck(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId), "receipt settled and ACKed").WaitAsync(TimeSpan.FromSeconds(15));
            await LinkHarness.Until(() => InputCount(parent, uuid) == 1, "native input once").WaitAsync(TimeSpan.FromSeconds(15));
        }
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ColdClaudeFallsBackToOneQueuedTurnOnlyAfterDurableNoInputProof()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var parent = await NewAsync(client, dir);
        var store = service.ReportStorage;
        var id = store.Reserve(new ReportIdentity("cold-fallback", "child", parent, ReportCause.User, "source-prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("cold result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id); service.QueueReportDeliveries(parent);
        await LinkHarness.Until(() => store.GetReport(id).Status == "completed", "queued fallback completed").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual("queued", store.GetReport(id).Delivery?.Route);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "reports", "reports.json")))!;
        var entry = manifest["entries"]!.AsArray().Single(x => (string?)x?["id"] == id)!;
        Assert.AreEqual("native_not_running", (string?)entry["delivery"]?["previousNoteProof"]?["reason"]);
        var input = Path.Combine(FakeRoot, parent + ".inputs.jsonl");
        await LinkHarness.Until(() => File.Exists(input) && File.ReadAllLines(input).Length == 1, "one native queued input").WaitAsync(TimeSpan.FromSeconds(15));
        StringAssert.Contains(File.ReadAllText(input), id);
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AckPublicationFaultRecoversAfterConductorRestartWithoutSecondInput()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); var first = hosts.Conductor(dir, adapter); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first); Conductor? next = null; var disposed = false;
        try
        {
            var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
            var route = first.Agents["claude"].RouteOf(parent)!; var store = first.ReportStorage;
            var id = store.Reserve(new ReportIdentity("ack-restart", "child", parent, ReportCause.User, "source")).ReportId;
            using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
            store.AcknowledgeProducer(id); const string text = "restart ack note";
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            var claim = store.ClaimDelivery(id, parent, route.HostId!, route.Generation!.Key, "note", digest);
            store.StartDelivery(id, claim.AttemptId);
            store.WriteManifest = (path, json) =>
            {
                if (json.Contains("\"acknowledgedReceiptRevision\":", StringComparison.Ordinal)) throw new IOException("ACK publication fault");
                AtomicFile.WriteAllText(path, json);
            };
            var request = new JsonObject { ["sessionId"] = parent, ["text"] = text, ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId } } };
            var response = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request));
            var uuid = (string)response["nativeMessageId"]!;
            await LinkHarness.Until(() => store.GetReport(id).Status == "completed" && store.NoteNeedsAck(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId) &&
                ((string?)first.Status()["_meta"]?["gorynych/reportRecovery"]?["lastFailure"]) is not null,
                "settlement retained while ACK publication fails").WaitAsync(TimeSpan.FromSeconds(15));
            store.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
            await HostTests.RestartAsync(first); hosts.Service = null; await first.DisposeAsync(); await client.DisposeAsync(); disposed = true; hosts.BreakAll();
            next = hosts.Conductor(dir, adapter); hosts.Service = next;
            await LinkHarness.Until(() => next.Agents["claude"].RouteOf(parent) is not null, "host reattached").WaitAsync(TimeSpan.FromSeconds(15));
            await LinkHarness.Until(() => !next.ReportStorage.NoteNeedsAck(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId),
                "ACK recovered").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual("completed", next.ReportStorage.GetReport(id).Status);
            Assert.AreEqual(1, InputCount(parent, uuid));
        }
        finally
        {
            first.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
            if (next is not null) await next.DisposeAsync();
            if (!disposed) { await client.DisposeAsync(); await first.DisposeAsync(); }
            hosts.BreakAll();
        }
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task IdleLiveReportOnlyCycleDoesNotProduceReportToGrandparent()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var grandparent = await NewAsync(client, dir);
        var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        service.Store.Update(parent, "claude", record => { record.ParentSessionId = grandparent; record.ReportToParent = true; });
        var store = service.ReportStorage;
        var id = store.Reserve(new ReportIdentity("note-only", "child", parent, ReportCause.User, "source")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id); service.QueueReportDeliveries(parent);
        await LinkHarness.Until(() => store.GetReport(id).Status == "completed" && !service.Feed(parent).TurnRunning,
            "report-only live cycle finished").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual("note", store.GetReport(id).Delivery?.Route);
        Assert.IsFalse(store.Inventory(null, 100).Items.Any(r => r.Identity.Child == parent && r.Identity.Parent == grandparent),
            "report-only native cycle must not create another report");
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task LiveDispatchReconcilesAfterConductorDisconnectWithoutResend()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter) { BufferBytes = 256 }; var first = hosts.Conductor(dir, adapter); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first); Conductor? next = null; var disposed = false; Task<JsonNode?>? prompt = null;
        try
        {
            var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
            first.Store.Update(parent, "claude", record => record.Draft = false);
            prompt = client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = parent,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "slow hold user work" }) });
            await LinkHarness.Until(() => first.Feed(parent).TurnRunning, "user turn running").WaitAsync(TimeSpan.FromSeconds(15));
            var store = first.ReportStorage;
            var id = store.Reserve(new ReportIdentity("routing-restart", "child", parent, ReportCause.User, "source")).ReportId;
            using (var capture = store.OpenCapture(id)) { capture.Append("receipt-hold-before-echo"); capture.Seal("ended"); store.Commit(id, capture); }
            store.AcknowledgeProducer(id); first.QueueReportDeliveries(parent);
            await LinkHarness.Until(() => store.GetReport(id).Delivery is { Route: "note", DispatchStarted: true }, "note dispatched").WaitAsync(TimeSpan.FromSeconds(15));
            var route = first.Agents["claude"].RouteOf(parent)!; var claim = store.GetReport(id).Delivery!; string? uuid = null;
            await LinkHarness.Until(() =>
            {
                try { uuid = (string?)store.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["nativeMessageId"]; }
                catch (InvalidOperationException) { return false; }
                return uuid is not null && InputCount(parent, uuid) == 1;
            }, "native input accepted once").WaitAsync(TimeSpan.FromSeconds(15));
            var replay = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReplay,
                new JsonObject { ["after"] = new JsonObject { [parent] = 0 } });
            Assert.IsTrue((replay?["trimmed"] as JsonArray)!.Any(x => (string?)x == parent), "bounded host replay was evicted");
            await HostTests.RestartAsync(first); hosts.Service = null; await first.DisposeAsync(); await client.DisposeAsync(); disposed = true; hosts.BreakAll();
            File.WriteAllText(Path.Combine(dir, "release-report-echo"), "");
            await LinkHarness.Until(() => (string?)store.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["cycleResult"] == "ended",
                "host persisted result without conductor").WaitAsync(TimeSpan.FromSeconds(15));
            next = hosts.Conductor(dir, adapter); hosts.Service = next;
            await LinkHarness.Until(() => next.Agents["claude"].RouteOf(parent) is not null, "host reattached").WaitAsync(TimeSpan.FromSeconds(15));
            await LinkHarness.Until(() => next.ReportStorage.GetReport(id).Status == "completed" &&
                !next.ReportStorage.NoteNeedsAck(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId),
                "receipt settled and ACKed after restart").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, InputCount(parent, uuid!));
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "release-report-echo"), "");
            if (next is not null) await next.DisposeAsync();
            if (!disposed) { await client.DisposeAsync(); await first.DisposeAsync(); }
            hosts.BreakAll();
            if (prompt is not null) try { await prompt.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException or TimeoutException) { }
        }
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow("claim")]
    [DataRow("start")]
    public async Task LiveAdmissionPersistenceFaultCannotDispatchNativeInput(string boundary)
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var input = Path.Combine(FakeRoot, parent + ".inputs.jsonl"); var before = File.ReadAllLines(input).Length;
        var store = service.ReportStorage;
        var id = store.Reserve(new ReportIdentity("live-fault-" + boundary, "child", parent, ReportCause.User, "source")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("fault result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        store.WriteManifest = (path, json) =>
        {
            if (json.Contains("\"route\":\"note\"", StringComparison.Ordinal) &&
                (boundary == "claim" || json.Contains("\"dispatchStarted\":true", StringComparison.Ordinal))) throw new IOException("live " + boundary + " fault");
            AtomicFile.WriteAllText(path, json);
        };
        try
        {
            service.QueueReportDeliveries(parent);
            await LinkHarness.Until(() => store.GetReport(id).Status == "pending" && store.GetReport(id).DispositionReason is not null,
                "unstarted live fault visible").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(before, File.ReadAllLines(input).Length, "persistence fault preceded native note input");
        }
        finally { store.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json); }
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReceiptWriteFaultCanHealOnExplicitStatusWithoutNativeReplay()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var route = service.Agents["claude"].RouteOf(parent)!; var store = service.ReportStorage;
        var id = store.Reserve(new ReportIdentity("receipt-heal", "child", parent, ReportCause.User, "source")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id); const string text = "receipt-hold-after-echo";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        var claim = store.ClaimDelivery(id, parent, route.HostId!, route.Generation!.Key, "note", digest); store.StartDelivery(id, claim.AttemptId);
        var request = new JsonObject { ["sessionId"] = parent, ["text"] = text, ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId } } };
        var response = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, request)); var uuid = (string)response["nativeMessageId"]!;
        await LinkHarness.Until(() => (bool?)store.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["taken"] == true,
            "native echo saved").WaitAsync(TimeSpan.FromSeconds(15));
        var receiptFaults = 0;
        store.WriteManifest = (path, json) => { if (json.Contains("\"cycleResult\":\"ended\"", StringComparison.Ordinal)) { Interlocked.Increment(ref receiptFaults); throw new IOException("result receipt fault"); } AtomicFile.WriteAllText(path, json); };
        try
        {
            File.WriteAllText(Path.Combine(dir, "release-report-result"), "");
            await LinkHarness.Until(() => Volatile.Read(ref receiptFaults) > 0 &&
                (string?)service.Status()["_meta"]?["gorynych/reportRecovery"]?["lastFailure"] is not null,
                "injected receipt fault and visible conductor health").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsNull(store.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)?["cycleResult"]);
        }
        finally { store.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json); File.WriteAllText(Path.Combine(dir, "release-report-result"), ""); }
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject());
        await LinkHarness.Until(() => store.GetReport(id).Status == "completed" && !store.NoteNeedsAck(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId),
            "status-triggered receipt and ACK recovery").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(1, InputCount(parent, uuid));
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AmbiguousLiveWriteIsNeverResentAndLateEvidenceCanSettle()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false,
            afterReportNoteWrite: () => throw new IOException("lost write confirmation")));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var store = service.ReportStorage; var id = store.Reserve(new ReportIdentity("uncertain-live", "child", parent, ReportCause.User, "source")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("receipt-hold-before-echo"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        try
        {
            service.QueueReportDeliveries(parent);
            await LinkHarness.Until(() => store.GetReport(id).Status == "uncertain", "uncertain live write").WaitAsync(TimeSpan.FromSeconds(15));
            var route = service.Agents["claude"].RouteOf(parent)!; var claim = store.GetReport(id).Delivery!;
            var facts = store.ReadNoteReceipt(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId)!;
            var uuid = (string)facts["nativeMessageId"]!;
            await LinkHarness.Until(() => InputCount(parent, uuid) == 1, "uncertain write observed by native").WaitAsync(TimeSpan.FromSeconds(15));
            for (var i = 0; i < 3; i++) { service.QueueReportDeliveries(parent); await client.Rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject()); }
            Assert.AreEqual(1, InputCount(parent, uuid));
            File.WriteAllText(Path.Combine(dir, "release-report-echo"), "");
            await LinkHarness.Until(() => store.GetReport(id).Status == "completed" &&
                !store.NoteNeedsAck(id, parent, route.HostId!, route.Generation!.Key, claim.AttemptId), "late evidence settled and ACKed").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, InputCount(parent, uuid));
        }
        finally { File.WriteAllText(Path.Combine(dir, "release-report-echo"), ""); }
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task LiveDeliveryRechecksReadOnlyAfterForwardGateWait()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var input = Path.Combine(FakeRoot, parent + ".inputs.jsonl"); var before = File.ReadAllLines(input).Length;
        var store = service.ReportStorage; var id = store.Reserve(new ReportIdentity("live-mode", "child", parent, ReportCause.User, "source")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("mode result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id); var feed = service.Feed(parent); await feed.ForwardGate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15)); var held = true;
        var reachedForward = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeLiveReportForwardWait = () => reachedForward.TrySetResult();
        try
        {
            service.QueueReportDeliveries(parent);
            await reachedForward.Task.WaitAsync(TimeSpan.FromSeconds(15));
            feed.SetReadOnly(new SessionFeed.ReadOnlyState("changed while waiting", null));
            feed.ForwardGate.Release(); held = false;
            await LinkHarness.Until(() => store.GetReport(id).Status == "pending" && store.GetReport(id).DispositionReason?.Contains("read-only") == true,
                "read-only refusal after wait").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(before, File.ReadAllLines(input).Length, "no native input after mode changed");
            feed.SetReadOnly(null); service.QueueReportDeliveries(parent);
            await LinkHarness.Until(() => store.GetReport(id).Status == "completed", "delivery after writability returns").WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { if (held) feed.ForwardGate.Release(); }
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ArchivedLiveParentIsNotWokenAndRestoreDeliversOnce()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var input = Path.Combine(FakeRoot, parent + ".inputs.jsonl"); var before = File.ReadAllLines(input).Length;
        service.Store.Update(parent, "claude", record => record.Archived = true);
        var store = service.ReportStorage; var id = store.Reserve(new ReportIdentity("live-archive", "child", parent, ReportCause.User, "source")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("archive result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id); service.QueueReportDeliveries(parent);
        await LinkHarness.Until(() => store.GetReport(id).DispositionReason?.Contains("archived") == true, "archive blocks delivery").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(before, File.ReadAllLines(input).Length);
        service.Store.Update(parent, "claude", record => record.Archived = false); service.QueueReportDeliveries(parent);
        await LinkHarness.Until(() => store.GetReport(id).Status == "completed", "restored recipient receives report").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual("note", store.GetReport(id).Delivery?.Route);
        Assert.AreEqual(before + 1, File.ReadAllLines(input).Length);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task DeletedLiveParentFailsWithoutInputAndChildRetainsResult()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var input = Path.Combine(FakeRoot, parent + ".inputs.jsonl"); var before = File.ReadAllLines(input).Length;
        service.Store.Update(parent, "claude", record => record.Deleted = true);
        var store = service.ReportStorage; var id = store.Reserve(new ReportIdentity("live-deleted", "child", parent, ReportCause.User, "source")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("retained child result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id); service.QueueReportDeliveries(parent);
        await LinkHarness.Until(() => store.GetReport(id).Status == "failed", "deleted parent failed").WaitAsync(TimeSpan.FromSeconds(15));
        StringAssert.Contains(store.GetReport(id).DispositionReason, "deleted");
        Assert.AreEqual(before, File.ReadAllLines(input).Length); Assert.AreEqual("retained child result", store.Read(id, "child").Text);
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LiveDeliveryRechecksArchiveOrDeletionAfterForwardWait(bool deleted)
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service); var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
        var input = Path.Combine(FakeRoot, parent + ".inputs.jsonl"); var before = File.ReadAllLines(input).Length;
        var store = service.ReportStorage; var id = store.Reserve(new ReportIdentity("after-wait-" + deleted, "child", parent, ReportCause.User, "source")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id); var feed = service.Feed(parent); await feed.ForwardGate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15)); var held = true;
        var reachedForward = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeLiveReportForwardWait = () => reachedForward.TrySetResult();
        try
        {
            service.QueueReportDeliveries(parent);
            await reachedForward.Task.WaitAsync(TimeSpan.FromSeconds(15));
            service.Store.Update(parent, "claude", record => { if (deleted) record.Deleted = true; else record.Archived = true; });
            feed.ForwardGate.Release(); held = false;
            await LinkHarness.Until(() => deleted ? store.GetReport(id).Status == "failed" :
                store.GetReport(id).Status == "pending" && store.GetReport(id).DispositionReason?.Contains("archived") == true,
                "changed recipient state handled").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(before, File.ReadAllLines(input).Length, "changed recipient state prevented native input");
        }
        finally { if (held) feed.ForwardGate.Release(); }
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task BusyLiveRecipientDoesNotConsumeAllGlobalDeliverySlots()
    {
        var dir = TestDirs.New(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var writes = 0;
        var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false, afterReportNoteWrite: () =>
        {
            if (Interlocked.Increment(ref writes) != 1) return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        }));
        var hosts = new InProcessHosts(_ => adapter); await using var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var a = await NewAsync(client, dir); var b = await NewAsync(client, dir); await PromptAsync(client, a, "warmup"); await PromptAsync(client, b, "warmup");
        var store = service.ReportStorage;
        string Publish(string key, string parent)
        {
            var id = store.Reserve(new ReportIdentity(key, "child", parent, ReportCause.User, "source")).ReportId;
            using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
            store.AcknowledgeProducer(id); return id;
        }
        var first = Publish("busy-live-0", a);
        try
        {
            service.QueueReportDeliveries(a); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            for (var i = 1; i <= 8; i++) Publish("busy-live-" + i, a);
            var ready = Publish("ready-live", b); service.QueueReportDeliveries();
            await LinkHarness.Until(() => store.GetReport(ready).Status == "completed", "other parent received live note").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsFalse(release.Task.IsCompleted, "other parent finished while first recipient was still blocked");
            Assert.AreEqual("note", store.GetReport(ready).Delivery?.Route);
        }
        finally { release.TrySetResult(); }
    }


    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AckWriteFaultThenRetiredEvictionRecoversLedgerReleaseAfterReattach()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, liveNotes: true, fetchCatalog: false, reportNoteLimit: 1, reportNoteGlobalLimit: 1));
        var hosts = new InProcessHosts(_ => adapter); var first = hosts.Conductor(dir, adapter); hosts.Service = first;
        var client = await TestClient.ConnectAsync(first); Conductor? next = null; var disposed = false;
        try
        {
            var parent = await NewAsync(client, dir); await PromptAsync(client, parent, "warmup");
            first.Store.Update(parent, "claude", record => record.Draft = false);
            var route = first.Agents["claude"].RouteOf(parent)!; var store = first.ReportStorage;
            (string Id, ReportDeliveryClaim Claim, JsonObject Request) Prepare(int index)
            {
                var id = store.Reserve(new ReportIdentity("ack-evict-" + index, "child", parent, ReportCause.User, "prompt-" + index)).ReportId;
                using (var capture = store.OpenCapture(id)) { capture.Append("immutable " + index); capture.Seal("ended"); store.Commit(id, capture); }
                store.AcknowledgeProducer(id); var text = "ack eviction note " + index;
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
                var claim = store.ClaimDelivery(id, parent, route.HostId!, route.Generation!.Key, "note", digest); store.StartDelivery(id, claim.AttemptId);
                return (id, claim, new JsonObject { ["sessionId"] = parent, ["text"] = text, ["_meta"] = new JsonObject
                    { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId } } });
            }
            var original = Prepare(0); var faultHits = 0;
            store.WriteManifest = (path, json) =>
            {
                var root = JsonNode.Parse(json)!;
                var item = root["entries"]!.AsArray().FirstOrDefault(x => (string?)x?["id"] == original.Id);
                if (item?["delivery"]?["acknowledgedReceiptRevision"] is not null || item?["delivery"]?["ledgerAbsentReleasedRevision"] is not null)
                { Interlocked.Increment(ref faultHits); throw new IOException("original ACK publication fault"); }
                AtomicFile.WriteAllText(path, json);
            };
            var one = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, original.Request));
            var uuid = (string)one["nativeMessageId"]!;
            await LinkHarness.Until(() => Volatile.Read(ref faultHits) > 0 && store.GetReport(original.Id).Status == "completed" &&
                store.NoteNeedsAck(original.Id, parent, route.HostId!, route.Generation!.Key, original.Claim.AttemptId), "first native ACK publication failed").WaitAsync(TimeSpan.FromSeconds(15));
            var uuids = new List<string> { uuid };
            for (var i = 1; i <= 2; i++)
            {
                var later = Prepare(i); var result = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNote, later.Request));
                uuids.Add((string)result["nativeMessageId"]!);
                await LinkHarness.Until(() => store.GetReport(later.Id).Status == "completed" &&
                    !store.NoteNeedsAck(later.Id, parent, route.HostId!, route.Generation!.Key, later.Claim.AttemptId), "later ACK completed").WaitAsync(TimeSpan.FromSeconds(15));
            }
            var query = new JsonObject { ["sessionId"] = parent, ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] =
                new JsonObject { ["reportId"] = original.Id, ["attemptId"] = original.Claim.AttemptId } } };
            var missing = Receipt(await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNoteStatus, query));
            Assert.AreEqual("unknown", (string?)missing["state"], "bounded retired receipt was evicted");
            Assert.IsTrue(store.NoteNeedsAck(original.Id, parent, route.HostId!, route.Generation!.Key, original.Claim.AttemptId),
                "ordinary unknown status is not ledger-release evidence");
            store.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
            await HostTests.RestartAsync(first); hosts.Service = null; await first.DisposeAsync(); await client.DisposeAsync(); disposed = true; hosts.BreakAll();
            next = hosts.Conductor(dir, adapter); hosts.Service = next;
            await LinkHarness.Until(() => next.Agents["claude"].RouteOf(parent) is not null, "host reattached").WaitAsync(TimeSpan.FromSeconds(15));
            await LinkHarness.Until(() => !next.ReportStorage.NoteNeedsAck(original.Id, parent, route.HostId!, route.Generation!.Key, original.Claim.AttemptId),
                "ledger absence released old ACK hold").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual("ledger_not_retained", next.ReportStorage.GetReport(original.Id).Delivery?.ReceiptReleaseReason);
            Assert.AreEqual("completed", next.ReportStorage.GetReport(original.Id).Status); Assert.AreEqual("immutable 0", next.ReportStorage.Read(original.Id, "child").Text);
            foreach (var item in uuids) Assert.AreEqual(1, InputCount(parent, item));
        }
        finally
        {
            first.ReportStorage.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
            if (next is not null) await next.DisposeAsync();
            if (!disposed) { await client.DisposeAsync(); await first.DisposeAsync(); }
            hosts.BreakAll();
        }
    }

}
