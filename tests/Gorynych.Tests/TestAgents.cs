using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>An in-process ACP agent whose behaviour each test scripts.</summary>
sealed class ScriptedAdapter(string name, Func<ScriptedAgent, JsonRpcRequest, CancellationToken, Task<JsonNode?>> handle) : IAgentAdapter
{
    public string Name => name;
    public string DisplayName => name;
    public int Starts { get; private set; }
    public ScriptedAgent? Current { get; private set; }
    public List<string> Methods { get; } = [];

    public Task<AgentInstance> StartAsync(CancellationToken ct)
    {
        Starts++;
        var link = new DuplexLink();
        var (ai, ao) = link.AgentEnd;
        var agent = new ScriptedAgent(new JsonRpcConnection(ai, ao, name + "-agent"));
        agent.Rpc.RequestHandler = (req, token) => { lock (Methods) Methods.Add(req.Method); return handle(agent, req, token); };
        agent.Rpc.NotificationHandler = (method, p) => { lock (Methods) Methods.Add(method); return Task.CompletedTask; };
        agent.Rpc.Start();
        Current = agent;
        var (ci, co) = link.ClientEnd;
        return Task.FromResult(new AgentInstance(ci, co, agent.Rpc.Completion, () => agent.Rpc.DisposeAsync()));
    }
}

sealed class ScriptedAgent(JsonRpcConnection rpc)
{
    public JsonRpcConnection Rpc { get; } = rpc;

    public Task Update(string sessionId, SessionUpdate update) => Rpc.SendUpdateAsync(sessionId, update);

    public static JsonNode? Init(bool list = true, bool resume = true, bool load = false) => AcpJson.ToNode(new InitializeResponse
    {
        ProtocolVersion = 1,
        AgentCapabilities = new AgentCapabilities
        {
            LoadSession = load,
            SessionCapabilities = new SessionCapabilities
            {
                List = list ? new SessionListCapabilities() : null,
                Resume = resume ? new SessionResumeCapabilities() : null,
            },
        },
        AgentInfo = new Implementation { Name = "scripted", Version = "1" },
    });
}

/// <summary>A test client connected to a conductor over an in-memory link.</summary>
sealed class TestClient : IAsyncDisposable
{
    readonly Task serving;
    public List<JsonObject> Updates { get; } = [];
    public List<RequestPermissionRequest> Approvals { get; } = [];
    public Func<RequestPermissionRequest, Task<RequestPermissionOutcome>> Approve { get; set; } =
        r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = r.Options[0].OptionId });

    TestClient(JsonRpcConnection rpc, Task serving)
    {
        Rpc = rpc;
        this.serving = serving;
    }

    public JsonRpcConnection Rpc { get; }

    public static async Task<TestClient> ConnectAsync(Conductor conductor, string name = "test-client")
    {
        var link = new DuplexLink();
        var (si, so) = link.AgentEnd;
        var serving = conductor.ServeClientAsync(si, so, name);
        var (ci, co) = link.ClientEnd;
        var client = new TestClient(new JsonRpcConnection(ci, co, name), serving);
        client.Rpc.NotificationHandler = (method, p) =>
        {
            if (method == AcpMethods.Client.SessionUpdate && p?["update"] is JsonObject u)
                lock (client.Updates) client.Updates.Add(u);
            return Task.CompletedTask;
        };
        client.Rpc.RequestHandler = async (req, ct) =>
        {
            var r = req.ParamsAs<RequestPermissionRequest>();
            lock (client.Approvals) client.Approvals.Add(r);
            return AcpJson.ToNode(new RequestPermissionResponse { Outcome = await client.Approve(r) });
        };
        client.Rpc.Start();
        await client.Rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
        return client;
    }

    public string[] Kinds()
    {
        lock (Updates) return Updates.Select(u => (string)u["sessionUpdate"]!).ToArray();
    }

    public string Text(string kind = "agent_message_chunk")
    {
        lock (Updates) return string.Concat(Updates.Where(u => (string?)u["sessionUpdate"] == kind).Select(u => (string?)u["content"]?["text"]));
    }

    public async ValueTask DisposeAsync()
    {
        await Rpc.DisposeAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

static class TestDirs
{
    public static string New()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gorynych-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static Conductor Conductor(string dir, params IAgentAdapter[] adapters) =>
        new(new ConductorOptions { DataDirectory = dir, ServiceId = "test-service" }, adapters);
}
