using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

/// <summary>
/// The Claude adapter's stores are shared by several adapter host processes on one data folder. Two
/// instances of a store on the same path stand for two processes here.
/// </summary>
[TestClass]
public sealed class ClaudeStoresTests
{
    [TestMethod]
    public void State_UpdatesFromBothInstances_AllSurvive()
    {
        var path = Path.Combine(TestDirs.New(), "claude", "sessions.json");
        var a = new ClaudeStateStore(path);
        var b = new ClaudeStateStore(path);

        a.Update("s1", "/w", r => r.Title = "one");
        b.Update("s2", "/w", r => r.Title = "two");
        a.Update("s3", "/w", r => r.Model = "opus");

        foreach (var store in new[] { a, b, new ClaudeStateStore(path) })
            CollectionAssert.AreEquivalent(new[] { "s1", "s2", "s3" }, store.All().Select(r => r.SessionId).ToArray());
        Assert.AreEqual("two", a.Get("s2")!.Title);
        Assert.AreEqual("opus", b.Get("s3")!.Model);
    }

    [TestMethod]
    public void State_ARecordChangedThroughOneInstance_IsSeenByTheOthersGet()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var a = new ClaudeStateStore(path);
        var b = new ClaudeStateStore(path);
        a.Update("s1", "/w", r => r.Title = "old");
        Assert.AreEqual("old", b.Get("s1")!.Title);

        a.Update("s1", "/w", r => r.Title = "new");

        Assert.AreEqual("new", b.Get("s1")!.Title);
    }

    [TestMethod]
    public void State_MissingOrUnreadableFile_ReadsAsEmpty()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var store = new ClaudeStateStore(path);
        Assert.AreEqual(0, store.All().Count);
        File.WriteAllText(path, "{ not json");
        Assert.AreEqual(0, new ClaudeStateStore(path).All().Count);
    }

    [TestMethod]
    public async Task State_ConcurrentUpdatesAcrossTwoInstances_LoseNone()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var stores = new[] { new ClaudeStateStore(path), new ClaudeStateStore(path) };

        var tasks = new List<Task>();
        for (var s = 0; s < 2; s++)
            for (var t = 0; t < 4; t++)
            {
                var store = stores[s];
                var prefix = $"s{s}t{t}-";
                tasks.Add(Task.Factory.StartNew(() =>
                {
                    for (var i = 0; i < 25; i++) store.Update(prefix + i, "/w", r => r.Title = "x");
                }, TaskCreationOptions.LongRunning));
            }
        await Task.WhenAll(tasks);

        Assert.AreEqual(200, new ClaudeStateStore(path).All().Count);
        Assert.AreEqual(200, stores[0].All().Count);
        Assert.AreEqual(200, stores[1].All().Count);
    }

    [TestMethod]
    public void Trust_FoldersAddedThroughBothInstances_AreAllKept()
    {
        var dir = TestDirs.New();
        var store = Path.Combine(dir, "claude", "trusted.json");
        var noClaudeState = Path.Combine(dir, "none.json");
        var a = new ClaudeTrust(store, noClaudeState, null);
        var b = new ClaudeTrust(store, noClaudeState, null);
        var f1 = Path.Combine(dir, "one");
        var f2 = Path.Combine(dir, "two");
        var f3 = Path.Combine(dir, "three");

        Assert.IsFalse(a.IsTrusted(f1));
        a.Remember(f1);
        b.Remember(f2);
        a.Remember(f3);

        foreach (var t in new[] { a, b, new ClaudeTrust(store, noClaudeState, null) })
        {
            Assert.IsTrue(t.IsTrusted(f1));
            Assert.IsTrue(t.IsTrusted(f2));
            Assert.IsTrue(t.IsTrusted(f3));
            Assert.IsTrue(t.IsTrusted(Path.Combine(f2, "child")));
        }
    }

    [TestMethod]
    public async Task Trust_ConcurrentAdds_LoseNone()
    {
        var dir = TestDirs.New();
        var store = Path.Combine(dir, "trusted.json");
        var noClaudeState = Path.Combine(dir, "none.json");
        var trusts = new[] { new ClaudeTrust(store, noClaudeState, null), new ClaudeTrust(store, noClaudeState, null) };

        await Task.WhenAll(Enumerable.Range(0, 2).Select(s => Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < 20; i++) trusts[s].Remember(Path.Combine(dir, $"f{s}-{i}"));
        }, TaskCreationOptions.LongRunning)));

        var fresh = new ClaudeTrust(store, noClaudeState, null);
        for (var s = 0; s < 2; s++)
            for (var i = 0; i < 20; i++)
                Assert.IsTrue(fresh.IsTrusted(Path.Combine(dir, $"f{s}-{i}")), $"f{s}-{i}");
    }

    [TestMethod]
    public void Consent_AnAnswerSavedThroughOneInstance_IsSeenByTheOther()
    {
        var path = Path.Combine(TestDirs.New(), "claude", "channels.json");
        var a = new ChannelConsentStore(path);
        var b = new ChannelConsentStore(path);
        Assert.IsNull(b.Answer);

        a.Save(true, "s1");
        Assert.AreEqual(true, b.Answer);

        b.Save(false, "s2");
        Assert.AreEqual(false, a.Answer);
        Assert.AreEqual(false, new ChannelConsentStore(path).Answer);
    }

    [TestMethod]
    public void Catalog_ACatalogSavedThroughOneInstance_IsLoadedByTheOther()
    {
        var path = Path.Combine(TestDirs.New(), "claude", "catalog.json");
        var a = new ClaudeCatalogStore(path, null);
        var b = new ClaudeCatalogStore(path, null);
        Assert.AreEqual(0, b.Load().Count);

        Assert.IsTrue(a.Save(Models("opus")));
        Assert.AreEqual("opus", b.Load().Single().Value);

        Assert.IsFalse(b.Save(Models("opus")));
        Assert.IsTrue(b.Save(Models("sonnet")));
        Assert.AreEqual("sonnet", a.Load().Single().Value);
    }

    [TestMethod]
    public void Lock_IsExclusive_AWaiterGetsItOnceReleased()
    {
        var path = Path.Combine(TestDirs.New(), "x.json");
        var first = SharedFileLock.Acquire(path);
        var release = Task.Run(() => { Thread.Sleep(200); first.Dispose(); });

        var started = DateTime.UtcNow;
        SharedFileLock.Acquire(path).Dispose();

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100), DateTime.UtcNow - started);
        release.Wait();
    }

    static JsonArray Models(string value) => new(new JsonObject { ["value"] = value, ["displayName"] = value });
}
