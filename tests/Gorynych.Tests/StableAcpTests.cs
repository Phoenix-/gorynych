using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Where Gorynych uses ACP's stable parts instead of its own: the client system prompt, message
/// IDs on chunks, and extension flags in the capability object's <c>_meta</c>.
/// </summary>
[TestClass]
public sealed class StableAcpTests
{
    static readonly string Cwd = Path.GetTempPath();

    static Conductor Conductor(bool peers, params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = peers ? ["svc.exe", "peers", "--pipe", "p"] : null,
    }, adapters);

    static async Task<string> NewAsync(TestClient client, string agent) =>
        (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd,
            McpServers = [],
            Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        })).SessionId;

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static JsonObject TextPrompt(string mode = "append") => new()
    {
        ["mode"] = mode,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Be brief." }),
    };

    static string Describe(JsonNode? systemPrompt) => systemPrompt is null
        ? "none"
        : $"{(string?)systemPrompt["mode"]}: {string.Join(" | ", systemPrompt["content"]!.AsArray().Select(b => $"{(string?)b!["type"]} {(string?)b["text"]}"))}";

    // ---------------------------------------------------------------- systemPrompt

    [TestMethod]
    public async Task ThePeerInstructions_AreTheSystemPrompt_OfAnAgentThatTakesOne_OnEveryNewAndResume()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { TakesSystemPrompt = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(peers: true, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        Assert.IsNull(zed.Initialized!.AgentCapabilities!.SessionCapabilities!.SystemPrompt, "clients get no systemPrompt capability yet");

        var asked = await NewAsync(zed, "claude");
        await PromptAsync(zed, asked, "Coordinate the release");
        var other = await NewAsync(zed, "kimi");
        await PromptAsync(zed, other, "Fix the flaky test");
        // Closed, then opened again: by a client, and by the service for a question.
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = asked });
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = asked, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = asked });
        await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = asked,
            ["text"] = "Done?",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Tokens[other] },
        });

        var peers = $"append: text {PeersServer.SystemPrompt}";
        CollectionAssert.AreEqual(
            new[] { $"session/new {peers}", $"session/resume {peers}", $"session/resume {peers}" },
            claude.SystemPrompts.Where(c => c.SessionId == asked).Select(c => $"{c.Method} {Describe(c.SystemPrompt)}").ToArray());
        Assert.AreEqual("none", Describe(kimi.SystemPrompts.Single(c => c.SessionId == other).SystemPrompt),
            "Kimi does not advertise sessionCapabilities.systemPrompt");
    }

    [TestMethod]
    public async Task WithoutThePeersServer_ThereAreNoPeerInstructions()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { TakesSystemPrompt = true };
        await using var conductor = Conductor(peers: false, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewAsync(zed, "claude");
        Assert.AreEqual("none", Describe(claude.SystemPrompts.Single(c => c.SessionId == id).SystemPrompt));
    }

    [TestMethod]
    public async Task AClientsSystemPrompt_IsRefused_SinceTheServiceDoesNotAdvertiseIt()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { TakesSystemPrompt = true };
        await using var conductor = Conductor(peers: true, claude.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewAsync(zed, "claude");
        await PromptAsync(zed, id, "hello");

        foreach (var (method, request) in new[]
        {
            (AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Cwd, ["mcpServers"] = new JsonArray(), ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" } }),
            (AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() }),
            (AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() }),
        })
        {
            request["systemPrompt"] = TextPrompt();
            var e = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(method, request), method);
            Assert.AreEqual(JsonRpcException.InvalidParams, e.Code, method);
            StringAssert.Contains(e.Message, "does not advertise sessionCapabilities.systemPrompt", method);
        }
        Assert.AreEqual(1, claude.SystemPrompts.Count, "nothing reached the agent");
    }

    [TestMethod]
    public void Claude_TakesASystemPromptOnlyToAppend_AndRefusesWhatItCannotPassOn()
    {
        Assert.IsNull(ClaudeHooks.SystemPromptText(null));
        Assert.AreEqual("One.\n\nTwo.", ClaudeHooks.SystemPromptText(new SystemPrompt
        {
            Mode = SystemPromptMode.Append,
            Content = [new TextContent { Text = "One." }, new TextContent { Text = "Two." }],
        }));
        foreach (var (prompt, why) in new (SystemPrompt, string)[]
        {
            (new SystemPrompt { Mode = SystemPromptMode.Override, Content = [new TextContent { Text = "x" }] }, "only in append mode, not \"override\""),
            (new SystemPrompt { Mode = SystemPromptMode.Create("_custom"), Content = [new TextContent { Text = "x" }] }, "only in append mode, not \"_custom\""),
            (new SystemPrompt { Mode = SystemPromptMode.Append, Content = [] }, "systemPrompt.content is empty"),
            (new SystemPrompt { Mode = SystemPromptMode.Append, Content = [new ImageContent { Data = "", MimeType = "image/png" }] }, "only text blocks in systemPrompt.content, not \"image\""),
        })
        {
            var e = Assert.ThrowsExactly<JsonRpcException>(() => ClaudeHooks.SystemPromptText(prompt));
            Assert.AreEqual(JsonRpcException.InvalidParams, e.Code);
            StringAssert.Contains(e.Message, why);
        }
    }

    // ---------------------------------------------------------------- messageId

    [TestMethod]
    public async Task ChunksWithoutAMessageId_GetOnePerMessage_AsAClientWouldGroupThem_AndReplayHasTheSameIds()
    {
        static TextContent T(string text) => new() { Text = text };
        var kimi = new LifecycleAgent("kimi", canDelete: true)
        {
            AnswerUpdates =
            [
                new SessionUpdateAgentThoughtChunk { Content = T("Thinking.") },
                new SessionUpdateAgentMessageChunk { Content = T("Hel") },
                new SessionUpdateAgentMessageChunk { Content = T("lo") },
                new SessionInfoUpdate { Title = "Greeting" }, // not part of a message: it does not end one
                new SessionUpdateAgentMessageChunk { Content = T("!") },
                new ToolCall { ToolCallId = "t1", Title = "Read" },
                new SessionUpdateAgentMessageChunk { Content = T("Done.") },
                new SessionUpdateAgentMessageChunk { Content = T("Own."), MessageId = "native-1" },
                new SessionUpdateAgentMessageChunk { Content = T("After.") },
            ],
        };
        await using var conductor = Conductor(peers: false, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await using var watching = await TestClient.ConnectAsync(conductor, "watching");
        var id = await NewAsync(zed, "kimi");
        await watching.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        await PromptAsync(zed, id, "Say hello");

        static (string Kind, string Text, string? Id)[] Chunks(TestClient client) => client.Snapshot()
            .Where(u => SessionJournal.IsChunkKind((string?)u["sessionUpdate"]))
            .Select(u => ((string)u["sessionUpdate"]!, (string?)u["content"]!["text"] ?? "", (string?)u["messageId"]))
            .ToArray();
        for (var i = 0; i < 250 && Chunks(watching).Length < 8; i++) await Task.Delay(20); // another connection
        var live = Chunks(watching);
        CollectionAssert.AreEqual(new[] { "Say hello", "Thinking.", "Hel", "lo", "!", "Done.", "Own.", "After." }, live.Select(c => c.Text).ToArray());
        Assert.IsTrue(live.All(c => !string.IsNullOrEmpty(c.Id)), "every chunk names its message");
        var (user, thought, hel, lo, bang, done, own, after) = (live[0].Id, live[1].Id, live[2].Id, live[3].Id, live[4].Id, live[5].Id, live[6].Id, live[7].Id);
        Assert.AreEqual(hel, lo);
        Assert.AreEqual(hel, bang, "a title update does not end the message");
        Assert.AreEqual("native-1", own, "an agent's own ID passes unchanged");
        Assert.AreEqual(5, new[] { user, thought, hel, done, after }.Distinct().Count(), "the user message, the thought, and each message after another part of the turn");
        Assert.AreNotEqual(own, after);

        await using var later = await TestClient.ConnectAsync(conductor, "later");
        await later.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });
        CollectionAssert.AreEqual(live, Chunks(later), "the replay has the live stream's IDs");
    }

    [TestMethod]
    public void Journal_NeverMergesChunksWithoutAMessageId()
    {
        var path = Path.Combine(TestDirs.New(), "j.jsonl");
        var journal = new SessionJournal(path, maxEntries: 16);
        // Two messages without IDs, as journals written before the service named every message have them.
        journal.Append(AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk { Content = new TextContent { Text = "first" } })!);
        journal.Append(AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk { Content = new TextContent { Text = "second" } })!);
        for (var i = 0; i < 20; i++)
            journal.Append(AcpJson.ToNode<SessionUpdate>(new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = $"{i} " }, MessageId = "m" })!);

        var entries = new SessionJournal(path, maxEntries: 16).ReadAll();
        Assert.IsTrue(entries.Count < 22, "it was compacted");
        CollectionAssert.AreEqual(new[] { "first", "second" }, entries.Take(2).Select(e => (string?)e["content"]!["text"]).ToArray());
        Assert.AreEqual(string.Concat(Enumerable.Range(0, 20).Select(i => $"{i} ")),
            string.Concat(entries.Skip(2).Select(e => (string?)e["content"]!["text"])), "chunks of one message still merge");
    }

    // ---------------------------------------------------------------- extension flags

    [TestMethod]
    public async Task ExtensionFlags_AreInTheCapabilities_AndForOneReleaseAlsoInTheResult()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(peers: false, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var capabilities = zed.Initialized!.AgentCapabilities!.Meta!;
        Assert.AreEqual(true, (bool?)capabilities[GorynychProtocol.MetaSessionsChanged]);
        Assert.AreEqual(true, (bool?)capabilities[GorynychProtocol.MetaArchive]);
        Assert.IsNull(capabilities[GorynychProtocol.MetaServiceId], "data is not a capability");
        var result = zed.Initialized!.Meta!;
        Assert.AreEqual("t", (string?)result[GorynychProtocol.MetaServiceId]);
        Assert.IsNotNull(result[GorynychProtocol.MetaAgents]);
        Assert.AreEqual(true, (bool?)result[GorynychProtocol.MetaSessionsChanged], "deprecated");
        Assert.AreEqual(true, (bool?)result[GorynychProtocol.MetaArchive], "deprecated");
    }
}
