using System.Text.Json.Nodes;

namespace Gorynych.Service;

/// <summary>The last configuration an ACP agent reported, independent of its conversation history.</summary>
internal sealed class SessionConfiguration
{
    readonly Lock gate = new();
    readonly JsonObject state = [];
    static readonly string[] Keys = ["configOptions", "modes", "models"];

    public void Capture(JsonNode? response)
    {
        if (response is not JsonObject o) return;
        lock (gate)
            foreach (var key in Keys)
                if (o[key] is { } value) state[key] = value.DeepClone();
    }

    public void Apply(JsonObject update)
    {
        switch ((string?)update["sessionUpdate"])
        {
            case "config_option_update":
                Capture(update);
                break;
            case "current_mode_update":
                SetCurrent("modes", "currentModeId", update["currentModeId"]);
                break;
        }
    }

    public void SetCurrent(string key, string field, JsonNode? value)
    {
        if (value is null) return;
        lock (gate)
            if (state[key] is JsonObject part) part[field] = value.DeepClone();
    }

    public JsonObject Snapshot()
    {
        lock (gate) return state.DeepClone().AsObject();
    }
}
