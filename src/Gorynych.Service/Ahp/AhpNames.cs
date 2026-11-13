using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Service.Ahp;

/// <summary>The AHP face's identifiers and small conversions (docs/ahp-face.md, "Identifiers").</summary>
internal static class AhpNames
{
    /// <summary>The root channel.</summary>
    public const string Root = "ahp-root://";

    const string SessionScheme = "ahp-session:/";
    const string ChatScheme = "ahp-chat:/";

    /// <summary>The only AHP version the face speaks.</summary>
    public const string Version = "1.0.0";

    /// <summary>A root session's URI: <c>ahp-session:/&lt;id&gt;</c>, the ID as a percent-encoded path segment.</summary>
    public static string SessionUri(string sessionId) => SessionScheme + Uri.EscapeDataString(sessionId);

    /// <summary>A chat's URI: <c>ahp-chat:/&lt;id&gt;</c>, with the Gorynych session ID of the session it shows.</summary>
    public static string ChatUri(string sessionId) => ChatScheme + Uri.EscapeDataString(sessionId);

    /// <summary>The Gorynych session ID an <c>ahp-session:</c> URI names; null for another URI.</summary>
    public static string? SessionIdOfSession(string uri) => Id(uri, SessionScheme);

    /// <summary>The Gorynych session ID an <c>ahp-chat:</c> URI names; null for another URI.</summary>
    public static string? SessionIdOfChat(string uri) => Id(uri, ChatScheme);

    static string? Id(string uri, string scheme)
    {
        if (!uri.StartsWith(scheme, StringComparison.Ordinal)) return null;
        var segment = uri[scheme.Length..];
        if (segment.Length == 0 || segment.Contains('/')) return null;
        try { return Uri.UnescapeDataString(segment); }
        catch (UriFormatException) { return null; }
    }

    /// <summary>A folder as a <c>file:</c> URI (<c>file:///C:/Users/x</c> for <c>C:\Users\x</c>); null for none.</summary>
    public static string? FileUri(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return new Uri(path).AbsoluteUri; }
        catch (UriFormatException) { return null; }
    }

    /// <summary>A time as AHP writes it: ISO 8601 in UTC with milliseconds.</summary>
    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A time the service wrote (<c>updatedAt</c>), as AHP writes it; null for none or one that does not parse.</summary>
    public static string? Time(string? iso) =>
        iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? Time(at) : null;

    /// <summary>A JSON value as the AHP types carry one.</summary>
    public static JsonElement Element(JsonNode? node)
    {
        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    public static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
