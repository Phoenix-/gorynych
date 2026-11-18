using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ReplyReminderDeliveryTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    sealed class Peer(string name)
    {
        public string Id => name + "-session";
        public string Token = "";
        public ScriptedAgent? Live;
        public readonly ConcurrentQueue<(string Text, JsonObject? Meta)> Prompts = new();
        public readonly ConcurrentQueue<string> NativeOrder = new();
        public TaskCompletionSource? ModeEntered;
        public TaskCompletionSource? ModeRelease;
        public TaskCompletionSource? ReminderEntered;
        public TaskCompletionSource? ReminderRelease;
        public TaskCompletionSource? CancelSeen;
        public ScriptedAdapter Adapter() => new(name, async (agent, request, ct) =>
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init(resume: false, load: true);
                case AcpMethods.Agent.SessionList: return AcpJson.ToNode(new ListSessionsResponse { Sessions = [] });
                case AcpMethods.Agent.SessionNew:
                    Live = agent;
                    agent.Rpc.NotificationHandler = (method, _) => { if (method == AcpMethods.Agent.SessionCancel) CancelSeen?.TrySetResult(); return Task.CompletedTask; };
                    var args = request.Params!["mcpServers"]!.AsArray().First(x => (string?)x!["name"] == "gorynych")!["args"]!.AsArray();
                    Token = (string)args[args.Count - 1]!;
                    var created = AcpJson.ToNode(new NewSessionResponse { SessionId = Id })!;
                    created["modes"] = SafeMode();
                    return created;
                case AcpMethods.Agent.SessionLoad: return AcpJson.ToNode(new LoadSessionResponse());
                case AcpMethods.Agent.SessionPrompt:
                    var p = request.ParamsAs<PromptRequest>(); var text = ((TextContent)p.Prompt[0]).Text;
                    var reminder = text.Contains("original_message_id", StringComparison.Ordinal);
                    Prompts.Enqueue((text, p.Meta)); NativeOrder.Enqueue(reminder ? "reminder" : "prompt");
                    if (reminder && ReminderRelease is { } hold) { ReminderEntered?.TrySetResult(); await hold.Task.WaitAsync(ct); }
                    await agent.Update(Id, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "observed" } });
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                case AcpMethods.Agent.SessionSetMode:
                    NativeOrder.Enqueue("mode:" + (string?)request.Params?["modeId"]);
                    ModeEntered?.TrySetResult();
                    if (ModeRelease is { } release) await release.Task.WaitAsync(ct);
                    return new JsonObject();
                default: throw new JsonRpcException(JsonRpcException.MethodNotFound, request.Method);
            }
        });
    }

    static JsonObject SafeMode() => new() { ["currentModeId"] = "safe", ["availableModes"] = new JsonArray(new JsonObject { ["id"] = "safe", ["name"] = "Safe" }) };

    static async Task ObserveEmptyBackground(ScriptedAgent agent, string id, SessionFeed feed, Conductor service)
    {
        await agent.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
        {
            ["sessionId"] = id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(), [GorynychProtocol.MetaBackgroundCompleteStream] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } },
        });
        var route = service.Agents[service.Store.Get(id)!.Agent].RouteOf(id)!.Rpc;
        await LinkHarness.Until(() => feed.HasFreshReminderBackground(route) && !feed.HasBackgroundWork && feed.Activity.Snapshot()["background"] is JsonArray b && b.Count == 0,
            "native empty background snapshots arrived").WaitAsync(Wait);
    }

    [TestMethod]
    public async Task MissingConfigLeavesNewWaitPassive()
    {
        var dir = TestDirs.New(); var clock = new ManualTime(); var a = new Peer("a"); var b = new Peer("b");
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "off", Time = clock,
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [a.Adapter(), b.Adapter()]);
        await using var client = await TestClient.ConnectAsync(service);
        foreach (var peer in new[] { a, b })
            await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = peer.Id.Replace("-session", "") } });
        service.UiMetadata.Set("p", "g", "Group");
        foreach (var peer in new[] { a, b }) service.UiMetadata.SetInitial(peer.Id,
            new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "p", ["groupId"] = "g" } });
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestReply, new JsonObject
        { ["sessionId"] = b.Id, ["text"] = "question", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaPeerToken] = a.Token, [GorynychProtocol.MetaMessageRequestId] = "q" } }).WaitAsync(Wait);
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!["requestDelivery"] == "delivered",
            "original delivered").WaitAsync(Wait);
        Assert.IsNull(service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!["reminders"]);
        clock.Advance(TimeSpan.FromHours(1)); await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(1, b.Prompts.Count);
        StringAssert.Contains((string?)service.Status()["_meta"]!["gorynych/replyWaits"]!["reminders"]!["policy"], "disabled");
    }

    [TestMethod]
    public async Task ConfiguredGroupGetsOneReminderAndPassiveWaitsRemainPassive()
    {
        var dir = TestDirs.New(); var clock = new ManualTime();
        File.WriteAllText(Path.Combine(dir, "reply-reminders.json"), """{"projectKey":"p","groupId":"g","delaySeconds":2,"overdueSeconds":30,"sweepSeconds":5}""");
        var a = new Peer("a"); var b = new Peer("b"); var c = new Peer("c");
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "reminders", Time = clock,
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [a.Adapter(), b.Adapter(), c.Adapter()]);
        await using var client = await TestClient.ConnectAsync(service);
        foreach (var peer in new[] { a, b, c })
            await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = peer.Id.Replace("-session", "") } });
        service.UiMetadata.Set("p", "g", "Group");
        foreach (var peer in new[] { a, b }) service.UiMetadata.SetInitial(peer.Id,
            new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "p", ["groupId"] = "g" } });
        service.UiMetadata.Set("other-project", "g", "Same group ID");
        service.UiMetadata.SetInitial(c.Id, new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject
            { ["projectKey"] = "other-project", ["groupId"] = "g" } });
        async Task Send(Peer target, string id) => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestReply, new JsonObject
        { ["sessionId"] = target.Id, ["text"] = "question", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaPeerToken] = a.Token, [GorynychProtocol.MetaMessageRequestId] = id } }).WaitAsync(Wait);
        await Send(b, "q"); await Send(c, "passive");
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!["requestDelivery"] == "delivered" &&
            !service.Feed(b.Id).TurnRunning && service.Feed(b.Id).Users == 0,
            "original request delivered and turn released").WaitAsync(Wait);
        var passive = service.ReplyMailbox.ListReplyWaits(c.Id, 0, 10)["items"]![0]!;
        Assert.IsNull(passive["reminders"]);
        var feed = service.Feed(b.Id);
        clock.Advance(TimeSpan.FromSeconds(2));
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "unknown background cannot wake");
        await b.Live!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
        { ["sessionId"] = b.Id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaBackgroundKnown] = new JsonArray() } } });
        await LinkHarness.Until(() => feed.Activity.Snapshot()["backgroundKnown"] is JsonArray, "partial-only snapshot observed").WaitAsync(Wait);
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "partial-only observation is not complete evidence");
        await b.Live!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
        { ["sessionId"] = b.Id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaBackgroundWork] = new JsonArray() } } });
        await LinkHarness.Until(() => feed.Activity.Snapshot()["background"] is JsonArray, "unconfirmed old-host snapshot observed").WaitAsync(Wait);
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "old or unconfirmed stream cannot authorize wake");
        await b.Live!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
        { ["sessionId"] = b.Id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaBackgroundWork] = new JsonArray(new JsonObject { ["type"] = "terminal", ["terminalId"] = "build" }),
              [GorynychProtocol.MetaBackgroundKnown] = new JsonArray() } } });
        await LinkHarness.Until(() => feed.HasBackgroundWork, "native background work observed").WaitAsync(Wait);
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "background work cannot wake");
        await ObserveEmptyBackground(b.Live!, b.Id, feed, service);
        service.Store.Update(b.Id, "b", r => r.Archived = true);
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "archived cannot wake");
        service.Store.Update(b.Id, "b", r => r.Archived = false);
        feed.SetReadOnly(new SessionFeed.ReadOnlyState("test", null));
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "read-only cannot wake");
        feed.SetReadOnly(null);
        feed.SetCurrent("modes", "currentModeId", JsonValue.Create("unknown"));
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "unknown mode cannot wake");
        feed.CaptureState(new JsonObject { ["modes"] = new JsonObject { ["currentModeId"] = "full", ["availableModes"] = new JsonArray(
            new JsonObject { ["id"] = "full", ["name"] = "Full", ["_meta"] = new JsonObject { [GorynychProtocol.MetaFullAccess] = true } },
            new JsonObject { ["id"] = "safe", ["name"] = "Safe" }) } });
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "unapproved full-access boundary cannot wake");
        feed.SetCurrent("modes", "currentModeId", JsonValue.Create("safe"));
        service.UiMetadata.Patch(b.Id, new JsonObject { [GorynychProtocol.MetaGroup] = null });
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "moved group cannot wake");
        service.UiMetadata.Patch(b.Id, new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "p", ["groupId"] = "g" } });
        await feed.TurnGate.WaitAsync(Wait);
        try
        {
            await service.SweepReplyRemindersAsync().WaitAsync(Wait);
            Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)), "busy gate is not waited on");
        }
        finally { feed.TurnGate.Release(); }
        clock.Advance(TimeSpan.FromSeconds(3));
        await LinkHarness.Until(() => b.Prompts.Any(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)),
            "the clock timer dispatched the eligible reminder").WaitAsync(Wait);
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!["reminders"]!["reminder"] == "delivered",
            "reminder delivery committed").WaitAsync(Wait);
        var reminder = b.Prompts.Single(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal));
        Assert.AreEqual("q", (string?)reminder.Meta?[GorynychProtocol.MetaReplyReminder]?["messageId"]);
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(1, b.Prompts.Count(p => p.Text.Contains("original_message_id q", StringComparison.Ordinal)));
        Assert.AreEqual("delivered", (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!["reminders"]!["reminder"]);

        await Send(b, "q2");
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![2]!["requestDelivery"] == "delivered" && !feed.TurnRunning && feed.Users == 0,
            "second original delivered and turn released").WaitAsync(Wait);
        clock.Advance(TimeSpan.FromSeconds(2));
        // Claude reports the complete snapshot at process start and only sends changes.
        // The same live full stream remains authoritative across the next normal turn.
        var before = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeReminderClaim = async id => { if (id == "q2") { before.TrySetResult(); await release.Task; } };
        var pending = service.SweepReplyRemindersAsync();
        try
        {
            await before.Task.WaitAsync(Wait);
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodCancelReplyWait, new JsonObject
            { ["messageId"] = "q2", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = a.Token } }).WaitAsync(Wait);
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q2", StringComparison.Ordinal)), "cancellation won before durable claim");

        service.BeforeReminderClaim = null;
        await Send(b, "q3");
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![3]!["requestDelivery"] == "delivered" && !feed.TurnRunning && feed.Users == 0,
            "third original delivered and turn released").WaitAsync(Wait);
        await b.Live!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
        { ["sessionId"] = b.Id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaBackgroundWork] = null } } });
        await LinkHarness.Until(() => feed.Activity.Snapshot()["background"] is null, "unknown background revoked full evidence").WaitAsync(Wait);
        clock.Advance(TimeSpan.FromSeconds(2)); await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q3", StringComparison.Ordinal)), "unknown background cannot wake");
        await ObserveEmptyBackground(b.Live!, b.Id, feed, service);
        var beforeReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeReminderClaim = async id => { if (id == "q3") { beforeReply.TrySetResult(); await resumeReply.Task; } };
        var afterReply = service.SweepReplyRemindersAsync();
        try
        {
            await beforeReply.Task.WaitAsync(Wait);
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReplyMessage, new JsonObject
            { ["messageId"] = "q3", ["text"] = "answer", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaPeerToken] = b.Token, [GorynychProtocol.MetaMessageRequestId] = "reply-q3" } }).WaitAsync(Wait);
        }
        finally { resumeReply.TrySetResult(); }
        await afterReply.WaitAsync(Wait);
        Assert.AreEqual("replied", (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![3]!["state"]);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q3", StringComparison.Ordinal)), "reply won before durable claim");

        service.BeforeReminderClaim = null;
        await Send(b, "q4");
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![4]!["requestDelivery"] == "delivered" && !feed.TurnRunning && feed.Users == 0,
            "fourth original delivered and turn released").WaitAsync(Wait);
        var beforeStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeReminderClaim = async id => { if (id == "q4") { beforeStop.TrySetResult(); await resumeStop.Task; } };
        clock.Advance(TimeSpan.FromSeconds(2)); var beforeMaintenance = service.SweepReplyRemindersAsync();
        Task<JsonNode?>? stopping = null;
        try
        {
            await beforeStop.Task.WaitAsync(Wait);
            stopping = client.Rpc.SendRequestAsync(GorynychProtocol.MethodStop,
                new JsonObject { ["agents"] = new JsonArray("b"), ["wait"] = true, ["timeoutSeconds"] = 10 });
            await LinkHarness.Until(() => service.IsStopping("b"), "maintenance entered").WaitAsync(Wait);
        }
        finally { resumeStop.TrySetResult(); }
        await beforeMaintenance.WaitAsync(Wait);
        if (stopping is not null) await stopping.WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q4", StringComparison.Ordinal)), "maintenance won before durable claim");
    }
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task NativeModeChangesAndReminderHandoffAreOrdered()
    {
        var dir = TestDirs.New(); var clock = new ManualTime(); var a = new Peer("a"); var b = new Peer("b");
        File.WriteAllText(Path.Combine(dir, "reply-reminders.json"), """{"projectKey":"p","groupId":"g","delaySeconds":2,"overdueSeconds":30,"sweepSeconds":3600}""");
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "mode-race", Time = clock,
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [a.Adapter(), b.Adapter()]);
        await using var client = await TestClient.ConnectAsync(service);
        foreach (var peer in new[] { a, b })
            await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = peer.Id.Replace("-session", "") } });
        service.UiMetadata.Set("p", "g", "Group");
        foreach (var peer in new[] { a, b }) service.UiMetadata.SetInitial(peer.Id,
            new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "p", ["groupId"] = "g" } });
        async Task Send(string id) => await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestReply, new JsonObject
        { ["sessionId"] = b.Id, ["text"] = "question", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaPeerToken] = a.Token, [GorynychProtocol.MetaMessageRequestId] = id } }).WaitAsync(Wait);
        Task<JsonNode?> SetMode(string value) => client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetMode,
            new JsonObject { ["sessionId"] = b.Id, ["modeId"] = value });
        var feed = service.Feed(b.Id);
        feed.CaptureState(new JsonObject { ["modes"] = new JsonObject { ["currentModeId"] = "safe", ["availableModes"] = new JsonArray(
            new JsonObject { ["id"] = "safe", ["name"] = "Safe" },
            new JsonObject { ["id"] = "full", ["name"] = "Full", ["_meta"] = new JsonObject { [GorynychProtocol.MetaFullAccess] = true } }) } });
        await Send("q1");
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!["requestDelivery"] == "delivered" && !feed.TurnRunning && feed.Users == 0,
            "first request delivered").WaitAsync(Wait);
        await ObserveEmptyBackground(b.Live!, b.Id, feed, service);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        service.BeforeMailboxMarkStarted = () => { entered.Set(); if (!release.Wait(Wait)) throw new IOException("claim barrier timed out"); };
        b.ReminderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.ReminderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.CancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Advance(TimeSpan.FromSeconds(2)); await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Task<JsonNode?>? mode = null;
        try
        {
            Assert.IsTrue(await Task.Run(() => entered.Wait(Wait)), "claimed reminder reached its final dispatch barrier");
            mode = SetMode("full");
            await LinkHarness.Until(() => feed.Users >= 2, "mode request registered behind the forward gate").WaitAsync(Wait);
            Assert.IsFalse(b.NativeOrder.Any(x => x == "mode:full"), "native mode change cannot pass the claimed reminder");
        }
        finally { release.Set(); }
        try
        {
            await b.ReminderEntered.Task.WaitAsync(Wait);
            Assert.IsFalse(mode!.IsCompleted, "mode waits while the native reminder is running");
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetMode,
                new JsonObject { ["sessionId"] = a.Id, ["modeId"] = "safe" }).WaitAsync(Wait);
            Assert.IsTrue(a.NativeOrder.Contains("mode:safe"), "safe target does not block the source's mode switch");
            await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = b.Id });
            await b.CancelSeen.Task.WaitAsync(Wait);
        }
        finally { b.ReminderRelease.TrySetResult(); }
        await mode!.WaitAsync(Wait); service.BeforeMailboxMarkStarted = null;
        b.ReminderEntered = null; b.ReminderRelease = null;
        await LinkHarness.Until(() => b.NativeOrder.Any(x => x == "mode:full"), "mode switch completed").WaitAsync(Wait);
        var first = b.NativeOrder.ToArray();
        Assert.IsTrue(Array.IndexOf(first, "reminder") >= 0 && Array.IndexOf(first, "reminder") < Array.IndexOf(first, "mode:full"),
            "native order: " + string.Join(",", first) + "; wait: " + service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10).ToJsonString());
        Assert.AreEqual("full", (string?)feed.State()["modes"]!["currentModeId"]);
        await SetMode("safe").WaitAsync(Wait);
        await Send("q2");
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![1]!["requestDelivery"] == "delivered" && !feed.TurnRunning && feed.Users == 0,
            "second request delivered").WaitAsync(Wait);
        b.ModeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.ModeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.CancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setting = SetMode("full");
        try
        {
            await b.ModeEntered.Task.WaitAsync(Wait);
            await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = b.Id });
            await b.CancelSeen.Task.WaitAsync(Wait);
            clock.Advance(TimeSpan.FromSeconds(2));
            await service.SweepReplyRemindersAsync().WaitAsync(Wait);
            Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q2", StringComparison.Ordinal)), "pending mode change cannot be bypassed");
        }
        finally { b.ModeRelease.TrySetResult(); }
        await setting.WaitAsync(Wait);
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q2", StringComparison.Ordinal)), "safe sender cannot wake a full-access target");
        service.Feed(a.Id).SetCurrent("modes", "currentModeId", JsonValue.Create("unknown"));
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q2", StringComparison.Ordinal)), "unknown sender mode cannot authorize full access");
        service.Feed(a.Id).SetCurrent("modes", "currentModeId", JsonValue.Create("safe"));
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodCancelReplyWait, new JsonObject
        { ["messageId"] = "q2", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = a.Token } }).WaitAsync(Wait);
        await SetMode("safe").WaitAsync(Wait); await Send("q3");
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![2]!["requestDelivery"] == "delivered" && !feed.TurnRunning && feed.Users == 0,
            "third request delivered").WaitAsync(Wait);
        using var finalEntered = new ManualResetEventSlim(); using var finalRelease = new ManualResetEventSlim();
        service.BeforeMailboxMarkStarted = () => { finalEntered.Set(); if (!finalRelease.Wait(Wait)) throw new IOException("final barrier timed out"); };
        clock.Advance(TimeSpan.FromSeconds(2)); await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        try
        {
            Assert.IsTrue(await Task.Run(() => finalEntered.Wait(Wait)));
            await b.Live!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
            { ["sessionId"] = b.Id, ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaBackgroundWork] = null } } });
            await LinkHarness.Until(() => feed.Activity.Snapshot()["background"] is null, "background evidence revoked before native dispatch").WaitAsync(Wait);
        }
        finally { finalRelease.Set(); }
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![2]!["reminders"]!["reminder"] == "uncertain",
            "claimed but unsent attempt remains honest and is not retried").WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q3", StringComparison.Ordinal)), "revoked activity evidence prevents native dispatch");
        service.BeforeMailboxMarkStarted = null;
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodCancelReplyWait, new JsonObject
        { ["messageId"] = "q3", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = a.Token } }).WaitAsync(Wait);
        await Send("q4");
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![3]!["requestDelivery"] == "delivered" && !feed.TurnRunning && feed.Users == 0,
            "fourth request delivered").WaitAsync(Wait);
        await ObserveEmptyBackground(b.Live!, b.Id, feed, service);
        using var closeEntered = new ManualResetEventSlim(); using var closeRelease = new ManualResetEventSlim();
        service.BeforeMailboxMarkStarted = () => { closeEntered.Set(); if (!closeRelease.Wait(Wait)) throw new IOException("route barrier timed out"); };
        clock.Advance(TimeSpan.FromSeconds(2)); await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        try
        {
            Assert.IsTrue(await Task.Run(() => closeEntered.Wait(Wait)));
            await b.Live!.Rpc.DisposeAsync();
            await LinkHarness.Until(() => service.Agents["b"].RouteOf(b.Id) is null, "test-owned native route closed").WaitAsync(Wait);
        }
        finally { closeRelease.Set(); }
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![3]!["reminders"]!["reminder"] == "uncertain",
            "closed route did not redispatch").WaitAsync(Wait);
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id q4", StringComparison.Ordinal)), "closed route prevents native dispatch");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CancelledModeRequestWaitsForNativeSettlementBeforeReminder(bool changeTarget)
    {
        var dir = TestDirs.New(); var clock = new ManualTime(); var a = new Peer("a"); var b = new Peer("b");
        File.WriteAllText(Path.Combine(dir, "reply-reminders.json"), """{"projectKey":"p","groupId":"g","delaySeconds":2,"overdueSeconds":30,"sweepSeconds":3600}""");
        var hosts = new InProcessHosts(name => name == "a" ? a.Adapter() : b.Adapter());
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "mode-cancel", Time = clock,
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [a.Adapter(), b.Adapter()]);
        hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        foreach (var peer in new[] { a, b })
            await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = peer.Id.Replace("-session", "") } });
        service.UiMetadata.Set("p", "g", "Group");
        foreach (var peer in new[] { a, b }) service.UiMetadata.SetInitial(peer.Id,
            new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "p", ["groupId"] = "g" } });
        JsonObject Modes() => new() { ["modes"] = new JsonObject { ["currentModeId"] = "full", ["availableModes"] = new JsonArray(
            new JsonObject { ["id"] = "safe", ["name"] = "Safe" },
            new JsonObject { ["id"] = "full", ["name"] = "Full", ["_meta"] = new JsonObject { [GorynychProtocol.MetaFullAccess] = true } }) } };
        service.Feed(a.Id).CaptureState(Modes()); service.Feed(b.Id).CaptureState(Modes());
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = a.Id, Prompt = [new TextContent { Text = "prior work" }] }).WaitAsync(Wait);
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestReply, new JsonObject { ["sessionId"] = b.Id, ["text"] = "question",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = a.Token, [GorynychProtocol.MetaMessageRequestId] = "cancel-mode" } }).WaitAsync(Wait);
        var target = service.Feed(b.Id);
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!["requestDelivery"] == "delivered" && !target.TurnRunning && target.Users == 0, "original delivered").WaitAsync(Wait);
        await ObserveEmptyBackground(b.Live!, b.Id, target, service);
        var changed = changeTarget ? b : a;
        changed.ModeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.ModeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var setting = client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetMode,
            new JsonObject { ["sessionId"] = changed.Id, ["modeId"] = "safe" }, cancel.Token);
        try
        {
            await changed.ModeEntered.Task.WaitAsync(Wait); // Fake native has applied the requested mode; response is held.
            cancel.Cancel();
            try { await setting.WaitAsync(Wait); Assert.Fail("client cancellation must complete promptly"); }
            catch (OperationCanceledException) { }
            clock.Advance(TimeSpan.FromSeconds(2));
            await service.SweepReplyRemindersAsync().WaitAsync(Wait);
            Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id cancel-mode", StringComparison.Ordinal)), "stale full sender must not authorize the target");
        }
        finally { changed.ModeRelease.TrySetResult(); }
        await LinkHarness.Until(() => (string?)service.Feed(changed.Id).State()["modes"]!["currentModeId"] == "safe" && !service.Feed(changed.Id).ReminderModeUncertain, "late native response updates cached authority").WaitAsync(Wait);
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        if (!changeTarget)
        {
            Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id cancel-mode", StringComparison.Ordinal)), "settled safe sender remains ineligible");
            changed.ModeRelease = null; changed.ModeEntered = null;
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetMode, new JsonObject { ["sessionId"] = a.Id, ["modeId"] = "full" }).WaitAsync(Wait);
            await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        }
        await LinkHarness.Until(() => b.Prompts.Count(p => p.Text.Contains("original_message_id cancel-mode", StringComparison.Ordinal)) == 1, "confirmed full mode authorizes one reminder").WaitAsync(Wait);
    }

    [TestMethod]
    public async Task OverdueEscalationToIdleWaiterSurvivesArchivedRecipient()
    {
        var dir = TestDirs.New(); var clock = new ManualTime();
        File.WriteAllText(Path.Combine(dir, "reply-reminders.json"), """{"projectKey":"p","groupId":"g","delaySeconds":2,"overdueSeconds":10,"sweepSeconds":3600}""");
        var a = new Peer("a"); var b = new Peer("b");
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "overdue", Time = clock,
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [a.Adapter(), b.Adapter()]);
        await using var client = await TestClient.ConnectAsync(service);
        foreach (var peer in new[] { a, b })
            await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = peer.Id.Replace("-session", "") } });
        service.UiMetadata.Set("p", "g", "Group");
        foreach (var peer in new[] { a, b }) service.UiMetadata.SetInitial(peer.Id,
            new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "p", ["groupId"] = "g" } });
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = a.Id, Prompt = [new TextContent { Text = "prior work" }] }).WaitAsync(Wait);
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestReply, new JsonObject
        { ["sessionId"] = b.Id, ["text"] = "question", ["_meta"] = new JsonObject
            { [GorynychProtocol.MetaPeerToken] = a.Token, [GorynychProtocol.MetaMessageRequestId] = "overdue-q" } }).WaitAsync(Wait);
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!["requestDelivery"] == "delivered",
            "original delivered").WaitAsync(Wait);
        var waiter = service.Feed(a.Id); await ObserveEmptyBackground(a.Live!, a.Id, waiter, service);
        service.Store.Update(b.Id, "b", r => r.Archived = true);
        clock.Advance(TimeSpan.FromSeconds(10));
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!["reminders"]!["escalation"] == "delivered",
            "overdue notice delivered to original waiter").WaitAsync(Wait);
        var status = service.ReplyMailbox.ListReplyWaits(a.Id, 0, 10)["items"]![0]!;
        Assert.IsNotNull(status["reminders"]!["overdueNotice"]);
        var notice = a.Prompts.Single(p => p.Text.Contains("original_message_id overdue-q", StringComparison.Ordinal));
        StringAssert.Contains(notice.Text, "overdue"); StringAssert.Contains(notice.Text, "archived");
        await service.SweepReplyRemindersAsync().WaitAsync(Wait);
        Assert.AreEqual(1, a.Prompts.Count(p => p.Text.Contains("original_message_id overdue-q", StringComparison.Ordinal)));
        Assert.AreEqual(0, b.Prompts.Count(p => p.Text.Contains("original_message_id overdue-q", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task StartedReminderIsNotReplayedAcrossHostedRestart()
    {
        var dir = TestDirs.New(); var clock = new ManualTime();
        File.WriteAllText(Path.Combine(dir, "reply-reminders.json"), """{"projectKey":"p","groupId":"g","delaySeconds":2,"overdueSeconds":30,"sweepSeconds":3600}""");
        var agent = new TurnAgent("turns"); var hosts = new InProcessHosts(_ => agent);
        Conductor Next() => new(new ConductorOptions { DataDirectory = dir, ServiceId = "reminder-restart", Time = clock,
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent]);
        var service = Next(); hosts.Service = service; var client = await TestClient.ConnectAsync(service);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var a = await HostTests.NewSession(client); var b = await HostTests.NewSession(client);
            service.UiMetadata.Set("p", "g", "Group");
            foreach (var id in new[] { a, b }) service.UiMetadata.SetInitial(id,
                new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "p", ["groupId"] = "g" } });
            var feed = service.Feed(b); feed.CaptureState(new JsonObject { ["modes"] = SafeMode() });
            await ObserveEmptyBackground(agent.AgentOf(b), b, feed, service);
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestReply, new JsonObject
            { ["sessionId"] = b, ["text"] = "question", ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaPeerToken] = service.Store.Get(a)!.PeerToken, [GorynychProtocol.MetaMessageRequestId] = "original" } }).WaitAsync(Wait);
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a, 0, 10)["items"]![0]!["requestDelivery"] == "delivered" &&
                !service.Feed(b).TurnRunning && service.Feed(b).Users == 0, "original delivered and turn released").WaitAsync(Wait);
            Assert.IsNull(feed.Activity.Snapshot()["backgroundKnown"], "Claude emits no partial companion snapshot");
            Assert.IsTrue(feed.HasFreshReminderBackground(service.Agents["turns"].RouteOf(b)!.Rpc),
                "the startup full snapshot survives the ordinary turn without a synthetic refresh");
            agent.During = async (_, sid, _) =>
            {
                if (sid == b && agent.Prompts.Last().Contains("original_message_id original", StringComparison.Ordinal))
                { entered.TrySetResult(); await release.Task; }
            };
            clock.Advance(TimeSpan.FromSeconds(2));
            var sweep = service.SweepReplyRemindersAsync();
            await entered.Task.WaitAsync(Wait);
            var started = service.ReplyMailbox.ListReplyWaits(a, 0, 10)["items"]![0]!["reminders"]!;
            var reminderId = (string)started["reminderMessageId"]!;
            Assert.AreEqual("started", (string?)started["reminder"]);
            await HostTests.RestartAsync(service); hosts.Service = null;
            await service.DisposeAsync(); await client.DisposeAsync(); hosts.BreakAll();
            service = Next(); hosts.Service = service; client = await TestClient.ConnectAsync(service);
            await LinkHarness.Until(() => service.Agents["turns"].RouteOf(b) is not null, "host route reattached").WaitAsync(Wait);
            Assert.IsFalse(service.Feed(b).HasFreshReminderBackground(service.Agents["turns"].RouteOf(b)!.Rpc),
                "restored host state alone is not a new live full-stream observation");
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.ListReplyWaits(a, 0, 10)["items"]![0]!["reminders"]!["reminder"] == "uncertain", "uncertain started reminder recovered").WaitAsync(Wait);
            var recovered = service.ReplyMailbox.ListReplyWaits(a, 0, 10)["items"]![0]!;
            Assert.AreEqual("waiting", (string?)recovered["state"]);
            Assert.AreEqual(reminderId, (string?)recovered["reminders"]!["reminderMessageId"]);
            release.TrySetResult();
            try { await sweep.WaitAsync(Wait); } catch (Exception) { /* old link was interrupted by restart */ }
            await service.SweepReplyRemindersAsync().WaitAsync(Wait);
            Assert.AreEqual(1, agent.Prompts.Count(p => p.Contains("original_message_id original", StringComparison.Ordinal)));
        }
        finally
        {
            release.TrySetResult(); await client.DisposeAsync(); await service.DisposeAsync(); hosts.BreakAll();
        }
    }

}
