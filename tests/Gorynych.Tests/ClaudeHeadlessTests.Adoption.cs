using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.AdoptClaude;

namespace Gorynych.Tests;

public sealed partial class ClaudeHeadlessTests
{
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AnExternalRegistration_IsListedAndResumed_WithoutRestartingTheService()
    {
        var dir = TestDirs.New();
        var options = Options(dir, fetchCatalog: false);
        var id = Guid.NewGuid().ToString();
        ClaudeAdoptionTests.Transcript(options.ProjectsDirectory!, dir, id);
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(options));
        await using var client = await TestClient.ConnectAsync(conductor);
        var before = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new());
        Assert.IsFalse(before.Sessions.Any(s => s.SessionId == id));

        Assert.IsTrue(ClaudeAdoption.Register(dir, id, dir, options.ProjectsDirectory));
        var after = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new());
        Assert.AreEqual("Chosen title", after.Sessions.Single(s => s.SessionId == id).Title);
        await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = dir, ["mcpServers"] = new JsonArray() });
        CollectionAssert.AreEqual(new[] { "user: From the terminal", "agent: Done there" }, Messages(client.Snapshot()));
        Assert.AreEqual(StopReason.EndTurn, (await PromptAsync(client, id, "continue here")).StopReason);
        var args = LaunchArgs(id);
        Assert.AreEqual(id, args[args.IndexOf("--resume") + 1]);
        CollectionAssert.DoesNotContain(args, "--session-id");
    }
}
