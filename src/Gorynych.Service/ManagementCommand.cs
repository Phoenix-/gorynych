using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Management;
using Gorynych.Outside;

namespace Gorynych.Service;

/// <summary>
/// <c>stop</c> and <c>start</c>: safe, idempotent management of adapter availability. The service does
/// the unsafe part (maintenance, blocker checks, unload, host stop) atomically; this command only
/// waits, reports and, for <c>start --all</c>, starts the service again.
/// </summary>
internal static class ManagementCommand
{
    /// <summary>The exit code of a stop called off because work still ran.</summary>
    public const int BusyExitCode = 4;

    /// <summary>The exit code when the service or an owned host was asked to stop but still runs.</summary>
    public const int StillRunningExitCode = 2;

    public static async Task<int> StopAsync(CommandLine line)
    {
        var pipe = line.Value("--pipe") ?? GorynychProtocol.DefaultPipeName;
        var directory = DataDirectory(line);
        var all = line.Flag("--all");
        var wait = line.Flag("--wait");
        var timeout = line.Value("--timeout") is { } text
            ? CommandLine.DurationOf(text) ?? Conductor.DefaultRestartTimeout
            : Conductor.DefaultRestartTimeout;
        var agents = line.Values("--agent").Distinct(StringComparer.Ordinal).ToArray();

        NamedPipeClientStream? stream;
        try
        {
            stream = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"gorynych stop: cannot reach the service on pipe {pipe}: {e.Message}{AcpBridge.Hint(e)}");
            return 1;
        }
        if (stream is null)
        {
            // No service runs: remember the stop so the next one starts in the state the person asked for.
            var state = ManagementState.Read(directory);
            state = all ? state.WithStopped(AdapterNames, all: true) : state.WithStopped(agents);
            try { ManagementState.Write(directory, state); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"gorynych stop: cannot save the stopped state in {directory}: {e.Message}");
                return 1;
            }
            Console.WriteLine(all
                ? $"gorynych stop: no service is running on pipe {pipe}; recorded that everything is stopped. `start --all` clears it."
                : $"gorynych stop: no service is running on pipe {pipe}; recorded {string.Join(", ", agents)} stopped. `start --agent ...` clears it.");
            return 0;
        }

        await using var rpc = new JsonRpcConnection(stream, stream, "gorynych-stop");
        rpc.Start();
        JsonNode? status;
        JsonNode? result;
        // Everything owned by the service now: its host processes and their descendants (the
        // harnesses, their MCP servers). Snapshotted before the stop, while they still have their
        // parent links; an external harness is not a descendant and is never waited for or touched.
        var ownedBefore = new List<(int Pid, DateTime? Started)>();
        try
        {
            await InitializeAsync(rpc, "gorynych-stop");
            status = await rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject());
            var snapshot = ProcessInspector.Snapshot();
            var roots = new List<int>();
            if (all && (int?)status?["processId"] is { } servicePid) roots.Add(servicePid);
            foreach (var agent in (status?["agents"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (!all && !agents.Contains((string?)agent["name"], StringComparer.Ordinal)) continue;
                foreach (var host in (agent["hosts"] as JsonArray ?? []).OfType<JsonObject>())
                    if ((int?)host["processId"] is { } hostPid) roots.Add(hostPid);
            }
            if (!all && (bool?)status?["hosts"] != true && (int?)status?["processId"] is { } inProcessPid)
                roots.AddRange(snapshot.Where(p => p.ParentPid == inProcessPid
                    && agents.Contains(Path.GetFileNameWithoutExtension(p.Name), StringComparer.OrdinalIgnoreCase)).Select(p => p.Pid));
            ownedBefore = Descendants(roots, snapshot);
            var request = new JsonObject { ["all"] = all, ["wait"] = wait, ["timeoutSeconds"] = timeout.TotalSeconds };
            if (!all) request["agents"] = new JsonArray(agents.Select(a => (JsonNode)a).ToArray());
            result = await rpc.SendRequestAsync(GorynychProtocol.MethodStop, request);
        }
        catch (JsonRpcException e) when (e.Code == JsonRpcException.MethodNotFound)
        {
            Console.Error.WriteLine("gorynych stop: the running service is an older version without safe stop. " +
                "Restart it onto this build (`Gorynych.Service restart`) and try again; `shutdown` is not used because it cancels turns.");
            return 1;
        }
        catch (Exception e) when (e is IOException or JsonRpcException)
        {
            Console.Error.WriteLine($"gorynych stop: the service on pipe {pipe} did not take the request: {e.Message}");
            return 1;
        }

        if (result?["stopped"] is not JsonValue flag || !flag.TryGetValue<bool>(out var stopped) || !stopped)
        {
            var blockers = result?["blockers"] as JsonArray ?? [];
            Console.Error.WriteLine($"gorynych stop: {(string?)result?["reason"] ?? "active work would be interrupted"}; nothing was stopped or cancelled.");
            foreach (var blocker in blockers.OfType<JsonObject>()) PrintBlocker(blocker);
            if (blockers.Count > 0)
                Console.Error.WriteLine(wait
                    ? "gorynych stop: run it again once that work ends, or give it a longer --timeout"
                    : "gorynych stop: it refuses by default while work is active; add --wait to wait for it, or --timeout <duration>");
            return BusyExitCode;
        }

        var unloaded = (result["unloadedSessions"] as JsonArray ?? []).Count;
        var hosts = (result["hosts"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        Console.WriteLine(all
            ? $"gorynych stop: stopped all adapters; {unloaded} idle session(s) unloaded, {hosts.Count} host(s) asked to stop. The service exits."
            : $"gorynych stop: stopped {string.Join(", ", (result["agents"] as JsonArray ?? []).Select(a => (string?)a).OfType<string>())}; "
              + $"{unloaded} idle session(s) unloaded, {hosts.Count} host(s) asked to stop.");
        foreach (var row in hosts)
            Console.WriteLine($"  {(string?)row["agent"],-7} host {(string?)row["hostId"]} process {(int?)row["processId"]}");

        // Wait for the service itself, then for the hosts and harness processes it owned. A PID is
        // trusted only with the start time the service saw, so a reused PID is not mistaken for one.
        var processId = (int?)status?["processId"] ?? 0;
        var startedAt = ParseTime((string?)status?["processStartedAt"]);
        var left = timeout;
        var clock = Stopwatch.StartNew();
        if (all && !await WaitForExitAsync(processId, startedAt, left))
        {
            Console.Error.WriteLine($"gorynych stop: the service (process {processId}) still runs after {timeout.TotalSeconds:0.#} s");
            return StillRunningExitCode;
        }
        left -= clock.Elapsed;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        var deadline = DateTime.UtcNow + left;
        var lingering = new List<JsonObject>();
        foreach (var row in hosts)
        {
            var hostPid = (int?)row["processId"] ?? 0;
            var hostStarted = ParseTime((string?)row["processStartedAt"]);
            if (!await WaitForExitAsync(hostPid, hostStarted, Remaining(deadline))) lingering.Add(row);
        }
        // A host may exit before a harness child it owned: wait for every descendant seen before the
        // stop too, so stop --all means the service and what it ran have exited, not just the host.
        var stillOwned = new List<(int Pid, DateTime? Started)>();
        foreach (var (pid, started) in ownedBefore)
        {
            if (hosts.Any(h => (int?)h["processId"] == pid) || pid == processId) continue;
            if (!await WaitForExitAsync(pid, started, Remaining(deadline))) stillOwned.Add((pid, started));
        }
        if (lingering.Count > 0 || stillOwned.Count > 0)
        {
            Console.Error.WriteLine($"gorynych stop: the service stopped, but {lingering.Count + stillOwned.Count} owned process(es) still run after the wait:");
            foreach (var row in lingering)
                Console.Error.WriteLine($"  {(string?)row["agent"],-7} host {(string?)row["hostId"]} process {(int?)row["processId"]}");
            foreach (var (pid, _) in stillOwned)
                Console.Error.WriteLine($"  owned descendant process {pid}");
            Console.Error.WriteLine("gorynych stop: nothing was killed. Check the host logs; `status --tree` shows their processes.");
            return StillRunningExitCode;
        }
        Console.WriteLine(all
            ? "gorynych stop: the service and its owned hosts have exited. `start --all` enables them again."
            : "gorynych stop: the selected adapter hosts and their observed harness processes have exited.");
        return 0;
    }

    static readonly string[] AdapterNames = CommandLine.AdapterNames;

    public static async Task<int> StartAsync(CommandLine line)
    {
        var pipe = line.Value("--pipe") ?? GorynychProtocol.DefaultPipeName;
        var directory = DataDirectory(line);
        var all = line.Flag("--all");
        var agents = line.Values("--agent").Distinct(StringComparer.Ordinal).ToArray();

        NamedPipeClientStream? stream;
        try
        {
            stream = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"gorynych start: cannot reach the service on pipe {pipe}: {e.Message}{AcpBridge.Hint(e)}");
            return 1;
        }
        if (stream is null)
        {
            if (!all)
            {
                // Nothing runs; clear the marker so the next service starts with these agents available.
                var state = ManagementState.Read(directory);
                if (state.StoppedAll)
                    state = new ManagementState(false,
                        new HashSet<string>(CommandLine.AdapterNames.Except(agents, StringComparer.Ordinal), StringComparer.Ordinal),
                        DateTimeOffset.UtcNow);
                else state = state.WithStarted(agents);
                try
                {
                    if (state.Agents.Count > 0) ManagementState.Write(directory, state);
                    else ManagementState.Clear(directory);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"gorynych start: cannot save the state in {directory}: {e.Message}");
                    return 1;
                }
                Console.WriteLine($"gorynych start: no service is running on pipe {pipe}; {string.Join(", ", agents)} will be available when it starts.");
                return 0;
            }
            Console.WriteLine($"gorynych start: no service is running on pipe {pipe}; starting it.");
            if (!await StartServiceAsync(pipe, directory, extraArguments: ServiceOptions.Read(directory)))
            {
                Console.Error.WriteLine("gorynych start: the service did not open its pipe within 20 s. Check the service log; the stopped-all marker was left in place.");
                return 1;
            }
            stream = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(5));
            if (stream is null)
            {
                Console.Error.WriteLine("gorynych start: the new service did not answer on its pipe; the stopped-all marker was left in place.");
                return 1;
            }
        }

        await using var rpc = new JsonRpcConnection(stream, stream, "gorynych-start");
        rpc.Start();
        try
        {
            await InitializeAsync(rpc, "gorynych-start");
            var request = new JsonObject { ["all"] = all };
            if (!all) request["agents"] = new JsonArray(agents.Select(a => (JsonNode)a).ToArray());
            var result = await rpc.SendRequestAsync(GorynychProtocol.MethodStart, request);
            if (result?["started"] is not JsonValue v || !v.TryGetValue<bool>(out var started) || !started)
            {
                Console.Error.WriteLine("gorynych start: the service did not restore the adapters");
                return 1;
            }
        }
        catch (JsonRpcException e) when (e.Code == JsonRpcException.MethodNotFound)
        {
            Console.Error.WriteLine("gorynych start: the running service is an older version without management start; restart it onto this build.");
            return 1;
        }
        catch (Exception e) when (e is IOException or JsonRpcException)
        {
            Console.Error.WriteLine($"gorynych start: the service on pipe {pipe} did not take the request: {e.Message}");
            return 1;
        }
        Console.WriteLine(all
            ? "gorynych start: all adapters are available again; a stopped-all marker is cleared."
            : $"gorynych start: {string.Join(", ", agents)} available again.");
        return 0;
    }

    internal static async Task InitializeAsync(JsonRpcConnection rpc, string name) =>
        await rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject
        {
            ["protocolVersion"] = AcpMethods.ProtocolVersion,
            ["clientInfo"] = new JsonObject { ["name"] = name, ["version"] = AgentHost.Version },
        });

    internal static string DataDirectory(CommandLine line) =>
        line.Value("--data") is { } data ? Path.GetFullPath(data) : ManagementState.DefaultDirectory();

    static void PrintBlocker(JsonObject blocker)
    {
        var reasons = string.Join("; ", (blocker["reasons"] as JsonArray ?? []).Select(r => (string?)r).OfType<string>());
        Console.Error.WriteLine($"  {(string?)blocker["agent"],-7} {(string?)blocker["sessionId"] ?? "(no session)"}  " +
            $"{(string?)blocker["title"] ?? "(untitled)"}: {reasons}");
    }

    static DateTime? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.UtcDateTime : null;

    static TimeSpan Remaining(DateTime deadline)
    {
        var left = deadline - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>The named processes and all their descendants, as one snapshot saw them.</summary>
    static List<(int Pid, DateTime? Started)> Descendants(IEnumerable<int> roots, IReadOnlyList<ProcessSnapshot> snapshot)
    {
        var byPid = snapshot.ToDictionary(p => p.Pid);
        var byParent = snapshot.GroupBy(p => p.ParentPid).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<(int, DateTime?)>();
        var seen = new HashSet<int>();
        var queue = new Queue<int>(roots);
        while (queue.Count > 0 && result.Count < 2000)
        {
            var pid = queue.Dequeue();
            if (!seen.Add(pid)) continue;
            byPid.TryGetValue(pid, out var process);
            result.Add((pid, process?.StartedAt));
            if (!byParent.TryGetValue(pid, out var children)) continue;
            foreach (var child in children) queue.Enqueue(child.Pid);
        }
        return result;
    }

    /// <summary>
    /// Waits for a process to exit, trusting its ID only if its start time is the one recorded: a PID
    /// the system reused for another process is not the process that was stopped.
    /// </summary>
    internal static async Task<bool> WaitForExitAsync(int processId, DateTime? startedAt, TimeSpan timeout)
    {
        if (processId <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (startedAt is { } expected && process.StartTime.ToUniversalTime() is var actual
                && Math.Abs((actual - expected).TotalSeconds) > 2)
                return true; // the PID belongs to someone else now
            if (process.HasExited) return true;
            if (timeout <= TimeSpan.Zero) return false;
            await process.WaitForExitAsync().WaitAsync(timeout);
            return true;
        }
        catch (ArgumentException) { return true; } // already gone
        catch (InvalidOperationException) { return true; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Starts a service executable detached, with the pipe and data folder, and waits for its pipe.</summary>
    internal static async Task<bool> StartServiceAsync(string pipe, string dataDirectory, string? executable = null, TimeSpan? wait = null,
        IReadOnlyList<string>? extraArguments = null)
    {
        var exe = executable ?? Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        List<string> args = ["--pipe", pipe, "--data", dataDirectory];
        if (extraArguments is not null) args.AddRange(extraArguments);
        Console.Error.WriteLine($"gorynych start: starting {exe} {string.Join(' ', args.Select(DetachedProcess.Quote))}");
        if (!DetachedProcess.Start(exe, args))
            Console.Error.WriteLine("gorynych start: the process cannot break away from its job; it stays in it");
        var stream = await ServicePipe.ConnectAsync(pipe, wait ?? TimeSpan.FromSeconds(20));
        if (stream is null) return false;
        await stream.DisposeAsync();
        return true;
    }
}
