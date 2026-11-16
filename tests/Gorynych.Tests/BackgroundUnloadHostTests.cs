using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Service;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

[TestClass]
public sealed class BackgroundUnloadHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnOrphanedHostCountsRetainedBackgroundAsBusy(bool partial)
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Closes = true };
        var hosts = new InProcessHosts(_ => agent)
        {
            OrphanIdle = TimeSpan.FromMilliseconds(200), OrphanBusy = TimeSpan.FromSeconds(20),
        };
        var current = hosts.Conductor(data, agent);
        hosts.Service = current;
        try
        {
            string id;
            await using (var client = await TestClient.ConnectAsync(current))
            {
                id = await HostTests.NewSession(client);
                await HostTests.Prompt(client, id, "start background work");
            }
            var field = partial ? GorynychProtocol.MetaBackgroundKnown : GorynychProtocol.MetaBackgroundWork;
            async Task Report(JsonNode? tasks) => await agent.AgentOf(id).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
                new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject
                {
                    ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject { [field] = tasks },
                } });
            await Report(new JsonArray(new JsonObject { ["type"] = "terminal", ["terminalId"] = "owned-build" }));
            await LinkHarness.Until(() => current.Feed(id).Users == 1, "background hold");
            await Report(null);
            await LinkHarness.Until(() => current.Feed(id).Activity.Snapshot()[partial ? "backgroundKnown" : "background"] is null,
                "unknown snapshot retains the hold");
            var host = hosts["turns"];
            await HostTests.RestartAsync(current);
            await HostTests.GoneAsync(current, null, hosts);
            await Task.Delay(750);
            Assert.IsFalse(host.Run.IsCompleted, "background work must get the busy orphan grace, not idle shutdown");
            current = hosts.Conductor(data, agent);
            hosts.Service = current;
            await LinkHarness.Until(() => current.Feed(id).Users == 1, "host reconnects with retained background");
            await Report(new JsonArray());
            await LinkHarness.Until(() => current.Feed(id).Users == 0, "confirmed completion releases the hold");
        }
        finally
        {
            await current.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AStaleHostKeepsBackgroundWorkAcrossConductorRestart(bool partial, bool canClose)
    {
        var data = TestDirs.New();
        var agent = new TurnAgent("turns") { Closes = canClose };
        var hosts = new InProcessHosts(_ => agent);
        Conductor Start()
        {
            var build = hosts.Build;
            var service = new Conductor(new ConductorOptions
            {
                DataDirectory = data, ServiceId = "background-generation-test",
                Hosts = hosts, HostToken = InProcessHosts.Token,
                HostGeneration = _ => new HostGeneration(build, []),
                SessionHosts = new HashSet<string>(canClose ? ["turns"] : [], StringComparer.Ordinal),
            }, [agent]);
            hosts.Service = service;
            return service;
        }
        var current = Start();
        try
        {
            string id;
            await using (var client = await TestClient.ConnectAsync(current))
            {
                id = await HostTests.NewSession(client);
                await HostTests.Prompt(client, id, "start background work");
            }
            var first = hosts["turns"];
            var field = partial ? GorynychProtocol.MetaBackgroundKnown : GorynychProtocol.MetaBackgroundWork;
            async Task Report(JsonNode? tasks) => await agent.AgentOf(id).Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate,
                new JsonObject { ["sessionId"] = id, ["update"] = new JsonObject
                {
                    ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject { [field] = tasks },
                } });
            await Report(new JsonArray(new JsonObject { ["type"] = "terminal", ["terminalId"] = "owned-build" }));
            await LinkHarness.Until(() => current.Feed(id).Users == 1, "native background holds the session");
            await Report(null);
            await LinkHarness.Until(() => current.Feed(id).Activity.Snapshot()[partial ? "backgroundKnown" : "background"] is null,
                "background became unknown but remains held");

            await HostTests.RestartAsync(current);
            await HostTests.GoneAsync(current, null, hosts);
            hosts.Build = "build-2";
            current = Start();
            await LinkHarness.Until(() => current.Feed(id).Users > 0 || first.Run.IsCompleted,
                "old host reattaches with its background hold");
            await current.CleanupsIdleAsync();
            Assert.IsFalse(first.Run.IsCompleted, "host rotation must not stop an uncertain background build");
            Assert.IsFalse(agent.Events.Any(e => e.StartsWith(AcpMethods.Agent.SessionClose + ":", StringComparison.Ordinal)),
                "no native session close is allowed while background work is held");
            Assert.AreEqual(1, current.Feed(id).Users);
            Assert.IsNull(current.Feed(id).Activity.Snapshot()[partial ? "backgroundKnown" : "background"],
                "a restored hold must not turn old tasks into a confirmed live snapshot");

            await using (var whileBusy = await TestClient.ConnectAsync(current))
                await HostTests.Prompt(whileBusy, id, "inspect while background is still running");
            Assert.AreEqual(1, hosts.Launches, "foreground work can continue in the held old host");
            await Report(new JsonArray());
            await LinkHarness.Until(() => first.Run.IsCompleted, "empty background lets the old generation retire");
            await using var nextClient = await TestClient.ConnectAsync(current);
            await HostTests.Prompt(nextClient, id, "continue after background finished");
            Assert.AreEqual(2, hosts.Launches, "the next prompt uses a current-generation host");
            Assert.AreEqual("build-2", hosts["turns"].Build);
        }
        finally
        {
            await current.DisposeAsync();
        }
    }
}
