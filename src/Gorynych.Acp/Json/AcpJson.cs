using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Gorynych.Acp;

/// <summary>
/// A string enumeration that tolerates values it does not know. ACP adds enum values between
/// schema releases; a conductor must forward them instead of failing.
/// </summary>
public interface IOpenEnum<TSelf> where TSelf : struct, IOpenEnum<TSelf>
{
    string Value { get; }
    static abstract TSelf Create(string value);
}

/// <summary>Serializes an <see cref="IOpenEnum{TSelf}"/> as its plain string value.</summary>
public sealed class OpenEnumJsonConverter<T> : JsonConverter<T> where T : struct, IOpenEnum<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? T.Create(reader.GetString()!)
            : throw new JsonException($"Expected a string for {typeof(T).Name}.");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>Helpers shared by the generated union converters and by protocol code.</summary>
public static class AcpJson
{
    /// <summary>
    /// How Gorynych writes JSON text everywhere: on the wire, in the journal, and in what agents
    /// read. Non-ASCII letters and characters such as <c>+</c>, <c>&lt;</c> or <c>&amp;</c> stay as
    /// they are, instead of <c>\uXXXX</c> escapes that an agent reads verbatim. Safe because the text
    /// never goes into HTML.
    /// </summary>
    public static readonly JsonSerializerOptions TextOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary><see cref="TextOptions"/>, indented: JSON an agent reads in a tool's result.</summary>
    public static readonly JsonSerializerOptions IndentedTextOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    public static string? GetTag(JsonObject obj, string discriminator) =>
        obj[discriminator] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Writes <paramref name="body"/> with the discriminator first, as the reference SDKs do.</summary>
    public static void WriteTagged(Utf8JsonWriter writer, string discriminator, string? tag, JsonNode? body)
    {
        writer.WriteStartObject();
        if (tag is not null) writer.WriteString(discriminator, tag);
        if (body is JsonObject obj)
            foreach (var (key, value) in obj)
            {
                if (tag is not null && key == discriminator) continue;
                writer.WritePropertyName(key);
                if (value is null) writer.WriteNullValue();
                else value.WriteTo(writer);
            }
        writer.WriteEndObject();
    }

    /// <summary>Serializes an ACP value to a <see cref="JsonNode"/> with the source-generated metadata.</summary>
    public static JsonNode? ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)AcpJsonContext.Default.GetTypeInfo(typeof(T))!);

    /// <summary>Deserializes an ACP value from a <see cref="JsonNode"/> with the source-generated metadata.</summary>
    public static T? FromNode<T>(JsonNode? node) =>
        node is null ? default : node.Deserialize((System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)AcpJsonContext.Default.GetTypeInfo(typeof(T))!);
}
