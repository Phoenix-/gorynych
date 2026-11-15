using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// Claude Code's native checklist as ACP v1 plan entries (docs/adapters.md, "Task plans"). Pure and
/// stateful: it sees the task tools' <c>tool_use</c> inputs and <c>tool_result</c> outputs in the
/// order the live stream or a transcript has them, and returns a complete replacement plan only
/// when a successful, applied change is seen. Task tools are <c>TaskCreate</c> and
/// <c>TaskUpdate</c> (Claude Code 2.1.268 and later by default, or opted in with
/// <c>CLAUDE_CODE_ENABLE_TODO_TOOLS=1</c>), or the legacy <c>TodoWrite</c> when
/// <c>CLAUDE_CODE_ENABLE_TASKS=0</c>. Task IDs are the native ones the results carry, never the
/// tool call IDs. A malformed payload changes nothing and throws nothing.
/// </summary>
internal sealed class ClaudeTaskPlan
{
    /// <summary>
    /// The most plan entries and pending tool calls one session keeps. A handful of interactive
    /// sessions have a handful of tasks; this only keeps a malformed or runaway stream bounded.
    /// </summary>
    public const int MaxEntries = 512;

    /// <summary>Whether a native tool call is part of Claude's task checklist.</summary>
    public static bool IsTool(string tool) => tool is "TodoWrite" or "TaskCreate" or "TaskUpdate";

    enum Mode
    {
        None,
        Tasks,
        Todo,
    }

    sealed class Item
    {
        public required string Id { get; init; }
        public string Subject { get; set; } = "";
        public string? ActiveForm { get; set; }
        public string Status { get; set; } = "pending";

        public Item Copy() => new() { Id = Id, Subject = Subject, ActiveForm = ActiveForm, Status = Status };
    }

    readonly record struct Todo(string Content, string? ActiveForm, string Status);

    sealed class Pending
    {
        public required string Tool { get; init; }
        public string? Subject { get; init; }
        public string? ActiveForm { get; init; }
        public string? TaskId { get; init; }
        public string? Status { get; init; }
        public List<Todo>? Todos { get; init; }
    }

    readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    readonly Dictionary<string, Item> tasks = new(StringComparer.Ordinal);
    readonly List<string> order = [];
    readonly List<Todo> todo = [];
    Mode mode;

    /// <summary>Forgets everything: a new conversation, or a replay that starts from its root.</summary>
    public void Reset()
    {
        pending.Clear();
        tasks.Clear();
        order.Clear();
        todo.Clear();
        mode = Mode.None;
    }

    /// <summary>Takes another replay's final state, leaving this instance in place for its live session.</summary>
    public void CopyFrom(ClaudeTaskPlan other)
    {
        Reset();
        foreach (var (id, operation) in other.pending) pending[id] = operation;
        foreach (var (id, item) in other.tasks) tasks[id] = item.Copy();
        order.AddRange(other.order);
        todo.AddRange(other.todo);
        mode = other.mode;
    }

    /// <summary>The current plan, as ACP v1 sends it: every entry, with its current status.</summary>
    public Plan Snapshot() => new() { Entries = Entries() };

    List<PlanEntry> Entries()
    {
        var entries = new List<PlanEntry>();
        if (mode == Mode.Todo)
        {
            foreach (var item in todo)
                if (Entry(item.Content, item.ActiveForm, item.Status) is { } entry) entries.Add(entry);
        }
        else if (mode == Mode.Tasks)
        {
            foreach (var id in order)
                if (tasks.TryGetValue(id, out var item) && Entry(item.Subject, item.ActiveForm, item.Status) is { } entry)
                    entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// The input of a task tool call. Nothing is published here: a call that fails, is denied or
    /// never returns a result changes no plan.
    /// </summary>
    public void ObserveToolUse(string toolCallId, string tool, JsonObject? input)
    {
        if (string.IsNullOrEmpty(toolCallId)) return;
        try
        {
            switch (tool)
            {
                case "TodoWrite":
                    if (!Room(toolCallId)) return;
                    pending[toolCallId] = new Pending { Tool = tool, Todos = Todos(input?["todos"]) };
                    break;
                case "TaskCreate":
                    if (!Room(toolCallId)) return;
                    pending[toolCallId] = new Pending
                    {
                        Tool = tool,
                        Subject = Text(input?["subject"]),
                        ActiveForm = Text(input?["activeForm"] ?? input?["active_form"]),
                    };
                    break;
                case "TaskUpdate":
                    if (!Room(toolCallId)) return;
                    pending[toolCallId] = new Pending
                    {
                        Tool = tool,
                        TaskId = Text(input?["taskId"] ?? input?["id"] ?? input?["task_id"]),
                        Status = Text(input?["status"]),
                        Subject = Text(input?["subject"]),
                        ActiveForm = Text(input?["activeForm"] ?? input?["active_form"]),
                    };
                    break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or ArgumentException)
        {
            pending.Remove(toolCallId);
        }
    }

    /// <summary>
    /// The structured output of a task tool call, paired by its tool call ID. A successful applied
    /// change returns the complete replacement plan; a failure, a denial or a malformed payload
    /// returns null. A result whose call this stream did not see (a session opened mid-turn) is
    /// still applied when its output alone is authoritative enough, so a primed state is published.
    /// </summary>
    public Plan? ObserveToolResult(string toolCallId, string? tool, bool failed, JsonNode? structured, string? text = null)
    {
        if (string.IsNullOrEmpty(toolCallId)) return null;
        if (!pending.Remove(toolCallId, out var operation))
            return failed ? null : Orphan(tool, structured);
        if (failed) return null;
        try
        {
            return operation.Tool switch
            {
                "TodoWrite" => ApplyTodo(operation, structured),
                "TaskCreate" => ApplyCreate(operation, structured),
                "TaskUpdate" => ApplyUpdate(operation, structured),
                _ => null,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    bool Room(string toolCallId) => pending.ContainsKey(toolCallId) || pending.Count < MaxEntries;

    /// <summary>A result whose tool call was not seen: TaskCreate/TaskUpdate outputs carry enough for the visible change.</summary>
    Plan? Orphan(string? tool, JsonNode? structured)
    {
        try
        {
            return tool switch
            {
                "TodoWrite" => TodoList(structured) is { } list ? ApplyTodoList(list) : null,
                "TaskCreate" => ApplyCreate(new Pending { Tool = "TaskCreate" }, structured),
                "TaskUpdate" => ApplyUpdate(new Pending { Tool = "TaskUpdate" }, structured),
                _ => null,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    Plan? ApplyTodo(Pending operation, JsonNode? structured)
    {
        // The native result's newTodos is the list Claude actually applied. The call's input is
        // the fallback for a harness that sends no structured result.
        var list = TodoList(structured) ?? operation.Todos;
        return list is null ? null : ApplyTodoList(list);
    }

    Plan ApplyTodoList(List<Todo> list)
    {
        mode = Mode.Todo;
        tasks.Clear();
        order.Clear();
        todo.Clear();
        todo.AddRange(list);
        return Snapshot();
    }

    Plan? ApplyCreate(Pending operation, JsonNode? structured)
    {
        if (structured is not JsonObject result || result["task"] is not JsonObject task) return null;
        var id = Text(task["id"]);
        if (string.IsNullOrEmpty(id)) return null;
        var subject = Text(task["subject"]) ?? operation.Subject;
        var active = operation.ActiveForm;
        if (string.IsNullOrWhiteSpace(subject) && string.IsNullOrWhiteSpace(active)) return null;
        if (mode != Mode.Tasks)
        {
            tasks.Clear();
            order.Clear();
            todo.Clear();
            mode = Mode.Tasks;
        }
        if (!tasks.TryGetValue(id, out var item))
        {
            if (tasks.Count >= MaxEntries) return null;
            item = new Item { Id = id, Subject = subject ?? "", ActiveForm = active };
            tasks[id] = item;
            order.Add(id);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(subject)) item.Subject = subject;
            if (!string.IsNullOrWhiteSpace(active)) item.ActiveForm = active;
        }
        return Snapshot();
    }

    Plan? ApplyUpdate(Pending operation, JsonNode? structured)
    {
        var result = structured as JsonObject;
        var id = Text(result?["taskId"]) ?? operation.TaskId;
        if (string.IsNullOrEmpty(id)) return null;
        if (result?["success"] is JsonValue successValue && successValue.TryGetValue<bool>(out var success) && !success) return null;
        if (Text(result?["error"]) is { Length: > 0 }) return null;
        var fields = Fields(result?["updatedFields"]);
        var to = Text((result?["statusChange"] as JsonObject)?["to"]);
        var deleted = to == "deleted" || fields.Contains("deleted")
            || (to is null && operation.Status == "deleted" && (fields.Count == 0 || fields.Contains("status")));
        if (deleted)
        {
            // A primed state may already lack it; the full replacement is still what the client needs.
            if (tasks.Remove(id, out _)) order.RemoveAll(candidate => candidate == id);
            return Snapshot();
        }
        if (!tasks.TryGetValue(id, out var item)) return null;
        var applied = false;
        if (to is "pending" or "in_progress" or "completed")
        {
            item.Status = to;
            applied = true;
        }
        else if (to is null && operation.Status is ("pending" or "in_progress" or "completed")
                 && (fields.Count == 0 || fields.Contains("status")))
        {
            item.Status = operation.Status;
            applied = true;
        }
        else if (fields.Contains("status"))
        {
            applied = true; // a primed state already holds a change the result does not repeat
        }
        if (!string.IsNullOrWhiteSpace(operation.Subject) && (fields.Count == 0 || fields.Contains("subject")))
        {
            item.Subject = operation.Subject;
            applied = true;
        }
        else if (fields.Contains("subject"))
        {
            applied = true;
        }
        if (!string.IsNullOrWhiteSpace(operation.ActiveForm)
            && (fields.Count == 0 || fields.Contains("activeForm") || fields.Contains("active_form")))
        {
            item.ActiveForm = operation.ActiveForm;
            applied = true;
        }
        else if (fields.Contains("activeForm") || fields.Contains("active_form"))
        {
            applied = true;
        }
        return applied ? Snapshot() : null;
    }

    /// <summary>The native TodoWrite list, if this is a structured result that has one; null leaves the plan alone.</summary>
    static List<Todo>? TodoList(JsonNode? structured)
    {
        if (structured is not JsonObject result || result["newTodos"] is not JsonArray array) return null;
        var list = Todos(array);
        if (list is null) return null;
        // An array with entries of which none is usable is malformed, not an empty checklist.
        return array.Count > 0 && list.Count == 0 ? null : list;
    }

    static List<Todo>? Todos(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var list = new List<Todo>();
        foreach (var entry in array.OfType<JsonObject>())
        {
            if (list.Count >= MaxEntries) break;
            var content = Text(entry["content"]);
            var active = Text(entry["activeForm"] ?? entry["active_form"]);
            var status = Text(entry["status"]);
            if (status is not ("pending" or "in_progress" or "completed")) continue;
            if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(active)) continue;
            list.Add(new Todo(content ?? active ?? "", active, status));
        }
        return list;
    }

    static HashSet<string> Fields(JsonNode? node)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (node is not JsonArray array) return fields;
        foreach (var value in array.OfType<JsonValue>())
            if (value.TryGetValue<string>(out var field) && field.Length > 0)
                fields.Add(field);
        return fields;
    }

    static PlanEntry? Entry(string? subject, string? activeForm, string status)
    {
        PlanEntryStatus? state = status switch
        {
            "pending" => PlanEntryStatus.Pending,
            "in_progress" => PlanEntryStatus.InProgress,
            "completed" => PlanEntryStatus.Completed,
            _ => null,
        };
        if (state is null) return null;
        // As Claude's own progress display names the work: the active form while it runs, the
        // subject otherwise. Only a native status becomes an ACP status.
        var text = status == "in_progress" && !string.IsNullOrWhiteSpace(activeForm)
            ? activeForm
            : !string.IsNullOrWhiteSpace(subject) ? subject : activeForm;
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new PlanEntry { Content = text.Trim(), Priority = PlanEntryPriority.Medium, Status = state.Value };
    }

    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
