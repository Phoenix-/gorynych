using System.Text;
using System.Text.Json;

namespace Gorynych.Management;

/// <summary>
/// What a management command stopped, kept in the data folder so a later start of the service or a
/// bridge does not bring it back. <c>stop --all</c> also records that the whole service is stopped,
/// which keeps a bridge from starting it again until <c>start --all</c>.
/// </summary>
/// <remarks>
/// The file is <c>maintenance.json</c> in the service's data folder. It holds no secrets and is
/// written atomically; a missing or malformed file means nothing is stopped.
/// </remarks>
public sealed record ManagementState(bool StoppedAll, IReadOnlySet<string> Agents, DateTimeOffset UpdatedAt)
{
    public const string FileName = "maintenance.json";

    public static readonly ManagementState Empty =
        new(false, new HashSet<string>(StringComparer.Ordinal), DateTimeOffset.MinValue);

    public bool IsStopped(string agent) => StoppedAll || Agents.Contains(agent);

    public ManagementState WithStopped(IEnumerable<string> agents, bool all = false) =>
        new(StoppedAll || all, new HashSet<string>(Agents.Concat(agents), StringComparer.Ordinal), DateTimeOffset.UtcNow);

    public ManagementState WithStarted(IEnumerable<string> agents, bool all = false)
    {
        var left = new HashSet<string>(Agents, StringComparer.Ordinal);
        foreach (var agent in agents) left.Remove(agent);
        if (all) left.Clear();
        return new(all ? false : StoppedAll, left, DateTimeOffset.UtcNow);
    }

    /// <summary>The data folder when no <c>--data</c> names one, as <c>DataFolder.DefaultPath</c> does.</summary>
    public static string DefaultDirectory() =>
        Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gorynych"));

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>Reads the marker; missing, unreadable or malformed means nothing is stopped.</summary>
    public static ManagementState Read(string dataDirectory)
    {
        try
        {
            var path = PathIn(dataDirectory);
            if (!File.Exists(path)) return Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;
            var all = root.TryGetProperty("stoppedAll", out var flag) && flag.ValueKind == JsonValueKind.True;
            var agents = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("agents", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                        agents.Add(name);
            var updated = UpdatedAtOf(root);
            return new ManagementState(all, agents, updated);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty;
        }
    }

    static DateTimeOffset UpdatedAtOf(JsonElement root) =>
        root.TryGetProperty("updatedAt", out var time) && time.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(time.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>
    /// Writes the marker atomically, retrying a moment as an antivirus scanner or indexer may hold it.
    /// Throws <see cref="IOException"/> when it cannot be written: a caller must not report a stop that
    /// a restart would forget.
    /// </summary>
    public static void Write(string dataDirectory, ManagementState state)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = PathIn(dataDirectory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteBoolean("stoppedAll", state.StoppedAll);
                writer.WriteStartArray("agents");
                foreach (var agent in state.Agents.Order(StringComparer.Ordinal)) writer.WriteStringValue(agent);
                writer.WriteEndArray();
                writer.WriteString("updatedAt", state.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteEndObject();
                writer.Flush();
            }
            for (var attempt = 0; ; attempt++)
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
                {
                    Thread.Sleep(50);
                }
        }
        finally
        {
            try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Clears the marker; a missing one is fine.</summary>
    public static void Clear(string dataDirectory)
    {
        try { File.Delete(PathIn(dataDirectory)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A short human line for <c>status</c>.</summary>
    public string Describe() => StoppedAll
        ? "everything stopped by a management command"
        : Agents.Count == 0 ? "nothing stopped" : "stopped: " + string.Join(", ", Agents.Order(StringComparer.Ordinal));
}
