using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AgentHostProtocol;

namespace Gorynych.Service.Ahp;

/// <summary>
/// An agent's options as AHP has them (docs/ahp-face.md, "Configuration"): its ACP config options as the session's
/// config (<see cref="SessionConfigState"/>), except the model, which is AHP's own (<c>AgentInfo.models</c> and
/// <c>Message.model</c>); with no config options, its ACP modes as one property.
/// </summary>
internal static class AhpConfig
{
    /// <summary>The property a session's ACP modes become when no config option already says its mode.</summary>
    public const string ModeKey = "mode";

    public sealed record Choice(string Value, string Name, string? Description);

    /// <summary>The agent's model option: its ID, the model now, and the models it offers.</summary>
    public sealed record ModelOption(string ConfigId, string Current, List<Choice> Models);

    static string? Text(JsonNode? node) => AhpNames.Text(node);

    static bool IsModel(JsonObject option) => Text(option["category"]) == "model";

    /// <summary>The choices of an ACP select option, its groups flattened.</summary>
    public static List<Choice> Choices(JsonObject option)
    {
        var choices = new List<Choice>();
        void Add(JsonNode? list)
        {
            foreach (var item in (list as JsonArray ?? []).OfType<JsonObject>())
                if (Text(item["value"]) is { } value) choices.Add(new Choice(value, Text(item["name"]) ?? value, Text(item["description"])));
                else if (item["options"] is JsonArray nested) Add(nested);
        }
        Add(option["options"]);
        return choices;
    }

    static JsonElement Value(string text) => AhpNames.Element(JsonValue.Create(text));

    static JsonElement Value(bool flag) => AhpNames.Element(JsonValue.Create(flag));

    /// <summary>
    /// The session's config: each select or boolean config option but the model, keyed by its ACP ID, which a
    /// <c>session/configChanged</c> sets back with <c>session/set_config_option</c>; with no option of category
    /// <c>mode</c>, the ACP modes as <see cref="ModeKey"/>, set back with <c>session/set_mode</c>. Null when there is
    /// nothing to choose.
    /// </summary>
    public static SessionConfigState? Of(JsonArray? options, JsonObject? modes, out bool modesAsMode)
    {
        modesAsMode = false;
        var properties = new Dictionary<string, SessionConfigPropertySchema>(StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var hasMode = false;
        foreach (var option in (options ?? []).OfType<JsonObject>())
        {
            if (Text(option["id"]) is not { Length: > 0 } id || IsModel(option)) continue;
            hasMode |= Text(option["category"]) == "mode";
            switch (Text(option["type"]))
            {
                case "select":
                    var choices = Choices(option);
                    if (choices.Count == 0) continue;
                    properties[id] = new SessionConfigPropertySchema
                    {
                        Type = "string", Title = Text(option["name"]) ?? id, Description = Text(option["description"]),
                        Enum = [.. choices.Select(c => Value(c.Value))], EnumLabels = [.. choices.Select(c => c.Name)],
                        EnumDescriptions = choices.Any(c => c.Description is not null) ? [.. choices.Select(c => c.Description ?? "")] : null,
                        SessionMutable = true,
                    };
                    if (Text(option["currentValue"]) is { } current) values[id] = Value(current);
                    break;
                case "boolean":
                    properties[id] = new SessionConfigPropertySchema
                    {
                        Type = "boolean", Title = Text(option["name"]) ?? id, Description = Text(option["description"]), SessionMutable = true,
                    };
                    if (option["currentValue"] is JsonValue b && b.TryGetValue<bool>(out var flag)) values[id] = Value(flag);
                    break;
            }
        }
        if (!hasMode && !properties.ContainsKey(ModeKey) && modes?["availableModes"] is JsonArray available && available.Count > 0)
        {
            var list = available.OfType<JsonObject>().Where(m => Text(m["id"]) is not null).ToList();
            properties[ModeKey] = new SessionConfigPropertySchema
            {
                Type = "string", Title = "Mode",
                Enum = [.. list.Select(m => Value(Text(m["id"])!))], EnumLabels = [.. list.Select(m => Text(m["name"]) ?? Text(m["id"])!)],
                EnumDescriptions = list.Any(m => Text(m["description"]) is not null) ? [.. list.Select(m => Text(m["description"]) ?? "")] : null,
                SessionMutable = true,
            };
            if (Text(modes["currentModeId"]) is { } mode) values[ModeKey] = Value(mode);
            modesAsMode = true;
        }
        return properties.Count == 0 ? null : new SessionConfigState
        {
            Schema = new SessionConfigSchema { Type = "object", Properties = properties },
            Values = values,
        };
    }

    /// <summary>The agent's model option (category <c>model</c>, a select); null when it has none.</summary>
    public static ModelOption? Model(JsonArray? options)
    {
        foreach (var option in (options ?? []).OfType<JsonObject>())
            if (IsModel(option) && Text(option["type"]) == "select" && Text(option["id"]) is { Length: > 0 } id && Choices(option) is { Count: > 0 } models)
                return new ModelOption(id, Text(option["currentValue"]) ?? "", models);
        return null;
    }

    /// <summary>Whether a value is one the property takes: one of its choices, or a boolean for a boolean one.</summary>
    public static bool Takes(SessionConfigState config, string key, JsonElement value)
    {
        if (!config.Schema.Properties.TryGetValue(key, out var property)) return false;
        return property.Type == "boolean"
            ? value.ValueKind is JsonValueKind.True or JsonValueKind.False
            : value.ValueKind == JsonValueKind.String && (property.Enum ?? []).Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == value.GetString());
    }

    /// <summary>Whether two values are the same JSON.</summary>
    public static bool Same(JsonElement? a, JsonElement? b) =>
        a is { } x && b is { } y ? x.GetRawText() == y.GetRawText() : a is null && b is null;
}
