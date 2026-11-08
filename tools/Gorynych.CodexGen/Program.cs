// Generates the Codex app-server protocol types the Codex adapter uses, from the JSON schema that
// `codex app-server generate-json-schema --out <dir>` writes.
//
//   dotnet run --project tools/Gorynych.CodexGen [-- <repo root> <codex version>]
//
// Reads schema/codex/<version>/codex_app_server_protocol.schemas.json and writes
// src/Gorynych.Adapters.Codex/Generated/Codex.g.cs: the types reachable from Roots below, in the
// way tools/Gorynych.AcpGen maps ACP's schema (docs/acp-types.md): open enums, discriminated unions
// with an Unknown variant, plain classes, and a source-generated JsonSerializerContext for Native AOT.
// A union of strings and objects (an approval decision, AskForApproval) stays raw JSON.
using System.Text;
using System.Text.Json.Nodes;

var root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepoRoot();
var version = args.Length > 1 ? args[1] : "0.167.0";
var schemaPath = Path.Combine(root, "schema", "codex", version, "codex_app_server_protocol.schemas.json");
var bundle = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
var output = Path.Combine(root, "src", "Gorynych.Adapters.Codex", "Generated", "Codex.g.cs");
var code = new Generator(bundle, version).Run();
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, code.ReplaceLineEndings("\n"));
Console.WriteLine($"wrote {output} ({code.Length} chars)");

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx"))) return dir.FullName;
    throw new InvalidOperationException("Run from inside the repository or pass its root.");
}

sealed record Prop(string JsonName, JsonObject Schema, bool Required);

sealed record Variant(string? Tag, string ClassName, List<Prop> Props);

abstract record Shape;
sealed record AliasShape(string CsType, bool IsValueType) : Shape;
sealed record EnumShape(List<string> Values, List<string?> Descriptions) : Shape;
sealed record ClassShape(List<Prop> Props) : Shape;
sealed record UnionShape(string Discriminator, List<Prop> BaseProps, List<Variant> Variants) : Shape;

sealed class Generator(JsonObject bundle, string version)
{
    /// <summary>The messages the adapter sends or handles; everything they reference comes with them.</summary>
    static readonly string[] Roots =
    [
        "InitializeParams", "InitializeResponse",
        "ThreadStartParams", "ThreadStartResponse", "ThreadResumeParams", "ThreadResumeResponse",
        "ThreadListParams", "ThreadListResponse", "ThreadReadParams", "ThreadReadResponse",
        "ThreadItemsListParams", "ThreadItemsListResponse",
        "ThreadUnsubscribeParams", "ThreadUnsubscribeResponse", "ThreadDeleteParams", "ThreadDeleteResponse",
        "ThreadSetNameParams", "ThreadSetNameResponse",
        "TurnStartParams", "TurnStartResponse", "TurnInterruptParams", "TurnInterruptResponse",
        "ModelListParams", "ModelListResponse",
        "ThreadNameUpdatedNotification", "ThreadStatusChangedNotification", "ThreadClosedNotification",
        "ThreadSettingsUpdatedNotification", "TurnStartedNotification", "TurnCompletedNotification",
        "TurnPlanUpdatedNotification", "ItemStartedNotification", "ItemCompletedNotification",
        "AgentMessageDeltaNotification", "ReasoningTextDeltaNotification", "ReasoningSummaryTextDeltaNotification",
        "ReasoningSummaryPartAddedNotification", "CommandExecutionOutputDeltaNotification",
        "ThreadTokenUsageUpdatedNotification", "ServerRequestResolvedNotification", "ErrorNotification",
        "CommandExecutionRequestApprovalParams", "CommandExecutionRequestApprovalResponse",
        "FileChangeRequestApprovalParams", "FileChangeRequestApprovalResponse",
        "PermissionsRequestApprovalParams", "PermissionsRequestApprovalResponse",
        "ToolRequestUserInputParams", "ToolRequestUserInputResponse",
        "McpServerElicitationRequestParams", "McpServerElicitationRequestResponse",
    ];

    readonly Dictionary<string, JsonObject> defs = MergeDefs(bundle);
    readonly Dictionary<string, Shape> shapes = [];
    readonly List<string> order = [];
    readonly HashSet<string> classNames = [];
    readonly HashSet<string> variantNames = [];
    readonly StringBuilder sb = new();

    static Dictionary<string, JsonObject> MergeDefs(JsonObject bundle)
    {
        var all = bundle["definitions"]!.AsObject();
        var merged = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (k, v) in all)
            if (k != "v2" && v is JsonObject o) merged[k] = o;
        foreach (var (k, v) in all["v2"]!.AsObject())
            if (v is JsonObject o) merged[k] = o; // the two names both bundles have (AbsolutePathBuf, RequestId) are the same
        return merged;
    }

    public string Run()
    {
        foreach (var name in Roots)
        {
            if (!defs.ContainsKey(name)) throw new InvalidOperationException($"{name} is not in the schema of Codex {version}");
            Visit(name);
        }
        foreach (var name in order) classNames.Add(name);

        Line("// <auto-generated>");
        Line($"// Generated by tools/Gorynych.CodexGen from the Codex {version} app-server protocol schema");
        Line("// (`codex app-server generate-json-schema`). Do not edit.");
        Line("// </auto-generated>");
        Line("#nullable enable");
        Line("#pragma warning disable CS1591");
        Line("using System.Text.Json;");
        Line("using System.Text.Json.Nodes;");
        Line("using System.Text.Json.Serialization;");
        Line("using Gorynych.Acp;");
        Line();
        Line("namespace Gorynych.Adapters.Codex.Protocol;");
        Line();
        EmitMethods();
        var serializable = new List<string>();
        foreach (var name in order)
        {
            var def = defs[name];
            switch (shapes[name])
            {
                case EnumShape e: EmitEnum(name, def, e); break;
                case ClassShape c: EmitClass(name, def, c.Props, null, null); serializable.Add(name); break;
                case UnionShape u: EmitUnion(name, def, u, serializable); break;
            }
        }
        EmitContext(serializable);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- analysis

    void Visit(string name)
    {
        if (shapes.ContainsKey(name)) return;
        var shape = Analyze(name, defs[name]);
        shapes[name] = shape;
        order.Add(name);
        foreach (var r in Refs(defs[name])) Visit(r);
    }

    static IEnumerable<string> Refs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (k == "$ref" && v is JsonValue r) yield return RefName(r.GetValue<string>());
                    else foreach (var x in Refs(v)) yield return x;
                }
                break;
            case JsonArray a:
                foreach (var v in a)
                    foreach (var x in Refs(v)) yield return x;
                break;
        }
    }

    static string RefName(string r) =>
        r.StartsWith("#/definitions/v2/", StringComparison.Ordinal) ? r["#/definitions/v2/".Length..]
        : r.StartsWith("#/definitions/", StringComparison.Ordinal) ? r["#/definitions/".Length..]
        : throw new NotSupportedException(r);

    Shape Analyze(string name, JsonObject def)
    {
        if (StringEnum(def) is { } values) return new EnumShape(values, values.Select(_ => (string?)null).ToList());
        var union = (def["oneOf"] ?? def["anyOf"]) as JsonArray;
        if (union is not null)
        {
            var variants = union.Select(v => v as JsonObject ?? new JsonObject()).ToList();
            if (variants.All(v => StringEnum(v) is not null))
                return new EnumShape(variants.SelectMany(v => StringEnum(v)!).ToList(),
                    variants.SelectMany(v => StringEnum(v)!.Select(_ => Str(v["description"]))).ToList());
            if (variants.All(v => v["properties"] is JsonObject) && DetectDiscriminator(variants) is { } disc)
                return BuildUnion(name, def, disc, variants);
            if (def["properties"] is null) return new AliasShape("JsonNode", false); // strings and objects mixed
        }
        if (def["properties"] is not null || Str(def["type"]) == "object")
        {
            var props = new List<Prop>();
            CollectProps(def, props, exclude: null);
            return new ClassShape(props);
        }
        return Str(def["type"]) switch
        {
            "string" => new AliasShape("string", false),
            "boolean" => new AliasShape("bool", true),
            "integer" => new AliasShape(IntType(Str(def["format"])), true),
            "number" => new AliasShape("double", true),
            _ => new AliasShape("JsonNode", false),
        };
    }

    /// <summary>The values of a string enum schema (<c>enum</c> or <c>const</c>), or null.</summary>
    static List<string>? StringEnum(JsonObject s)
    {
        if (s["properties"] is not null) return null;
        if (s["enum"] is JsonArray e && e.Count > 0 && e.All(x => x is JsonValue v && v.TryGetValue<string>(out _)))
            return e.Select(x => x!.GetValue<string>()).ToList();
        if (Str(s["const"]) is { } c) return [c];
        return null;
    }

    static string? TagOf(JsonObject variant, string disc) => variant["properties"]?[disc] is JsonObject p
        ? Str(p["const"]) ?? (p["enum"] is JsonArray e && e.Count == 1 ? Str(e[0]) : null)
        : null;

    static string? DetectDiscriminator(List<JsonObject> variants)
    {
        foreach (var (k, _) in variants[0]["properties"]!.AsObject())
            if (variants.All(v => TagOf(v, k) is not null)) return k;
        return null;
    }

    UnionShape BuildUnion(string name, JsonObject def, string disc, List<JsonObject> variants)
    {
        var baseProps = new List<Prop>();
        CollectProps(def, baseProps, exclude: disc, includeUnion: false);
        var result = new List<Variant>();
        foreach (var v in variants)
        {
            var tag = TagOf(v, disc)!;
            var props = new List<Prop>();
            CollectProps(v, props, exclude: disc);
            props.RemoveAll(p => baseProps.Any(b => b.JsonName == p.JsonName));
            var className = Str(v["title"]) is { } title && !defs.ContainsKey(title) ? title : name + Pascal(tag);
            if (defs.ContainsKey(className)) className += "Variant";
            while (!variantNames.Add(className)) className += "Alt"; // "openai/form" and "openaiForm" both read OpenaiForm
            result.Add(new Variant(tag, className, props));
        }
        return new UnionShape(disc, baseProps, result);
    }

    void CollectProps(JsonObject s, List<Prop> into, string? exclude, bool includeUnion = true)
    {
        var required = (s["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToHashSet() ?? [];
        if (s["properties"] is JsonObject props)
            foreach (var (k, p) in props)
                if (k != exclude && into.All(x => x.JsonName != k))
                    into.Add(new Prop(k, p as JsonObject ?? new JsonObject(), required.Contains(k)));
        if (s["allOf"] is JsonArray all)
            foreach (var a in all)
                if (Str(a?["$ref"]) is { } r) CollectProps(defs[RefName(r)], into, exclude);
        _ = includeUnion;
    }

    // ---------------------------------------------------------------- type mapping

    (string Type, bool IsValue) TypeOf(JsonObject p)
    {
        if (Str(p["$ref"]) is { } direct) return TypeOfDef(RefName(direct));
        if (p["allOf"] is JsonArray all && all.Count == 1 && Str(all[0]?["$ref"]) is { } r) return TypeOfDef(RefName(r));
        if ((p["anyOf"] ?? p["oneOf"]) is JsonArray any)
        {
            var nonNull = any.Where(a => Str(a?["type"]) != "null").Select(a => a as JsonObject ?? new JsonObject()).ToList();
            if (nonNull.Count == 1) return TypeOf(nonNull[0]);
            return ("JsonNode", false);
        }
        var type = p["type"] switch
        {
            JsonArray arr => arr.Select(Str).FirstOrDefault(t => t != "null"),
            var t => Str(t),
        };
        switch (type)
        {
            case "string": return ("string", false);
            case "boolean": return ("bool", true);
            case "integer": return (IntType(Str(p["format"])), true);
            case "number": return ("double", true);
            case "array":
                var item = p["items"] is JsonObject io ? TypeOf(io).Type : "JsonNode";
                return ($"List<{item}>", false);
            case "object":
                if (p["additionalProperties"] is JsonObject ap && ap.Count > 0) return ($"Dictionary<string, {TypeOf(ap).Type}?>", false);
                return ("JsonObject", false);
            default: return ("JsonNode", false);
        }
    }

    (string, bool) TypeOfDef(string name) => shapes[name] switch
    {
        AliasShape a => (a.CsType, a.IsValueType),
        EnumShape => (name, true),
        _ => (name, false),
    };

    static string IntType(string? format) => format switch
    {
        "int32" => "int",
        "uint32" => "uint",
        "uint16" => "ushort",
        "uint8" => "byte",
        "uint64" or "uint" => "ulong",
        _ => "long",
    };

    // ---------------------------------------------------------------- emission

    void EmitMethods()
    {
        Summary("The Codex app-server's method names, from the schema's request and notification unions.");
        Line("public static class CodexMethods");
        Line("{");
        Line($"    public const string SchemaVersion = \"{version}\";");
        foreach (var (union, cls) in new[] { ("ClientRequest", "Client"), ("ServerRequest", "Server"), ("ServerNotification", "Notification") })
        {
            Line();
            Line($"    public static class {cls}");
            Line("    {");
            var seen = new HashSet<string>();
            foreach (var v in (defs[union]["oneOf"] as JsonArray)!)
                if (TagOf(v!.AsObject(), "method") is { } m && seen.Add(Pascal(m)))
                    Line($"        public const string {Pascal(m)} = \"{m}\";");
            Line("    }");
        }
        Line("}");
        Line();
    }

    void EmitEnum(string name, JsonObject def, EnumShape e)
    {
        Summary(Str(def["description"]));
        Line($"[JsonConverter(typeof(OpenEnumJsonConverter<{name}>))]");
        Line($"public readonly partial record struct {name}(string Value) : IOpenEnum<{name}>");
        Line("{");
        var seen = new HashSet<string>();
        for (var i = 0; i < e.Values.Count; i++)
        {
            var member = Pascal(e.Values[i]);
            if (member.Length == 0 || char.IsDigit(member[0])) member = "Value" + member;
            if (member is "Value" or "Create" || !seen.Add(member)) continue;
            Summary(e.Descriptions[i], "    ");
            Line($"    public static {name} {member} {{ get; }} = new(\"{e.Values[i]}\");");
        }
        Line($"    public static {name} Create(string value) => new(value);");
        Line("    public override string ToString() => Value;");
        Line("}");
        Line();
    }

    void EmitClass(string name, JsonObject def, List<Prop> props, string? baseName, Variant? variant)
    {
        Summary(Str(def["description"]));
        Line(baseName is null ? $"public sealed partial class {name}" : $"public sealed partial class {name} : {baseName}");
        Line("{");
        if (variant is not null)
            Line($"    public {name}() : base(\"{variant.Tag}\") {{ }}");
        foreach (var p in props) EmitProp(name, p);
        Line("}");
        Line();
    }

    void EmitProp(string owner, Prop p)
    {
        var (type, isValue) = TypeOf(p.Schema);
        var csName = p.JsonName == "_meta" ? "Meta" : Pascal(p.JsonName);
        if (csName == owner || csName == "Tag") csName += "Value";
        Summary(Str(p.Schema["description"]), "    ");
        Line($"    [JsonPropertyName(\"{p.JsonName}\")]");
        if (!p.Required)
        {
            Line($"    public {type}? {csName} {{ get; set; }}");
            return;
        }
        var init = isValue ? "" : type.StartsWith("List<", StringComparison.Ordinal) ? " = [];" : type == "string" ? " = \"\";" : " = null!;";
        Line($"    public {type} {csName} {{ get; set; }}{init}");
    }

    void EmitUnion(string name, JsonObject def, UnionShape u, List<string> serializable)
    {
        Summary(Str(def["description"]));
        Line($"/// <remarks>Discriminated by <c>{u.Discriminator}</c>; unknown variants deserialize to <see cref=\"Unknown\"/>.</remarks>");
        Line($"[JsonConverter(typeof({name}JsonConverter))]");
        Line($"public abstract partial class {name}");
        Line("{");
        Line($"    private protected {name}(string? tag) {{ Tag = tag; }}");
        Line();
        Line($"    /// <summary>The <c>{u.Discriminator}</c> value of this variant.</summary>");
        Line("    [JsonIgnore] public string? Tag { get; }");
        foreach (var p in u.BaseProps) EmitProp(name, p);
        Line();
        Line("    /// <summary>A variant this schema version does not know; keeps the raw JSON.</summary>");
        Line($"    public sealed class Unknown(string? tag, JsonObject raw) : {name}(tag)");
        Line("    {");
        Line("        [JsonIgnore] public JsonObject Raw { get; } = raw;");
        Line("    }");
        Line("}");
        Line();
        foreach (var v in u.Variants)
        {
            EmitClass(v.ClassName, new JsonObject(), v.Props, name, v);
            serializable.Add(v.ClassName);
        }

        Line($"internal sealed class {name}JsonConverter : JsonConverter<{name}>");
        Line("{");
        Line($"    public override {name}? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
        Line("    {");
        Line("        if (reader.TokenType == JsonTokenType.Null) return null;");
        Line($"        var obj = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException(\"{name} must be a JSON object.\");");
        Line($"        var tag = AcpJson.GetTag(obj, \"{u.Discriminator}\");");
        Line("        return tag switch");
        Line("        {");
        foreach (var v in u.Variants)
            Line($"            \"{v.Tag}\" => obj.Deserialize(CodexJsonContext.Default.{v.ClassName}),");
        Line($"            _ => new {name}.Unknown(tag, obj),");
        Line("        };");
        Line("    }");
        Line();
        Line($"    public override void Write(Utf8JsonWriter writer, {name} value, JsonSerializerOptions options)");
        Line("    {");
        Line("        JsonNode? body = value switch");
        Line("        {");
        Line($"            {name}.Unknown u => u.Raw,");
        foreach (var v in u.Variants)
            Line($"            {v.ClassName} x => JsonSerializer.SerializeToNode(x, CodexJsonContext.Default.{v.ClassName}),");
        Line($"            _ => throw new JsonException($\"Unsupported {name} variant {{value.GetType()}}.\"),");
        Line("        };");
        Line($"        AcpJson.WriteTagged(writer, \"{u.Discriminator}\", value is {name}.Unknown ? null : value.Tag, body);");
        Line("    }");
        Line("}");
        Line();
    }

    void EmitContext(List<string> serializable)
    {
        Summary("Source-generated System.Text.Json metadata for every generated type, so serialization works under Native AOT.");
        Line("[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]");
        foreach (var name in serializable.Distinct())
            Line($"[JsonSerializable(typeof({name}))]");
        foreach (var (name, shape) in shapes)
            if (shape is UnionShape) Line($"[JsonSerializable(typeof({name}))]");
        Line("[JsonSerializable(typeof(JsonNode))]");
        Line("[JsonSerializable(typeof(JsonObject))]");
        Line("[JsonSerializable(typeof(JsonElement))]");
        Line("public sealed partial class CodexJsonContext : JsonSerializerContext;");
    }

    // ---------------------------------------------------------------- helpers

    void Summary(string? text, string indent = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        Line($"{indent}/// <summary>");
        foreach (var l in escaped.ReplaceLineEndings("\n").Split('\n'))
            Line($"{indent}/// {l}".TrimEnd());
        Line($"{indent}/// </summary>");
    }

    void Line(string s = "") => sb.Append(s).Append('\n');

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string Pascal(string s)
    {
        var b = new StringBuilder();
        var upper = true;
        foreach (var ch in s)
        {
            if (!char.IsLetterOrDigit(ch)) { upper = true; continue; }
            b.Append(upper ? char.ToUpperInvariant(ch) : ch);
            upper = false;
        }
        return b.ToString();
    }
}
