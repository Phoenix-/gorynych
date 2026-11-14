using System.Text;
using System.Text.Json.Nodes;

namespace Gorynych.Adapters.Codex;

/// <summary>
/// The bounded, isolated structured request a generated Codex title uses: the prompt, its JSON
/// schema, the config that turns off built-in tools and MCP servers, and the normalization of what
/// comes back (docs/adapters.md, "Codex", "Titles"). The generation itself runs in an ephemeral
/// thread of the adapter's existing app-server, never in the visible conversation.
/// </summary>
internal static class CodexTitleGeneration
{
    /// <summary>The thread's own instructions: untrusted transcript text is title input, never a task.</summary>
    public const string Instructions =
        "Generate a concise, single-line task title of at most 60 characters and under five words where possible. " +
        "Start with an imperative verb. Capitalize only the first word unless the user's language, proper nouns, acronyms " +
        "or code terms require otherwise. Preserve ticket references exactly. Write in the user's language. " +
        "Do not use quotes, markdown or trailing punctuation. Do not answer the request, run tools, read files or use the network.";

    /// <summary>
    /// The feature and tool switches that keep the temporary thread from exposing tools or MCP
    /// servers on the installed Codex. Keys are Codex 0.167's own feature names; unknown names are
    /// ignored by Codex, so the same list also covers the keys upstream disables under other names.
    /// </summary>
    static readonly string[] DisabledFeatures =
    [
        "apps", "code_mode", "code_mode_host", "code_mode_host_grpc", "code_mode_host_output_truncation",
        "code_mode_interrupt", "code_mode_only", "code_mode_prewarm", "code_mode_tool_search",
        "context_management", "current_time_reminder", "deferred_executor", "goals", "hooks", "image_generation",
        "memories", "multi_agent", "multi_agent_v2", "multi_agent_v2_dynamic_tools", "plugins", "remote_plugin",
        "request_permissions_tool", "shell_snapshot", "shell_tool", "sleep_tool", "standalone_web_search",
        "token_budget", "tool_suggest", "unified_exec", "unified_exec_tty", "view_image", "computer_use",
        "browser_use", "in_app_browser", "enable_mcp_apps", "artifact", "default_mode_request_user_input",
    ];

    /// <summary>The JSON schema that constrains the title turn's final message.</summary>
    public static JsonObject Schema(int maxChars) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = maxChars,
            },
        },
        ["required"] = new JsonArray("title"),
        ["additionalProperties"] = false,
    };

    /// <summary>The user half of the prompt: the first task, cut to <paramref name="maxBytes"/> without splitting a character.</summary>
    public static string Prompt(string input, int maxBytes)
    {
        var prefix = "User task:\n";
        var room = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(prefix));
        return prefix + Cut(input.Trim(), room);
    }

    /// <summary>
    /// The thread's config: every effective MCP server off, built-in tools off, no project docs,
    /// environment blocks, skills or collaboration tools. A server map that could not be read is
    /// not passed here: the caller fails closed instead.
    /// </summary>
    public static JsonObject Config(JsonObject mcpServers)
    {
        var features = new JsonObject();
        foreach (var name in DisabledFeatures) features[name] = false;
        return new JsonObject
        {
            ["features"] = features,
            ["agents"] = new JsonObject { ["enabled"] = false },
            ["tools"] = new JsonObject
            {
                ["web_search"] = false,
                ["update_plan"] = new JsonObject { ["enabled"] = false },
                ["experimental_request_user_input"] = new JsonObject { ["enabled"] = false },
            },
            ["web_search"] = "disabled",
            ["skills"] = new JsonObject { ["include_instructions"] = false },
            ["cloud"] = new JsonObject { ["skills"] = new JsonObject { ["enabled"] = false } },
            ["project_doc_max_bytes"] = 0,
            ["include_environment_context"] = false,
            ["include_apps_instructions"] = false,
            ["include_permissions_instructions"] = false,
            ["include_collaboration_mode_instructions"] = false,
            ["mcp_servers"] = mcpServers.DeepClone(),
        };
    }

    /// <summary>Normalizes a structured result: one line, no markdown or quotes, no trailing punctuation, bounded.</summary>
    public static string? Normalize(string? response, int maxChars)
    {
        if (response is null) return null;
        var text = response.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject json && (string?)json["title"] is { } title) text = title;
            }
            catch (System.Text.Json.JsonException)
            {
                // not the structured answer: treat it as text below
            }
        }
        text = text.Trim().Trim('"', '\'', '`', '“', '”', '‘', '’');
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        line = line.TrimEnd('.', '?', '!').Trim();
        if (line.Length == 0) return null;
        return line.Length <= maxChars ? line : Cut(line, maxChars);
    }

    /// <summary>At most <paramref name="maxBytes"/> UTF-8 bytes of <paramref name="text"/>, without splitting a character.</summary>
    public static string Cut(string text, int maxBytes)
    {
        if (maxBytes <= 0) return "";
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        var sb = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (used + size > maxBytes) break;
            sb.Append(rune.ToString());
            used += size;
        }
        return sb.ToString();
    }
}
