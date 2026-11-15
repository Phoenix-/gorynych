using System.Diagnostics;
using System.Text.Json.Nodes;
using Gorynych.Management;

namespace Gorynych.Tests;

/// <summary>
/// The management commands as the person runs them: an isolated service process, an isolated pipe
/// and data folder, and the same executable as the CLI. No real harness is started.
/// </summary>
[TestClass]
public sealed class ManagementCliTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    sealed class Cli : IAsyncDisposable
    {
        readonly string pipe = "gorynych-test-" + Guid.NewGuid().ToString("N")[..10];
        public readonly string Data = TestDirs.New();
        public Process? Service;

        public string Pipe => pipe;

        public string[] BaseArgs => ["--pipe", pipe, "--data", Data];

        public async Task StartServiceAsync()
        {
            Service = Process.Start(new ProcessStartInfo(BridgeTests.ServiceExe())
            {
                ArgumentList = { "--pipe", pipe, "--data", Data, "--hosts", "off", "--no-channel", "--no-peers" },
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            _ = Service.StandardOutput.ReadToEndAsync();
            _ = Service.StandardError.ReadToEndAsync();
            await Until(async () =>
            {
                try
                {
                    await using var stream = await Gorynych.Acp.Hosting.ServicePipe.ConnectAsync(pipe, TimeSpan.FromMilliseconds(300));
                    return stream is not null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
            }, "the service pipe");
        }

        public async Task<CliResult> RunAsync(params string[] args)
        {
            var info = new ProcessStartInfo(BridgeTests.ServiceExe())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(Wait); }
            catch (TimeoutException) { try { process.Kill(); } catch (InvalidOperationException) { } throw; }
            return new CliResult(process.ExitCode, await output, await error);
        }

        public async ValueTask DisposeAsync()
        {
            try { await RunAsync("shutdown", "--pipe", pipe); } catch (Exception e) when (e is TimeoutException or InvalidOperationException) { }
            if (Service is { HasExited: false })
            {
                try { Service.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
                try { await Service.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception e) when (e is TimeoutException or InvalidOperationException) { }
            }
            Service?.Dispose();
        }
    }

    sealed record CliResult(int ExitCode, string Out, string Error)
    {
        public JsonObject Json()
        {
            var text = Out.Trim();
            var start = text.IndexOf('{');
            Assert.IsTrue(start >= 0, "no JSON on stdout: " + Out + "\nstderr: " + Error);
            return JsonNode.Parse(text[start..])!.AsObject();
        }
    }

    static async Task Until(Func<Task<bool>> condition, string what, int ms = 20_000)
    {
        for (var waited = 0; waited < ms; waited += 100)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Status_WithALiveService_IsUsefulAsJsonAndTree()
    {
        await using var cli = new Cli();
        await cli.StartServiceAsync();
        var json = await cli.RunAsync("status", "--pipe", cli.Pipe, "--data", cli.Data, "--json");
        Assert.AreEqual(0, json.ExitCode, json.Error);
        var report = json.Json();
        Assert.AreEqual(1, (int?)report["schemaVersion"]);
        var service = report["service"] as JsonObject;
        Assert.IsNotNull(service, "the service is reported");
        Assert.IsTrue((int?)service["pid"] > 0);
        Assert.IsNotNull(service["path"]);
        Assert.IsNotNull(service["sessions"]);
        Assert.IsNotNull(service["maintenance"]);
        Assert.IsNotNull(service["adapters"]);
        Assert.IsNotNull(report["external"], "external processes are a separate, informational list");

        var tree = await cli.RunAsync("status", "--pipe", cli.Pipe, "--data", cli.Data, "--tree");
        Assert.AreEqual(0, tree.ExitCode, tree.Error);
        StringAssert.Contains(tree.Out, "service");
        StringAssert.Contains(tree.Out, "adapter");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Status_WithoutAService_StillReportsTheDataFolderAndTheMarker()
    {
        await using var cli = new Cli();
        ManagementState.Write(cli.Data, ManagementState.Empty.WithStopped(["codex"]));
        var result = await cli.RunAsync("status", "--pipe", cli.Pipe, "--data", cli.Data, "--json");
        Assert.AreEqual(1, result.ExitCode, "no service is a nonzero status, but the report is still useful");
        var report = result.Json();
        Assert.IsNull(report["service"]);
        Assert.AreEqual(cli.Data, (string?)report["dataDirectory"]);
        Assert.AreEqual("codex", (string?)((report["maintenanceMarker"] as JsonObject)?["stoppedAgents"] as JsonArray)?[0]);
        StringAssert.Contains(result.Error, "no service");
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task StopAll_WaitsForTheService_RecordsIt_AndStartAllBringsItBack()
    {
        await using var cli = new Cli();
        await cli.StartServiceAsync();
        var stop = await cli.RunAsync("stop", "--all", "--wait", "--timeout", "15s", "--pipe", cli.Pipe, "--data", cli.Data);
        Assert.AreEqual(0, stop.ExitCode, stop.Error + stop.Out);
        StringAssert.Contains(stop.Out, "exited");
        Assert.IsTrue(cli.Service!.HasExited, "the service process exited");
        var marker = ManagementState.Read(cli.Data);
        Assert.IsTrue(marker.StoppedAll, "stop --all is remembered");

        // A bridge must not start it: the acp command reports the explicit stop instead.
        var blocked = await cli.RunAsync("acp", "--agent", "codex", "--pipe", cli.Pipe, "--data", cli.Data);
        Assert.AreEqual(1, blocked.ExitCode);
        StringAssert.Contains(blocked.Error, "stop --all");

        var start = await cli.RunAsync("start", "--all", "--pipe", cli.Pipe, "--data", cli.Data);
        Assert.AreEqual(0, start.ExitCode, start.Error + start.Out);
        Assert.IsFalse(ManagementState.Read(cli.Data).StoppedAll, "start --all clears the marker");
        await Until(async () =>
        {
            var status = await cli.RunAsync("status", "--pipe", cli.Pipe, "--data", cli.Data, "--json");
            if (status.ExitCode != 0) return false;
            var report = status.Json();
            Assert.IsFalse((bool?)report["service"]?["hosts"] == true, "start --all restores the remembered --hosts off");
            return true;
        }, "the service to answer again");
        var shutdown = await cli.RunAsync("shutdown", "--pipe", cli.Pipe);
        Assert.AreEqual(0, shutdown.ExitCode, shutdown.Error);
    }
}
