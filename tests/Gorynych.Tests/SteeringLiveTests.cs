using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Codex;

namespace Gorynych.Tests;

/// <summary>Opt-in proof against owned native sessions: steering preserves an executing shell call.</summary>
[TestClass]
[TestCategory("Live")]
public sealed class SteeringLiveTests
{
    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task ClaudeNativeSteer_PreservesTheRunningTool() => NativeSteer_PreservesTheRunningTool("claude");

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task CodexNativeSteer_PreservesTheRunningTool() => NativeSteer_PreservesTheRunningTool("codex");

    async Task NativeSteer_PreservesTheRunningTool(string agent)
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1") Assert.Inconclusive("set GORYNYCH_LIVE=1");
        if (ExecutableLocator.Find(agent) is null) Assert.Inconclusive($"{agent} is not on PATH");
        var dir = TestDirs.New();
        var log = new List<string>();
        Action<string> trace = line => { lock (log) log.Add(line); };
        IAgentAdapter adapter = agent == "claude"
            ? new ClaudeAdapter(new ClaudeAdapterOptions
            {
                DataDirectory = Path.Combine(dir, "claude"), Model = "haiku", FetchCatalog = false,
                ClaudeStatePath = Path.Combine(dir, "claude-state.json"), Log = trace,
            })
            : new CodexAdapter(new CodexAdapterOptions { Log = trace });
        await using var conductor = TestDirs.Conductor(dir, adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent } })).SessionId;
        var marker = "TOOL_SURVIVED_" + Guid.NewGuid().ToString("N");
        var ack = "STEER_ACK_" + Guid.NewGuid().ToString("N");
        var started = Path.Combine(dir, "command-started");
        try
        {
            if (agent == "codex")
            {
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                    new JsonObject { ["sessionId"] = id, ["configId"] = "mode", ["value"] = "auto" });
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                    new JsonObject { ["sessionId"] = id, ["configId"] = "reasoning_effort", ["value"] = "low" });
            }
            var running = client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent
                {
                    Text = $"This is a steering integration test in an empty temporary directory. Run exactly this harmless shell command using your command tool: powershell -NoProfile -Command \"Set-Content -LiteralPath '{started}' -Value started; Start-Sleep -Seconds 8; Write-Output '{marker}'\". Wait for its completion (poll if needed). Do not modify any other files or run any other commands. Then report the command's actual output and answer any subsequent user instruction.",
                }] });
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            string? tool = null;
            while (DateTime.UtcNow < until && !running.IsCompleted)
            {
                tool = client.Snapshot().FirstOrDefault(u => (string?)u["sessionUpdate"] is "tool_call" or "tool_call_update"
                    && (string?)u["status"] is "pending" or "in_progress")?["toolCallId"]?.GetValue<string>();
                if (tool is not null && File.Exists(started)) break;
                await Task.Delay(50);
            }
            if (tool is null && running.IsCompleted) await running; // surface startup/auth failures instead of hiding them
            Assert.IsNotNull(tool, "the native agent must start the requested command before steering: " + client.Text());
            Assert.IsTrue(File.Exists(started), "the actual command must have started before steering: "
                + string.Join("\n", client.Snapshot().Select(u => u.ToJsonString())));
            Assert.IsFalse(running.IsCompleted);
            var steer = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodSteering, new JsonObject
            {
                ["sessionId"] = id,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Keep the current command running to completion. Include {ack} in your final reply along with the command's actual output." }),
                ["_meta"] = new JsonObject { ["steering"] = new JsonObject { ["idleBehavior"] = "promptRequired" } },
            });
            Assert.AreEqual(AcpSteering.Injected, (string?)steer?["outcome"]);
            Assert.AreEqual(StopReason.EndTurn, (await running.WaitAsync(TimeSpan.FromSeconds(120))).StopReason);
            var completed = client.Snapshot().Where(u => (string?)u["sessionUpdate"] == "tool_call_update"
                && (string?)u["toolCallId"] == tool && (string?)u["status"] == "completed").ToList();
            Assert.IsTrue(completed.Any(u => u.ToJsonString().Contains(marker, StringComparison.Ordinal)),
                "the executing call must complete and return the marker in its actual tool output");
            StringAssert.Contains(client.Text(), ack);
            Console.WriteLine($"{agent}: injected, tool {tool} completed with {marker}, final reply contains {ack}");
        }
        finally
        {
            lock (log) foreach (var line in log) Console.WriteLine(line);
            await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = id });
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });
        }
    }
}
