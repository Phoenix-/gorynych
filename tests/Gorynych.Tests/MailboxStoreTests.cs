using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public class MailboxStoreTests
{
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
}
