using System.Text.Json.Nodes;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class CompletionReminderClaimTests
{
    static (MailboxStore Store, string Path, string Generation) Ready(int assignmentCapacity = 1000)
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path, assignmentCapacity: assignmentCapacity);
        store.Accept("task", "parent", "child", "work", delegation: true);
        store.MarkStarted("task", "child"); store.MarkDelivered("task", "child");
        return (store, path, (string)store.FindAssignment("task", "parent")!["generation"]!);
    }
    static string Attempt() => Guid.NewGuid().ToString("N");
    static readonly CompletionReminderBinding DefaultBinding = Binding();
    static CompletionReminderAdmission Claim(MailboxStore store, string generation, string attempt, long revision = 1) =>
        store.ClaimCompletionReminder("delegation", "task", "parent", "child", generation, revision, attempt, DefaultBinding);

    static CompletionReminderBinding Binding() => new("project", "group", "prompt", Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("N"), 17, new string('A', 64), "host", "generation", 123, "2026-01-01T00:00:00.0000000Z");

    [TestMethod]
    public void ClaimWithoutExactBindingIsRejected()
    {
        var (store, _, generation) = Ready();
        Assert.ThrowsExactly<ArgumentException>(() => store.ClaimCompletionReminder("delegation", "task", "parent", "child", generation, 1, Attempt(), null!));
        Assert.IsNull(store.ReadCompletionReminder("task", "parent", "child", generation));
    }

    [TestMethod]
    public void ExactRecoveryBindingSurvivesReopenAndOutcomeChange()
    {
        var (store, path, generation) = Ready(); var attempt = Attempt(); var binding = Binding();
        Assert.IsTrue(store.ClaimCompletionReminder("delegation", "task", "parent", "child", generation, 1, attempt, binding).Acquired);
        Assert.IsFalse(store.ClaimCompletionReminder("delegation", "task", "parent", "child", generation, 1, attempt, binding).Acquired);
        Assert.IsNull(store.ClaimCompletionReminder("delegation", "task", "parent", "child", generation, 1, attempt, binding with { GroupId = "foreign" }).Claim);
        store.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: generation);
        var reopened = new MailboxStore(path);
        var recovered = reopened.ReadCompletionRecovery("task", "parent", "child", generation, attempt);
        Assert.IsNotNull(recovered); Assert.AreEqual(binding, recovered.Binding); Assert.AreEqual(1L, recovered.OriginalRevision);
        var byAssignee = reopened.CompletionReminderPending("project", "group", "child");
        Assert.IsFalse(byAssignee.Unknown); Assert.AreEqual(1, byAssignee.Items.Count); Assert.AreEqual(recovered, byAssignee.Items[0]);
        Assert.AreEqual(0, reopened.CompletionReminderPending("other", "group", "child").Items.Count);
        Assert.AreEqual("uncertain", recovered.Disposition);
        Assert.IsNull(reopened.ReadCompletionRecovery("task", "foreign", "child", generation, attempt));
        Assert.IsNull(reopened.ReadCompletionRecovery("task", "parent", "child", Guid.NewGuid().ToString("N"), attempt));
        var (legacy, legacyPath, legacyGeneration) = Ready(); var legacyAttempt = Attempt();
        Assert.IsTrue(Claim(legacy, legacyGeneration, legacyAttempt).Acquired);
        var legacyDocument = JsonNode.Parse(File.ReadAllText(legacyPath))!;
        legacyDocument["assignments"]!["task"]!["completionReminder"]!.AsObject().Remove("binding");
        File.WriteAllText(legacyPath, legacyDocument.ToJsonString());
        legacy = new MailboxStore(legacyPath);
        Assert.IsNull(legacy.ReadCompletionRecovery("task", "parent", "child", legacyGeneration, legacyAttempt));
        Assert.IsNull(legacy.ClaimCompletionReminder("delegation", "task", "parent", "child", legacyGeneration, 1, legacyAttempt, binding).Claim);
    }

    [TestMethod]
    public void RecoveryInventoryIsBoundedOwnerScopedAndRejectsMalformedProducerStart()
    {
        var (store, path, generation) = Ready(); var binding = Binding(); var attempt = Attempt();
        Assert.ThrowsExactly<ArgumentException>(() => store.ClaimCompletionReminder("delegation", "task", "parent", "child",
            generation, 1, attempt, binding with { ProducerStart = "garbage" }));
        Assert.IsTrue(store.ClaimCompletionReminder("delegation", "task", "parent", "child", generation, 1, attempt, binding).Acquired);
        foreach (var id in new[] { "task2", "task3" })
        {
            store.Accept(id, "parent", "child", "work", delegation: true); store.MarkStarted(id, "child"); store.MarkDelivered(id, "child");
            var gen = (string)store.FindAssignment(id, "parent")!["generation"]!;
            Assert.IsTrue(store.ClaimCompletionReminder("delegation", id, "parent", "child", gen, 1, Attempt(), binding).Acquired);
        }
        var reopened = new MailboxStore(path); var first = reopened.ListCompletionRecovery("parent", null, 2);
        Assert.AreEqual(2, first.Items.Count); Assert.IsNotNull(first.NextCursor);
        var second = reopened.ListCompletionRecovery("parent", first.NextCursor, 2);
        Assert.AreEqual(1, second.Items.Count); Assert.IsNull(second.NextCursor);
        Assert.AreEqual(0, reopened.ListCompletionRecovery("foreign", null, 2).Items.Count);
        Assert.ThrowsExactly<ArgumentException>(() => reopened.ListCompletionRecovery("parent", null, 101));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BindingAndClaimShareOneCowBoundaryOnFault(bool afterWrite)
    {
        var (store, path, generation) = Ready(); var attempt = Attempt(); var binding = Binding(); var writer = store.WriteFile;
        store.WriteFile = (file, text) => { if (afterWrite) writer(file, text); throw new IOException("injected binding fault"); };
        Assert.ThrowsExactly<IOException>(() => store.ClaimCompletionReminder("delegation", "task", "parent", "child", generation, 1, attempt, binding));
        var recovered = new MailboxStore(path).ReadCompletionRecovery("task", "parent", "child", generation, attempt);
        Assert.AreEqual(afterWrite, recovered is not null);
        if (recovered is not null) { Assert.AreEqual(binding, recovered.Binding); Assert.AreEqual("uncertain", recovered.Disposition); }
    }

    [TestMethod]
    public void SingleClaimIsIndependentOfOutcomeAndUncertainAfterRestart()
    {
        var (store, path, generation) = Ready(); var attempt = Attempt();
        var first = Claim(store, generation, attempt); Assert.IsTrue(first.Acquired);
        Assert.AreEqual("handoff_started", first.Claim!.Disposition);
        var again = Claim(store, generation, attempt); Assert.IsFalse(again.Acquired); Assert.AreEqual(first.Claim, again.Claim);
        Assert.IsNull(Claim(store, generation, Attempt()).Claim);
        Assert.IsNull(store.FindAssignment("task", "child")!["completionReminder"], "not a public assignment field");
        store.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: generation);
        Assert.AreEqual("handoff_started", store.ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        Assert.AreEqual("cancelled", (string?)store.FindAssignment("task", "parent")!["status"]);
        store = new MailboxStore(path);
        Assert.AreEqual("uncertain", store.ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        var retry = Claim(store, generation, attempt); Assert.IsFalse(retry.Acquired); Assert.AreEqual("uncertain", retry.Claim!.Disposition);
        Assert.IsNull(Claim(store, generation, Attempt()).Claim);
    }

    [TestMethod]
    public void ConcurrentClaimsHaveExactlyOneWinner()
    {
        var (store, path, generation) = Ready(); var attempt = Attempt(); var results = new CompletionReminderAdmission[24];
        Parallel.For(0, results.Length, i => results[i] = Claim(store, generation, attempt));
        Assert.AreEqual(1, results.Count(r => r.Acquired)); Assert.IsTrue(results.All(r => r.Claim?.AttemptId == attempt));
        Assert.AreEqual("uncertain", new MailboxStore(path).ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        var (competing, _, otherGeneration) = Ready(); var distinct = new CompletionReminderAdmission[24];
        Parallel.For(0, distinct.Length, i => distinct[i] = Claim(competing, otherGeneration, Attempt()));
        Assert.AreEqual(1, distinct.Count(r => r.Acquired));
        Assert.AreEqual(1, distinct.Count(r => r.Claim is not null));
    }

    [TestMethod]
    public void CancellationAndContinuationBeforeClaimPreventIt()
    {
        var (cancelled, _, gen) = Ready(); var attempt = Attempt();
        cancelled.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: gen);
        Assert.IsNull(Claim(cancelled, gen, attempt).Claim);
        var (waiting, _, gen2) = Ready(); waiting.Accept("wait", "child", "peer", "question", expectReply: true);
        var target = waiting.ContinuationWait("wait", "child")!; target["kind"] = "reply_wait";
        target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
        waiting.UpdateContinuation("task", "child", gen2, 1, "wait-op", "register", target);
        Assert.IsNull(Claim(waiting, gen2, Attempt(), 2).Claim);
        Assert.IsNull(Claim(waiting, gen2, Attempt(), 1).Claim);
    }

    [TestMethod]
    public void CancellationRaceAndContinuationRaceSerializeWithClaim()
    {
        for (var n = 0; n < 20; n++)
        {
            var (store, path, gen) = Ready(); var attempt = Attempt(); CompletionReminderAdmission? claim = null;
            Parallel.Invoke(() => claim = Claim(store, gen, attempt), () => store.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: gen));
            var reopened = new MailboxStore(path); Assert.AreEqual("cancelled", (string?)reopened.FindAssignment("task", "parent")!["status"]);
            var recovered = reopened.ReadCompletionReminder("task", "parent", "child", gen);
            Assert.AreEqual(claim!.Acquired, recovered is not null);
            if (recovered is not null) Assert.AreEqual("uncertain", recovered.Disposition);
        }
        var (waiting, _, generation) = Ready(); waiting.Accept("wait", "child", "peer", "question", expectReply: true);
        var target = waiting.ContinuationWait("wait", "child")!; target["kind"] = "reply_wait";
        target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
        CompletionReminderAdmission? concurrent = null;
        Parallel.Invoke(() => concurrent = Claim(waiting, generation, Attempt()),
            () => waiting.UpdateContinuation("task", "child", generation, 1, "wait-op", "register", target));
        Assert.IsNotNull(waiting.FindAssignment("task", "child")!["continuation"]);
        if (!concurrent!.Acquired) Assert.IsNull(concurrent.Claim);
        Assert.IsNull(Claim(waiting, generation, Attempt(), 2).Claim);
    }

    [TestMethod]
    public void FaultBeforeAndAfterCommitReconcilesWholeMailboxWithoutSecondGrant()
    {
        var (store, path, generation) = Ready(); var attempt = Attempt(); var writer = store.WriteFile;
        store.Accept("question", "child", "peer", "question \"\u96ea\U0001F600\n", expectReply: true);
        store.WriteFile = (_, _) => throw new IOException("before write");
        Assert.ThrowsExactly<IOException>(() => Claim(store, generation, attempt));
        Assert.IsNull(store.ReadCompletionReminder("task", "parent", "child", generation));
        store.WriteFile = writer;
        store.Accept("answer", "peer", "child", "answer", replyTo: "question");
        Assert.AreEqual("replied", (string?)store.ListReplyWaits("child", 0, 10)["items"]![0]!["state"]);
        store.WriteFile = (p, text) => { writer(p, text); throw new IOException("after write"); };
        Assert.ThrowsExactly<IOException>(() => Claim(store, generation, attempt));
        Assert.AreEqual("uncertain", store.ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        Assert.IsFalse(Claim(store, generation, attempt).Acquired);
        Assert.IsNull(Claim(store, generation, Attempt()).Claim);
        store.WriteFile = writer; store.Accept("unrelated", "parent", "child", "ordinary");
        store = new MailboxStore(path);
        Assert.AreEqual("uncertain", store.ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        Assert.AreEqual("replied", (string?)store.ListReplyWaits("child", 0, 10)["items"]![0]!["state"]);
        Assert.IsNotNull(store.Find("unrelated"));
    }

    [TestMethod]
    public void InvalidCanonicalSnapshotFailsClosedUntilExplicitValidatedRecovery()
    {
        var (store, path, generation) = Ready(); var attempt = Attempt(); var writer = store.WriteFile;
        var original = File.ReadAllText(path);
        store.WriteFile = (p, _) => { File.WriteAllText(p, "{broken"); throw new IOException("disk fault"); };
        Assert.ThrowsExactly<IOException>(() => Claim(store, generation, attempt));
        Assert.IsFalse(store.ReconcileCompletionPublication());
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("unrelated", "parent", "child", "message"));
        Assert.ThrowsExactly<InvalidOperationException>(() => Claim(store, generation, attempt));
        var unrelatedSnapshot = JsonNode.Parse(original)!.AsObject(); unrelatedSnapshot["next"] = (long)unrelatedSnapshot["next"]! + 1;
        File.WriteAllText(path, unrelatedSnapshot.ToJsonString());
        Assert.IsFalse(store.ReconcileCompletionPublication(), "a valid but neither-before-nor-after snapshot is not adopted");
        File.WriteAllText(path, original); Assert.IsTrue(store.ReconcileCompletionPublication());
        store.WriteFile = writer; store.Accept("unrelated", "parent", "child", "message");
        Assert.IsTrue(Claim(store, generation, attempt).Acquired);
    }

    [TestMethod]
    public void SnapshotHashAllocationDoesNotScaleWithMailboxText()
    {
        var method = typeof(MailboxStore).GetMethod("Utf8Digest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var digest = (Func<string, byte[]>)method.CreateDelegate(typeof(Func<string, byte[]>));
        var shortText = "sample"; var longText = new string('x', 32000) + "\u96ea\U0001F600";
        CollectionAssert.AreEqual(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(longText)), digest(longText));
        for (var i = 0; i < 10; i++) { digest(shortText); digest(longText); }
        static long Measure(Func<string, byte[]> hash, string text)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) hash(text);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        var small = Measure(digest, shortText); var large = Measure(digest, longText);
        Console.WriteLine($"SHA-256 managed bytes / 100 calls: short={small}; 32000 chars={large}");
        Assert.IsTrue(large <= small + 65536, "hashing must not allocate a mailbox-sized byte array per call");
    }

    [TestMethod]
    public void LegacyBomAndLongUnicodeMailboxReconcileAgainstExactBytes()
    {
        var (store, path, generation) = Ready();
        store.Accept("long", "parent", "child", new string('x', 9000) + "\u96ea\U0001F600\n");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(true));
        store = new MailboxStore(path); var writer = store.WriteFile; var attempt = Attempt();
        store.WriteFile = (_, _) => throw new IOException("before replacement");
        Assert.AreEqual("before replacement", Assert.ThrowsExactly<IOException>(() => Claim(store, generation, attempt)).Message);
        Assert.IsTrue(store.ReconcileCompletionPublication());
        store.WriteFile = writer; Assert.IsTrue(Claim(store, generation, attempt).Acquired);
        store = new MailboxStore(path);
        Assert.AreEqual("uncertain", store.ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        Assert.IsNotNull(store.Find("long"));
    }

    [TestMethod]
    [DataRow("{\"next\":2,\"next\":2,\"entries\":[]}")]
    [DataRow("{\"next\":2,\"next\":3,\"entries\":[]}")]
    [DataRow("{\"next\":2,\"entries\":[{\"sequence\":1,\"sequence\":1}]}")]
    [DataRow("{\"next\":2,\"entries\":[{\"sequence\":1,\"sequence\":2}]}")]
    public void DuplicateKeysDoNotReplacePrimaryIoFailure(string corrupt)
    {
        var (store, path, generation) = Ready(); var original = File.ReadAllText(path); var writer = store.WriteFile;
        store.WriteFile = (p, _) => { File.WriteAllText(p, corrupt); throw new IOException("original write fault"); };
        var error = Assert.ThrowsExactly<IOException>(() => Claim(store, generation, Attempt()));
        Assert.AreEqual("original write fault", error.Message);
        Assert.IsFalse(store.ReconcileCompletionPublication());
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("ordinary", "parent", "child", "text"));
        File.WriteAllText(path, original); Assert.IsTrue(store.ReconcileCompletionPublication());
        store.WriteFile = writer; Assert.IsTrue(Claim(store, generation, Attempt()).Acquired);
    }

    [TestMethod]
    public void MissingCanonicalFileFailsClosedUntilExactFileReturns()
    {
        var (store, path, generation) = Ready(); var original = File.ReadAllText(path); var writer = store.WriteFile;
        store.WriteFile = (p, _) => { File.Delete(p); throw new IOException("replace unavailable"); };
        Assert.AreEqual("replace unavailable", Assert.ThrowsExactly<IOException>(() => Claim(store, generation, Attempt())).Message);
        Assert.IsFalse(store.ReconcileCompletionPublication());
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("ordinary", "parent", "child", "text"));
        File.WriteAllText(path, original); Assert.IsTrue(store.ReconcileCompletionPublication());
        store.WriteFile = writer; store.Accept("ordinary", "parent", "child", "text");
    }

    [TestMethod]
    public void CompletionSlotLimitDefersWithoutStarvingOrdinaryWorkAndUnknownCanClose()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, assignmentCapacity: 10, completionClaimCapacity: 1);
        foreach (var id in new[] { "a", "b" }) { store.Accept(id, "parent", "child", id, delegation: true); store.MarkStarted(id, "child"); store.MarkDelivered(id, "child"); }
        string Gen(string id) => (string)store.FindAssignment(id, "parent")!["generation"]!;
        var genA = Gen("a"); var genB = Gen("b"); var attemptA = Attempt(); var attemptB = Attempt();
        CompletionReminderAdmission ClaimFor(string id, string gen, string attempt) => store.ClaimCompletionReminder("delegation", id, "parent", "child", gen, 1, attempt, DefaultBinding);
        Assert.IsTrue(ClaimFor("a", genA, attemptA).Acquired);
        Assert.IsNull(ClaimFor("b", genB, attemptB).Claim);
        store.Accept("ordinary", "parent", "child", "message");
        store.Accept("c", "parent", "other", "ordinary assignment", delegation: true);
        store = new MailboxStore(path, assignmentCapacity: 10, completionClaimCapacity: 1);
        Assert.AreEqual("uncertain", store.ReadCompletionReminder("a", "parent", "child", genA)!.Disposition);
        Assert.IsNull(ClaimFor("b", genB, attemptB).Claim, "reopen alone must not release the hold");
        Assert.IsFalse(store.CloseCompletionReminderUnknown("delegation", "a", "parent", "child", Attempt(), 1, attemptA));
        Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", "a", "parent", "child", genA, 1, attemptA));
        Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", "a", "parent", "child", genA, 1, attemptA));
        Assert.AreEqual("unknown_closed", store.ReadCompletionReminder("a", "parent", "child", genA)!.Disposition);
        Assert.IsFalse(ClaimFor("a", genA, attemptA).Acquired); Assert.IsNull(ClaimFor("a", genA, Attempt()).Claim);
        Assert.IsTrue(ClaimFor("b", genB, attemptB).Acquired);
        store = new MailboxStore(path, assignmentCapacity: 10, completionClaimCapacity: 1);
        Assert.AreEqual("unknown_closed", store.ReadCompletionReminder("a", "parent", "child", genA)!.Disposition);
        Assert.IsTrue(store.SettleCompletionReminder("delegation", "a", "parent", "child", genA, 1, attemptA, CompletionReminderSettlement.NativeEnded));
        Assert.IsFalse(store.SettleCompletionReminder("delegation", "a", "parent", "child", genA, 1, attemptA, CompletionReminderSettlement.NativeFailed));
        Assert.IsFalse(store.CloseCompletionReminderUnknown("delegation", "a", "parent", "child", genA, 1, attemptA));
        Assert.AreEqual("native_ended", store.ReadCompletionReminder("a", "parent", "child", genA)!.Disposition);
    }

    [TestMethod]
    public void UnknownReceiptSurvivesAssignmentEvictionAndAckIsOwnerScoped()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1, completionReceiptCapacity: 1);
        store.Accept("task", "parent", "child", "work", delegation: true); store.MarkStarted("task", "child"); store.MarkDelivered("task", "child");
        var gen = (string)store.FindAssignment("task", "parent")!["generation"]!; var attempt = Attempt();
        Assert.IsTrue(Claim(store, gen, attempt).Acquired);
        Assert.IsNull(store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt));
        Assert.IsFalse(store.AckCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt), "reservation is not a receipt");
        store.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: gen);
        Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", "task", "parent", "child", gen, 1, attempt));
        var receipt = store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt)!;
        Assert.AreEqual("handoff_unknown_at_closure", (string?)receipt["event"]); Assert.AreEqual("unacked", (string?)receipt["state"]);
        Assert.IsNull(store.ReadCompletionUnknownReceipt("intruder", "task", "child", gen, 1, attempt));
        Assert.IsFalse(store.AckCompletionUnknownReceipt("intruder", "task", "child", gen, 1, attempt));
        Assert.IsFalse(store.AckCompletionUnknownReceipt("parent", "task", "child", Attempt(), 1, attempt));
        store.Accept("other", "parent", "child", "other", delegation: true);
        Assert.IsNull(store.FindAssignment("task", "parent"));
        store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1, completionReceiptCapacity: 1);
        Assert.AreEqual("unacked", (string?)store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt)!["state"]);
        Assert.AreEqual(1, store.ListCompletionUnknownReceipts("parent")["items"]!.AsArray().Count);
        Assert.AreEqual(0, store.ListCompletionUnknownReceipts("intruder")["items"]!.AsArray().Count);
        Assert.IsTrue(store.AckCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt));
        Assert.IsTrue(store.AckCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt));
        store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1, completionReceiptCapacity: 1);
        Assert.AreEqual("acked", (string?)store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt)!["state"]);
        Assert.AreEqual(0, store.ListCompletionUnknownReceipts("parent")["items"]!.AsArray().Count);
    }

    [TestMethod]
    public void OldReceiptAckDoesNotAffectNewAssignmentGeneration()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1, completionReceiptCapacity: 2);
        store.Accept("task", "parent", "child", "old", delegation: true); store.MarkStarted("task", "child"); store.MarkDelivered("task", "child");
        var oldGen = (string)store.FindAssignment("task", "parent")!["generation"]!; var oldAttempt = Attempt(); Claim(store, oldGen, oldAttempt);
        store.TransitionAssignment("task", "parent", 1, "cancel-old", "cancelled", "stop", generation: oldGen);
        Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", "task", "parent", "child", oldGen, 1, oldAttempt));
        store.Accept("other", "parent", "child", "other", delegation: true);
        var otherGen = (string)store.FindAssignment("other", "parent")!["generation"]!;
        store.TransitionAssignment("other", "parent", 1, "cancel-other", "cancelled", "stop", generation: otherGen);
        store.Accept("task", "parent", "child", "new", delegation: true); store.MarkStarted("task", "child"); store.MarkDelivered("task", "child");
        var newGen = (string)store.FindAssignment("task", "parent")!["generation"]!; var newAttempt = Attempt();
        Assert.AreNotEqual(oldGen, newGen); Assert.IsTrue(Claim(store, newGen, newAttempt).Acquired);
        Assert.IsTrue(store.AckCompletionUnknownReceipt("parent", "task", "child", oldGen, 1, oldAttempt));
        Assert.IsFalse(store.AckCompletionUnknownReceipt("parent", "task", "child", newGen, 1, oldAttempt));
        Assert.AreEqual("handoff_started", store.ReadCompletionReminder("task", "parent", "child", newGen)!.Disposition);
        Assert.IsFalse(Claim(store, newGen, newAttempt).Acquired);
        Assert.AreEqual("open", (string?)store.FindAssignment("task", "parent")!["status"]);
    }

    [TestMethod]
    public void OwnerReceiptPagesAreBoundedAndDoNotExposeOtherOwners()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path, completionReceiptCapacity: 4);
        foreach (var (id, owner) in new[] { ("a", "parent"), ("b", "parent"), ("c", "other") })
        {
            store.Accept(id, owner, "child", id, delegation: true); store.MarkStarted(id, "child"); store.MarkDelivered(id, "child");
            var gen = (string)store.FindAssignment(id, owner)!["generation"]!; var attempt = Attempt();
            Assert.IsTrue(store.ClaimCompletionReminder("delegation", id, owner, "child", gen, 1, attempt, DefaultBinding).Acquired);
            Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", id, owner, "child", gen, 1, attempt));
        }
        var first = store.ListCompletionUnknownReceipts("parent", limit: 1);
        Assert.AreEqual(1, first["items"]!.AsArray().Count); var cursor = (string)first["nextCursor"]!;
        var second = store.ListCompletionUnknownReceipts("parent", cursor, 1);
        Assert.AreEqual(1, second["items"]!.AsArray().Count); Assert.IsNull(second["nextCursor"]);
        Assert.AreNotEqual((string?)first["items"]![0]!["assignmentId"], (string?)second["items"]![0]!["assignmentId"]);
        Assert.AreEqual(1, store.ListCompletionUnknownReceipts("other")["items"]!.AsArray().Count);
        Assert.AreEqual(0, store.ListCompletionUnknownReceipts("intruder")["items"]!.AsArray().Count);
        Assert.ThrowsExactly<ArgumentException>(() => store.ListCompletionUnknownReceipts("parent", limit: 101));
    }

    [TestMethod]
    public void ReceiptReservationCapDefersOnlyClaimsAndAckFreesSpace()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, completionClaimCapacity: 3, completionReceiptCapacity: 1);
        foreach (var id in new[] { "a", "b" }) { store.Accept(id, "parent", "child", id, delegation: true); store.MarkStarted(id, "child"); store.MarkDelivered(id, "child"); }
        string Gen(string id) => (string)store.FindAssignment(id, "parent")!["generation"]!;
        var genA = Gen("a"); var genB = Gen("b"); var attemptA = Attempt(); var attemptB = Attempt();
        CompletionReminderAdmission? a = null, b = null;
        Parallel.Invoke(() => a = store.ClaimCompletionReminder("delegation", "a", "parent", "child", genA, 1, attemptA, DefaultBinding),
            () => b = store.ClaimCompletionReminder("delegation", "b", "parent", "child", genB, 1, attemptB, DefaultBinding));
        Assert.AreEqual(1, new[] { a, b }.Count(x => x!.Acquired));
        var winningId = a!.Acquired ? "a" : "b"; var losingId = a.Acquired ? "b" : "a";
        var winningGen = a.Acquired ? genA : genB; var losingGen = a.Acquired ? genB : genA;
        var winningAttempt = a.Acquired ? attemptA : attemptB; var losingAttempt = a.Acquired ? attemptB : attemptA;
        store.Accept("ordinary", "parent", "child", "message"); store.Accept("c", "parent", "other", "work", delegation: true);
        Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", winningId, "parent", "child", winningGen, 1, winningAttempt));
        Assert.IsNull(store.ClaimCompletionReminder("delegation", losingId, "parent", "child", losingGen, 1, losingAttempt, DefaultBinding).Claim, "unacked receipt reserves capacity");
        store = new MailboxStore(path, completionClaimCapacity: 3, completionReceiptCapacity: 1);
        Assert.IsNull(store.ClaimCompletionReminder("delegation", losingId, "parent", "child", losingGen, 1, losingAttempt, DefaultBinding).Claim);
        Assert.IsTrue(store.AckCompletionUnknownReceipt("parent", winningId, "child", winningGen, 1, winningAttempt));
        Assert.IsTrue(store.ClaimCompletionReminder("delegation", losingId, "parent", "child", losingGen, 1, losingAttempt, DefaultBinding).Acquired);
        Assert.IsFalse(store.AckCompletionUnknownReceipt("parent", winningId, "child", winningGen, 1, winningAttempt), "evicted ack is a safe no-op");
        Assert.IsFalse(store.ClaimCompletionReminder("delegation", winningId, "parent", "child", winningGen, 1, winningAttempt, DefaultBinding).Acquired);
        store.TransitionAssignment(winningId, "parent", 1, "cancel-winner", "cancelled", "stop", generation: winningGen);
        store.AssignmentCapacityForTests = 3;
        store.Accept("d", "parent", "child", "new work", delegation: true);
        Assert.IsNull(store.FindAssignment(winningId, "parent"), "durable ack permits eviction after the receipt tombstone expires");
    }

    [TestMethod]
    public void LegacyUnknownWithoutReceiptRemainsPinnedForExplicitMigration()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1);
        store.Accept("task", "parent", "child", "work", delegation: true); store.MarkStarted("task", "child"); store.MarkDelivered("task", "child");
        var gen = (string)store.FindAssignment("task", "parent")!["generation"]!; var attempt = Attempt(); Claim(store, gen, attempt);
        store.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: gen);
        Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", "task", "parent", "child", gen, 1, attempt));
        var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); legacy.Remove("completionReceipts"); File.WriteAllText(path, legacy.ToJsonString());
        store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("other", "parent", "child", "work", delegation: true));
        Assert.IsFalse(store.CloseCompletionReminderUnknown("delegation", "task", "parent", "child", gen, 1, attempt));
        Assert.IsFalse(store.SettleCompletionReminder("delegation", "task", "parent", "child", gen, 1, attempt, CompletionReminderSettlement.NativeEnded));
    }

    [TestMethod]
    public void ClaimAfterReplacementFaultRetainsItsReceiptReservation()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, completionClaimCapacity: 2, completionReceiptCapacity: 1);
        foreach (var id in new[] { "a", "b" }) { store.Accept(id, "parent", "child", id, delegation: true); store.MarkStarted(id, "child"); store.MarkDelivered(id, "child"); }
        var genA = (string)store.FindAssignment("a", "parent")!["generation"]!; var genB = (string)store.FindAssignment("b", "parent")!["generation"]!;
        var attemptA = Attempt(); var writer = store.WriteFile;
        store.WriteFile = (p, text) => { writer(p, text); throw new IOException("claim fault"); };
        Assert.ThrowsExactly<IOException>(() => store.ClaimCompletionReminder("delegation", "a", "parent", "child", genA, 1, attemptA, DefaultBinding));
        Assert.AreEqual("uncertain", store.ReadCompletionReminder("a", "parent", "child", genA)!.Disposition);
        Assert.IsNull(store.ClaimCompletionReminder("delegation", "b", "parent", "child", genB, 1, Attempt(), DefaultBinding).Claim);
        store.WriteFile = writer; Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", "a", "parent", "child", genA, 1, attemptA));
        store = new MailboxStore(path, completionClaimCapacity: 2, completionReceiptCapacity: 1);
        Assert.AreEqual("unacked", (string?)store.ReadCompletionUnknownReceipt("parent", "a", "child", genA, 1, attemptA)!["state"]);
    }

    [TestMethod]
    public void ClosedUnknownReceiptIsImmutableWhenLateNativeResultArrives()
    {
        var (store, path, gen) = Ready(); var attempt = Attempt(); Claim(store, gen, attempt);
        Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", "task", "parent", "child", gen, 1, attempt));
        var before = store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt)!.ToJsonString();
        Assert.IsTrue(store.SettleCompletionReminder("delegation", "task", "parent", "child", gen, 1, attempt, CompletionReminderSettlement.NativeEnded));
        Assert.AreEqual(before, store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt)!.ToJsonString());
        store = new MailboxStore(path);
        Assert.AreEqual("native_ended", store.ReadCompletionReminder("task", "parent", "child", gen)!.Disposition);
        Assert.AreEqual(before, store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt)!.ToJsonString());
        Assert.AreEqual("open", (string?)store.FindAssignment("task", "parent")!["status"]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClosureAndReceiptRecoverAsOneUnitOnFault(bool afterWrite)
    {
        var (store, path, gen) = Ready(); var attempt = Attempt(); Assert.IsTrue(Claim(store, gen, attempt).Acquired);
        var writer = store.WriteFile;
        store.WriteFile = (p, text) => { if (afterWrite) writer(p, text); throw new IOException("closure fault"); };
        Assert.ThrowsExactly<IOException>(() => store.CloseCompletionReminderUnknown("delegation", "task", "parent", "child", gen, 1, attempt));
        Assert.AreEqual(afterWrite ? "unknown_closed" : "handoff_started", store.ReadCompletionReminder("task", "parent", "child", gen)!.Disposition);
        Assert.AreEqual(afterWrite, store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt) is not null);
        store.WriteFile = writer;
        if (!afterWrite) Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", "task", "parent", "child", gen, 1, attempt));
        store = new MailboxStore(path);
        Assert.AreEqual("unknown_closed", store.ReadCompletionReminder("task", "parent", "child", gen)!.Disposition);
        Assert.AreEqual("unacked", (string?)store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt)!["state"]);
    }

    [TestMethod]
    public void AckAfterReplacementFaultIsIdempotentAndDoesNotAffectOutcome()
    {
        var (store, path, gen) = Ready(); var attempt = Attempt(); Claim(store, gen, attempt);
        store.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: gen);
        store.CloseCompletionReminderUnknown("delegation", "task", "parent", "child", gen, 1, attempt);
        var writer = store.WriteFile;
        store.WriteFile = (_, _) => throw new IOException("before ack");
        Assert.ThrowsExactly<IOException>(() => store.AckCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt));
        Assert.AreEqual("unacked", (string?)store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt)!["state"]);
        store.WriteFile = (p, text) => { writer(p, text); throw new IOException("ack fault"); };
        Assert.ThrowsExactly<IOException>(() => store.AckCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt));
        Assert.IsTrue(store.AckCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt));
        Assert.IsFalse(Claim(store, gen, attempt).Acquired);
        store = new MailboxStore(path);
        Assert.AreEqual("cancelled", (string?)store.FindAssignment("task", "parent")!["status"]);
        Assert.AreEqual("acked", (string?)store.ReadCompletionUnknownReceipt("parent", "task", "child", gen, 1, attempt)!["state"]);
    }

    [TestMethod]
    public void UnrecognizedDispositionKeepsItsSlotAndCannotBeClosedAsUnknown()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, completionClaimCapacity: 1);
        foreach (var id in new[] { "a", "b" }) { store.Accept(id, "parent", "child", id, delegation: true); store.MarkStarted(id, "child"); store.MarkDelivered(id, "child"); }
        var genA = (string)store.FindAssignment("a", "parent")!["generation"]!;
        var genB = (string)store.FindAssignment("b", "parent")!["generation"]!; var attemptA = Attempt();
        Assert.IsTrue(store.ClaimCompletionReminder("delegation", "a", "parent", "child", genA, 1, attemptA, DefaultBinding).Acquired);
        var root = JsonNode.Parse(File.ReadAllText(path))!; root["assignments"]!["a"]!["completionReminder"]!["disposition"] = "future_unknown_state";
        File.WriteAllText(path, root.ToJsonString()); store = new MailboxStore(path, completionClaimCapacity: 1);
        Assert.IsNull(store.ClaimCompletionReminder("delegation", "b", "parent", "child", genB, 1, Attempt(), DefaultBinding).Claim);
        Assert.IsFalse(store.CloseCompletionReminderUnknown("delegation", "a", "parent", "child", genA, 1, attemptA));
    }

    [TestMethod]
    public void CompetingAssignmentsCannotOverfillCompletionSlotLimit()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, completionClaimCapacity: 1);
        foreach (var id in new[] { "a", "b" }) { store.Accept(id, "parent", "child", id, delegation: true); store.MarkStarted(id, "child"); store.MarkDelivered(id, "child"); }
        var genA = (string)store.FindAssignment("a", "parent")!["generation"]!;
        var genB = (string)store.FindAssignment("b", "parent")!["generation"]!;
        CompletionReminderAdmission? a = null, b = null;
        Parallel.Invoke(() => a = store.ClaimCompletionReminder("delegation", "a", "parent", "child", genA, 1, Attempt(), DefaultBinding),
            () => b = store.ClaimCompletionReminder("delegation", "b", "parent", "child", genB, 1, Attempt(), DefaultBinding));
        Assert.AreEqual(1, new[] { a, b }.Count(x => x!.Acquired));
        Assert.AreEqual(1, new[] { a, b }.Count(x => x!.Claim is not null));
        store.Accept("ordinary", "parent", "child", "message");
        Assert.IsNotNull(store.Find("ordinary"));
    }

    [TestMethod]
    public void ClosedUnknownTerminalCanEvictWithoutCallingItNotSent()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1, completionClaimCapacity: 1);
        store.Accept("task", "parent", "child", "work", delegation: true); store.MarkStarted("task", "child"); store.MarkDelivered("task", "child");
        var gen = (string)store.FindAssignment("task", "parent")!["generation"]!; var attempt = Attempt(); Assert.IsTrue(Claim(store, gen, attempt).Acquired);
        store.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: gen);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("next", "parent", "child", "work", delegation: true));
        Assert.IsTrue(store.CloseCompletionReminderUnknown("delegation", "task", "parent", "child", gen, 1, attempt));
        Assert.AreEqual("unknown_closed", store.ReadCompletionReminder("task", "parent", "child", gen)!.Disposition);
        store.Accept("next", "parent", "child", "work", delegation: true);
        Assert.IsNull(store.ReadCompletionReminder("task", "parent", "child", gen));
        Assert.IsFalse(store.SettleCompletionReminder("delegation", "task", "parent", "child", gen, 1, attempt, CompletionReminderSettlement.ProvenNotSent));
    }

    [TestMethod]
    [DataRow(0, "native_ended")]
    [DataRow(1, "native_failed")]
    [DataRow(2, "native_cancelled")]
    [DataRow(3, "not_sent")]
    public void ExactLateSettlementIsIdempotentAndReleasesTerminalRetention(int value, string expected)
    {
        var settlement = (CompletionReminderSettlement)value;
        var (store, path, generation) = Ready(1); var attempt = Attempt(); Assert.IsTrue(Claim(store, generation, attempt).Acquired);
        store.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: generation);
        store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1);
        Assert.AreEqual("uncertain", store.ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        bool Settle(string source, string owner, string child, string gen, long rev, string id, CompletionReminderSettlement state) =>
            store.SettleCompletionReminder(source, "task", owner, child, gen, rev, id, state);
        Assert.IsFalse(Settle("spawn", "parent", "child", generation, 1, attempt, settlement));
        Assert.IsFalse(Settle("delegation", "other", "child", generation, 1, attempt, settlement));
        Assert.IsFalse(Settle("delegation", "parent", "other", generation, 1, attempt, settlement));
        Assert.IsFalse(Settle("delegation", "parent", "child", Attempt(), 1, attempt, settlement));
        Assert.IsFalse(Settle("delegation", "parent", "child", generation, 2, attempt, settlement));
        Assert.IsFalse(Settle("delegation", "parent", "child", generation, 1, Attempt(), settlement));
        Assert.IsTrue(Settle("delegation", "parent", "child", generation, 1, attempt, settlement));
        Assert.IsTrue(Settle("delegation", "parent", "child", generation, 1, attempt, settlement));
        var conflict = settlement == CompletionReminderSettlement.NativeEnded ? CompletionReminderSettlement.NativeFailed : CompletionReminderSettlement.NativeEnded;
        Assert.IsFalse(Settle("delegation", "parent", "child", generation, 1, attempt, conflict));
        Assert.IsFalse(Claim(store, generation, attempt).Acquired); Assert.IsNull(Claim(store, generation, Attempt()).Claim);
        store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1);
        Assert.AreEqual(expected, store.ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        Assert.AreEqual("cancelled", (string?)store.FindAssignment("task", "parent")!["status"]);
        store.Accept("other", "parent", "child", "work", delegation: true);
        Assert.IsNull(store.ReadCompletionReminder("task", "parent", "child", generation));
        Assert.IsFalse(Settle("delegation", "parent", "child", generation, 1, attempt, settlement));
        var otherGeneration = (string)store.FindAssignment("other", "parent")!["generation"]!;
        store.TransitionAssignment("other", "parent", 1, "cancel-other", "cancelled", "stop", generation: otherGeneration);
        store.Accept("task", "parent", "child", "new", delegation: true);
        store.MarkStarted("task", "child"); store.MarkDelivered("task", "child");
        var newGeneration = (string)store.FindAssignment("task", "parent")!["generation"]!;
        var newAttempt = Attempt(); Assert.IsTrue(Claim(store, newGeneration, newAttempt).Acquired);
        Assert.IsFalse(Settle("delegation", "parent", "child", generation, 1, attempt, settlement));
        Assert.AreEqual("handoff_started", store.ReadCompletionReminder("task", "parent", "child", newGeneration)!.Disposition);
    }

    [TestMethod]
    public void SettlementWriteFaultAfterReplacementPreservesExactCommittedOutcome()
    {
        var (store, path, generation) = Ready(); var attempt = Attempt(); Claim(store, generation, attempt);
        var writer = store.WriteFile;
        store.WriteFile = (_, _) => throw new IOException("before replacement");
        Assert.ThrowsExactly<IOException>(() => store.SettleCompletionReminder("delegation", "task", "parent", "child", generation, 1, attempt, CompletionReminderSettlement.NativeEnded));
        Assert.AreEqual("handoff_started", store.ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        store.WriteFile = (p, text) => { writer(p, text); throw new IOException("after replacement"); };
        Assert.ThrowsExactly<IOException>(() => store.SettleCompletionReminder("delegation", "task", "parent", "child", generation, 1, attempt, CompletionReminderSettlement.NativeEnded));
        Assert.AreEqual("native_ended", store.ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
        Assert.IsTrue(store.SettleCompletionReminder("delegation", "task", "parent", "child", generation, 1, attempt, CompletionReminderSettlement.NativeEnded));
        Assert.IsFalse(Claim(store, generation, attempt).Acquired);
        store.WriteFile = writer; store.Accept("unrelated", "parent", "child", "message");
        Assert.AreEqual("native_ended", new MailboxStore(path).ReadCompletionReminder("task", "parent", "child", generation)!.Disposition);
    }

    [TestMethod]
    public void ReusedAssignmentIdCannotAcceptOldGenerationClaim()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1);
        store.Accept("task", "parent", "child", "old", delegation: true);
        var oldGeneration = (string)store.FindAssignment("task", "parent")!["generation"]!;
        store.TransitionAssignment("task", "parent", 1, "old-cancel", "cancelled", "stop", generation: oldGeneration);
        store.Accept("other", "parent", "child", "other", delegation: true);
        var otherGeneration = (string)store.FindAssignment("other", "parent")!["generation"]!;
        store.TransitionAssignment("other", "parent", 1, "other-cancel", "cancelled", "stop", generation: otherGeneration);
        store.Accept("task", "parent", "child", "new", delegation: true);
        store.MarkStarted("task", "child"); store.MarkDelivered("task", "child");
        var newGeneration = (string)store.FindAssignment("task", "parent")!["generation"]!;
        Assert.AreNotEqual(oldGeneration, newGeneration);
        var attempt = Attempt(); Assert.IsNull(Claim(store, oldGeneration, attempt).Claim);
        Assert.IsTrue(Claim(store, newGeneration, attempt).Acquired);
        store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1);
        Assert.IsNull(store.ReadCompletionReminder("task", "parent", "child", oldGeneration));
        Assert.AreEqual("uncertain", store.ReadCompletionReminder("task", "parent", "child", newGeneration)!.Disposition);
    }

    [TestMethod]
    public void IdentityRevisionAndRetentionRemainBounded()
    {
        var (store, path, gen) = Ready(1); var attempt = Attempt();
        Assert.IsNull(store.ClaimCompletionReminder("delegation", "task", "intruder", "child", gen, 1, attempt, DefaultBinding).Claim);
        Assert.IsNull(store.ClaimCompletionReminder("delegation", "task", "parent", "other", gen, 1, attempt, DefaultBinding).Claim);
        Assert.IsNull(Claim(store, Attempt(), attempt).Claim); Assert.IsNull(Claim(store, gen, attempt, 2).Claim);
        Assert.ThrowsExactly<ArgumentException>(() => store.ClaimCompletionReminder("spawn", "task", "parent", "child", gen, 1, attempt, DefaultBinding));
        Assert.IsTrue(Claim(store, gen, attempt).Acquired);
        store.TransitionAssignment("task", "parent", 1, "cancel", "cancelled", "stop", generation: gen);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("next", "parent", "child", "work", delegation: true));
        store = new MailboxStore(path, assignmentCapacity: 1);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("next", "parent", "child", "work", delegation: true));
        Assert.AreEqual("uncertain", store.ReadCompletionReminder("task", "parent", "child", gen)!.Disposition);
    }
}
