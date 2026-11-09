using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Adapters.Claude;

/// <summary>A model Claude Code offers, from the <c>models</c> of its <c>initialize</c> answer.</summary>
public sealed record ClaudeModel(string Value, string? ResolvedModel, string Name, string? Description, IReadOnlyList<string> EffortLevels, bool SupportsAutoMode);

/// <summary>What one session runs with: the person's choices (null: Claude's own default) and what Claude last reported.</summary>
public sealed class ClaudeSettings
{
    /// <summary>The model the person picked, as one of the catalog's values.</summary>
    public string? Model { get; set; }

    /// <summary>The effort level the person picked; null leaves Claude's default.</summary>
    public string? Effort { get; set; }

    /// <summary>The permission mode the person picked.</summary>
    public string? Mode { get; set; }

    /// <summary>The model Claude reported in its last <c>system/init</c> (a resolved ID such as <c>claude-opus-5-5</c>).</summary>
    public string? ReportedModel { get; set; }

    /// <summary>The permission mode Claude reported last.</summary>
    public string? ReportedMode { get; set; }

    public ClaudeSettings Clone() => (ClaudeSettings)MemberwiseClone();

    /// <summary>Takes the person's choices of <paramref name="other"/>.</summary>
    public void CopyChoicesFrom(ClaudeSettings other)
    {
        Model = other.Model;
        Effort = other.Effort;
        Mode = other.Mode;
    }
}

/// <summary>
/// The ACP config options of a headless Claude session (docs/adapters.md, "Modes, models and
/// effort"): <c>mode</c>, <c>model</c> and <c>reasoning_effort</c>, built from Claude's model catalog.
/// </summary>
public static class ClaudeOptions
{
    public const string DefaultEffort = "default";

    /// <summary>
    /// Claude's <c>bypassPermissions</c>, offered as "Full access": only ever the person's own pick for
    /// one session, never a default. Claude cannot enter it in a process started without it, so the
    /// adapter starts Claude again for it (docs/adapters.md, "Full access mode").
    /// </summary>
    public const string FullAccess = "bypassPermissions";

    /// <summary>The permission modes offered, in order; <c>auto</c> only for a model that supports it.</summary>
    public static readonly (string Id, string Name, string Description)[] Modes =
    [
        ("default", "Ask", "Claude asks before it edits files or runs commands"),
        ("acceptEdits", "Accept edits", "File edits go ahead; commands still ask"),
        ("plan", "Plan", "Claude plans and changes nothing"),
        ("auto", "Auto", "A classifier approves most actions instead of you"),
        (FullAccess, "Full access", "Claude asks nothing at all: it runs any command and changes any file, outside this folder too"),
    ];

    /// <summary>The mode a launch takes when the person picked none: never Full access, whatever the adapter was given.</summary>
    public static string DefaultMode(string configured) => configured == FullAccess ? "default" : configured;

    /// <summary>Reads the <c>models</c> of an <c>initialize</c> answer.</summary>
    public static List<ClaudeModel> Catalog(JsonObject? initialize) =>
        (initialize?["models"] as JsonArray)?.OfType<JsonObject>()
            .Where(m => (string?)m["value"] is { Length: > 0 })
            .Select(m => new ClaudeModel(
                (string)m["value"]!,
                (string?)m["resolvedModel"],
                (string?)m["displayName"] ?? (string)m["value"]!,
                (string?)m["description"],
                (m["supportedEffortLevels"] as JsonArray)?.Select(e => (string?)e).OfType<string>().ToList() ?? [],
                (bool?)m["supportsAutoMode"] == true))
            .ToList() ?? [];

    /// <summary>
    /// The catalog entry the session runs on: the person's pick (none when the catalog no longer has
    /// it: the session still runs on it), else the one Claude reports, else <c>default</c>.
    /// </summary>
    public static ClaudeModel? Current(ClaudeSettings s, IReadOnlyList<ClaudeModel> catalog) =>
        s.Model is { } chosen ? catalog.FirstOrDefault(m => m.Value == chosen)
        : catalog.FirstOrDefault(m => m.Value == "default" && (s.ReportedModel is null || m.ResolvedModel == s.ReportedModel))
        ?? (s.ReportedModel is { } reported ? catalog.FirstOrDefault(m => m.ResolvedModel == reported || m.Value == reported) : null)
        ?? catalog.FirstOrDefault(m => m.Value == "default");

    public static string CurrentMode(ClaudeSettings s) => s.Mode ?? s.ReportedMode ?? "default";

    public static List<SessionConfigOption> ConfigOptions(ClaudeSettings s, IReadOnlyList<ClaudeModel> catalog)
    {
        var options = new List<SessionConfigOption>();
        var model = Current(s, catalog);
        var mode = CurrentMode(s);
        var modes = Modes.Where(m => m.Id != "auto" || model?.SupportsAutoMode == true || mode == "auto")
            .Select(m => new SessionConfigSelectOption { Value = m.Id, Name = m.Name, Description = m.Description }).ToList();
        if (modes.All(m => m.Value != mode)) modes.Add(new SessionConfigSelectOption { Value = mode, Name = mode, Description = "Set in your Claude settings" });
        options.Add(Select("mode", "Permissions", SessionConfigOptionCategory.Mode, mode, modes));
        if (catalog.Count == 0) return options;

        var models = catalog.Select(m => new SessionConfigSelectOption { Value = m.Value, Name = m.Name, Description = m.Description }).ToList();
        var currentModel = model?.Value ?? s.Model ?? s.ReportedModel;
        if (currentModel is not null)
        {
            if (models.All(m => m.Value != currentModel)) models.Add(new SessionConfigSelectOption { Value = currentModel, Name = currentModel });
            options.Add(Select("model", "Model", SessionConfigOptionCategory.Model, currentModel, models));
        }
        if (model is { EffortLevels.Count: > 0 })
        {
            var efforts = new List<SessionConfigSelectOption> { new() { Value = DefaultEffort, Name = "Default", Description = "Claude's own default" } };
            efforts.AddRange(model.EffortLevels.Select(e => new SessionConfigSelectOption { Value = e, Name = e }));
            var effort = s.Effort ?? DefaultEffort;
            if (efforts.All(e => e.Value != effort)) efforts.Add(new SessionConfigSelectOption { Value = effort, Name = effort });
            options.Add(Select("reasoning_effort", "Reasoning effort", SessionConfigOptionCategory.ThoughtLevel, effort, efforts));
        }
        return options;
    }

    /// <summary>
    /// Applies a <c>session/set_config_option</c> to <paramref name="s"/>, checked against the catalog,
    /// and returns the control request that applies it to a running Claude.
    /// </summary>
    public static JsonObject Change(ClaudeSettings s, IReadOnlyList<ClaudeModel> catalog, string configId, string value)
    {
        switch (configId)
        {
            case "mode":
                if (Modes.All(m => m.Id != value) && value != CurrentMode(s))
                    throw new JsonRpcException(JsonRpcException.InvalidParams, $"No mode {value}");
                s.Mode = value;
                return new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = value };
            case "model":
                if (catalog.Count > 0 && catalog.All(m => m.Value != value))
                    throw new JsonRpcException(JsonRpcException.InvalidParams, $"Claude Code has no model {value}");
                s.Model = value;
                // An effort the new model does not have goes back to the default.
                if (s.Effort is { } effort && catalog.FirstOrDefault(m => m.Value == value) is { } next && !next.EffortLevels.Contains(effort))
                    s.Effort = null;
                return new JsonObject { ["subtype"] = "set_model", ["model"] = value };
            case "reasoning_effort":
                if (value != DefaultEffort && Current(s, catalog) is { EffortLevels.Count: > 0 } current && !current.EffortLevels.Contains(value))
                    throw new JsonRpcException(JsonRpcException.InvalidParams, $"{current.Name} has no reasoning effort {value}");
                s.Effort = value == DefaultEffort ? null : value;
                return new JsonObject
                {
                    ["subtype"] = "apply_flag_settings",
                    ["settings"] = new JsonObject { ["effortLevel"] = s.Effort is null ? null : JsonValue.Create(s.Effort) },
                };
            default:
                throw new JsonRpcException(JsonRpcException.InvalidParams, $"No option {configId}");
        }
    }

    /// <summary>The launch flags that carry the person's choices into a new process.</summary>
    public static IEnumerable<string> LaunchArgs(ClaudeSettings s, string defaultMode, string? defaultModel)
    {
        yield return "--permission-mode";
        yield return s.Mode ?? DefaultMode(defaultMode);
        if ((s.Model ?? defaultModel) is { Length: > 0 } model)
        {
            yield return "--model";
            yield return model;
        }
        if (s.Effort is { Length: > 0 } effort)
        {
            yield return "--effort";
            yield return effort;
        }
    }

    static SessionConfigSelect Select(string id, string name, SessionConfigOptionCategory category, string current, List<SessionConfigSelectOption> values) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        CurrentValue = current,
        Options = new JsonArray(values.Select(v => (JsonNode?)AcpJson.ToNode(v)).ToArray()),
    };
}
