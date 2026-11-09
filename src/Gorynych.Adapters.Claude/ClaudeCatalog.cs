using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// Claude's model catalog, the <c>models</c> of a headless <c>initialize</c>, kept in the adapter's
/// <c>catalog.json</c> so a restarted service shows the model and effort selectors before any launch
/// (docs/adapters.md, "Mode, model and effort selectors"). A file that cannot be read counts as none.
/// </summary>
internal sealed class ClaudeCatalogStore(string path, Action<string>? log)
{
    readonly Lock gate = new();
    string? saved;

    /// <summary>The kept catalog, or an empty one when there is none or it cannot be read.</summary>
    public List<ClaudeModel> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text) is not JsonObject file || file["models"] is not JsonArray models)
                throw new JsonException("no models list");
            var catalog = ClaudeOptions.Catalog(file);
            lock (gate) saved = Key(models);
            return catalog;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            log?.Invoke($"claude: {path} unreadable, no model catalog until Claude gives one: {e.Message}");
            return [];
        }
    }

    /// <summary>Keeps <paramref name="models"/> unless they are what is kept already. False when nothing changed.</summary>
    public bool Save(JsonArray models)
    {
        var key = Key(models);
        lock (gate)
        {
            if (key == saved) return false;
            saved = key;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, new JsonObject { ["models"] = models.DeepClone() }.ToJsonString(AcpJson.TextOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"claude: the model catalog was not saved to {path}: {e.Message}");
        }
        return true;
    }

    static string Key(JsonArray models) => models.ToJsonString(AcpJson.TextOptions);
}
