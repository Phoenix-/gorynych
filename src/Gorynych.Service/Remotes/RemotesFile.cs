using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service.Remotes;

/// <summary>
/// A remote service: the machine's name, as the hub shows it (<c>claude@&lt;name&gt;</c>), and the command
/// that reaches the bridge there, in the shape of ACP's <c>McpServerStdio</c> (<c>name</c>, <c>command</c>,
/// <c>args</c>, <c>env</c>), for example <c>ssh -T r-pc-167 Gorynych.Service.exe acp</c>.
/// </summary>
internal sealed record RemoteEntry(string Name, string Command, IReadOnlyList<string> Args, IReadOnlyList<KeyValuePair<string, string>> Env)
{
    /// <summary>The command line, as a person would type it.</summary>
    public string Shown => string.Join(' ', new[] { Command }.Concat(Args).Select(Gorynych.Outside.DetachedProcess.Quote));

    /// <summary>Whether two entries start the same thing.</summary>
    public bool SameLaunch(RemoteEntry other) =>
        Command == other.Command && Args.SequenceEqual(other.Args) && Env.SequenceEqual(other.Env);
}

/// <summary>
/// <c>remotes.json</c> in the data folder: the remote services this one shows as its own agents
/// (docs/service-protocol.md, "Remotes"). <c>{"remotes": [{name, command, args, env: [{name, value}]}]}</c>.
/// The <c>remote</c> command writes it; a running service reads it again when it changes.
/// </summary>
internal static class RemotesFile
{
    public const string FileName = "remotes.json";

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>The entries; none for a missing file.</summary>
    /// <exception cref="FormatException">The file is not one this service can read, with why.</exception>
    public static List<RemoteEntry> Read(string path)
    {
        if (!File.Exists(path)) return [];
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException e) { throw new FormatException($"{path} is not JSON: {e.Message}"); }
        var list = new List<RemoteEntry>();
        foreach (var node in root?["remotes"] as JsonArray ?? [])
        {
            if (node is not JsonObject o || Text(o["name"]) is not { } name || Text(o["command"]) is not { } command)
                throw new FormatException($"{path}: every remote needs a name and a command");
            if (NameProblem(name) is { } problem) throw new FormatException($"{path}: {problem}");
            if (list.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException($"{path}: the machine '{name}' is there twice");
            var args = (o["args"] as JsonArray ?? []).Select(a => Text(a) ?? throw new FormatException($"{path}: '{name}': args are strings")).ToList();
            var env = (o["env"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(v => new KeyValuePair<string, string>(Text(v["name"]) ?? "", Text(v["value"]) ?? "")).Where(v => v.Key.Length > 0).ToList();
            list.Add(new RemoteEntry(name, command, args, env));
        }
        return list;
    }

    public static void Write(string path, IEnumerable<RemoteEntry> entries)
    {
        var remotes = new JsonArray();
        foreach (var e in entries)
            remotes.Add((JsonNode)new JsonObject
            {
                ["name"] = e.Name,
                ["command"] = e.Command,
                ["args"] = new JsonArray(e.Args.Select(a => (JsonNode)a).ToArray()),
                ["env"] = new JsonArray(e.Env.Select(v => (JsonNode)new JsonObject { ["name"] = v.Key, ["value"] = v.Value }).ToArray()),
            });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, new JsonObject { ["remotes"] = remotes }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>
    /// Why a machine's name will not do, or null. It is what follows the <c>@</c> of a remote agent's
    /// name (<c>claude@R-PC-167</c>), so it has no <c>@</c> and nothing that would read as more than one name.
    /// </summary>
    public static string? NameProblem(string name)
    {
        if (name.Length is 0 or > 64) return $"a machine's name has 1 to 64 characters: '{name}'";
        if (name.Contains('@')) return $"a machine's name has no @: it comes after the @ of its agents' names (claude@{name.Replace("@", "")}): '{name}'";
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') || name[0] is '-' or '.' or '_')
            return $"a machine's name has letters, digits, - _ and . only, and starts with a letter or a digit: '{name}'";
        return null;
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
