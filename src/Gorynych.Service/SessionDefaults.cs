using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Service;

/// <summary>One native session config option Gorynych sets on a new session: an opaque ID and value.</summary>
internal sealed record SessionConfigChoice(string Id, string Value);

/// <summary>
/// The shape of a <c>session-defaults.json</c> map of native config option IDs to string values, and
/// the shape of a client's or spawn's explicit <c>configOptions</c> map. IDs and values are opaque:
/// DSH's model is a JSON-encoded provider/model pair, and Kimi's thinking level is a plain string.
/// Whether a choice is a model or a model-related option is decided from the agent's advertised
/// option (its <c>category</c>), never from its ID.
/// </summary>
internal static class SessionConfigOptions
{
    /// <summary>The categories Gorynych may set here: model and model-related options only.</summary>
    public static readonly IReadOnlySet<string> AllowedCategories =
        new HashSet<string>(["model", "model_config", "thought_level"], StringComparer.Ordinal);

    /// <summary>
    /// Reads a map of option IDs to string values. A wrong shape, a non-string value or an empty ID
    /// is a <see cref="FormatException"/>: the caller decides whether that is a bad request, a bad
    /// spawn argument or a bad service configuration file.
    /// </summary>
    public static List<SessionConfigChoice> Parse(JsonNode? node, string what)
    {
        if (node is not JsonObject map)
            throw new FormatException($"{what} must be a JSON object of native config option IDs to string values.");
        var choices = new List<SessionConfigChoice>();
        foreach (var (id, value) in map)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new FormatException($"{what}: a config option ID must not be empty.");
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
                throw new FormatException($"{what}: the value of \"{id}\" must be a string, one of the agent's advertised value IDs.");
            choices.Add(new SessionConfigChoice(id, text));
        }
        return choices;
    }

    /// <summary>A map for a JSON result or a card, in the order the choices were given.</summary>
    public static JsonObject ToObject(IEnumerable<SessionConfigChoice> choices)
    {
        var map = new JsonObject();
        foreach (var choice in choices) map[choice.Id] = choice.Value;
        return map;
    }
}

/// <summary>
/// Per-agent defaults for new sessions, read from <c>session-defaults.json</c> in the service's
/// data folder. They belong to Gorynych's own data, never to a harness's native configuration
/// directory, and are applied only when a session is created: never on load or resume.
/// <para>
/// The file has one shape:
/// <code>
/// {
///   "agents": {
///     "dsh": {
///       "configOptions": {
///         "model": "[\"deepseek-official\",\"deepseek-flash\"]",
///         "reasoning_effort": "high"
///       }
///     }
///   }
/// }
/// </code>
/// A missing file means no defaults. A malformed one stops the service at start with the file and
/// what is wrong in the message, rather than silently ignoring a person's configuration.
/// </para>
/// </summary>
internal sealed class SessionDefaults
{
    public const string FileName = "session-defaults.json";

    static readonly IReadOnlyList<SessionConfigChoice> None = [];

    readonly Dictionary<string, IReadOnlyList<SessionConfigChoice>> agents;

    SessionDefaults(Dictionary<string, IReadOnlyList<SessionConfigChoice>> agents) => this.agents = agents;

    /// <summary>No defaults: every new session starts with its harness's own choices.</summary>
    public static SessionDefaults Empty { get; } = new(new(StringComparer.Ordinal));

    /// <summary>The defaults for an adapter, or none.</summary>
    public IReadOnlyList<SessionConfigChoice> For(string agent) => agents.GetValueOrDefault(agent) ?? None;

    /// <summary>Reads the defaults file in <paramref name="dataDirectory"/>; a missing file is none.</summary>
    /// <exception cref="InvalidDataException">The file cannot be read, or is not valid for this shape.</exception>
    public static SessionDefaults Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path)) return Empty;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{path}: cannot be read: {e.Message}", e);
        }
        return Parse(text, path);
    }

    /// <summary>Reads defaults from JSON text, naming <paramref name="source"/> in every error.</summary>
    /// <exception cref="InvalidDataException">The text is not JSON, or not valid for this shape.</exception>
    public static SessionDefaults Parse(string text, string source = FileName)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{source}: not JSON: {e.Message}", e);
        }
        return FromNode(root, source);
    }

    static SessionDefaults FromNode(JsonNode? root, string source)
    {
        if (root is not JsonObject file)
            throw new InvalidDataException($"{source}: the root must be a JSON object with an \"agents\" object.");
        foreach (var (key, _) in file)
            if (key != "agents")
                throw new InvalidDataException($"{source}: unknown key \"{key}\"; only \"agents\" is allowed.");
        if (file["agents"] is null) return Empty;
        if (file["agents"] is not JsonObject agents)
            throw new InvalidDataException($"{source}: \"agents\" must be an object keyed by adapter name.");
        var parsed = new Dictionary<string, IReadOnlyList<SessionConfigChoice>>(StringComparer.Ordinal);
        foreach (var (agent, value) in agents)
        {
            var where = $"{source}: agents.{agent}";
            if (string.IsNullOrWhiteSpace(agent))
                throw new InvalidDataException($"{source}: an adapter name must not be empty.");
            if (value is not JsonObject entry)
                throw new InvalidDataException($"{where} must be an object with \"configOptions\".");
            foreach (var (key, _) in entry)
                if (key != "configOptions")
                    throw new InvalidDataException($"{where}: unknown key \"{key}\"; only \"configOptions\" is allowed.");
            if (entry["configOptions"] is null)
            {
                parsed[agent] = None;
                continue;
            }
            try
            {
                parsed[agent] = SessionConfigOptions.Parse(entry["configOptions"], $"{where}.configOptions");
            }
            catch (FormatException e)
            {
                throw new InvalidDataException(e.Message, e);
            }
        }
        return new SessionDefaults(parsed);
    }
}
