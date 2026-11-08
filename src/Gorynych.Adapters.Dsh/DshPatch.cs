using System.Text;

namespace Gorynych.Adapters.Dsh;

/// <summary>
/// The per-launch <c>--patch</c> overlay Gorynych gives <c>dsh --profile acp</c>: the appended
/// system prompt and the read-only sandbox with approvals. A patch replaces a row's whole config, so
/// each row restates what it keeps (docs/adapters.md, "DeepSeek Harness").
/// </summary>
internal static class DshPatch
{
    /// <summary>The acp app's own persona line, restated because the row's config is replaced.</summary>
    public const string PersonaPrefix = "You are a coding agent powered by the {{model}} model.";

    /// <summary>DSH has a <c>list_agents</c> of its own, for its subagents; the gorynych tools are named in full.</summary>
    public const string ToolNames =
        "In this harness the gorynych tools are named mcp__gorynych__list_agents, mcp__gorynych__ask_agent, " +
        "mcp__gorynych__spawn_session, mcp__gorynych__archive_session, mcp__gorynych__restore_session and mcp__gorynych__reply. " +
        "Your own list_agents tool lists only your own subagents, not these sessions.";

    /// <summary>What a Gorynych DSH session can and cannot do, said up front so the model does not try.</summary>
    public const string ReadOnlyShell =
        "In Gorynych your shell is read-only. pwsh commands run, but they cannot write files, and Gorynych refuses every request " +
        "to run a shell command with wider sandbox permissions: the tool result then says the user rejected it, although no person " +
        "was asked. So builds, tests, package installs and other commands that write are not available here; when a task needs one, " +
        "say so instead of trying. Reading, searching and commands that only read work, such as git status, git diff and git log. " +
        "Change files only with your write and edit tools, and pass sandbox_permissions \"workspace-write\" with a one-sentence " +
        "justification on each such call: the person approves every write.";

    /// <summary>The overlay's text. <paramref name="instructions"/> is the service's peer text, or null without the peers server.</summary>
    /// <exception cref="ArgumentException">The text holds <c>{{</c>, which DSH would take for a template variable.</exception>
    public static string Text(string? instructions)
    {
        var suffix = instructions is null ? ReadOnlyShell : $"{instructions}\n\n{ToolNames}\n\n{ReadOnlyShell}";
        if (suffix.Contains("{{", StringComparison.Ordinal))
            throw new ArgumentException("The appended system prompt contains \"{{\", which DSH reads as a template variable.", nameof(instructions));
        var text = new StringBuilder();
        text.Append("# Written by Gorynych before each start of dsh --profile acp, and rewritten every time (docs/adapters.md).\n");
        text.Append("- id: system-prompt\n  config:\n    personaPrefix: >-\n      ").Append(PersonaPrefix).Append('\n');
        text.Append("    personaSuffix: |-\n");
        foreach (var line in suffix.ReplaceLineEndings("\n").Split('\n'))
            text.Append(line.Length == 0 ? "" : "      " + line).Append('\n');
        text.Append("- id: sandbox-policy\n  config:\n    mode: read-only\n    workspaceRoot: !!js process.cwd()\n");
        text.Append("- id: approval\n  config:\n    policy: ask\n");
        return text.ToString();
    }
}
