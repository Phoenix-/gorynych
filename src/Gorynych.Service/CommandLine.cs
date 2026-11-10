using System.Globalization;

namespace Gorynych.Service;

/// <summary>A wrong command line, or a request for help: the usage is printed and the exit code is 2.</summary>
internal sealed class UsageException(string? problem) : Exception(problem ?? "usage")
{
    /// <summary>What is wrong; null when help was asked for.</summary>
    public string? Problem { get; } = problem;
}

/// <summary>
/// The service executable's command line: a command (none runs the service) and its options.
/// Every option must be one the command knows, and an option that takes a value must have one,
/// so a typo or <c>--help</c> never starts the service in the foreground.
/// </summary>
internal sealed class CommandLine
{
    public const int UsageExitCode = 2;

    public const string Usage = """
        Usage:
          Gorynych.Service [serve] [service options]
              runs the service in the foreground (docs/service-protocol.md)
          Gorynych.Service acp --agent claude|kimi|codex|dsh [--pipe <name>] [service options]
              the stdio bridge for ACP clients such as Zed; starts the service when its pipe is missing
          Gorynych.Service shutdown [--pipe <name>]
              asks the running service to exit and waits for it
          Gorynych.Service restart [--pipe <name>] [--timeout <minutes>] [--no-wait] [--force]
              asks the running service to exit for an update: it starts no new turn, waits for the
              running ones (5 minutes at most by default; none with --no-wait), and its bridges start
              the new one; if turns still run then, it stays (exit code 4), unless --force cancels them
          Gorynych.Service task install [--pipe <name>] [service options]
          Gorynych.Service task uninstall [--pipe <name>]
              registers or removes the Windows logon task that starts the service in this user's session
          Gorynych.Service peers [--pipe <name> --token <token>] [--channel-url <url> | --reply-url <url>]
              the MCP server the service gives its agents

        Service options:
          --pipe <name>                         the endpoint (default gorynych-acp-<user name>)
          --data <dir>                          the data folder (default %LOCALAPPDATA%\Gorynych)
          --default-agent claude|kimi|codex|dsh
          --claude-model <model>
          --unload-after <minutes>              how long an idle session with no client stays loaded (default 10)
          --claude-transport headless|tui
          --claude-channels ask|accept|decline
          --no-channel  --no-peers
          --console                             also log to the console (not for acp and task install)

        """;

    static readonly string[] ServiceValues =
        ["--pipe", "--data", "--default-agent", "--claude-model", "--unload-after", "--claude-transport", "--claude-channels"];
    static readonly string[] ServiceFlags = ["--no-channel", "--no-peers"];

    readonly List<(string Name, string? Value)> options = [];

    CommandLine(string? command, string? action)
    {
        Command = command;
        Action = action;
    }

    /// <summary>null for the service itself, else <c>acp</c>, <c>shutdown</c>, <c>restart</c>, <c>task</c> or <c>peers</c>.</summary>
    public string? Command { get; }

    /// <summary><c>install</c> or <c>uninstall</c> for <c>task</c>.</summary>
    public string? Action { get; }

    public string? Value(string name) => options.FirstOrDefault(o => o.Name == name).Value;

    public bool Flag(string name) => options.Any(o => o.Name == name);

    /// <summary>The service options given, in order, as arguments for a service this command starts.</summary>
    public List<string> ServiceArguments(params string[] except)
    {
        var list = new List<string>();
        foreach (var (name, value) in options)
        {
            if (except.Contains(name) || !(ServiceValues.Contains(name) || ServiceFlags.Contains(name) || name == "--console")) continue;
            list.Add(name);
            if (value is not null) list.Add(value);
        }
        return list;
    }

    /// <exception cref="UsageException">The command line is wrong, or asks for help.</exception>
    public static CommandLine Parse(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;
        var rest = command is null ? 0 : 1;
        // "serve" names the service itself, as no command does; it was ignored before, and starts still use it.
        if (command == "serve") command = null;
        string? action = null;
        (string[] Values, string[] Flags) known;
        switch (command)
        {
            case "help" or "/?":
                throw new UsageException(null);
            case null:
                known = (ServiceValues, [.. ServiceFlags, "--console"]);
                break;
            case "acp":
                known = ([.. ServiceValues, "--agent"], ServiceFlags);
                break;
            case "shutdown":
                known = (["--pipe"], []);
                break;
            case "restart":
                known = (["--pipe", "--timeout"], ["--no-wait", "--force"]);
                break;
            case "peers":
                known = (["--pipe", "--token", "--channel-url", "--reply-url"], []);
                break;
            case "task":
                action = args.Length > 1 ? args[1] : null;
                rest = 2;
                known = action switch
                {
                    _ when IsHelp(action) => throw new UsageException(null),
                    "install" => (ServiceValues, ServiceFlags),
                    "uninstall" => (["--pipe"], []),
                    null => throw new UsageException("task takes install or uninstall"),
                    _ => throw new UsageException($"task takes install or uninstall, not '{action}'"),
                };
                break;
            default:
                throw new UsageException($"unknown command '{command}'");
        }

        var line = new CommandLine(command, action);
        for (var i = rest; i < args.Length; i++)
        {
            var name = args[i];
            string? value = null;
            if (IsHelp(name)) throw new UsageException(null);
            if (known.Values.Contains(name))
            {
                // A value is never empty and never an option: "--pipe --data d" lacks the pipe.
                if (i + 1 >= args.Length || args[i + 1].Length == 0 || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new UsageException($"{name} needs a value");
                value = args[++i];
                Check(name, value);
            }
            else if (!known.Flags.Contains(name))
                throw new UsageException(command is null ? $"unknown option '{name}'" : $"{command}{(action is null ? "" : " " + action)} does not take '{name}'");
            if (line.Flag(name)) throw new UsageException($"{name} is given twice");
            line.options.Add((name, value));
        }
        if (command == "acp" && line.Value("--agent") is null) throw new UsageException("acp needs --agent");
        return line;
    }

    static bool IsHelp(string? arg) => arg is "--help" or "-h" or "-?" or "/?";

    /// <summary>Whether that many minutes make a time span: not infinite, not too long.</summary>
    static bool Minutes(double minutes)
    {
        try
        {
            TimeSpan.FromMinutes(minutes);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>The values with a fixed set, checked here so that a wrong one never reaches a started service or a task.</summary>
    static void Check(string name, string value)
    {
        var ok = name switch
        {
            "--claude-transport" => value is "headless" or "tui",
            "--claude-channels" => value is "ask" or "accept" or "decline",
            "--unload-after" or "--timeout" => MinutesOf(value) is not null,
            _ => true,
        };
        if (ok) return;
        throw new UsageException(name switch
        {
            "--claude-transport" => $"--claude-transport takes headless or tui, not '{value}'",
            "--claude-channels" => $"--claude-channels takes ask, accept or decline, not '{value}'",
            _ => $"{name} takes a number of minutes, 0 or more, not '{value}'",
        });
    }

    /// <summary>A number of minutes, 0 or more, that makes a time span; null for anything else.</summary>
    public static TimeSpan? MinutesOf(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m >= 0 && Minutes(m) ? TimeSpan.FromMinutes(m) : null;
}
