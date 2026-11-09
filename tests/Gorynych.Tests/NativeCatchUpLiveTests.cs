using System.Diagnostics;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Codex;
using Gorynych.Adapters.Kimi;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Opt-in checks that the journal catches up with each real agent's own history
/// (docs/service-protocol.md, "History made outside Gorynych"), under the user's own login. Run
/// with <c>GORYNYCH_LIVE=1</c>. Each makes a new session in a new temporary folder, runs one short
/// turn in Gorynych, lets it go, continues it with the agent's own CLI in that folder, and opens
/// it in Gorynych again: the turn made outside shows once, marked <c>native</c>, and a second open
/// adds nothing. The user's own sessions are never touched.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class NativeCatchUpLiveTests
{
    static void RequireLive(string executable)
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1") Assert.Inconclusive("set GORYNYCH_LIVE=1 to run live agents");
        if (ExecutableLocator.Find(executable) is null) Assert.Inconclusive($"{executable} is not on PATH");
    }

    static Conductor Conductor(string dir, IAgentAdapter adapter) => new(new ConductorOptions
    {
        DataDirectory = dir,
        ServiceId = "live-catch-up",
        UnloadAfter = TimeSpan.Zero, // let go of a session as soon as its client closes it
    }, [adapter]);

    /// <summary>
    /// Runs the agent's own CLI in <paramref name="folder"/>, as a person in a terminal would. Tried
    /// again for a while when the session is still held (Codex lets its writer lock go a moment
    /// after the thread is unloaded).
    /// </summary>
    static async Task<string> RunNativeAsync(string folder, string executable, params string[] args)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(folder, executable, args);
            }
            catch (AssertFailedException e) when (attempt < 10 && e.Message.Contains("active writer", StringComparison.Ordinal))
            {
                Console.WriteLine($"{executable}: still held, trying again ({attempt})");
                await Task.Delay(3000);
            }
        }
    }

    static async Task<string> RunOnceAsync(string folder, string executable, string[] args)
    {
        var psi = new ProcessStartInfo(ExecutableLocator.Find(executable)!)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        var text = await output + await error;
        Assert.AreEqual(0, process.ExitCode, $"{executable} {string.Join(' ', args)}: {text}");
        return text;
    }

    static async Task<JsonObject[]> SeenAsync(Conductor conductor, string agent, string sessionId, string folder)
    {
        await using var client = await TestClient.ConnectAsync(conductor, "viewer", agent: agent);
        await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = sessionId, ["cwd"] = folder, ["mcpServers"] = new JsonArray() });
        await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId });
        await conductor.CleanupsIdleAsync();
        return client.Snapshot();
    }

    static string AgentText(IEnumerable<JsonObject> updates, Func<JsonObject, bool> which) =>
        string.Concat(updates.Where(u => (string?)u["sessionUpdate"] == "agent_message_chunk" && which(u)).Select(u => (string?)u["content"]?["text"]));

    static bool Native(JsonObject u) => (string?)u["_meta"]?[GorynychProtocol.MetaOrigin] == "native";

    /// <summary>
    /// The common steps: a turn in Gorynych, the service stopped (its agents with it, so nothing of
    /// Gorynych holds the session), the agent's CLI on the session (<paramref name="continueNatively"/>),
    /// then the service started again on the same data and the session opened twice.
    /// </summary>
    static async Task<string> CheckAsync(string agent, Func<IAgentAdapter> adapter, string dir, string folder,
        Func<TestClient, string, Task>? setUp, Func<string, Task> continueNatively, Func<TestClient, string, Task>? cleanUp)
    {
        string id;
        await using (var conductor = Conductor(dir, adapter()))
        {
            await using (var zed = await TestClient.ConnectAsync(conductor, "live", agent: agent))
            {
                id = (await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                    new NewSessionRequest { Cwd = folder, McpServers = [] })).SessionId;
                if (setUp is not null) await setUp(zed, id);
                await zed.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                    new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "Reply with exactly the word pong and nothing else." }] });
                await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
            }
            await conductor.CleanupsIdleAsync();
            Console.WriteLine($"{agent} {id}: mark after Gorynych's turn: {conductor.Feed(id).Journal.NativeMark ?? "(none)"}");
        }

        await continueNatively(id);

        await using (var conductor = Conductor(dir, adapter()))
        {
            var first = await SeenAsync(conductor, agent, id, folder);
            Console.WriteLine($"{agent} {id}: mark after the catch-up: {conductor.Feed(id).Journal.NativeMark ?? "(none)"}");
            foreach (var u in first.Where(u => (string?)u["sessionUpdate"] is "user_message_chunk" or "agent_message_chunk"))
                Console.WriteLine($"  {(string?)u["sessionUpdate"]} {(string?)u["messageId"]} {(Native(u) ? "native" : "")}: {(string?)u["content"]?["text"]}");
            StringAssert.Contains(AgentText(first, u => !Native(u)).ToLowerInvariant(), "pong", "Gorynych's own turn");
            StringAssert.Contains(AgentText(first, Native).ToLowerInvariant(), "ping", "the turn made outside, marked native");
            var again = await SeenAsync(conductor, agent, id, folder);
            static string[] Messages(JsonObject[] updates) => updates
                .Where(u => (string?)u["sessionUpdate"] is "user_message_chunk" or "agent_message_chunk")
                .Select(u => $"{(string?)u["sessionUpdate"]} {(string?)u["messageId"]}").Distinct().ToArray();
            CollectionAssert.AreEqual(Messages(first), Messages(again), "a second open adds nothing: the same messages, by ID");
            if (cleanUp is not null)
                await using (var zed = await TestClient.ConnectAsync(conductor, "cleanup", agent: agent))
                    await cleanUp(zed, id);
        }
        return id;
    }

    const string Outside = "Reply with exactly the word ping and nothing else.";

    /// <summary>
    /// A thread Gorynych lets go of is free for another Codex app at once, with the service still
    /// running: its app-server unloads it without Codex's 60 s delay, and the close waits for that.
    /// </summary>
    [TestMethod]
    [Timeout(900_000, CooperativeCancellation = true)]
    public async Task Codex_AThreadGorynychLetGoOf_IsFreeForCodexExecResumeAtOnce()
    {
        RequireLive("codex");
        var dir = TestDirs.New();
        var folder = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;
        await using var conductor = Conductor(dir, new CodexAdapter(new CodexAdapterOptions()));
        string id;
        await using (var zed = await TestClient.ConnectAsync(conductor, "live", agent: "codex"))
        {
            id = (await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = folder, McpServers = [] })).SessionId;
            await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                new JsonObject { ["sessionId"] = id, ["configId"] = "reasoning_effort", ["value"] = "low" });
            await zed.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "Reply with exactly the word pong and nothing else." }] });
            await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
        }
        for (var i = 0; i < 100 && conductor.Agents["codex"].IsLive(id); i++)
        {
            await conductor.CleanupsIdleAsync();
            await Task.Delay(100);
        }
        Assert.IsFalse(conductor.Agents["codex"].IsLive(id), "let go in its agent");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var output = await RunOnceAsync(folder, "codex", ["exec", "resume", "-c", "model_reasoning_effort=\"low\"", "--skip-git-repo-check", id, Outside]);
        Console.WriteLine($"codex exec resume took the thread at once and ran its turn in {started.ElapsedMilliseconds} ms");
        StringAssert.Contains(output.ToLowerInvariant(), "ping");

        await using (var cleanup = await TestClient.ConnectAsync(conductor, "cleanup", agent: "codex"))
            await cleanup.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });
    }

    [TestMethod]
    [Timeout(900_000, CooperativeCancellation = true)]
    public async Task Codex_ATurnTakenWithCodexExecResume_ShowsOnceWhenOpenedAgain()
    {
        RequireLive("codex");
        var dir = TestDirs.New();
        var folder = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;
        await CheckAsync("codex", () => new CodexAdapter(new CodexAdapterOptions()), dir, folder,
            setUp: (zed, id) => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                new JsonObject { ["sessionId"] = id, ["configId"] = "reasoning_effort", ["value"] = "low" }),
            continueNatively: id => RunNativeAsync(folder, "codex", "exec", "resume", "-c", "model_reasoning_effort=\"low\"", "--skip-git-repo-check", id, Outside),
            cleanUp: (zed, id) => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id }));
    }

    // Claude's transcripts of these sessions stay in its projects folder: Gorynych never writes or
    // deletes anything of an agent's own (~/.claude), and Claude has no delete.
    [TestMethod]
    [Timeout(900_000, CooperativeCancellation = true)]
    public async Task Claude_ATurnTakenWithClaudeResume_ShowsOnceWhenOpenedAgain()
    {
        RequireLive("claude");
        var dir = TestDirs.New();
        var folder = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;
        await CheckAsync("claude", () => new ClaudeAdapter(new ClaudeAdapterOptions { DataDirectory = Path.Combine(dir, "claude"), Model = "haiku" }), dir, folder,
            setUp: null,
            continueNatively: id => RunNativeAsync(folder, "claude", "-p", "--resume", id, "--model", "haiku", Outside),
            cleanUp: null);
    }

    [TestMethod]
    [Timeout(900_000, CooperativeCancellation = true)]
    public async Task Kimi_ATurnTakenWithTheKimiCli_ShowsOnceWhenOpenedAgain()
    {
        RequireLive("kimi");
        var dir = TestDirs.New();
        var folder = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;
        await CheckAsync("kimi", () => new KimiAdapter(new KimiAdapterOptions()), dir, folder,
            setUp: null,
            continueNatively: id => RunNativeAsync(folder, "kimi", "-S", id, "-p", Outside),
            cleanUp: (zed, id) => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id }));
    }

    /// <summary>
    /// A Kimi turn with a tool call, opened again, is not taken for a rewritten history. Kimi's
    /// replay gives its messages no IDs, so only the tool calls' IDs could tell the turns apart;
    /// the check prints what the replay gives them next to what the turn gave them live.
    /// </summary>
    [TestMethod]
    [Timeout(900_000, CooperativeCancellation = true)]
    public async Task Kimi_ATurnWithAToolCall_IsNotTakenForARewriteWhenOpenedAgain()
    {
        RequireLive("kimi");
        var dir = TestDirs.New();
        var folder = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;
        File.WriteAllText(Path.Combine(folder, "note.txt"), "kiwi\n");
        static string[] ToolIds(IEnumerable<JsonNode> updates) => updates.OfType<JsonObject>()
            .Where(u => (string?)u["sessionUpdate"] == "tool_call").Select(u => (string?)u["toolCallId"] ?? "(none)").ToArray();
        string id;
        string[] live;
        await using (var conductor = Conductor(dir, new KimiAdapter(new KimiAdapterOptions())))
        {
            await using (var zed = await TestClient.ConnectAsync(conductor, "live", agent: "kimi"))
            {
                id = (await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                    new NewSessionRequest { Cwd = folder, McpServers = [] })).SessionId;
                await zed.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                    new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "Read the file note.txt in the current folder with your file reading tool, then reply with exactly the word it holds and nothing else." }] });
                await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
            }
            await conductor.CleanupsIdleAsync();
            live = ToolIds(conductor.Feed(id).Journal.ReadAll());
        }
        Console.WriteLine($"kimi {id}: tool calls live: {string.Join(", ", live)}");
        Assert.AreNotEqual(0, live.Length, "the turn called a tool");

        // What Kimi itself replays of the session, apart from the service.
        var agent = await new KimiAdapter(new KimiAdapterOptions()).StartAsync(CancellationToken.None);
        var replayed = new List<JsonObject>();
        await using (agent)
        await using (var acp = new JsonRpcConnection(agent.Input, agent.Output, "direct-client")
        {
            NotificationHandler = (method, p) =>
            {
                if (method == AcpMethods.Client.SessionUpdate) lock (replayed) replayed.Add((JsonObject)p!["update"]!.DeepClone());
                return Task.CompletedTask;
            },
        })
        {
            acp.Start();
            await acp.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
            await acp.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = folder, ["mcpServers"] = new JsonArray() });
        }
        string[] again;
        lock (replayed) again = ToolIds(replayed);
        Console.WriteLine($"kimi {id}: tool calls replayed: {string.Join(", ", again)}");
        lock (replayed)
            Console.WriteLine($"kimi {id}: replayed message IDs: {string.Join(", ", replayed.Where(u => ((string?)u["sessionUpdate"])?.EndsWith("_chunk", StringComparison.Ordinal) == true).Select(u => (string?)u["messageId"] ?? "(none)").Distinct())}");

        await using (var conductor = Conductor(dir, new KimiAdapter(new KimiAdapterOptions())))
        {
            var seen = await SeenAsync(conductor, "kimi", id, folder);
            var rewritten = seen.Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaNotice] == HistoryCatchUp.HistoryRewritten);
            Console.WriteLine($"kimi {id}: taken for a rewrite: {rewritten}; tool calls in the journal now: {string.Join(", ", ToolIds(conductor.Feed(id).Journal.ReadAll()))}");
            Assert.IsFalse(rewritten, "the same turn, opened again, is not a rewritten history");
            StringAssert.Contains(AgentText(seen, _ => true).ToLowerInvariant(), "kiwi");
            static string[] Parts(Conductor c, string id) => c.Feed(id).Journal.ReadAll().OfType<JsonObject>()
                .Where(u => SessionJournal.IsTurnKind((string?)u["sessionUpdate"]))
                .Select(u => $"{(string?)u["sessionUpdate"]} {(string?)u["messageId"] ?? (string?)u["toolCallId"]} {(string?)u["status"]}").ToArray();
            var before = Parts(conductor, id);
            await SeenAsync(conductor, "kimi", id, folder);
            CollectionAssert.AreEqual(before, Parts(conductor, id), "a second open adds no part of a turn");
            await using var zed = await TestClient.ConnectAsync(conductor, "cleanup", agent: "kimi");
            await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });
        }
    }
}
