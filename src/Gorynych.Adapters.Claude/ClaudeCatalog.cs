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
    string? current; // the catalog in use, as the file's text
    string? written; // what the file holds

    /// <summary>The kept catalog, or an empty one when there is none or it cannot be read.</summary>
    public List<ClaudeModel> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject file || file["models"] is not JsonArray models)
                throw new JsonException("no models list");
            var catalog = ClaudeOptions.Catalog(file);
            var text = Text(models);
            lock (gate) current = written = text;
            return catalog;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever the file holds, it never stops the adapter: a catalog comes with the next launch.
            log?.Invoke($"claude: {path} unreadable, no model catalog until Claude gives one: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// Keeps <paramref name="models"/>. True when they differ from the catalog in use. The file is
    /// written when it does not hold them yet; one that could not be written is tried again next time.
    /// </summary>
    public bool Save(JsonArray models)
    {
        var text = Text(models);
        bool changed;
        lock (gate)
        {
            changed = text != current;
            current = text;
            if (text == written) return changed;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, text);
            lock (gate) written = text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"claude: the model catalog was not saved to {path}: {e.Message}");
        }
        return changed;
    }

    static string Text(JsonArray models) => new JsonObject { ["models"] = models.DeepClone() }.ToJsonString(AcpJson.TextOptions);
}