using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

public sealed partial class ClaudeLiveTests
{
    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Claude_NativeTaskTools_ReachTheClientAsFullReplacementPlans()
    {
        RequireLive();
        var dir = TestDirs.New();
        var log = new List<string>();
        await using var conductor = TestDirs.Conductor(dir, new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "adapter"), Model = "haiku", FetchCatalog = false,
            ClaudeStatePath = Path.Combine(dir, "native-trust.json"),
            Log = line => { lock (log) log.Add(line); },
        }));
        await using var client = await TestClient.ConnectAsync(conductor);
        var id = (await NewAsync(client, dir)).SessionId;
        try
        {
            await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest
            {
                SessionId = id, Prompt = [new TextContent
                {
                    Text = "This is a task-tool integration check in an empty temporary folder. "
                        + "Use TaskCreate to create exactly two tasks with subjects 'PLAN_CHECK_ONE' and 'PLAN_CHECK_TWO'. "
                        + "Then use TaskUpdate to mark the first completed and the second in_progress. "
                        + "Do not set activeForm. Do not run shell commands, read or edit files, or invoke other tools. "
                        + "Leave those two task statuses as requested and reply OK.",
                }],
            }).WaitAsync(TimeSpan.FromSeconds(140));
            var plans = client.Snapshot().Where(u => (string?)u["sessionUpdate"] == "plan").ToList();
            Assert.IsTrue(plans.Count >= 4, "native task events must yield plans: " + client.Text() + " | " + string.Join(" | ", log));
            var final = plans[^1]["entries"]!.AsArray();
            CollectionAssert.AreEqual(new[] { "completed", "in_progress" }, final.Select(e => (string?)e!["status"]).ToArray());
            Assert.AreEqual(2, final.Count);
            Assert.IsTrue(final.All(e => (string?)e!["priority"] == "medium"));
            Console.WriteLine("Native ACP plan evidence: " + string.Join("\n", plans.Select(p => p.ToJsonString())));
        }
        finally
        {
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
        }
    }
}
