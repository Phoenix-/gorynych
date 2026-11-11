using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Acp.Link;
using Gorynych.Outside;

namespace Gorynych.Service;

/// <summary>
/// The stdio entry point for external ACP clients (Zed and the like), which spawn an agent and
/// speak ACP over its stdin and stdout: <c>Gorynych.Service acp --agent claude|kimi|codex|dsh [--pipe P]</c>.
/// It starts the service if needed, connects to its pipe and passes messages through on the
/// resumable link (<see cref="ResumableLink"/>), so the client keeps one connection while the
/// service restarts. It adds <c>_meta["gorynych/agent"]</c> to the client's <c>initialize</c>,
/// which scopes the connection to that agent, so the client itself stays plain ACP.
/// </summary>
public static class AcpBridge
{
    /// <param name="serviceArgs">Arguments for the service if the bridge has to start it.</param>
    /// <param name="agent">The agent the bridge scopes its client to; null for none, as a hub's link to another machine is.</param>
    public static async Task<int> RunAsync(string? agent, string pipe, IReadOnlyList<string> serviceArgs)
    {
        NamedPipeClientStream service;
        try
        {
            service = await ConnectOrStartAsync(pipe, serviceArgs);
        }
        catch (NotStartedException e)
        {
            Console.Error.WriteLine($"gorynych acp: {e.Message}");
            return 1;
        }
        catch (Exception e) when (e is TimeoutException or IOException or Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"gorynych acp: cannot reach the service on pipe {pipe}: {e.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine($"gorynych acp: cannot reach the service on pipe {pipe}: {e.Message}{Hint(e)}");
            return 1;
        }
        // The client that goes cancels a reconnect: no service is started for a bridge whose client is gone.
        var link = new ResumableLink(Console.OpenStandardInput(), Console.OpenStandardOutput(), async ct =>
        {
            var again = await ConnectOrStartAsync(pipe, serviceArgs, ct);
            return (again, again);
        }, LinkOptions(agent));
        return await link.RunAsync((service, service));
    }

    /// <summary>
    /// The bridge's link: it scopes the client's <c>initialize</c> to its agent, says on stderr what
    /// happens to the connection, and gives up when no service is back within 60 seconds.
    /// </summary>
    internal static ResumableLinkOptions LinkOptions(string? agent) => new()
    {
        Log = m => Console.Error.WriteLine($"gorynych acp: {m}"),
        InitializeMeta = agent is null ? null : new JsonObject { [GorynychProtocol.MetaAgent] = agent },
    };

    /// <summary>Connects to the service's pipe; when it is missing, starts the service and waits for it.</summary>
    static async Task<NamedPipeClientStream> ConnectOrStartAsync(string pipe, IReadOnlyList<string> serviceArgs, CancellationToken ct = default)
    {
        if (await ServicePipe.ConnectAsync(pipe, TimeSpan.FromMilliseconds(300), ct) is { } running) return running;
        ct.ThrowIfCancellationRequested();
        return await StartAndConnectAsync(pipe, serviceArgs);
    }

    /// <summary>The elevation hint for a pipe whose ACL refused this process; none for a pipe someone else owns.</summary>
    internal static string Hint(Exception e) => e is UnauthorizedAccessException and not ServicePipe.ForeignPipeException ? " " + ElevationHint : "";

    internal const string ElevationHint =
        "A service started elevated (from an administrator terminal) serves only elevated clients; a plain service " +
        "serves the user's plain and elevated clients alike.";

    /// <summary>
    /// <c>Gorynych.Service shutdown [--pipe P]</c>: asks the running service to exit, as a client's
    /// <c>_gorynych/shutdown</c> does, and waits up to <paramref name="waitSeconds"/> for its process
    /// to end. 0 when it stopped, 1 when no service runs on the pipe or it could not be asked, 2
    /// when it was asked but still runs.
    /// </summary>
    public static Task<int> ShutdownAsync(string pipe, int waitSeconds = 30) => StopAsync("shutdown", pipe, waitSeconds, async (rpc, processId) =>
    {
        await rpc.SendRequestAsync(GorynychProtocol.MethodShutdown, new JsonObject());
        Console.Error.WriteLine($"gorynych shutdown: asked the service (process {processId}) to exit; turns still running are cancelled");
        return null;
    });

    /// <summary>The exit code of <c>restart</c> when turns still ran after the wait and the restart was called off.</summary>
    public const int RestartCalledOffExitCode = 4;

    /// <summary>
    /// <c>Gorynych.Service restart [--pipe P] [--timeout M] [--no-wait] [--force]</c>: asks the running
    /// service to restart (<c>_gorynych/restart</c>): it starts no new turn and waits up to
    /// <paramref name="timeout"/> for the running ones (none with <c>--no-wait</c>). If none is left
    /// it exits, and this waits for its process to end. If some are left, the restart is called off
    /// and the service serves on: this lists them and exits with <see cref="RestartCalledOffExitCode"/>;
    /// with <paramref name="force"/> they are cancelled and the service exits all the same. The bridges
    /// connected to it start the next service, with the executable now at the path they were started
    /// from, or its logon task: so an update renames the running executable aside, copies the new one
    /// in its place, and runs this. Other exit codes as for <see cref="ShutdownAsync"/>. A service
    /// without restart (an older version) is asked to exit instead.
    /// </summary>
    public static Task<int> RestartAsync(string pipe, bool wait, TimeSpan timeout, bool force, int waitSeconds = 30) => StopAsync("restart", pipe, waitSeconds, async (rpc, processId) =>
    {
        // A service whose adapters run in hosts waits for no turn: they go on in the hosts (docs/service-protocol.md, "Adapter hosts").
        var status = await rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject());
        var hosted = status?["hosts"] is JsonValue h && h.TryGetValue<bool>(out var on) && on;
        var waiting = !wait || timeout <= TimeSpan.Zero ? "does not wait for the running ones"
            : $"waits up to {(timeout < TimeSpan.FromMinutes(1) ? $"{timeout.TotalSeconds:0.#} s" : $"{timeout.TotalMinutes:0.#} min")} for the running ones";
        Console.Error.WriteLine(hosted
            ? $"gorynych restart: asked the service (process {processId}) to restart; its adapters run in hosts, which keep the running turns " +
              "for the next service, so it waits for none"
            : $"gorynych restart: asked the service (process {processId}) to restart; it starts no new turn and {waiting}, " +
              (force ? "then cancels what still runs" : "and stays if any still runs"));
        try
        {
            var reply = await rpc.SendRequestAsync(GorynychProtocol.MethodRestart,
                new JsonObject { ["wait"] = wait, ["timeoutSeconds"] = timeout.TotalSeconds, ["force"] = force });
            var seconds = (double?)reply?["waitedSeconds"] ?? 0;
            if (reply?["restarted"] is JsonValue r && r.TryGetValue<bool>(out var restarted) && !restarted)
            {
                var running = reply["running"] as JsonArray ?? [];
                Console.Error.WriteLine($"gorynych restart: {running.Count} turn{(running.Count == 1 ? "" : "s")} still ran after {seconds:0.#} s, " +
                    "so the restart was called off and the service goes on:");
                foreach (var s in running.OfType<JsonObject>())
                    Console.Error.WriteLine($"  {(string?)s["agent"] ?? "?",-7} {(string?)s["sessionId"]}  {(string?)s["title"] ?? "(untitled)"}");
                Console.Error.WriteLine("gorynych restart: run it again once they end, with a longer --timeout, or with --force, which cancels them");
                return RestartCalledOffExitCode;
            }
            var cancelled = (int?)reply?["cancelledTurns"] ?? 0;
            if (reply?["turnsInHosts"] is JsonValue t && t.TryGetValue<int>(out var inHosts))
            {
                Console.Error.WriteLine($"gorynych restart: it is exiting; {inHosts} running turn{(inHosts == 1 ? " goes" : "s go")} on in {(inHosts == 1 ? "its host" : "their hosts")}");
                // What keeps running: a host of another build than the next service's is stale there, and
                // its sessions move to a host of the new build as they go idle (docs/service-protocol.md, "Updating adapters").
                foreach (var row in HostRows(status)) Console.Error.WriteLine("  " + row);
                Console.Error.WriteLine("gorynych restart: sessions in hosts of another build than the next service's move to its hosts as they go idle, " +
                    "and those hosts exit when they have none; `Gorynych.Service status` shows what still waits");
            }
            else
                Console.Error.WriteLine($"gorynych restart: it waited {seconds:0.#} s" +
                    (cancelled > 0 ? $" and cancelled {cancelled} turn{(cancelled == 1 ? "" : "s")} that still ran" : "") + "; it is exiting");
        }
        catch (JsonRpcException e) when (e.Code == JsonRpcException.MethodNotFound)
        {
            await rpc.SendRequestAsync(GorynychProtocol.MethodShutdown, new JsonObject());
            Console.Error.WriteLine("gorynych restart: the service is an older version without restart, so it was asked to exit; its running turns are cancelled");
        }
        return null;
    }, stopped: "; the next bridge or the logon task starts the new one");

    /// <summary>
    /// <c>Gorynych.Service status [--pipe P]</c>: the running service, its build, and per adapter its
    /// hosts: of the current build or stale, their options, sessions and running turns, and what still
    /// waits to move after an update. 0, or 1 when no service runs on the pipe or it could not be asked.
    /// </summary>
    public static async Task<int> StatusAsync(string pipe)
    {
        NamedPipeClientStream? stream;
        try
        {
            stream = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"gorynych status: cannot reach the service on pipe {pipe}: {e.Message}{Hint(e)}");
            return 1;
        }
        if (stream is null)
        {
            Console.Error.WriteLine($"gorynych status: no service is running on pipe {pipe}");
            return 1;
        }
        await using var rpc = new JsonRpcConnection(stream, stream, "status");
        rpc.Start();
        JsonNode? status;
        try
        {
            await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
            {
                ProtocolVersion = AcpMethods.ProtocolVersion,
                ClientInfo = new Implementation { Name = "gorynych-status", Version = AgentHost.Version },
            });
            status = await rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject());
        }
        catch (Exception e) when (e is IOException or JsonRpcException)
        {
            Console.Error.WriteLine($"gorynych status: the service on pipe {pipe} did not answer: {e.Message}");
            return 1;
        }
        var hosted = status?["hosts"] is JsonValue h && h.TryGetValue<bool>(out var on) && on;
        Console.WriteLine($"service: process {(int?)status?["processId"]}, version {(string?)status?["version"]}" +
            (hosted ? $", build {(string?)status?["build"]}, adapters in hosts" : ", adapters in its own process") +
            $", {(int?)status?["clients"] ?? 0} clients, {(int?)status?["sessions"] ?? 0} sessions");
        if ((string?)status?["dataFolderOpen"] is { } open) Console.WriteLine($"warning: {open}");
        foreach (var row in HostRows(status)) Console.WriteLine("  " + row);
        foreach (var remote in (status?[GorynychProtocol.StatusRemotes] as JsonArray ?? []).OfType<JsonObject>())
            Console.WriteLine($"remote {(string?)remote["name"]}: {RemoteState(remote)}");
        if (hosted)
        {
            var stale = (status?["agents"] as JsonArray ?? []).OfType<JsonObject>()
                .SelectMany(a => (a["hosts"] as JsonArray ?? []).OfType<JsonObject>().Where(x => x["current"] is JsonValue c && c.TryGetValue<bool>(out var current) && !current))
                .ToList();
            var waiting = stale.Sum(x => (x["waiting"] as JsonObject)?.Count ?? (x["sessions"] as JsonArray)?.Count ?? 0);
            // The executable renamed aside by an update (Gorynych.Service.*.old.exe) is free once no host of an earlier build runs.
            Console.WriteLine(stale.Count == 0 ? "nothing waits to move: every host runs the current build, and no process runs an earlier executable"
                : waiting == 0 ? $"{stale.Count} host{(stale.Count == 1 ? "" : "s")} of an earlier build {(stale.Count == 1 ? "is" : "are")} on {(stale.Count == 1 ? "its" : "their")} way out"
                : $"{waiting} session{(waiting == 1 ? "" : "s")} wait{(waiting == 1 ? "s" : "")} in hosts of an earlier build, each for what is said above");
        }
        return 0;
    }

    /// <summary>The remotes the service on <paramref name="pipe"/> has, from its <c>_gorynych/status</c>; null when none runs there.</summary>
    internal static async Task<JsonArray?> RemoteStatesAsync(string pipe)
    {
        try
        {
            await using var stream = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(1));
            if (stream is null) return null;
            await using var rpc = new JsonRpcConnection(stream, stream, "remote list");
            rpc.Start();
            await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
            {
                ProtocolVersion = AcpMethods.ProtocolVersion,
                ClientInfo = new Implementation { Name = "gorynych-remote", Version = AgentHost.Version },
            }).WaitAsync(TimeSpan.FromSeconds(10));
            var status = await rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject()).WaitAsync(TimeSpan.FromSeconds(10));
            return status?[GorynychProtocol.StatusRemotes] as JsonArray ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonRpcException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>A remote's state, from its entry in <c>_gorynych/status</c>, in words.</summary>
    internal static string RemoteState(JsonObject? remote)
    {
        if (remote is null) return "the running service does not have it yet: it reads remotes.json again within a few seconds";
        var since = (string?)remote["since"] is { } at && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? $" since {time.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "";
        if (remote["connected"] is JsonValue c && c.TryGetValue<bool>(out var connected) && connected)
        {
            var agents = string.Join(", ", (remote["agents"] as JsonArray ?? []).Select(a => (string?)a?["name"]).OfType<string>());
            return $"connected{since}, service {(string?)remote["serviceId"]}, agents: {(agents.Length == 0 ? "none" : agents)}";
        }
        // No error yet: its first connection is on its way.
        return (string?)remote["lastError"] is { } error ? $"not connected{since}: {error}" : $"connecting{since}";
    }

    /// <summary>
    /// A line per adapter host of a <c>_gorynych/status</c>: its adapter, build, whether stale, options,
    /// sessions and running turns; under a stale one, a line per session for what keeps it there.
    /// </summary>
    static IEnumerable<string> HostRows(JsonNode? status)
    {
        foreach (var agent in (status?["agents"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var x in (agent["hosts"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var current = x["current"] is JsonValue c && c.TryGetValue<bool>(out var now) && now;
                var sessions = (x["sessions"] as JsonArray ?? []).Select(s => (string?)s).OfType<string>().ToList();
                var options = string.Join(' ', (x["options"] as JsonArray ?? []).Select(o => (string?)o));
                var turns = (int?)x["turns"] ?? 0;
                var stopping = x["stopping"] is JsonValue s && s.TryGetValue<bool>(out var going) && going;
                yield return $"{(string?)agent["name"],-7} host {(string?)x["hostId"]} (process {(int?)x["processId"]}): " +
                    $"{(current ? "current" : "stale")}{(stopping ? ", stopping" : "")}, build {(string?)x["build"] ?? "(an earlier version)"}{(options.Length > 0 ? $" {options}" : "")}, " +
                    $"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")}{(sessions.Count > 0 ? $" ({string.Join(", ", sessions)})" : "")}, " +
                    $"{turns} turn{(turns == 1 ? "" : "s")} running";
                foreach (var (session, why) in x["waiting"] as JsonObject ?? [])
                    yield return $"          {session}: {(string?)why}";
            }
    }

    /// <summary>
    /// Connects to the running service, asks it with <paramref name="ask"/>, and waits up to
    /// <paramref name="waitSeconds"/> for its process to end: 0 when it stopped, 1 when no service runs
    /// on the pipe or it could not be asked, 2 when it was asked but still runs. An exit code from
    /// <paramref name="ask"/> ends it at once: the service was not asked to stop after all.
    /// </summary>
    static async Task<int> StopAsync(string command, string pipe, int waitSeconds, Func<JsonRpcConnection, int, Task<int?>> ask, string stopped = "")
    {
        NamedPipeClientStream? stream;
        try
        {
            stream = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"gorynych {command}: cannot reach the service on pipe {pipe}: {e.Message}{Hint(e)}");
            return 1;
        }
        if (stream is null)
        {
            Console.Error.WriteLine($"gorynych {command}: no service is running on pipe {pipe}");
            return 1;
        }
        int processId;
        await using (var rpc = new JsonRpcConnection(stream, stream, command))
        {
            rpc.Start();
            try
            {
                await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
                {
                    ProtocolVersion = AcpMethods.ProtocolVersion,
                    ClientInfo = new Implementation { Name = "gorynych-" + command, Version = AgentHost.Version },
                });
                processId = (int?)(await rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject()))?["processId"] ?? 0;
                if (await ask(rpc, processId) is { } code) return code;
            }
            catch (Exception e) when (e is IOException or JsonRpcException)
            {
                Console.Error.WriteLine($"gorynych {command}: the service on pipe {pipe} did not take the request: {e.Message}");
                return 1;
            }
        }
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(waitSeconds));
        }
        catch (ArgumentException) { } // already gone
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"gorynych {command}: the service (process {processId}) still runs after {waitSeconds} s");
            return 2;
        }
        Console.Error.WriteLine($"gorynych {command}: the service has stopped{stopped}");
        return 0;
    }

    /// <summary>
    /// Starts the service, as <see cref="ServiceLaunch.Decide"/> says, and waits for its pipe: with
    /// the logon task when there is one, else as a detached process, but never from an ssh login.
    /// </summary>
    static async Task<NamedPipeClientStream> StartAndConnectAsync(string pipe, IReadOnlyList<string> serviceArgs)
    {
        const int wait = 20;
        switch (ServiceLaunch.Decide(LogonTask.Exists(pipe), ServiceLaunch.IsRemoteLogon()))
        {
            case ServiceLaunch.Way.Refuse:
                throw new NotStartedException(ServiceLaunch.Refusal(pipe, pipe == GorynychProtocol.DefaultPipeName));
            case ServiceLaunch.Way.RunTask:
                Console.Error.WriteLine($"gorynych acp: starting the service with the logon task {LogonTask.Name(pipe)}" +
                    (serviceArgs.Count > 0 ? "; the task's options apply, not these" : ""));
                if (LogonTask.Run(pipe) is { } failure)
                    throw new InvalidOperationException($"the logon task {LogonTask.Name(pipe)} did not start: {failure}");
                return await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(wait)) ?? throw new TimeoutException(ServiceLaunch.TaskTimeout(pipe, wait));
            default:
                // A service would refuse this data folder and exit: say why here, rather than wait for its pipe.
                if (serviceArgs.ToList().IndexOf("--data") is var data and >= 0 && data + 1 < serviceArgs.Count
                    && DataFolder.Refusal(serviceArgs[data + 1]) is { } refused)
                    throw new NotStartedException(refused);
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
                List<string> args = ["--pipe", pipe, .. serviceArgs];
                Console.Error.WriteLine($"gorynych acp: starting the service ({exe} {string.Join(' ', args)})");
                if (!DetachedProcess.Start(exe, args))
                    Console.Error.WriteLine("gorynych acp: the client's job object forbids breaking away, so the service stays in it; " +
                        "closing the client may stop the service and every session");
                return await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(wait)) ?? throw new TimeoutException("the service did not open its pipe");
        }
    }

    /// <summary>The bridge started no service, on purpose; the message says why.</summary>
    sealed class NotStartedException(string message) : Exception(message);
}
