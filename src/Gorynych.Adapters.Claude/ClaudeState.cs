using System.Text.Json;
using System.Text.Json.Serialization;
using Gorynych.Acp.Hosting;

namespace Gorynych.Adapters.Claude;

/// <summary>What the adapter remembers about the Claude sessions it started.</summary>
public sealed class ClaudeSessionRecord
{
    public string SessionId { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string? Title { get; set; }

    /// <summary>Headless: the model, effort and permission mode the person picked, for the session's next launches.</summary>
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? Mode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ClaudeSessionRecord>))]
[JsonSerializable(typeof(ChannelConsentRecord))]
internal sealed partial class ClaudeJsonContext : JsonSerializerContext;

/// <summary>The adapter's list of sessions, for <c>session/list</c> and <c>session/resume</c>.</summary>
public sealed class ClaudeStateStore
{
    readonly string path;
    readonly Lock gate = new();
    readonly Dictionary<string, ClaudeSessionRecord> records = new(StringComparer.Ordinal);

    readonly Action<string>? log;

    public ClaudeStateStore(string path, Action<string>? log = null)
    {
        this.path = path;
        this.log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) return;
        try
        {
            foreach (var r in JsonSerializer.Deserialize(File.ReadAllText(path), ClaudeJsonContext.Default.ListClaudeSessionRecord) ?? [])
                records[r.SessionId] = r;
        }
        catch (JsonException) { }
    }

    public ClaudeSessionRecord? Get(string id)
    {
        lock (gate) return records.TryGetValue(id, out var r) ? r : null;
    }

    public List<ClaudeSessionRecord> All()
    {
        lock (gate) return records.Values.ToList();
    }

    public void Update(string id, string cwd, Action<ClaudeSessionRecord> change)
    {
        lock (gate)
        {
            if (!records.TryGetValue(id, out var r))
            {
                var now = DateTimeOffset.UtcNow;
                records[id] = r = new ClaudeSessionRecord { SessionId = id, Cwd = cwd, CreatedAt = now, UpdatedAt = now };
            }
            change(r);
            try
            {
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(records.Values.ToList(), ClaudeJsonContext.Default.ListClaudeSessionRecord));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The records stay in memory; the next update writes them.
                log?.Invoke($"claude sessions file not saved, will retry with the next change: {e.Message}");
            }
        }
    }
}
