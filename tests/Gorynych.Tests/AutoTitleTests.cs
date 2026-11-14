using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The service's half of generated titles (docs/adapter-contract.md, §9): it asks an adapter that
/// can generate one exactly once for a session whose title is still the first prompt, never for a
/// session named at spawn, and not again after a restart.
/// </summary>
[TestClass]
public sealed class AutoTitleTests
{
    static readonly string Cwd = Path.GetTempPath();
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 250; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    sealed class Fake
    {
        readonly string sessionId;
        public List<JsonObject> Titles { get; } = [];
        public ScriptedAdapter Adapter { get; }

        public Fake(string sessionId = "s1")
        {
            this.sessionId = sessionId;
            Adapter = new ScriptedAdapter("fake", HandleAsync);
        }

        async Task<JsonNode?> HandleAsync(ScriptedAgent agent, JsonRpcRequest request, CancellationToken ct)
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize:
                    return ScriptedAgent.Init(autoTitle: true);
                case AcpMethods.Agent.SessionNew:
                    return new JsonObject { ["sessionId"] = sessionId };
                case AcpMethods.Agent.SessionPrompt:
                    return new JsonObject { ["stopReason"] = "end_turn" };
                case GorynychProtocol.MethodGenerateTitle:
                    lock (Titles) Titles.Add(request.Params!.DeepClone().AsObject());
                    return new JsonObject();
                default:
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, request.Method);
            }
        }
    }

    static Task<TestClient> ConnectAsync(Conductor conductor) => TestClient.ConnectAsync(conductor, "zed");

    static async Task<string> NewAsync(TestClient client) =>
        (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Cwd, McpServers = [] })).SessionId;

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    [TestMethod]
    public async Task FirstPrompt_AsksOnce_WithTheFirstTaskOnly_AndNotAgainOnLaterTurns()
    {
        var fake = new Fake();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), fake.Adapter);
        await using var zed = await ConnectAsync(conductor);
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "Fix the flaky login test");
        await Until(() => { lock (fake.Titles) return fake.Titles.Count == 1; }, "the title request");

        JsonObject asked;
        lock (fake.Titles) asked = fake.Titles[0];
        Assert.AreEqual(id, (string?)asked["sessionId"]);
        Assert.AreEqual("Fix the flaky login test", (string?)asked["input"], "the first user task only");
        Assert.AreEqual("Fix the flaky login test", (string?)asked["title"], "the provisional title is the fallback");
        Assert.IsTrue(conductor.Store.Get(id)!.AutoTitleAttempted);

        await PromptAsync(zed, id, "Now the second turn");
        await Task.Delay(150);
        lock (fake.Titles) Assert.AreEqual(1, fake.Titles.Count, "no per-turn regeneration");
    }

    [TestMethod]
    public async Task ASpawnedName_IsNeverReplacedByAGeneratedOne()
    {
        var fake = new Fake();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), fake.Adapter);
        await using var zed = await ConnectAsync(conductor);
        var id = await NewAsync(zed);
        conductor.Store.Update(id, "fake", r => { r.Title = "Named child"; r.TitleSource = "spawn"; });

        await PromptAsync(zed, id, "Fix the flaky login test");
        await Task.Delay(150);
        lock (fake.Titles) Assert.AreEqual(0, fake.Titles.Count);
        Assert.AreEqual("spawn", conductor.Store.Get(id)!.TitleSource);
    }

    [TestMethod]
    public async Task TheAttempt_IsKept_SoARestartDoesNotAskAgain()
    {
        var dir = TestDirs.New();
        var fake = new Fake();
        string id;
        await using (var conductor = TestDirs.Conductor(dir, fake.Adapter))
        {
            await using var zed = await ConnectAsync(conductor);
            id = await NewAsync(zed);
            await PromptAsync(zed, id, "Fix the flaky login test");
            await Until(() => { lock (fake.Titles) return fake.Titles.Count == 1; }, "the title request");
        }

        await using var again = TestDirs.Conductor(dir, new Fake().Adapter);
        Assert.IsTrue(again.Store.Get(id)!.AutoTitleAttempted, "the overlay keeps the one attempt");
        Assert.AreEqual("prompt", again.Store.Get(id)!.TitleSource);
    }
}
