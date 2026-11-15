using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Management;
using Gorynych.Service.Hosts;

namespace Gorynych.Service;

/// <summary>
/// <c>status</c>: the running service, its adapter hosts, their harness processes and descendants,
/// with versions and builds as far as they are reliably known, and live sessions, turns and
/// blockers. It also separates Gorynych's own processes from harness processes it did not start
/// (external ones are informational and never stopped). Without a service it still reports the data
/// folder, the management marker and detectable external harnesses.
/// </summary>
internal static class StatusCommand
{
    public static async Task<int> RunAsync(CommandLine line)
    {
        var pipe = line.Value("--pipe") ?? GorynychProtocol.DefaultPipeName;
        var dataDirectory = ManagementCommand.DataDirectory(line);
        var tree = line.Flag("--tree");
        var json = line.Flag("--json");
        var status = await TryStatusAsync(pipe);
        if (status is null) Console.Error.WriteLine($"gorynych status: no service is running on pipe {pipe}");
        var snapshot = ProcessInspector.Snapshot();
        var report = Report(pipe, dataDirectory, status, snapshot);
        if (json)
        {
            Console.WriteLine(report["json"]!.ToJsonString(AcpJson.IndentedTextOptions));
            return status is null ? 1 : 0;
        }
        if (tree)
        {
            PrintTree(report, pipe, status);
        }
        else
        {
            PrintDefault(report, pipe, status, dataDirectory);
        }
        return status is null ? 1 : 0;
    }

    static async Task<JsonObject?> TryStatusAsync(string pipe)
    {
        NamedPipeClientStream? stream;
        try { stream = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(1)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (stream is null) return null;
        await using var rpc = new JsonRpcConnection(stream, stream, "gorynych-status");
        rpc.Start();
        try
        {
            await ManagementCommand.InitializeAsync(rpc, "gorynych-status");
            return await rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject()) as JsonObject;
        }
        catch (Exception e) when (e is IOException or JsonRpcException)
        {
            return null;
        }
    }

    sealed class Item
    {
        public required JsonObject Json { get; init; }
        public List<Item> Children { get; } = [];
    }

    static JsonObject Report(string pipe, string dataDirectory, JsonObject? status, IReadOnlyList<ProcessSnapshot> snapshot)
    {
        var byPid = snapshot.ToDictionary(p => p.Pid);
        var service = status is null ? null : ServiceReport(status, byPid, snapshot);
        var external = ExternalReport(snapshot, status);
        var marker = ManagementState.Read(dataDirectory);
        var maintenance = status?["maintenance"]?.DeepClone() as JsonObject ?? new JsonObject
        {
            ["stoppedAll"] = marker.StoppedAll,
            ["stoppedAgents"] = new JsonArray(marker.Agents.Order(StringComparer.Ordinal).Select(a => (JsonNode)a).ToArray()),
            ["stoppingAgents"] = new JsonArray(),
            ["stopInProgress"] = false,
        };
        var json = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["pipe"] = pipe,
            ["dataDirectory"] = dataDirectory,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["service"] = service?.DeepClone(),
            ["maintenance"] = maintenance.DeepClone(),
            ["maintenanceMarker"] = new JsonObject
            {
                ["stoppedAll"] = marker.StoppedAll,
                ["stoppedAgents"] = new JsonArray(marker.Agents.Order(StringComparer.Ordinal).Select(a => (JsonNode)a).ToArray()),
                ["updatedAt"] = marker.UpdatedAt == DateTimeOffset.MinValue ? null : marker.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            },
            ["external"] = external.DeepClone(),
        };
        return new JsonObject
        {
            ["json"] = json,
            ["service"] = service?.DeepClone(),
            ["external"] = external.DeepClone(),
            ["marker"] = maintenance.DeepClone(),
        };
    }

    static JsonObject? ServiceReport(JsonObject status, IReadOnlyDictionary<int, ProcessSnapshot> byPid, IReadOnlyList<ProcessSnapshot> snapshot)
    {
        var pid = (int?)status["processId"] ?? 0;
        var path = (string?)status["processPath"];
        var started = Parse((string?)status["processStartedAt"]);
        var process = ProcessReport(pid, started, path, byPid, snapshot);
        var run = HostGeneration.OnDisk(path);
        var agents = status["agents"] as JsonArray ?? [];
        // Older services had no details; a service before that change put the count in "sessions".
        var sessions = status["sessionDetails"] as JsonArray ?? (status["sessions"] as JsonArray) ?? [];
        var serviceNode = new JsonObject
        {
            ["pid"] = pid,
            ["path"] = path,
            ["startedAt"] = started?.ToString("O", CultureInfo.InvariantCulture),
            ["version"] = status["version"]?.DeepClone(),
            ["build"] = status["build"]?.DeepClone(),
            ["diskVersion"] = run.Version,
            ["diskBuild"] = run.Build,
            ["diskUnread"] = run.Unread,
            ["hosts"] = status["hosts"]?.DeepClone() ?? false,
            ["clients"] = status["clients"]?.DeepClone() ?? 0,
            ["sessions"] = status["sessions"]?.DeepClone() ?? 0,
            ["sessionDetails"] = sessions.DeepClone(),
            ["maintenance"] = status["maintenance"]?.DeepClone() ?? new JsonObject(),
            ["tree"] = new JsonArray(),
        };
        var adapters = new JsonArray();
        foreach (var agent in agents.OfType<JsonObject>())
        {
            var name = (string?)agent["name"];
            var held = (agent["hosts"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var adapter = new JsonObject
            {
                ["name"] = name,
                ["running"] = agent["running"]?.DeepClone() ?? false,
                ["generation"] = agent["generation"]?.DeepClone(),
                ["hosts"] = new JsonArray(),
                ["tree"] = new JsonArray(),
            };
            foreach (var host in held)
            {
                var hostPid = (int?)host["processId"] ?? 0;
                var hostStarted = Parse((string?)host["processStartedAt"]);
                var hostPath = byPid.TryGetValue(hostPid, out var entry) ? entry.Path : null;
                var hostRun = HostGeneration.OnDisk(hostPath);
                var hostSessions = host["sessions"] as JsonArray ?? [];
                var blockers = sessions.OfType<JsonObject>()
                    .Where(s => (string?)s["agent"] == name && hostSessions.Any(x => (string?)x == (string?)s["sessionId"]))
                    .SelectMany(s => (s["blockers"] as JsonArray ?? []).Select(b => b!))
                    .Distinct().ToArray();
                var node = new JsonObject
                {
                    ["hostId"] = host["hostId"]?.DeepClone(),
                    ["slot"] = host["slot"]?.DeepClone(),
                    ["current"] = host["current"]?.DeepClone() ?? false,
                    ["build"] = host["build"]?.DeepClone(),
                    ["options"] = host["options"]?.DeepClone() ?? new JsonArray(),
                    ["pid"] = hostPid,
                    ["path"] = hostPath,
                    ["startedAt"] = hostStarted?.ToString("O", CultureInfo.InvariantCulture),
                    ["diskVersion"] = hostRun.Version,
                    ["diskBuild"] = hostRun.Build,
                    ["sessions"] = hostSessions.DeepClone(),
                    ["turns"] = host["turns"]?.DeepClone() ?? 0,
                    ["blockers"] = new JsonArray(blockers),
                    ["children"] = new JsonArray(),
                };
                if (hostPid > 0 && byPid.TryGetValue(hostPid, out var live) && hostStarted is { } expected
                    && live.StartedAt is { } actual && Math.Abs((actual - expected).TotalSeconds) > 2)
                    node["pidReused"] = true;
                var harness = ChildrenTree(hostPid, byPid, snapshot, name, 0);
                node["children"] = harness;
                ((JsonArray)adapter["hosts"]!).Add((JsonNode)node);
                ((JsonArray)adapter["tree"]!).Add((JsonNode)node.DeepClone());
                ((JsonArray)serviceNode["tree"]!).Add((JsonNode)new JsonObject
                {
                    ["role"] = "adapter", ["name"] = name, ["host"] = node.DeepClone(),
                });
            }
            if (held.Count == 0)
            {
                // Without hosts the adapters run in the service: its children are where their
                // harnesses and descendants are, and the tree names them under each adapter.
                var children = ChildrenTree(pid, byPid, snapshot, name, 0);
                adapter["tree"] = children.DeepClone();
                ((JsonArray)serviceNode["tree"]!).Add((JsonNode)new JsonObject
                {
                    ["role"] = "adapter", ["name"] = name, ["inProcess"] = true, ["children"] = children.DeepClone(),
                });
            }
            adapters.Add((JsonNode)adapter);
        }
        serviceNode["adapters"] = adapters;
        serviceNode["process"] = process;
        return serviceNode;
    }

    static JsonObject ProcessReport(int pid, DateTime? expected, string? path, IReadOnlyDictionary<int, ProcessSnapshot> byPid,
        IReadOnlyList<ProcessSnapshot> snapshot)
    {
        var row = new JsonObject { ["pid"] = pid, ["path"] = path };
        if (!byPid.TryGetValue(pid, out var found))
        {
            row["running"] = false;
            return row;
        }
        row["running"] = true;
        row["path"] = path ?? found.Path;
        row["name"] = found.Name;
        row["startedAt"] = found.StartedAt?.ToString("O", CultureInfo.InvariantCulture);
        if (expected is { } expectedTime && found.StartedAt is { } actual && Math.Abs((actual - expectedTime).TotalSeconds) > 2)
            row["pidReused"] = true;
        return row;
    }

    /// <summary>The descendants of a host or of the service, with the harness child named where it can be.</summary>
    static JsonArray ChildrenTree(int rootPid, IReadOnlyDictionary<int, ProcessSnapshot> byPid,
        IReadOnlyList<ProcessSnapshot> snapshot, string? adapter, int depth)
    {
        var rows = new JsonArray();
        if (rootPid <= 0 || depth > 6) return rows;
        var children = snapshot.Where(p => p.ParentPid == rootPid).OrderBy(p => p.Pid).Take(64).ToList();
        foreach (var child in children)
        {
            var role = HarnessRole(child, adapter) ?? "descendant";
            var node = new JsonObject
            {
                ["pid"] = child.Pid,
                ["parentPid"] = child.ParentPid,
                ["name"] = child.Name,
                ["path"] = child.Path,
                ["startedAt"] = child.StartedAt?.ToString("O", CultureInfo.InvariantCulture),
                ["role"] = role,
                ["children"] = ChildrenTree(child.Pid, byPid, snapshot, adapter, depth + 1),
            };
            rows.Add((JsonNode)node);
        }
        return rows;
    }

    static string? HarnessRole(ProcessSnapshot process, string? adapter)
    {
        var name = Path.GetFileNameWithoutExtension(process.Name);
        var pathName = process.Path is null ? null : Path.GetFileNameWithoutExtension(process.Path);
        foreach (var candidate in new[] { name, pathName })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (adapter is not null && candidate.Equals(adapter, StringComparison.OrdinalIgnoreCase)) return "harness";
            if (candidate.Equals("claude", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals("codex", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals("kimi", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals("dsh", StringComparison.OrdinalIgnoreCase))
                return "harness";
            if (adapter is "kimi" or "dsh" && candidate.Equals("node", StringComparison.OrdinalIgnoreCase)) return "harness";
        }
        return null;
    }

    static JsonArray ExternalReport(IReadOnlyList<ProcessSnapshot> snapshot, JsonObject? status)
    {
        var owned = new HashSet<int>();
        if (status is not null)
        {
            if ((int?)status["processId"] is { } servicePid) owned.Add(servicePid);
            foreach (var agent in (status["agents"] as JsonArray ?? []).OfType<JsonObject>())
                foreach (var host in (agent["hosts"] as JsonArray ?? []).OfType<JsonObject>())
                    if ((int?)host["processId"] is { } hostPid) owned.Add(hostPid);
        }
        // Everything descending from an owned process is owned too, so an external harness is never
        // mistaken for one of Gorynych's and never stopped.
        var byParent = snapshot.GroupBy(p => p.ParentPid).ToDictionary(g => g.Key, g => g.ToList());
        var queue = new Queue<int>(owned);
        while (queue.Count > 0 && owned.Count < 1000)
        {
            var pid = queue.Dequeue();
            if (!byParent.TryGetValue(pid, out var children)) continue;
            foreach (var child in children)
                if (owned.Add(child.Pid)) queue.Enqueue(child.Pid);
        }
        var rows = new JsonArray();
        var left = 0;
        foreach (var process in snapshot.OrderBy(p => p.Pid))
        {
            if (owned.Contains(process.Pid) || !IsHarnessImage(process)) continue;
            if (rows.Count >= 20) { left++; continue; }
            rows.Add((JsonNode)new JsonObject
            {
                ["pid"] = process.Pid,
                ["name"] = process.Name,
                ["path"] = process.Path,
                ["startedAt"] = process.StartedAt?.ToString("O", CultureInfo.InvariantCulture),
                ["role"] = "external",
                ["note"] = "detected outside Gorynych; informational only, never stopped",
            });
        }
        if (left > 0)
            rows.Add((JsonNode)new JsonObject
            {
                ["role"] = "external",
                ["truncated"] = left,
                ["note"] = "more external harness processes are not listed; status is bounded",
            });
        return rows;
    }

    /// <summary>
    /// An image that is a harness's own executable, not a helper with a related name (Claude's
    /// Electron helpers, Codex's sandbox service or code-mode host): exact base names only.
    /// </summary>
    static bool IsHarnessImage(ProcessSnapshot process)
    {
        var name = Path.GetFileNameWithoutExtension(process.Path ?? process.Name);
        return name.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || name.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || name.Equals("kimi", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dsh", StringComparison.OrdinalIgnoreCase);
    }

    static DateTime? Parse(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime : null;

    // ---------------------------------------------------------------- text

    static void PrintDefault(JsonObject report, string pipe, JsonObject? status, string dataDirectory)
    {
        var svc = report["service"] as JsonObject;
        if (svc is null)
        {
            Console.WriteLine($"service: not running (pipe {pipe})");
            Console.WriteLine($"data: {dataDirectory}");
            var marker = report["marker"] as JsonObject;
            Console.WriteLine("maintenance: " + DescribeMarker(marker));
            PrintExternal(report);
            return;
        }
        Console.WriteLine($"service: process {svc["pid"]}, version {Text(svc["version"])}, build {Text(svc["build"])}");
        Console.WriteLine($"  path: {Text(svc["path"])}");
        Console.WriteLine($"  on disk: version {Text(svc["diskVersion"])}, build {Text(svc["diskBuild"])}"
            + (svc["diskUnread"] is JsonValue u && u.TryGetValue<string>(out var why) ? $" (unread: {why})" : ""));
        Console.WriteLine($"  {svc["clients"]} clients, {svc["sessions"]} sessions, {((bool?)svc["hosts"] == true ? "adapters in hosts" : "adapters in its own process")}");
        Console.WriteLine("maintenance: " + DescribeMarker(report["marker"] as JsonObject));
        if ((string?)status!["dataFolderOpen"] is { } open) Console.WriteLine($"warning: {open}");
        if (status["ahp"] is JsonObject ahp)
            Console.WriteLine((string?)ahp["error"] is { } off ? $"ahp: {off}"
                : $"ahp: the face listens on 127.0.0.1:{(int?)ahp["port"]}/ahp, {(int?)ahp["clients"] ?? 0} clients");
        foreach (var agent in (svc["adapters"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var hosts = agent["hosts"] as JsonArray ?? [];
            if (hosts.Count == 0)
            {
                Console.WriteLine($"  {(string?)agent["name"],-7} in-process, {((bool?)agent["running"] == true ? "running" : "not running")}, generation {Text(agent["generation"])}");
                continue;
            }
            foreach (var host in hosts.OfType<JsonObject>())
            {
                var current = (bool?)host["current"] == true;
                var runs = host["diskBuild"] is null ? "" : $", on disk {(string?)host["diskVersion"] ?? "unknown"} / {Text(host["diskBuild"])}";
                Console.WriteLine($"  {(string?)agent["name"],-7} host {(string?)host["hostId"]} (process {(int?)host["pid"]}): " +
                    $"{(current ? "current" : "stale")}{((host["pidReused"] is JsonValue r && r.TryGetValue<bool>(out var reused) && reused ? ", PID reused" : ""))}, " +
                    $"build {Text(host["build"])}{runs}, {(host["sessions"] as JsonArray ?? []).Count} session(s), {(int?)host["turns"] ?? 0} turn(s) running");
                if ((string?)host["path"] is { } path) Console.WriteLine($"          {path}");
                foreach (var blocker in host["blockers"] as JsonArray ?? [])
                    Console.WriteLine($"          blocker: {Text(blocker)}");
            }
        }
        PrintSessions(svc);
        PrintExternal(report);
    }

    static void PrintSessions(JsonObject svc)
    {
        var sessions = svc["sessionDetails"] as JsonArray ?? [];
        if (sessions.Count == 0) return;
        Console.WriteLine("live sessions:");
        foreach (var session in sessions.OfType<JsonObject>())
        {
            var blockers = session["blockers"] as JsonArray ?? [];
            Console.WriteLine($"  {(string?)session["agent"],-7} {(string?)session["sessionId"]}  {Text(session["title"])}" +
                $"  [{Text(session["state"])}{((blockers.Count > 0 ? ": " + string.Join("; ", blockers.Select(b => Text(b))) : ""))}]");
        }
    }

    static void PrintTree(JsonObject report, string pipe, JsonObject? status)
    {
        var svc = report["service"] as JsonObject;
        if (svc is null)
        {
            Console.WriteLine($"service: not running (pipe {pipe})");
            Console.WriteLine("maintenance: " + DescribeMarker(report["marker"] as JsonObject));
            Console.WriteLine("data: " + Text((report["json"] as JsonObject)?["dataDirectory"]));
            PrintExternal(report);
            return;
        }
        Console.WriteLine($"service {svc["pid"]}  version {Text(svc["version"])}  build {Text(svc["build"])}");
        Console.WriteLine($"  path {Text(svc["path"])}");
        Console.WriteLine($"  disk version {Text(svc["diskVersion"])}  disk build {Text(svc["diskBuild"])}");
        Console.WriteLine($"  clients {svc["clients"]}  sessions {svc["sessions"]}  maintenance {DescribeMarker(report["marker"] as JsonObject)}");
        foreach (var agent in (svc["adapters"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var hosts = agent["hosts"] as JsonArray ?? [];
            Console.WriteLine($"  adapter {(string?)agent["name"]}{((hosts.Count == 0 ? " (in-process)" : ""))}");
            if (hosts.Count == 0)
            {
                // An in-process adapter's harness is a child of the service; name it under the adapter
                // when the image matches, so the tree still shows where its work runs.
                var serviceTree = svc["process"] as JsonObject;
                foreach (var child in ChildrenOfReport(svc, (string?)agent["name"]))
                    PrintNode(child, 4);
                continue;
            }
            foreach (var host in hosts.OfType<JsonObject>())
            {
                Console.WriteLine($"    host {(string?)host["hostId"]}  pid {host["pid"]}  {((bool?)host["current"] == true ? "current" : "stale")}");
                Console.WriteLine($"      path {Text(host["path"])}  build {Text(host["build"])}  disk {Text(host["diskVersion"])}/{Text(host["diskBuild"])}");
                var sessions = (host["sessions"] as JsonArray ?? []).Select(s => (string?)s).OfType<string>().ToList();
                Console.WriteLine($"      sessions {sessions.Count}{(sessions.Count > 0 ? " (" + string.Join(", ", sessions.Take(8)) + (sessions.Count > 8 ? ", …" : "") + ")" : "")}  turns {host["turns"]}");
                foreach (var blocker in (host["blockers"] as JsonArray ?? []).OfType<JsonValue>()) Console.WriteLine($"      blocker {Text(blocker)}");
                foreach (var child in (host["children"] as JsonArray ?? []).OfType<JsonObject>()) PrintNode(child, 6);
            }
        }
        PrintExternal(report);
    }

    static IEnumerable<JsonObject> ChildrenOfReport(JsonObject svc, string? adapter)
    {
        if (svc["tree"] is not JsonArray tree) yield break;
        foreach (var item in tree.OfType<JsonObject>())
        {
            if ((string?)item["role"] != "adapter" || (string?)item["name"] != adapter) continue;
            var children = item["host"] is JsonObject host ? host["children"] as JsonArray : item["children"] as JsonArray;
            foreach (var child in children ?? []) if (child is JsonObject row) yield return row;
        }
    }

    static void PrintNode(JsonObject node, int indent)
    {
        var pad = new string(' ', indent);
        Console.WriteLine($"{pad}{(string?)node["role"],-10} {(string?)node["name"]} {node["pid"]}");
        if ((string?)node["path"] is { } path) Console.WriteLine($"{pad}           {path}");
        foreach (var child in node["children"] as JsonArray ?? []) if (child is JsonObject row) PrintNode(row, indent + 2);
    }

    static void PrintExternal(JsonObject report)
    {
        var external = report["external"] as JsonArray ?? [];
        if (external.Count == 0) return;
        Console.WriteLine("external harness processes (informational; never stopped by Gorynych):");
        foreach (var row in external.OfType<JsonObject>())
        {
            if (row["truncated"] is JsonValue) 
            {
                Console.WriteLine($"  … {row["truncated"]} more not listed (status is bounded)");
                continue;
            }
            Console.WriteLine($"  {(string?)row["name"]} {(int?)row["pid"]}  {(string?)row["path"] ?? "(path unknown)"}");
        }
    }

    static string Text(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<string>(out var text) ? text
        : v.TryGetValue<bool>(out var flag) ? (flag ? "true" : "false")
        : v.TryGetValue<int>(out var number) ? number.ToString(CultureInfo.InvariantCulture)
        : v.ToJsonString()
        : "(unknown)";

    static string DescribeMarker(JsonObject? marker)
    {
        if (marker is null) return "(unknown)";
        var all = (bool?)marker["stoppedAll"] == true;
        var agents = (marker["stoppedAgents"] as JsonArray ?? []).Select(a => (string?)a).OfType<string>().ToList();
        if (all) return "stopped all (bridges will not start the service until `start --all`)";
        return agents.Count == 0 ? "available" : "stopped: " + string.Join(", ", agents);
    }
}
