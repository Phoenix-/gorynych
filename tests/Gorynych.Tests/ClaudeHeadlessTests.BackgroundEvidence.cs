using System.Diagnostics;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Adapters.Claude;
using System.Text.Json.Nodes;

namespace Gorynych.Tests;

public sealed partial class ClaudeHeadlessTests
{
    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task HostedFakeClaudePublishesComparableBackgroundEvidence()
    {
        var dir = TestDirs.New(); var adapter = new ClaudeAdapter(Options(dir, fetchCatalog: false));
        var hosts = new InProcessHosts(_ => adapter); var service = hosts.Conductor(dir, adapter); hosts.Service = service;
        var client = await TestClient.ConnectAsync(service); string? sid = null;
        try
        {
            sid = await NewAsync(client, dir);
            await PromptAsync(client, sid, "background snapshot probe").WaitAsync(TimeSpan.FromSeconds(20));
            var id = sid;
            var feed = service.Feed(id);
            var route = service.Agents["claude"].RouteOf(id)!;
            await LinkHarness.Until(() => feed.BackgroundEvidenceFor(route)?.Items.Contains("fake-background") == true,
                "actual FakeClaude background task").WaitAsync(TimeSpan.FromSeconds(20));
            var first = feed.BackgroundEvidenceFor(route)!;
            var parent = await NewAsync(client, dir);
            service.ReplyMailbox.Accept("fake-owner", parent, sid, "work", delegation: true);
            Assert.IsTrue(service.ReplyMailbox.TryMarkStarted("fake-owner", sid)); service.ReplyMailbox.MarkDelivered("fake-owner", sid);
            var generation = (string)service.ReplyMailbox.FindAssignment("fake-owner", sid)!["generation"]!;
            var pipe = "background-mcp-" + Guid.NewGuid().ToString("N");
            using var stop = new CancellationTokenSource();
            var listening = PipeListener.RunAsync(service, pipe, stop.Token);
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gorynych.slnx"))) root = root.Parent;
            Assert.IsNotNull(root);
            var exe = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Gorynych.Service", "bin"), TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories).First();
            var start = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "peers", "--pipe", pipe, "--token", service.Store.Get(sid)!.PeerToken! }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start test MCP process");
            try
            {
                await using var mcp = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, "background-mcp-test"); mcp.Start();
                var init = await mcp.SendRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() }).WaitAsync(TimeSpan.FromSeconds(20));
                Assert.IsNotNull(init!["capabilities"]!["tools"]);
                var tools = await mcp.SendRequestAsync("tools/list", new JsonObject()).WaitAsync(TimeSpan.FromSeconds(20));
                Assert.IsTrue(tools!["tools"]!.AsArray().Any(t => (string?)t?["name"] == "list_background_tasks"));
                async Task<JsonNode> Tool(string name, JsonObject args)
                {
                    var response = await mcp.SendRequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args }).WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.AreNotEqual(true, (bool?)response!["isError"], (string?)response["content"]?[0]?["text"]);
                    return JsonNode.Parse((string)response["content"]![0]!["text"]!)!;
                }
                var listed = await Tool("list_background_tasks", new JsonObject());
                Assert.AreEqual("fake-background", (string?)listed["items"]![0]!["taskId"]);
                var registered = await Tool("update_continuation", new JsonObject { ["source"] = "delegation", ["assignment_id"] = "fake-owner",
                    ["generation"] = generation, ["expected_revision"] = 1, ["operation_id"] = "mcp-register", ["action"] = "register", ["background_ref"] = (string)listed["items"]![0]!["reference"]! });
                Assert.AreEqual("waiting", (string?)registered["continuation"]!["state"]);
                File.WriteAllText(Path.Combine(dir, "release-background"), "release");
                await LinkHarness.Until(() => feed.BackgroundEvidenceFor(route) is { } next && next.Revision > first.Revision && next.Items.Length == 0,
                    "actual FakeClaude background disappearance").WaitAsync(TimeSpan.FromSeconds(20));
                var read = await Tool("read_assignment", new JsonObject { ["source"] = "delegation", ["assignment_id"] = "fake-owner" });
                Assert.AreEqual("ready", (string?)read["continuation"]!["state"]);
            }
            finally
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                finally { stop.Cancel(); await listening; }
            }
            File.WriteAllText(Path.Combine(dir, "release-background"), "release");
            await LinkHarness.Until(() => feed.BackgroundEvidenceFor(route) is { } next && next.Revision > first.Revision && next.Items.Length == 0,
                "actual FakeClaude background disappearance").WaitAsync(TimeSpan.FromSeconds(20));
            var later = feed.BackgroundEvidenceFor(route)!;
            Assert.AreEqual(first.Stream, later.Stream); Assert.AreEqual(first.HostId, later.HostId);
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "release-background"), "release");
            if (sid is not null)
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sid }).WaitAsync(TimeSpan.FromSeconds(20));
            await client.DisposeAsync(); await service.DisposeAsync(); hosts.BreakAll();
        }
    }
}
