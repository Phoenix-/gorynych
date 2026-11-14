using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Dsh;

namespace Gorynych.Tests;

/// <summary>
/// DSH's native titles (docs/adapters.md, "DeepSeek Harness", "Titles"): the acp profile's title
/// provider is enabled by Gorynych's patch, and the adapter follows the title DSH writes into its
/// projection cache, read only, and sends it as a standard <c>session_info_update</c>.
/// </summary>
public sealed partial class DshAdapterTests
{
    static void WriteCache(string home, string sessionId, string? title, int version = 7)
    {
        var dir = Path.Combine(home, "storages", "session_projcache", "sessions");
        Directory.CreateDirectory(dir);
        var record = new JsonObject
        {
            ["version"] = version,
            ["record"] = new JsonObject
            {
                ["identity"] = new JsonObject { ["formatVersion"] = 3, ["createdAt"] = 1, ["isSeeded"] = false, ["inheritedEventCount"] = 0 },
                ["rows"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["ver"] = 1, ["seq"] = 2, ["val"] = title },
                },
            },
        };
        var target = Path.Combine(dir, sessionId + ".json");
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, record.ToJsonString());
        File.Move(temp, target, overwrite: true);
    }

    static bool HasTitle(Service service, string title)
    {
        lock (service.Updates) return service.Updates.Any(u => (string?)u["title"] == title);
    }

    static async Task WaitForTitle(Service service, string title)
    {
        await Until(() => HasTitle(service, title), $"the title \"{title}\"");
    }

    [TestMethod]
    public async Task Patch_EnablesDshsOwnTitleProvider()
    {
        var (service, dsh, _) = await StartAsync();
        await using var _ = service;
        StringAssert.Contains(File.ReadAllText(dsh.PatchPath!), "- id: session-title-llm", "the acp profile disables the provider; Gorynych turns it back on");
        StringAssert.Contains(File.ReadAllText(dsh.PatchPath!), "disabled: false");
    }

    [TestMethod]
    public async Task NativeTitle_IsReadFromTheProjectionCache_AndAnnounced_AlsoWhenReplacedAtomically()
    {
        var home = TestDirs.New();
        var (service, _, _) = await StartAsync(dshHome: home, titlePoll: TimeSpan.FromMilliseconds(20));
        await using var _ = service;
        var id = await NewAsync(service, Work);

        WriteCache(home, id, "Native title");
        await WaitForTitle(service, "Native title");

        var listed = await service.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject());
        var session = listed!["sessions"]!.AsArray().Single(s => (string?)s!["sessionId"] == id)!;
        Assert.AreEqual("Native title", (string?)session["title"]);

        // DSH replaces a record atomically; the watcher reads the replacement, never writes it.
        WriteCache(home, id, "Replaced title");
        await WaitForTitle(service, "Replaced title");
        Assert.AreEqual("Replaced title", DshPins.TitleOf(JsonNode.Parse(File.ReadAllText(Path.Combine(home, "storages", "session_projcache", "sessions", id + ".json")))));
    }

    [TestMethod]
    public async Task ARecordOfAnotherFormat_AndAMissingTitle_AreIgnored()
    {
        var home = TestDirs.New();
        var (service, _, _) = await StartAsync(dshHome: home, titlePoll: TimeSpan.FromMilliseconds(20));
        await using var _ = service;
        var id = await NewAsync(service, Work);

        WriteCache(home, id, "Old format title", version: 6);
        await Task.Delay(300);
        Assert.IsFalse(HasTitle(service, "Old format title"));

        WriteCache(home, id, null);
        await Task.Delay(200);
        Assert.IsFalse(HasTitle(service, "Old format title"));
    }

    [TestMethod]
    public async Task ATitleIsListedForASessionThatIsNotLoaded_AndAfterARestart()
    {
        var home = TestDirs.New();
        var (first, dsh, _) = await StartAsync(dshHome: home, titlePoll: TimeSpan.FromMilliseconds(20));
        var id = await NewAsync(first, Work);
        await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
        WriteCache(home, id, "Stored title");
        await WaitForTitle(first, "Stored title");
        await first.DisposeAsync();

        // A restarted adapter with the same DSH home: no live session, only the cache and DSH's list.
        var restarted = new FakeDsh(dsh.Store);
        var data = TestDirs.New();
        var adapter = new DshAdapter(new DshAdapterOptions
        {
            DataDirectory = data, Launch = restarted.LaunchAsync, DshHome = home, TitlePollInterval = TimeSpan.FromMilliseconds(20),
        });
        var instance = await adapter.StartAsync(CancellationToken.None);
        var rpc = new JsonRpcConnection(instance.Input, instance.Output, "service");
        var service = new Service { Instance = instance, Rpc = rpc };
        rpc.NotificationHandler = (method, p) =>
        {
            if (method == AcpMethods.Client.SessionUpdate && p?["update"] is JsonObject u) lock (service.Updates) service.Updates.Add(u);
            return Task.CompletedTask;
        };
        rpc.Start();
        try
        {
            await rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 });
            var listed = await rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject());
            var session = listed!["sessions"]!.AsArray().Single(s => (string?)s!["sessionId"] == id)!;
            Assert.AreEqual("Stored title", (string?)session["title"], "a listed session carries its native title after a restart");
        }
        finally
        {
            await service.DisposeAsync();
        }
    }
}
