using System.Text;
using System.Text.Json;
using Gorynych.Adapters.Claude;

namespace Gorynych.AdoptClaude;

public sealed record NativeClaudeSession(string SessionId, string Title, string? Cwd, DateTimeOffset ModifiedAt);

/// <summary>Read-only discovery of top-level native transcripts, newest first.</summary>
public static class NativeClaudeSessions
{
    // Session histories can be hundreds of MB. Metadata is in their head and tail; do not
    // read every conversation in full just to build a picker. A partial live line is ignored.
    const int ChunkBytes = 256 * 1024;

    public static IReadOnlyList<NativeClaudeSession> List(string? cwd = null, int limit = 50,
        string? projectsDirectory = null, Action<string>? warning = null)
    {
        if (limit is < 1 or > 1000) throw new ArgumentException("--limit must be between 1 and 1000.");
        if (cwd is not null) cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var root = projectsDirectory ?? Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config
                ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects");
        if (!Directory.Exists(root)) return [];
        var files = new List<FileInfo>();
        // No recursive walk: subagents live under <session>/subagents/ and are not sessions.
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                files.AddRange(new DirectoryInfo(directory).EnumerateFiles("*.jsonl")
                    .Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "D", out _)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warning?.Invoke($"Skipped project directory {directory}: {e.Message}");
            }
        }
        var sessions = new List<NativeClaudeSession>();
        foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, StringComparer.Ordinal))
        {
            try
            {
                var session = Read(file);
                if (session is null || cwd is not null && !SameDirectory(cwd, session.Cwd)) continue;
                sessions.Add(session);
                if (sessions.Count == limit) break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                warning?.Invoke($"Skipped transcript {file.Name}: {e.Message}");
            }
        }
        return sessions;
    }

    static bool SameDirectory(string cwd, string? other) => other is not null && string.Equals(cwd,
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    static NativeClaudeSession? Read(FileInfo file)
    {
        var id = Path.GetFileNameWithoutExtension(file.Name);
        var titles = new TranscriptTitles();
        string? cwd = null, firstPrompt = null;
        var conversation = false;
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        ReadChunk(0, (int)Math.Min(length, ChunkBytes));
        if (length > ChunkBytes) ReadChunk(Math.Max(ChunkBytes, length - ChunkBytes), (int)Math.Min(length - ChunkBytes, ChunkBytes));
        return conversation ? new NativeClaudeSession(id, titles.Title ?? firstPrompt ?? "(untitled)", cwd,
            new DateTimeOffset(file.LastWriteTimeUtc)) : null;

        void ReadChunk(long start, int count)
        {
            stream.Position = start;
            var bytes = new byte[count];
            var read = stream.ReadAtLeast(bytes, count, throwOnEndOfStream: false);
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            if (start > 0)
            {
                var newline = text.IndexOf('\n');
                if (newline < 0) return;
                text = text[(newline + 1)..]; // starts inside a line (possibly inside a UTF-8 character)
            }
            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line.TrimStart('\uFEFF'));
                    var entry = doc.RootElement;
                    if (entry.ValueKind != JsonValueKind.Object || Flag(entry, "isSidechain")) continue;
                    if (String(entry, "sessionId") is { } sid && !string.Equals(sid, id, StringComparison.OrdinalIgnoreCase)) continue;
                    titles.Feed(line);
                    cwd ??= String(entry, "cwd");
                    if (String(entry, "type") is not ("user" or "assistant")) continue;
                    conversation = true;
                    if (firstPrompt is not null || String(entry, "type") != "user" || Flag(entry, "isMeta")) continue;
                    if (entry.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                        && message.TryGetProperty("content", out var content))
                    {
                        var prompt = content.ValueKind == JsonValueKind.String ? content.GetString()
                            : content.ValueKind == JsonValueKind.Array
                                ? string.Join(" ", content.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object && String(c, "type") == "text").Select(c => String(c, "text")))
                                : null;
                        if (!string.IsNullOrWhiteSpace(prompt)) firstPrompt = OneLine(prompt, 120);
                    }
                }
                catch (JsonException) { /* an incomplete line, or a malformed entry, is not metadata */ }
            }
        }
    }

    static string? String(JsonElement entry, string name) => entry.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static bool Flag(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    internal static string OneLine(string text, int max = int.MaxValue)
    {
        var clean = new string(text.Select(c => char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..(max - 3)] + "...";
    }
}
