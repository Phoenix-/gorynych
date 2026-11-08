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

    /// <summary>Whether Claude's process runs now.</summary>
    bool IsRunning { get; }

    /// <param name="cancelled">True once the client cancelled after sending this prompt.</param>
    Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> prompt, Func<bool> cancelled, CancellationToken ct);

    void Cancel();
}

/// <summary>The environment Claude is started with.</summary>
internal static class ClaudeEnvironment
{
    /// <summary>
    /// This process's environment without what would tie the child to another Claude Code session:
    /// started from inside one (the desktop app, say), the child would otherwise attach to that
    /// session instead of the user's own login.
    /// </summary>
    /// <param name="headless">Also leave out the API key and auth token variables: in <c>claude -p</c> they win over
    /// the person's plan login, so headless Claude never sees them (the TUI is left as it was).</param>
    /// <param name="source">The variables to start from; this process's environment when null.</param>
    public static Dictionary<string, string> Clean(bool headless = false, System.Collections.IDictionary? source = null)
    {
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in source ?? Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        if (headless)
            foreach (var key in env.Keys.Where(k => k.Equals("ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase)
                         || k.Equals("ANTHROPIC_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase)).ToList())
                env.Remove(key);
        var nested = env.ContainsKey("CLAUDECODE");
        foreach (var key in env.Keys.ToList())
        {
            if (key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE_AGENT_SDK", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE_PREVIEW_", StringComparison.OrdinalIgnoreCase)
                || key is "CLAUDE_PID" or "CLAUDE_EFFORT"
                || (nested && key is "ANTHROPIC_BASE_URL" or "MCP_CONNECTION_NONBLOCKING" or "MCP_SERVER_CONNECTION_BATCH_SIZE"))
                env.Remove(key);
        }
        return env;
    }
}
