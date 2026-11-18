using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

[TestClass]
public sealed class HostReportCaptureTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    sealed class Harness : IAsyncDisposable
    {
        public readonly JsonRpcConnection Agent;
        public readonly JsonRpcConnection Conductor;
        public readonly AdapterHost Host;
        public readonly Task<int> Run;
        public readonly ConcurrentQueue<JsonObject> Prompts = new();
        public readonly ConcurrentQueue<JsonObject> Ends = new();
        public readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> Responses = new();
        public readonly ConcurrentQueue<string> HistoryCalls = new();
        public readonly TaskCompletionSource HistoryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldHistory;
        public Harness(ReportStore? store, Action<ReportCapture>? opened = null, int buffer = 2)
        {
            var wire = new DuplexLink();
            Agent = new JsonRpcConnection(wire.AgentEnd.Input, wire.AgentEnd.Output, "capture-agent");
            Agent.RequestHandler = async (req, _) =>
            {
                if (req.Method == AcpMethods.Agent.Initialize) return new JsonObject { ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject() };
                if (req.Method is AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionResume or GorynychProtocol.MethodCatchUp)
                {
                    var sid = (string)req.Params!["sessionId"]!;
                    HistoryCalls.Enqueue(sid);
                    await Update(sid, "agent_message_chunk", "OLD HISTORY");
                    await Update(sid, "tool_call");
                    if (HoldHistory) await HistoryRelease.Task;
                    return new JsonObject();
                }
                if (req.Method == AcpMethods.Agent.SessionPrompt)
                {
                    var p = req.Params!.DeepClone().AsObject(); Prompts.Enqueue(p);
                    var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Responses[(string)p["sessionId"]!] = completion;
                    return await completion.Task;
                }
                return new JsonObject();
            };
            Agent.Start();
            var instance = new AgentInstance(wire.ClientEnd.Input, wire.ClientEnd.Output, Agent.Completion, () => Agent.DisposeAsync());
            var link = new DuplexLink();
            Conductor = new JsonRpcConnection(link.AgentEnd.Input, link.AgentEnd.Output, "capture-conductor");
            Conductor.NotificationHandler = (method, p) => { if (method == GorynychProtocol.MethodTurnEnded) Ends.Enqueue(p!.DeepClone().AsObject()); return Task.CompletedTask; };
            Conductor.Start();
            Host = new AdapterHost(instance, _ => Task.FromResult<(Stream, Stream)>((link.ClientEnd.Input, link.ClientEnd.Output)),
                new AdapterHostOptions { Adapter = "fake", Token = "test", ReportStore = store, CaptureOpened = opened, BufferLimit = buffer });
            Run = Host.RunAsync();
        }
        public async Task<JsonNode?> Initialize()
        {
            var result = await Conductor.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 }).WaitAsync(Wait);
            await Conductor.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject() }).WaitAsync(Wait);
            return result;
        }
        public Task<JsonNode?> Prompt(string session, string prompt, string? report) => Conductor.SendRequestAsync(AcpMethods.Agent.SessionPrompt,
            new JsonObject { ["sessionId"] = session, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "go" }),
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = prompt, [GorynychProtocol.MetaReportId] = report } });
        public Task Update(string session, string kind, string? text = null) => Agent.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
            new JsonObject { ["sessionId"] = session, ["update"] = kind == "tool_call" ?
                new JsonObject { ["sessionUpdate"] = kind, ["toolCallId"] = "t", ["title"] = "tool" } :
                new JsonObject { ["sessionUpdate"] = kind, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } } });
        public async ValueTask DisposeAsync()
        {
            await Conductor.DisposeAsync(); await Agent.DisposeAsync(); await Run.WaitAsync(Wait);
        }
    }
    static ReportIdentity Identity(string child, string prompt) => new(prompt, child, "parent", ReportCause.User, prompt);
    static Task Until(Func<bool> condition, string why) => LinkHarness.Until(condition, why).WaitAsync(Wait);

    [TestMethod]
    public async Task LiveCaptureSurvivesReplayEvictionAndCanBeImported()
    {
        var store = new ReportStore(TestDirs.New(), bodyLimit: 100000, totalLimit: 2000000);
        var id = store.Reserve(Identity("s", "p")).ReportId;
        await using var h = new Harness(store);
        await h.Initialize();
        var pending = h.Prompt("s", "p", id);
        await Until(() => h.Responses.ContainsKey("s"), "native prompt");
        Assert.IsNull(h.Prompts.Single()["_meta"]?[GorynychProtocol.MetaReportId]);
        await h.Update("s", "agent_message_chunk", "discard");
        await h.Update("s", "tool_call");
        var final = new string('x', 40000) + "😀end";
        await h.Update("s", "agent_message_chunk", new string('x', 40000));
        await h.Update("s", "agent_message_chunk", "😀");
        await h.Update("s", "agent_message_chunk", "end");
        h.Responses["s"].SetResult(new JsonObject { ["stopReason"] = "end_turn" });
        await pending.WaitAsync(Wait);
        await Until(() => !h.Ends.IsEmpty, "terminal");
        Assert.AreEqual(id, (string?)h.Ends.Single()["_meta"]?[GorynychProtocol.MetaReportId]);
        var before = (long)h.Host.Report()["seq"]!;
        for (var n = 0; n < 4; n++) await h.Update("other", "agent_message_chunk", "unrelated " + n);
        await Until(() => (long)h.Host.Report()["seq"]! >= before + 4, "terminal evicted");
        h.Ends.Clear();
        var replay = await h.Conductor.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject { ["s"] = 0, ["other"] = 0 } }).WaitAsync(Wait);
        Assert.IsTrue((replay?["trimmed"] as JsonArray)!.Any(x => (string?)x == "s"), "the host replay evicted the terminal");
        Assert.IsTrue(h.Ends.IsEmpty, "completed terminal is no longer in replay");
        using (var capture = store.OpenCapture(id))
        {
            Assert.AreEqual(final, capture.Snapshot().Text); Assert.AreEqual("ended", capture.Snapshot().Outcome);
            Assert.AreEqual("pending", store.Commit(id, capture).Status);
        }
        store.AcknowledgeProducer(id);
        Assert.AreEqual(final.Length, store.Read(id, "parent", limit: 16384).TotalBytes - 2); // UTF-8 emoji adds two bytes
    }

    [TestMethod]
    public async Task RejectsForeignReservationBeforeDispatchAndLegacyPromptWorks()
    {
        var store = new ReportStore(TestDirs.New()); var id = store.Reserve(Identity("owner", "p")).ReportId;
        await using var h = new Harness(store); await h.Initialize();
        await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await h.Prompt("other", "p", id).WaitAsync(Wait));
        Assert.IsTrue(h.Prompts.IsEmpty);
        var prompt = h.Prompt("other", "legacy", null);
        await Until(() => h.Responses.ContainsKey("other"), "legacy prompt");
        h.Responses["other"].SetResult(new JsonObject { ["stopReason"] = "cancelled" });
        await prompt.WaitAsync(Wait);
    }
    [TestMethod]
    public async Task SealCompletesBeforeTerminalAndResponseAndSeparateSessionsDoNotMix()
    {
        var store = new ReportStore(TestDirs.New(), bodyLimit: 4096, totalLimit: 1000000);
        var one = store.Reserve(Identity("one", "p1")).ReportId;
        var two = store.Reserve(Identity("two", "p2")).ReportId;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var opened = 0;
        await using var h = new Harness(store, c =>
        {
            if (Interlocked.Increment(ref opened) != 1) return;
            var original = c.WriteFrame;
            c.WriteFrame = frame => { if (frame[0] == 3) { entered.Set(); if (!release.Wait(Wait)) throw new TimeoutException(); } original(frame); };
        });
        await h.Initialize();
        var p1 = h.Prompt("one", "p1", one);
        await Until(() => h.Responses.ContainsKey("one"), "first native prompt");
        var p2 = h.Prompt("two", "p2", two);
        await Until(() => h.Responses.Count == 2, "both native prompts");
        var running = h.Host.Report()["turns"]!.AsArray();
        Assert.IsTrue(running.Any(t => (string?)t?["_meta"]?[GorynychProtocol.MetaReportId] == one));
        Assert.IsTrue(running.Any(t => (string?)t?["_meta"]?[GorynychProtocol.MetaReportId] == two));
        await h.Update("one", "agent_message_chunk", "first");
        await h.Update("two", "agent_message_chunk", "second");
        h.Responses["one"].SetResult(new JsonObject { ["stopReason"] = "end_turn" });
        try
        {
            Assert.IsTrue(await Task.Run(() => entered.Wait(Wait)));
            Assert.IsFalse(p1.IsCompleted); Assert.IsTrue(h.Ends.IsEmpty);
        }
        finally { release.Set(); }
        await p1.WaitAsync(Wait);
        h.Responses["two"].SetResult(new JsonObject { ["stopReason"] = "cancelled" });
        await p2.WaitAsync(Wait);
        Assert.AreEqual(2, h.Prompts.Count);
        using var a = store.OpenCapture(one); using var b = store.OpenCapture(two);
        Assert.AreEqual("first", a.Snapshot().Text); Assert.AreEqual("second", b.Snapshot().Text);
        Assert.AreEqual("cancelled", b.Snapshot().Outcome);
    }

    [TestMethod]
    public async Task SealIoFailureIsReportedAndHostContinuesWithoutDuplicateDispatch()
    {
        var store = new ReportStore(TestDirs.New()); var id = store.Reserve(Identity("s", "p")).ReportId;
        await using var h = new Harness(store, c =>
        {
            var original = c.WriteFrame;
            c.WriteFrame = frame => { if (frame[0] == 3) throw new IOException("seal fault"); original(frame); };
        });
        await h.Initialize(); var pending = h.Prompt("s", "p", id);
        await Until(() => h.Responses.ContainsKey("s"), "native prompt");
        await h.Update("s", "agent_message_chunk", "text");
        h.Responses["s"].SetResult(new JsonObject { ["stopReason"] = "end_turn" });
        var response = await pending.WaitAsync(Wait);
        StringAssert.Contains((string?)response?["_meta"]?[GorynychProtocol.MetaReportCapture]?["failure"] ?? "", "seal fault");
        Assert.AreEqual(1, h.Prompts.Count);
        using (var capture = store.OpenCapture(id)) Assert.IsFalse(capture.Snapshot().Sealed);
        var next = h.Prompt("other", "legacy", null);
        await Until(() => h.Responses.ContainsKey("other"), "next native prompt");
        h.Responses["other"].SetResult(new JsonObject { ["stopReason"] = "end_turn" });
        await next.WaitAsync(Wait);
    }

    [TestMethod]
    public async Task AppendIoFailureIsVisibleAndDoesNotCancelNativeTurn()
    {
        var store = new ReportStore(TestDirs.New()); var id = store.Reserve(Identity("s", "p")).ReportId;
        await using var h = new Harness(store, c =>
        {
            var original = c.WriteFrame;
            c.WriteFrame = frame => { if (frame[0] == 1) throw new IOException("append fault"); original(frame); };
        });
        await h.Initialize(); var prompt = h.Prompt("s", "p", id);
        await Until(() => h.Responses.ContainsKey("s"), "native prompt");
        await h.Update("s", "agent_message_chunk", "not durable");
        h.Responses["s"].SetResult(new JsonObject { ["stopReason"] = "end_turn" });
        var response = await prompt.WaitAsync(Wait);
        Assert.IsNotNull(response?["_meta"]?[GorynychProtocol.MetaReportCapture]?["failure"]);
        using var recovered = store.OpenCapture(id);
        Assert.IsFalse(recovered.Snapshot().Sealed);
        Assert.AreEqual(1, h.Prompts.Count);
    }

    [TestMethod]
    public async Task HostWithoutConfiguredStoreDoesNotAdvertiseCaptureAndRejectsReportId()
    {
        await using var h = new Harness(null);
        var init = await h.Initialize();
        Assert.IsNull(init?["_meta"]?[GorynychProtocol.MetaReportCapture]);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await h.Prompt("s", "p", Guid.NewGuid().ToString("N")).WaitAsync(Wait));
        Assert.IsTrue(h.Prompts.IsEmpty);
    }

    [TestMethod]
    public async Task NativeErrorSealsFailedWithBoundedExplicitlyTruncatedError()
    {
        var store = new ReportStore(TestDirs.New()); var id = store.Reserve(Identity("s", "p")).ReportId;
        await using var h = new Harness(store); await h.Initialize();
        var prompt = h.Prompt("s", "p", id);
        await Until(() => h.Responses.ContainsKey("s"), "native prompt");
        await h.Update("s", "agent_message_chunk", "partial");
        h.Responses["s"].SetException(new JsonRpcException(JsonRpcException.InternalError, new string('z', 2000)));
        await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await prompt.WaitAsync(Wait));
        using var capture = store.OpenCapture(id);
        var snapshot = capture.Snapshot();
        Assert.IsTrue(snapshot.Sealed); Assert.AreEqual("failed", snapshot.Outcome);
        Assert.AreEqual("partial", snapshot.Text);
        StringAssert.EndsWith(snapshot.Error!, " [truncated]");
        Assert.IsLessThanOrEqualTo(ReportCapture.ErrorBytes, System.Text.Encoding.UTF8.GetByteCount(snapshot.Error!));
    }

    [TestMethod]
    public async Task ExistingUnsealedCaptureIsNotRedispatched()
    {
        var store = new ReportStore(TestDirs.New()); var id = store.Reserve(Identity("s", "p")).ReportId;
        using (var capture = store.OpenCapture(id)) capture.Append("partial");
        await using var h = new Harness(store); await h.Initialize();
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await h.Prompt("s", "p", id).WaitAsync(Wait));
        StringAssert.Contains(error.Message, "reconcile");
        Assert.IsTrue(h.Prompts.IsEmpty);
        using var recovered = store.OpenCapture(id); Assert.AreEqual("partial", recovered.Snapshot().Text);
    }

    [TestMethod]
    [DataRow(AcpMethods.Agent.SessionLoad)]
    [DataRow(AcpMethods.Agent.SessionResume)]
    [DataRow(GorynychProtocol.MethodCatchUp)]
    public async Task HistoryAndCapturedTurnCannotOverlapEitherWay(string method)
    {
        var store = new ReportStore(TestDirs.New()); var id = store.Reserve(Identity("s", "p")).ReportId;
        await using var h = new Harness(store); await h.Initialize();
        var prompt = h.Prompt("s", "p", id);
        await Until(() => h.Responses.ContainsKey("s"), "captured prompt");
        await h.Update("s", "agent_message_chunk", "live answer");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await h.Conductor.SendRequestAsync(method, new JsonObject { ["sessionId"] = "s" }).WaitAsync(Wait));
        Assert.IsTrue(h.HistoryCalls.IsEmpty, "history text and tool never reach native adapter");
        await h.Conductor.SendRequestAsync(method, new JsonObject { ["sessionId"] = "other" }).WaitAsync(Wait);
        h.Responses["s"].SetResult(new JsonObject { ["stopReason"] = "end_turn" });
        await prompt.WaitAsync(Wait);
        using (var result = store.OpenCapture(id)) { Assert.AreEqual("live answer", result.Snapshot().Text); Assert.IsNull(result.Snapshot().IncompleteReason); }

        var otherId = store.Reserve(Identity("history-first", "p2")).ReportId;
        h.HoldHistory = true;
        var history = h.Conductor.SendRequestAsync(method, new JsonObject { ["sessionId"] = "history-first" });
        try
        {
            await Until(() => h.HistoryCalls.Contains("history-first"), "held native history");
            await Assert.ThrowsExactlyAsync<JsonRpcException>(async () => await h.Prompt("history-first", "p2", otherId).WaitAsync(Wait));
            Assert.AreEqual(1, h.Prompts.Count, "no second native prompt");
            Assert.IsFalse(File.Exists(store.CapturePath(otherId)), "refusal is before capture admission");
        }
        finally { h.HistoryRelease.TrySetResult(); }
        await history.WaitAsync(Wait);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ShutdownAndAgentEofReleaseUnsealedCapture(bool stop)
    {
        var directory = TestDirs.New(); var store = new ReportStore(directory);
        var id = store.Reserve(Identity("s", "p")).ReportId;
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness(store, c => { var original = c.WriteFrame; c.WriteFrame = frame => { original(frame); if (frame[0] == 1) written.TrySetResult(); }; });
        await h.Initialize(); var prompt = h.Prompt("s", "p", id);
        await Until(() => h.Responses.ContainsKey("s"), "pending native prompt");
        await h.Update("s", "agent_message_chunk", "partial"); await written.Task.WaitAsync(Wait);
        try
        {
            if (stop) await h.Conductor.SendRequestAsync(GorynychProtocol.MethodHostStop, new JsonObject()).WaitAsync(Wait);
            else await h.Agent.DisposeAsync();
            await h.Run.WaitAsync(Wait);
            var reopenedStore = new ReportStore(directory);
            using var capture = reopenedStore.OpenCapture(id);
            Assert.AreEqual("partial", capture.Snapshot().Text);
            Assert.IsFalse(capture.Snapshot().Sealed);
            Assert.IsNull(capture.Snapshot().Outcome);
        }
        finally { h.Responses["s"].TrySetResult(new JsonObject { ["stopReason"] = "end_turn" }); }
        _ = prompt.Exception;
    }

}
