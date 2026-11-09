using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

public sealed partial class ClaudeHeadlessTests
{
    /// <summary>The user's and Claude's messages, "user: …" or "agent: …", a message's chunks joined.</summary>
    static string[] Messages(IEnumerable<JsonObject> updates)
    {
        var texts = new List<(string Kind, string? Id, string Text)>();
        foreach (var u in updates.Where(u => (string?)u["sessionUpdate"] is "user_message_chunk" or "agent_message_chunk"))
        {
            var (kind, id, text) = (((string)u["sessionUpdate"]!).Split('_')[0], (string?)u["messageId"], (string?)u["content"]?["text"] ?? "");
            if (texts.Count > 0 && texts[^1].Kind == kind && texts[^1].Id == id) texts[^1] = (kind, id, texts[^1].Text + text);
            else texts.Add((kind, id, text));
        }
        return texts.Select(t => $"{t.Kind}: {t.Text}").ToArray();
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ATurnTakenInTheTerminal_LandsInTheJournal_BeforeTheNextPrompt()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.UnloadingAtOnce(dir, new ClaudeAdapter(Options(dir)));
        string id;
        await using (var client = await TestClient.ConnectAsync(conductor))
        {
            id = await NewAsync(client, dir);
            await PromptAsync(client, id, "hello");
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
        }
        await conductor.CleanupsIdleAsync();
        var transcript = TranscriptTitles.ExpectedPath(dir, id, Path.Combine(FakeRoot, "projects"));
        var mark = TranscriptHistory.Mark(transcript)!;
        Assert.AreEqual(mark, conductor.Feed(id).Journal.NativeMark, "the turn ended with the transcript's mark");

        // claude --resume in a terminal, while Gorynych had let the session go: a turn in the transcript only.
        var last = mark[..mark.IndexOf('@')];
        File.AppendAllText(transcript,
            new JsonObject
            {
                ["type"] = "user", ["uuid"] = "terminal-u", ["parentUuid"] = last, ["isSidechain"] = false,
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "From the terminal" },
            }.ToJsonString() + "\n" +
            new JsonObject
            {
                ["type"] = "assistant", ["uuid"] = "terminal-a", ["parentUuid"] = "terminal-u", ["isSidechain"] = false,
                ["message"] = new JsonObject
                {
                    ["id"] = "msg_terminal", ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Done there" }),
                },
            }.ToJsonString() + "\n");

        await using (var client = await TestClient.ConnectAsync(conductor, "again"))
            await PromptAsync(client, id, "next");

        await using var viewer = await TestClient.ConnectAsync(conductor, "viewer");
        await viewer.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = dir, ["mcpServers"] = new JsonArray() });
        CollectionAssert.AreEqual(new[] { "user: hello", "agent: echo: hello", "user: From the terminal", "agent: Done there", "user: next", "agent: echo: next" },
            Messages(viewer.Snapshot()), string.Join(" | ", Messages(viewer.Snapshot())));
        var terminal = viewer.Snapshot().Single(u => (string?)u["content"]?["text"] == "From the terminal");
        Assert.AreEqual(("native", "terminal-u"), ((string?)terminal["_meta"]?[GorynychProtocol.MetaOrigin], (string?)terminal["messageId"]));
        Assert.AreEqual(TranscriptHistory.Mark(transcript), conductor.Feed(id).Journal.NativeMark, "the last prompt's mark");
    }
}
