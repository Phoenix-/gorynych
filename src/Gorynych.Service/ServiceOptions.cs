using System.Text.Json;

namespace Gorynych.Service;

/// <summary>
/// The behavioral service options of the last start, kept so <c>start --all</c> can bring the
/// service back the way it ran (for example with <c>--hosts off</c> or <c>--no-peers</c>) rather
/// than with defaults. It holds no secret: only the options already visible in the command line.
/// </summary>
internal static class ServiceOptions
{
    const string FileName = "service-options.json";

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public static void Write(string dataDirectory, IReadOnlyList<string> arguments)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var path = PathIn(dataDirectory);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteStartArray("arguments");
                foreach (var argument in arguments) writer.WriteStringValue(argument);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the convenience does not stop the service; start --all falls back to defaults.
        }
    }

    /// <summary>The remembered options, or none when the file is absent or malformed.</summary>
    public static IReadOnlyList<string> Read(string dataDirectory)
    {
        try
        {
            var path = PathIn(dataDirectory);
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Array) return [];
            var list = new List<string>();
            foreach (var item in arguments.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text) list.Add(text);
            return list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
