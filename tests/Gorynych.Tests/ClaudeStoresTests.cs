using System.Text.Json;
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
    public void State_AChangeThatCouldNotBeSaved_GoesOverAnotherInstancesNewerRecord_FieldByField()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var a = new ClaudeStateStore(path);
        var b = new ClaudeStateStore(path);
        a.Update("s1", "/w", r => r.Title = "first");
        Assert.AreEqual("first", b.Get("s1")!.Title);

        using (SharedFileLock.Acquire(path)) // another process holds the file: a's change stays in memory
            a.Update("s1", "/w", r => r.Model = "opus");
        b.Update("s1", "/w", r => r.Title = "renamed"); // then another process renames it

        var seen = a.Get("s1")!;
        Assert.AreEqual(("renamed", "opus"), (seen.Title, seen.Model), "the newer title is kept, and only a's own change goes over it");
        a.Update("s2", "/w", _ => { }); // the next change that is saved writes the one kept in memory too
        var fresh = new ClaudeStateStore(path).Get("s1")!;
        Assert.AreEqual(("renamed", "opus"), (fresh.Title, fresh.Model));
    }

    [TestMethod]
    public void Consent_AnAnswerThatCouldNotBeSaved_HoldsUntilTheFileHasALaterOne()
    {
        var path = Path.Combine(TestDirs.New(), "claude", "channels.json");
        var a = new ChannelConsentStore(path);
        var b = new ChannelConsentStore(path);
        b.Save(false, "s0");
        Assert.AreEqual(false, a.Answer);
        using (SharedFileLock.Acquire(path))
            a.Save(true, "s1"); // not saved
        Assert.AreEqual(true, a.Answer);

        // Another process writes an answer given before this one: it does not replace it. Then a later
        // one, which does, in the same tick of the file system's clock: whole seconds, so it is as long.
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        string Answer(bool accepted, DateTimeOffset at) =>
            JsonSerializer.Serialize(new ChannelConsentRecord { Accepted = accepted, AnsweredAt = at, SessionId = "other" }, ClaudeJsonContext.Default.ChannelConsentRecord);
        AtomicFile.WriteAllText(path, Answer(false, now.AddMinutes(-1)));
        Assert.AreEqual(true, a.Answer, "an earlier answer on disk does not replace the later one in memory");
        InOneTick(path, () => AtomicFile.WriteAllText(path, Answer(false, now.AddMinutes(1))));
        Assert.AreEqual(false, a.Answer, "a later one does");
    }

    [TestMethod]
    public void State_AChangeAsLongAsTheRecordBefore_InOneTickOfTheClock_IsSeenByTheOther()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var a = new ClaudeStateStore(path);
        var b = new ClaudeStateStore(path);
        a.Update("s1", "/w", r => r.Title = "old");
        Assert.AreEqual("old", b.Get("s1")!.Title);

        InOneTick(path, () => a.Update("s1", "/w", r => r.Title = "new"));

        Assert.AreEqual("new", b.Get("s1")!.Title);
        Assert.AreEqual("new", b.All().Single().Title);
    }

    [TestMethod]
    public void Trust_AFolderPutInAnothersPlace_InOneTickOfTheClock_IsSeenByTheOther()
    {
        var dir = TestDirs.New();
        var store = Path.Combine(dir, "trusted.json");
        var noClaudeState = Path.Combine(dir, "none.json");
        var (one, two) = (Path.Combine(dir, "one"), Path.Combine(dir, "two"));
        var a = new ClaudeTrust(store, noClaudeState, null);
        var b = new ClaudeTrust(store, noClaudeState, null);
        a.Remember(one);
        Assert.IsTrue(b.IsTrusted(one));

        // The person, or another process, puts another folder in its place.
        InOneTick(store, () => AtomicFile.WriteAllText(store, File.ReadAllText(store).Replace("/one\"", "/two\"", StringComparison.Ordinal)));

        Assert.IsTrue(b.IsTrusted(two));
        Assert.IsFalse(b.IsTrusted(one));
    }

    /// <summary>
    /// Runs a write of the file, then gives it back the write time it had before, as when two writes fall
    /// in one tick of the clock the file system stamps them with (about 15 ms on Windows). The write must
    /// keep the file's length: then its write time and length cannot tell that it changed.
    /// </summary>
    static void InOneTick(string path, Action write)
    {
        var before = new FileInfo(path);
        var (time, length) = (before.LastWriteTimeUtc, before.Length);
        var text = File.ReadAllText(path);
        write();
        File.SetLastWriteTimeUtc(path, time);
        var after = new FileInfo(path);
        Assert.AreEqual((time, length), (after.LastWriteTimeUtc, after.Length), "the same write time and length as before");
        Assert.AreNotEqual(text, File.ReadAllText(path), "and yet another text");
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
