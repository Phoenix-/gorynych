using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
[TestCategory("Live")]
public sealed class MailboxLiveTests
{
    [TestMethod]
    [Timeout(240000, CooperativeCancellation = true)]
    public async Task NativeCodex_ReceivesWhileIdle_AndRepliesThroughItsRealMcpTool()
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1") Assert.Inconclusive("set GORYNYCH_LIVE=1");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gorynych.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        var exe = Path.Combine(root.FullName, "src", "Gorynych.Service", "bin", "Release", "net10.0", TestDirs.Exe("Gorynych.Service"));
        var data = TestDirs.New();
        var pipe = "gorynych-mailbox-live-" + Guid.NewGuid().ToString("N");
        var sender = new LifecycleAgent("echo", canDelete: true);
        var trace = new List<string>();
        await using var service = new Conductor(new ConductorOptions
        { DataDirectory = data, ServiceId = "mailbox-live", PeerServerCommand = [exe, "peers", "--pipe", pipe] },
            [sender.Adapter, new CodexAdapter(new CodexAdapterOptions { Log = s => { lock (trace) trace.Add(s); } })]);
        using var stop = new CancellationTokenSource();
        var listening = PipeListener.RunAsync(service, pipe, stop.Token);
        await using var client = await TestClient.ConnectAsync(service);
        string? native = null;
        try
        {
            var original = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = data, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "echo" } })).SessionId;
            native = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = data, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "codex" } })).SessionId;
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                new JsonObject { ["sessionId"] = native, ["configId"] = "reasoning_effort", ["value"] = "low" });
            var request = Guid.NewGuid().ToString("N");
            var marker = "MAILBOX_NATIVE_" + Guid.NewGuid().ToString("N");
            var receipt = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodSendMessage, new JsonObject
            {
                ["sessionId"] = native,
                ["text"] = $"This is a harmless messaging integration check. Call the gorynych reply_message tool exactly once, passing message_id \"{request}\" and text \"{marker}\". Do not edit files or run commands. Then finish your turn.",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = sender.Tokens[original], [GorynychProtocol.MetaMessageRequestId] = request },
            });
            Assert.AreEqual("accepted", (string?)receipt!["status"]);
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(180);
            var found = false;
            while (DateTime.UtcNow < until)
            {
                lock (sender.Prompts) found = sender.Prompts.Any(p => p.SessionId == original && p.Text.Contains(marker, StringComparison.Ordinal)
                    && p.Text.Contains("reply_to " + request, StringComparison.Ordinal));
                if (found) break;
                await Task.Delay(100);
            }
            Assert.IsTrue(found, "The native agent must call its actual MCP tool and wake the sender, not merely print a proposed call: " + string.Join("\n", trace));
            Console.WriteLine("Native Codex took an idle mailbox turn, called reply_message, and its correlated reply woke the original sender.");
        }
        finally
        {
            if (native is not null)
            {
                await client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = native });
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = native });
            }
            stop.Cancel();
            await listening;
        }
    }
}
