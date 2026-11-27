using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class CoordinatorInboxTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [TestMethod]
    public void BlockedNoticeIsDeduplicatedAndReopenSupersedesOnlyUnstarted()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var box = new MailboxStore(path);
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        var generation = (string)box.FindAssignment("task", "owner")!["generation"]!;
        JsonObject Change(long revision, string operation, string state, string text) =>
            box.TransitionAssignment("task", "child", revision, operation, state, text, generation: generation);
        Change(1, "block", "blocked", "need input");
        var original = box.ReadManagedQueue("owner", 8).Single();
        Assert.AreEqual("blocked", (string?)original["kind"]);
        Assert.AreEqual("need input", (string?)original["text"]);
        Change(1, "block", "blocked", "need input");
        Change(2, "again", "blocked", "still waiting");
        Assert.AreEqual((long?)original["sequence"], (long?)box.ReadManagedQueue("owner", 8).Single()["sequence"]);
        Change(3, "reopen", "open", "");
        Assert.AreEqual(0, new MailboxStore(path).ReadManagedQueue("owner", 8).Count);
        Change(4, "block2", "blocked", "another obstacle");
        var second = box.ReadManagedQueue("owner", 8).Single();
        Assert.IsTrue((long)second["sequence"]! > (long)original["sequence"]!);
        Assert.IsTrue(box.TryStartCoordinatorBatch([second]));
        Change(5, "reopen2", "open", "");
        box = new MailboxStore(path);
        Assert.IsFalse(box.TryStartCoordinatorBatch(box.ReadManagedQueue("owner", 8)));
        box.FailCoordinatorNotice(second, "lost", true);
        Assert.AreEqual("uncertain", (string?)box.FindAssignment("task", "owner")!["coordinatorNotice"]!["delivery"]);
        Change(6, "block3", "blocked", "new obstacle");
        Assert.AreEqual("new obstacle", (string?)box.ReadManagedQueue("owner", 8).Single()["text"]);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void BlockedResumeReportRequiresNoticeAfterExactDurableClaim(bool blockedBeforeClaim)
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var box = new MailboxStore(path);
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var generation = (string)box.FindAssignment("task", "owner")!["generation"]!;
        box.Accept("wait", "child", "peer", "question", expectReply: true);
        var target = box.ContinuationWait("wait", "child")!; target["kind"] = "reply_wait";
        target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
        box.UpdateContinuation("task", "child", generation, 1, "register", "register", target);
        box.Accept("reply", "peer", "child", "answer", replyTo: "wait");
        var reply = box.ReadManagedQueue("child", 8).Single(x => (string?)x["id"] == "reply");
        var exact = box.ResolveCoordinatorReply([reply], "owner"); Assert.IsNotNull(exact);
        if (blockedBeforeClaim)
        {
            box.TransitionAssignment("task", "child", 2, "block", "blocked", "obstacle", generation: generation);
            Assert.IsFalse(box.RecordCoordinatorReport("task", generation, "child", "owner", "unclaimed", resume: true,
                replySequence: exact.ReplySequence, waitId: exact.WaitId, waitSequence: exact.WaitSequence));
        }
        Assert.IsTrue(box.TryStartCoordinatorBatch([reply], exact, "owner"));
        box = new MailboxStore(path);
        if (!blockedBeforeClaim) box.TransitionAssignment("task", "child", 2, "block", "blocked", "obstacle", generation: generation);
        box = new MailboxStore(path);
        Assert.IsFalse(box.RecordCoordinatorReport("task", generation, "child", "owner", "foreign", resume: true,
            replySequence: exact.ReplySequence, waitId: "wrong", waitSequence: exact.WaitSequence));
        var retained = box.RecordCoordinatorReport("task", generation, "child", "owner", "report", resume: true,
            replySequence: exact.ReplySequence, waitId: exact.WaitId, waitSequence: exact.WaitSequence);
        Assert.AreEqual(!blockedBeforeClaim, retained, "only a post-claim notice replaces legacy report delivery");
        if (retained) Assert.AreEqual("report", (string?)box.ReadManagedQueue("owner", 8).Single()["reportId"]);
    }

    [TestMethod]
    [DataRow("cancelled")]
    [DataRow("completed")]
    public void TerminalTransitionSupersedesUnstartedBlockedNotice(string outcome)
    {
        var box = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"));
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        var generation = (string)box.FindAssignment("task", "owner")!["generation"]!;
        box.TransitionAssignment("task", "child", 1, "block", "blocked", "obstacle", generation: generation);
        box.TransitionAssignment("task", outcome == "cancelled" ? "owner" : "child", 2, "finish", outcome, "finished", generation: generation);
        var notices = box.ReadManagedQueue("owner", 8);
        Assert.IsFalse(notices.Any(item => (string?)item["kind"] == "blocked"));
        Assert.AreEqual(outcome == "completed" ? 1 : 0, notices.Count);
        if (outcome == "completed") Assert.AreEqual("outcome", (string?)notices.Single()["kind"]);
    }

    [TestMethod]
    public void BatchClaimSurvivesRestartWithoutReplay()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var box = new MailboxStore(path);
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var generation = (string)box.FindAssignment("task", "owner")!["generation"]!;
        box.TransitionAssignment("task", "child", 1, "done", "completed", "finished", generation: generation);
        box.Accept("question", "owner", "peer", "question", expectReply: true);
        box.MarkStarted("question", "peer"); box.MarkDelivered("question", "peer");
        box.Accept("answer", "peer", "owner", "reply text", replyTo: "question");
        var batch = box.ReadManagedQueue("owner", 8);
        Assert.AreEqual(2, batch.Count);
        Assert.IsTrue(box.TryStartCoordinatorBatch(batch));
        box = new MailboxStore(path);
        var retained = box.ReadManagedQueue("owner", 8);
        Assert.AreEqual(2, retained.Count);
        Assert.IsTrue(retained.All(item => (bool?)item["started"] == true));
        Assert.IsFalse(box.TryStartCoordinatorBatch(retained), "a started batch is never replayed");
        box.FailCoordinatorNotice(retained[0], "connection lost", true);
        var notice = box.FindAssignment("task", "owner")!["coordinatorNotice"]!;
        Assert.AreEqual("uncertain", (string?)notice["delivery"]);
        Assert.AreEqual("completed", (string?)box.FindAssignment("task", "owner")!["status"]);
    }

    [TestMethod]
    public void InFlightFallbackDoesNotLoseLaterOutcome()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var box = new MailboxStore(path);
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var generation = (string)box.FindAssignment("task", "owner")!["generation"]!;
        Assert.IsTrue(box.RecordCoordinatorUnsupervisedTurn("task", generation, "child", "owner"));
        var old = box.ReadManagedQueue("owner", 8).Single();
        Assert.IsTrue(box.TryStartCoordinatorBatch([old]));
        box.TransitionAssignment("task", "child", 1, "done", "completed", "finished", generation: generation);
        var queued = box.ReadManagedQueue("owner", 8);
        Assert.AreEqual(2, queued.Count);
        Assert.AreEqual("unsupervised", (string?)queued[0]["kind"]);
        Assert.AreEqual("outcome", (string?)queued[1]["kind"]);
        box.CompleteCoordinatorBatch([old]);
        box = new MailboxStore(path);
        var later = box.ReadManagedQueue("owner", 8).Single();
        Assert.AreEqual("outcome", (string?)later["kind"]);
        Assert.IsTrue(box.TryStartCoordinatorBatch([later]));
        box.CompleteCoordinatorBatch([later]);
        Assert.AreEqual(0, new MailboxStore(path).ReadManagedQueue("owner", 8).Count);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task BusyOwnerReceivesOutcomeAndReplyInOnePrompt()
    {
        var prompts = new ConcurrentQueue<string>();
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string parentToken = "", childToken = "";
        ScriptedAdapter Adapter(string name) => new(name, async (agent, request, ct) =>
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init(resume: false, load: true);
                case AcpMethods.Agent.SessionList: return AcpJson.ToNode(new ListSessionsResponse { Sessions = [] });
                case AcpMethods.Agent.SessionNew:
                    var args = request.Params!["mcpServers"]!.AsArray().First(x => (string?)x!["name"] == "gorynych")!["args"]!.AsArray();
                    if (name == "owner") parentToken = (string)args[args.Count - 1]!;
                    else childToken = (string)args[args.Count - 1]!;
                    return AcpJson.ToNode(new NewSessionResponse { SessionId = name });
                case AcpMethods.Agent.SessionLoad: return AcpJson.ToNode(new LoadSessionResponse());
                case AcpMethods.Agent.SessionPrompt:
                    var text = ((TextContent)request.ParamsAs<PromptRequest>().Prompt[0]).Text;
                    if (name == "owner")
                    {
                        prompts.Enqueue(text);
                        if (text == "busy") { busy.TrySetResult(); await release.Task; }
                        else if (text.Contains("assignment_id task ·", StringComparison.Ordinal))
                        { delivered.TrySetResult(); await finishFirstBatch.Task; }
                    }
                    else await releaseChild.Task;
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                default: throw new JsonRpcException(JsonRpcException.MethodNotFound, request.Method);
            }
        });
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "inbox",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [Adapter("owner"), Adapter("child")]);
        await using var client = await TestClient.ConnectAsync(service);
        foreach (var name in new[] { "owner", "child" })
            await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = Path.GetTempPath(), Meta = new JsonObject { [GorynychProtocol.MetaAgent] = name } });
        service.Store.Update("child", "child", r => { r.ParentSessionId = "owner"; r.ReportToParent = false; });
        var running = client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = "owner", Prompt = [new TextContent { Text = "busy" }] });
        try
        {
            await busy.Task.WaitAsync(Wait);
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = "child", ["text"] = "work",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = parentToken, [GorynychProtocol.MetaMessageRequestId] = "task" } });
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("task", "owner")?["delivery"] == "started", "delegation started").WaitAsync(Wait);
            service.ReplyMailbox.Accept("question", "owner", "child", "question", expectReply: true);
            service.ReplyMailbox.MarkStarted("question", "child"); service.ReplyMailbox.MarkDelivered("question", "child");
            var generation = (string)service.ReplyMailbox.FindAssignment("task", "owner")!["generation"]!;
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, new JsonObject { ["source"] = "delegation",
                ["assignmentId"] = "task", ["generation"] = generation, ["expectedRevision"] = 1, ["operationId"] = "done",
                ["outcome"] = "completed", ["summary"] = "exact outcome summary",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = childToken } });
            service.ReplyMailbox.Accept("answer", "child", "owner", "exact reply", replyTo: "question");
            releaseChild.TrySetResult();
            await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("task", "owner")?["delivery"] == "delivered", "delegation delivered").WaitAsync(Wait);
            release.TrySetResult(); await running.WaitAsync(Wait); await delivered.Task.WaitAsync(Wait);
            // This notice arrives after the first exact batch is committed and must form a later batch.
            service.ReplyMailbox.Accept("later-task", "owner", "child", "later", delegation: true, coordinatorLifecycle: true);
            service.ReplyMailbox.MarkStarted("later-task", "child"); service.ReplyMailbox.MarkDelivered("later-task", "child");
            var laterGeneration = (string)service.ReplyMailbox.FindAssignment("later-task", "owner")!["generation"]!;
            service.ReplyMailbox.TransitionAssignment("later-task", "child", 1, "later-done", "completed", "later summary", generation: laterGeneration);
            service.QueueMailbox("owner"); finishFirstBatch.TrySetResult();
            await LinkHarness.Until(() => service.ReplyMailbox.ReadManagedQueue("owner", 8).Count == 0, "batch delivered").WaitAsync(Wait);
            var texts = prompts.ToArray(); Assert.AreEqual(3, texts.Length);
            StringAssert.Contains(texts[1], "assignment_id task");
            StringAssert.Contains(texts[1], "exact outcome summary");
            StringAssert.Contains(texts[1], "message_id answer");
            StringAssert.Contains(texts[1], "exact reply");
            StringAssert.Contains(texts[2], "assignment_id later-task");
            StringAssert.Contains(texts[2], "later summary");
            Assert.AreEqual("completed", (string?)service.ReplyMailbox.FindAssignment("task", "owner")!["status"]);
        }
        finally { finishFirstBatch.TrySetResult(); releaseChild.TrySetResult(); release.TrySetResult(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ModeChangeBeforeBatchAdmissionExcludesUnapprovedReply(bool replyOnly)
    {
        var owner = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true };
        var child = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "inbox-mode",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [owner.Adapter, child.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> Make(string agent) => (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Path.GetTempPath(), Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent } })).SessionId;
        Task Mode(string id, string mode) => client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = id, ["configId"] = "mode", ["value"] = mode });
        var parent = await Make("kimi"); var worker = await Make("dsh"); await Mode(parent, "ask"); await Mode(worker, "ask");
        var box = service.ReplyMailbox;
        if (replyOnly)
        {
            box.Accept("question-head", parent, worker, "question", expectReply: true);
            box.MarkStarted("question-head", worker); box.MarkDelivered("question-head", worker);
            box.Accept("reply-head", worker, parent, "first secret", replyTo: "question-head");
        }
        else
        {
            box.Accept("task-mode", parent, worker, "work", delegation: true, coordinatorLifecycle: true);
            box.MarkStarted("task-mode", worker); box.MarkDelivered("task-mode", worker);
            var generation = (string)box.FindAssignment("task-mode", parent)!["generation"]!;
            box.TransitionAssignment("task-mode", worker, 1, "done", "completed", "summary", generation: generation);
        }
        box.Accept("question-mode", parent, worker, "question", expectReply: true);
        box.MarkStarted("question-mode", worker); box.MarkDelivered("question-mode", worker);
        box.Accept("reply-mode", worker, parent, "secret reply", replyTo: "question-mode");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeCoordinatorModeWaitForTests = async () => { reached.TrySetResult(); await release.Task; };
        client.Approve = _ => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "decline" });
        try
        {
            service.QueueMailbox(parent); await reached.Task.WaitAsync(Wait);
            await Mode(parent, "full"); release.TrySetResult();
            await LinkHarness.Until(() => client.Approvals.Count > 0, "reply approval requested").WaitAsync(Wait);
            await LinkHarness.Until(() => box.ReadManagedQueue(parent, 8).Count == 0, "batch and declined reply settled").WaitAsync(Wait);
            string[] prompts; lock (owner.Prompts) prompts = owner.Prompts.Select(p => p.Text).ToArray();
            Assert.AreEqual(replyOnly ? 0 : 1, prompts.Length);
            if (!replyOnly)
            {
                StringAssert.Contains(prompts[0], "assignment_id task-mode");
                Assert.IsFalse(prompts[0].Contains("secret reply", StringComparison.Ordinal));
            }
            Assert.AreEqual(replyOnly ? 2 : 1, client.Approvals.Count);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public void ReminderSettlementAndNoticeAreAtomicAndIdempotent()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var box = new MailboxStore(path);
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var generation = (string)box.FindAssignment("task", "owner")!["generation"]!;
        var binding = new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), "prompt",
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), 1, new string('A', 64),
            "host", "generation", null, null);
        var attempt = Guid.NewGuid().ToString("N");
        Assert.IsTrue(box.ClaimCompletionReminder("delegation", "task", "owner", "child", generation, 1, attempt, binding).Acquired);
        Assert.IsTrue(box.RecordCoordinatorReport("task", generation, "child", "owner", "report-id"));
        Assert.IsTrue(box.SettleCompletionReminder("delegation", "task", "owner", "child", generation, 1, attempt, CompletionReminderSettlement.NativeEnded));
        var first = box.ReadManagedQueue("owner", 8).Single();
        Assert.AreEqual("reminder_ended", (string?)first["kind"]);
        Assert.AreEqual("report-id", (string?)first["reportId"]);
        box = new MailboxStore(path);
        Assert.IsTrue(box.SettleCompletionReminder("delegation", "task", "owner", "child", generation, 1, attempt, CompletionReminderSettlement.NativeEnded));
        Assert.AreEqual((long)first["sequence"]!, (long)box.ReadManagedQueue("owner", 8).Single()["sequence"]!);
        Assert.AreEqual("open", (string?)box.FindAssignment("task", "owner")!["status"]);
        Assert.AreEqual(1L, (long)box.FindAssignment("task", "owner")!["revision"]!);
        box.TransitionAssignment("task", "owner", 1, "cancel", "cancelled", "cancel", generation: generation);
        Assert.AreEqual(0, box.ReadManagedQueue("owner", 8).Count, "an unstarted escalation is superseded");
    }

    [TestMethod]
    public void ReverseReportAcknowledgementsKeepLatestAllocatedReportAndNotice()
    {
        var directory = TestDirs.New(); var path = Path.Combine(directory, "mailbox.json");
        var box = new MailboxStore(path); var reports = new ReportStore(Path.Combine(directory, "reports"));
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var gen = (string)box.FindAssignment("task", "child")!["generation"]!;
        var initial = reports.Reserve(new ReportIdentity("initial", "child", "owner", ReportCause.Delegation,
            "initial-prompt", "task", "initial-message", AssignmentGeneration: gen)).ReportId;
        var resumed = reports.Reserve(new ReportIdentity("reply", "child", "owner", ReportCause.Reply,
            "reply-prompt", "task", "reply-message", AssignmentGeneration: gen,
            ReplyWaitId: "wait", ReplyWaitSequence: 3, ReplyMessageSequence: 4)).ReportId;
        Assert.IsTrue(ReportStore.TryCompareReportOrder(resumed, initial, out var order)); Assert.IsTrue(order > 0);
        var foreign = (initial[0] == '0' ? "1" : "0") + initial[1..];
        Assert.IsFalse(ReportStore.TryCompareReportOrder(foreign, initial, out _), "different store incarnations cannot be ordered");
        foreach (var id in new[] { initial, resumed })
        {
            using (var capture = reports.OpenCapture(id)) { capture.Append(id); capture.Seal("ended"); reports.Commit(id, capture); }
            reports.AcknowledgeProducer(id);
        }
        box.TransitionAssignment("task", "child", 1, "done", "completed", "finished", generation: gen);
        Assert.IsTrue(box.RecordCoordinatorReport("task", gen, "child", "owner", resumed, resume: true));
        Assert.IsTrue(box.RecordCoordinatorReport("task", gen, "child", "owner", initial));
        box = new MailboxStore(path); var row = box.FindAssignment("task", "owner")!;
        Assert.AreEqual(resumed, (string?)row["latestReportId"]);
        Assert.AreEqual(true, (bool?)row["coordinatorReportObserved"], "older initial report fact is still recorded");
        Assert.AreEqual(resumed, (string?)box.ReadManagedQueue("owner", 8).Single()["reportId"]);
        Assert.IsTrue(box.RecordCoordinatorReport("task", gen, "child", "owner", initial));
        Assert.IsTrue(box.RecordCoordinatorReport("task", gen, "child", "owner", foreign));
        Assert.AreEqual(resumed, (string?)box.FindAssignment("task", "owner")!["latestReportId"]);
        Assert.AreEqual(resumed, (string?)box.ReadManagedQueue("owner", 8).Single()["reportId"]);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReportAckAndExactBoundaryCommute(bool ackFirst)
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var box = new MailboxStore(path);
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var gen = (string)box.FindAssignment("task", "owner")!["generation"]!;
        void Ack() => Assert.IsTrue(box.RecordCoordinatorReport("task", gen, "child", "owner", "report"));
        void Boundary() => Assert.IsFalse(box.RecordCoordinatorInitialTurn("task", gen, "child", "owner", true, "host", "build", 42));
        if (ackFirst)
        {
            Ack(); var pending = box.FindAssignment("task", "owner")!;
            Assert.IsNull(pending["coordinatorInitialTurnSettled"]); Assert.IsNull(pending["coordinatorRouteEpoch"]);
            Assert.AreEqual(0, box.CoordinatorSupervisionInventory().Length);
            Boundary();
        }
        else { Boundary(); Ack(); }
        box = new MailboxStore(path); var row = box.FindAssignment("task", "owner")!;
        Assert.AreEqual("report", (string?)row["latestReportId"]);
        Assert.AreEqual(42L, (long?)row["coordinatorRouteEpoch"]);
        Assert.AreEqual("host", (string?)row["coordinatorHostId"]);
        Assert.AreEqual(0, box.ReadManagedQueue("owner", 8).Count);
    }

    [TestMethod]
    public void ExactBoundaryRepairsEarlierIncompleteMarker()
    {
        var box = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"));
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var gen = (string)box.FindAssignment("task", "owner")!["generation"]!;
        Assert.IsFalse(box.RecordCoordinatorInitialTurn("task", gen, "child", "owner", true, "host", "build"));
        Assert.IsNull(box.FindAssignment("task", "owner")!["coordinatorRouteEpoch"]);
        Assert.IsFalse(box.RecordCoordinatorInitialTurn("task", gen, "child", "owner", true, "host", "build", 42));
        Assert.AreEqual(42L, (long?)box.FindAssignment("task", "owner")!["coordinatorRouteEpoch"]);
        Assert.AreEqual(0, box.ReadManagedQueue("owner", 8).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReportWithoutBoundaryRecoveryNeverAuthorizesAuto(bool terminalNotEnded)
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var box = new MailboxStore(path);
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var gen = (string)box.FindAssignment("task", "owner")!["generation"]!;
        Assert.IsTrue(box.RecordCoordinatorReport("task", gen, "child", "owner", "report", terminalNotEnded: terminalNotEnded));
        box = new MailboxStore(path);
        if (!terminalNotEnded) Assert.IsTrue(box.RecoverCoordinatorReportWithoutBoundary("task", gen, "child", "owner"));
        Assert.AreEqual("report_only", (string?)box.ReadManagedQueue("owner", 8).Single()["kind"]);
        Assert.AreEqual(0, box.CompletionAutoCandidates(null, 16).Length);
        var binding = new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), "prompt",
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), 1, new string('A', 64), "host", "generation", null, null);
        Assert.IsFalse(box.ClaimCompletionReminder("delegation", "task", "owner", "child", gen, 1,
            Guid.NewGuid().ToString("N"), binding, automatic: true).Acquired);
        var manual = Guid.NewGuid().ToString("N");
        Assert.IsTrue(box.ClaimCompletionReminder("delegation", "task", "owner", "child", gen, 1,
            manual, binding).Acquired, "explicit manual path remains available");
        Assert.IsTrue(box.SettleCompletionReminder("delegation", "task", "owner", "child", gen, 1, manual, CompletionReminderSettlement.NativeEnded));
        Assert.AreEqual(1, box.ReadManagedQueue("owner", 8).Count, "manual settlement must not duplicate the earlier factual fallback");
    }

    [TestMethod]
    public async Task FallbackAndAutomaticClaimAreMutuallyExclusiveAtMailboxCommit()
    {
        var box = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"));
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var gen = (string)box.FindAssignment("task", "owner")!["generation"]!;
        box.RecordCoordinatorInitialTurn("task", gen, "child", "owner", true, "host", "build", 4);
        var binding = new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), "prompt",
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), 1, new string('A', 64), "host", "generation", null, null);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fallback = Task.Run(async () => { await start.Task; return box.RecordCoordinatorInitialTurn("task", gen, "child", "owner", false); });
        var claim = Task.Run(async () => { await start.Task; return box.ClaimCompletionReminder("delegation", "task", "owner", "child", gen, 1,
            Guid.NewGuid().ToString("N"), binding, automatic: true).Acquired; });
        start.TrySetResult(); await Task.WhenAll(fallback, claim);
        Assert.AreNotEqual(fallback.Result, claim.Result, "exactly one mailbox publication wins");
        Assert.AreEqual(fallback.Result ? 1 : 0, box.ReadManagedQueue("owner", 8).Count);
    }

    [TestMethod]
    public void PreviouslySupervisedTurnCanBeRecoveredOnceAfterReopen()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var box = new MailboxStore(path);
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var generation = (string)box.FindAssignment("task", "owner")!["generation"]!;
        Assert.IsFalse(box.RecordCoordinatorInitialTurn("task", generation, "child", "owner", supervision: true));
        box = new MailboxStore(path);
        Assert.AreEqual(1, box.CoordinatorSupervisionInventory().Length);
        Assert.IsTrue(box.RecordCoordinatorInitialTurn("task", generation, "child", "owner", supervision: false));
        var notice = box.ReadManagedQueue("owner", 8).Single();
        Assert.AreEqual("unsupervised", (string?)notice["kind"]);
        Assert.IsTrue(box.TryStartCoordinatorBatch([notice])); box.CompleteCoordinatorBatch([notice]);
        box = new MailboxStore(path);
        Assert.IsFalse(box.RecordCoordinatorInitialTurn("task", generation, "child", "owner", supervision: false));
        Assert.AreEqual(0, box.CoordinatorSupervisionInventory().Length);
        Assert.AreEqual(0, box.ReadManagedQueue("owner", 8).Count);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ReopenWithoutSupervisionQueuesFallbackUntilArchiveRestore()
    {
        var data = TestDirs.New(); var agent = new LifecycleAgent("kimi", canDelete: true) { LoadsWithoutIds = true };
        string parent, child, generation;
        await using (var first = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "fallback-restart" }, [agent.Adapter]))
        await using (var client = await TestClient.ConnectAsync(first))
        {
            parent = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = Path.GetTempPath() })).SessionId;
            child = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = Path.GetTempPath() })).SessionId;
            first.Store.Update(parent, "kimi", r => r.Archived = true, durable: true);
            var box = first.ReplyMailbox; box.Accept("restart-task", parent, child, "work", delegation: true, coordinatorLifecycle: true);
            box.MarkStarted("restart-task", child); box.MarkDelivered("restart-task", child);
            generation = (string)box.FindAssignment("restart-task", parent)!["generation"]!;
            Assert.IsFalse(box.RecordCoordinatorInitialTurn("restart-task", generation, child, parent, supervision: true));
        }
        await using var reopened = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "fallback-restart" }, [agent.Adapter]);
        await using var next = await TestClient.ConnectAsync(reopened);
        var notice = reopened.ReplyMailbox.ReadManagedQueue(parent, 8).Single();
        Assert.AreEqual("unsupervised", (string?)notice["kind"]);
        lock (agent.Prompts) Assert.AreEqual(0, agent.Prompts.Count(p => p.SessionId == parent));
        await next.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = parent, ["archived"] = false });
        await LinkHarness.Until(() => reopened.ReplyMailbox.ReadManagedQueue(parent, 8).Count == 0, "fallback after restore").WaitAsync(Wait);
        lock (agent.Prompts) Assert.AreEqual(1, agent.Prompts.Count(p => p.SessionId == parent));
        Assert.AreEqual("open", (string?)reopened.ReplyMailbox.FindAssignment("restart-task", parent)!["status"]);
    }

    [TestMethod]
    public void UnknownClosureAndLateTerminalProduceOnlyOneNotice()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var box = new MailboxStore(path);
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var generation = (string)box.FindAssignment("task", "owner")!["generation"]!;
        var binding = new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), "prompt",
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), 1, new string('A', 64), "host", "generation", null, null);
        var attempt = Guid.NewGuid().ToString("N");
        Assert.IsTrue(box.ClaimCompletionReminder("delegation", "task", "owner", "child", generation, 1, attempt, binding).Acquired);
        Assert.IsTrue(box.CloseCompletionReminderUnknown("delegation", "task", "owner", "child", generation, 1, attempt));
        var notice = box.ReadManagedQueue("owner", 8).Single();
        Assert.AreEqual("reminder_unavailable", (string?)notice["kind"]);
        Assert.IsTrue(box.TryStartCoordinatorBatch([notice])); box.CompleteCoordinatorBatch([notice]);
        box = new MailboxStore(path);
        Assert.IsTrue(box.SettleCompletionReminder("delegation", "task", "owner", "child", generation, 1, attempt, CompletionReminderSettlement.NativeEnded));
        Assert.AreEqual(0, box.ReadManagedQueue("owner", 8).Count);
        Assert.AreEqual("open", (string?)box.FindAssignment("task", "owner")!["status"]);
    }

    [TestMethod]
    [DataRow("continuation")]
    [DataRow("completed")]
    [DataRow("cancelled")]
    public void ChangedAssignmentSuppressesLateReminderEscalation(string change)
    {
        var box = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"));
        box.Accept("task", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("task", "child"); box.MarkDelivered("task", "child");
        var generation = (string)box.FindAssignment("task", "owner")!["generation"]!;
        var binding = new CompletionReminderBinding("project", "group", Guid.NewGuid().ToString("N"), "prompt",
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), 1, new string('A', 64), "host", "generation", null, null);
        var attempt = Guid.NewGuid().ToString("N");
        Assert.IsTrue(box.ClaimCompletionReminder("delegation", "task", "owner", "child", generation, 1, attempt, binding).Acquired);
        if (change == "continuation")
        {
            box.Accept("wait", "child", "peer", "question", expectReply: true);
            var target = box.ContinuationWait("wait", "child")!; target["kind"] = "reply_wait";
            target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
            box.UpdateContinuation("task", "child", generation, 1, "register", "register", target);
        }
        else box.TransitionAssignment("task", change == "cancelled" ? "owner" : "child", 1, "end", change, "result", generation: generation);
        Assert.IsTrue(box.SettleCompletionReminder("delegation", "task", "owner", "child", generation, 1, attempt, CompletionReminderSettlement.NativeEnded));
        var notices = box.ReadManagedQueue("owner", 8);
        Assert.IsFalse(notices.Any(n => (string?)n["kind"] == "reminder_ended"));
        Assert.AreEqual(change == "completed" ? 1 : 0, notices.Count);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ExactDelegationReportRemainsReadableWithoutASecondWake()
    {
        var agent = new LifecycleAgent("kimi", canDelete: true) { Answer = "full retained report" };
        var hosts = new InProcessHosts(_ => agent.Adapter);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "retained",
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        hosts.Service = service; await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client);
        service.Store.Update(child, "kimi", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        service.Store.Update(parent, "kimi", r => r.Archived = true);
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "work",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent],
                [GorynychProtocol.MetaMessageRequestId] = "retained-task" } });
        await LinkHarness.Until(() => service.ReportStorage.List(child).Items.Any(r => r.Identity.AssignmentId == "retained-task" &&
            r.ProducerAck && r.Status == "retained"), "retained report").WaitAsync(Wait);
        var report = service.ReportStorage.List(child).Items.Single(r => r.Identity.AssignmentId == "retained-task");
        await LinkHarness.Until(() => service.ReplyMailbox.ReadManagedQueue(parent, 8).Count == 1, "terminal fallback recorded").WaitAsync(Wait);
        var view = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadAssignment, new JsonObject {
            ["source"] = "delegation", ["assignmentId"] = "retained-task",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } });
        Assert.AreEqual(report.ReportId, (string?)view!["latestReportId"]);
        StringAssert.Contains(service.ReportStorage.Read(report.ReportId, parent).Text, "full retained report");
        Assert.AreEqual("retained", service.ReportStorage.GetReport(report.ReportId).Status);
        Assert.AreEqual(0, agent.Prompts.Count(p => p.Item1 == parent), "archived parent was not woken by report");
        Assert.AreEqual("queued", (string?)view["outcomeNotification"]!["delivery"]);
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = parent, ["archived"] = false });
        await LinkHarness.Until(() => service.ReplyMailbox.ReadManagedQueue(parent, 8).Count == 0, "restored notice delivered").WaitAsync(Wait);
        Assert.AreEqual(1, agent.Prompts.Count(p => p.Item1 == parent), "restore emits one notice, not a second report wake");
        StringAssert.Contains(agent.Prompts.Single(p => p.Item1 == parent).Item2, report.ReportId);
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ExactReplyResumesAssignmentAndRetainsReportWithoutSecondWake(bool ambiguous, bool blocked, bool blockedBeforeClaim)
    {
        var agent = new LifecycleAgent("kimi", canDelete: true) { Answer = "full resumed report" };
        var hosts = new InProcessHosts(_ => agent.Adapter);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "resume-report",
            Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        hosts.Service = service; await using var client = await TestClient.ConnectAsync(service);
        var parent = await HostTests.NewSession(client); var child = await HostTests.NewSession(client); var peer = await HostTests.NewSession(client);
        service.Store.Update(child, "kimi", r => { r.ParentSessionId = parent; r.ReportToParent = true; });
        service.Store.Update(parent, "kimi", r => r.Archived = true);
        string? generation = null; var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.DuringPrompt = async (_, sid) =>
        {
            if (sid != child) return;
            string text; lock (agent.Prompts) text = agent.Prompts.Last(p => p.SessionId == child).Text;
            if (text.Contains("initial-delegation", StringComparison.Ordinal))
            {
                await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestReply, new JsonObject { ["sessionId"] = peer, ["text"] = "question",
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child], [GorynychProtocol.MetaMessageRequestId] = "wait-exact" } });
                generation = (string)service.ReplyMailbox.FindAssignment("task-resume", child)!["generation"]!;
                await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateContinuation, new JsonObject { ["source"] = "delegation", ["assignmentId"] = "task-resume",
                    ["generation"] = generation, ["expectedRevision"] = 1, ["operationId"] = "register", ["action"] = "register", ["messageId"] = "wait-exact",
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } });
                registered.TrySetResult();
            }
            else if (text.Contains("reply-exact", StringComparison.Ordinal) && !blockedBeforeClaim)
                await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, new JsonObject { ["source"] = "delegation", ["assignmentId"] = "task-resume",
                    ["generation"] = generation, ["expectedRevision"] = 2, ["operationId"] = "complete", ["outcome"] = blocked ? "blocked" : "completed", ["summary"] = "finished after reply",
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } });
        };
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodDelegateTask, new JsonObject { ["sessionId"] = child, ["text"] = "initial-delegation",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent], [GorynychProtocol.MetaMessageRequestId] = "task-resume" } });
        await registered.Task.WaitAsync(Wait);
        await LinkHarness.Until(() => (string?)service.ReplyMailbox.FindAssignment("task-resume", parent)?["delivery"] == "delivered" &&
            service.ReportStorage.List(child).Items.Any(r => r.Identity.Cause == ReportCause.Delegation && r.Status == "retained"), "initial turn retained").WaitAsync(Wait);
        if (ambiguous)
        {
            var box = service.ReplyMailbox;
            box.Accept("second-task", parent, child, "other work", delegation: true, coordinatorLifecycle: true);
            box.MarkStarted("second-task", child); box.MarkDelivered("second-task", child);
            var second = (string)box.FindAssignment("second-task", child)!["generation"]!;
            var target = box.ContinuationWait("wait-exact", child)!; target["kind"] = "reply_wait";
            target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
            box.UpdateContinuation("second-task", child, second, 1, "second-register", "register", target);
        }
        var beforeClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (blockedBeforeClaim) service.BeforeCoordinatorModeWaitForTests = async () => { beforeClaim.TrySetResult(); await releaseClaim.Task; };
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReplyMessage, new JsonObject { ["messageId"] = "wait-exact", ["text"] = "answer",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[peer], [GorynychProtocol.MetaMessageRequestId] = "reply-exact" } });
        if (blockedBeforeClaim)
        {
            try
            {
                await beforeClaim.Task.WaitAsync(Wait);
                await client.Rpc.SendRequestAsync(GorynychProtocol.MethodUpdateAssignment, new JsonObject { ["source"] = "delegation", ["assignmentId"] = "task-resume",
                    ["generation"] = generation, ["expectedRevision"] = 2, ["operationId"] = "block-before-claim", ["outcome"] = "blocked", ["summary"] = "finished after reply",
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[child] } });
            }
            finally { service.BeforeCoordinatorModeWaitForTests = null; releaseClaim.TrySetResult(); }
        }
        await LinkHarness.Until(() => service.ReportStorage.List(child).Items.Any(r => r.Identity.Cause == ReportCause.Reply &&
            (ambiguous || blockedBeforeClaim ? r.Status == "pending" : r.Status == "retained")), "reply report recorded").WaitAsync(Wait);
        var report = service.ReportStorage.List(child).Items.Single(r => r.Identity.Cause == ReportCause.Reply);
        if (ambiguous)
        {
            Assert.IsNull(report.Identity.AssignmentId); Assert.IsNull(report.Identity.ReplyWaitId);
        }
        else
        {
            Assert.AreEqual("task-resume", report.Identity.AssignmentId); Assert.AreEqual(generation, report.Identity.AssignmentGeneration);
            Assert.AreEqual("wait-exact", report.Identity.ReplyWaitId); Assert.IsTrue(report.Identity.ReplyWaitSequence > 0);
            Assert.IsTrue(report.Identity.ReplyMessageSequence > 0);
        }
        StringAssert.Contains(service.ReportStorage.Read(report.ReportId, parent).Text, "full resumed report");
        var view = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReadAssignment, new JsonObject { ["source"] = "delegation", ["assignmentId"] = "task-resume",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.Tokens[parent] } });
        Assert.AreEqual(blocked ? "blocked" : "completed", (string?)view!["status"]);
        if (!ambiguous && !blockedBeforeClaim) Assert.AreEqual(report.ReportId, (string?)view["latestReportId"]);
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = parent, ["archived"] = false });
        await LinkHarness.Until(() => service.ReplyMailbox.ReadManagedQueue(parent, 8).Count == 0, "one owner outcome").WaitAsync(Wait);
        if (ambiguous || blockedBeforeClaim) await LinkHarness.Until(() => service.ReportStorage.GetReport(report.ReportId).Status == "completed", "legacy report delivered").WaitAsync(Wait);
        string[] prompts; lock (agent.Prompts) prompts = agent.Prompts.Where(p => p.SessionId == parent).Select(p => p.Text).ToArray();
        Assert.AreEqual(ambiguous || blockedBeforeClaim ? 2 : 1, prompts.Length);
        Assert.AreEqual(1, prompts.Count(text => text.Contains("finished after reply", StringComparison.Ordinal)));
        Assert.AreEqual(ambiguous || blockedBeforeClaim ? 1 : 0, prompts.Count(text => text.StartsWith("[Report", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ReplyProvenanceRejectsAmbiguityAndStaleWaitSequence()
    {
        var box = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"));
        box.Accept("a", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("a", "child"); box.MarkDelivered("a", "child");
        box.Accept("wait", "child", "peer", "question", expectReply: true);
        var gen = (string)box.FindAssignment("a", "child")!["generation"]!;
        var target = box.ContinuationWait("wait", "child")!; target["kind"] = "reply_wait";
        target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
        box.UpdateContinuation("a", "child", gen, 1, "register", "register", target);
        box.Accept("reply", "peer", "child", "answer", replyTo: "wait");
        var reply = box.ReadManagedQueue("child", 8).Single(x => (string?)x["id"] == "reply");
        var exact = box.ResolveCoordinatorReply([reply], "owner"); Assert.IsNotNull(exact);
        Assert.IsNull(box.ResolveCoordinatorReply([reply], "foreign"));
        var changed = reply.DeepClone().AsObject(); changed["sequence"] = exact.ReplySequence + 1;
        Assert.IsFalse(box.TryStartCoordinatorBatch([changed], exact, "owner"));
        box.UpdateContinuation("a", "child", gen, 2, "withdraw", "withdraw", null);
        Assert.IsFalse(box.TryStartCoordinatorBatch([reply], exact, "owner"), "withdrawal before handoff invalidates the captured provenance");
        Assert.IsFalse((bool?)box.ReadManagedQueue("child", 8).Single(x => (string?)x["id"] == "reply")["started"] == true);
        box.UpdateContinuation("a", "child", gen, 3, "register-again", "register", target.DeepClone().AsObject());
        box.Accept("b", "owner", "child", "work", delegation: true, coordinatorLifecycle: true);
        box.MarkStarted("b", "child"); box.MarkDelivered("b", "child");
        var gen2 = (string)box.FindAssignment("b", "child")!["generation"]!;
        box.UpdateContinuation("b", "child", gen2, 1, "register-b", "register", target.DeepClone().AsObject());
        Assert.IsNull(box.ResolveCoordinatorReply([reply], "owner"), "two assignments waiting on the same reply are ambiguous");
        Assert.IsNull(box.ResolveCoordinatorReply([reply, reply.DeepClone().AsObject()], "owner"), "multi-item batch remains legacy");
    }

    [TestMethod]
    public void RetainedReportRespectsBoundedRetention()
    {
        var store = new ReportStore(TestDirs.New(), bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 1, pinnedLimit: 1);
        var identity = new ReportIdentity("turn", "child", "parent", ReportCause.Delegation, "prompt", "task", "message",
            AssignmentGeneration: Guid.NewGuid().ToString("N"));
        var id = store.Reserve(identity).ReportId;
        using (var capture = store.OpenCapture(id))
        {
            capture.Append("full report"); capture.Seal("ended"); store.Commit(id, capture);
        }
        store.AcknowledgeProducer(id); store.SetDisposition(id, "retained", "available");
        Assert.AreEqual("full report", store.Read(id, "parent").Text);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => store.Read(id, "foreign"));
        var replacement = store.Reserve(identity with { TurnKey = "new-turn" }).ReportId;
        Assert.AreNotEqual(id, replacement);
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.Read(id, "parent"));
    }
}
