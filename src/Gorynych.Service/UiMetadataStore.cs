using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

// One copy-on-write document keeps group deletion and attribute updates atomic.
internal sealed class UiMetadataStore
{
    readonly string path;
    readonly Lock gate = new();
    JsonObject state;
    internal Action<string, string> WriteFile { get; set; } = static (path, text) => AtomicFile.WriteAllText(path, text);
    const int MaxDocument = 4 * 1024 * 1024;
    const int MaxAttributes = 64 * 1024;
    const int MaxField = 1024;
    const string Group = GorynychProtocol.MetaGroup;

    internal UiMetadataStore(string path)
    {
        this.path = path;
        state = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException("Empty UI metadata") : new JsonObject();
        state["sessions"] ??= new JsonObject();
        state["projects"] ??= new JsonObject();
        _ = state["sessions"]!.AsObject();
        _ = state["projects"]!.AsObject();
    }

    static string Field(JsonNode? value, string name)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrEmpty(s) || s.Length > MaxField)
            throw new ArgumentException($"Invalid {name}");
        return s;
    }

    static JsonObject Sessions(JsonObject doc) => doc["sessions"]!.AsObject();
    static JsonObject Projects(JsonObject doc) => doc["projects"]!.AsObject();
    static JsonObject Attributes(JsonObject doc, string id) => Sessions(doc)[id] is JsonObject a ? a.DeepClone().AsObject() : new();
    internal JsonObject Get(string id) { lock (gate) return Attributes(state, id); }

    internal void Remove(string id)
    {
        lock (gate)
        {
            if (!Sessions(state).ContainsKey(id)) return;
            var next = state.DeepClone().AsObject();
            Sessions(next).Remove(id);
            Commit(next);
        }
    }

    static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (key.Length > MaxField) throw new ArgumentException("Attribute key too long");
            if (value is null) target.Remove(key);
            else if (value is JsonObject obj)
            {
                var nested = target[key] is JsonObject existing ? existing.DeepClone().AsObject() : new JsonObject();
                Merge(nested, obj);
                target[key] = nested;
            }
            else target[key] = value.DeepClone();
        }
    }

    static void ValidateGroup(JsonObject doc, JsonObject attrs)
    {
        if (!attrs.TryGetPropertyValue(Group, out var value)) return;
        if (value is not JsonObject group || group.Count != 2) throw new ArgumentException("Invalid group reference");
        var project = Field(group["projectKey"], "projectKey");
        var id = Field(group["groupId"], "groupId");
        if (Projects(doc)[project] is not JsonObject groups || !groups.ContainsKey(id)) throw new ArgumentException("Group does not exist");
    }

    void Commit(JsonObject next)
    {
        var text = next.ToJsonString();
        if (text.Length > MaxDocument) throw new ArgumentException("UI metadata too large");
        WriteFile(path, text);
        state = next;
    }

    internal JsonObject Patch(string id, JsonObject patch)
    {
        Field(JsonValue.Create(id), "sessionId");
        lock (gate)
        {
            var next = state.DeepClone().AsObject();
            var attrs = Attributes(next, id);
            Merge(attrs, patch);
            ValidateGroup(next, attrs);
            if (attrs.ToJsonString().Length > MaxAttributes) throw new ArgumentException("Attributes too large");
            Sessions(next)[id] = attrs;
            Commit(next);
            return attrs.DeepClone().AsObject();
        }
    }

    internal void SetInitial(string id, JsonObject attributes)
    {
        Field(JsonValue.Create(id), "sessionId");
        lock (gate)
        {
            var next = state.DeepClone().AsObject();
            var attrs = attributes.DeepClone().AsObject();
            ValidateGroup(next, attrs);
            if (attrs.ToJsonString().Length > MaxAttributes) throw new ArgumentException("Attributes too large");
            Sessions(next)[id] = attrs;
            Commit(next);
        }
    }

    internal JsonArray List(string project)
    {
        Field(JsonValue.Create(project), "projectKey");
        lock (gate)
        {
            var result = new JsonArray();
            if (Projects(state)[project] is JsonObject groups)
                foreach (var (id, name) in groups) result.Add((JsonNode)new JsonObject { ["groupId"] = id, ["name"] = name!.DeepClone() });
            return result;
        }
    }

    internal bool Set(string project, string id, string name)
    {
        Field(JsonValue.Create(project), "projectKey"); Field(JsonValue.Create(id), "groupId"); Field(JsonValue.Create(name), "name");
        lock (gate)
        {
            var next = state.DeepClone().AsObject();
            var projects = Projects(next);
            if (projects[project] is not JsonObject) projects[project] = new JsonObject();
            var groups = projects[project]!.AsObject();
            if ((string?)groups[id] == name) return false;
            groups[id] = name;
            Commit(next);
            return true;
        }
    }

    // A consent binding names this instance, not only its reusable public group key.
    // Creating the token and deleting the group use the same COW document.
    internal string? EnsureGroupInstance(string project, string id)
    {
        Field(JsonValue.Create(project), "projectKey"); Field(JsonValue.Create(id), "groupId");
        lock (gate)
        {
            if (Projects(state)[project] is not JsonObject groups || !groups.ContainsKey(id)) return null;
            var existing = state["groupInstances"]?[project]?[id];
            if (existing is not null)
            {
                if (existing is not JsonValue value || !value.TryGetValue<string>(out var instance) ||
                    !Guid.TryParseExact(instance, "N", out _) || instance != instance.ToLowerInvariant())
                    throw new InvalidDataException("Invalid group instance");
                return instance;
            }
            var next = state.DeepClone().AsObject();
            next["groupInstances"] ??= new JsonObject();
            var projects = next["groupInstances"]!.AsObject();
            projects[project] ??= new JsonObject();
            var token = Guid.NewGuid().ToString("N");
            projects[project]![id] = token;
            Commit(next);
            return token;
        }
    }

    internal List<string> Delete(string project, string id, out bool changed)
    {
        Field(JsonValue.Create(project), "projectKey"); Field(JsonValue.Create(id), "groupId");
        lock (gate)
        {
            var next = state.DeepClone().AsObject();
            changed = Projects(next)[project] is JsonObject groups && groups.Remove(id);
            var affected = new List<string>();
            if (!changed) return affected;
            if (next["groupInstances"]?[project] is JsonObject instances)
            {
                instances.Remove(id);
                if (instances.Count == 0) next["groupInstances"]!.AsObject().Remove(project);
            }
            foreach (var (sid, value) in Sessions(next))
                if (value is JsonObject attrs && attrs[Group] is JsonObject group && (string?)group["projectKey"] == project && (string?)group["groupId"] == id)
                { attrs.Remove(Group); affected.Add(sid); }
            Commit(next);
            return affected;
        }
    }
}
