namespace Gorynych.Outside;

/// <summary>A tiny log, one file per day: <c>outside-yyyyMMdd.log</c> next to the service's logs. Never stdout, which is the host's.</summary>
internal static class OutsideLog
{
    static readonly Lock Gate = new();
    static string? directory;

    public static void Init(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            directory = logDirectory;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);

    static void Write(string level, string message)
    {
        if (directory is null) return;
        var now = DateTimeOffset.Now;
        lock (Gate)
            try { File.AppendAllText(Path.Combine(directory, $"outside-{now:yyyyMMdd}.log"), $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
