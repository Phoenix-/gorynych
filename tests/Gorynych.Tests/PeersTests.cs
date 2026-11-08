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
        var created = 0;
        var answerer = new ScriptedAdapter("answerer", (agent, req, ct) => Task.FromResult(req.Method switch
        {
            AcpMethods.Agent.Initialize => ScriptedAgent.Init(),
            // The first is the session asked for opinions; later ones are spawned.
            AcpMethods.Agent.SessionNew => AcpJson.ToNode(new NewSessionResponse
            {
                SessionId = Interlocked.Increment(ref created) == 1 ? "answering" : $"child-{created}",
            }),
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
        Assert.AreEqual("peer_asker", (string?)asked["sender"], "the label for GUIs stays");
        Assert.AreEqual("[Opinion request · from session_id asking (asker)]\nSQLite or PostgreSQL?", (string?)asked["text"],
            "the asked agent sees the asking session by its ID");
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
    public void ListAgents_KeepsTitlesAndTimesAsTheyAre_AndSaysWhenTheListIsEmpty()
    {
        var text = PeersServer.ListAgentsText(new JsonObject
        {
            ["caller"] = "session_id s1 (claude, \"Ревью\")",
            ["sessions"] = new JsonArray(new JsonObject
            {
                ["session_id"] = "k1", ["title"] = "Обзор + план <ok> & done", ["updated_at"] = "2026-11-08T10:00:00.0000000+00:00",
            }),
        });
        StringAssert.Contains(text, "\"title\": \"Обзор + план <ok> & done\"");
        StringAssert.Contains(text, "\"updated_at\": \"2026-11-08T10:00:00.0000000+00:00\"");
        Assert.IsFalse(text.Contains("\\u", StringComparison.Ordinal), "no \\u escapes for an agent to read: " + text);

        Assert.AreEqual("You are session_id s1 (claude).\nNo other sessions to ask. spawn_session starts one.",
            PeersServer.ListAgentsText(new JsonObject { ["caller"] = "session_id s1 (claude)", ["sessions"] = new JsonArray(), ["hiddenArchived"] = 0 }));
        Assert.AreEqual("You are session_id s1 (claude).\nNo other sessions to ask. spawn_session starts one.\n" +
            "2 archived sessions are not shown; pass include_archived: true to list them.",
            PeersServer.ListAgentsText(new JsonObject { ["caller"] = "session_id s1 (claude)", ["sessions"] = new JsonArray(), ["hiddenArchived"] = 2 }));
    }

    [TestMethod]
    public void ToolDescriptions_SayWhenToUseWhich()
    {
        var tools = PeersServer.Tools(peers: true).ToDictionary(t => (string)t["name"]!);
        string Description(string tool) => (string)tools[tool]["description"]!;

        StringAssert.Contains(Description("ask_agent"), "Start a new question");
        StringAssert.Contains(Description("ask_agent"), "To answer a request you received (its header names a message_id), use reply instead.");
        StringAssert.Contains((string?)tools["ask_agent"]["inputSchema"]!["properties"]!["session_id"]!["description"],
            "never an agent name, a title or a sender label");
        StringAssert.Contains(Description("reply"), "Use it only to answer; to start a question of your own, use ask_agent.");
        StringAssert.Contains(Description("spawn_session"), "sends this session a short report with the child's final message by itself");
        StringAssert.Contains(Description("list_agents"), "The result's first line names this session, which the list leaves out: \"You are session_id ...\"");
        StringAssert.Contains(Description("list_agents"),
            "Pass a session_id exactly as listed, never adding or stripping a prefix: Claude's are a bare UUID, Kimi's start with session_.");
        StringAssert.Contains(Description("list_agents"), "Each entry has session_id, agent, title, cwd, status, updated_at and parent_session_id");
        StringAssert.Contains(Description("list_agents"), "status \"open\": the session's agent has it loaded right now.");
        StringAssert.Contains(Description("list_agents"),
            "status \"saved\": it is saved but not loaded; asking it starts it, so it answers more slowly, and it is closed again after it answers.");
        StringAssert.Contains(Description("list_agents"), "status has nothing to do with archived.");
        Assert.IsFalse(Description("list_agents").Contains("Claude session can", StringComparison.Ordinal), "every agent is started the same way now");
        StringAssert.Contains(Description("list_agents"), "Archived sessions are left out unless include_archived is true; when some are left out, the result's last line says how many.");
        Assert.AreEqual("boolean", (string?)tools["list_agents"]["inputSchema"]!["properties"]!["include_archived"]!["type"]);
        StringAssert.Contains(Description("archive_session"), "this one included");
        StringAssert.Contains(Description("archive_session"), "Archiving is not deleting: the session keeps all its history, ask_agent still reaches it");
        StringAssert.Contains(Description("restore_session"), "To ask an archived session something, use ask_agent: it need not be restored first.");
        foreach (var tool in new[] { "archive_session", "restore_session" })
        {
            StringAssert.Contains(Description(tool), "The user approves each call.");
            CollectionAssert.AreEqual(new[] { "session_id" }, tools[tool]["inputSchema"]!["required"]!.AsArray().Select(r => (string?)r).ToArray());
        }
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
            CollectionAssert.AreEqual(new[] { "list_agents", "ask_agent", "spawn_session", "archive_session", "restore_session", "reply" },
                tools!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToArray());

            var listed = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "list_agents", ["arguments"] = new JsonObject() });
            var listing = (string)listed!["content"]![0]!["text"]!;
            StringAssert.StartsWith(listing, $"You are session_id asking (asker).\n{PeersServer.StatusLegend}\n[",
                "the caller first, what status means, then the list");
            var entries = JsonNode.Parse(listing[(listing.IndexOf("\n[", StringComparison.Ordinal) + 1)..])!.AsArray();
            CollectionAssert.AreEqual(new[] { "answering" }, entries.Select(s => (string?)s!["session_id"]).ToArray());
            Assert.AreEqual("open", (string?)entries[0]!["status"]);

            var answer = await mcp.SendRequestAsync("tools/call", new JsonObject
            {
                ["name"] = "ask_agent",
                ["arguments"] = new JsonObject { ["session_id"] = "answering", ["question"] = "SQLite or PostgreSQL?" },
            });
            Assert.AreEqual("SQLite, for one user.", (string?)answer!["content"]![0]!["text"]);
            Assert.IsFalse((bool)answer["isError"]!);
            Assert.AreEqual("peer_asker", (string?)opinions.Single()["sender"]);

            // What an agent sees when it passes a sender label, or answers nothing.
            var wrong = await mcp.SendRequestAsync("tools/call", new JsonObject
            {
                ["name"] = "ask_agent",
                ["arguments"] = new JsonObject { ["session_id"] = "peer_answerer", ["question"] = "SQLite or PostgreSQL?" },
            });
            Assert.IsTrue((bool)wrong!["isError"]!);
            Assert.AreEqual("Gorynych could not do that: No session \"peer_answerer\". Pass an exact session_id from list_agents. " +
                "Sessions you can ask:\n- session_id answering (answerer, \"Pick a database for the cockpit\")", (string?)wrong["content"]![0]!["text"]);
            var stray = await mcp.SendRequestAsync("tools/call", new JsonObject
            {
                ["name"] = "reply",
                ["arguments"] = new JsonObject { ["message_id"] = "m99", ["text"] = "SQLite." },
            });
            Assert.IsTrue((bool)stray!["isError"]!);
            Assert.AreEqual(PeersServer.NotDelivered("m99"), (string?)stray["content"]![0]!["text"]);

            var spawned = await mcp.SendRequestAsync("tools/call", new JsonObject
            {
                ["name"] = "spawn_session",
                ["arguments"] = new JsonObject { ["agent"] = "answerer", ["prompt"] = "Write the tests", ["title"] = "Tests" },
            });
            var text = (string)spawned!["content"]![0]!["text"]!;
            Assert.IsFalse((bool)spawned["isError"]!, text);
            Assert.AreEqual("child-2", (string?)JsonNode.Parse(text)!["session_id"]);
            var approval = client.Approvals.Single();
            Assert.AreEqual("asking", approval.SessionId, "asked on the calling session");
            StringAssert.Contains(approval.ToolCall.Title, "Tests");

            // Archive and restore, and list_agents with and without the archived ones.
            async Task<JsonNode> Call(string tool, JsonObject arguments)
            {
                var result = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });
                Assert.IsFalse((bool)result!["isError"]!, (string?)result["content"]![0]!["text"]);
                return result["content"]![0]!["text"]!;
            }
            // The caller's line, the list, then (when it leaves archived sessions out) how many.
            async Task<(string[] Sessions, string Last)> Listed(bool includeArchived)
            {
                var text = (string)(await Call("list_agents", new JsonObject { ["include_archived"] = includeArchived }))!;
                StringAssert.StartsWith(text, "You are session_id asking (asker).\n");
                var end = text.LastIndexOf(']') + 1;
                var sessions = JsonNode.Parse(text[(text.IndexOf("\n[", StringComparison.Ordinal) + 1)..end])!.AsArray()
                    .Select(s => $"{(string?)s!["session_id"]}{((bool?)s["archived"] == true ? " archived" : "")}").ToArray();
                return (sessions, text[end..]);
            }
            var who = "session_id answering (answerer, \"Pick a database for the cockpit\")";
            Assert.AreEqual($"Archived {who}. It is now hidden from list_agents and from the user's session list; nothing in it is lost. " +
                "ask_agent still reaches it, and restore_session brings it back.", (string?)await Call("archive_session", new JsonObject { ["session_id"] = "answering" }));
            var hidden = await Listed(includeArchived: false);
            CollectionAssert.AreEquivalent(new[] { "child-2" }, hidden.Sessions);
            Assert.AreEqual("\n1 archived session is not shown; pass include_archived: true to list it.", hidden.Last);
            var all = await Listed(includeArchived: true);
            CollectionAssert.AreEquivalent(new[] { "child-2", "answering archived" }, all.Sessions);
            Assert.AreEqual("", all.Last);
            Assert.AreEqual("SQLite, for one user.", (string?)await Call("ask_agent", new JsonObject { ["session_id"] = "answering", ["question"] = "Still SQLite?" }));
            Assert.AreEqual($"Restored {who}. It is back in list_agents and in the user's session list.",
                (string?)await Call("restore_session", new JsonObject { ["session_id"] = "answering" }));
            var unknown = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = "restore_session", ["arguments"] = new JsonObject { ["session_id"] = "answerer" } });
            Assert.IsTrue((bool)unknown!["isError"]!);
            Assert.AreEqual("Gorynych could not do that: No session \"answerer\". Pass an exact session_id from list_agents with include_archived: true. " +
                "No session is archived now.", (string?)unknown["content"]![0]!["text"]);
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
