using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Codex;
using Gorynych.Adapters.Dsh;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>Owned scratch sessions through the real service and adapter, under the user's login.</summary>
[TestClass]
[TestCategory("Live")]
[DoNotParallelize]
public sealed class AutoTitleLiveTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("codex")]
    [DataRow("claude")]
    [DataRow("dsh")]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task FirstPrompt_GetsANativeTitle_ThroughTheService(string agentName)
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1") Assert.Inconclusive("set GORYNYCH_LIVE=1");
        var data = TestDirs.New();
        var cwd = Path.Combine(data, "work");
        Directory.CreateDirectory(cwd);
        var log = new List<string>();
        void Log(string line) { lock (log) log.Add(line); }
        IAgentAdapter adapter = agentName switch
        {
            "codex" => new CodexAdapter(new CodexAdapterOptions { AutoTitle = true, Log = Log }),
            "claude" => new ClaudeAdapter(new ClaudeAdapterOptions
            {
                DataDirectory = Path.Combine(data, "claude"), AutoTitle = true,
                Model = "haiku", FetchCatalog = false, Log = Log,
            }),
            "dsh" => new DshAdapter(new DshAdapterOptions { DataDirectory = Path.Combine(data, "dsh"), Log = Log }),
            _ => throw new ArgumentException(agentName),
        };
        await using var conductor = new Conductor(new ConductorOptions { DataDirectory = data, ServiceId = "title-live" }, [adapter]);
        await using var client = await TestClient.ConnectAsync(conductor, "title-live", agent: agentName);
        // Only the owned empty workspace's folder-trust question may be approved; tool calls are refused.
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome
        {
            OptionId = r.ToolCall.Title?.Contains("Trust", StringComparison.OrdinalIgnoreCase) == true
                ? ClaudeHooks.AllowOptionId : r.Options.First(o => o.Kind == PermissionOptionKind.RejectOnce).OptionId,
        });
        var session = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = cwd, McpServers = [] });
        var id = session.SessionId;
        TestContext.WriteLine($"Owned {agentName} session: {id}; scratch: {data}");
        const string prompt = "We need to stabilize the flaky CI login test by removing its timing race. " +
            "For this title integration smoke test, do not inspect files or use tools: reply only OK.";
        try
        {
            if (agentName == "codex")
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                    new JsonObject { ["sessionId"] = id, ["configId"] = "reasoning_effort", ["value"] = "low" });
            var result = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = prompt }] });
            Assert.AreEqual(StopReason.EndTurn, result.StopReason);
            var deadline = DateTime.UtcNow.AddSeconds(80);
            string? title = null;
            while (DateTime.UtcNow < deadline)
            {
                var record = conductor.Store.Get(id);
                if (record is { TitleSource: "agent", Title: { Length: > 0 } candidate } &&
                    !prompt.StartsWith(candidate, StringComparison.Ordinal)) { title = candidate; break; }
                await Task.Delay(250);
            }
            Assert.IsNotNull(title, "No generated title reached the service. " + string.Join(" | ", log));
            var listed = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList,
                new ListSessionsRequest { Cwd = cwd });
            Assert.AreEqual(title, listed.Sessions.Single(s => s.SessionId == id).Title);
            TestContext.WriteLine($"Native title observed through service and list: {title}");
        }
        finally
        {
            lock (log) File.WriteAllLines(Path.Combine(data, "adapter.log"), log);
            if (agentName == "codex")
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });
            else
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
        }
    }
}
