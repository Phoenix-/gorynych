using Gorynych.Acp;

namespace Gorynych.Adapters.Claude;

/// <summary>How the adapter drives Claude Code (docs/adapters.md).</summary>
public enum ClaudeTransport
{
    /// <summary><c>claude -p</c> with stream-json in and out, one process per session: the default.</summary>
    Headless,

    /// <summary>The interactive TUI in a Windows pseudo console, with hooks and the development channel: the fallback.</summary>
    Tui,
}

/// <summary>One Claude Code session as the agent face sees it, whichever transport runs it.</summary>
internal interface IClaudeSession : IAsyncDisposable
{
    string Id { get; }
    string Cwd { get; }

    /// <summary>The secret in this session's URLs on the adapter's loopback server.</summary>
    string Token { get; }

    /// <summary>The MCP servers the ACP client gave this session; they apply from the next launch.</summary>
    List<McpServer> McpServers { get; set; }

    /// <summary>The text of the ACP <c>systemPrompt</c> (append mode); it applies from the next launch.</summary>
    string? SystemPrompt { get; set; }

    /// <summary>Claude's task checklist, reduced from its native task tools into ACP's plan.</summary>
    ClaudeTaskPlan TaskPlan { get; }

    /// <summary>
    /// Whether <see cref="TaskPlan"/> was built from the whole native history, so a task update that
    /// names an older task can be applied; false until a replay or a lazy prime reads it.
    /// </summary>
    bool TasksPrimed { get; set; }

    /// <summary>Whether Claude's process runs now.</summary>
    bool IsRunning { get; }

    /// <param name="cancelled">True once the client cancelled after sending this prompt.</param>
    /// <param name="arrivalSequence">Its place in the ACP stream, so a steer behind it is ordered after it.</param>
    Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> prompt, Func<bool> cancelled, CancellationToken ct, long arrivalSequence = 0);

    /// <param name="arrivalSequence">The cancel's place in the ACP stream: it waits for prompts and steers before it.</param>
    void Cancel(long arrivalSequence = 0);
}

/// <summary>The environment Claude is started with.</summary>
internal static class ClaudeEnvironment
{
    // Parent-session identity must not leak into an independently managed session. All other
    // settings, including unknown provider/authentication variables, belong to Claude Code.
    static readonly HashSet<string> ParentSessionVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLAUDECODE", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_BRIDGE_SESSION_ID",
        "CLAUDE_CODE_REMOTE_SESSION_ID", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_PID", "CLAUDE_EFFORT",
    };

    /// <summary>
    /// This process's environment without the known identity markers of its parent Claude session.
    /// Native authentication, provider, TLS, proxy and SDK configuration are inherited unchanged.
    /// </summary>
    /// <param name="source">The variables to start from; this process's environment when null.</param>
    public static Dictionary<string, string> Clean(System.Collections.IDictionary? source = null)
    {
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in source ?? Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var key in env.Keys.Where(ParentSessionVariables.Contains).ToList()) env.Remove(key);
        return env;
    }

    /// <summary>
    /// The environment of a session's own Claude launch: <see cref="Clean"/> plus Claude Code's
    /// documented per-subprocess opt-in for its task-tracking tools. The task tools are off by
    /// default on newer models, and Gorynych reads them to show ACP's plan. Nothing else is
    /// touched: inherited <c>CLAUDE_CODE_ENABLE_TASKS</c>, authentication and provider settings
    /// stay as the person configured them, and no settings file is written.
    /// </summary>
    /// <param name="source">The variables to start from; this process's environment when null.</param>
    public static Dictionary<string, string> ForSession(System.Collections.IDictionary? source = null)
    {
        var env = Clean(source);
        env["CLAUDE_CODE_ENABLE_TODO_TOOLS"] = "1";
        return env;
    }
}
