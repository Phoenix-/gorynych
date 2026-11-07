using System.Text.Json;
using System.Text.Json.Serialization;
using Gorynych.Acp.Hosting;

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

    /// <summary>
    /// Made by <c>session/new</c> and not prompted yet (nor asked for an opinion). Clients such
    /// as Zed create sessions eagerly, so the service deletes a draft when it is closed or its
    /// clients leave, and sweeps drafts left over from an earlier run at start.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Draft { get; set; }

    /// <summary>
    /// Deleted by a client while its agent cannot delete sessions itself (Claude): a tombstone
    /// that keeps the session out of every list, since the harness still has it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deleted { get; set; }
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
    /// <summary>How often a bare activity stamp (<see cref="Touch"/>) is written at most.</summary>
    static readonly TimeSpan TouchSaveInterval = TimeSpan.FromSeconds(1);

    readonly string path;
    readonly Lock gate = new();
    readonly Dictionary<string, SessionRecord> records = new(StringComparer.Ordinal);
    bool dirty;
    long lastSave;

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
            var r = GetOrCreate(sessionId, agent);
            change(r);
            Save();
            return Clone(r);
        }
    }

    /// <summary>
    /// Stamps activity on a session. Streaming produces many updates a second, so the stamp is
    /// written at most once per <see cref="TouchSaveInterval"/>; the next save carries it anyway.
    /// </summary>
    public void Touch(string sessionId, string agent)
    {
        lock (gate)
        {
            GetOrCreate(sessionId, agent).UpdatedAt = DateTimeOffset.UtcNow;
            dirty = true;
            if (Environment.TickCount64 - lastSave >= TouchSaveInterval.TotalMilliseconds) Save();
        }
    }

    public void Remove(string sessionId)
    {
        lock (gate)
            if (records.Remove(sessionId)) Save();
    }

    /// <summary>Writes pending changes, such as a recent <see cref="Touch"/>.</summary>
    public void Flush()
    {
        lock (gate)
            if (dirty) Save();
    }

    SessionRecord GetOrCreate(string sessionId, string agent)
    {
        if (records.TryGetValue(sessionId, out var r)) return r;
        var now = DateTimeOffset.UtcNow;
        return records[sessionId] = new SessionRecord { SessionId = sessionId, Agent = agent, CreatedAt = now, UpdatedAt = now };
    }

    /// <summary>
    /// Writes the whole file. A write that still fails after <see cref="AtomicFile"/>'s retries
    /// is logged and left pending: the records stay in memory and the next save writes them.
    /// </summary>
    void Save()
    {
        dirty = true;
        var json = JsonSerializer.Serialize(records.Values.OrderBy(r => r.CreatedAt).ToList(), ServiceJsonContext.Default.ListSessionRecord);
        try
        {
            AtomicFile.WriteAllText(path, json);
            dirty = false;
            lastSave = Environment.TickCount64;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"sessions file not saved, will retry with the next change: {e.Message}");
        }
    }

    static SessionRecord Clone(SessionRecord r) => new()
    {
        SessionId = r.SessionId, Agent = r.Agent, Cwd = r.Cwd, Title = r.Title, TitleSource = r.TitleSource,
        CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt, Draft = r.Draft, Deleted = r.Deleted,
    };
}
