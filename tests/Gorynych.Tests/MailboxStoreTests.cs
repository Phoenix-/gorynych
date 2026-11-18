using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public class MailboxStoreTests
{
    [TestMethod]
    public void DelegationRetrySurvivesRestartAndCannotCollideWithOrdinaryMessages()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path);
        store.Accept("ordinary", "parent", "child", "task");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("ordinary", "parent", "child", "task", delegation: true));
        var item = store.Accept("assignment", "parent", "child", "task", delegation: true);
        Assert.AreEqual("assignment", (string?)item["assignmentId"]);
        store = new MailboxStore(path);
        Assert.AreEqual("assignment", (string?)store.Accept("assignment", "parent", "child", "task", delegation: true)["assignmentId"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("assignment", "parent", "child", "task"));
        Assert.AreEqual(2, store.Read("child", 0, 10).Count);
    }
    [TestMethod]
    public void FailureAndItsNotice_ArePersistedTogetherEvenAtCapacity()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, 1);
        store.Accept("failed", "alice", "bob", "hello");
        store.MarkFailed("failed", "bob", "cannot start", "alice");
        var recovered = new MailboxStore(path, 1);
        var notice = recovered.Read("alice", 0, 10).Single();
        Assert.AreEqual("service", (string?)notice["sender"]);
        StringAssert.Contains((string?)notice["text"], "cannot start");
        Assert.AreEqual(0, recovered.Read("bob", 0, 10, true).Count);
    }

    [TestMethod]
    public void RecoveryRetryAndReplyOwnership()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gorynych-mailbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "mailbox.json");
            var store = new MailboxStore(path, 3);
            store.Accept("one", "alice", "bob", "hello");
            store = new MailboxStore(path, 3);
            Assert.AreEqual("one", (string?)store.Accept("one", "alice", "bob", "hello")["id"]);
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("one", "mallory", "bob", "hello"));
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("two", "mallory", "alice", "forged", "one"));
            store.Accept("two", "bob", "alice", "answer", "one");
            Assert.AreEqual("one", (string?)store.Read("alice", 0, 10)[0]["replyTo"]);
            Assert.AreEqual(1, store.Read("bob", 0, 10, pendingOnly: true).Count);
            store.MarkDelivered("one", "bob");
            store = new MailboxStore(path, 3);
            Assert.AreEqual(0, store.Read("bob", 0, 10, pendingOnly: true).Count);
            Assert.AreEqual(1, store.Read("bob", 0, 10).Count);
            Assert.AreEqual(0, store.Read("bob", 1, 10).Count);
            store.Accept("three", "alice", "bob", "last");
            store.Accept("four", "alice", "bob", "replacement");
            Assert.IsNull(store.Find("one"));
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("five", "alice", "bob", "full"));
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("reply", "bob", "alice", "too late", "one"));
            Assert.ThrowsExactly<ArgumentException>(() => store.Accept("large", "a", "b", new string('x', MailboxStore.MaxTextLength + 1)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void ConcurrentAcceptanceAndFailedWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gorynych-mailbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "mailbox.json");
            var store = new MailboxStore(path, 100);
            Parallel.For(0, 50, i => store.Accept(i.ToString(), "a", "b", "text"));
            var page = store.Read("b", 0, 10);
            Assert.AreEqual(10, page.Count);
            var cursor = (long)page[^1]["sequence"]!;
            Assert.AreEqual(40, store.Read("b", cursor, 100).Count);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.Read("b", -1, 10));
            // A missing parent makes AtomicFile fail before committing; no in-memory acceptance.
            var broken = new MailboxStore(Path.Combine(directory, "missing", "state.json"));
            Assert.ThrowsExactly<DirectoryNotFoundException>(() => broken.Accept("id", "a", "b", "text"));
            Assert.IsNull(broken.Find("id"));
            Assert.AreEqual(50, new MailboxStore(path, 100).Read("b", 0, 100).Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [TestMethod]
    public void ReplyWaitSurvivesMessageEvictionAndRetryAfterRestart()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, waitCapacity: 2);
        store.Accept("question", "alice", "bob", "hello", expectReply: true);
        store.MarkDelivered("question", "bob");
        store.Accept("other", "alice", "bob", "ordinary");
        Assert.IsNull(store.Find("question"));
        store.MarkDelivered("other", "bob");
        store = new MailboxStore(path, capacity: 1, waitCapacity: 2);
        store.Accept("question", "alice", "bob", "hello", expectReply: true);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("question", "alice", "bob", "changed", expectReply: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("bad", "mallory", "alice", "answer", "question"));
        store.Accept("answer", "bob", "alice", "answer", "question");
        var wait = store.ListReplyWaits("alice", 0, 10)["items"]![0]!;
        Assert.AreEqual("replied", (string?)wait["state"]);
        Assert.AreEqual("answer", (string?)wait["replyId"]);
        store.MarkDelivered("answer", "alice");
        store.Accept("later", "bob", "alice", "next");
        store = new MailboxStore(path, capacity: 1, waitCapacity: 2);
        store.Accept("answer", "bob", "alice", "answer", "question");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("answer", "bob", "alice", "different", "question"));
        Assert.AreEqual(0, store.ListReplyWaits("mallory", 0, 10)["items"]!.AsArray().Count);
    }

    [TestMethod]
    public void OpenWaitsAreNeverEvictedAndCancelDoesNotPreventReply()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 10, waitCapacity: 1);
        store.Accept("one", "alice", "bob", "question", expectReply: true);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("two", "alice", "bob", "question", expectReply: true));
        StringAssert.Contains(error.Message, "full of open waits");
        Assert.IsNull(store.Find("two"));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.CancelReplyWait("one", "bob"));
        Assert.AreEqual("cancelled", (string?)store.CancelReplyWait("one", "alice")["state"]);
        store.Accept("reply", "bob", "alice", "answer", "one");
        Assert.AreEqual("cancelled", (string?)store.CancelReplyWait("one", "alice")["state"]);
        store.Accept("two", "alice", "bob", "question", expectReply: true);
        Assert.AreEqual("two", (string?)store.ListReplyWaits("bob", 0, 10)["items"]![0]!["messageId"]);
    }

    [TestMethod]
    public void ReplyWaitFailedWriteDoesNotPublishMessageOrWait()
    {
        var path = Path.Combine(TestDirs.New(), "missing", "mailbox.json");
        var store = new MailboxStore(path);
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => store.Accept("id", "a", "b", "text", expectReply: true));
        Assert.IsNull(store.Find("id"));
        Assert.AreEqual(0, store.ListReplyWaits("a", 0, 10)["items"]!.AsArray().Count);
    }

    [TestMethod]
    public void FailedResolutionAndCancellationLeaveDurableWaitUnchanged()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path);
        store.Accept("question", "alice", "bob", "text", expectReply: true);
        store.WriteFile = (_, _) => throw new IOException("injected write failure");
        Assert.ThrowsExactly<IOException>(() => store.Accept("reply", "bob", "alice", "answer", "question"));
        Assert.ThrowsExactly<IOException>(() => store.CancelReplyWait("question", "alice"));
        Assert.IsNull(store.Find("reply"));
        Assert.AreEqual("waiting", (string?)store.ListReplyWaits("alice", 0, 10)["items"]![0]!["state"]);
        store = new MailboxStore(path);
        Assert.IsNull(store.Find("reply"));
        Assert.AreEqual("waiting", (string?)store.ListReplyWaits("alice", 0, 10)["items"]![0]!["state"]);
        store.Accept("reply", "bob", "alice", "answer", "question");
        Assert.AreEqual("replied", (string?)new MailboxStore(path).ListReplyWaits("alice", 0, 10)["items"]![0]!["state"]);
    }

    [TestMethod]
    public void ReplyCancellationRaceKeepsReplyDeliverableAndOneDisposition()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path);
        store.Accept("q", "a", "b", "text", expectReply: true);
        Parallel.Invoke(() => store.CancelReplyWait("q", "a"), () => store.Accept("r", "b", "a", "answer", "q"));
        var wait = new MailboxStore(path).ListReplyWaits("a", 0, 10)["items"]![0]!;
        Assert.IsTrue((string?)wait["state"] is "cancelled" or "replied");
        Assert.AreEqual("r", (string?)new MailboxStore(path).Find("r")!["id"]);
        Assert.AreEqual("r", (string?)wait["replyId"]);
    }

    [TestMethod]
    public void CancelledWaitRetainsFirstReplyRetryAfterBodyEviction()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1);
        store.Accept("q", "a", "b", "question", expectReply: true);
        store.CancelReplyWait("q", "a");
        store.MarkDelivered("q", "b");
        store.Accept("r", "b", "a", "answer", "q");
        store.MarkDelivered("r", "a");
        store.Accept("other", "a", "b", "other");
        store = new MailboxStore(path, capacity: 1);
        store.Accept("r", "b", "a", "answer", "q");
        Assert.AreEqual("cancelled", (string?)store.ListReplyWaits("a", 0, 10)["items"]![0]!["state"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("r", "b", "a", "changed", "q"));
        Assert.AreEqual(0, store.Read("a", 0, 10, pendingOnly: true).Count);
    }

    [TestMethod]
    public void RetryOfExpiredWaitDoesNotPromiseAnExpectation()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 10, waitCapacity: 1);
        store.Accept("old", "a", "b", "question", expectReply: true);
        store.CancelReplyWait("old", "a");
        store.Accept("new", "a", "b", "other", expectReply: true);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => store.Accept("old", "a", "b", "question", expectReply: true));
        StringAssert.Contains(error.Message, "expired");
        Assert.AreEqual("new", (string?)store.ListReplyWaits("a", 0, 10)["items"]![0]!["messageId"]);
    }

    [TestMethod]
    public void MutualWaitsArePagedByParticipantAndKeepDeliveryUncertainty()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path);
        store.Accept("ordinary", "a", "b", "notification");
        Assert.AreEqual(0, store.ListReplyWaits("a", 0, 10)["items"]!.AsArray().Count);
        store.Accept("one", "a", "b", "question 1", expectReply: true);
        store.Accept("two", "b", "a", "question 2", expectReply: true);
        store.MarkStarted("one", "b");
        store.MarkFailed("one", "b", "handoff unknown", uncertain: true);
        store = new MailboxStore(path);
        var first = store.ListReplyWaits("a", 0, 1);
        Assert.AreEqual("uncertain", (string?)first["items"]![0]!["requestDelivery"]);
        Assert.AreEqual("waiting", (string?)first["items"]![0]!["state"]);
        var cursor = long.Parse((string)first["nextCursor"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual("two", (string?)store.ListReplyWaits("a", cursor, 1)["items"]![0]!["messageId"]);
        Assert.AreEqual(2, store.ListReplyWaits("b", 0, 10)["items"]!.AsArray().Count);
        Assert.AreEqual(0, store.ListReplyWaits("c", 0, 10)["items"]!.AsArray().Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RetryOnlyNeverReadmitsAfterObservedIdentityIsEvicted(bool firstReply)
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path, capacity: 1, waitCapacity: 1);
        store.Accept("q", "alice", "bob", "question", expectReply: true);
        store.MarkDelivered("q", "bob");
        if (firstReply)
        {
            store.Accept("r", "bob", "alice", "answer", "q");
            store.MarkDelivered("r", "alice");
        }
        else store.CancelReplyWait("q", "alice");
        var id = firstReply ? "r" : "q";
        var sender = firstReply ? "bob" : "alice";
        var target = firstReply ? "q" : "bob";
        var text = firstReply ? "answer" : "question";
        Assert.IsNotNull(store.FindAddress(id), "the previous lookup observed an identity");
        store.Accept("new", "alice", "bob", "new question", expectReply: true);
        Assert.IsNull(store.FindAddress(id), "both message and terminal wait correlation were evicted");
        Assert.IsNull(store.TryRetry(id, sender, target, text, firstReply, expectReply: !firstReply));
        Assert.IsNull(store.Find(id), "retry-only must never create a message or dispatchable work");
        Assert.AreEqual(1, store.Read("bob", 0, 10, pendingOnly: true).Count);
        Assert.AreEqual(0, store.Read("alice", 0, 10, pendingOnly: true).Count);
        Assert.AreEqual("new", (string?)new MailboxStore(path, 1, 1).ListReplyWaits("alice", 0, 10)["items"]![0]!["messageId"]);
    }

    [TestMethod]
    public void RetryOnlyValidatesRetainedIdentityAndDoesNotMutate()
    {
        var path = Path.Combine(TestDirs.New(), "mailbox.json");
        var store = new MailboxStore(path);
        store.Accept("q", "alice", "bob", "question", expectReply: true);
        Assert.AreEqual("bob", store.TryRetry("q", "alice", "bob", "question", false, expectReply: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TryRetry("q", "mallory", "bob", "question", false, expectReply: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TryRetry("q", "alice", "bob", "changed", false, expectReply: true));
        Assert.AreEqual(1, store.Read("bob", 0, 10, pendingOnly: true).Count);
    }

}
