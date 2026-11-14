namespace Gorynych.AdoptClaude;

public static class AdoptionCommand
{
    const string Usage = "Usage: gorynych-adopt-claude <session-id> --cwd <folder> [--data <Gorynych data folder>]\n"
        + "       gorynych-adopt-claude --list [--cwd <folder>] [--limit <1..1000>]";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["-h"])
        {
            output.WriteLine(Usage);
            output.WriteLine("--list reads saved Claude sessions, newest first (local modification date, name, ID, folder); default limit: 50.");
            output.WriteLine("Before adopting, close the original Claude session. Listing works while it is running.");
            return 0;
        }
        string? id = null, cwd = null, data = null;
        var list = false;
        int? limit = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--list")
            {
                if (list) return BadUsage();
                list = true;
            }
            else if (arg == "--limit")
            {
                if (limit is not null || ++i == args.Length || !int.TryParse(args[i], out var n) || n is < 1 or > 1000) return BadUsage();
                limit = n;
            }
            else if (arg is "--cwd" or "--data")
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)
                    || (arg == "--cwd" ? cwd : data) is not null) return BadUsage();
                if (arg == "--cwd") cwd = args[i]; else data = args[i];
            }
            else if (arg.StartsWith('-') || id is not null) return BadUsage();
            else id = arg;
        }
        if (list ? id is not null || data is not null : id is null || string.IsNullOrWhiteSpace(cwd) || limit is not null) return BadUsage();
        try
        {
            if (list)
            {
                var sessions = NativeClaudeSessions.List(cwd, limit ?? 50, warning: error.WriteLine);
                output.WriteLine("LAST MODIFIED (local)  SESSION ID                            NAME / WORKING DIRECTORY");
                foreach (var session in sessions)
                {
                    output.WriteLine($"{session.ModifiedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}   {session.SessionId}  {NativeClaudeSessions.OneLine(session.Title)}");
                    output.WriteLine($"                                                            {NativeClaudeSessions.OneLine(session.Cwd ?? "(unknown folder)")}");
                }
                output.WriteLine(sessions.Count == 0 ? "No saved Claude sessions found." : $"Shown: {sessions.Count} (limit: {limit ?? 50}). Use --cwd to filter, --limit to show more.");
                return 0;
            }
            data ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gorynych");
            var added = ClaudeAdoption.Register(data, id!, cwd!);
            output.WriteLine(added ? $"Registered Claude session {id}." : $"Claude session {id} is already registered; kept its settings.");
            output.WriteLine("Refresh the GUI session list/import dialog, or reopen the GUI. No service restart is needed.");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            error.WriteLine($"Could not {(list ? "list Claude sessions" : "register Claude session")}: {e.Message}");
            return 1;
        }

        int BadUsage() { error.WriteLine(Usage); return 2; }
    }
}
