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
    string? stateToWrite; // the state to write next, under the gate
    Task stateWriter = Task.CompletedTask; // the writer, while it runs

    /// <summary>
    /// Under the gate: what the face kept of each agent last time, if anything. A file it cannot read, or that is not
    /// as it writes one, is none: the face starts as if fresh, and writes it anew on the next change.
    /// </summary>
    void LoadState()
    {
        if (options.StateFile is not { } file || !File.Exists(file)) return;
        var models = new Dictionary<string, List<AhpConfig.Choice>>(StringComparer.Ordinal);
        var configs = new Dictionary<string, SessionConfigState>(StringComparer.Ordinal);
        try
        {
            foreach (var (agent, node) in JsonNode.Parse(File.ReadAllText(file))?["agents"] as JsonObject ?? throw new FormatException("no agents"))
            {
                if (node is not JsonObject kept) throw new FormatException($"{agent}: not an object");
                if (kept["models"] is JsonArray seen)
                    models[agent] = [.. seen.Select(m => m as JsonObject is { } o && Text(o["id"]) is { Length: > 0 } id
                        ? new AhpConfig.Choice(id, Text(o["name"]) ?? id, Text(o["description"]))
                        : throw new FormatException($"{agent}: a model with no ID"))];
                if (kept["schema"] is not JsonObject schema) continue;
                var parsed = Json.Deserialize<SessionConfigSchema>(AhpNames.Element(schema));
                if (parsed?.Properties is not { } properties || properties.Values.Any(p => p is null || p.Type is null))
                    throw new FormatException($"{agent}: an options schema the face does not write");
                configs[agent] = new SessionConfigState { Schema = parsed, Values = new(StringComparer.Ordinal) };
            }
        }
        catch (Exception e)
        {
            // Whatever it is (not JSON, a key twice, a field missing), it is no state of the face's: none is.
            ServiceLog.Warn($"ahp: {file} is not one the face can read; it starts without it: {e.Message}");
            return;
        }
        foreach (var (agent, seen) in models) agentModels[agent] = seen;
        foreach (var (agent, config) in configs)
        {
            agentConfigs[agent] = config;
            savedSchemas[agent] = Json.SerializeToElement(config.Schema).GetRawText();
        }
    }

    /// <summary>
    /// Under the gate: each agent's models and options schema, to be written for the next start, off the gate and at
    /// most every <see cref="AhpFaceOptions.StateWriteDelay"/>: Claude's options change with its model, so a model
    /// switch changes them each time.
    /// </summary>
    void SaveState()
    {
        if (options.StateFile is null) return;
        var agents = new JsonObject();
        foreach (var agent in agentModels.Keys.Union(agentConfigs.Keys).Order(StringComparer.Ordinal))
        {
            var kept = new JsonObject();
            if (agentModels.TryGetValue(agent, out var models))
                kept["models"] = new JsonArray([.. models.Select(m => (JsonNode?)new JsonObject { ["id"] = m.Value, ["name"] = m.Name, ["description"] = m.Description })]);
            if (agentConfigs.TryGetValue(agent, out var config)) kept["schema"] = JsonNode.Parse(Json.SerializeToElement(config.Schema).GetRawText());
            agents[agent] = kept;
        }
        var first = stateToWrite is null && stateWriter.IsCompleted;
        stateToWrite = new JsonObject { ["version"] = 1, ["agents"] = agents }.ToJsonString();
        if (first) stateWriter = Task.Run(WriteStateAsync);
    }

    /// <summary>Writes the latest state, a while after the first change, then any that came while it wrote.</summary>
    async Task WriteStateAsync()
    {
        var file = options.StateFile!;
        try { await Task.Delay(options.StateWriteDelay, stopping.Token); }
        catch (OperationCanceledException) { } // stopping: written now
        while (true)
        {
            string? json;
            lock (gate)
            {
                json = stateToWrite;
                stateToWrite = null;
            }
            if (json is null) return;
            try
            {
                var written = file + ".tmp";
                File.WriteAllText(written, json);
                File.Move(written, file, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ServiceLog.Warn($"ahp: {file} could not be written: {e.Message}");
            }
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
