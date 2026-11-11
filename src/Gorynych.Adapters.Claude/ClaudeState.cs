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

/// <summary>
/// The adapter's list of sessions, for <c>session/list</c> and <c>session/resume</c>. The file is
/// shared by every adapter host process: an update re-reads it under a cross-process lock, and reads
/// reload it when another process changed it, told by what the file holds (<see cref="SharedFile"/>).
/// </summary>
public sealed class ClaudeStateStore
{
    readonly string path;
    readonly Lock gate = new();
    readonly Dictionary<string, ClaudeSessionRecord> records = new(StringComparer.Ordinal);
    // Changes whose write failed, in order: applied again to what each reload reads, so another
    // process's newer change to the same session is kept, and only this one's fields go over it.
    readonly List<(string Id, string Cwd, DateTimeOffset At, Action<ClaudeSessionRecord> Change)> unsaved = [];
    string? readText; // what the file held when it was last read (null: no file)
    bool read;

    readonly Action<string>? log;

    public ClaudeStateStore(string path, Action<string>? log = null)
    {
        this.path = path;
        this.log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (gate) Refresh();
    }

    public ClaudeSessionRecord? Get(string id)
    {
        lock (gate)
        {
            Refresh();
            return records.TryGetValue(id, out var r) ? r : null;
        }
    }

    public List<ClaudeSessionRecord> All()
    {
        lock (gate)
        {
            Refresh();
            return records.Values.ToList();
        }
    }

    public void Update(string id, string cwd, Action<ClaudeSessionRecord> change)
    {
        lock (gate)
        {
            SharedFileLock? fileLock = null;
            try
            {
                try
                {
                    fileLock = SharedFileLock.Acquire(path);
                    Reload();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    fileLock?.Dispose();
                    fileLock = null;
                    log?.Invoke($"claude sessions file not locked or read, the change is kept in memory: {e.Message}");
                }
                var now = DateTimeOffset.UtcNow;
                Apply(id, cwd, now, change);
                unsaved.Add((id, cwd, now, change));
                if (fileLock is null) return;
                try
                {
                    var text = JsonSerializer.Serialize(records.Values.ToList(), ClaudeJsonContext.Default.ListClaudeSessionRecord);
                    AtomicFile.WriteAllText(path, text);
                    unsaved.Clear();
                    readText = text;
                    read = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The records stay in memory; the next update writes them.
                    log?.Invoke($"claude sessions file not saved, will retry with the next change: {e.Message}");
                }
            }
            finally { fileLock?.Dispose(); }
        }
    }

    /// <summary>Reloads when the file holds something else than it did when it was last read.</summary>
    void Refresh()
    {
        string? text;
        try { text = SharedFile.ReadText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; /* read again next time */ }
        if (read && string.Equals(text, readText, StringComparison.Ordinal)) return;
        Load(text);
    }

    /// <summary>
    /// Replaces the records with the file's (a missing or unreadable file reads as empty), then applies
    /// the unsaved changes to them again. A change that reads the clock (<c>UpdatedAt</c>) gets the
    /// time of the reload; it is written with the next change that is saved.
    /// </summary>
    void Reload() => Load(SharedFile.ReadText(path));

    void Load(string? text)
    {
        var fromFile = new List<ClaudeSessionRecord>();
        if (text is not null)
        {
            try { fromFile = JsonSerializer.Deserialize(text, ClaudeJsonContext.Default.ListClaudeSessionRecord) ?? []; }
            catch (JsonException) { }
        }
        records.Clear();
        foreach (var r in fromFile) records[r.SessionId] = r;
        foreach (var (id, cwd, at, change) in unsaved) Apply(id, cwd, at, change);
        readText = text;
        read = true;
    }

    /// <summary>Applies a change to the session's record, made first if there is none.</summary>
    void Apply(string id, string cwd, DateTimeOffset at, Action<ClaudeSessionRecord> change)
    {
        if (!records.TryGetValue(id, out var r))
            records[id] = r = new ClaudeSessionRecord { SessionId = id, Cwd = cwd, CreatedAt = at, UpdatedAt = at };
        change(r);
    }
}
