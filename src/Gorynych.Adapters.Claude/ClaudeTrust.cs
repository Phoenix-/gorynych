using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// Folder trust for headless sessions. The TUI asks before Claude works in a folder never trusted;
/// <c>claude -p</c> asks nothing and runs the folder's project hooks and MCP servers at once. So
/// the adapter checks Claude's own flag, read only (<c>projects[folder].hasTrustDialogAccepted</c>
/// in <c>~/.claude.json</c>, for the folder or a parent), and otherwise asks the person; an allow
/// is kept in the adapter's own file, never in Claude's configuration.
/// </summary>
internal sealed class ClaudeTrust(string storePath, string? claudeStatePath, Action<string>? log)
{
    readonly Lock gate = new();
    HashSet<string>? allowed;

    /// <summary>Claude's own state file: <c>$CLAUDE_CONFIG_DIR/.claude.json</c>, else <c>~/.claude.json</c>.</summary>
    public static string DefaultClaudeStatePath() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, ".claude.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public bool IsTrusted(string cwd)
    {
        var key = Key(cwd);
        lock (gate)
        {
            allowed ??= Load();
            if (Ancestors(key).Any(allowed.Contains)) return true;
        }
        return TrustedByClaude(key);
    }

    /// <summary>Keeps the person's allow for <paramref name="cwd"/>.</summary>
    public void Remember(string cwd)
    {
        lock (gate)
        {
            allowed ??= Load();
            if (!allowed.Add(Key(cwd))) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
                var tmp = storePath + ".tmp";
                File.WriteAllText(tmp, new JsonObject { ["trusted"] = new JsonArray(allowed.Order(StringComparer.Ordinal).Select(f => (JsonNode)f).ToArray()) }
                    .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, storePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"claude: could not keep the trusted folder in {storePath}: {e.Message}");
            }
        }
    }

    HashSet<string> Load()
    {
        var set = new HashSet<string>(Comparer);
        try
        {
            if (File.Exists(storePath) && JsonNode.Parse(File.ReadAllText(storePath))?["trusted"] is JsonArray list)
                foreach (var f in list.Select(n => (string?)n).OfType<string>()) set.Add(Key(f));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke($"claude: {storePath} could not be read, so no folder counts as trusted by Gorynych: {e.Message}");
        }
        return set;
    }

    /// <summary>Claude's own trust flag for the folder or one of its parents; false when the file cannot be read.</summary>
    bool TrustedByClaude(string key)
    {
        var path = claudeStatePath ?? DefaultClaudeStatePath();
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path))?["projects"] is not JsonObject projects) return false;
            var trusted = projects
                .Where(p => p.Value is JsonObject project && (bool?)project["hasTrustDialogAccepted"] == true)
                .Select(p => Key(p.Key))
                .ToHashSet(Comparer);
            return Ancestors(key).Any(trusted.Contains);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke($"claude: {path} could not be read for folder trust: {e.Message}");
            return false;
        }
    }

    static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>A folder spelled one way: full path, forward slashes, no trailing slash (Claude keys its projects so).</summary>
    static string Key(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { full = folder; }
        var key = full.Replace('\\', '/');
        return key.Length > 1 && !key.EndsWith(":/", StringComparison.Ordinal) ? key.TrimEnd('/') : key;
    }

    static IEnumerable<string> Ancestors(string key)
    {
        for (var k = key; !string.IsNullOrEmpty(k);)
        {
            yield return k;
            var slash = k.LastIndexOf('/');
            if (slash <= 0) yield break;
            var parent = k[..slash];
            if (parent.EndsWith(':')) parent += "/"; // a drive root
            if (parent == k) yield break;
            k = parent;
        }
    }
}
