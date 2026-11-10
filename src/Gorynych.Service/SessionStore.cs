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

    /// <summary>The session that started this one with <c>spawn_session</c>; null for one a client created.</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>
    /// The token of the session's peers server (<c>gorynych/peerToken</c>), kept so that the service
    /// that comes after a restart knows the agent that still holds it.
    /// </summary>
    public string? PeerToken { get; set; }

    /// <summary>
    /// The newest place in its adapter host's stream the service took for this session
    /// (<c>gorynych/hostSeq</c>), kept when the service exits: it took some without journaling them
    /// with their place (a catch-up of the agent's own history), and the host must not send those to
    /// the next service again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostSeen { get; set; }

    /// <summary>
    /// The <c>gorynych/promptId</c>s of the turns that went on in the adapter's host when the service
    /// exited for a restart. The next service ends those its host no longer has (it died, or exited
    /// alone) as cancelled, and the host reports the end of the others.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? HostPrompts { get; set; }

    /// <summary>
    /// The host the session was open in when the service exited (its ID and slot): the next service
    /// waits for that host to come back before it opens the session anywhere else, so it is never open
    /// in two hosts at once.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostOf { get; set; }

    /// <inheritdoc cref="HostOf"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostSlot { get; set; }

    /// <summary>A spawned session reports the end of each of its turns to its parent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReportToParent { get; set; }

    /// <summary>
    /// Archived: out of sight, not locked. Left out of <c>session/list</c> and <c>list_agents</c>
    /// unless asked for, but asked, prompted and opened like any other. Only <c>_gorynych/archive</c>
    /// changes it. No harness offers an archive through ACP, so it lives here for every agent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Archived { get; set; }

    /// <summary>Shown in client lists: neither a draft nor deleted.</summary>
    [JsonIgnore]
    public bool Listed => !Draft && !Deleted;
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
    /// <summary>How often a bare activity stamp (<see cref="Touch"/>) may try to write the file.</summary>
    static readonly TimeSpan TouchSaveInterval = TimeSpan.FromSeconds(1);

    readonly string path;
    readonly Lock gate = new();      // the records
    readonly Lock writeGate = new(); // the file; its retries never hold the records' lock
    readonly Dictionary<string, SessionRecord> records = new(StringComparer.Ordinal);
    readonly HashSet<string> removed = new(StringComparer.Ordinal);
    long changes;     // in-memory changes so far (under gate)
    long written;     // the change count the file holds (under writeGate)
    long lastAttempt; // when a write was last tried (under gate)

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

    /// <summary>
    /// Called with copies of a record before and after each change (null when it did not exist,
    /// or was removed), under the store's lock and in the order of the changes. It must not block.
    /// A bare activity stamp (<see cref="Touch"/>) calls <see cref="Active"/> instead.
    /// </summary>
    public Action<SessionRecord?, SessionRecord>? Changed { get; set; }

    /// <summary>Called with a copy of a record when it is removed, like <see cref="Changed"/>.</summary>
    public Action<SessionRecord>? Removed { get; set; }

    /// <summary>
    /// Called with the session and its agent when <see cref="Touch"/> stamps activity on a listed
    /// session (one that is neither a draft nor deleted), under the store's lock like
    /// <see cref="Changed"/>. Streaming touches a session many times a second, so it gets no copies.
    /// </summary>
    public Action<string, string>? Active { get; set; }

    public SessionRecord? Get(string sessionId)
    {
        lock (gate) return records.TryGetValue(sessionId, out var r) ? Clone(r) : null;
    }

    public List<SessionRecord> All()
    {
        lock (gate) return records.Values.Select(Clone).ToList();
    }

    /// <summary>
    /// Runs <paramref name="look"/> with a copy of the record (null when there is none) under the
    /// store's lock, so no change comes in between; like <see cref="Changed"/>, it must not block.
    /// </summary>
    public void Inspect(string sessionId, Action<SessionRecord?> look)
    {
        lock (gate) look(records.TryGetValue(sessionId, out var r) ? Clone(r) : null);
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the record (created if missing) and saves. Returns
    /// null for a session removed in this run, which a late update must not bring back.
    /// </summary>
    /// <param name="durable">Throw an <see cref="IOException"/> when the change could not be written,
    /// instead of logging it and leaving it for the next write. The change stays in memory either way.</param>
    public SessionRecord? Update(string sessionId, string agent, Action<SessionRecord> change, bool durable = false)
    {
        SessionRecord result;
        (string Json, long Version) snapshot;
        lock (gate)
        {
            var before = records.TryGetValue(sessionId, out var existing) ? Clone(existing) : null;
            if (GetOrCreate(sessionId, agent) is not { } r) return null;
            change(r);
            result = Clone(r);
            snapshot = Snapshot();
            Changed?.Invoke(before, Clone(r));
        }
        Write(snapshot, durable);
        return result;
    }

    /// <summary>
    /// Stamps activity on a session. Streaming produces many updates a second, so a stamp tries
    /// to write the file at most once per <see cref="TouchSaveInterval"/> after the last attempt
    /// (successful or not); the next write carries it anyway. Ignores unknown, removed and deleted
    /// sessions: an update that arrives before its session is recorded must not record it.
    /// </summary>
    public void Touch(string sessionId)
    {
        (string Json, long Version) snapshot;
        lock (gate)
        {
            if (!records.TryGetValue(sessionId, out var r) || r.Deleted) return;
            r.UpdatedAt = DateTimeOffset.UtcNow;
            changes++;
            if (r.Listed) Active?.Invoke(r.SessionId, r.Agent);
            if (Environment.TickCount64 - lastAttempt < TouchSaveInterval.TotalMilliseconds) return;
            snapshot = Snapshot(counted: true);
        }
        Write(snapshot, durable: false);
    }

    /// <summary>Removes the record; later updates of the same session in this run are ignored.</summary>
    public void Remove(string sessionId)
    {
        (string Json, long Version) snapshot;
        lock (gate)
        {
            removed.Add(sessionId);
            if (!records.Remove(sessionId, out var gone)) return;
            snapshot = Snapshot();
            Removed?.Invoke(Clone(gone));
        }
        Write(snapshot, durable: false);
    }

    /// <summary>Writes pending changes, such as a recent <see cref="Touch"/>.</summary>
    public void Flush()
    {
        (string Json, long Version) snapshot;
        lock (gate)
        {
            if (changes <= Volatile.Read(ref written)) return;
            snapshot = Snapshot(counted: true);
        }
        Write(snapshot, durable: false);
    }

    SessionRecord? GetOrCreate(string sessionId, string agent)
    {
        if (records.TryGetValue(sessionId, out var r)) return r;
        if (removed.Contains(sessionId)) return null;
        var now = DateTimeOffset.UtcNow;
        return records[sessionId] = new SessionRecord { SessionId = sessionId, Agent = agent, CreatedAt = now, UpdatedAt = now };
    }

    /// <summary>The file's content as of now, numbered by the in-memory change count. Called under <c>gate</c>.</summary>
    (string Json, long Version) Snapshot(bool counted = false)
    {
        if (!counted) changes++;
        lastAttempt = Environment.TickCount64;
        return (JsonSerializer.Serialize(records.Values.OrderBy(r => r.CreatedAt).ToList(), ServiceJsonContext.Default.ListSessionRecord), changes);
    }

    /// <summary>
    /// Writes a snapshot unless a later one is already on disk. A write that still fails after
    /// <see cref="AtomicFile"/>'s retries is logged and left for the next write, or thrown when durable.
    /// </summary>
    void Write((string Json, long Version) snapshot, bool durable)
    {
        lock (writeGate)
        {
            if (written >= snapshot.Version) return;
            try
            {
                AtomicFile.WriteAllText(path, snapshot.Json);
                Volatile.Write(ref written, snapshot.Version);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (durable) throw new IOException($"The sessions file could not be saved: {e.Message}", e);
                ServiceLog.Warn($"sessions file not saved, will retry with the next change: {e.Message}");
            }
        }
    }

    static SessionRecord Clone(SessionRecord r) => new()
    {
        SessionId = r.SessionId, Agent = r.Agent, Cwd = r.Cwd, Title = r.Title, TitleSource = r.TitleSource,
        CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt, Draft = r.Draft, Deleted = r.Deleted,
        ParentSessionId = r.ParentSessionId, ReportToParent = r.ReportToParent, Archived = r.Archived, PeerToken = r.PeerToken,
        HostSeen = r.HostSeen, HostPrompts = r.HostPrompts is null ? null : [.. r.HostPrompts],
        HostOf = r.HostOf, HostSlot = r.HostSlot,
    };
}
