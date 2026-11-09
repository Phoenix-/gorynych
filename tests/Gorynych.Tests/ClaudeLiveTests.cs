using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Opt-in checks of the headless Claude adapter's catalog and Full access against the real
/// <c>claude</c> on this machine, under the user's own plan login. Run with <c>GORYNYCH_LIVE=1</c>.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed partial class ClaudeLiveTests
{
    static void RequireLive()
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1") Assert.Inconclusive("set GORYNYCH_LIVE=1 to run live agents");
        if (ExecutableLocator.Find("claude") is null) Assert.Inconclusive("claude is not on PATH");
    }

    static string ServiceExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx")))
                return Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "Gorynych.Service", "bin"), TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).First();
        throw new InvalidOperationException("repository root not found");
    }

    static async Task Until(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(200);
    }

    static Task<NewSessionResponse> NewAsync(TestClient client, string cwd, string agent = "claude") =>
        client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = cwd, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        });

    [GeneratedRegex("fetching the model catalog in .+ \\(session ([0-9a-f-]{36}), not kept\\)")]
    private static partial Regex FetchLine();

    /// <summary>
    /// The catalog: fetched at start with no prompt and no session kept in <c>~/.claude</c>, shown to
    /// the open thread, and kept for the next start, whose <c>session/new</c> has all three selectors
    /// before Claude runs. No tokens: nothing but <c>initialize</c> is sent.
    /// </summary>
    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task Claude_Catalog_IsFetchedAtStart_LeavesNoSession_AndIsKeptForTheNextStart()
    {
        RequireLive();
        var dir = TestDirs.New();
        var log = new List<string>();
        ClaudeAdapter Adapter() => new(new ClaudeAdapterOptions { DataDirectory = Path.Combine(dir, "claude"), Log = m => { lock (log) log.Add(m); } });
        var catalog = Path.Combine(dir, "claude", "catalog.json");

        await using (var conductor = TestDirs.Conductor(dir, Adapter()))
        {
            await using var client = await TestClient.ConnectAsync(conductor);
            var first = await NewAsync(client, dir);
            Console.WriteLine($"first session/new before the catalog: {string.Join(", ", first.ConfigOptions?.Select(o => o.Id) ?? [])}");
            await Until(() => File.Exists(catalog), 120_000);
            Assert.IsTrue(File.Exists(catalog), "fetched: " + string.Join(" | ", log));
            await Until(() => { lock (client.Updates) return client.SessionUpdates.Any(u => u.SessionId == first.SessionId && (string?)u.Update["sessionUpdate"] == "config_option_update"); }, 10_000);
            lock (client.Updates)
                Assert.IsTrue(client.SessionUpdates.Any(u => u.SessionId == first.SessionId && (string?)u.Update["sessionUpdate"] == "config_option_update"
                    && u.Update["configOptions"]!.AsArray().Any(o => (string?)o!["id"] == "model")), "the open thread got its selectors");
            Assert.AreEqual(0, client.Approvals.Count, "the fetch asked nothing");
        }
        string fetchId;
        lock (log) fetchId = log.Select(l => FetchLine().Match(l)).First(m => m.Success).Groups[1].Value;
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var named = Directory.EnumerateFileSystemEntries(home, "*" + fetchId + "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        // Claude makes an empty session-env/<id> folder for every run, as for the user's own; nothing more may be there.
        var left = named.Where(p => File.Exists(p) || Directory.EnumerateFileSystemEntries(p).Any()).ToList();
        Assert.AreEqual(0, left.Count, "no file of the fetch in ~/.claude: " + string.Join(", ", left));
        Console.WriteLine($"named after the fetch in ~/.claude (empty folders only): {string.Join(", ", named.Select(p => Path.GetRelativePath(home, p)))}");
        var slug = new string(Path.GetFullPath(Path.Combine(dir, "claude", "catalog")).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        Assert.IsFalse(Directory.Exists(Path.Combine(home, "projects", slug)), "no project folder for the fetch's folder");
        Console.WriteLine($"fetch session {fetchId}: nothing in {home}; catalog: {File.ReadAllText(catalog).Length} bytes");

        lock (log) log.Clear();
        await using (var conductor = TestDirs.Conductor(dir, Adapter()))
        {
            await using var client = await TestClient.ConnectAsync(conductor);
            var created = await NewAsync(client, dir);
            CollectionAssert.AreEquivalent(new[] { "mode", "model", "reasoning_effort" }, created.ConfigOptions!.Select(o => o.Id).ToArray(),
                "all three before any prompt after a restart");
            lock (log)
            {
                Assert.IsFalse(log.Any(l => l.Contains("fetching the model catalog", StringComparison.Ordinal)), "kept, so not fetched again");
                Assert.IsFalse(log.Any(l => l.Contains("launching headless", StringComparison.Ordinal)), "no Claude ran for it");
            }
            var modes = ((SessionConfigSelect)created.ConfigOptions!.Single(o => o.Id == "mode")).Options.AsArray().Select(o => (string)o!["value"]!).ToArray();
            Console.WriteLine($"after the restart: {string.Join(", ", created.ConfigOptions!.Select(o => o.Id))}; modes {string.Join(", ", modes)}");
            CollectionAssert.Contains(modes, ClaudeOptions.FullAccess);
        }
    }

    /// <summary>A service with the real Claude on <c>haiku</c> and a scripted <c>echo</c> agent that answers "pong", on its own pipe.</summary>
    static (Conductor Conductor, LifecycleAgent Echo, List<string> Log, Func<CancellationToken, Task> Listen) LiveService(string dir)
    {
        var pipe = "gorynych-live-" + Guid.NewGuid().ToString("N")[..8];
        var log = new List<string>();
        var echo = new LifecycleAgent("echo", canDelete: true) { Answer = "pong" };
        var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = dir,
            ServiceId = "live",
            PeerServerCommand = [ServiceExe(), "peers", "--pipe", pipe],
        }, [new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "claude"),
            Model = "haiku",
            ClaudeStatePath = Path.Combine(dir, "no-claude-state.json"),
            Log = m => { lock (log) log.Add(m); },
        }), echo.Adapter]);
        return (conductor, echo, log, ct => PipeListener.RunAsync(conductor, pipe, ct));
    }

    static int PromptsOf(LifecycleAgent agent)
    {
        lock (agent.Prompts) return agent.Prompts.Count;
    }

    /// <summary>The approvals other than the folder-trust question.</summary>
    static string[] Asked(TestClient client) =>
        client.Approvals.Where(a => a.ToolCall.ToolCallId != "trust-folder").Select(a => a.ToolCall.Title ?? "").ToArray();

    static string AskPrompt(string target, string question) =>
        $"Call the tool mcp__gorynych__ask_agent once with session_id \"{target}\" and question \"{question}\". Then reply in one sentence with what it returned.";

    /// <summary>
    /// Outside full access, <c>ask_agent</c> asking a session that is not in it runs unasked in Ask and
    /// in auto (the launch's allow rule comes before the classifier), and a project deny rule still
    /// stops it. Three short turns on <c>haiku</c>; every approval but the folder's trust is declined.
    /// </summary>
    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Claude_AskAgent_RunsUnasked_InAskAndAuto_AndADenyRuleStillStopsIt()
    {
        RequireLive();
        var dir = TestDirs.New();
        var folder = Path.Combine(dir, "work");
        var denied = Path.Combine(dir, "denied");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(denied, ".claude"));
        File.WriteAllText(Path.Combine(denied, ".claude", "settings.json"), """{"permissions":{"deny":["mcp__gorynych__ask_agent"]}}""");
        var (service, echo, log, listen) = LiveService(dir);
        await using var conductor = service;
        using var stop = new CancellationTokenSource();
        var listening = listen(stop.Token);
        try
        {
            await using var client = await TestClient.ConnectAsync(conductor);
            client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome
            {
                OptionId = r.ToolCall.ToolCallId == "trust-folder" ? r.Options[0].OptionId : r.Options.First(o => o.Kind.Value.StartsWith("reject", StringComparison.Ordinal)).OptionId,
            });
            var target = (await NewAsync(client, folder, "echo")).SessionId;
            await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = target, Prompt = [new TextContent { Text = "hello" }] });
            async Task<string> AskFromAsync(string cwd, string? mode, string question)
            {
                var id = (await NewAsync(client, cwd)).SessionId;
                if (mode is not null)
                    await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = id, ["configId"] = "mode", ["value"] = mode });
                var r = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                    new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = AskPrompt(target, question) }] });
                Console.WriteLine($"{mode ?? "default"} in {Path.GetFileName(cwd)}: {r.StopReason}; echo prompts {PromptsOf(echo)}; asked: {string.Join(" | ", Asked(client))}; session {id}");
                return id;
            }

            await AskFromAsync(folder, null, "ping");
            Assert.AreEqual(2, PromptsOf(echo), "asked, in Ask: " + string.Join(" | ", log));
            await AskFromAsync(folder, "auto", "ping again");
            Assert.AreEqual(3, PromptsOf(echo), "asked, in auto: " + string.Join(" | ", log));
            Assert.AreEqual(0, Asked(client).Length, "nothing asked but the folder's trust");

            await AskFromAsync(denied, null, "ping once more");
            Assert.AreEqual(3, PromptsOf(echo), "the deny rule kept ask_agent from running");
            Assert.AreEqual(0, Asked(client).Length);
        }
        finally
        {
            stop.Cancel();
            await listening;
        }
    }

    /// <summary>
    /// Full access on <c>haiku</c>: a shell write and <c>ask_agent</c> to a session outside full access
    /// run with no question; a session outside full access asking this one is approved by the person
    /// first (declined here, so Claude gets nothing); back in Ask a write asks again. Three short turns
    /// in a temporary folder.
    /// </summary>
    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Claude_FullAccess_RunsUnasked_AndASessionOutsideItIsApprovedBeforeAskingIt()
    {
        RequireLive();
        var dir = TestDirs.New();
        var folder = Path.Combine(dir, "work");
        Directory.CreateDirectory(folder);
        var (service, echo, log, listen) = LiveService(dir);
        await using var conductor = service;
        using var stop = new CancellationTokenSource();
        var listening = listen(stop.Token);
        try
        {
            await using var client = await TestClient.ConnectAsync(conductor);
            // The folder-trust question and Claude's own approvals are allowed; the question into full access is declined.
            client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome
            {
                OptionId = r.ToolCall.Title?.StartsWith("Ask session_id", StringComparison.Ordinal) == true ? "decline" : r.Options[0].OptionId,
            });
            var target = (await NewAsync(client, folder, "echo")).SessionId;
            await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = target, Prompt = [new TextContent { Text = "hello" }] });
            var id = (await NewAsync(client, folder)).SessionId;
            async Task<JsonNode?> ModeAsync(string mode) => await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                new JsonObject { ["sessionId"] = id, ["configId"] = "mode", ["value"] = mode });
            async Task PromptAsync(string text)
            {
                var r = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                    new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = text }] });
                Console.WriteLine($"> {text}\n< {r.StopReason}; approvals so far: {string.Join(" | ", client.Approvals.Select(a => a.ToolCall.Title))}");
            }

            var full = await ModeAsync(ClaudeOptions.FullAccess);
            Assert.AreEqual(ClaudeOptions.FullAccess, (string?)full!["configOptions"]!.AsArray().Single(o => (string?)o!["id"] == "mode")!["currentValue"]);

            // 1. A shell write: no question but the folder's trust.
            await PromptAsync("Run this shell command with your Bash or PowerShell tool, exactly once: echo hi > shell.txt . Then reply DONE.");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "shell.txt")), "the shell wrote: " + string.Join(" | ", log));
            Assert.AreEqual(0, Asked(client).Length, "nothing asked in full access but the folder's trust");

            // 2. ask_agent into a session outside full access: no question either.
            await PromptAsync(AskPrompt(target, "ping"));
            Assert.AreEqual(2, PromptsOf(echo), "asked: " + string.Join(" | ", log));
            Assert.AreEqual(0, Asked(client).Length, "nothing asked");

            // 3. The echo session, outside full access, asks this one: the person is asked first, on the echo session, and declines.
            var declined = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
            {
                ["sessionId"] = id,
                ["text"] = "What did you just write?",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = echo.Tokens[target] },
            }));
            Console.WriteLine($"echo -> claude in full access: {declined.Message}");
            var card = client.Approvals.Single(a => a.ToolCall.Title?.StartsWith("Ask session_id", StringComparison.Ordinal) == true);
            Assert.AreEqual(target, card.SessionId, "asked on the asking session");
            StringAssert.StartsWith(card.ToolCall.Title, $"Ask session_id {id}");
            lock (client.Updates)
                Assert.IsFalse(client.SessionUpdates.Any(u => u.SessionId == id && u.Update.ToJsonString().Contains("What did you just write?", StringComparison.Ordinal)),
                    "declined, so Claude got nothing");

            // 4. Back to Ask: a write asks again.
            await ModeAsync("default");
            var before = client.Approvals.Count;
            await PromptAsync("Create the file note.txt containing exactly hi with your Write tool. Then reply DONE.");
            Assert.IsTrue(client.Approvals.Count > before, "the write asked: " + string.Join(" | ", client.Approvals.Select(a => a.ToolCall.Title)));
            Console.WriteLine($"claude session {id}");
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
        }
        finally
        {
            stop.Cancel();
            await listening;
        }
    }}
