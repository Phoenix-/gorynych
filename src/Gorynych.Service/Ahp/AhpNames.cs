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

    /// <summary>
    /// The URI of a session's own chat: <c>ahp-chat:/</c> with its session URI's path, so that a session an AHP
    /// client created under a URI of its choosing has its chat under the same <c>&lt;id&gt;</c>.
    /// </summary>
    public static string ChatOf(string sessionUri) => ChatScheme + sessionUri[SessionScheme.Length..];

    /// <summary>Whether a URI is an <c>ahp-session:/&lt;id&gt;</c> of one path segment.</summary>
    public static bool IsSessionUri(string uri) => Id(uri, SessionScheme) is not null;

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

    /// <summary>
    /// A local path as a <c>file:</c> URI (RFC 8089): <c>file:///C:/Users/x</c> for <c>C:\Users\x</c>, <c>file:///home/x</c>
    /// for <c>/home/x</c>, <c>file://host/share/x</c> for <c>\\host\share\x</c>; each segment percent-encoded as UTF-8 but
    /// its unreserved characters, <c>%</c> among the rest (.NET's <see cref="Uri"/> leaves a <c>%41</c> in a path as it is,
    /// which a reader takes for <c>A</c>). Null for none, or a path that is not absolute.
    /// </summary>
    public static string? FileUri(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        // Windows takes either slash between folders; elsewhere a backslash is a character of a name.
        string Segments(string rest) => string.Join('/', rest.Split(OperatingSystem.IsWindows() ? ['/', '\\'] : ['/']).Select(Uri.EscapeDataString));
        if (OperatingSystem.IsWindows())
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2) // another machine's share
            {
                var host = path[2..].Split('\\', 2);
                if (host[0] is "?" or "." || host[0].Length == 0) return null; // a device path (\\?\x, \\.\x) names no file a URI can
                return $"file://{Uri.EscapeDataString(host[0])}/{(host.Length > 1 ? Segments(host[1]) : "")}";
            }
            // A drive's path; not one relative to a drive's current folder (C:x, C:).
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return null;
            return $"file:///{char.ToUpperInvariant(path[0])}:/{Segments(path[2..].TrimStart('\\', '/'))}";
        }
        return path.StartsWith('/') ? $"file:///{Segments(path[1..])}" : null;
    }

    /// <summary>
    /// The local path a client's <c>file:</c> URI names (RFC 8089), read as VS Code writes one: <c>file:///c%3A/Users/x</c>
    /// and <c>file:///C:/Users/x</c> are both <c>C:\Users\x</c> (the drive letter upper-cased, as Windows writes it), the
    /// path percent-decoded as UTF-8, no trailing separator but a root's. Null for a URI that names no file of this host:
    /// another scheme, another machine's (<c>file://host/…</c>; <c>localhost</c> is this one), one with a query or a
    /// fragment, or on Windows one with no drive. .NET's <see cref="Uri"/> is not used: it reads <c>c%3A</c> as no drive.
    /// </summary>
    public static string? FilePath(string? uri)
    {
        const string scheme = "file:";
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri[scheme.Length..];
        if (rest.AsSpan().IndexOfAny('?', '#') >= 0) return null;
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var slash = rest.IndexOf('/', 2);
            var host = slash < 0 ? rest[2..] : rest[2..slash];
            if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null;
            rest = slash < 0 ? "/" : rest[slash..];
        }
        if (!rest.StartsWith('/')) return null;
        var path = Uri.UnescapeDataString(rest);
        if (OperatingSystem.IsWindows())
        {
            // "/c:/Users/x" or "/c:": a drive, then its path.
            if (path.Length < 3 || path[0] != '/' || !char.IsAsciiLetter(path[1]) || path[2] != ':' || (path.Length > 3 && path[3] is not ('/' or '\\')))
                return null;
            path = $"{char.ToUpperInvariant(path[1])}:\\{path[Math.Min(4, path.Length)..].Replace('/', '\\')}";
        }
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            // Never a device or another machine's path (\\.\x, \\?\x, \\host\x), whatever the path made of itself.
            return OperatingSystem.IsWindows() && !(full.Length >= 3 && char.IsAsciiLetter(full[0]) && full[1] == ':' && full[2] == '\\') ? null : full;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
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
