namespace Gorynych.Service;

/// <summary>The one spelling of a session's folder that the service lists and keeps.</summary>
internal static class FolderPath
{
    /// <summary>
    /// Kimi reports <c>C:/Users/x</c> for the folder Zed calls <c>C:\Users\x</c>, and a client
    /// that compares folders as text sees two projects. On Windows this gives backslashes, an
    /// upper-case drive letter and no <c>.</c> or <c>..</c> segments; everywhere, no trailing
    /// separator except on a root. Other casing stays as given: the real casing would cost a
    /// look at the disk for every listed session.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        var p = path;
        if (OperatingSystem.IsWindows())
        {
            p = p.Replace('/', '\\');
            if (Path.IsPathFullyQualified(p))
            {
                try { p = Path.GetFullPath(p); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetterLower(p[0])) p = char.ToUpperInvariant(p[0]) + p[1..];
        }
        return Path.TrimEndingDirectorySeparator(p);
    }
}
