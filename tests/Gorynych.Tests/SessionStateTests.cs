using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Tests;

/// <summary>
/// The agent's session state (config options such as Kimi's model and thinking, and modes) that
/// clients draw as selectors. A client that loads or resumes a session the service already has
/// open must still get it, with the current values: Zed restarts and loads its threads again.
/// </summary>
[TestClass]
public sealed class SessionStateTests
{
    static readonly string Cwd = Path.GetTempPath();

    static string Model(JsonNode? response) => (string)response!["configOptions"]![0]!["currentValue"]!;
    static string Mode(JsonNode? response) => (string)response!["modes"]!["currentModeId"]!;

    static Task<JsonNode?> NewAsync(TestClient client) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });

    static Task<JsonNode?> LoadAsync(TestClient client) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = "s1", ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });

    static Task<JsonNode?> ResumeAsync(TestClient client) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = "s1", ["cwd"] = Cwd });

    [TestMethod]
    public async Task LoadAndResume_OfAnOpenSession_ReturnTheAgentsSessionState()
    {
        var kimi = new StatefulAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var first = await TestClient.ConnectAsync(conductor, "zed");
        var created = await NewAsync(first);
        Assert.AreEqual("k2", Model(created));

        await using var again = await TestClient.ConnectAsync(conductor, "zed-restarted");
        var responses = new[] { await LoadAsync(again), await LoadAsync(again), await ResumeAsync(again) };

        foreach (var response in responses)
        {
            Assert.AreEqual("k2", Model(response));
            Assert.AreEqual("default", Mode(response));
            Assert.AreEqual(2, response!["modes"]!["availableModes"]!.AsArray().Count);
        }
        CollectionAssert.DoesNotContain(kimi.Methods, AcpMethods.Agent.SessionLoad, "the agent was not asked to open the session again");
        CollectionAssert.DoesNotContain(kimi.Methods, AcpMethods.Agent.SessionResume);
    }

    [TestMethod]
    public async Task TheReturnedState_FollowsSetConfigOption_SetMode_AndTheAgentsOwnUpdates()
    {
        var kimi = new StatefulAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var first = await TestClient.ConnectAsync(conductor, "zed");
        await NewAsync(first);
        await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = "s1", ["configId"] = "model", ["value"] = "k3" });
        await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetMode, new JsonObject { ["sessionId"] = "s1", ["modeId"] = "plan" });

        await using var again = await TestClient.ConnectAsync(conductor, "zed-restarted");
        var loaded = await LoadAsync(again);
        Assert.AreEqual("k3", Model(loaded));
        Assert.AreEqual("plan", Mode(loaded));

        // During a turn the agent switches by itself (config_option_update, current_mode_update).
        await first.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = "s1", Prompt = [new TextContent { Text = "go" }] });
        var resumed = await ResumeAsync(again);
        Assert.AreEqual("k2", Model(resumed));
        Assert.AreEqual("default", Mode(resumed));
    }

    [TestMethod]
    public async Task ARestartedAgent_IsAskedAgain_AndItsAnswerReturned()
    {
        var kimi = new StatefulAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var first = await TestClient.ConnectAsync(conductor, "zed");
        await NewAsync(first);
        await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = "s1", ["configId"] = "model", ["value"] = "k3" });

        await kimi.Adapter.Current!.Rpc.DisposeAsync(); // the agent process dies
        await Task.Delay(100);
        var loaded = await LoadAsync(first);

        // The service has no journal of this session yet, so the agent replays it with its own load.
        CollectionAssert.Contains(kimi.Methods, AcpMethods.Agent.SessionLoad);
        Assert.AreEqual("k3", Model(loaded), "what the restarted agent reports");

        await kimi.Adapter.Current!.Rpc.DisposeAsync(); // dies again; session/resume reopens with the agent's resume
        await Task.Delay(100);
        var resumed = await ResumeAsync(first);
        CollectionAssert.Contains(kimi.Methods, AcpMethods.Agent.SessionResume);
        Assert.AreEqual("k3", Model(resumed));
    }

    /// <summary>
    /// A Kimi-like agent with one session: session state on new, load and resume in Kimi
    /// 2.4.1's shape; set_config_option and set_mode; and a turn in which it switches back to
    /// its defaults by itself. Its state outlives a restart, like Kimi's session files.
    /// </summary>
    sealed class StatefulAgent
    {
        string model = "k2";
        string mode = "default";

        public StatefulAgent()
        {
            Adapter = new ScriptedAdapter("kimi", async (agent, req, ct) =>
            {
                switch (req.Method)
                {
                    case AcpMethods.Agent.Initialize:
                        return ScriptedAgent.Init(list: true, resume: true, load: true);
                    case AcpMethods.Agent.SessionNew:
                        var created = State();
                        created["sessionId"] = "s1";
                        return created;
                    case AcpMethods.Agent.SessionList:
                        return new JsonObject { ["sessions"] = new JsonArray(new JsonObject { ["sessionId"] = "s1", ["cwd"] = Cwd }) };
                    case AcpMethods.Agent.SessionResume:
                    case AcpMethods.Agent.SessionLoad:
                        return State();
                    case AcpMethods.Agent.SessionSetConfigOption:
                        model = (string)req.Params!["value"]!;
                        return new JsonObject { ["configOptions"] = Options() };
                    case AcpMethods.Agent.SessionSetMode:
                        mode = (string)req.Params!["modeId"]!;
                        return new JsonObject();
                    case AcpMethods.Agent.SessionPrompt:
                        model = "k2";
                        mode = "default";
                        await Notify(agent, new JsonObject { ["sessionUpdate"] = "config_option_update", ["configOptions"] = Options() });
                        await Notify(agent, new JsonObject { ["sessionUpdate"] = "current_mode_update", ["currentModeId"] = mode });
                        return new JsonObject { ["stopReason"] = "end_turn" };
                    default:
                        throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
                }
            });
        }

        public ScriptedAdapter Adapter { get; }

        public string[] Methods
        {
            get { lock (Adapter.Methods) return Adapter.Methods.ToArray(); }
        }

        static Task Notify(ScriptedAgent agent, JsonObject update) =>
            agent.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject { ["sessionId"] = "s1", ["update"] = update });

        JsonArray Options() => new(new JsonObject
        {
            ["type"] = "select",
            ["id"] = "model",
            ["name"] = "Model",
            ["category"] = "model",
            ["currentValue"] = model,
            ["options"] = new JsonArray(
                new JsonObject { ["value"] = "k2", ["name"] = "K2" },
                new JsonObject { ["value"] = "k3", ["name"] = "K3" }),
        });

        JsonObject State() => new()
        {
            ["configOptions"] = Options(),
            ["modes"] = new JsonObject
            {
                ["currentModeId"] = mode,
                ["availableModes"] = new JsonArray(
                    new JsonObject { ["id"] = "default", ["name"] = "Default" },
                    new JsonObject { ["id"] = "plan", ["name"] = "Plan" }),
            },
        };
    }
}
