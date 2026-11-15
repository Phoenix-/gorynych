using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Adapters.Codex;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
[TestCategory("Live")]
public sealed class CodexImportedHistoryLiveTests
{
    // Explicit IDs only: never resume, prompt, rename, or delete a user's thread.
    [TestMethod]
    public async Task SavedImportedHistory_ReadsThroughTheConductor_AndReopens()
    {
        var id = Environment.GetEnvironmentVariable("GORYNYCH_HISTORY_THREAD");
        var exe = Environment.GetEnvironmentVariable("GORYNYCH_HISTORY_CODEX");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(exe))
            Assert.Inconclusive("set GORYNYCH_HISTORY_THREAD and GORYNYCH_HISTORY_CODEX for the read-only history probe");
        await using var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = TestDirs.New(), ServiceId = "history-probe",
            PeerServerCommand = ["unused.exe"],
        }, [new CodexAdapter(new CodexAdapterOptions
        {
            Executable = exe,
            // Exercise the adapter's native read-only path regardless of the actual holder.
            FindHolders = ids => ids.ToDictionary(i => i, _ => "read-only history probe"),
            LocksDirectory = Path.Combine(TestDirs.New(), "unused-locks"),
        })]);
        string[]? first = null;
        for (var open = 0; open < 2; open++)
        {
            await using var client = await TestClient.ConnectAsync(conductor, "history-probe", agent: "codex");
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject
            {
                ["sessionId"] = id, ["cwd"] = Environment.CurrentDirectory, ["mcpServers"] = new JsonArray(),
            });
            var messages = client.Snapshot().Where(u => (string?)u["sessionUpdate"] is "user_message_chunk" or "agent_message_chunk")
                .Select(u => u.ToJsonString()).ToArray();
            Assert.IsTrue(messages.Length > 0, "the imported session must contain messages");
            if (first is null) first = messages;
            else CollectionAssert.AreEqual(first, messages, "reopen must replay the same history once");
            Console.WriteLine($"Open {open + 1}: {messages.Length} message updates; mark {conductor.Feed(id!).Journal.NativeMark}");
        }
    }
}
