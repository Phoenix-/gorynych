using System.Text.Json.Nodes;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class MailboxAssignmentTests
{
    [TestMethod]
    public void AdmissionIsAtomicAndRetrySurvivesEvictionAndRestart()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1);
        var write = store.WriteFile;
        store.WriteFile = (_, _) => throw new IOException("injected");
        Assert.ThrowsExactly<IOException>(() => store.Accept("one", "parent", "child", "work", delegation: true));
        Assert.IsNull(store.Find("one")); Assert.IsNull(store.FindAssignment("one", "parent"));
        store.WriteFile = write;
        store.Accept("one", "parent", "child", "work", delegation: true);
        store.MarkStarted("one", "child"); store.MarkDelivered("one", "child");
        store.Accept("next", "parent", "child", "ordinary");
        Assert.IsNull(store.Find("one"));
        store = new MailboxStore(path, capacity: 1);
        Assert.AreEqual("child", store.TryRetry("one", "parent", "child", "work", false, delegation: true));
        Assert.AreEqual("one", (string?)store.Accept("one", "parent", "child", "work", delegation: true)["assignmentId"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("one", "parent", "child", "changed", delegation: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("one", "parent", "child", "work"));
        Assert.AreEqual(1, store.Read("child", 0, 10, true).Count);
    }

    [TestMethod]
    public void IndependentTransitionsReceiptsAndClaimsSurviveRestart()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path);
        store.Accept("one", "parent", "child", "work 1", delegation: true);
        store.Accept("two", "parent", "child", "work 2", delegation: true);
        Assert.IsNull(store.FindAssignment("one", "intruder"));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionAssignment("one", "child", 1, "bad", "cancelled", "stop", generation: (string)store.FindAssignment("one", "child")!["generation"]!));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionAssignment("one", "parent", 1, "bad", "completed", "done", generation: (string)store.FindAssignment("one", "parent")!["generation"]!));
        var blocked = store.TransitionAssignment("one", "child", 1, "block", "blocked", "waiting", generation: (string)store.FindAssignment("one", "child")!["generation"]!);
        Assert.AreEqual(2L, (long)blocked["revision"]!);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionAssignment("one", "child", 1, "stale", "failed", "failure", generation: (string)store.FindAssignment("one", "child")!["generation"]!));
        store = new MailboxStore(path);
        Assert.AreEqual(2L, (long)store.TransitionAssignment("one", "child", 1, "block", "blocked", "waiting", generation: (string)store.FindAssignment("one", "child")!["generation"]!)["revision"]!);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionAssignment("one", "child", 1, "block", "blocked", "different", generation: (string)store.FindAssignment("one", "child")!["generation"]!));
        store.TransitionAssignment("one", "child", 2, "resume", "open", "", generation: (string)store.FindAssignment("one", "child")!["generation"]!);
        store.TransitionAssignment("one", "parent", 3, "cancel", "cancelled", "withdrawn", generation: (string)store.FindAssignment("one", "parent")!["generation"]!);
        Assert.IsFalse(store.TryMarkStarted("one", "child"));
        Assert.AreEqual("suppressed", (string?)store.FindAssignment("one", "parent")!["delivery"]);
        Assert.AreEqual("two", (string?)store.Read("child", 0, 10, true).Single()["id"]);
        Assert.IsTrue(store.TryMarkStarted("two", "child"));
        store.TransitionAssignment("two", "parent", 1, "cancel2", "cancelled", "withdrawn", generation: (string)store.FindAssignment("two", "parent")!["generation"]!);
        store.MarkFailed("two", "child", "lost response", uncertain: true);
        store = new MailboxStore(path);
        var two = store.FindAssignment("two", "child")!;
        Assert.AreEqual("cancelled", (string?)two["status"]); Assert.AreEqual("uncertain", (string?)two["delivery"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionAssignment("two", "child", 2, "late", "completed", "done", generation: (string)store.FindAssignment("two", "child")!["generation"]!));
    }

    [TestMethod]
    public void FailedTransitionAndClaimPublishNothing()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path);
        store.Accept("one", "parent", "child", "work", delegation: true);
        store.WriteFile = (_, _) => throw new IOException("injected");
        Assert.ThrowsExactly<IOException>(() => store.TransitionAssignment("one", "parent", 1, "cancel", "cancelled", "stop", generation: (string)store.FindAssignment("one", "parent")!["generation"]!));
        Assert.ThrowsExactly<IOException>(() => store.TryMarkStarted("one", "child"));
        Assert.AreEqual("open", (string?)store.FindAssignment("one", "parent")!["status"]);
        Assert.AreEqual("queued", (string?)new MailboxStore(path).FindAssignment("one", "child")!["delivery"]);
        Assert.AreEqual(1, store.Read("child", 0, 10, true).Count);
    }

    [TestMethod]
    public void LegacyRetryDoesNotCreateAssignment()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path);
        store.Accept("legacy", "parent", "child", "work", delegation: true);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); root.Remove("assignments"); File.WriteAllText(path, root.ToJsonString());
        store = new MailboxStore(path);
        store.Accept("legacy", "parent", "child", "work", delegation: true);
        Assert.IsNull(store.FindAssignment("legacy", "parent"));
    }
    [TestMethod]
    public void ActiveCapacityRejectsAndAStaleWorkerCannotClaimAReusedId()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1);
        store.Accept("active", "parent", "child", "work", delegation: true);
        store.MarkStarted("active", "child"); store.MarkDelivered("active", "child");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("other", "parent", "child", "other", delegation: true));
        StringAssert.Contains(error.Message, "Assignment storage is full");
        Assert.IsNull(store.Find("other"));
        store = new MailboxStore(Path.Combine(TestDirs.New(), "mailbox.json"), capacity: 1, assignmentCapacity: 1);
        var first = store.Accept("same", "parent", "child", "first", delegation: true);
        store.TransitionAssignment("same", "parent", 1, "cancel", "cancelled", "stop", generation: (string)store.FindAssignment("same", "parent")!["generation"]!);
        store.Accept("same", "parent", "child", "first", delegation: true); // retained retry
        store.Accept("other", "parent", "child", "other", delegation: true); // evicts resolved record
        store.TransitionAssignment("other", "parent", 1, "cancel2", "cancelled", "stop", generation: (string)store.FindAssignment("other", "parent")!["generation"]!);
        store.Accept("same", "parent", "child", "new work", delegation: true);
        Assert.IsFalse(store.TryMarkStarted("same", "child", (long)first["sequence"]!));
        Assert.IsFalse(store.MarkFailed("same", "child", "old worker failed", expectedSequence: (long)first["sequence"]!));
        Assert.AreEqual("queued", (string?)store.FindAssignment("same", "parent")!["delivery"]);
        Assert.AreEqual(1, store.Read("child", 0, 10, true).Count);
    }

    [TestMethod]
    public void ConcurrentCancellationAndClaimHaveOneDurableOrder()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path);
        store.Accept("one", "parent", "child", "work", delegation: true);
        bool claimed = false;
        Parallel.Invoke(() => claimed = store.TryMarkStarted("one", "child"),
            () => store.TransitionAssignment("one", "parent", 1, "cancel", "cancelled", "stop", generation: (string)store.FindAssignment("one", "parent")!["generation"]!));
        var recovered = new MailboxStore(path); var item = recovered.FindAssignment("one", "child")!;
        Assert.AreEqual("cancelled", (string?)item["status"]);
        Assert.AreEqual(claimed ? "started" : "suppressed", (string?)item["delivery"]);
        Assert.AreEqual(claimed, (bool?)recovered.Find("one")!["started"] == true);
    }

    [TestMethod]
    public void CompletionAndCancellationRaceCannotReopenTerminalOutcome()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path);
        store.Accept("one", "parent", "child", "work", delegation: true);
        var wins = 0; var conflicts = 0;
        void Attempt(string actor, string operation, string outcome)
        {
            try { store.TransitionAssignment("one", actor, 1, operation, outcome, "result", generation: (string)store.FindAssignment("one", actor)!["generation"]!); Interlocked.Increment(ref wins); }
            catch (InvalidOperationException) { Interlocked.Increment(ref conflicts); }
        }
        Parallel.Invoke(() => Attempt("parent", "cancel", "cancelled"), () => Attempt("child", "complete", "completed"));
        Assert.AreEqual(1, wins); Assert.AreEqual(1, conflicts);
        var record = new MailboxStore(path).FindAssignment("one", "child")!;
        Assert.IsTrue((string?)record["status"] is "cancelled" or "completed");
        Assert.AreEqual(2L, (long)record["revision"]!);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionAssignment("one", "child", 2, "late", "open", "", generation: (string)store.FindAssignment("one", "child")!["generation"]!));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReceiptHistoryEvictionNeverPreventsTerminalOutcome(bool cancel)
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path);
        store.Accept("one", "parent", "child", "work", delegation: true);
        for (long revision = 1; revision <= 80; revision++)
            store.TransitionAssignment("one", "child", revision, "op-" + revision,
                revision % 2 == 1 ? "blocked" : "open", revision % 2 == 1 ? "waiting" : "", generation: (string)store.FindAssignment("one", "child")!["generation"]!);
        store = new MailboxStore(path);
        var duplicate = store.TransitionAssignment("one", "child", 80, "op-80", "open", "", generation: (string)store.FindAssignment("one", "child")!["generation"]!);
        Assert.AreEqual(81L, (long)duplicate["revision"]!);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionAssignment("one", "child", 1, "op-1", "blocked", "waiting", generation: (string)store.FindAssignment("one", "child")!["generation"]!));
        Assert.AreEqual(81L, (long)store.FindAssignment("one", "parent")!["revision"]!);
        var actor = cancel ? "parent" : "child"; var outcome = cancel ? "cancelled" : "completed";
        store.TransitionAssignment("one", actor, 81, "terminal", outcome, "finished", generation: (string)store.FindAssignment("one", actor)!["generation"]!);
        store = new MailboxStore(path);
        Assert.AreEqual(outcome, (string?)store.FindAssignment("one", "child")!["status"]);
        Assert.AreEqual(82L, (long)store.TransitionAssignment("one", actor, 81, "terminal", outcome, "finished", generation: (string)store.FindAssignment("one", actor)!["generation"]!)["revision"]!);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RetainedAssignmentRetryDoesNotMakeEvictedMessageReplyable(bool suppressed)
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json"); var store = new MailboxStore(path, capacity: 1);
        store.Accept("one", "parent", "child", "work", delegation: true);
        if (suppressed) store.TransitionAssignment("one", "parent", 1, "cancel", "cancelled", "stop", generation: (string)store.FindAssignment("one", "parent")!["generation"]!);
        else { store.MarkStarted("one", "child"); store.MarkDelivered("one", "child"); }
        store.Accept("next", "parent", "child", "other");
        store = new MailboxStore(path, capacity: 1);
        Assert.IsNull(store.FindAddress("one"));
        Assert.AreEqual("child", store.TryRetry("one", "parent", "child", "work", false, delegation: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("answer", "child", "parent", "reply", "one"));
        Assert.IsNull(store.Find("answer"));
    }

    [TestMethod]
    public void ReusedIdRejectsOldGenerationAndOldReportAcrossRestart()
    {
        var dir = TestDirs.New(); var path = Path.Combine(dir, "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1);
        store.Accept("same", "parent", "child", "first", delegation: true);
        var oldGeneration = (string)store.FindAssignment("same", "child")!["generation"]!;
        var reports = new ReportStore(Path.Combine(dir, "reports"));
        string Publish(string key, string generation)
        {
            var id = reports.Reserve(new ReportIdentity(key, "child", "parent", ReportCause.Delegation,
                AssignmentId: "same", AssignmentGeneration: generation)).ReportId;
            using var capture = reports.OpenCapture(id); capture.Append("result"); capture.Seal("ended"); reports.Commit(id, capture); return id;
        }
        var oldReport = Publish("old-turn", oldGeneration);
        store.TransitionAssignment("same", "parent", 1, "cancel-old", "cancelled", "stop", generation: oldGeneration);
        store.Accept("filler", "parent", "child", "filler", delegation: true);
        store.TransitionAssignment("filler", "parent", 1, "cancel-filler", "cancelled", "stop", generation: (string)store.FindAssignment("filler", "parent")!["generation"]!);
        store.Accept("same", "parent", "child", "new work", delegation: true);
        var newGeneration = (string)store.FindAssignment("same", "child")!["generation"]!;
        Assert.AreNotEqual(oldGeneration, newGeneration);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionAssignment("same", "child", 1, "stale", "completed", "done", generation: oldGeneration));
        Assert.ThrowsExactly<InvalidOperationException>(() => reports.WithAssignmentReport(oldReport, "child", "parent", "child", "same", newGeneration,
            ReportCause.Delegation, () => store.TransitionAssignment("same", "child", 1, "bad-report", "completed", "done", oldReport, newGeneration)));
        Assert.AreEqual("open", (string?)store.FindAssignment("same", "parent")!["status"]);
        var freshReport = Publish("new-turn", newGeneration);
        var result = reports.WithAssignmentReport(freshReport, "child", "parent", "child", "same", newGeneration,
            ReportCause.Delegation, () => store.TransitionAssignment("same", "child", 1, "new-op", "completed", "done", freshReport, newGeneration));
        Assert.AreEqual("completed", (string?)result["status"]);
        store = new MailboxStore(path, capacity: 1, assignmentCapacity: 1);
        Assert.AreEqual(newGeneration, (string?)store.FindAssignment("same", "child")!["generation"]);
        Assert.AreEqual("completed", (string?)store.RetryAssignmentTransition("same", "child", 1, "new-op", "completed", "done", freshReport, newGeneration)!["status"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RetryAssignmentTransition("same", "child", 1, "new-op", "completed", "done", freshReport, oldGeneration));
    }

}
