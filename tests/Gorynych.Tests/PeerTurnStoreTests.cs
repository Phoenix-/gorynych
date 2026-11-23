using System.Text.Json.Nodes;
using Gorynych.Service;
using Gorynych.Acp.Hosting;

namespace Gorynych.Tests;

[TestClass]
public sealed class PeerTurnStoreTests
{
    static System.Text.Json.Nodes.JsonObject Settle(MailboxStore box, PeerTurnIdentity turn, string outcome, string? text,
        string? witnessedAcceptedId = null, bool finalTextTruncated = false, string? reportId = null) =>
        box.SettlePeerTurn(turn, outcome, text, witnessedAcceptedId, finalTextTruncated, reportId, producerEvidenceComplete: true);

    static (MailboxStore Box, PeerTurnIdentity Turn, string Path) Start()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json"); var box = new MailboxStore(path);
        var item = box.Accept("trigger", "sender", "worker", "question");
        var turn = new PeerTurnIdentity("trigger", (long)item["sequence"]!, "sender", "worker", "prompt-1");
        Assert.IsTrue(box.TryMarkStarted("trigger", "worker", turn.Sequence, turn));
        box.MarkDelivered("trigger", "worker"); return (box, turn, path);
    }

    [TestMethod]
    public void ExactExplicitReplySuppressesOnlyThatTurn()
    {
        var (box, turn, path) = Start();
        box.Accept("older", "sender", "worker", "old"); box.MarkStarted("older", "worker"); box.MarkDelivered("older", "worker");
        box.Accept("old-reply", "worker", "sender", "old answer", replyTo: "older");
        box.Accept("ordinary", "worker", "sender", "extra");
        box.Accept("exact-reply", "worker", "sender", "explicit", replyTo: "trigger");
        Assert.AreEqual("explicit_reply_accepted", (string?)Settle(box, turn, "ended", "final", "exact-reply")["state"]);
        box = new MailboxStore(path);
        Assert.AreEqual("explicit_reply_accepted", (string?)Settle(box, turn, "ended", "final", "exact-reply")["state"]);
        Assert.AreEqual(0, box.Read("sender", 0, 20, false).Count(x => (bool?)x["peerAutoReply"] == true));
    }

    [TestMethod]
    public void NativeEvidenceMustMatchPreviouslyAcceptedExactMailboxReply()
    {
        var (box, turn, path) = Start();
        box.Accept("older", "sender", "worker", "old"); box.MarkStarted("older", "worker"); box.MarkDelivered("older", "worker");
        box.Accept("other", "worker", "sender", "old answer", replyTo: "older");
        box.Accept("exact", "worker", "sender", "answer", replyTo: "trigger");
        Assert.AreEqual("unknown", (string?)Settle(box, turn with { PromptId = "other" }, "ended", "answer", "exact")["state"]);
        Assert.AreEqual("explicit_reply_accepted", (string?)Settle(box, turn, "ended", "answer", "exact")["state"]);
        box = new MailboxStore(path);
        Assert.AreEqual("explicit_reply_accepted", (string?)Settle(box, turn, "ended", "answer", "exact")["state"]);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void EmptySuccessfulTerminalSettlesWithoutEmptyAutoReply(bool explicitReply)
    {
        var (box, turn, path) = Start();
        if (explicitReply) box.Accept("explicit", "worker", "sender", "answer", replyTo: "trigger");
        var state = explicitReply ? "explicit_reply_accepted" : "ended_without_text";
        Assert.AreEqual(state, (string?)Settle(box, turn, "ended", null, explicitReply ? "explicit" : null)["state"]);
        box = new MailboxStore(path);
        Assert.AreEqual(state, (string?)Settle(box, turn, "ended", "", explicitReply ? "explicit" : null)["state"]);
        Assert.AreEqual(0, box.Read("sender", 0, 20, false).Count(x => (bool?)x["peerAutoReply"] == true));
        box.CapacityForTests = explicitReply ? 2 : 1;
        box.Accept("later", "sender", "worker", "new work");
    }

    [TestMethod]
    public void LongReportUsesSharedBoundedPreviewAndFullHistoryRemainsReadable()
    {
        var (box, turn, _) = Start(); var directory = TestDirs.New();
        var reports = new ReportStore(directory, bodyLimit: 65536, totalLimit: 4 * 1024 * 1024);
        var id = reports.Reserve(new ReportIdentity("turn-1", "worker", "sender", ReportCause.Delegation, "prompt", "assignment", "message")).ReportId;
        var text = new string('x', 1999) + char.ConvertFromUtf32(0x1F600) + new string('y', 38000);
        using (var capture = reports.OpenCapture(id)) { capture.Append(text); capture.Seal("ended"); reports.Commit(id, capture); }
        var preview = reports.Preview(id, "sender"); Assert.IsTrue(preview.Truncated);
        var result = Settle(box, turn, "ended", text);
        var reply = box.Read("sender", 0, 20, false).Single(x => (string?)x["id"] == (string?)result["automaticReplyId"]);
        Assert.AreEqual(preview.Text + "\n\n[Response truncated.]", (string?)reply["text"]); Assert.AreEqual(1999, preview.Text.Length);
        Assert.IsTrue((bool?)result["previewTruncated"]);
        var pieces = new System.Text.StringBuilder(); string? cursor = null;
        do { var page = reports.Read(id, "sender", cursor, limit: 16384); pieces.Append(page.Text); cursor = page.NextCursor; } while (cursor is not null);
        Assert.AreEqual(text, pieces.ToString());
    }

    [TestMethod]
    public void AlreadyLimitedReportPreviewRetainsTruncationAcrossReopen()
    {
        var (box, turn, path) = Start(); var text = new string('x', 2000);
        var result = Settle(box, turn, "ended", text, finalTextTruncated: true);
        var reply = box.Read("sender", 0, 20, false).Single(x => (string?)x["id"] == (string?)result["automaticReplyId"]);
        Assert.AreEqual(text + "\n\n[Response truncated.]", (string?)reply["text"]);
        box = new MailboxStore(path);
        Assert.IsTrue((bool?)Settle(box, turn, "ended", text, finalTextTruncated: true)["previewTruncated"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => Settle(box, turn, "ended", text));
    }

    [TestMethod]
    public void ReusedMessageIdRejectsOlderAcceptedReplySequence()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var box = new MailboxStore(path) { CapacityForTests = 2 };
        var old = box.Accept("reused", "sender", "worker", "old");
        Assert.IsTrue(box.TryMarkStarted("reused", "worker")); box.MarkDelivered("reused", "worker");
        var oldReply = box.Accept("old-reply", "worker", "sender", "old answer", replyTo: "reused");
        box.Accept("padding", "sender", "worker", "padding");
        Assert.IsTrue(box.TryMarkStarted("padding", "worker")); box.MarkDelivered("padding", "worker");
        var fresh = box.Accept("reused", "sender", "worker", "new");
        Assert.IsTrue((long)old["sequence"]! < (long)oldReply["sequence"]! && (long)oldReply["sequence"]! < (long)fresh["sequence"]!);
        var turn = new PeerTurnIdentity("reused", (long)fresh["sequence"]!, "sender", "worker", "new-prompt");
        Assert.IsTrue(box.TryMarkStarted("reused", "worker", turn.Sequence, turn)); box.MarkDelivered("reused", "worker");
        // Make room without dropping the older reply before the settlement check.
        box.CapacityForTests = 3;
        Assert.AreEqual("unknown", (string?)Settle(box, turn, "ended", "new answer", "old-reply")["state"]);
        Assert.AreEqual("automatic_reply_accepted", (string?)Settle(box, turn, "ended", "new answer")["state"]);
        Assert.AreEqual(1, box.Read("sender", 0, 20, false).Count(x => (bool?)x["peerAutoReply"] == true));
    }

    [TestMethod]
    public void SameSessionReplyWithoutNativeWitnessAndForeignEvidenceDoNotSuppress()
    {
        foreach (var witness in new string?[] { null, "missing", "older-reply" })
        {
            var (box, turn, _) = Start();
            box.Accept("older", "sender", "worker", "old"); box.MarkStarted("older", "worker"); box.MarkDelivered("older", "worker");
            box.Accept("older-reply", "worker", "sender", "old reply", replyTo: "older");
            box.Accept("same-session", "worker", "sender", "unrelated process", replyTo: "trigger");
            Assert.AreEqual(witness is null ? "automatic_reply_accepted" : "unknown", (string?)Settle(box, turn, "ended", "final", witness)["state"]);
        }
    }

    [TestMethod]
    public void OtherReplyAndOrdinaryMessageDoNotSuppressAndAutoReplyDoesNotLoop()
    {
        var (box, turn, path) = Start();
        box.Accept("older", "sender", "worker", "old"); box.MarkStarted("older", "worker"); box.MarkDelivered("older", "worker");
        box.Accept("old-reply", "worker", "sender", "old answer", replyTo: "older");
        box.Accept("ordinary", "worker", "sender", "extra");
        var result = Settle(box, turn, "ended", "final answer");
        Assert.AreEqual("automatic_reply_accepted", (string?)result["state"]);
        var id = (string)result["automaticReplyId"]!;
        var auto = box.Read("sender", 0, 20, false).Single(x => (string?)x["id"] == id);
        Assert.AreEqual("worker", (string?)auto["sender"]); Assert.AreEqual("sender", (string?)auto["recipient"]);
        Assert.AreEqual("trigger", (string?)auto["replyTo"]); Assert.IsTrue((bool?)auto["peerAutoReply"]);
        var reverse = new PeerTurnIdentity(id, (long)auto["sequence"]!, "worker", "sender", "prompt-2");
        Assert.IsFalse(box.TryMarkStarted(id, "sender", reverse.Sequence, reverse));
        box = new MailboxStore(path);
        Assert.AreEqual(id, (string?)Settle(box, turn, "ended", "final answer")["automaticReplyId"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => Settle(box, turn, "ended", "different answer"));
        Assert.ThrowsExactly<InvalidOperationException>(() => Settle(box, turn, "cancelled", null));
        Assert.AreEqual(1, box.Read("sender", 0, 20, false).Count(x => (bool?)x["peerAutoReply"] == true));
    }

    [TestMethod]
    public async Task ExplicitReplyAndCompletionSerializeUnderOneLock()
    {
        var (box, turn, _) = Start(); var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = Task.Run(async () => { await start.Task; return box.Accept("explicit", "worker", "sender", "answer", replyTo: "trigger"); });
        var terminal = Task.Run(async () => { await start.Task; return Settle(box, turn, "ended", "final", "explicit"); });
        start.SetResult(); await Task.WhenAll(reply, terminal);
        var auto = box.Read("sender", 0, 20, false).Count(x => (bool?)x["peerAutoReply"] == true);
        Assert.AreEqual((string?)terminal.Result["state"] == "automatic_reply_accepted" ? 1 : 0, auto);
        Assert.AreEqual(1, box.Read("sender", 0, 20, false).Count(x => (string?)x["id"] == "explicit"));
    }

    [TestMethod]
    public void UntrustedOrStaleIdentityCannotSuppressOrSettle()
    {
        var (box, turn, _) = Start();
        var wrong = turn with { PromptId = "other" };
        box.Accept("not-trusted", "worker", "sender", "reply", replyTo: "trigger");
        Assert.AreEqual("unknown", (string?)Settle(box, wrong, "ended", "bad")["state"]);
        Assert.AreEqual("automatic_reply_accepted", (string?)Settle(box, turn, "ended", "final")["state"]);
    }

    [TestMethod]
    public void AcceptedReceiptSurvivesDeliveredReplyEvictionButStillNeedsWitness()
    {
        var (box, turn, path) = Start(); box.CapacityForTests = 2;
        box.Accept("explicit", "worker", "sender", "answer", replyTo: "trigger");
        Assert.IsTrue(box.TryMarkStarted("explicit", "sender")); box.MarkDelivered("explicit", "sender");
        box.Accept("filler", "sender", "worker", "filler");
        Assert.IsFalse(box.Read("sender", 0, 20, false).Any(e => (string?)e["id"] == "explicit"));
        box = new MailboxStore(path) { CapacityForTests = 2 };
        Assert.AreEqual("explicit_reply_accepted", (string?)Settle(box, turn, "ended", "final", "explicit")["state"]);
        Assert.AreEqual(0, box.Read("sender", 0, 20, false).Count(e => (bool?)e["peerAutoReply"] == true));
    }

    [TestMethod]
    public void AcceptanceReceiptOverflowRemainsUnknown()
    {
        var (box, turn, path) = Start(); box.CapacityForTests = 2;
        for (var i = 0; i < 17; i++)
        {
            var id = "explicit-" + i;
            box.Accept(id, "worker", "sender", "answer", replyTo: "trigger");
            Assert.IsTrue(box.TryMarkStarted(id, "sender")); box.MarkDelivered(id, "sender");
        }
        box = new MailboxStore(path) { CapacityForTests = 2 };
        Assert.AreEqual("unknown", (string?)Settle(box, turn, "ended", "final", "explicit-0")["state"]);
        Assert.AreEqual("unknown", (string?)box.SettlePeerTurn(turn, "ended", "final")["state"]);
        Assert.AreEqual("automatic_reply_accepted", (string?)Settle(box, turn, "ended", "final")["state"]);
        Assert.AreEqual(1, box.Read("sender", 0, 20, false).Count(e => (bool?)e["peerAutoReply"] == true));
    }

    [TestMethod]
    [DataRow("failed")]
    [DataRow("cancelled")]
    public void NonSuccessTerminalCanSettleDespiteAcceptanceOverflow(string outcome)
    {
        var (box, turn, path) = Start(); box.CapacityForTests = 2;
        for (var i = 0; i < 17; i++)
        {
            var id = "explicit-" + i;
            box.Accept(id, "worker", "sender", "answer", replyTo: "trigger");
            Assert.IsTrue(box.TryMarkStarted(id, "sender")); box.MarkDelivered(id, "sender");
        }
        box = new MailboxStore(path) { CapacityForTests = 2 };
        Assert.AreEqual(outcome, (string?)box.SettlePeerTurn(turn, outcome, null)["state"]);
        Assert.AreEqual(0, box.Read("sender", 0, 20, false).Count(e => (bool?)e["peerAutoReply"] == true));
    }

    [TestMethod]
    public void RetainedReportAckSurvivesExactMailboxSourceEvictionAndReopen()
    {
        var dir = TestDirs.New(); var path = Path.Combine(dir, "mailbox.json");
        var box = new MailboxStore(path, capacity: 2); var row = box.Accept("trigger", "sender", "worker", "question");
        var sequence = (long)row["sequence"]!;
        var turn = new PeerTurnIdentity("trigger", sequence, "sender", "worker", $"service-peer-{sequence}-trigger");
        Assert.IsTrue(box.TryMarkStarted("trigger", "worker", sequence, turn)); box.MarkDelivered("trigger", "worker");
        var reportPath = Path.Combine(dir, "reports"); var reports = new ReportStore(reportPath, bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 1, pinnedLimit: 1);
        var identity = new ReportIdentity($"peer:{sequence}:trigger", "worker", null, ReportCause.Peer, PromptId: turn.PromptId,
            MessageId: turn.MessageId, HostId: "host", HostGeneration: "generation", PeerRoute: turn);
        var id = reports.Reserve(identity).ReportId;
        using (var capture = reports.OpenCapture(id)) capture.Seal("ended");
        reports.RecordProducerEvidence(id, "host", "ended", null, null);
        using (var capture = reports.OpenCapture(id)) reports.Commit(id, capture);
        reports.AcknowledgeProducer(id);
        var settled = Settle(box, turn, "ended", "answer", reportId: id); var incarnation = (string)settled["mailboxIncarnation"]!;
        reports.RetainPeerReport(id, turn, incarnation);
        Assert.ThrowsExactly<InvalidOperationException>(() => reports.Reserve(new ReportIdentity("other", "worker", "sender", ReportCause.Peer)));
        Assert.IsFalse(box.AcknowledgePeerRetained(turn, id, Guid.NewGuid().ToString("N")));
        var canonical = File.ReadAllBytes(path); File.WriteAllText(path, "{invalid");
        Assert.IsFalse(box.AcknowledgePeerRetained(turn, id, incarnation)); File.WriteAllBytes(path, canonical);
        Assert.IsTrue(box.AcknowledgePeerRetained(turn, id, incarnation));
        box.Accept("pressure", "sender", "worker", "another");
        Assert.IsFalse(box.Read("worker", 0, 20, false).Any(e => (string?)e["id"] == "trigger"));
        box = new MailboxStore(path, capacity: 2);
        Assert.IsTrue(box.AcknowledgePeerRetained(turn, id, incarnation), "same canonical incarnation and durable eviction prove prior release");
        reports = new ReportStore(reportPath, bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 1, pinnedLimit: 1);
        reports.AcknowledgePeerMailbox(id, turn, incarnation);
        Assert.IsTrue(reports.GetReport(id).PeerMailboxAck);
        Assert.AreNotEqual(id, reports.Reserve(new ReportIdentity("other", "worker", "sender", ReportCause.Peer)).ReportId);
        Assert.AreEqual(1, box.Read("sender", 0, 20, false).Count(e => (bool?)e["peerAutoReply"] == true));
    }

    [TestMethod]
    public void MissingWitnessedAcceptanceCanCloseUnknownIdempotentlyWithoutOutbound()
    {
        var (box, turn, path) = Start(); var report = Guid.NewGuid().ToString("N");
        Assert.AreEqual("unknown", (string?)Settle(box, turn, "ended", "result", "missing", reportId: report)["state"]);
        var unknown = box.SettlePeerTerminalUnknown(turn, report, "ended", "accepted_unconfirmed", "missing");
        Assert.AreEqual("outcome_unknown", (string?)unknown["state"]); Assert.AreEqual(report, (string?)unknown["settlementPin"]);
        box = new MailboxStore(path);
        Assert.AreEqual("outcome_unknown", (string?)Settle(box, turn, "ended", "result", "missing", reportId: report)["state"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => box.SettlePeerTerminalUnknown(turn, report, "ended", "accepted_unconfirmed", "foreign"));
        Assert.AreEqual(0, box.Read("sender", 0, 20, false).Count(e => (bool?)e["peerAutoReply"] == true));
    }

    [TestMethod]
    public void DurableReportPinSurvivesReopenAndBlocksEveryMailboxEvictionPath()
    {
        var (box, turn, path) = Start(); var report = Guid.NewGuid().ToString("N");
        var result = Settle(box, turn, "ended", "answer", reportId: report);
        Assert.AreEqual("automatic_reply_accepted", (string?)result["state"]);
        box = new MailboxStore(path) { CapacityForTests = 2 };
        Assert.AreEqual(report, (string?)Settle(box, turn, "ended", "answer", reportId: report)["settlementPin"]);
        CollectionAssert.AreEqual(new[] { new PeerSettlementPin(turn, report) }, box.PeerSettlementPins().ToArray());
        Assert.AreEqual(0, box.PeerSettlementPins(turn.Sequence).Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => Settle(box, turn, "ended", "answer", reportId: Guid.NewGuid().ToString("N")));
        Assert.ThrowsExactly<InvalidOperationException>(() => Settle(box, turn, "ended", "answer", witnessedAcceptedId: "different", reportId: report));
        Assert.ThrowsExactly<InvalidOperationException>(() => box.Accept("new", "sender", "worker", "new work"));
        var auto = box.Read("sender", 0, 20, false).Single(x => (bool?)x["peerAutoReply"] == true);
        Assert.AreEqual((string?)result["automaticReplyId"], (string?)auto["id"]);
    }

    [TestMethod]
    public void CapacityNeverEvictsAnUnsettledStartedTurn()
    {
        var (box, turn, path) = Start(); box.CapacityForTests = 1;
        Assert.ThrowsExactly<InvalidOperationException>(() => box.Accept("new", "sender", "worker", "new work"));
        Assert.AreEqual("deferred", (string?)Settle(box, turn, "ended", "answer")["state"]);
        box = new MailboxStore(path);
        Assert.AreEqual("automatic_reply_accepted", (string?)Settle(box, turn, "ended", "answer")["state"]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedSettlementReconcilesBeforeAndAfterReplacement(bool afterReplace)
    {
        var (box, turn, path) = Start(); var writer = box.WriteFile;
        box.WriteFile = (file, json) => { if (afterReplace) AtomicFile.WriteAllText(file, json); throw new IOException("injected"); };
        Assert.ThrowsExactly<IOException>(() => Settle(box, turn, "ended", "answer"));
        box.WriteFile = writer;
        Assert.IsTrue(box.ReconcileCompletionPublication());
        var result = Settle(box, turn, "ended", "answer");
        Assert.AreEqual("automatic_reply_accepted", (string?)result["state"]);
        box = new MailboxStore(path);
        Assert.AreEqual(1, box.Read("sender", 0, 20, false).Count(x => (bool?)x["peerAutoReply"] == true));
    }

    [TestMethod]
    public void AcceptedExplicitReplySettlementAfterAmbiguousReplaceStillSuppresses()
    {
        var (box, turn, path) = Start(); box.Accept("explicit", "worker", "sender", "answer", replyTo: "trigger");
        var writer = box.WriteFile;
        box.WriteFile = (file, json) => { AtomicFile.WriteAllText(file, json); throw new IOException("lost acknowledgement"); };
        Assert.ThrowsExactly<IOException>(() => Settle(box, turn, "ended", "final", "explicit"));
        box.WriteFile = writer; Assert.IsTrue(box.ReconcileCompletionPublication());
        Assert.AreEqual("explicit_reply_accepted", (string?)Settle(box, turn, "ended", "final", "explicit")["state"]);
        box = new MailboxStore(path);
        Assert.AreEqual(0, box.Read("sender", 0, 20, false).Count(x => (bool?)x["peerAutoReply"] == true));
    }

    [TestMethod]
    [DataRow("failed")]
    [DataRow("cancelled")]
    public void NonSuccessfulOutcomeDoesNotCreateSuccessAnswer(string outcome)
    {
        var (box, turn, _) = Start();
        Assert.AreEqual(outcome, (string?)Settle(box, turn, outcome, null)["state"]);
        Assert.AreEqual(0, box.Read("sender", 0, 20, false).Count(x => (bool?)x["peerAutoReply"] == true));
    }
}
