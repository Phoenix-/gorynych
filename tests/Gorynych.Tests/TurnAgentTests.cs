using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Tests;

[TestClass]
public sealed class TurnAgentTests
{
    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task AnOldInstanceCannotCancelTheSameSessionInANewInstance()
    {
        var adapter = new TurnAgent("turns") { Hold = true };
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var oldInstance = await adapter.StartAsync(CancellationToken.None);
        var oldAgent = adapter.Current;
        adapter.During = (agent, _, _) =>
        {
            (agent == oldAgent ? oldStarted : newStarted).TrySetResult();
            return Task.CompletedTask;
        };
        await using var oldClient = new JsonRpcConnection(oldInstance.Input, oldInstance.Output, "old-instance")
        {
            NotificationHandler = (_, _) => Task.CompletedTask,
        };
        oldClient.Start();
        var sessionId = (string)(await oldClient.SendRequestAsync(AcpMethods.Agent.SessionNew,
            new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
        var oldPrompt = oldClient.SendRequestAsync(AcpMethods.Agent.SessionPrompt, Prompt(sessionId, "old"));
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var newInstance = await adapter.StartAsync(CancellationToken.None);
        await using var newClient = new JsonRpcConnection(newInstance.Input, newInstance.Output, "new-instance")
        {
            NotificationHandler = (_, _) => Task.CompletedTask,
        };
        newClient.Start();
        await newClient.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = sessionId });
        var newPrompt = newClient.SendRequestAsync(AcpMethods.Agent.SessionPrompt, Prompt(sessionId, "new"));
        await newStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await oldClient.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId });
            var ended = await Task.WhenAny(oldPrompt, newPrompt).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(oldPrompt, ended, "the old host's cancel belongs only to its own instance");
            Assert.AreEqual("cancelled", (string?)(await oldPrompt)!["stopReason"]);
            Assert.IsFalse(newPrompt.IsCompleted, "the new host's turn keeps running");
            adapter.Release();
            Assert.AreEqual("end_turn", (string?)(await newPrompt.WaitAsync(TimeSpan.FromSeconds(5)))!["stopReason"]);
        }
        finally
        {
            adapter.Release();
        }
    }

    static JsonObject Prompt(string sessionId, string text) => new()
    {
        ["sessionId"] = sessionId,
        ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };
}
