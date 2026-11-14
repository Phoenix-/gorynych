using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Adapters.Claude;

namespace Gorynych.AdoptClaude;

/// <summary>Registers a native Claude conversation without contacting or restarting the service.</summary>
public static class ClaudeAdoption
{
    /// <returns>True if added; false if already registered (all existing fields are kept).</returns>
    public static bool Register(string dataDirectory, string sessionId, string cwd, string? projectsDirectory = null)
    {
        if (!Guid.TryParseExact(sessionId, "D", out var uuid))
            throw new ArgumentException("The session ID must be a UUID, as shown by Claude's /status.");
        sessionId = uuid.ToString("D");
        cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"Working directory does not exist: {cwd}");
        var transcript = TranscriptTitles.ExpectedPath(cwd, sessionId, projectsDirectory);
        var record = ReadTranscript(transcript, sessionId, cwd);

        var path = Path.Combine(Path.GetFullPath(dataDirectory), "claude", "sessions.json");
        // Same lock and atomic replacement as ClaudeStateStore. Read AFTER acquiring the lock:
        // another adapter host (or another invocation) may just have changed the catalog.
        using var held = SharedFileLock.Acquire(path);
        var text = SharedFile.ReadText(path);
        var catalog = text is null ? new JsonArray() : JsonNode.Parse(text) as JsonArray
            ?? throw new IOException("The Claude catalog is not a JSON array; left unchanged.");
        foreach (var item in catalog)
            if (item is not JsonObject obj || obj["sessionId"] is not JsonValue value || !value.TryGetValue<string>(out _))
                throw new IOException("The Claude catalog has an invalid record; left unchanged.");
        if (catalog.Any(item => string.Equals((string?)item!["sessionId"], sessionId, StringComparison.Ordinal))) return false;
        catalog.Add((JsonNode)record);
        // Do not use ClaudeStateStore.Update: its best-effort persistence is right for a running
        // adapter, but a one-shot utility must fail loudly if the write did not reach disk.
        AtomicFile.WriteAllText(path, catalog.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    static JsonObject ReadTranscript(string path, string id, string cwd)
    {
        var titles = new TranscriptTitles();
        DateTimeOffset? first = null, last = null;
        var found = false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var entry = doc.RootElement;
            if (entry.ValueKind != JsonValueKind.Object) throw new IOException("Invalid Claude transcript entry.");
            titles.Feed(line);
            if (entry.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True) continue;
            if (!entry.TryGetProperty("type", out var type) || type.GetString() is not ("user" or "assistant")) continue;
            if (!entry.TryGetProperty("sessionId", out var sid) || sid.GetString() != id)
                throw new IOException("The transcript's session ID does not match; no registration was written.");
            if (entry.TryGetProperty("cwd", out var dir) && dir.GetString() is { Length: > 0 } folder
                && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), cwd,
                    OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                throw new IOException("The transcript's working directory does not match --cwd; no registration was written.");
            found = true;
            if (entry.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                first = first is null || at < first ? at : first;
                last = last is null || at > last ? at : last;
            }
        }
        if (!found) throw new IOException("The transcript has no main conversation to resume.");
        var fallback = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        return new JsonObject
        {
            ["sessionId"] = id, ["cwd"] = cwd, ["title"] = titles.Title,
            ["createdAt"] = (first ?? fallback).ToString("O"), ["updatedAt"] = (last ?? fallback).ToString("O"),
        };
    }
}
