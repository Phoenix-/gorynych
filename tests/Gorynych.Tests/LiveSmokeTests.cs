using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Kimi;

namespace Gorynych.Tests;

/// <summary>
/// Opt-in checks against the real agents on this machine, under the user's own logins.
/// Run with <c>GORYNYCH_LIVE=1</c>; Claude uses <c>--model haiku</c> to spare the limit.
/// The Claude session runs in the repository folder (already trusted) and writes only to a
/// temporary file outside it.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class LiveSmokeTests
{
    static void RequireLive()
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1") Assert.Inconclusive("set GORYNYCH_LIVE=1 to run live agents");
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    static string ServiceExe(string root)
    {
        var bin = Path.Combine(root, "src", "Gorynych.Service", "bin");
        return Directory.EnumerateFiles(bin, "Gorynych.Service.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Claude_FirstPrompt_Approval_Title_Reload_AndOpinion()
    {
        RequireLive();
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("ConPTY is Windows only");
        var root = RepoRoot();
        var dir = TestDirs.New();
        var target = Path.Combine(dir, "live.txt");
        var adapter = new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "claude"),
            Model = "haiku",
            ChannelServerCommand = [ServiceExe(root), "channel"],
        });
        await using var conductor = TestDirs.Conductor(dir, adapter);
        string sessionId;
        await using (var client = await TestClient.ConnectAsync(conductor, "live-1"))
        {
            client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId });
            sessionId = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
            {
                Cwd = root,
                McpServers = [],
                Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" },
            })).SessionId;
            var result = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest
            {
                SessionId = sessionId,
                Prompt = [new TextContent { Text = $"Use the Write tool to create the file {target} containing the word hi. Then reply with exactly DONE." }],
            });
            Assert.AreEqual(StopReason.EndTurn, result.StopReason);
            Assert.IsTrue(client.Approvals.Any(a => a.ToolCall.Title?.Contains("live.txt", StringComparison.Ordinal) == true), "a Write approval round-tripped");
            Assert.AreEqual("hi", File.ReadAllText(target).Trim());
            StringAssert.Contains(client.Text(), "DONE");

            var provisional = client.Snapshot().First(u => (string?)u["sessionUpdate"] == "session_info_update")["title"]?.ToString();
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline && !client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "session_info_update" && (string?)u["title"] != provisional))
                await Task.Delay(500);
            Assert.IsTrue(client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "session_info_update" && (string?)u["title"] != provisional),
                "Claude's generated title arrived");
        }

        await using (var again = await TestClient.ConnectAsync(conductor, "live-2"))
        {
            await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
                AcpJson.ToNode(new LoadSessionRequest { SessionId = sessionId, Cwd = root, McpServers = [] }));
            StringAssert.Contains(again.Text("user_message_chunk"), "live.txt");
            StringAssert.Contains(again.Text(), "DONE");

            var opinion = await again.Rpc.SendRequestAsync("_gorynych/opinion", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["sender"] = "peer_kimi",
                ["text"] = "In one sentence: tabs or spaces for C#?",
            });
            Assert.IsFalse(string.IsNullOrWhiteSpace((string?)opinion?["text"]), "the channel reply came back");
        }
    }

    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task Kimi_FirstPrompt()
    {
        RequireLive();
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new KimiAdapter(new KimiAdapterOptions()));
        await using var client = await TestClient.ConnectAsync(conductor);
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome
        {
            OptionId = r.Options.First(o => o.Kind.Value.StartsWith("allow", StringComparison.Ordinal)).OptionId,
        });
        var sessionId = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = dir,
            McpServers = [],
            Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "kimi" },
        })).SessionId;
        try
        {
            var result = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest
            {
                SessionId = sessionId,
                Prompt = [new TextContent { Text = "Create a file named hello.txt in the current directory containing the word hi. Then reply with exactly DONE." }],
            });
            Assert.AreEqual(StopReason.EndTurn, result.StopReason);
            Assert.IsTrue(client.Approvals.Count > 0, "Kimi asked for approval");
            StringAssert.Contains(client.Text(), "DONE");
        }
        catch (JsonRpcException e) when (e.Code == JsonRpcException.AuthRequired)
        {
            Assert.Inconclusive($"Kimi refused the turn: {e.Message}");
        }
        finally
        {
            // Leave no test session behind in the user's Kimi history.
            try { await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = sessionId }); }
            catch (JsonRpcException) { }
        }
    }
}
