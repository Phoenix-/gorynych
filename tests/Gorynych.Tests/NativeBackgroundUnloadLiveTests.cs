using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Codex;
using Gorynych.Service;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

/// <summary>
/// Opt-in reproduction of an unattended build losing its native harness to idle unload.
/// The real background worker must survive a completed turn, client disconnect, and more
/// than one full unload period. Idle cases get a new full grace after completion;
/// rotation cases keep their old host until the native worker has completed.
/// Uses only newly created sessions and files. Run with GORYNYCH_LIVE=1 on Windows.
/// </summary>
[TestClass]
[TestCategory("Live")]
[DoNotParallelize]
public sealed class NativeBackgroundUnloadLiveTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(120);
    static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task Claude_NativeWorkerSurvivesIdleUnloadUntilItCompletes() => CheckAsync("claude");

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task Codex_NativeWorkerSurvivesIdleUnloadUntilItCompletes() => CheckAsync("codex");

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task Claude_NativeWorkerSurvivesRestartAndHostRotation() => CheckAsync("claude", rotateHost: true);

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task Codex_NativeWorkerSurvivesRestartAndHostRotation() => CheckAsync("codex", rotateHost: true);

    static async Task CheckAsync(string agent, bool rotateHost = false)
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1")
            Assert.Inconclusive("set GORYNYCH_LIVE=1 to run live agents");
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows native process reproduction");
        if (ExecutableLocator.Find(agent) is null) Assert.Inconclusive($"{agent} is not on PATH");

        var dir = TestDirs.New();
        var work = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;
        var worker = Path.Combine(work, "background-worker.ps1");
        var release = Path.Combine(work, "release.txt");
        var completed = Path.Combine(work, "completed.txt");
        File.WriteAllText(worker, $$"""
            $ErrorActionPreference = 'Stop'
            $root = '{{work.Replace("'", "''")}}'
            Set-Content -LiteralPath (Join-Path $root 'worker-pid.txt') -Value $PID
            $until = [DateTime]::UtcNow.AddSeconds(150)
            while (-not (Test-Path -LiteralPath (Join-Path $root 'release.txt'))) {
                if ([DateTime]::UtcNow -ge $until) { exit 2 }
                Start-Sleep -Milliseconds 100
            }
            Set-Content -LiteralPath (Join-Path $root 'completed.txt') -Value 'completed'
            """);
        var time = new ManualTime();
        var log = new List<string>();
        void Log(string message) { lock (log) log.Add(message); }
        IAgentAdapter Adapter() => agent == "claude"
            ? new ClaudeAdapter(new ClaudeAdapterOptions
            {
                DataDirectory = Path.Combine(dir, "claude"), Model = "haiku",
                FetchCatalog = false, AutoTitle = false, Log = Log,
            })
            : new CodexAdapter(new CodexAdapterOptions
            {
                BackgroundPollInterval = TimeSpan.FromMilliseconds(200), AutoTitle = false, Log = Log,
            });
        var adapter = Adapter();
        var hosts = rotateHost ? new InProcessHosts(_ => Adapter()) : null;
        Conductor Start()
        {
            var build = hosts?.Build;
            var service = new Conductor(new ConductorOptions
            {
                DataDirectory = dir, ServiceId = "native-background-unload",
                UnloadAfter = Idle, Time = time, ActivityNoticeInterval = TimeSpan.Zero,
                Hosts = hosts, HostToken = hosts is null ? null : InProcessHosts.Token,
                HostGeneration = hosts is null ? null : _ => new HostGeneration(build!, []),
                SessionHosts = new HashSet<string>(agent == "claude" ? [agent] : [], StringComparer.Ordinal),
            }, [adapter]);
            if (hosts is not null) hosts.Service = service;
            return service;
        }
        var conductor = Start();
        TestClient? client = null;
        string? id = null;
        try
        {
            client = await TestClient.ConnectAsync(conductor, "owned-live-probe", agent: agent);
            id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = work, McpServers = [] }).WaitAsync(Wait)).SessionId;
            if (agent == "codex")
            {
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                    new JsonObject { ["sessionId"] = id, ["configId"] = "mode", ["value"] = "auto" });
                await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                    new JsonObject { ["sessionId"] = id, ["configId"] = "reasoning_effort", ["value"] = "low" });
            }
            // Codex cannot rely on user-installed interpreters or script-file execution policy.
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(File.ReadAllText(worker)));
            var command = agent == "claude"
                ? $"\"{shell.Replace('\\', '/')}\" -NoProfile -NonInteractive -File \"{worker.Replace('\\', '/')}\""
                : $"& '{shell.Replace("'", "''")}' -NoProfile -NonInteractive -EncodedCommand {encoded}";
            var instructions = $"This is an authorized isolated background-process test. Run exactly one shell command: {command}. " +
                "Use Bash run_in_background=true for Claude, or a short yield allowing the command to remain running for Codex. " +
                "Then immediately answer STARTED and finish your turn. Do not wait for the command, poll it, run other commands, " +
                "change files, or stop it. The external test runner releases the worker after your answer.";
            var response = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = instructions }] }).WaitAsync(Wait);
            Assert.AreEqual(StopReason.EndTurn, response.StopReason);
            var failed = client.Snapshot().FirstOrDefault(update =>
                (string?)update["sessionUpdate"] == "tool_call_update" && (string?)update["status"] == "failed");
            Assert.IsNull(failed, $"The native command failed before the unload check: {failed}");
            var feed = conductor.Feed(id);
            var backgroundField = agent == "claude" ? "background" : "backgroundKnown";
            await Until(() => feed.Activity.Snapshot()[backgroundField] is JsonArray { Count: > 0 }, "native background observed after foreground ended");
            await Until(() => File.Exists(Path.Combine(work, "worker-pid.txt")), "native worker PID recorded");
            var workerPid = int.Parse(File.ReadAllText(Path.Combine(work, "worker-pid.txt")).Trim());
            using var ownedWorker = Process.GetProcessById(workerPid);
            Console.WriteLine($"{agent}: session {id}, owned worker {workerPid}, artifacts {dir}");

            await client.DisposeAsync();
            client = null;
            await Until(() => feed.Clients().Length == 0, "client detached");
            time.Advance(Idle * 3);
            await conductor.CleanupsIdleAsync().WaitAsync(Wait);
            Assert.IsTrue(conductor.Agents[agent].IsLive(id), "native session survives three unload periods with background work");
            Assert.IsFalse(ownedWorker.HasExited, "the actual worker was not killed by unload");
            Assert.IsFalse(File.Exists(completed), "worker is still waiting, rather than already finished");

            InProcessHosts.Running? oldHost = null;
            if (hosts is not null)
            {
                oldHost = hosts[agent];
                await HostTests.RestartAsync(conductor);
                await HostTests.GoneAsync(conductor, null, hosts);
                hosts.Build = "build-2";
                conductor = Start();
                feed = conductor.Feed(id);
                await Until(() => feed.HasBackgroundWork || oldHost.Run.IsCompleted, "background hold after host reconnect");
                await conductor.CleanupsIdleAsync().WaitAsync(Wait);
                Assert.IsFalse(oldHost.Run.IsCompleted, "a new generation cannot retire a host with background work");
                time.Advance(Idle * 3);
                await conductor.CleanupsIdleAsync().WaitAsync(Wait);
                Assert.IsFalse(ownedWorker.HasExited, "the same actual worker survives conductor restart and rotation");
                Console.WriteLine($"{agent}: old host {oldHost.Host.HostId} retained across restart with worker {workerPid}");
            }

            File.WriteAllText(release, "release");
            await Until(() => File.Exists(completed), "worker completed after surviving idle unload");
            if (oldHost is not null)
            {
                await Until(() => oldHost.Run.IsCompleted, "completed worker lets the old generation retire");
                Assert.IsFalse(conductor.Feed(id).HasBackgroundWork, "native completion released the restored hold");
            }
            else
            {
                await Until(() => feed.Activity.Snapshot()[backgroundField] is JsonArray { Count: 0 }, "native background became empty");
                await Until(() => time.Waiting > 0, "new idle grace registered");
                time.Advance(Idle - TimeSpan.FromSeconds(1));
                await conductor.CleanupsIdleAsync().WaitAsync(Wait);
                Assert.IsTrue(conductor.Agents[agent].IsLive(id), "full grace starts after background completion");
                time.Advance(TimeSpan.FromSeconds(1));
                await Until(() => !conductor.Agents[agent].IsLive(id), "session unloaded once its new grace elapsed");
            }
            Assert.AreEqual("completed", File.ReadAllText(completed).Trim());
            Console.WriteLine($"{agent}: PASS; worker survived, completed, and only then was its session unloaded");
        }
        finally
        {
            File.WriteAllText(release, "release");
            if (client is not null) await client.DisposeAsync();
            await conductor.DisposeAsync();
            lock (log) foreach (var message in log) Console.WriteLine(message);
        }
    }

    static async Task Until(Func<bool> condition, string why)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > Wait) Assert.Fail($"Timed out: {why}");
            await Task.Delay(50);
        }
    }
}
