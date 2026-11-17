namespace Gorynych.Tests;

[TestClass]
public sealed class ClosedHostRouteTests
{
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AClosedConnectionIsNeverReturnedAsASessionsLiveRoute()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var agent = new TurnAgent("turns");
            var hosts = new InProcessHosts(_ => agent);
            await using var service = hosts.Conductor(TestDirs.New(), agent);
            hosts.Service = service;
            await using var client = await TestClient.ConnectAsync(service, "gui");
            var sessionId = await HostTests.NewSession(client);
            Assert.AreEqual("end_turn", (string?)(await HostTests.Prompt(client, sessionId, "one"))!["stopReason"]);
            var host = service.Agent("turns");
            var route = host.RouteOf(sessionId)!;
            var closing = route.Rpc.DisposeAsync();
            try
            {
                Assert.IsTrue(route.Rpc.IsClosed);
                Assert.IsNull(host.RouteOf(sessionId), "a queued close callback must not keep a dead transport routable");
                Assert.IsFalse(host.IsLive(sessionId), "a session on a closed transport must reopen before its next request");
            }
            finally
            {
                await closing;
            }
            Assert.AreEqual("end_turn", (string?)(await HostTests.Prompt(client, sessionId, "two"))!["stopReason"]);
            Assert.AreEqual(2, hosts.Launches);
        }
    }
}
