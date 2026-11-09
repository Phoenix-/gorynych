using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Dsh;

namespace Gorynych.Tests;

/// <summary>The DSH router when a switch or a process fails: the session is never left full access by mistake, nor open nowhere.</summary>
public sealed partial class DshAdapterTests
{
    static string ModesPath(string data) => Path.Combine(data, DshAdapter.ModesFile);

    /// <summary>Makes the modes file unwritable: its temporary file's name is taken by a folder.</summary>
    static DirectoryInfo Unwritable(string data) => Directory.CreateDirectory(ModesPath(data) + ".tmp");

    [TestMethod]
    public async Task LoweringTheMode_IsRefused_WhenItCannotBeSaved_AndTheSessionStaysInFullAccess()
    {
        var (service, host, data) = await StartModesAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        await SetModeAsync(service, id, "full-access");
        var stuck = Unwritable(data);

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SetModeAsync(service, id, "read-only"));

        StringAssert.Contains(refused.Message, "stays in full access");
        Assert.IsTrue(host.FullAccess.IsOpen(id));
        Assert.AreEqual(0, CallsTo(host.FullAccess, AcpMethods.Agent.SessionClose, id), "nothing was moved");
        Assert.AreEqual(DshModes.FullAccess, new DshModeStore(ModesPath(data), null).Get(id), "the file still says full access, as the client does");

        stuck.Delete();
        Assert.AreEqual("read-only", ModeIn(await SetModeAsync(service, id, "read-only")));
        Assert.AreEqual(DshModes.ReadOnly, new DshModeStore(ModesPath(data), null).Get(id));
    }

    [TestMethod]
    public async Task RaisingTheMode_WhenItCannotBeSaved_SwitchesForNow_AndOpensReadOnlyNextTime()
    {
        var log = new List<string>();
        var (service, host, data) = await StartModesAsync(log: log);
        await using var _ = service;
        var id = await NewAsync(service, Work);
        Unwritable(data);

        Assert.AreEqual("full-access", ModeIn(await SetModeAsync(service, id, "full-access")));

        Assert.IsTrue(host.FullAccess.IsOpen(id));
        lock (log) Assert.IsTrue(log.Any(l => l.Contains("opens read-only next time", StringComparison.Ordinal)), string.Join(" | ", log));
        Assert.AreEqual(DshModes.ReadOnly, new DshModeStore(ModesPath(data), null).Get(id), "nothing on disk says full access");
    }

    [TestMethod]
    public async Task AFullAccessProcessThatCannotStart_LeavesTheSessionWhereItWas()
    {
        var host = new FakeDshHost { FullAccessStarts = 0 };
        var (service, _, _) = await StartModesAsync(host: host);
        await using var _ = service;
        var id = await NewAsync(service, Work);

        var failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SetModeAsync(service, id, "full-access"));

        StringAssert.Contains(failed.Message, "did not start");
        Assert.IsTrue(host.ReadOnly.IsOpen(id));
        Assert.AreEqual(0, CallsTo(host.ReadOnly, AcpMethods.Agent.SessionClose, id), "never closed");
    }

    [TestMethod]
    public async Task AFullAccessProcessThatDiesMidSwitch_LeavesNoProcess_AndTheSessionWhereItWas()
    {
        // The full-access process starts, then dies while the session is being closed, and cannot start again.
        var host = new FakeDshHost { FullAccessStarts = 1 };
        var (service, _, data) = await StartModesAsync(host: host);
        await using var _ = service;
        var id = await NewAsync(service, Work);
        host.ReadOnly.OnClose = async _ =>
        {
            var full = host.FullAccess;
            await full.Rpc.DisposeAsync();
            await full.Rpc.Completion;
            await Task.Delay(200);
        };

        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SetModeAsync(service, id, "full-access"));

        Assert.IsTrue(host.ReadOnly.IsOpen(id), "resumed where it was");
        Assert.AreEqual(1, host.FullAccessLaunches);
        Assert.IsTrue(host.FullAccess.Rpc.Completion.IsCompleted);
        Assert.IsFalse(File.Exists(ModesPath(data)) && File.ReadAllText(ModesPath(data)).Contains(id, StringComparison.Ordinal));
        host.ReadOnly.OnClose = null;
        var ran = false;
        host.ReadOnly.OnPrompt = _ => { ran = true; return Task.CompletedTask; };
        await PromptAsync(service, id);
        Assert.IsTrue(ran);
    }

    [TestMethod]
    public async Task AFullAccessProcessThatNeverAnswers_IsStoppedAfterItsStartTime()
    {
        var host = new FakeDshHost { FullAccessHangs = true };
        var (service, _, _) = await StartModesAsync(host: host, startTimeout: TimeSpan.FromMilliseconds(300));
        await using var _ = service;
        var id = await NewAsync(service, Work);

        var failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SetModeAsync(service, id, "full-access"));

        StringAssert.Contains(failed.Message, "no answer to initialize");
        await host.FullAccess.Rpc.Completion.WaitAsync(Bound);
        Assert.IsTrue(host.ReadOnly.IsOpen(id));
        host.FullAccessHangs = false;
        Assert.AreEqual("full-access", ModeIn(await SetModeAsync(service, id, "full-access")), "the next switch starts it again");
        Assert.AreEqual(2, host.FullAccessLaunches);
    }

    [TestMethod]
    public async Task StoppingTheAdapter_WhileTheFullAccessProcessStarts_StopsItToo()
    {
        var host = new FakeDshHost { FullAccessHangs = true };
        var (service, _, _) = await StartModesAsync(host: host);
        await using var _ = service;
        var id = await NewAsync(service, Work);
        var switching = SetModeAsync(service, id, "full-access");
        await Until(() => host.FullAccessLaunches == 1, "the full-access process starting");

        await service.Instance.DisposeAsync();

        await host.FullAccess.Rpc.Completion.WaitAsync(Bound);
        await service.Instance.Exited.WaitAsync(Bound);
        await Assert.ThrowsAsync<Exception>(() => switching);
    }

    [TestMethod]
    public async Task AResumeOfASessionOpenInTheOtherProcess_GoesToThatProcess()
    {
        var (service, host, data) = await StartModesAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        Unwritable(data); // the kept mode stays read-only: only the open session says where it is
        await SetModeAsync(service, id, "full-access");

        var twice = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => ResumeAsync(service, id, Work));

        StringAssert.Contains(twice.Message, "already active");
        Assert.AreEqual(0, CallsTo(host.ReadOnly, AcpMethods.Agent.SessionResume, id), "not opened a second time");
        Assert.IsTrue(host.FullAccess.IsOpen(id));
    }

    [TestMethod]
    public async Task AConfigUpdate_FromAProcessThatNoLongerHasTheSession_IsDropped()
    {
        var (service, host, _) = await StartModesAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        await SetModeAsync(service, id, "full-access");
        var update = $$"""{"sessionUpdate":"config_option_update","configOptions":[{{FakeDsh.ModelOption}}]}""";

        await host.ReadOnly.Update(id, update);
        await host.FullAccess.Update(id, update);

        await Until(() => { lock (service.Updates) return service.Updates.Any(u => (string?)u["sessionUpdate"] == "config_option_update"); }, "the update");
        await Task.Delay(100);
        JsonObject[] updates;
        lock (service.Updates) updates = service.Updates.Where(u => (string?)u["sessionUpdate"] == "config_option_update").ToArray();
        Assert.AreEqual(1, updates.Length, "only the process that has the session speaks for it");
        Assert.AreEqual("full-access", ModeIn(updates[0]));
    }

    [TestMethod]
    public async Task AFork_RunsInItsParentsProcess_AndKeepsItsMode()
    {
        var (service, host, data) = await StartModesAsync();
        await using var _ = service;
        var parent = await NewAsync(service, Work);
        await SetModeAsync(service, parent, "full-access");

        var forked = await service.Rpc.SendRequestAsync("session/fork", new JsonObject
        {
            ["sessionId"] = parent, ["cwd"] = Work, ["mcpServers"] = new JsonArray(),
        }).WaitAsync(Bound);

        var fork = (string)forked!["sessionId"]!;
        Assert.AreEqual("full-access", ModeIn(forked));
        StringAssert.Contains(File.ReadAllText(ModesPath(data)), fork);
        var ran = new List<string>();
        host.FullAccess.OnPrompt = s => { lock (ran) ran.Add(s); return Task.CompletedTask; };
        await PromptAsync(service, fork);
        CollectionAssert.AreEqual(new[] { fork }, ran);
    }

    [TestMethod]
    public async Task AFork_WhoseModeCannotBeSaved_StillRunsInItsParentsProcess()
    {
        var (service, host, data) = await StartModesAsync();
        await using var _ = service;
        var parent = await NewAsync(service, Work);
        await SetModeAsync(service, parent, "full-access");
        Unwritable(data);

        var forked = await service.Rpc.SendRequestAsync("session/fork", new JsonObject
        {
            ["sessionId"] = parent, ["cwd"] = Work, ["mcpServers"] = new JsonArray(),
        }).WaitAsync(Bound);

        var fork = (string)forked!["sessionId"]!;
        Assert.AreEqual(DshModes.ReadOnly, new DshModeStore(ModesPath(data), null).Get(fork), "not saved: it opens read-only next time");
        var ran = new List<string>();
        host.FullAccess.OnPrompt = s => { lock (ran) ran.Add("full:" + s); return Task.CompletedTask; };
        host.ReadOnly.OnPrompt = s => { lock (ran) ran.Add("read-only:" + s); return Task.CompletedTask; };
        await PromptAsync(service, fork);
        CollectionAssert.AreEqual(new[] { "full:" + fork }, ran, "open where it was made");
    }
}
