using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AgentHostProtocol;

namespace Gorynych.Service.Ahp;

/// <summary>
/// The face's own state across restarts (docs/ahp-face.md, "Root channel"): each agent's models and the schema of its
/// options, as the face last saw them in one of its sessions, so that the window can start a session of an agent the
/// face has not opened one of since it started.
/// </summary>
internal sealed partial class AhpFace
{
    /// <summary>
    /// The one model of an agent the face knows none of: the agent's own default. VS Code disables an agent with no
    /// models; this keeps it selectable. Not a value of any agent's model option, so the face never sets it on a session.
    /// </summary>
    internal const string AgentDefault = "gorynych/agent-default";

    readonly Dictionary<string, string> savedSchemas = new(StringComparer.Ordinal); // agent -> its schema as last saved

    /// <summary>Under the gate: what the face kept of each agent last time, if anything.</summary>
    void LoadState()
    {
        if (options.StateFile is not { } file || !File.Exists(file)) return;
        try
        {
            foreach (var (agent, node) in JsonNode.Parse(File.ReadAllText(file))?["agents"] as JsonObject ?? [])
            {
                if (node is not JsonObject kept) continue;
                if (kept["models"] is JsonArray models)
                    agentModels[agent] = [.. models.OfType<JsonObject>().Where(m => Text(m["id"]) is { Length: > 0 })
                        .Select(m => new AhpConfig.Choice(Text(m["id"])!, Text(m["name"]) ?? Text(m["id"])!, Text(m["description"])))];
                if (kept["schema"] is JsonObject schema && Json.Deserialize<SessionConfigSchema>(AhpNames.Element(schema)) is { } parsed)
                {
                    agentConfigs[agent] = new SessionConfigState { Schema = parsed, Values = new(StringComparer.Ordinal) };
                    savedSchemas[agent] = Json.SerializeToElement(parsed).GetRawText();
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            ServiceLog.Warn($"ahp: {file} could not be read: {e.Message}");
        }
    }

    /// <summary>Under the gate: each agent's models and options schema, written for the next start.</summary>
    void SaveState()
    {
        if (options.StateFile is not { } file) return;
        var agents = new JsonObject();
        foreach (var agent in agentModels.Keys.Union(agentConfigs.Keys).Order(StringComparer.Ordinal))
        {
            var kept = new JsonObject();
            if (agentModels.TryGetValue(agent, out var models))
                kept["models"] = new JsonArray([.. models.Select(m => (JsonNode?)new JsonObject { ["id"] = m.Value, ["name"] = m.Name, ["description"] = m.Description })]);
            if (agentConfigs.TryGetValue(agent, out var config)) kept["schema"] = JsonNode.Parse(Json.SerializeToElement(config.Schema).GetRawText());
            agents[agent] = kept;
        }
        try
        {
            var written = file + ".tmp";
            File.WriteAllText(written, new JsonObject { ["version"] = 1, ["agents"] = agents }.ToJsonString());
            File.Move(written, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"ahp: {file} could not be written: {e.Message}");
        }
    }

    /// <summary>Under the gate: an agent's options schema seen in a session; saved when it is another than the one saved.</summary>
    void SchemaSeen(string agent, SessionConfigSchema schema)
    {
        var json = Json.SerializeToElement(schema).GetRawText();
        if (savedSchemas.GetValueOrDefault(agent) == json) return;
        savedSchemas[agent] = json;
        SaveState();
    }

    /// <summary>An agent's models in <c>AgentInfo.models</c>: those seen, else the one that stands for its own default.</summary>
    List<SessionModelInfo> ModelInfos(string agent) =>
        agentModels.TryGetValue(agent, out var models) && models.Count > 0
            ? [.. models.Select(m => new SessionModelInfo
            {
                Id = m.Value, Provider = agent, Name = m.Name,
                Meta = m.Description is { } d ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["description"] = AhpNames.Element(JsonValue.Create(d)) } : null,
            })]
            : [new SessionModelInfo
            {
                Id = AgentDefault, Provider = agent, Name = "Agent's default",
                Meta = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["description"] = AhpNames.Element(JsonValue.Create("The model the agent picks itself; its own models show once a session of it is open")),
                },
            }];
}
