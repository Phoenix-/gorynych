using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Opt-in checks of the Codex adapter against the real <c>codex app-server</c> on this machine,
/// under the user's own login. Run with <c>GORYNYCH_LIVE=1</c>. Two short turns at the lowest
/// reasoning effort, in a temporary folder; the thread is deleted at the end.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class CodexLiveTests
{
    static void RequireLive()
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1") Assert.Inconclusive("set GORYNYCH_LIVE=1 to run live agents");
        if (ExecutableLocator.Find("codex") is null) Assert.Inconclusive("codex is not on PATH");
    }

    static string ServiceExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx")))
                return Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "Gorynych.Service", "bin"), "Gorynych.Service.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).First();
        throw new InvalidOperationException("repository root not found");
    }

    static Conductor Conductor(string dir, List<string> log) => new(new ConductorOptions
    {
        DataDirectory = dir,
        ServiceId = "live",
        // The peers server cannot reach this test's service, but Codex lists its tools: the thread got it.
        PeerServerCommand = [ServiceExe(), "peers", "--pipe", "gorynych-live-none"],
    }, [new CodexAdapter(new CodexAdapterOptions { Log = m => { lock (log) log.Add(m); } })]);

    static Task<JsonNode?> SetAsync(TestClient client, string sessionId, string option, string value) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = sessionId, ["configId"] = option, ["value"] = value });

    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Codex_FirstPrompt_Approval_Reload_List_AndDelete()
    {
        RequireLive();
        var dir = TestDirs.New();
        var folder = Path.Combine(dir, "work");
        Directory.CreateDirectory(folder);
        var log = new List<string>();
        string id;
        await using (var conductor = Conductor(dir, log))
        await using (var zed = await TestClient.ConnectAsync(conductor, "live", agent: "codex"))
        {
            var created = await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = folder, McpServers = [] });
            id = created.SessionId;
            try
            {
                var options = created.ConfigOptions!.Select(o => o.Id).ToList();
                CollectionAssert.IsSubsetOf(new[] { "mode", "model", "reasoning_effort" }, options);
                await SetAsync(zed, id, "reasoning_effort", "low");
                await SetAsync(zed, id, "mode", "read-only");

                var first = await zed.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                    new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "Reply with exactly the word pong and nothing else." }] });
                Assert.AreEqual(StopReason.EndTurn, first.StopReason);
                StringAssert.Contains(zed.Text().ToLowerInvariant(), "pong");
                var answer = zed.Snapshot().Last(u => (string?)u["sessionUpdate"] == "agent_message_chunk");
                StringAssert.StartsWith((string?)answer["messageId"], "msg_", "Codex's own item ID");

                // A file write in read-only mode asks; the person declines, and no option writes Codex's config.
                zed.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "decline" });
                await zed.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest
                {
                    SessionId = id,
                    Prompt = [new TextContent { Text = "Use your apply_patch tool, not the shell, to create note.txt containing the word hi." }],
                });
                Assert.IsTrue(zed.Approvals.Count > 0, "the write asked: " + string.Join(" | ", log));
                foreach (var asked in zed.Approvals)
                    CollectionAssert.IsSubsetOf(asked.Options.Select(o => o.OptionId).ToList(), new[] { "accept", "acceptForSession", "decline", "cancel" });
                Assert.IsFalse(File.Exists(Path.Combine(folder, "note.txt")));
            }
            catch
            {
                await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });
                throw;
            }
        }

        // Another run of the service: a new app-server resumes the thread, which Codex lists.
        await using (var conductor = Conductor(dir, log))
        await using (var zed = await TestClient.ConnectAsync(conductor, "live again", agent: "codex"))
        {
            await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = folder, ["mcpServers"] = new JsonArray() });
            var listed = await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
            Assert.IsTrue(listed.Sessions.Any(s => s.SessionId == id), "listed after a restart");
            await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });
            var after = await zed.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
            Assert.IsFalse(after.Sessions.Any(s => s.SessionId == id), "deleted");
        }
    }
}
