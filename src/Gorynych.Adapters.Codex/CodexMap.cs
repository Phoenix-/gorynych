using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Gorynych.Acp;
using Gorynych.Adapters.Codex.Protocol;

namespace Gorynych.Adapters.Codex;

/// <summary>Translations between Codex app-server items and settings and ACP (docs/adapters.md, "Codex").</summary>
static class CodexMap
{
    // ---------------------------------------------------------------- wire

    public static JsonNode ToNode<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToNode(value, info)!;

    public static T From<T>(JsonNode? node, JsonTypeInfo<T> info) =>
        node.Deserialize(info) ?? throw new JsonException($"{typeof(T).Name}: missing");

    // ---------------------------------------------------------------- items

    /// <summary>A tool-like item as an ACP tool call, when it starts or when history replays it; null for messages and items shown no other way.</summary>
    public static ToolCall? ToolCallOf(ThreadItem item, bool done)
    {
        var call = item switch
        {
            CommandExecutionThreadItem c => new ToolCall
            {
                ToolCallId = c.Id,
                Title = CommandTitle(c),
                Kind = CommandKind(c),
                Status = done ? StatusOf(c.Status.Value) : ToolCallStatus.InProgress,
                RawInput = new JsonObject { ["command"] = c.Command, ["cwd"] = c.Cwd },
                Locations = [new ToolCallLocation { Path = c.Cwd }],
            },
            FileChangeThreadItem f => new ToolCall
            {
                ToolCallId = f.Id,
                Title = FileChangeTitle(f),
                Kind = ToolKind.Edit,
                Status = done ? StatusOf(f.Status.Value) : ToolCallStatus.InProgress,
                Locations = f.Changes.Select(ch => new ToolCallLocation { Path = ch.Path }).ToList(),
                Content = [Text(FileChangeText(f))],
            },
            McpToolCallThreadItem m => new ToolCall
            {
                ToolCallId = m.Id,
                Title = McpTitle(m.Server, m.Tool),
                Kind = ToolKind.Other,
                Status = done ? StatusOf(m.Status.Value) : ToolCallStatus.InProgress,
                RawInput = m.Arguments?.DeepClone(),
            },
            WebSearchThreadItem w => new ToolCall
            {
                ToolCallId = w.Id, Title = $"Search the web: {w.Query}", Kind = ToolKind.Fetch,
                Status = done ? ToolCallStatus.Completed : ToolCallStatus.InProgress,
            },
            ImageViewThreadItem v => new ToolCall
            {
                ToolCallId = v.Id, Title = $"View {Path.GetFileName(v.Path)}", Kind = ToolKind.Read,
                Status = done ? ToolCallStatus.Completed : ToolCallStatus.InProgress, Locations = [new ToolCallLocation { Path = v.Path }],
            },
            DynamicToolCallThreadItem d => new ToolCall
            {
                ToolCallId = d.Id, Title = d.Tool, Kind = ToolKind.Other,
                Status = done ? StatusOf(d.Status.Value) : ToolCallStatus.InProgress, RawInput = d.Arguments?.DeepClone(),
            },
            CollabAgentToolCallThreadItem a => new ToolCall
            {
                ToolCallId = a.Id, Title = "Codex sub-agent", Kind = ToolKind.Other,
                Status = done ? ToolCallStatus.Completed : ToolCallStatus.InProgress,
            },
            ImageGenerationThreadItem g => new ToolCall
            {
                ToolCallId = g.Id, Title = "Generate an image", Kind = ToolKind.Other,
                Status = done ? ToolCallStatus.Completed : ToolCallStatus.InProgress,
            },
            _ => null,
        };
        if (call is not null && done) call.Content = ResultContent(item) ?? call.Content;
        return call;
    }

    /// <summary>The end of a tool-like item, as an ACP tool call update; null for any other item.</summary>
    public static SessionUpdateToolCallUpdate? ToolCallDoneOf(ThreadItem item)
    {
        if (ToolCallOf(item, done: true) is not { } call) return null;
        var update = new SessionUpdateToolCallUpdate { ToolCallId = call.ToolCallId, Status = call.Status, Content = call.Content };
        if (item is CommandExecutionThreadItem c)
            update.RawOutput = new JsonObject { ["exitCode"] = c.ExitCode, ["output"] = c.AggregatedOutput };
        if (item is McpToolCallThreadItem { Result: { } r }) update.RawOutput = ToNode(r, CodexJsonContext.Default.McpToolCallResult);
        return update;
    }

    static List<ToolCallContent>? ResultContent(ThreadItem item) => item switch
    {
        CommandExecutionThreadItem { AggregatedOutput: { Length: > 0 } output } => [Text(Truncate(output, 8000))],
        FileChangeThreadItem f => [Text(FileChangeText(f))],
        McpToolCallThreadItem { Error: { } e } => [Text(e.Message)],
        McpToolCallThreadItem { Result: { } r } => McpResultText(r) is { Length: > 0 } t ? [Text(Truncate(t, 8000))] : null,
        _ => null,
    };

    static string McpResultText(McpToolCallResult r) =>
        string.Join("\n", r.Content.Select(c => (string?)c?["text"]).Where(t => !string.IsNullOrEmpty(t)));

    /// <summary>How an MCP tool is named in a tool call and its approval: <c>&lt;tool&gt; (&lt;server&gt;)</c>, as Claude's titles are.</summary>
    public static string McpTitle(string server, string tool) => $"{tool} ({server})";

    static string CommandTitle(CommandExecutionThreadItem c) => CommandTitle(c.Command, c.CommandActions);

    /// <summary>A command's title from what Codex parsed of it; its approval request carries the same as its item.</summary>
    public static string CommandTitle(string command, List<CommandAction> actions)
    {
        if (actions.Count == 1)
        {
            switch (actions[0])
            {
                case ReadCommandAction r: return $"Read {r.Name}";
                case ListFilesCommandAction l: return l.Path is { Length: > 0 } p ? $"List {p}" : "List files";
                case SearchCommandAction s: return s.Query is { Length: > 0 } q ? $"Search {q}" + (s.Path is { Length: > 0 } sp ? $" in {sp}" : "") : "Search";
            }
        }
        var commands = actions.Select(CommandOf).Where(x => x.Length > 0).ToList();
        return commands.Count > 0 ? string.Join(" && ", commands) : command;
    }

    static string CommandOf(CommandAction a) => a switch
    {
        ReadCommandAction r => r.Command,
        ListFilesCommandAction l => l.Command,
        SearchCommandAction s => s.Command,
        UnknownCommandAction u => u.Command,
        _ => "",
    };

    static ToolKind CommandKind(CommandExecutionThreadItem c) => CommandKind(c.CommandActions);

    public static ToolKind CommandKind(List<CommandAction> actions) =>
        actions.Count > 0 && actions.All(a => a is ReadCommandAction) ? ToolKind.Read
        : actions.Count > 0 && actions.All(a => a is ReadCommandAction or ListFilesCommandAction or SearchCommandAction) ? ToolKind.Search
        : ToolKind.Execute;

    public static string FileChangeTitle(FileChangeThreadItem f) => f.Changes.Count switch
    {
        0 => "Edit files",
        1 => $"{ChangeVerb(f.Changes[0])} {Path.GetFileName(f.Changes[0].Path)}",
        _ => $"Edit {f.Changes.Count} files",
    };

    static string ChangeVerb(FileUpdateChange ch) => ch.Kind switch
    {
        AddPatchChangeKind => "Create",
        DeletePatchChangeKind => "Delete",
        _ => "Edit",
    };

    /// <summary>A file change as text: each file and its diff (for a new file, its content).</summary>
    static string FileChangeText(FileChangeThreadItem f)
    {
        var b = new StringBuilder();
        foreach (var ch in f.Changes)
            b.Append(ChangeVerb(ch)).Append(' ').Append(ch.Path).Append("\n```diff\n").Append(Truncate(ch.Diff, 8000).TrimEnd('\n')).Append("\n```\n");
        return b.ToString().TrimEnd();
    }

    public static ToolCallStatus StatusOf(string status) => status switch
    {
        "completed" => ToolCallStatus.Completed,
        "failed" or "declined" => ToolCallStatus.Failed,
        "inProgress" => ToolCallStatus.InProgress,
        _ => ToolCallStatus.Pending,
    };

    static Content Text(string text) => new() { ContentValue = new TextContent { Text = text } };

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";

    /// <summary>A user message's text: its text inputs, other inputs as a mark.</summary>
    public static string UserText(UserMessageThreadItem u) => string.Join("\n\n", u.Content.Select(i => i switch
    {
        TextUserInput t => t.Text,
        MentionUserInput m => $"@{m.Path}",
        SkillUserInput s => $"${s.Name}",
        _ => $"[{i.Tag}]",
    }));

    /// <summary>A reasoning item's text: its summary, or else its raw content.</summary>
    public static string ReasoningText(ReasoningThreadItem r) =>
        string.Join("\n\n", (r.Summary is { Count: > 0 } s ? s : r.Content ?? []).Where(x => x.Length > 0));

    /// <summary>Unix seconds as ACP's ISO 8601.</summary>
    public static string Iso(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("O", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- approvals

    /// <summary>
    /// The options of a command or file change approval: only decisions that keep nothing beyond
    /// the session. Codex's exec-policy and network amendments are written to the user's own
    /// Codex rules (<c>~/.codex/rules/default.rules</c>), so they are never offered.
    /// </summary>
    public static List<PermissionOption> DecisionOptions(IEnumerable<string> available)
    {
        var options = new List<PermissionOption>();
        var set = available.ToHashSet(StringComparer.Ordinal);
        if (set.Contains("accept")) options.Add(Option("accept", "Allow", PermissionOptionKind.AllowOnce));
        if (set.Contains("acceptForSession")) options.Add(Option("acceptForSession", "Allow for this session", PermissionOptionKind.AllowAlways));
        if (set.Contains("decline")) options.Add(Option("decline", "Deny", PermissionOptionKind.RejectOnce));
        if (set.Contains("cancel")) options.Add(Option("cancel", "Deny and stop the turn", PermissionOptionKind.RejectOnce));
        return options;
    }

    /// <summary>The plain decisions of a command approval's <c>availableDecisions</c> (all four when it has none).</summary>
    public static IEnumerable<string> PlainDecisions(JsonArray? available) =>
        available is null ? ["accept", "acceptForSession", "decline", "cancel"]
        : available.Select(d => d is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>();

    public static PermissionOption Option(string id, string name, PermissionOptionKind kind) => new() { OptionId = id, Name = name, Kind = kind };

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// A thread's settings as Codex reports them. The approval policy and the sandbox are kept as
    /// Codex's own values (a granular policy object, a sandbox with its writable roots and network
    /// access), so that whatever goes back to Codex is what it gave.
    /// </summary>
    public sealed record CodexSettings(string? Model, string? Effort, JsonNode? ApprovalPolicy, SandboxPolicy? Sandbox, string Reviewer)
    {
        /// <summary>The approval policy's name; <c>granular</c> for Codex's per-kind policy object.</summary>
        public string Policy => ApprovalPolicy is JsonValue v && v.TryGetValue<string>(out var p) ? p : "granular";

        public string SandboxType => Sandbox?.Tag ?? "unknown";
    }

    /// <summary>
    /// What a client changed since a thread's last turn, sent with the next one: only these fields
    /// go to Codex, and a mode sends its approval policy, sandbox and reviewer.
    /// </summary>
    public sealed record CodexChange(string? Model = null, string? Effort = null, CodexMode? Mode = null)
    {
        public bool IsEmpty => Model is null && Effort is null && Mode is null;
    }

    public sealed record CodexMode(string Id, string Name, string Description, string Policy, string Sandbox, string Reviewer);

    public static CodexSettings SettingsOf(string model, string? effort, JsonNode? approvalPolicy, SandboxPolicy? sandbox, ApprovalsReviewer reviewer) =>
        new(model, effort, approvalPolicy?.DeepClone(), sandbox, reviewer.Value ?? "user");

    /// <summary>
    /// Codex's permission presets, as the <c>mode</c> option: approval policy, sandbox and approval
    /// reviewer. "Approve for me" is Codex's automatic reviewer (<c>approvalsReviewer: auto_review</c>),
    /// a per-thread setting of app-server.
    /// </summary>
    public static readonly CodexMode[] Modes =
    [
        new("read-only", "Read only", "Codex reads files and asks before anything else.", "on-request", "readOnly", "user"),
        new("auto", "Auto", "Codex works in this folder and asks before going outside it.", "on-request", "workspaceWrite", "user"),
        new("auto-review", "Approve for me", "As Auto, but Codex's automatic reviewer answers its questions instead of you.", "on-request", "workspaceWrite", "auto_review"),
        new("full-access", "Full access", "Codex runs anything without asking, outside this folder too.", "never", "dangerFullAccess", "user"),
    ];

    public static string ModeOf(CodexSettings s) =>
        Modes.FirstOrDefault(m => m.Policy == s.Policy && m.Sandbox == s.SandboxType && m.Reviewer == s.Reviewer)?.Id ?? "custom";

    /// <summary>Codex's default sandbox of a type, or null for a type the presets do not use.</summary>
    public static SandboxPolicy? SandboxOf(string type) => type switch
    {
        "readOnly" => new ReadOnlySandboxPolicy(),
        "workspaceWrite" => new WorkspaceWriteSandboxPolicy(),
        "dangerFullAccess" => new DangerFullAccessSandboxPolicy(),
        _ => null,
    };

    /// <summary>The ACP config options of a thread: its permission mode, model and reasoning effort.</summary>
    public static List<SessionConfigOption> ConfigOptions(CodexSettings s, IReadOnlyList<Model> models)
    {
        var mode = ModeOf(s);
        var modes = Modes.Select(m => new SessionConfigSelectOption { Value = m.Id, Name = m.Name, Description = m.Description }).ToList();
        if (mode == "custom")
            modes.Add(new SessionConfigSelectOption
            {
                Value = "custom", Name = "Your Codex settings",
                Description = $"approval {s.Policy}, sandbox {s.SandboxType}, reviewer {s.Reviewer}",
            });
        var options = new List<SessionConfigOption>
        {
            Select("mode", "Permissions", SessionConfigOptionCategory.Mode, mode, modes),
        };
        var visible = models.Where(m => !m.Hidden).ToList();
        var modelOptions = visible.Select(m => new SessionConfigSelectOption { Value = m.Id, Name = m.DisplayName, Description = m.Description }).ToList();
        if (s.Model is { } current && modelOptions.All(o => o.Value != current))
            modelOptions.Add(new SessionConfigSelectOption { Value = current, Name = current });
        if (modelOptions.Count > 0 && s.Model is not null)
            options.Add(Select("model", "Model", SessionConfigOptionCategory.Model, s.Model, modelOptions));
        var model = models.FirstOrDefault(m => m.Id == s.Model);
        var efforts = (model?.SupportedReasoningEfforts ?? []).Select(e => new SessionConfigSelectOption
        {
            Value = e.ReasoningEffort, Name = e.ReasoningEffort, Description = e.Description,
        }).ToList();
        var effort = s.Effort ?? model?.DefaultReasoningEffort;
        if (effort is not null && efforts.All(o => o.Value != effort)) efforts.Add(new SessionConfigSelectOption { Value = effort, Name = effort });
        if (efforts.Count > 0 && effort is not null)
            options.Add(Select("reasoning_effort", "Reasoning effort", SessionConfigOptionCategory.ThoughtLevel, effort, efforts));
        return options;
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
