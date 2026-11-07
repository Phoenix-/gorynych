using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>Agents asking each other for opinions through the peers MCP server the service hands out.</summary>
[TestClass]
public sealed class PeersTests
{
    static string ServiceExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx")))
                return Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "Gorynych.Service", "bin"), "Gorynych.Service.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).First();
        throw new InvalidOperationException("repository root not found");
    }

    /// <summary>"asker" records the MCP servers it is given; "answerer" answers opinions.</summary>
    static (ScriptedAdapter Asker, ScriptedAdapter Answerer, List<JsonObject> Servers, List<JsonObject> Opinions) Agents()
    {
        var servers = new List<JsonObject>();
        var opinions = new List<JsonObject>();
        var asker = new ScriptedAdapter("asker", (agent, req, ct) => Task.FromResult(req.Method switch
        {
            AcpMethods.Agent.Initialize => ScriptedAgent.Init(),
            AcpMethods.Agent.SessionNew => Record(servers, req, AcpJson.ToNode(new NewSessionResponse { SessionId = "asking" })),
            AcpMethods.Agent.SessionList => AcpJson.ToNode(new ListSessionsResponse { Sessions = [] }),
            _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method),
        }));
        var answerer = new ScriptedAdapter("answerer", (agent, req, ct) => Task.FromResult(req.Method switch
        {
            AcpMethods.Agent.Initialize => ScriptedAgent.Init(),
            AcpMethods.Agent.SessionNew => AcpJson.ToNode(new NewSessionResponse { SessionId = "answering" }),
            AcpMethods.Agent.SessionList => AcpJson.ToNode(new ListSessionsResponse { Sessions = [] }),
            AcpMethods.Agent.SessionPrompt => AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn }),
            GorynychProtocol.MethodOpinion => RecordOpinion(opinions, req),
            _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method),
        }));
        return (asker, answerer, servers, opinions);

        static JsonNode? Record(List<JsonObject> into, JsonRpcRequest req, JsonNode? result)
        {
            foreach (var s in req.Params?["mcpServers"]?.AsArray() ?? []) lock (into) into.Add(s!.DeepClone().AsObject());
            return result;
        }

        static JsonNode? RecordOpinion(List<JsonObject> into, JsonRpcRequest req)
        {
            lock (into) into.Add(req.Params!.DeepClone().AsObject());
            return new JsonObject { ["text"] = "SQLite, for one user." };
        }
    }

    static NewSessionRequest New(string agent) => new()
    {
        Cwd = Path.GetTempPath(),
        McpServers = [],
        Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
    };

    /// <summary>Creates the answering session and gives it history, so <c>list_agents</c> shows it.</summary>
    static async Task NewAnsweringSessionAsync(TestClient client)
    {
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("answerer"));
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = "answering", Prompt = [new TextContent { Text = "Pick a database for the cockpit" }] });
    }

    [TestMethod]
    public async Task Service_GivesEachSessionThePeerServer_AndKnowsWhoAsks()
    {
        var (asker, answerer, servers, opinions) = Agents();
        await using var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(),
            ServiceId = "t",
            PeerServerCommand = [@"C:\svc\Gorynych.Service.exe", "peers", "--pipe", "p"],
        }, [asker, answerer]);
        await using var client = await TestClient.ConnectAsync(conductor);
        await NewAnsweringSessionAsync(client);
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("asker"));

        var server = servers.Single();
        Assert.AreEqual(PeersServer.ServerName, (string?)server["name"]);
        Assert.AreEqual(@"C:\svc\Gorynych.Service.exe", (string?)server["command"]);
        var args = server["args"]!.AsArray().Select(a => (string)a!).ToArray();
        CollectionAssert.AreEqual(new[] { "peers", "--pipe", "p", "--token" }, args[..4]);
        var token = args[4];

        var peers = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject { ["token"] = token });
        CollectionAssert.AreEqual(new[] { "answering" }, peers!["sessions"]!.AsArray().Select(s => (string)s!["session_id"]!).ToArray(), "the asker does not see itself");

        var reply = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = "answering",
            ["text"] = "SQLite or PostgreSQL?",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
        });
        Assert.AreEqual("SQLite, for one user.", (string?)reply!["text"]);
        var asked = opinions.Single();
        Assert.AreEqual("peer_asker", (string?)asked["sender"]);
        Assert.AreEqual("asking", (string?)asked["fromSessionId"]);

        var bad = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = "answering",
            ["text"] = "x",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = "forged" },
        }));
        Assert.AreEqual(JsonRpcException.InvalidRequest, bad.Code);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task PeersMcpServer_AsksThroughTheServicePipe()
    {
        var (asker, answerer, servers, opinions) = Agents();
        var pipe = "gorynych-test-" + Guid.NewGuid().ToString("N")[..8];
        var exe = ServiceExe();
        await using var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(),
            ServiceId = "t",
            PeerServerCommand = [exe, "peers", "--pipe", pipe],
        }, [asker, answerer]);
        using var stop = new CancellationTokenSource();
        var listening = PipeListener.RunAsync(conductor, pipe, stop.Token);
        await using var client = await TestClient.ConnectAsync(conductor);
        await NewAnsweringSessionAsync(client);
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("asker"));
        var server = servers.Single();

        // Start the MCP server the way the agent would, from the command it was given.
        var psi = new ProcessStartInfo((string)server["command"]!)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in server["args"]!.AsArray()) psi.ArgumentList.Add((string)a!);
        using var process = Process.Start(psi)!;
        try
        {
            var mcp = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, "mcp-client");
            mcp.Start();
            var init = await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() });
            Assert.IsNotNull(init!["capabilities"]!["tools"]);
            var tools = await mcp.SendRequestAsync("tools/list", new JsonObject());
            CollectionAssert.AreEqual(new[] { "list_agents", "ask_agent", "reply" }, tools!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToArray());

            var listed = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "list_agents", ["arguments"] = new JsonObject() });
            StringAssert.Contains((string?)listed!["content"]![0]!["text"], "answering");

            var answer = await mcp.SendRequestAsync("tools/call", new JsonObject
            {
                ["name"] = "ask_agent",
                ["arguments"] = new JsonObject { ["session_id"] = "answering", ["question"] = "SQLite or PostgreSQL?" },
            });
            Assert.AreEqual("SQLite, for one user.", (string?)answer!["content"]![0]!["text"]);
            Assert.IsFalse((bool)answer["isError"]!);
            Assert.AreEqual("peer_asker", (string?)opinions.Single()["sender"]);
            await mcp.DisposeAsync();
        }
        finally
        {
            if (!process.WaitForExit(5000)) process.Kill();
            stop.Cancel();
            await listening;
        }
    }
}
