using Gorynych.Acp.Hosting;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ReportStoreTests
{
    static ReportIdentity Identity(string turn) => new(turn, "child", "parent", ReportCause.Delegation, "prompt", "assignment", "message");
    static ReportCapture Sealed(ReportStore store, string id, string text)
    {
        var capture = store.OpenCapture(id);
        try { capture.Append(text); capture.Seal("ended"); return capture; }
        catch { capture.Dispose(); throw; }
    }

    [TestMethod]
    public void SenderOnlyPeerCaptureHasExactIndependentAclAndAdmissionSequence()
    {
        var directory = TestDirs.New(); var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var route = new PeerTurnIdentity("message", 17, "sender", "worker", "service-peer-17-message");
        var identity = new ReportIdentity("peer:17:message", "worker", null, ReportCause.Peer,
            PromptId: route.PromptId, MessageId: route.MessageId, PeerRoute: route);
        var id = store.Reserve(identity).ReportId;
        Assert.ThrowsExactly<ArgumentException>(() => store.Reserve(identity with { PeerRoute = route with { Sequence = 18 } }));
        using (var capture = Sealed(store, id, "peer result")) store.Commit(id, capture);
        store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.AreEqual(route, store.GetReport(id).Identity.PeerRoute); Assert.IsNull(store.GetReport(id).Identity.Parent);
        Assert.AreEqual("peer result", store.Read(id, "sender").Text); Assert.AreEqual("peer result", store.Read(id, "worker").Text);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => store.Read(id, "unrelated"));
        Assert.AreEqual(1, store.List("sender").Items.Count); Assert.AreEqual(0, store.List("unrelated").Items.Count);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PeerCaptureParentVisibilityIsIndependentFromSender(bool parentVisible)
    {
        var directory = TestDirs.New(); var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var route = new PeerTurnIdentity("message", 17, "sender", "worker", "service-peer-17-message");
        var identity = new ReportIdentity("peer:17:message", "worker", parentVisible ? "parent" : null, ReportCause.Peer,
            PromptId: route.PromptId, MessageId: route.MessageId, PeerRoute: route);
        var id = store.Reserve(identity).ReportId;
        using (var capture = Sealed(store, id, "body")) store.Commit(id, capture);
        Assert.AreEqual("body", store.Read(id, "sender").Text);
        Assert.AreEqual("body", store.Read(id, "worker").Text);
        if (parentVisible) Assert.AreEqual("body", store.Read(id, "parent").Text);
        else Assert.ThrowsExactly<UnauthorizedAccessException>(() => store.Read(id, "parent"));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => store.Read(id, "other"));
    }

    [TestMethod]
    public void RetainedPeerReportStaysPinnedUntilMailboxAcknowledgement()
    {
        var directory = TestDirs.New(); var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 1, pinnedLimit: 1);
        var route = new PeerTurnIdentity("message", 17, "sender", "worker", "service-peer-17-message");
        var identity = new ReportIdentity("peer:17:message", "worker", null, ReportCause.Peer, PromptId: route.PromptId,
            MessageId: route.MessageId, HostId: "host", HostGeneration: "generation", PeerRoute: route);
        var id = store.Reserve(identity).ReportId;
        using (var capture = Sealed(store, id, "result")) { store.RecordProducerEvidence(id, "host", "ended", null, null); store.Commit(id, capture); }
        store.AcknowledgeProducer(id); store.SetDisposition(id, "retained", "settled");
        store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 1, pinnedLimit: 1);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Reserve(Identity("another")));
        Assert.AreEqual("result", store.Read(id, "sender").Text);
    }

    [TestMethod]
    public void PeerProducerEvidenceAndTerminalAreAtomicImmutableAndSurviveReopen()
    {
        var directory = TestDirs.New(); var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var route = new PeerTurnIdentity("message", 17, "sender", "worker", "service-peer-17-message");
        var identity = new ReportIdentity("peer:17:message", "worker", null, ReportCause.Peer, PromptId: route.PromptId,
            MessageId: route.MessageId, HostId: "original-host", HostGeneration: "generation", PeerRoute: route);
        var id = store.Reserve(identity).ReportId;
        var proof = new PeerProducerEvidence(route.PromptId, "01234567-89ab-cdef-0123-456789abcdef", "stream", "0123456789abcdef0123456789abcdef");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordProducerEvidence(id, "foreign-host", "ended", null, null, proof));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordProducerEvidence(id, "original-host", "ended", null, null, proof with { PromptId = "foreign" }));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordProducerEvidence(id, "original-host", "ended", "contradiction", null, proof));
        var write = store.WriteManifest;
        store.WriteManifest = (_, _) => throw new IOException("injected before replace");
        Assert.ThrowsExactly<IOException>(() => store.RecordProducerEvidence(id, "original-host", "ended", null, null, proof));
        store.WriteManifest = write;
        Assert.IsNull(new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024).GetReport(id).PeerEvidence);
        store.RecordProducerEvidence(id, "original-host", "ended", null, null, proof);
        store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.AreEqual(proof, store.GetReport(id).PeerEvidence);
        store.RecordProducerEvidence(id, "original-host", "ended", null, null, proof);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RecordProducerEvidence(id, "original-host", "ended", null, null, null));
        using (var capture = Sealed(store, id, "result")) store.Commit(id, capture);
        Assert.AreEqual(proof, new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024).GetReport(id).PeerEvidence);
    }

    [TestMethod]
    public void AtomicPublicationFailureAndIdempotentRecovery()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var receipt = store.Reserve(Identity("turn-1"));
        using var capture = Sealed(store, receipt.ReportId, "  result 😀\n");
        store.WriteManifest = (_, _) => throw new IOException("injected manifest publication failure");
        Assert.ThrowsExactly<IOException>(() => store.Commit(receipt.ReportId, capture));
        Assert.IsTrue(File.Exists(Path.Combine(directory, receipt.ReportId + ".body")), "body candidate exists but is not yet published");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Read(receipt.ReportId, "parent"));
        var recovered = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.ThrowsExactly<InvalidOperationException>(() => recovered.Read(receipt.ReportId, "parent"));
        Assert.AreEqual(receipt.ReportId, recovered.Reserve(Identity("turn-1")).ReportId);
        Assert.ThrowsExactly<InvalidOperationException>(() => recovered.Reserve(Identity("turn-1") with { Parent = "other" }));
        Assert.AreEqual("pending", recovered.Commit(receipt.ReportId, capture).Status);
        Assert.AreEqual("pending", recovered.Commit(receipt.ReportId, capture).Status);
        Assert.AreEqual("ended", recovered.Read(receipt.ReportId, "parent").Outcome);
        Assert.AreEqual("  result 😀\n", new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024).Read(receipt.ReportId, "parent").Text);
    }

    [TestMethod]
    public void ScalarPagesStayBoundToImmutableReportAndAuthorizedPrincipals()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("first")).ReportId;
        using var capture = Sealed(store, id, "A😀BéC");
        store.Commit(id, capture);
        var first = store.Read(id, "parent", limit: 4);
        Assert.AreEqual("A", first.Text);
        var second = store.Read(id, "child", first.NextCursor, 4);
        Assert.AreEqual("😀", second.Text);
        Assert.AreEqual(first, store.Read(id, "parent", limit: 4));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => store.Read(id, "sibling"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Read(id, "parent", id + ":2", 4));
        var later = store.Reserve(Identity("later")).ReportId;
        using var laterCapture = Sealed(store, later, "later");
        store.Commit(later, laterCapture);
        Assert.ThrowsExactly<ArgumentException>(() => store.Read(later, "parent", first.NextCursor, 4));
        Assert.AreEqual(first, store.Read(id, "parent", limit: 4));
    }

    [TestMethod]
    public void PinnedCapacityRejectsAndCompletedAcknowledgedRecordCanBeEvicted()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 1, pinnedLimit: 1);
        var first = store.Reserve(Identity("first")).ReportId;
        using var capture = Sealed(store, first, "one");
        store.Commit(first, capture);
        capture.Dispose();
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Reserve(Identity("second")));
        store.SetDisposition(first, "claimed");
        store.SetDisposition(first, "uncertain", "handoff unconfirmed");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.SetDisposition(first, "pending"));
        store.SetDisposition(first, "failed", "delivery uncertain");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Reserve(Identity("second")));
        store.AcknowledgeProducer(first);
        var lockFile = Path.Combine(directory, "producer-slot-0000.lock");
        Assert.IsTrue(File.Exists(lockFile));
        var next = store.Reserve(Identity("second")).ReportId;
        Assert.IsTrue(File.Exists(lockFile), "lifetime lock file is never deleted on ACK or eviction");
        using (var nextCapture = store.OpenCapture(next)) nextCapture.Append("new");
        Assert.IsTrue(File.Exists(lockFile));
        Assert.AreNotEqual(first, next);
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.Read(first, "parent"));
    }
    [TestMethod]
    public void SealedCaptureReconcilesAfterRestartAndDispositionWriteFailureIsAtomic()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 65536, totalLimit: 4 * 1024 * 1024);
        var id = store.Reserve(Identity("long-turn")).ReportId;
        var text = new string('x', 40000) + "😀 end";
        using (var capture = Sealed(store, id, text)) store.Commit(id, capture);
        var restarted = new ReportStore(directory, bodyLimit: 65536, totalLimit: 4 * 1024 * 1024);
        using (var recovered = restarted.OpenCapture(id))
            Assert.AreEqual(id, restarted.Commit(id, recovered).ReportId);
        restarted.WriteManifest = (_, _) => throw new IOException("disposition fault");
        Assert.ThrowsExactly<IOException>(() => restarted.SetDisposition(id, "claimed"));
        var page = new ReportStore(directory, bodyLimit: 65536, totalLimit: 4 * 1024 * 1024).Read(id, "parent", limit: 16384);
        Assert.AreEqual("pending", page.Status);
        var result = page.Text;
        while (page.NextCursor is not null)
        {
            page = restarted.Read(id, "parent", page.NextCursor, 16384);
            result += page.Text;
        }
        Assert.AreEqual(text, result);
        Assert.AreEqual("assignment", page.Identity.AssignmentId);
        Assert.AreEqual("message", page.Identity.MessageId);
    }

    [TestMethod]
    public void SoftTtlAndPagedMetadataKeepPinnedReports()
    {
        var directory = TestDirs.New();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024) { Now = () => now };
        var old = store.Reserve(Identity("old")).ReportId;
        using (var c = Sealed(store, old, "old")) store.Commit(old, c);
        store.AcknowledgeProducer(old);
        store.SetDisposition(old, "claimed"); store.SetDisposition(old, "accepted"); store.SetDisposition(old, "completed");
        var pinned = store.Reserve(Identity("pinned")).ReportId;
        using (var c = Sealed(store, pinned, "waiting")) store.Commit(pinned, c);
        now = now.AddDays(31);
        var next = store.Reserve(Identity("new")).ReportId;
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.Read(old, "parent"));
        Assert.AreEqual("pending", store.Read(pinned, "parent").Status);
        var page = store.List("parent", limit: 1);
        Assert.AreEqual(pinned, page.Items.Single().ReportId);
        Assert.AreEqual(next, store.List("parent", page.NextCursor, 1).Items.Single().ReportId);
        Assert.AreEqual(0, store.List("sibling").Items.Count);
        Assert.ThrowsExactly<ArgumentException>(() => store.List("sibling", page.NextCursor));
    }
    [TestMethod]
    public void ReservationFailureAndConcurrentRetryAreDurable()
    {
        var directory = TestDirs.New();
        var limited = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1000);
        Assert.ThrowsExactly<InvalidOperationException>(() => limited.Reserve(Identity("too-large")));
        Assert.AreEqual(0, limited.List("parent").Items.Count);
        var firstStore = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var secondStore = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        ReportReceipt? first = null, second = null;
        Parallel.Invoke(() => first = firstStore.Reserve(Identity("same-turn")), () => second = secondStore.Reserve(Identity("same-turn")));
        Assert.AreEqual(first!.ReportId, second!.ReportId);
        Assert.AreEqual(1, firstStore.List("parent").Items.Count);
        firstStore.WriteManifest = (_, _) => throw new IOException("reservation publication fault");
        Assert.ThrowsExactly<IOException>(() => firstStore.Reserve(Identity("other-turn")));
        Assert.AreEqual(1, new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024).List("parent").Items.Count);
    }

    [TestMethod]
    public void DispositionUpdatesNeverRewriteImmutableBody()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("locked-body")).ReportId;
        using (var capture = Sealed(store, id, "immutable")) store.Commit(id, capture);
        var body = Path.Combine(directory, id + ".body");
        using (new FileStream(body, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.SetDisposition(id, "pending", "archived");
            store.SetDisposition(id, "pending", null);
            store.SetDisposition(id, "claimed");
        }
        Assert.AreEqual("immutable", store.Read(id, "parent").Text);
    }
    [TestMethod]
    public void FailedProducerAckKeepsSealedCaptureRecoverable()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("ack-failure")).ReportId;
        using (var capture = Sealed(store, id, "recoverable")) store.Commit(id, capture);
        store.WriteManifest = (_, _) => throw new IOException("ack publication failed");
        Assert.ThrowsExactly<IOException>(() => store.AcknowledgeProducer(id));
        var restarted = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        using (var capture = restarted.OpenCapture(id))
            Assert.AreEqual(id, restarted.Commit(id, capture).ReportId);
        Assert.AreEqual(id, restarted.Recoverable().Items.Single().ReportId);
        restarted.AcknowledgeProducer(id);
        Assert.AreEqual(0, restarted.Recoverable().Items.Count);
        Assert.IsFalse(File.Exists(restarted.CapturePath(id)));
        Assert.AreEqual("recoverable", restarted.Read(id, "parent").Text);
    }
    [TestMethod]
    public void ReservationReleaseRequiresMatchingIdentityAndNoLiveProducer()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024, pinnedLimit: 1);
        var identity = Identity("abandon");
        var id = store.Reserve(identity).ReportId;
        using (var capture = store.OpenCapture(id))
            Assert.ThrowsExactly<IOException>(() => store.AbandonReservation(id, identity));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.AbandonReservation(id, identity with { Parent = "other" }));
        store.WriteManifest = (_, _) => throw new IOException("fault");
        Assert.ThrowsExactly<IOException>(() => store.AbandonReservation(id, identity));
        var recovered = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024, pinnedLimit: 1);
        Assert.AreEqual(id, recovered.Reserve(identity).ReportId);
        recovered.AbandonReservation(id, identity);
        Assert.AreEqual(0, recovered.List("parent").Items.Count);
        Assert.AreNotEqual(id, recovered.Reserve(Identity("new")).ReportId);
    }

    [TestMethod]
    public void PreviewIsBoundedAndDoesNotSplitSurrogatePairs()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 8192, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("preview")).ReportId;
        using (var capture = Sealed(store, id, new string('a', 1999) + "😀tail")) store.Commit(id, capture);
        var preview = store.Preview(id, "parent");
        Assert.AreEqual(new string('a', 1999), preview.Text);
        Assert.IsTrue(preview.Truncated);
        Assert.IsNull(preview.IncompleteReason);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => store.Preview(id, "sibling"));
    }
    [TestMethod]
    public void CorruptProducerCaptureCanBePublishedOnlyAsExplicitFailure()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("corrupt-turn")).ReportId;
        using (var capture = store.OpenCapture(id)) capture.Append("partial");
        using (var file = new FileStream(store.CapturePath(id), FileMode.Append, FileAccess.Write)) file.WriteByte(1);
        Assert.ThrowsExactly<InvalidDataException>(() => store.OpenCapture(id));
        Assert.AreEqual("pending", store.CommitCaptureFailure(id, "failed", "capture journal corrupt").Status);
        Assert.AreEqual("pending", store.CommitCaptureFailure(id, "failed", "capture journal corrupt").Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.CommitCaptureFailure(id, "ended", "capture journal corrupt"));
        var result = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024).Read(id, "parent");
        Assert.AreEqual("capture journal corrupt", result.IncompleteReason);
        Assert.IsFalse(result.HasText);
        Assert.AreEqual("", result.Text);
        Assert.AreEqual(ReportCause.Delegation, result.Identity.Cause);
    }
    [TestMethod]
    public void DuplicateImportWithChangedTextCannotRewriteCommittedBody()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("content-conflict")).ReportId;
        using (var capture = Sealed(store, id, "original")) store.Commit(id, capture);
        File.Delete(store.CapturePath(id));
        using (var altered = new ReportCapture(store.CapturePath(id), 1024))
        {
            altered.Append("different"); altered.Seal("ended");
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Commit(id, altered));
        }
        Assert.AreEqual("original", store.Read(id, "parent").Text);
    }
    [TestMethod]
    public async Task SlowBodyStagingDoesNotHoldSharedMetadataLock()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("slow-body")).ReportId;
        using var capture = Sealed(store, id, "result");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.WriteBodyStage = (path, text) =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test barrier timed out");
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        };
        var commit = Task.Run(() => store.Commit(id, capture));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var other = await Task.Run(() => store.Reserve(Identity("parallel-turn"))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreNotEqual(id, other.ReportId);
        }
        finally { release.Set(); }
        Assert.AreEqual("pending", (await commit.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [TestMethod]
    public void FailedBodyStagingCannotPublishResultOrOutbox()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("body-fault")).ReportId;
        using var capture = Sealed(store, id, "result");
        store.WriteBodyStage = (path, _) => { File.WriteAllText(path, "partial"); throw new IOException("injected body write failure"); };
        Assert.ThrowsExactly<IOException>(() => store.Commit(id, capture));
        Assert.IsFalse(File.Exists(Path.Combine(directory, id + ".body.stage")));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Read(id, "parent"));
        var recovered = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.AreEqual("reserved", recovered.Recoverable().Items.Single().Status);
        Assert.AreEqual("pending", recovered.Commit(id, capture).Status);
    }
    [TestMethod]
    public void CorruptImmutableBodyNeverReadsAsComplete()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("corrupt-body")).ReportId;
        using (var capture = Sealed(store, id, "original")) store.Commit(id, capture);
        File.WriteAllText(Path.Combine(directory, id + ".body"), "changed");
        Assert.ThrowsExactly<InvalidDataException>(() => store.Read(id, "parent"));
    }
    [TestMethod]
    public void DurableIdSequenceDoesNotResetAcrossRestartOrEviction()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 1);
        var first = store.Reserve(Identity("sequence-first")).ReportId;
        using (var capture = Sealed(store, first, "done")) store.Commit(first, capture);
        store.AcknowledgeProducer(first);
        store.SetDisposition(first, "claimed"); store.SetDisposition(first, "accepted"); store.SetDisposition(first, "completed");
        var restarted = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 1);
        var second = restarted.Reserve(Identity("sequence-second")).ReportId;
        Assert.AreEqual(first[..16], second[..16]);
        Assert.AreNotEqual(first, second);
        File.Delete(Path.Combine(directory, "id-sequence.json"));
        restarted.AbandonReservation(second, Identity("sequence-second"));
        Assert.ThrowsExactly<InvalidDataException>(() => restarted.Reserve(Identity("sequence-third")));
    }
    [TestMethod]
    public void MissingCommittedSpoolIsNotSilentlyRecreated()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("missing-spool")).ReportId;
        using (var capture = Sealed(store, id, "durable")) store.Commit(id, capture);
        File.Delete(store.CapturePath(id));
        Assert.ThrowsExactly<FileNotFoundException>(() => store.OpenCapture(id));
        Assert.IsFalse(File.Exists(store.CapturePath(id)));
        Assert.AreEqual("durable", store.Read(id, "parent").Text);
        Assert.AreEqual(id, store.Recoverable().Items.Single().ReportId);
    }
    [TestMethod]
    public void AdmittedConcurrentProducersCanSealCommitAndAckAtFullBodyLimit()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 4096, totalLimit: 145000);
        var a = store.Reserve(Identity("quota-a")).ReportId;
        var b = store.Reserve(Identity("quota-b")).ReportId;
        using var ca = store.OpenCapture(a);
        using var cb = store.OpenCapture(b);
        for (var i = 0; i < 1000; i++) { ca.Append("a"); cb.Append("b"); }
        ca.Append(new string('a', 3096)); cb.Append(new string('b', 3096));
        ca.Seal("ended"); cb.Seal("cancelled", "user cancelled");
        Assert.AreEqual("pending", store.Commit(a, ca).Status);
        ca.Dispose(); store.AcknowledgeProducer(a);
        Assert.AreEqual("pending", store.Commit(b, cb).Status);
        cb.Dispose(); store.AcknowledgeProducer(b);
        Assert.AreEqual(4096, store.Read(a, "parent").Text.Length);
        Assert.AreEqual("cancelled", store.Read(b, "parent").Outcome);
        Assert.AreEqual("user cancelled", store.Read(b, "parent").Error);
        Assert.IsTrue(Directory.EnumerateFiles(directory).Sum(f => new FileInfo(f).Length) <= 145000);
    }

    [TestMethod]
    public void RecoveryCursorSurvivesAcknowledgementAndEvictionOfPriorPage()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024, recordLimit: 2);
        var a = store.Reserve(Identity("page-a")).ReportId;
        var b = store.Reserve(Identity("page-b")).ReportId;
        using (var ca = Sealed(store, a, "a")) store.Commit(a, ca);
        using (var cb = Sealed(store, b, "b")) store.Commit(b, cb);
        var recoveryPage = store.Recoverable(limit: 1);
        var listPage = store.List("parent", limit: 1);
        Assert.AreEqual(a, recoveryPage.Items.Single().ReportId);
        store.AcknowledgeProducer(a);
        Assert.AreEqual(b, store.Recoverable(recoveryPage.NextCursor, 1).Items.Single().ReportId);
        store.SetDisposition(a, "claimed"); store.SetDisposition(a, "accepted"); store.SetDisposition(a, "completed");
        var c = store.Reserve(Identity("page-c")).ReportId; // evicts a
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.Read(a, "parent"));
        var next = store.List("parent", listPage.NextCursor, 1);
        Assert.AreEqual(b, next.Items.Single().ReportId);
        Assert.AreEqual(c, store.List("parent", next.NextCursor, 1).Items.Single().ReportId);
        Assert.AreEqual(b, store.Recoverable(recoveryPage.NextCursor, 1).Items.Single().ReportId);
        Assert.ThrowsExactly<ArgumentException>(() => store.List("child", listPage.NextCursor, 1));
    }

    [TestMethod]
    public void TerminalOutcomeAndErrorSurviveSealBeforeImportAndCannotBeReplacedOnRetry()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("sealed-failure")).ReportId;
        using (var capture = store.OpenCapture(id)) { capture.Append("partial result"); capture.Seal("failed", "adapter failed"); }
        var restarted = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        using (var recovered = restarted.OpenCapture(id))
        {
            Assert.AreEqual("failed", recovered.Snapshot().Outcome);
            Assert.AreEqual("adapter failed", recovered.Snapshot().Error);
            restarted.Commit(id, recovered);
            Assert.AreEqual(id, restarted.Commit(id, recovered).ReportId);
        }
        Assert.AreEqual("failed", restarted.Read(id, "parent").Outcome);
        Assert.AreEqual("adapter failed", restarted.Read(id, "parent").Error);
        File.Delete(store.CapturePath(id));
        using var forged = new ReportCapture(store.CapturePath(id), 1024);
        forged.Append("partial result"); forged.Seal("ended");
        Assert.ThrowsExactly<InvalidOperationException>(() => restarted.Commit(id, forged));
        Assert.AreEqual("failed", restarted.Read(id, "parent").Outcome);
    }
    [TestMethod]
    public void SealIncompleteAfterOverflowRetainsOriginalLossAndTerminalAcrossRestartAndCommit()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 4, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("overflow-terminal")).ReportId;
        string reason;
        using (var capture = store.OpenCapture(id))
        {
            capture.Append("1234");
            Assert.ThrowsExactly<InvalidOperationException>(() => capture.Append("5"));
            reason = capture.Snapshot().IncompleteReason!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            capture.SealIncomplete("Host was interrupted after output overflow", "failed", "host failure");
            Assert.AreEqual(reason, capture.Snapshot().IncompleteReason);
        }
        var restarted = new ReportStore(directory, bodyLimit: 4, totalLimit: 1024 * 1024);
        using (var recovered = restarted.OpenCapture(id))
        {
            var snapshot = recovered.Snapshot();
            Assert.IsTrue(snapshot.Sealed);
            Assert.AreEqual("1234", snapshot.Text);
            Assert.AreEqual(reason, snapshot.IncompleteReason);
            Assert.AreEqual("failed", snapshot.Outcome);
            Assert.AreEqual("host failure", snapshot.Error);
            Assert.AreEqual("pending", restarted.Commit(id, recovered).Status);
        }
        var result = new ReportStore(directory, bodyLimit: 4, totalLimit: 1024 * 1024).Read(id, "parent");
        Assert.AreEqual(reason, result.IncompleteReason);
        Assert.AreEqual("failed", result.Outcome);
        Assert.AreEqual("host failure", result.Error);
        Assert.AreEqual("1234", result.Text);
    }

    [TestMethod]
    public void MismatchedStoreLimitsFailClosedBeforeAdmissionOrMutation()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("config-guard")).ReportId;
        var mismatched = new ReportStore(directory, bodyLimit: 2048, totalLimit: 1024 * 1024);
        Assert.ThrowsExactly<InvalidDataException>(() => mismatched.Reserve(Identity("must-not-start")));
        Assert.ThrowsExactly<InvalidDataException>(() => mismatched.AbandonReservation(id, Identity("config-guard")));
        Assert.AreEqual(id, new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024).Recoverable().Items.Single().ReportId);
    }
    [TestMethod]
    public void DefaultBudgetAdmitsFifteenEmptyProducerReservationsWithoutRaisingCap()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory);
        for (var i = 0; i < 15; i++) store.Reserve(Identity("default-" + i));
        Assert.AreEqual(15, store.Recoverable(limit: 100).Items.Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Reserve(Identity("default-overflow")));
    }
    static async Task ProbeLeaseInChildProcess(string script, string path, int expected)
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, path }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start lease probe");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var output = await stdout;
        var errors = await stderr;
        Assert.AreEqual(expected, process.ExitCode, $"Lease probe stdout: {output}; stderr: {errors}");
    }
    [TestMethod]
    public async Task CaptureLeaseRemainsExclusiveAcrossAtomicCompaction()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var other = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("lease-compaction")).ReportId;
        var script = Path.Combine(directory, "lease-probe.ps1");
        File.WriteAllText(script, "param([string]$p)\ntry { $f=[IO.File]::Open($p,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $f.Dispose(); exit 0 } catch [IO.IOException] { exit 7 }");
        var leasePath = Path.Combine(directory, "producer-slot-0000.lock");
        using var capture = store.OpenCapture(id);
        using var beforeRelease = new ManualResetEventSlim();
        using var afterRelease = new ManualResetEventSlim();
        var before = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeCount = 0; var afterCount = 0;
        capture.BeforeReplace = () =>
        {
            if (Interlocked.Increment(ref beforeCount) != 1) return;
            before.TrySetResult();
            if (!beforeRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("before replace barrier");
        };
        capture.AfterReplace = () =>
        {
            if (Interlocked.Increment(ref afterCount) != 1) return;
            after.TrySetResult();
            if (!afterRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("after replace barrier");
        };
        var writer = Task.Run(() => { for (var i = 0; i < 900; i++) capture.Append("x"); });
        try
        {
            await before.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.ThrowsExactly<IOException>(() => other.OpenCapture(id));
            if (OperatingSystem.IsWindows()) await ProbeLeaseInChildProcess(script, leasePath, 7);
            beforeRelease.Set();
            await after.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.ThrowsExactly<IOException>(() => other.OpenCapture(id));
            if (OperatingSystem.IsWindows()) await ProbeLeaseInChildProcess(script, leasePath, 7);
            afterRelease.Set();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(new string('x', 900), capture.Snapshot().Text);
            Assert.ThrowsExactly<IOException>(() => other.OpenCapture(id));
        }
        finally
        {
            beforeRelease.Set(); afterRelease.Set();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
        }
        capture.Dispose();
        if (OperatingSystem.IsWindows()) await ProbeLeaseInChildProcess(script, leasePath, 0);
        using var reopened = other.OpenCapture(id);
        Assert.AreEqual(new string('x', 900), reopened.Snapshot().Text);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CompactionFailurePreservesCandidateBeforeAndAfterAtomicReplacement(bool afterMove)
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var other = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("compaction-fault-" + afterMove)).ReportId;
        var capture = store.OpenCapture(id);
        if (afterMove) capture.AfterReplace = () => throw new IOException("after rename");
        else capture.BeforeReplace = () => throw new IOException("before rename");
        var accepted = 0;
        try
        {
            Assert.ThrowsExactly<IOException>(() => { for (; accepted < 900; accepted++) capture.Append("x"); });
            Assert.ThrowsExactly<IOException>(() => other.OpenCapture(id));
        }
        finally { capture.Dispose(); }
        using var recovered = other.OpenCapture(id);
        Assert.AreEqual(new string('x', accepted), recovered.Snapshot().Text);
        Assert.IsFalse(recovered.Snapshot().Sealed);
        recovered.Seal("ended");
        Assert.AreEqual("pending", other.Commit(id, recovered).Status);
        Assert.AreEqual(new string('x', accepted), other.Read(id, "parent").Text);
    }

    [TestMethod]
    public void RetryAfterManifestFailureFitsQuotaWithOldBodyAndNewStage()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 4096, totalLimit: 145000);
        var a = store.Reserve(Identity("quota-retry-a")).ReportId;
        var b = store.Reserve(Identity("quota-retry-b")).ReportId;
        using var ca = store.OpenCapture(a); using var cb = store.OpenCapture(b);
        ca.Append(new string('a', 4096)); cb.Append(new string('b', 4096));
        ca.Seal("ended"); cb.Seal("ended");
        store.WriteManifest = (_, _) => throw new IOException("manifest failed after body rename");
        Assert.ThrowsExactly<IOException>(() => store.Commit(a, ca));
        Assert.IsTrue(File.Exists(Path.Combine(directory, a + ".body")));
        store.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
        Assert.AreEqual("pending", store.Commit(b, cb).Status);
        Assert.AreEqual("pending", store.Commit(a, ca).Status);
        ca.Dispose(); cb.Dispose(); store.AcknowledgeProducer(a); store.AcknowledgeProducer(b);
        Assert.AreEqual(4096, store.Read(a, "parent").Text.Length);
        Assert.AreEqual(4096, store.Read(b, "parent").Text.Length);
    }
    [TestMethod]
    public void CorruptDuplicateActiveLeaseSlotsFailClosed()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        store.Reserve(Identity("slot-a")); store.Reserve(Identity("slot-b"));
        var manifest = Path.Combine(directory, "reports.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        root["entries"]![1]!["leaseSlot"] = (int)root["entries"]![0]!["leaseSlot"]!;
        File.WriteAllText(manifest, root.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => store.Recoverable());
    }
    [TestMethod]
    public void RepeatedAckDoesNotCompeteWithReusedProducerSlot()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024, pinnedLimit: 2);
        var first = store.Reserve(Identity("ack-slot-first")).ReportId;
        using (var capture = Sealed(store, first, "first")) store.Commit(first, capture);
        store.AcknowledgeProducer(first);
        var second = store.Reserve(Identity("ack-slot-second")).ReportId;
        using var next = store.OpenCapture(second);
        store.AcknowledgeProducer(first); // first's old slot is now owned by second
        Assert.ThrowsExactly<IOException>(() => store.OpenCapture(second));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "producer-slot-0000.lock")));
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ToolResetFailureRecoversTheCorrectSideOfAtomicReplacement(bool afterMove)
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var other = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("reset-fault-" + afterMove)).ReportId;
        using (var capture = store.OpenCapture(id))
        {
            capture.Append("before tool");
            if (afterMove) capture.AfterReplace = () => throw new IOException("after rename");
            else capture.BeforeReplace = () => throw new IOException("before rename");
            Assert.ThrowsExactly<IOException>(() => capture.ToolCall());
            Assert.ThrowsExactly<IOException>(() => other.OpenCapture(id));
        }
        using var recovered = other.OpenCapture(id);
        Assert.IsFalse(recovered.Snapshot().Sealed);
        Assert.AreEqual(afterMove ? "" : "before tool", recovered.Snapshot().Text);
        Assert.AreEqual(!afterMove, recovered.Snapshot().HasText);
    }
    [TestMethod]
    public void BoundedProducerEvidenceAndResultFitOriginalMetadataBudget()
    {
        var store = new ReportStore(TestDirs.New(), bodyLimit: 4096, totalLimit: 145000);
        var identity = new ReportIdentity("metadata-bound", "child", "parent", ReportCause.User, "prompt", HostId: "host");
        var id = store.Reserve(identity).ReportId;
        var text = new string('\u0001', 512);
        using (var capture = store.OpenCapture(id)) { capture.Append("data"); capture.SealIncomplete(text, "failed", text); }
        store.RecordProducerEvidence(id, "host", "failed", text, text);
        using (var capture = store.OpenCapture(id)) store.Commit(id, capture);
        store.AcknowledgeProducer(id);
        store.SetDisposition(id, "claimed"); store.SetDisposition(id, "failed", text);
        var result = store.Read(id, "parent");
        Assert.AreEqual(text, result.Error); Assert.AreEqual(text, result.IncompleteReason); Assert.AreEqual(text, result.DispositionReason);
        store.RecordProducerEvidence(id, "host", "failed", text, text); // idempotence after producer fields are compacted
    }

    [TestMethod]
    public void UnconfirmedSealedResultRetainsOriginalLossAndNativeOutcomeImmutably()
    {
        var store = new ReportStore(TestDirs.New(), bodyLimit: 4096, totalLimit: 145000);
        var id = store.Reserve(new ReportIdentity("unconfirmed", "child", "parent", ReportCause.User, "prompt", HostId: "host")).ReportId;
        var loss = new string('\u0001', 512); var error = new string('e', 512);
        using (var capture = store.OpenCapture(id))
        {
            capture.Append("exact retained text"); capture.SealIncomplete(loss, "failed", error);
            store.CommitUnconfirmed(id, capture); store.CommitUnconfirmed(id, capture);
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Commit(id, capture));
        }
        store.AcknowledgeProducer(id);
        store.SetDisposition(id, "claimed"); store.SetDisposition(id, "failed", loss);
        var result = store.Read(id, "parent");
        Assert.AreEqual("exact retained text", result.Text); Assert.IsTrue(result.HasText);
        Assert.AreEqual("failed", result.Outcome); Assert.AreEqual(error, result.Error);
        Assert.AreEqual(loss, result.IncompleteReason); Assert.AreEqual(ReportStore.MissingProducerEvidence, result.CaptureConfidenceReason);
        Assert.AreEqual(loss, result.DispositionReason);
    }


    [TestMethod]
    public void DeliveryClaimIsAtomicBoundedAndOnlyUnstartedCanBeReleased()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var id = store.Reserve(Identity("delivery-claim")).ReportId;
        using (var capture = Sealed(store, id, "immutable result")) store.Commit(id, capture);
        store.AcknowledgeProducer(id);
        store.WriteManifest = (_, _) => throw new IOException("claim fault");
        Assert.ThrowsExactly<IOException>(() => store.ClaimDelivery(id, "parent", "host", "generation"));
        var reopened = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        Assert.AreEqual("pending", reopened.GetReport(id).Status); Assert.IsNull(reopened.GetReport(id).Delivery);
        Assert.ThrowsExactly<InvalidOperationException>(() => reopened.ClaimDelivery(id, "other", null, null));
        var claim = reopened.ClaimDelivery(id, "parent", new string('\u0001', 128), new string('\u0002', 256));
        Assert.AreEqual("claimed", reopened.GetReport(id).Status); Assert.AreEqual(claim, reopened.GetReport(id).Delivery);
        reopened.WriteManifest = (_, _) => throw new IOException("dispatch marker fault");
        Assert.ThrowsExactly<IOException>(() => reopened.StartDelivery(id, claim.AttemptId));
        Assert.IsFalse(new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024).GetReport(id).Delivery!.DispatchStarted);
        reopened.WriteManifest = (path, json) => AtomicFile.WriteAllText(path, json);
        reopened.ReleaseUnstartedDelivery(id, claim.AttemptId, new string('\u0003', 512));
        var second = reopened.ClaimDelivery(id, "parent", "host", "generation");
        reopened.StartDelivery(id, second.AttemptId);
        Assert.ThrowsExactly<InvalidOperationException>(() => reopened.ReleaseUnstartedDelivery(id, second.AttemptId));
        reopened.SetDisposition(id, "uncertain", new string('\u0004', 512));
        var result = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024).Read(id, "parent");
        Assert.AreEqual("immutable result", result.Text); Assert.AreEqual("uncertain", result.Status);
        Assert.AreEqual(second.AttemptId, result.Delivery!.AttemptId); Assert.IsTrue(result.Delivery.DispatchStarted);
    }

    [TestMethod]
    public void DeliveryMetadataBudgetCoversEscapedWorstCaseWithoutDuplicatingParent()
    {
        var directory = TestDirs.New();
        var store = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024);
        var parent = new string('\u0001', 512);
        var id = store.Reserve(Identity("worst-escaped") with { Parent = parent }).ReportId;
        using (var capture = store.OpenCapture(id))
        {
            capture.Append("result");
            capture.SealIncomplete(new string('\u0002', 512), "failed", new string('\u0003', 512));
            store.CommitUnconfirmed(id, capture);
        }
        store.AcknowledgeProducer(id);
        var claim = store.ClaimDelivery(id, parent, new string('\u0004', 128), new string('\u0005', 256));
        store.StartDelivery(id, claim.AttemptId);
        store.SetDisposition(id, "uncertain", new string('\u0006', 512));
        var recovered = new ReportStore(directory, bodyLimit: 1024, totalLimit: 1024 * 1024).Read(id, parent);
        Assert.AreEqual("result", recovered.Text); Assert.AreEqual("failed", recovered.Outcome);
        Assert.AreEqual(512, recovered.Error!.Length); Assert.AreEqual(512, recovered.IncompleteReason!.Length);
        Assert.AreEqual(512, recovered.DispositionReason!.Length); Assert.AreEqual(parent, recovered.Delivery!.Parent);
    }
}
