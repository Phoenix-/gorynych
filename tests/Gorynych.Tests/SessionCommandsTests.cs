using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The agent's list of commands (<c>available_commands_update</c>) is the session's state, as ACP
/// has it: each list replaces the one before. Kimi sends it on every load and resume, twice
/// (Kimi Code 2.5.2), so journaled it grew the journal with every open, and a client that loaded
/// the session got every stale list again.
/// </summary>
[TestClass]
public sealed class SessionCommandsTests
{
    static readonly string Cwd = Path.GetTempPath();

    /// <summary>How long a request may take: a request that never ends fails the test instead of hanging it.</summary>
    static readonly TimeSpan Answer = TimeSpan.FromSeconds(10);

    static Task<JsonNode?> NewAsync(TestClient client) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() }).WaitAsync(Answer);

    static Task<JsonNode?> LoadAsync(TestClient client) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = "s1", ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() }).WaitAsync(Answer);

    static Task<JsonNode?> ResumeAsync(TestClient client) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = "s1", ["cwd"] = Cwd }).WaitAsync(Answer);

    static Task PromptAsync(TestClient client, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = "s1", Prompt = [new TextContent { Text = text }] }).WaitAsync(Answer);

    static string JournalOf(string dir) => Path.Combine(dir, "journal", SessionJournal.FileNameFor("s1"));

    static int ListsIn(string journal) => File.ReadAllLines(journal).Count(l => l.Contains("\"available_commands_update\""));

    /// <summary>The names in each list of commands the client got, in order.</summary>
    static string[] Lists(TestClient client) =>
        client.Snapshot().Where(SessionJournal.IsCommands)
            .Select(u => string.Join(",", u["availableCommands"]!.AsArray().Select(c => (string)c!["name"]!))).ToArray();

    static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10);
        Assert.IsTrue(done(), "timed out");
    }

    [TestMethod]
    public async Task TwoOpensInARow_AddNoListToTheJournal_AndEachLoadGetsTheCurrentListOnce_AfterTheHistory()
    {
        var dir = TestDirs.New();
        var kimi = new CommandsAgent();
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);
        await using var first = await TestClient.ConnectAsync(conductor, "zed");
        await NewAsync(first);
        await PromptAsync(first, "go");
        var journal = JournalOf(dir);
        Assert.AreEqual(0, ListsIn(journal));
        var lines = File.ReadAllLines(journal).Length;

        foreach (var n in new[] { 1, 2 })
        {
            await kimi.Adapter.Current!.Rpc.DisposeAsync(); // the agent stops, so the load opens the session in it again
            await Task.Delay(100);
            await using var zed = await TestClient.ConnectAsync(conductor, $"zed-{n}");
            await LoadAsync(zed);

            Assert.AreEqual(n, kimi.Methods.Count(m => m == AcpMethods.Agent.SessionLoad), "the agent was asked to open it, and sent its list twice");
            CollectionAssert.AreEqual(new[] { "compact,status" }, Lists(zed), "the current list, once");
            Assert.IsTrue(SessionJournal.IsCommands(zed.Snapshot()[^1]), "after the history it replays");
            Assert.AreEqual("hi", zed.Text());
            Assert.AreEqual(0, ListsIn(journal));
            Assert.AreEqual(lines, File.ReadAllLines(journal).Length, "the open added nothing to the journal");
        }
    }

    [TestMethod]
    public async Task ANewList_ReachesTheAttachedClients_AndTheSameListIsNotSentAgain()
    {
        var dir = TestDirs.New();
        var kimi = new CommandsAgent();
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await NewAsync(zed);
        await using var other = await TestClient.ConnectAsync(conductor, "other");
        await LoadAsync(other);
        CollectionAssert.AreEqual(new[] { "compact,status" }, Lists(other));

        kimi.Next = ["compact", "status", "skill"]; // the agent's list changes in the turn
        await PromptAsync(zed, "go");
        await PromptAsync(zed, "again"); // and comes again the same
        await Until(() => Lists(other).Length == 2);
        await Task.Delay(50);

        CollectionAssert.AreEqual(new[] { "compact,status", "compact,status,skill" }, Lists(zed), "the first after its answer, the new one live, each once");
        CollectionAssert.AreEqual(new[] { "compact,status", "compact,status,skill" }, Lists(other));
        Assert.AreEqual(0, ListsIn(JournalOf(dir)));
    }

    /// <summary>The lines <paramref name="client"/> receives from now on, in order.</summary>
    static List<string> Received(TestClient client)
    {
        var lines = new List<string>();
        client.Rpc.Trace = (way, line) => { if (way == "<") lock (lines) lines.Add(line); };
        return lines;
    }

    /// <summary>The list came after the answer to <c>session/new</c>: before it, Zed does not know the session and drops it.</summary>
    static void AssertListAfterTheAnswer(List<string> lines)
    {
        string[] got;
        lock (lines) got = lines.ToArray();
        var answer = Array.FindIndex(got, l => l.Contains("\"result\"", StringComparison.Ordinal) && l.Contains("\"sessionId\":\"s1\"", StringComparison.Ordinal));
        var list = Array.FindIndex(got, l => l.Contains("\"available_commands_update\"", StringComparison.Ordinal));
        Assert.IsTrue(answer >= 0 && list > answer, $"the answer at {answer}, the list at {list}");
    }

    [TestMethod]
    public async Task ANewSession_GetsTheListTheAgentSentBeforeItsAnswer_AfterTheAnswer()
    {
        var kimi = new CommandsAgent(); // the list comes before the agent's own answer
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var lines = Received(zed);
        await NewAsync(zed);
        await Until(() => Lists(zed).Length == 1);
        await Task.Delay(50);

        CollectionAssert.AreEqual(new[] { "compact,status" }, Lists(zed), "once");
        AssertListAfterTheAnswer(lines);
    }

    [TestMethod]
    public async Task ANewSession_GetsAListThatComesAfterItsAnswer_Once()
    {
        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kimi = new CommandsAgent { ListAfterNew = after.Task };
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var lines = Received(zed);
        await NewAsync(zed);
        after.SetResult(); // the agent sends its list once the client has its answer
        await Until(() => Lists(zed).Length == 1);
        await Task.Delay(50);

        CollectionAssert.AreEqual(new[] { "compact,status" }, Lists(zed), "live, and not again");
        AssertListAfterTheAnswer(lines);
    }

    [TestMethod]
    public async Task ANewSession_GetsAListThatComesWhileItsAnswerIsOnTheWay_AfterTheAnswer_Once()
    {
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kimi = new CommandsAgent { ListAfterNew = send.Task };
        Conductor? service = null;
        await using var conductor = service = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(),
            ServiceId = "test-service",
            // The client is attached, its answer not written yet: the agent's list comes now.
            Answering = async id =>
            {
                send.SetResult();
                await Until(() => service!.Feed(id).HasCommands);
            },
        }, [kimi.Adapter]);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var lines = Received(zed);
        await NewAsync(zed);
        await Until(() => Lists(zed).Length == 1);
        await Task.Delay(50);

        CollectionAssert.AreEqual(new[] { "compact,status" }, Lists(zed), "once");
        AssertListAfterTheAnswer(lines);
    }

    [TestMethod]
    public async Task ANewSessionsClient_DetachedBeforeItsAnswer_GetsNoList()
    {
        var kimi = new CommandsAgent();
        Conductor? service = null;
        await using var conductor = service = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(),
            ServiceId = "test-service",
            Answering = id =>
            {
                var feed = service!.Feed(id);
                foreach (var client in feed.Clients()) feed.Detach(client); // a send to it failed, say
                return Task.CompletedTask;
            },
        }, [kimi.Adapter]);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await NewAsync(zed);
        await Task.Delay(100);

        Assert.AreEqual(0, Lists(zed).Length);
    }

    [TestMethod]
    public async Task AResume_GetsTheCurrentList_ThatAResumeDoesNotReplay()
    {
        var kimi = new CommandsAgent();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await NewAsync(zed);
        kimi.Next = ["compact", "status", "skill"];
        await PromptAsync(zed, "go");

        await using var again = await TestClient.ConnectAsync(conductor, "zed-restarted");
        await ResumeAsync(again);

        CollectionAssert.AreEqual(new[] { "compact,status,skill" }, Lists(again));
        Assert.AreEqual(1, again.Snapshot().Length, "a resume replays nothing else");
        CollectionAssert.DoesNotContain(kimi.Methods, AcpMethods.Agent.SessionResume, "the session was open: the list the agent last sent");
    }

    [TestMethod]
    public async Task AJournalWrittenBefore_KeepsOnlyItsLastList_AndALoadThatCannotOpenTheSessionSendsIt()
    {
        var dir = TestDirs.New();
        var journal = JournalOf(dir);
        OldJournal(journal);
        var kimi = new CommandsAgent { LoadFails = true };
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed);

        CollectionAssert.AreEqual(new[] { "old" }, Lists(zed), "the last one, once");
        CollectionAssert.AreEqual(new[] { "user_message_chunk", "agent_message_chunk", "available_commands_update" }, zed.Kinds());
        Assert.AreEqual(1, ListsIn(journal), "the journal on disk keeps the last one only");
    }

    [TestMethod]
    public async Task AJournalWrittenBefore_GivesWayToTheListTheAgentSends()
    {
        var dir = TestDirs.New();
        OldJournal(JournalOf(dir));
        var kimi = new CommandsAgent();
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed);

        CollectionAssert.AreEqual(new[] { "compact,status" }, Lists(zed), "the agent's list, not the journal's as well");
        Assert.AreEqual("hi", zed.Text());
    }

    [TestMethod]
    public void AJournalWrittenBefore_IsReadWithItsLastList_AndRewrittenSo()
    {
        var path = Path.Combine(TestDirs.New(), SessionJournal.FileNameFor("s1"));
        OldJournal(path);
        var before = File.ReadAllLines(path).Length;

        var read = new SessionJournal(path, 100).ReadAll();

        Assert.AreEqual(1, read.Count(SessionJournal.IsCommands));
        Assert.AreEqual("old", (string?)read.Single(SessionJournal.IsCommands)["availableCommands"]![0]!["name"]);
        Assert.AreEqual(before - 2, read.Count);
        Assert.AreEqual(before - 2, File.ReadAllLines(path).Length);
        Assert.AreEqual(1, ListsIn(path));
        Assert.IsTrue(new SessionJournal(path, 100).HasTurns);
    }

    [TestMethod]
    public void AJournalWithOneList_IsLeftAsItIs()
    {
        var path = Path.Combine(TestDirs.New(), SessionJournal.FileNameFor("s1"));
        var text = Line(List("only")) + Line(Chunk("user_message_chunk", "go"));
        File.WriteAllText(path, text);
        var written = File.GetLastWriteTimeUtc(path);

        Assert.AreEqual(2, new SessionJournal(path, 100).ReadAll().Count);
        Assert.AreEqual(text, File.ReadAllText(path));
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>A journal of the session as the service wrote it before: a list for each open, the last one "old".</summary>
    static void OldJournal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            Line(List("older")) + Line(Chunk("user_message_chunk", "go")) + Line(List("older"))
            + Line(Chunk("agent_message_chunk", "hi")) + Line(List("old")));
    }

    static string Line(JsonObject update) => update.ToJsonString() + "\n";

    static JsonObject List(params string[] names) => new()
    {
        ["sessionUpdate"] = "available_commands_update",
        ["availableCommands"] = new JsonArray(names.Select(n => (JsonNode)new JsonObject { ["name"] = n, ["description"] = n }).ToArray()),
    };

    static JsonObject Chunk(string kind, string text) => new()
    {
        ["sessionUpdate"] = kind,
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
        ["messageId"] = kind + "-1",
    };

    /// <summary>
    /// A Kimi-like agent with one session, <c>s1</c>: it sends its list of commands after
    /// <c>session/new</c>, and twice on every <c>session/load</c> (around its replay of the
    /// session's one turn, "go" and "hi", without message IDs) and <c>session/resume</c>, as Kimi
    /// Code 2.5.2 does. A turn sends <see cref="Next"/> as its new list, when set.
    /// </summary>
    sealed class CommandsAgent
    {
        string[] current = ["compact", "status"];

        public string[]? Next { get; set; }
        public bool LoadFails { get; init; }

        /// <summary>When set, the list comes after the agent's answer to <c>session/new</c>, once this completes.</summary>
        public Task? ListAfterNew { get; init; }

        public CommandsAgent()
        {
            Adapter = new ScriptedAdapter("kimi", async (agent, req, ct) =>
            {
                switch (req.Method)
                {
                    case AcpMethods.Agent.Initialize:
                        return ScriptedAgent.Init(list: true, resume: true, load: true);
                    case AcpMethods.Agent.SessionNew when ListAfterNew is { } later:
                        req.AfterResult(async () =>
                        {
                            await later;
                            await Notify(agent, List(current));
                        });
                        return new JsonObject { ["sessionId"] = "s1" };
                    case AcpMethods.Agent.SessionNew:
                        await Notify(agent, List(current));
                        return new JsonObject { ["sessionId"] = "s1" };
                    case AcpMethods.Agent.SessionList:
                        return new JsonObject { ["sessions"] = new JsonArray(new JsonObject { ["sessionId"] = "s1", ["cwd"] = Cwd }) };
                    case AcpMethods.Agent.SessionLoad when LoadFails:
                        throw new JsonRpcException(JsonRpcException.InternalError, "cannot load");
                    case AcpMethods.Agent.SessionLoad:
                        await Notify(agent, List(current));
                        await Notify(agent, Replayed("user_message_chunk", "go"));
                        await Notify(agent, Replayed("agent_message_chunk", "hi"));
                        await Notify(agent, List(current));
                        return new JsonObject();
                    case AcpMethods.Agent.SessionResume:
                        await Notify(agent, List(current));
                        await Notify(agent, List(current));
                        return new JsonObject();
                    case AcpMethods.Agent.SessionPrompt:
                        await Notify(agent, Replayed("agent_message_chunk", "hi"));
                        if (Next is { } next) await Notify(agent, List(current = next));
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

        static JsonObject Replayed(string kind, string text) => new()
        {
            ["sessionUpdate"] = kind,
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
        };

        static Task Notify(ScriptedAgent agent, JsonObject update) =>
            agent.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject { ["sessionId"] = "s1", ["update"] = update });
    }
}
