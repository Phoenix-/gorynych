using System.Collections.Concurrent;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// A thread changing hands in another Codex app reaches clients as a list change (docs/adapters.md,
/// "One writer"): the adapter watches Codex's lock folder, finds the holders again once a burst of
/// changes settles and on a timer, and tells the service only of a real change, which the service
/// passes on as <c>_gorynych/sessions_changed</c> <c>updated</c>. The holders are scripted; the lock
/// folder and its file events are real.
/// </summary>
[TestClass]
public sealed class CodexLockWatcherTests
{
    static readonly string Cwd = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

    sealed class Rig : IAsyncDisposable
    {
        public required string Locks { get; init; }
        public required ConcurrentDictionary<string, string> Held { get; init; }
        public required Conductor Conductor { get; init; }
        public required TestClient Zed { get; init; }

        public int Updates(string id) => Zed.ChangeLog().Count(c => c == "updated " + id);

        public async ValueTask DisposeAsync()
        {
            await Zed.DisposeAsync();
            await Conductor.DisposeAsync();
        }
    }

    static async Task<Rig> StartAsync(TimeSpan recheck, bool folder = true, params string[] threads)
    {
        var locks = Path.Combine(TestDirs.New(), "thread-writer-locks");
        if (folder) Directory.CreateDirectory(locks);
        var held = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var fake = new FakeAppServer();
        foreach (var id in threads) fake.Existing(id, Cwd, name: id);
        var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(),
            ServiceId = "t",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        }, [new CodexAdapter(new CodexAdapterOptions
        {
            Launch = fake.StartAsync,
            ClientVersion = "test",
            FindHolders = ids => ids.Where(held.ContainsKey).ToDictionary(id => id, id => held[id]),
            LocksDirectory = locks,
            LockSettle = TimeSpan.FromMilliseconds(100),
            LockRecheck = recheck,
        })]);
        var zed = await TestClient.ConnectAsync(conductor, "zed", agent: "codex");
        await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest()); // the service lists them
        return new Rig { Locks = locks, Held = held, Conductor = conductor, Zed = zed };
    }

    static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 250; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ALockFileThatAppearsOrGoes_IsToldOnce_AsAListChange()
    {
        await using var rig = await StartAsync(TimeSpan.FromHours(1), threads: ["t1", "t2"]);
        var file = Path.Combine(rig.Locks, "t1.lock");

        rig.Held["t1"] = "Codex Desktop";
        File.WriteAllBytes(file, []); // Codex Desktop took it
        await Until(() => rig.Updates("t1") == 1, "the notice that t1 was taken");
        var listed = await rig.Zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
        Assert.AreEqual("Codex Desktop", (string?)listed.Sessions.Single(s => s.SessionId == "t1").Meta?["gorynych/openElsewhere"], "listed again, it shows the holder");

        rig.Held.TryRemove("t1", out _);
        File.Delete(file); // and let it go
        await Until(() => rig.Updates("t1") == 2, "the notice that t1 was let go");

        await Task.Delay(500);
        Assert.AreEqual(2, rig.Updates("t1"), "once each");
        Assert.AreEqual(0, rig.Updates("t2"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHolderThatLeftNoEvent_IsFoundGone_ByTheTimer()
    {
        var held = new[] { "t1" };
        await using var rig = await StartAsync(TimeSpan.FromMilliseconds(300), threads: held);
        // Held since before the adapter started: no event, only the list has seen it held.
        rig.Held["t1"] = "a terminal codex (pid 4242)";
        await rig.Zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
        await Task.Delay(600);
        var before = rig.Updates("t1");
        Assert.AreEqual(0, before, "the list showed it held already: nothing to tell");

        rig.Held.TryRemove("t1", out _); // the terminal codex crashed: its lock file stays

        await Until(() => rig.Updates("t1") == before + 1, "the timer finding it free");
        await Task.Delay(700);
        Assert.AreEqual(before + 1, rig.Updates("t1"), "told once");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task FilesThatChangeNoHolder_TellNothing()
    {
        await using var rig = await StartAsync(TimeSpan.FromMilliseconds(300), threads: ["t1"]);

        File.WriteAllBytes(Path.Combine(rig.Locks, "t1.lock"), []); // the user's own app-server, say: no other holder
        File.WriteAllBytes(Path.Combine(rig.Locks, ".coordination.lock"), []);
        File.Delete(Path.Combine(rig.Locks, "t1.lock"));
        await Task.Delay(1000);

        Assert.AreEqual(0, rig.Updates("t1"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AFolderThatIsNotThereYet_IsWatchedOnceItIs()
    {
        await using var rig = await StartAsync(TimeSpan.FromMilliseconds(200), folder: false, threads: ["t1"]);
        await Task.Delay(300);

        Directory.CreateDirectory(rig.Locks); // Codex's first thread ever
        await Task.Delay(600); // the timer finds the folder
        rig.Held["t1"] = "Codex Desktop";
        File.WriteAllBytes(Path.Combine(rig.Locks, "t1.lock"), []);

        await Until(() => rig.Updates("t1") == 1, "the notice once the folder is watched");
    }
}
