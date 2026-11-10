namespace Gorynych.Service;

/// <summary>A tiny log: one file per day under the data directory, and stderr when attached.</summary>
public static class ServiceLog
{
    static readonly Lock Gate = new();
    static string? directory;
    static string prefix = "service";

    public static bool Console { get; set; }

    /// <param name="name">The files' name before the date: <c>service</c>, or <c>host-claude</c> for an adapter host.</param>
    public static void Init(string logDirectory, bool console, string name = "service")
    {
        Directory.CreateDirectory(logDirectory);
        directory = logDirectory;
        prefix = name;
        Console = console;
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message) => Write("ERR", message);

    static void Write(string level, string message)
    {
        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        lock (Gate)
        {
            if (directory is not null)
                try { File.AppendAllText(Path.Combine(directory, $"{prefix}-{now:yyyyMMdd}.log"), line + Environment.NewLine); }
                catch (IOException) { }
            if (Console) System.Console.Error.WriteLine(line);
        }
    }
}
