using System.Text.Json.Nodes;

namespace Gorynych.Adapters.Dsh;

/// <summary>A tool call DSH announced: the session, its ID, DSH's tool name and the call's arguments.</summary>
internal sealed record DshTool(string SessionId, string ToolCallId, string Name, JsonNode? Input, string? Cwd);

/// <summary>
/// What DSH's ACP leaves out of a tool call and the proxy adds: an ACP kind, a readable title,
/// locations and diffs, all from DSH's tool name and arguments (docs/adapters.md, "DeepSeek Harness").
/// </summary>
internal static class DshMap
{
    /// <summary>DSH's file tools, the only ones whose sandbox escalation reaches the person.</summary>
    static readonly HashSet<string> FileTools = ["write", "edit"];

    /// <summary>DSH's shell tools: their escalation is always refused, so the workspace's ACL never changes.</summary>
    static readonly HashSet<string> ShellTools = ["pwsh", "bash"];

    public static string KindOf(string name) => name switch
    {
        "pwsh" or "bash" or "job_kill" => "execute",
        "read" or "read_image" or "read_mcp_resource" or "job_output" => "read",
        "write" or "edit" => "edit",
        "glob" or "grep" or "list_mcp_resources" or "list_mcp_resource_templates" or "web_search" => "search",
        "web_fetch" => "fetch",
        "todo_write" or "create_goal" or "get_goal" or "update_goal" => "think",
        _ => "other",
    };

    /// <summary>
    /// A title a person can read: a shell call by its description (the command stays in
    /// <c>rawInput</c>), a file tool with its path, a search with its pattern. MCP tools keep their
    /// <c>mcp__server__tool</c> name, which the service's pre-allow and <c>read_session</c> read.
    /// </summary>
    public static string TitleOf(string name, JsonNode? input)
    {
        if (name.StartsWith("mcp__", StringComparison.Ordinal)) return name;
        var arg = name switch
        {
            "pwsh" or "bash" => Str(input, "description") ?? Str(input, "command"),
            "read" or "read_image" or "write" or "edit" => Str(input, "file_path") ?? Str(input, "path"),
            "glob" or "grep" => Str(input, "pattern"),
            "web_fetch" => Str(input, "url"),
            "web_search" => Str(input, "query"),
            _ => null,
        };
        if (arg is null) return name;
        return name is "pwsh" or "bash" ? arg : $"{name} {arg}";
    }

    /// <summary>The files a call touches, as ACP locations with absolute paths; a shell call's folder.</summary>
    public static JsonArray? LocationsOf(string name, JsonNode? input, string? cwd)
    {
        var path = name is "pwsh" or "bash"
            ? Str(input, "workdir") ?? cwd
            : Str(input, "file_path") ?? Str(input, "path");
        if (path is null || Resolve(path, cwd) is not { } full) return null;
        return new JsonArray(new JsonObject { ["path"] = full });
    }

    /// <summary>ACP diff content for a file write or edit, from the call's arguments.</summary>
    public static JsonObject? DiffOf(string name, JsonNode? input, string? cwd)
    {
        if (Str(input, "file_path") is not { } path || Resolve(path, cwd) is not { } full) return null;
        return name switch
        {
            "write" when Str(input, "content") is { } content =>
                new JsonObject { ["type"] = "diff", ["path"] = full, ["oldText"] = null, ["newText"] = content },
            "edit" when Str(input, "old_string") is { } old && Str(input, "new_string") is { } now =>
                new JsonObject { ["type"] = "diff", ["path"] = full, ["oldText"] = old, ["newText"] = now },
            _ => null,
        };
    }

    /// <summary>
    /// Whether the proxy refuses a permission request itself instead of asking the person. Every
    /// request DSH sends is a sandbox escalation; a shell command's would write a standing ACE and a
    /// Low integrity label onto the workspace tree, so it is always refused. A file tool's goes to
    /// the person. A request for a call the proxy did not see, or another tool's escalation, is
    /// refused too, so no escalation it cannot attribute changes an ACL.
    /// </summary>
    public static bool Refused(DshTool? tool) =>
        tool is null
        || ShellTools.Contains(tool.Name)
        || (!FileTools.Contains(tool.Name) && tool.Input?["sandbox_permissions"] is not null);

    /// <summary>What an escalation asks for, for the person: the wider mode and the model's justification.</summary>
    public static string? Escalation(JsonNode? input) =>
        Str(input, "sandbox_permissions") is { } mode
            ? $"Asks to run this once with {mode} permissions" + (Str(input, "justification") is { } why ? $": {why}" : ".")
            : null;

    static string? Str(JsonNode? input, string key) =>
        input is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>An absolute path: a relative one is taken from the session's folder, as DSH takes it.</summary>
    static string? Resolve(string path, string? cwd)
    {
        try
        {
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            return cwd is null ? null : Path.GetFullPath(Path.Combine(cwd, path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
