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
/// reload it when another process changed it.
/// </summary>
public sealed class ClaudeStateStore
{
    readonly string path;
    readonly Lock gate = new();
    readonly Dictionary<string, ClaudeSessionRecord> records = new(StringComparer.Ordinal);
    readonly Dictionary<string, ClaudeSessionRecord> unsaved = new(StringComparer.Ordinal); // changes whose write failed
    FileStamp? readStamp;
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
                if (!records.TryGetValue(id, out var r))
                {
                    var now = DateTimeOffset.UtcNow;
                    records[id] = r = new ClaudeSessionRecord { SessionId = id, Cwd = cwd, CreatedAt = now, UpdatedAt = now };
                }
                change(r);
                unsaved[id] = r;
                if (fileLock is null) return;
                try
                {
                    AtomicFile.WriteAllText(path, JsonSerializer.Serialize(records.Values.ToList(), ClaudeJsonContext.Default.ListClaudeSessionRecord));
                    unsaved.Clear();
                    readStamp = FileStamp.Of(path);
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

    /// <summary>Reloads when the file's write time or length differ from what was last read.</summary>
    void Refresh()
    {
        if (read && FileStamp.Of(path) == readStamp) return;
        try { Reload(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* read again next time */ }
    }

    /// <summary>Replaces the records with the file's (a missing or unreadable file reads as empty), keeping unsaved changes.</summary>
    void Reload()
    {
        var stamp = FileStamp.Of(path);
        var fromFile = new List<ClaudeSessionRecord>();
        if (stamp is not null)
        {
            try { fromFile = JsonSerializer.Deserialize(File.ReadAllText(path), ClaudeJsonContext.Default.ListClaudeSessionRecord) ?? []; }
            catch (JsonException) { }
        }
        records.Clear();
        foreach (var r in fromFile) records[r.SessionId] = r;
        foreach (var (id, r) in unsaved) records[id] = r;
        readStamp = stamp;
        read = true;
    }
}
