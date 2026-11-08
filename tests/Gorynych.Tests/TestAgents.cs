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
        toAgent = new BreakableStream(co);
        return Task.FromResult(new AgentInstance(ci, toAgent, agent.Rpc.Completion, () => agent.Rpc.DisposeAsync()));
    }

    BreakableStream? toAgent;

    /// <summary>From now on every write of the service to the agent fails, while the agent's own messages still arrive.</summary>
    public void BreakWritesToAgent() => toAgent!.Broken = true;
}

/// <summary>A write-only stream whose writes start failing with <see cref="IOException"/> once it is broken.</summary>
sealed class BreakableStream(Stream inner) : Stream
{
    public volatile bool Broken;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Broken) throw new IOException("the link to the agent is broken");
        inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        Broken ? ValueTask.FromException(new IOException("the link to the agent is broken")) : inner.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken ct) =>
        Broken ? Task.FromException(new IOException("the link to the agent is broken")) : inner.FlushAsync(ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
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

    /// <summary>The <c>_gorynych/sessions_changed</c> notifications received, in order.</summary>
    public List<JsonObject> Changes { get; } = [];

    public InitializeResponse? Initialized { get; private set; }
    public List<ElicitationFormMode> Forms { get; } = [];

    /// <summary>Answers a form; by default every question with its first choice.</summary>
    public Func<ElicitationFormMode, CreateElicitationResponse> Fill { get; set; } = form => new ElicitationAcceptAction
    {
        Content = (form.RequestedSchema.Properties ?? []).ToDictionary(p => p.Key, p => p.Value switch
        {
            StringPropertySchema s => (JsonNode?)(s.OneOf?.FirstOrDefault()?.Const ?? ""),
            MultiSelectPropertySchema m => new JsonArray((JsonNode)((TitledMultiSelectItems)m.Items).AnyOf[0].Const),
            _ => null,
        }),
    };
    public Func<RequestPermissionRequest, Task<RequestPermissionOutcome>> Approve { get; set; } =
        r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = r.Options[0].OptionId });

    TestClient(JsonRpcConnection rpc, Task serving)
    {
        Rpc = rpc;
        this.serving = serving;
    }

    public JsonRpcConnection Rpc { get; }

    /// <param name="agent">Scopes the connection to one agent, as the stdio bridge does.</param>
    public static async Task<TestClient> ConnectAsync(Conductor conductor, string name = "test-client", bool forms = true, string? agent = null)
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
            else if (method == GorynychProtocol.MethodSessionsChanged && p is JsonObject change)
                lock (client.Changes) client.Changes.Add(change);
            return Task.CompletedTask;
        };
        client.Rpc.RequestHandler = async (req, ct) =>
        {
            if (req.Method == AcpMethods.Client.ElicitationCreate)
            {
                var form = (ElicitationFormMode)req.ParamsAs<CreateElicitationRequest>();
                lock (client.Forms) client.Forms.Add(form);
                return AcpJson.ToNode(client.Fill(form));
            }
            var r = req.ParamsAs<RequestPermissionRequest>();
            lock (client.Approvals) client.Approvals.Add(r);
            return AcpJson.ToNode(new RequestPermissionResponse { Outcome = await client.Approve(r) });
        };
        client.Rpc.Start();
        client.Initialized = await client.Rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
        {
            ProtocolVersion = 1,
            ClientCapabilities = forms ? new ClientCapabilities { Elicitation = new ElicitationCapabilities { Form = new ElicitationFormCapabilities() } } : null,
            Meta = agent is null ? null : new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        });
        return client;
    }

    /// <summary>The <c>sessions_changed</c> notifications so far, as "change sessionId".</summary>
    public string[] ChangeLog()
    {
        lock (Changes) return Changes.Select(c => $"{(string?)c["change"]} {(string?)c["sessionId"]}").ToArray();
    }

    public JsonObject[] Snapshot()
    {
        lock (Updates) return Updates.ToArray();
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

    /// <summary>The file name of a project's executable: <c>.exe</c> on Windows, none elsewhere.</summary>
    public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static Conductor Conductor(string dir, params IAgentAdapter[] adapters) =>
        new(new ConductorOptions { DataDirectory = dir, ServiceId = "test-service" }, adapters);

    /// <summary>A conductor that unloads a session as soon as it is idle with no client (<c>--unload-after 0</c>).</summary>
    public static Conductor UnloadingAtOnce(string dir, params IAgentAdapter[] adapters) =>
        new(new ConductorOptions { DataDirectory = dir, ServiceId = "test-service", UnloadAfter = TimeSpan.Zero }, adapters);
}
