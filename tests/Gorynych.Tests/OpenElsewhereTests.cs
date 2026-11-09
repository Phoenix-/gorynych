using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Sessions another app has open (docs/service-protocol.md, "Sessions open in another app"): agents
/// see them marked in <c>list_agents</c> and <c>list_sessions</c>, as their running adapter says now,
/// and a question to one fails at once with what to do.
/// </summary>
[TestClass]
public sealed class OpenElsewhereTests
{
    static readonly string Cwd = Path.GetTempPath();

    static Conductor Conductor(string data, params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = data,
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        UnloadAfter = TimeSpan.Zero, // a session its client left is unloaded at once
    }, adapters);

    static async Task<string> NewPromptedAsync(TestClient client, string agent, string text)
    {
        var id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        })).SessionId;
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = text }] });
        return id;
    }

    static JsonObject Entry(JsonNode? peers, string sessionId) =>
        peers!["sessions"]!.AsArray().Single(s => (string?)s!["session_id"] == sessionId)!.AsObject();

    [TestMethod]
    public async Task ASessionAnotherAppHolds_IsMarkedForAgents_AndAQuestionToItSaysWhatToDo()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { TellsOpenElsewhere = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(TestDirs.New(), codex.Adapter, kimi.Adapter);
        string held, free;
        await using (var owner = await TestClient.ConnectAsync(conductor, "owner"))
        {
            held = await NewPromptedAsync(owner, "codex", "Fix the flaky test");
            free = await NewPromptedAsync(owner, "codex", "Write the notes");
        }
        await conductor.CleanupsIdleAsync();
        lock (codex.OpenElsewhere) codex.OpenElsewhere[held] = "Codex Desktop"; // the person opened it in Codex Desktop
        await using var client = await TestClient.ConnectAsync(conductor);
        var asker = await NewPromptedAsync(client, "kimi", "Coordinate");
        var token = kimi.Tokens[asker];

        var peers = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject { ["token"] = token });

        Assert.AreEqual("Codex Desktop has it open; it cannot be asked until it is closed there", (string?)Entry(peers, held)["open_elsewhere"]);
        Assert.IsNull(Entry(peers, free)["open_elsewhere"], "free");
        Assert.AreEqual("Codex Desktop has it open; it cannot be asked until it is closed there",
            (string?)Entry(await conductor.PeerListAsync(null, includeArchived: false, CancellationToken.None), held)["open_elsewhere"], "list_sessions, outside Gorynych, too");

        var asked = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = held, ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
        }));
        Assert.AreEqual($"session_id {held} (codex, \"Fix the flaky test\") is open in Codex Desktop, which holds it, so it cannot be asked " +
            "until it is closed there. Ask another session from list_agents, or ask the user to close it in Codex Desktop.", asked.Message);
        Assert.AreEqual("Codex Desktop", (string?)asked.ErrorData?[GorynychProtocol.MetaOpenElsewhere]);
        lock (codex.Prompts) Assert.AreEqual(1, codex.Prompts.Count(p => p.SessionId == held), "asked nothing");
    }

    [TestMethod]
    public async Task AnAdapterThatFailsToTell_LeavesTheListAsItIs()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { TellsOpenElsewhere = true, OpenElsewhereFails = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(TestDirs.New(), codex.Adapter, kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var held = await NewPromptedAsync(client, "codex", "Fix the flaky test");
        lock (codex.OpenElsewhere) codex.OpenElsewhere[held] = "Codex Desktop";
        var asker = await NewPromptedAsync(client, "kimi", "Coordinate");

        var peers = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject { ["token"] = kimi.Tokens[asker] });

        Assert.IsNull(Entry(peers, held)["open_elsewhere"], "a hint: not known, and the list is whole");
        codex.Adapter.BreakWritesToAgent();
        var outside = await conductor.PeerListAsync(null, includeArchived: false, CancellationToken.None);
        Assert.IsNull(Entry(outside, held)["open_elsewhere"], "nor when the agent cannot be reached");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AnAdaptersListChange_IsPassedOn_OnlyFromOneThatSaysItSendsThem(bool advertised)
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { TellsListChanges = advertised };
        await using var conductor = Conductor(TestDirs.New(), codex.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewPromptedAsync(zed, "codex", "Fix the flaky test");
        var before = zed.ChangeLog().Count(c => c == "updated " + id);

        await codex.Adapter.Current!.Rpc.SendNotificationAsync(GorynychProtocol.MethodSessionsChanged, new JsonObject { ["sessionId"] = id, ["change"] = "updated" });
        await codex.Adapter.Current!.Rpc.SendNotificationAsync(GorynychProtocol.MethodSessionsChanged, new JsonObject { ["sessionId"] = id, ["change"] = "removed" });
        await Task.Delay(300);

        Assert.AreEqual(before + (advertised ? 1 : 0), zed.ChangeLog().Count(c => c == "updated " + id));
        Assert.IsFalse(zed.ChangeLog().Contains("removed " + id), "only updated is taken");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AChangeOfHolder_DoesNotHoldBackTheNextActivityNotice()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { TellsListChanges = true };
        await using var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(), ServiceId = "t", PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
            ActivityNoticeInterval = TimeSpan.FromSeconds(1),
        }, [codex.Adapter]);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewPromptedAsync(zed, "codex", "Fix the flaky test");
        await Task.Delay(2500); // past the first prompt's activity notices
        int Updates() => zed.ChangeLog().Count(c => c == "updated " + id);
        await codex.Adapter.Current!.Rpc.SendNotificationAsync(GorynychProtocol.MethodSessionsChanged, new JsonObject { ["sessionId"] = id, ["change"] = "updated" });
        for (var i = 0; i < 100 && Updates() == 0; i++) await Task.Delay(20);
        var told = Updates();

        await zed.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "And now?" }] });
        await Task.Delay(400);

        Assert.IsTrue(Updates() > told, "the new activity is told at once, not a second after the change of holder");
    }

    [TestMethod]
    public async Task AnAgentThatIsNotRunning_IsNotStartedForTheList()
    {
        var codex = new LifecycleAgent("codex", canDelete: true) { TellsOpenElsewhere = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var data = TestDirs.New();
        string held;
        await using (var before = Conductor(data, codex.Adapter, kimi.Adapter))
        {
            await using var owner = await TestClient.ConnectAsync(before, "owner");
            held = await NewPromptedAsync(owner, "codex", "Fix the flaky test");
        }
        lock (codex.OpenElsewhere) codex.OpenElsewhere[held] = "Codex Desktop";
        await using var conductor = Conductor(data, codex.Adapter, kimi.Adapter);

        var list = await conductor.PeerListAsync(null, includeArchived: false, CancellationToken.None);

        Assert.IsFalse(conductor.Agents["codex"].IsRunning, "listing started no agent");
        Assert.IsNull(Entry(list, held)["open_elsewhere"], "not known, so not marked");

        // Nor one that ran in this service and stopped.
        await conductor.Agents["codex"].GetAsync(CancellationToken.None);
        var starts = codex.Adapter.Starts;
        await codex.Adapter.Current!.Rpc.DisposeAsync();
        for (var i = 0; i < 250 && conductor.Agents["codex"].IsRunning; i++) await Task.Delay(20);
        Assert.IsFalse(conductor.Agents["codex"].IsRunning);

        list = await conductor.PeerListAsync(null, includeArchived: false, CancellationToken.None);

        Assert.AreEqual(starts, codex.Adapter.Starts, "not started again for the list");
        Assert.IsNull(Entry(list, held)["open_elsewhere"]);
    }
}
