using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gorynych.Service;

/// <summary>What the service keeps about a session on top of the harness's own state.</summary>
public sealed class SessionRecord
{
    public string SessionId { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string? Title { get; set; }

    /// <summary><c>agent</c> when the harness gave the title, <c>prompt</c> when it is the first prompt.</summary>
    public string? TitleSource { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ServiceState
{
    public string ServiceId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<SessionRecord>))]
[JsonSerializable(typeof(ServiceState))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;

/// <summary>
/// The service's overlay of sessions: which adapter owns each one, plus the fields the
/// harness cannot store (for Kimi, a title). Persisted as one small JSON file.
/// </summary>
public sealed class SessionStore
{
    readonly string path;
    readonly Lock gate = new();
    readonly Dictionary<string, SessionRecord> records = new(StringComparer.Ordinal);

    public SessionStore(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        try
        {
            var list = JsonSerializer.Deserialize(File.ReadAllText(path), ServiceJsonContext.Default.ListSessionRecord) ?? [];
            foreach (var r in list) records[r.SessionId] = r;
        }
        catch (JsonException e)
        {
            ServiceLog.Warn($"sessions file unreadable, starting empty: {e.Message}");
        }
    }

    public SessionRecord? Get(string sessionId)
    {
        lock (gate) return records.TryGetValue(sessionId, out var r) ? Clone(r) : null;
    }

    public List<SessionRecord> All()
    {
        lock (gate) return records.Values.Select(Clone).ToList();
    }

    /// <summary>Applies <paramref name="change"/> to the record (created if missing) and saves.</summary>
    public SessionRecord Update(string sessionId, string agent, Action<SessionRecord> change)
    {
        lock (gate)
        {
            if (!records.TryGetValue(sessionId, out var r))
            {
                var now = DateTimeOffset.UtcNow;
                r = new SessionRecord { SessionId = sessionId, Agent = agent, CreatedAt = now, UpdatedAt = now };
                records[sessionId] = r;
            }
            change(r);
            Save();
            return Clone(r);
        }
    }

    public void Remove(string sessionId)
    {
        lock (gate)
            if (records.Remove(sessionId)) Save();
    }

    void Save()
    {
        var json = JsonSerializer.Serialize(records.Values.OrderBy(r => r.CreatedAt).ToList(), ServiceJsonContext.Default.ListSessionRecord);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    static SessionRecord Clone(SessionRecord r) => new()
    {
        SessionId = r.SessionId, Agent = r.Agent, Cwd = r.Cwd, Title = r.Title, TitleSource = r.TitleSource,
        CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt,
    };
}
