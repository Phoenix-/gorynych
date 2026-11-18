using System.Text.Json.Nodes;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class SpawnAssignmentStoreTests
{
    static (SessionStore Store, string Path) New()
    {
        var path = System.IO.Path.Combine(TestDirs.New(), "sessions.json"); var store = new SessionStore(path);
        store.Update("child", "fake", r => { r.Draft = true; r.ParentSessionId = "parent"; }, durable: true);
        store.CommitSpawnAssignment("child", "assignment"); return (store, path);
    }

    [TestMethod]
    public void AdmissionClaimAndOutcomeWritesAreCopyOnWrite()
    {
        var (store, path) = New(); var saved = store.WriteFile;
        store.WriteFile = (_, _, _) => throw new IOException("fault");
        Assert.ThrowsExactly<IOException>(() => store.ClaimSpawnAssignment("child", "assignment", "prompt"));
        Assert.ThrowsExactly<IOException>(() => store.TransitionSpawnAssignment("child", "assignment", "parent", 1, "cancel", "cancelled", "stop", generation: (string)store.Get("child")!.SpawnAssignment!["generation"]!));
        Assert.AreEqual("queued", (string?)store.FindSpawnAssignment("assignment", "parent")!["delivery"]);
        Assert.AreEqual("queued", (string?)new SessionStore(path).FindSpawnAssignment("assignment", "child")!["delivery"]);
        store.WriteFile = saved;
        store.TransitionSpawnAssignment("child", "assignment", "parent", 1, "cancel", "cancelled", "stop", generation: (string)store.Get("child")!.SpawnAssignment!["generation"]!);
        Assert.IsFalse(store.ClaimSpawnAssignment("child", "assignment", "prompt"));
        var recovered = new SessionStore(path).FindSpawnAssignment("assignment", "child")!;
        Assert.AreEqual("cancelled", (string?)recovered["status"]); Assert.AreEqual("suppressed", (string?)recovered["delivery"]);
        Assert.IsNull(store.FindSpawnAssignment("assignment", "intruder"));
    }

    [TestMethod]
    public void RestartBeforeAndAfterClaimPreservesOutcomeAndNeverReclaims()
    {
        var (store, path) = New();
        store = new SessionStore(path); store.ReconcileSpawnAssignments();
        Assert.AreEqual("not_started", (string?)store.FindSpawnAssignment("assignment", "parent")!["delivery"]);
        Assert.IsFalse(store.ClaimSpawnAssignment("child", "assignment", "prompt"));
        var (started, second) = New();
        Assert.IsTrue(started.ClaimSpawnAssignment("child", "assignment", "prompt"));
        started.TransitionSpawnAssignment("child", "assignment", "parent", 1, "cancel", "cancelled", "stop", generation: (string)started.Get("child")!.SpawnAssignment!["generation"]!);
        started = new SessionStore(second); started.ReconcileSpawnAssignments();
        Assert.AreEqual("uncertain", (string?)started.FindSpawnAssignment("assignment", "child")!["delivery"]);
        Assert.IsFalse(started.ClaimSpawnAssignment("child", "assignment", "prompt"));
        Assert.IsFalse(started.RecordSpawnDelivery("child", "wrong-prompt", "delivered"));
        Assert.IsTrue(started.RecordSpawnDelivery("child", "prompt", "delivered"));
        var after = new SessionStore(second).FindSpawnAssignment("assignment", "parent")!;
        Assert.AreEqual("cancelled", (string?)after["status"]); Assert.AreEqual("delivered", (string?)after["delivery"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => started.TransitionSpawnAssignment("child", "assignment", "child", 2, "late", "completed", "done", generation: (string)started.Get("child")!.SpawnAssignment!["generation"]!));
    }

    [TestMethod]
    public void RemovedChildRetainsHiddenAssignmentAndBoundedRetryReceipts()
    {
        var (store, path) = New();
        for (long revision = 1; revision <= 80; revision++)
            store.TransitionSpawnAssignment("child", "assignment", "child", revision, "op-" + revision,
                revision % 2 == 1 ? "blocked" : "open", revision % 2 == 1 ? "waiting" : "", generation: (string)store.Get("child")!.SpawnAssignment!["generation"]!);
        store.TransitionSpawnAssignment("child", "assignment", "parent", 81, "end", "cancelled", "stop", generation: (string)store.Get("child")!.SpawnAssignment!["generation"]!);
        store.Remove("child"); store.Flush(); store = new SessionStore(path);
        Assert.IsTrue(store.Get("child")!.Deleted);
        Assert.AreEqual("cancelled", (string?)store.FindSpawnAssignment("assignment", "parent")!["status"]);
        Assert.AreEqual(82L, (long)store.TransitionSpawnAssignment("child", "assignment", "parent", 81, "end", "cancelled", "stop", generation: (string)store.Get("child")!.SpawnAssignment!["generation"]!)["revision"]!);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionSpawnAssignment("child", "assignment", "child", 1, "op-1", "blocked", "waiting", generation: (string)store.Get("child")!.SpawnAssignment!["generation"]!));
    }
    [TestMethod]
    public void RemovedChildCannotBeClaimedButItsAssignmentRemainsVisible()
    {
        var (store, path) = New(); store.Remove("child");
        Assert.IsFalse(store.ClaimSpawnAssignment("child", "assignment", "prompt"));
        var recovered = new SessionStore(path); Assert.IsTrue(recovered.Get("child")!.Deleted);
        Assert.AreEqual("not_started", (string?)recovered.FindSpawnAssignment("assignment", "parent")!["delivery"]);
        recovered.ReconcileSpawnAssignments();
        Assert.AreEqual("not_started", (string?)recovered.FindSpawnAssignment("assignment", "parent")!["delivery"]);
    }

    [TestMethod]
    public void CapacityAndEvictionAreAtomicAndDoNotRecreateOldAssignments()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var store = new SessionStore(path) { SpawnAssignmentCapacity = 1 };
        store.Update("one", "fake", r => { r.Draft = true; r.ParentSessionId = "parent"; });
        store.Update("two", "fake", r => { r.Draft = true; r.ParentSessionId = "parent"; });
        store.CommitSpawnAssignment("one", "first");
        store.CommitSpawnAssignment("one", "first");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.CommitSpawnAssignment("two", "second"));
        Assert.IsNull(store.Get("two")!.SpawnAssignment);
        store.TransitionSpawnAssignment("one", "first", "parent", 1, "cancel", "cancelled", "stop", generation: (string)store.Get("one")!.SpawnAssignment!["generation"]!);
        var writer = store.WriteFile; store.WriteFile = (_, _, _) => throw new IOException("fault");
        Assert.ThrowsExactly<IOException>(() => store.CommitSpawnAssignment("two", "second"));
        Assert.IsNotNull(store.FindSpawnAssignment("first", "parent"));
        Assert.IsNull(store.FindSpawnAssignment("second", "parent"));
        store.WriteFile = writer; store.CommitSpawnAssignment("two", "second");
        Assert.IsNull(store.FindSpawnAssignment("first", "parent"));
        Assert.AreEqual("first", store.Get("one")!.SpawnAssignmentId);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.CommitSpawnAssignment("one", "first"));
        var recovered = new SessionStore(path);
        Assert.IsNull(recovered.FindSpawnAssignment("first", "parent"));
        Assert.IsNotNull(recovered.FindSpawnAssignment("second", "parent"));
    }

    [TestMethod]
    public void UncertainBecomesNotStartedOnlyWithMatchingRecordedHostReceipt()
    {
        var (store, path) = New();
        Assert.IsTrue(store.ClaimSpawnAssignment("child", "assignment", "prompt", "report"));
        store.TransitionSpawnAssignment("child", "assignment", "parent", 1, "cancel", "cancelled", "stop", generation: (string)store.Get("child")!.SpawnAssignment!["generation"]!);
        store = new SessionStore(path); store.ReconcileSpawnAssignments();
        Assert.AreEqual("uncertain", (string?)store.FindSpawnAssignment("assignment", "parent")!["delivery"]);
        Assert.IsFalse(store.RecordSpawnDelivery("child", "prompt", "not_started"));
        Assert.IsFalse(store.RecordProvenUnstartedSpawn("child", "other-prompt", "report"));
        Assert.IsFalse(store.RecordProvenUnstartedSpawn("child", "prompt", "other-report"));
        Assert.AreEqual("uncertain", (string?)store.FindSpawnAssignment("assignment", "child")!["delivery"]);
        Assert.IsTrue(store.RecordProvenUnstartedSpawn("child", "prompt", "report"));
        var recovered = new SessionStore(path).FindSpawnAssignment("assignment", "parent")!;
        Assert.AreEqual("cancelled", (string?)recovered["status"]);
        Assert.AreEqual("not_started", (string?)recovered["delivery"]);
        Assert.AreEqual("report", (string?)recovered["hostRefusalReportId"]);
    }

    [TestMethod]
    public void LateTerminalAfterSettledRecordEvictionIsNoOp()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json"); var store = new SessionStore(path) { SpawnAssignmentCapacity = 1 };
        store.Update("one", "fake", r => { r.Draft = true; r.ParentSessionId = "parent"; });
        store.Update("two", "fake", r => { r.Draft = true; r.ParentSessionId = "parent"; });
        store.CommitSpawnAssignment("one", "first"); store.ClaimSpawnAssignment("one", "first", "prompt", "report");
        store.RecordSpawnDelivery("one", "prompt", "delivered");
        store.TransitionSpawnAssignment("one", "first", "one", 1, "complete", "completed", "done", generation: (string)store.Get("one")!.SpawnAssignment!["generation"]!);
        store.CommitSpawnAssignment("two", "second");
        Assert.IsNull(store.FindSpawnAssignment("first", "parent"));
        Assert.IsFalse(store.RecordSpawnDelivery("one", "prompt", "delivered"));
        Assert.IsFalse(store.RecordProvenUnstartedSpawn("one", "prompt", "report"));
        Assert.IsFalse(store.RecordSpawnDelivery("missing", "prompt", "failed"));
        Assert.AreEqual("queued", (string?)new SessionStore(path).FindSpawnAssignment("second", "parent")!["delivery"]);
    }

    [TestMethod]
    public void RecreatedChildAndReusedSpawnIdRejectOldGenerationAndReport()
    {
        var dir = TestDirs.New(); var path = System.IO.Path.Combine(dir, "sessions.json");
        var store = new SessionStore(path) { SpawnAssignmentCapacity = 1 };
        void Prepare(string child) => store.Update(child, "fake", r => { r.Draft = true; r.ParentSessionId = "parent"; });
        Prepare("child"); store.CommitSpawnAssignment("child", "same");
        var oldGeneration = (string)store.FindSpawnAssignment("same", "child")!["generation"]!;
        var reports = new ReportStore(System.IO.Path.Combine(dir, "reports"));
        string Publish(string turn, string generation)
        {
            var id = reports.Reserve(new ReportIdentity(turn, "child", "parent", ReportCause.Spawn,
                AssignmentId: "same", AssignmentGeneration: generation)).ReportId;
            using var capture = reports.OpenCapture(id); capture.Append("result"); capture.Seal("ended"); reports.Commit(id, capture); return id;
        }
        var oldReport = Publish("old", oldGeneration);
        store.ClaimSpawnAssignment("child", "same", "prompt"); store.RecordSpawnDelivery("child", "prompt", "delivered");
        store.TransitionSpawnAssignment("child", "same", "child", 1, "complete-old", "completed", "done", generation: oldGeneration);
        Prepare("filler"); store.CommitSpawnAssignment("filler", "filler-id");
        store.TransitionSpawnAssignment("filler", "filler-id", "parent", 1, "cancel", "cancelled", "stop", generation: (string)store.Get("filler")!.SpawnAssignment!["generation"]!);
        store.Remove("child"); store = new SessionStore(path) { SpawnAssignmentCapacity = 1 };
        Prepare("child"); store.CommitSpawnAssignment("child", "same");
        var newGeneration = (string)store.FindSpawnAssignment("same", "child")!["generation"]!;
        Assert.AreNotEqual(oldGeneration, newGeneration);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TransitionSpawnAssignment("child", "same", "child", 1, "stale", "completed", "done", generation: oldGeneration));
        Assert.ThrowsExactly<InvalidOperationException>(() => reports.WithAssignmentReport(oldReport, "child", "parent", "child", "same", newGeneration,
            ReportCause.Spawn, () => store.TransitionSpawnAssignment("child", "same", "child", 1, "bad", "completed", "done", oldReport, newGeneration)));
        var freshReport = Publish("fresh", newGeneration);
        reports.WithAssignmentReport(freshReport, "child", "parent", "child", "same", newGeneration,
            ReportCause.Spawn, () => store.TransitionSpawnAssignment("child", "same", "child", 1, "fresh-op", "completed", "done", freshReport, newGeneration));
        store = new SessionStore(path);
        Assert.AreEqual("completed", (string?)store.RetrySpawnTransition("child", "same", "child", 1, "fresh-op", "completed", "done", freshReport, newGeneration)!["status"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.RetrySpawnTransition("child", "same", "child", 1, "fresh-op", "completed", "done", freshReport, oldGeneration));
    }

}
