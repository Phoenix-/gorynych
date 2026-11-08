using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// The title entries of a Claude transcript. Claude writes <c>{"type":"ai-title","aiTitle":...}</c>
/// a few seconds after the first answer, and <c>{"type":"custom-title","customTitle":...}</c>
/// on <c>/rename</c>; a name the person chose wins over the generated one.
/// </summary>
public sealed class TranscriptTitles
{
    public string? AiTitle { get; private set; }
    public string? CustomTitle { get; private set; }
    public string? Title => CustomTitle ?? AiTitle;

    /// <summary>Reads one transcript line; returns true when <see cref="Title"/> changed.</summary>
    public bool Feed(string line)
    {
        if (!line.Contains("-title\"", StringComparison.Ordinal)) return false;
        JsonObject? entry;
        try { entry = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return false; }
        var before = Title;
        switch ((string?)entry?["type"])
        {
            case "ai-title" when (string?)entry["aiTitle"] is { Length: > 0 } ai:
                AiTitle = ai;
                break;
            case "custom-title" when (string?)entry["customTitle"] is { Length: > 0 } custom:
                CustomTitle = custom;
                break;
        }
        return Title != before;
    }

    /// <summary>Where Claude keeps the transcript of a session started in <paramref name="cwd"/>.</summary>
    /// <param name="projectsDirectory">Claude's <c>projects</c> folder; null for <c>$CLAUDE_CONFIG_DIR/projects</c>, else <c>~/.claude/projects</c>.</param>
    public static string ExpectedPath(string cwd, string sessionId, string? projectsDirectory = null)
    {
        var folder = new string(Path.GetFullPath(cwd).TrimEnd('\\', '/').Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return Path.Combine(projectsDirectory ?? DefaultProjectsDirectory(), folder, sessionId + ".jsonl");
    }

    static string DefaultProjectsDirectory() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, "projects")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
}

/// <summary>Follows a transcript file as Claude appends to it and reports title changes. Reads only.</summary>
public sealed class TranscriptWatcher : IAsyncDisposable
{
    readonly TranscriptTitles titles = new();
    readonly Action<string> onTitle;
    readonly CancellationTokenSource stop = new();
    readonly Lock gate = new();
    readonly Task loop;
    string path;
    long offset;

    public TranscriptWatcher(string path, Action<string> onTitle)
    {
        this.path = path;
        this.onTitle = onTitle;
        loop = Task.Run(RunAsync);
    }

    public string? Title => titles.Title;

    /// <summary>Switches to the path Claude reported in a hook payload.</summary>
    public void Follow(string reportedPath)
    {
        lock (gate)
        {
            if (string.Equals(reportedPath, path, StringComparison.OrdinalIgnoreCase)) return;
            path = reportedPath;
            offset = 0;
        }
    }

    async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try { ReadNew(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { await Task.Delay(1000, stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    void ReadNew()
    {
        var changed = new List<string>();
        lock (gate)
        {
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset) offset = 0;
            if (fs.Length == offset) return;
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - offset];
            var read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            // Take whole lines only, so a multi-byte character split by a write is read later in one piece.
            var end = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (end < 0) return;
            offset += end + 1;
            foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split('\n'))
                if (titles.Feed(line) && titles.Title is { } t) changed.Add(t);
        }
        foreach (var t in changed) onTitle(t);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await loop; } catch { }
        stop.Dispose();
    }
}
