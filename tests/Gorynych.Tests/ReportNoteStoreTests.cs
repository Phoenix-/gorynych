using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ReportNoteStoreTests
{
    [TestMethod]
    public void ReceiptSnapshotsAreDurableBoundedIdentityCheckedAndMonotone()
    {
        var dir = TestDirs.New(); var store = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(new ReportIdentity("turn", "child", "parent", ReportCause.User, "prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("result"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, "parent", "host", "generation", "note", digest);
        var uuid = Guid.NewGuid().ToString();
        JsonObject Facts(long revision, bool taken = false, string? result = null) => new() {
            ["reportId"] = id, ["attemptId"] = claim.AttemptId, ["nativeMessageId"] = uuid, ["revision"] = revision,
            ["writeStarted"] = true, ["writeCompleted"] = true, ["taken"] = taken, ["cycleResult"] = result };
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordNoteReceipt(id, "parent", "host", "generation", Facts(1)));
        store.StartDelivery(id, claim.AttemptId);
        store.WriteManifest = (_, _) => throw new IOException("host handoff fault");
        Assert.ThrowsExactly<IOException>(() => store.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "note"));
        store.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
        Assert.IsTrue(store.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "note"));
        Assert.IsFalse(store.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "note"));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "different"));
        store.RecordNoteReceipt(id, "parent", "host", "generation", Facts(1));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordNoteReceipt(id, "other", "host", "generation", Facts(2)));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordNoteReceipt(id, "parent", "wrong", "generation", Facts(2)));
        store.WriteManifest = (_, _) => throw new IOException("receipt write fault");
        Assert.ThrowsExactly<IOException>(() => store.RecordNoteReceipt(id, "parent", "host", "generation", Facts(2, true)));
        var reopened = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.AreEqual(false, (bool?)reopened.ReadNoteReceipt(id, "parent", "host", "generation", claim.AttemptId)!["taken"]);
        reopened.RecordNoteReceipt(id, "parent", "host", "generation", Facts(2, true, "ended"));
        reopened.RecordNoteReceipt(id, "parent", "host", "generation", Facts(1));
        var replayed = reopened.ReadNoteReceipt(id, "parent", "host", "generation", claim.AttemptId)!;
        Assert.AreEqual(true, (bool?)replayed["taken"]); Assert.AreEqual("ended", (string?)replayed["cycleResult"]);
        Assert.AreEqual(2L, (long?)replayed["revision"]);
        var later = Facts(3, true, "failed"); later["nativeError"] = "later error"; later["nativeCancelled"] = true; later["contradiction"] = true;
        reopened.RecordNoteReceipt(id, "parent", "host", "generation", later);
        var pending = Facts(4, true, "failed"); pending["nativeCancelled"] = true; pending["contradiction"] = true; pending["cancelReceiptPending"] = true;
        reopened.RecordNoteReceipt(id, "parent", "host", "generation", pending);
        reopened.RecordNoteReceipt(id, "parent", "host", "generation", Facts(2, true, "ended"));
        var final = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024).ReadNoteReceipt(id, "parent", "host", "generation", claim.AttemptId)!;
        Assert.AreEqual("ended", (string?)final["cycleResult"]); Assert.IsNull(final["nativeError"]);
        Assert.AreEqual(true, (bool?)final["contradiction"]); Assert.AreEqual(true, (bool?)final["cancelReceiptPending"]);
        var cleared = Facts(5, true, "ended"); cleared["nativeCancelled"] = true; cleared["contradiction"] = true; cleared["cancelReceiptPending"] = false;
        reopened.RecordNoteReceipt(id, "parent", "host", "generation", cleared);
        Assert.AreEqual(false, (bool?)reopened.ReadNoteReceipt(id, "parent", "host", "generation", claim.AttemptId)!["cancelReceiptPending"]);
        Assert.AreEqual("result", reopened.Read(id, "parent").Text);
        var tooLong = Facts(6); tooLong["nativeError"] = new string('x', 65);
        Assert.ThrowsExactly<ArgumentException>(() => reopened.RecordNoteReceipt(id, "parent", "host", "generation", tooLong));
        Assert.ThrowsExactly<ArgumentException>(() => reopened.RecordNoteReceipt(id, "parent", "host", "generation", Facts(6, false, "ended")));
    }
    [TestMethod]
    public void WorstEscapedNoteFactsFitReservedManifestHeadroom()
    {
        var store = new ReportStore(TestDirs.New(), bodyLimit: 1024, totalLimit: 1024 * 1024);
        var parent = new string('\u0001', 512);
        var id = store.Reserve(new ReportIdentity("worst", "child", parent, ReportCause.User, "prompt")).ReportId;
        using (var capture = store.OpenCapture(id))
        {
            capture.Append("result"); capture.SealIncomplete(new string('\u0002', 512), "failed", new string('\u0003', 512));
            store.CommitUnconfirmed(id, capture);
        }
        store.AcknowledgeProducer(id);
        var host = new string('\u0004', 128); var generation = new string('\u0005', 256);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, parent, host, generation, "note", digest, 1, new string('\u0008', 64));
        store.StartDelivery(id, claim.AttemptId); store.BeginHostNote(id, parent, host, generation, claim.AttemptId, "note"); store.SetDisposition(id, "uncertain", new string('\u0006', 512));
        var facts = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId,
            ["nativeMessageId"] = Guid.NewGuid().ToString(), ["revision"] = 1L, ["writeStarted"] = true, ["taken"] = true, ["cycleResult"] = "ended" };
        foreach (var field in new[] { "nativeError", "nativeSubtype", "nativeStopReason", "nativeTerminalReason", "dropReason", "uncertainty" })
            facts[field] = new string('\u0007', 64);
        store.RecordNoteReceipt(id, parent, host, generation, facts);
        Assert.IsTrue(store.TrySettleNoteReceipt(id, parent, host, generation, claim.AttemptId, 1));
        Assert.AreEqual("result", store.Read(id, parent).Text);
    }

    [TestMethod]
    public void SettlementComparesDurableRevisionAndRejectsPendingOrContradictoryFacts()
    {
        var dir = TestDirs.New(); var store = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(new ReportIdentity("settlement", "child", "parent", ReportCause.User, "prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("body"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, "parent", "host", "generation", "note", digest);
        store.StartDelivery(id, claim.AttemptId); store.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "note");
        var facts = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId,
            ["nativeMessageId"] = Guid.NewGuid().ToString(), ["revision"] = 1L,
            ["writeStarted"] = true, ["writeCompleted"] = true, ["taken"] = true, ["cycleResult"] = "ended" };
        store.RecordNoteReceipt(id, "parent", "host", "generation", facts);
        facts["revision"] = 2L; facts["cancelReceiptPending"] = true;
        store.RecordNoteReceipt(id, "parent", "host", "generation", facts);
        Assert.IsFalse(store.TrySettleNoteReceipt(id, "parent", "host", "generation", claim.AttemptId, 1));
        Assert.IsFalse(store.TrySettleNoteReceipt(id, "parent", "host", "generation", claim.AttemptId, 2));
        facts["revision"] = 3L; facts["cancelReceiptPending"] = false; facts["nativeCancelled"] = true; facts["contradiction"] = true;
        store.RecordNoteReceipt(id, "parent", "host", "generation", facts);
        Assert.IsFalse(store.TrySettleNoteReceipt(id, "parent", "host", "generation", claim.AttemptId, 3));
        var reopened = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.AreEqual("claimed", reopened.GetReport(id).Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => reopened.TrySettleNoteReceipt(id, "other", "host", "generation", claim.AttemptId, 3));
    }

    [TestMethod]
    public void MatchingReceiptSettlementIsDurableAndWriteFailureDoesNotPublish()
    {
        var dir = TestDirs.New(); var store = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(new ReportIdentity("settlement", "child", "parent", ReportCause.User, "prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("body"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, "parent", "host", "generation", "note", digest);
        store.StartDelivery(id, claim.AttemptId); store.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "note");
        store.RecordNoteReceipt(id, "parent", "host", "generation", new JsonObject { ["reportId"] = id,
            ["attemptId"] = claim.AttemptId, ["nativeMessageId"] = Guid.NewGuid().ToString(), ["revision"] = 1L,
            ["writeStarted"] = true, ["taken"] = true, ["cycleResult"] = "ended" });
        store.WriteManifest = (_, _) => throw new IOException("settlement fault");
        Assert.ThrowsExactly<IOException>(() => store.TrySettleNoteReceipt(id, "parent", "host", "generation", claim.AttemptId, 1));
        var reopened = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.AreEqual("claimed", reopened.GetReport(id).Status);
        Assert.IsTrue(reopened.TrySettleNoteReceipt(id, "parent", "host", "generation", claim.AttemptId, 1));
        Assert.AreEqual("completed", new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024).GetReport(id).Status);
        Assert.IsFalse(reopened.TrySettleNoteReceipt(id, "parent", "host", "generation", claim.AttemptId, 1));
        reopened.WriteManifest = (_, _) => throw new IOException("ack fault");
        Assert.ThrowsExactly<IOException>(() => reopened.RecordNoteAcknowledged(id, "parent", "host", "generation", claim.AttemptId, 1));
        var afterFault = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.IsTrue(afterFault.RecordNoteAcknowledged(id, "parent", "host", "generation", claim.AttemptId, 1));
        Assert.IsTrue(afterFault.RecordNoteAcknowledged(id, "parent", "host", "generation", claim.AttemptId, 1));
        var late = afterFault.ReadNoteReceipt(id, "parent", "host", "generation", claim.AttemptId)!;
        late["reportId"] = id; late["attemptId"] = claim.AttemptId; late["revision"] = 2L;
        late["nativeCancelled"] = true; late["contradiction"] = true;
        afterFault.RecordNoteReceipt(id, "parent", "host", "generation", late);
        Assert.IsFalse(afterFault.RecordNoteAcknowledged(id, "parent", "host", "generation", claim.AttemptId, 1));
        var conflicting = afterFault.GetReport(id);
        Assert.AreEqual("completed", conflicting.Status, "late contradictory evidence does not silently reopen a terminal disposition");
        Assert.AreEqual(true, conflicting.Delivery?.ReceiptAckPending);
        Assert.AreEqual(true, conflicting.Delivery?.ReceiptConflict);
    }


    [TestMethod]
    [DataRow("ended", true, false, "completed")]
    [DataRow("failed", true, false, "failed")]
    [DataRow("cancelled", true, false, "failed")]
    [DataRow(null, false, true, "failed")]
    [DataRow(null, false, false, "claimed")]
    public void SettlementRequiresNativeResultOrDefinitiveDrop(string? cycle, bool taken, bool dropped, string expected)
    {
        var store = new ReportStore(TestDirs.New(), bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(new ReportIdentity("mapping", "child", "parent", ReportCause.User, "prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("body"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, "parent", "host", "generation", "note", digest);
        store.StartDelivery(id, claim.AttemptId); store.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "note");
        store.RecordNoteReceipt(id, "parent", "host", "generation", new JsonObject { ["reportId"] = id,
            ["attemptId"] = claim.AttemptId, ["nativeMessageId"] = Guid.NewGuid().ToString(), ["revision"] = 1L,
            ["writeStarted"] = true, ["taken"] = taken, ["cycleResult"] = cycle, ["nativeCancelled"] = dropped });
        Assert.AreEqual(expected != "claimed", store.TrySettleNoteReceipt(id, "parent", "host", "generation", claim.AttemptId, 1));
        Assert.AreEqual(expected, store.GetReport(id).Status);
    }


    [TestMethod]
    public void NoInputProofRequiresOriginalStartedAttemptAndSurvivesWriteFaultAndReopen()
    {
        var dir = TestDirs.New(); var store = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(new ReportIdentity("proof", "child", "parent", ReportCause.User, "prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("body"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, "parent", "host", "generation", "note", digest);
        store.StartDelivery(id, claim.AttemptId); store.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "note");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordNoteNoInput(id, "parent", "host", "generation", claim.AttemptId, new string('A', 64), "native_not_running"));
        store.WriteManifest = (_, _) => throw new IOException("proof fault");
        Assert.ThrowsExactly<IOException>(() => store.RecordNoteNoInput(id, "parent", "host", "generation", claim.AttemptId, digest, "native_not_running"));
        var reopened = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        reopened.RecordNoteNoInput(id, "parent", "host", "generation", claim.AttemptId, digest, "native_not_running");
        reopened.RecordNoteNoInput(id, "parent", "host", "generation", claim.AttemptId, digest, "native_not_running");
        Assert.ThrowsExactly<InvalidOperationException>(() => reopened.RecordNoteNoInput(id, "parent", "host", "generation", claim.AttemptId, digest, "receipt_capacity"));
        Assert.ThrowsExactly<InvalidOperationException>(() => reopened.RecordNoteReceipt(id, "parent", "host", "generation", new JsonObject {
            ["reportId"] = id, ["attemptId"] = claim.AttemptId, ["nativeMessageId"] = Guid.NewGuid().ToString(), ["revision"] = 1L }));
        Assert.AreEqual("claimed", reopened.GetReport(id).Status);
        Assert.AreEqual("native_not_running", new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024)
            .ReadNoteNoInput(id, "parent", "host", "generation", claim.AttemptId));
        reopened.QueueProvenNoInput(id, "parent", "host", "generation", claim.AttemptId);
        Assert.AreEqual("pending", reopened.GetReport(id).Status);
        var queued = reopened.ClaimDelivery(id, "parent", "host", "generation");
        Assert.AreNotEqual(claim.AttemptId, queued.AttemptId);
        Assert.ThrowsExactly<InvalidOperationException>(() => reopened.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "note"));
    }


    [TestMethod]
    public void NativeReceiptRevisionChangesForFactsButNotRepeatedReads()
    {
        var receipt = new Gorynych.Adapters.Claude.ReportNoteAttempt(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString(), new string('A', 64), true);
        JsonNode Facts() => receipt.Receipt()["_meta"]![GorynychProtocol.MetaReportNote]!;
        var first = (long)Facts()["revision"]!;
        Assert.AreEqual(first, (long)Facts()["revision"]!);
        lock (receipt) receipt.WriteStarted = true;
        Assert.IsTrue((long)Facts()["revision"]! > first);
        var second = (long)Facts()["revision"]!;
        Assert.AreEqual(second, (long)Facts()["revision"]!);
    }


    [TestMethod]
    public void WorstEscapedFallbackProofFitsReservedManifestHeadroom()
    {
        var store = new ReportStore(TestDirs.New(), bodyLimit: 1024, totalLimit: 1024 * 1024);
        var parent = new string('\u0001', 512); var host = new string('\u0004', 128); var generation = new string('\u0005', 256);
        var id = store.Reserve(new ReportIdentity("worst-proof", "child", parent, ReportCause.User, "prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("body"); capture.SealIncomplete(new string('\u0002', 512), "failed", new string('\u0003', 512)); store.CommitUnconfirmed(id, capture); }
        store.AcknowledgeProducer(id);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, parent, host, generation, "note", digest, 1, new string('\u0008', 64));
        store.StartDelivery(id, claim.AttemptId); store.BeginHostNote(id, parent, host, generation, claim.AttemptId, "note");
        store.RecordNoteNoInput(id, parent, host, generation, claim.AttemptId, digest, "receipt_capacity");
        store.QueueProvenNoInput(id, parent, host, generation, claim.AttemptId);
        var queued = store.ClaimDelivery(id, parent, host, generation);
        store.StartDelivery(id, queued.AttemptId);
        store.SetDisposition(id, "uncertain", new string('\u0006', 512));
        Assert.AreEqual("body", store.Read(id, parent).Text);
    }


    [TestMethod]
    public void SettledNoteStaysPinnedUntilMatchingReceiptAck()
    {
        var store = new ReportStore(TestDirs.New(), bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 1, pinnedLimit: 1);
        var id = store.Reserve(new ReportIdentity("pin", "child", "parent", ReportCause.User, "prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("body"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, "parent", "host", "generation", "note", digest);
        store.StartDelivery(id, claim.AttemptId); store.BeginHostNote(id, "parent", "host", "generation", claim.AttemptId, "note");
        store.RecordNoteReceipt(id, "parent", "host", "generation", new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId,
            ["nativeMessageId"] = Guid.NewGuid().ToString(), ["revision"] = 1L, ["writeStarted"] = true, ["taken"] = true, ["cycleResult"] = "ended" });
        Assert.IsTrue(store.TrySettleNoteReceipt(id, "parent", "host", "generation", claim.AttemptId, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Reserve(new ReportIdentity("second", "child", "parent", ReportCause.User, "other")));
        Assert.IsTrue(store.RecordNoteAcknowledged(id, "parent", "host", "generation", claim.AttemptId, 1));
        var next = store.Reserve(new ReportIdentity("second", "child", "parent", ReportCause.User, "other"));
        Assert.AreNotEqual(id, next.ReportId);
    }


    [TestMethod]
    public async Task RetiredReceiptWithActiveObserverCannotBeEvicted()
    {
        var options = new Gorynych.Adapters.Claude.ClaudeAdapterOptions { DataDirectory = TestDirs.New(), FetchCatalog = false, ReportNoteGlobalLimit = 1 };
        await using var rpc = new Gorynych.Acp.JsonRpc.JsonRpcConnection(Stream.Null, Stream.Null, "observer-test");
        await using var agent = new Gorynych.Adapters.Claude.ClaudeAgent(options, rpc);
        Gorynych.Adapters.Claude.ReportNoteAttempt Create() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString(), new string('A', 64), true)
            { WriteStarted = true, Taken = true, CycleResult = "ended" };
        var first = Create(); Assert.IsTrue(agent.ClaimReportNote(first.AttemptId, "session", first));
        var firstRevision = (long)first.Receipt()["_meta"]![GorynychProtocol.MetaReportNote]!["revision"]!;
        lock (first) first.ObserverReferences = 1;
        Assert.IsFalse(agent.AcknowledgeReportNote(first.AttemptId, "session", first.ReportId, first.Uuid, firstRevision).Ack,
            "an event holding the active receipt cannot be retired");
        lock (first) first.ObserverReferences = 0;
        Assert.IsTrue(agent.AcknowledgeReportNote(first.AttemptId, "session", first.ReportId, first.Uuid, firstRevision).Ack);
        lock (first) first.NotificationActive = true;
        var second = Create(); Assert.IsTrue(agent.ClaimReportNote(second.AttemptId, "session", second));
        var secondRevision = (long)second.Receipt()["_meta"]![GorynychProtocol.MetaReportNote]!["revision"]!;
        Assert.IsFalse(agent.AcknowledgeReportNote(second.AttemptId, "session", second.ReportId, second.Uuid, secondRevision).Ack);
        Assert.IsNotNull(agent.FindReportNote(first.AttemptId, "session"));
        lock (first) first.NotificationActive = false;
        var late = agent.FindRetiredReportNote(first.Uuid, "session"); Assert.AreSame(first, late);
        Assert.IsFalse(agent.AcknowledgeReportNote(second.AttemptId, "session", second.ReportId, second.Uuid, secondRevision).Ack,
            "an in-flight late observer keeps the retired entry accounted");
        lock (first) first.ObserverReferences--;
        Assert.IsTrue(agent.AcknowledgeReportNote(second.AttemptId, "session", second.ReportId, second.Uuid, secondRevision).Ack);
        Assert.IsNull(agent.FindReportNote(first.AttemptId, "session"));
    }


    [TestMethod]
    public void LedgerAbsenceReleaseRequiresCurrentSettledNonConflictingReceipt()
    {
        var dir = TestDirs.New(); var store = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(new ReportIdentity("absent-cas", "child", "parent", ReportCause.User, "prompt")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("body"); capture.Seal("ended"); store.Commit(id, capture); }
        store.AcknowledgeProducer(id);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("note")));
        var claim = store.ClaimDelivery(id, "parent", "host", "gen", "note", digest); store.StartDelivery(id, claim.AttemptId); store.BeginHostNote(id, "parent", "host", "gen", claim.AttemptId, "note");
        var uuid = Guid.NewGuid().ToString(); var facts = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId,
            ["nativeMessageId"] = uuid, ["revision"] = 1L, ["writeStarted"] = true, ["taken"] = true, ["cycleResult"] = "ended" };
        store.RecordNoteReceipt(id, "parent", "host", "gen", facts);
        Assert.IsFalse(store.RecordNoteLedgerNotRetained(id, "parent", "host", "gen", claim.AttemptId, uuid, 1));
        Assert.IsTrue(store.TrySettleNoteReceipt(id, "parent", "host", "gen", claim.AttemptId, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordNoteLedgerNotRetained(id, "parent", "host", "gen", claim.AttemptId, Guid.NewGuid().ToString(), 1));
        store.WriteManifest = (_, _) => throw new IOException("release fault");
        Assert.ThrowsExactly<IOException>(() => store.RecordNoteLedgerNotRetained(id, "parent", "host", "gen", claim.AttemptId, uuid, 1));
        var reopened = new ReportStore(dir, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.IsTrue(reopened.NoteNeedsAck(id, "parent", "host", "gen", claim.AttemptId));
        facts["revision"] = 2L; facts["nativeCancelled"] = true; facts["contradiction"] = true;
        reopened.RecordNoteReceipt(id, "parent", "host", "gen", facts);
        Assert.IsFalse(reopened.RecordNoteLedgerNotRetained(id, "parent", "host", "gen", claim.AttemptId, uuid, 1));
        Assert.IsFalse(reopened.RecordNoteLedgerNotRetained(id, "parent", "host", "gen", claim.AttemptId, uuid, 2));
        Assert.IsTrue(reopened.NoteNeedsAck(id, "parent", "host", "gen", claim.AttemptId));
        Assert.AreEqual("completed", reopened.GetReport(id).Status);
    }

}
