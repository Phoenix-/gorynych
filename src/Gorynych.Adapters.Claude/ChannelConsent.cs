using System.Text.Json;
using Gorynych.Acp.Hosting;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// Whether Gorynych may start Claude Code with development channels
/// (<c>--dangerously-load-development-channels</c>, a Claude Code research preview) and confirm the
/// notice Claude shows for them at every launch. The channel carries other agents' questions and
/// child sessions' reports into a running session; without it they arrive as ordinary turns.
/// </summary>
public enum ChannelConsent
{
    /// <summary>
    /// Ask the person once, with an approval on the session that would first launch with the
    /// channel, and keep the answer in the adapter's data (<c>channels.json</c>).
    /// </summary>
    Ask,

    /// <summary>Agreed up front (<c>--claude-channels accept</c>), for a service nobody watches.</summary>
    Accept,

    /// <summary>Declined up front (<c>--claude-channels decline</c>): Claude runs without the channel.</summary>
    Decline,
}

/// <summary>The kept answer to the development-channels question.</summary>
public sealed class ChannelConsentRecord
{
    public bool Accepted { get; set; }
    public DateTimeOffset AnsweredAt { get; set; }

    /// <summary>The session the person answered on.</summary>
    public string? SessionId { get; set; }
}

/// <summary>
/// The person's answer to the development-channels question, in <c>channels.json</c> of the
/// adapter's data directory: the service's own state, never Claude's configuration.
/// </summary>
internal sealed class ChannelConsentStore
{
    readonly string path;
    readonly Action<string>? log;
    readonly Lock gate = new();
    ChannelConsentRecord? record;
    ChannelConsentRecord? unsaved; // an answer whose write failed: it holds until the file has a later one
    FileStamp? readStamp;
    bool read;

    public ChannelConsentStore(string path, Action<string>? log = null)
    {
        this.path = path;
        this.log = log;
        lock (gate) Refresh();
    }

    public string Path => path;

    /// <summary>The kept answer, or null when the person has not answered yet.</summary>
    public bool? Answer
    {
        get
        {
            lock (gate)
            {
                Refresh();
                return record?.Accepted;
            }
        }
    }

    /// <summary>
    /// Reads the file when it has not been read yet or another process changed it. An answer of this
    /// process's that could not be saved stays, unless the file holds a later one.
    /// </summary>
    void Refresh()
    {
        var stamp = FileStamp.Of(path);
        if (read && stamp == readStamp) return;
        read = true;
        readStamp = stamp;
        ChannelConsentRecord? fromFile = null;
        if (stamp is not null)
            try { fromFile = JsonSerializer.Deserialize(File.ReadAllText(path), ClaudeJsonContext.Default.ChannelConsentRecord); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Treated as not asked yet, like an answer that could not be saved.
                log?.Invoke($"claude adapter: {path} is not readable, the person is asked again: {e.Message}");
            }
        if (unsaved is { } mine && (fromFile is null || fromFile.AnsweredAt <= mine.AnsweredAt))
        {
            record = mine;
            return;
        }
        unsaved = null;
        record = fromFile;
    }

    public void Save(bool accepted, string sessionId)
    {
        lock (gate)
        {
            record = new ChannelConsentRecord { Accepted = accepted, AnsweredAt = DateTimeOffset.UtcNow, SessionId = sessionId };
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                using var fileLock = SharedFileLock.Acquire(path);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(record, ClaudeJsonContext.Default.ChannelConsentRecord));
                readStamp = FileStamp.Of(path);
                read = true;
                unsaved = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The answer holds for this process, also when another one changes the file, unless it
                // saves a later answer; after a restart the person is asked again.
                unsaved = record;
                log?.Invoke($"claude adapter: the development-channels answer was not saved to {path}: {e.Message}");
            }
        }
    }
}
