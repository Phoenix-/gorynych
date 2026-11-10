using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

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
    readonly HashSet<string> unsaved = new(Comparer); // allows whose write failed
    FileStamp? loadedStamp;

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
            Refresh();
            if (Ancestors(key).Any(allowed!.Contains)) return true;
        }
        return TrustedByClaude(key);
    }

    /// <summary>Keeps the person's allow for <paramref name="cwd"/>; folders other processes added on disk are kept too.</summary>
    public void Remember(string cwd)
    {
        lock (gate)
        {
            var key = Key(cwd);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
                using var fileLock = SharedFileLock.Acquire(storePath);
                allowed = Load();
                if (!allowed.Add(key) && unsaved.Count == 0) return;
                allowed.UnionWith(unsaved);
                AtomicFile.WriteAllText(storePath, new JsonObject { ["trusted"] = new JsonArray(allowed.Order(StringComparer.Ordinal).Select(f => (JsonNode)f).ToArray()) }
                    .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                unsaved.Clear();
                loadedStamp = FileStamp.Of(storePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                allowed ??= new HashSet<string>(Comparer);
                allowed.Add(key);
                unsaved.Add(key);
                log?.Invoke($"claude: could not keep the trusted folder in {storePath}: {e.Message}");
            }
        }
    }

    /// <summary>Loads the file when it is not loaded yet or another process changed it.</summary>
    void Refresh()
    {
        if (allowed is not null && FileStamp.Of(storePath) == loadedStamp) return;
        allowed = Load();
        allowed.UnionWith(unsaved);
    }

    HashSet<string> Load()
    {
        var set = new HashSet<string>(Comparer);
        loadedStamp = FileStamp.Of(storePath);
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
