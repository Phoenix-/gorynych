using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using static Gorynych.Adapters.Dsh.DshLines;

namespace Gorynych.Adapters.Dsh;

/// <summary>
/// A permission mode of a Gorynych DSH session: the sandbox of the DSH process that runs it. The
/// IDs and names are Codex's, so a client draws one selector for both (docs/adapters.md).
/// </summary>
internal sealed record DshMode(string Id, string Name, string Description, string Sandbox, string PatchFile);

internal static class DshModes
{
    public const string OptionId = "mode";

    /// <summary>The value a session DSH pinned to something else shows; it cannot be chosen.</summary>
    public const string PinnedValue = "dsh";

    public static readonly DshMode ReadOnly = new(
        "read-only", "Read only",
        "Each file write asks you, and the shell cannot write. This protects files from changes; it does not stop DeepSeek Harness from reading secrets or sending them over the network.",
        "read-only", DshAdapter.PatchFile);

    public static readonly DshMode FullAccess = new(
        "full-access", "Full access",
        "DeepSeek Harness asks nothing at all: it runs any command and writes any file, outside this folder too.",
        "danger-full-access", "gorynych.full-access.patch.yml");

    public static readonly DshMode[] All = [ReadOnly, FullAccess];

    public static DshMode? ById(string? id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// The mode a pin amounts to, or null for a pin no mode matches (such as <c>workspace-write</c>,
    /// even one that never asks: it is still confined). No sandbox is full access whatever the
    /// approval: nothing escalates, so nothing asks, as in Gorynych's own full-access process (approval
    /// <c>ask</c>). Only full access is marked <c>gorynych/fullAccess</c> (<see cref="Option"/>).
    /// </summary>
    public static DshMode? Of(DshPin pin) => pin.Sandbox switch
    {
        "read-only" when pin.Approval is null or "ask" => ReadOnly,
        "danger-full-access" => FullAccess,
        _ => null,
    };

    /// <summary>
    /// The <c>mode</c> config option of a session that runs in <paramref name="mode"/>'s process. A
    /// session DSH pinned shows what the pin amounts to; a pin no mode matches is shown as it is.
    /// </summary>
    public static JsonObject Option(DshMode mode, DshPin? pin)
    {
        var current = pin is null ? mode.Id : Of(pin)?.Id ?? PinnedValue;
        var values = new JsonArray();
        foreach (var m in All)
            values.Add(AcpJson.ToNode(new SessionConfigSelectOption
            {
                Value = m.Id, Name = m.Name, Description = m.Description,
                // The service asks the person before a session not in full access asks this one.
                Meta = m == FullAccess ? new JsonObject { [GorynychProtocol.MetaFullAccess] = true } : null,
            }));
        if (current == PinnedValue)
            values.Add(AcpJson.ToNode(new SessionConfigSelectOption
            {
                Value = PinnedValue, Name = "Set in DeepSeek Harness",
                Description = $"sandbox {pin!.Sandbox}, approval {pin.Approval ?? "ask"}; change it in dsh web with /permission",
            }));
        return AcpJson.ToNode<SessionConfigOption>(new SessionConfigSelect
        {
            Id = OptionId,
            Name = "Permissions",
            Category = SessionConfigOptionCategory.Mode,
            CurrentValue = current,
            Options = values,
        })!.AsObject();
    }

    /// <summary>Puts the mode option first in a <c>configOptions</c> list, replacing one already there.</summary>
    public static void AddTo(JsonObject holder, DshMode mode, DshPin? pin)
    {
        if (holder["configOptions"] is not JsonArray options) holder["configOptions"] = options = [];
        for (var i = options.Count - 1; i >= 0; i--)
            if (options[i] is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var id) && id == OptionId)
                options.RemoveAt(i);
        options.Insert(0, Option(mode, pin));
    }
}

/// <summary>A sandbox mode DSH recorded in a session itself: it wins over the process's sandbox.</summary>
internal sealed record DshPin(string Sandbox, string? Approval);

/// <summary>
/// Reads whether DSH pinned a session's permissions, from DSH's projection cache
/// (<c>storages/session_projcache/sessions/&lt;id&gt;.json</c> in DSH's home). DSH 0.2.2 pins them
/// into every session made where its permission presets are on: <c>dsh web</c>, and Gorynych
/// before the full-access mode. Read only, and best effort: a cache that is missing, of another
/// version or unreadable counts as no pin.
/// </summary>
internal sealed class DshPins(string? home)
{
    /// <summary>The projection cache's format version this reader knows.</summary>
    const int CacheVersion = 7;

    public static string DefaultHome() =>
        Environment.GetEnvironmentVariable("DSH_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

    public DshPin? Read(string sessionId)
    {
        if (home is null || sessionId.Length == 0 || sessionId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        var file = Path.Combine(home, "storages", "session_projcache", "sessions", sessionId + ".json");
        try
        {
            if (!File.Exists(file)) return null;
            var cache = JsonNode.Parse(File.ReadAllText(file));
            if (cache?["version"] is not JsonValue version || !version.TryGetValue<int>(out var v) || v != CacheVersion) return null;
            var rows = cache["record"]?["rows"];
            if (Str(rows?["sandboxMode"]?["val"]) is not { } sandbox) return null;
            return new DshPin(sandbox, Str(rows?["approvalState"]?["val"]?["policy"]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

}

/// <summary>
/// The modes Gorynych keeps for DSH sessions, which DSH cannot keep for it: one small JSON file in
/// the adapter's data folder, with the sessions that are not read-only.
/// </summary>
internal sealed class DshModeStore
{
    readonly string path;
    readonly Lock gate = new();
    readonly Dictionary<string, string> modes = new(StringComparer.Ordinal);

    public DshModeStore(string path, Action<string>? log)
    {
        this.path = path;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject saved)
                foreach (var (id, mode) in saved)
                    if (mode is JsonValue v && v.TryGetValue<string>(out var m) && DshModes.ById(m) is not null)
                        modes[id] = m;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"dsh: {path} unreadable, every session starts read-only: {e.Message}");
        }
    }

    public DshMode Get(string sessionId)
    {
        lock (gate) return DshModes.ById(modes.GetValueOrDefault(sessionId)) ?? DshModes.ReadOnly;
    }

    /// <summary>Records a session's mode and saves the file.</summary>
    /// <exception cref="IOException">The file could not be written; the mode is not changed.</exception>
    public void Set(string sessionId, DshMode mode)
    {
        lock (gate)
        {
            var before = modes.GetValueOrDefault(sessionId);
            if (mode == DshModes.ReadOnly) modes.Remove(sessionId);
            else modes[sessionId] = mode.Id;
            var json = new JsonObject();
            foreach (var (id, m) in modes.OrderBy(kv => kv.Key, StringComparer.Ordinal)) json[id] = m;
            try
            {
                AtomicFile.WriteAllText(path, json.ToJsonString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (before is null) modes.Remove(sessionId);
                else modes[sessionId] = before;
                throw new IOException($"The DSH session modes could not be saved: {e.Message}", e);
            }
        }
    }
}
