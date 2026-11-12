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
          Gorynych.Service acp [--agent <agent>] [--pipe <name>] [service options]
              the stdio bridge for ACP clients such as Zed; starts the service when its pipe is missing.
              --agent scopes it to one agent: claude, kimi, codex, dsh, or a remote one, as claude@<machine>;
              without it the client sees every agent (a hub uses it so, through ssh)
          Gorynych.Service shutdown [--pipe <name>]
              asks the running service to exit and waits for it
          Gorynych.Service status [--pipe <name>]
              shows the running service and its adapter hosts: their build, sessions and running turns,
              and which sessions still wait to move to a host of the current build after an update
          Gorynych.Service restart [--pipe <name>] [--timeout <minutes>] [--no-wait] [--force]
              asks the running service to exit for an update: it starts no new turn, waits for the
              running ones (5 minutes at most by default; none with --no-wait), and its bridges start
              the new one; if turns still run then, it stays (exit code 4), unless --force cancels them
          Gorynych.Service remote add <machine> [--data <dir>] -- <command> [arguments]
          Gorynych.Service remote remove <machine> [--data <dir>]
          Gorynych.Service remote list [--pipe <name>] [--data <dir>]
              the services of other machines this one shows as its own agents (claude@<machine>): the
              command reaches the bridge there, for example: ssh -T <host> Gorynych.Service.exe acp;
              a running service takes a change at once; list shows whether each is connected
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
          --hosts on|off                        run each adapter in a host of its own, which a restart leaves
                                                running (default on Windows and Linux; off on macOS, where it is untried)
          --no-channel  --no-peers
          --console                             also log to the console (not for acp and task install)

        The service starts its adapter hosts itself, as
          Gorynych.Service host --adapter claude|kimi|codex|dsh [--pipe <name>] [--data <dir>] [adapter options]

        """;

    static readonly string[] ServiceValues =
        ["--pipe", "--data", "--default-agent", "--claude-model", "--unload-after", "--claude-transport", "--claude-channels", "--hosts"];

    /// <summary>The options an adapter host takes: those of the adapters, and where the service is.</summary>
    static readonly string[] HostValues = ["--adapter", "--slot", "--pipe", "--data", "--claude-model", "--claude-transport", "--claude-channels"];

    /// <summary>The adapters a host runs, as the service names them.</summary>
    public static readonly string[] AdapterNames = ["claude", "kimi", "codex", "dsh"];
    static readonly string[] ServiceFlags = ["--no-channel", "--no-peers"];

    readonly List<(string Name, string? Value)> options = [];

    CommandLine(string? command, string? action)
    {
        Command = command;
        Action = action;
    }

    /// <summary>null for the service itself, else <c>acp</c>, <c>shutdown</c>, <c>restart</c>, <c>task</c> or <c>peers</c>.</summary>
    public string? Command { get; }

    /// <summary><c>install</c> or <c>uninstall</c> for <c>task</c>; <c>add</c>, <c>remove</c> or <c>list</c> for <c>remote</c>.</summary>
    public string? Action { get; }

    /// <summary>For <c>remote add</c> and <c>remote remove</c>: the machine's name.</summary>
    public string? Target { get; private set; }

    /// <summary>For <c>remote add</c>: the command and its arguments, after <c>--</c>.</summary>
    public IReadOnlyList<string> Rest { get; private set; } = [];

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
            case "shutdown" or "status":
                known = (["--pipe"], []);
                break;
            case "restart":
                known = (["--pipe", "--timeout"], ["--no-wait", "--force"]);
                break;
            case "host":
                known = (HostValues, [.. ServiceFlags, "--console"]);
                break;
            case "peers":
                known = (["--pipe", "--token", "--channel-url", "--reply-url"], []);
                break;
            case "remote":
                action = args.Length > 1 ? args[1] : null;
                rest = 2;
                known = action switch
                {
                    _ when IsHelp(action) => throw new UsageException(null),
                    "add" or "remove" => (["--data"], []),
                    "list" => (["--pipe", "--data"], []),
                    null => throw new UsageException("remote takes add, remove or list"),
                    _ => throw new UsageException($"remote takes add, remove or list, not '{action}'"),
                };
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
        if (command == "remote" && action is "add" or "remove")
        {
            if (args.Length <= 2 || args[2].StartsWith('-')) throw new UsageException($"remote {action} needs the machine's name");
            line.Target = args[2];
            rest = 3;
        }
        var end = args.Length;
        if (command == "remote" && action == "add")
        {
            // Everything after "--" is the command that reaches the other machine's bridge, as it is.
            var dashes = Array.IndexOf(args, "--", rest);
            if (dashes < 0 || dashes == args.Length - 1) throw new UsageException("remote add needs the command after --, as: remote add <machine> -- ssh -T <host> Gorynych.Service.exe acp");
            line.Rest = args[(dashes + 1)..];
            end = dashes;
        }
        for (var i = rest; i < end; i++)
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
        if (command == "host" && line.Value("--adapter") is null) throw new UsageException("host needs --adapter");
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
            "--hosts" => value is "on" or "off",
            "--adapter" => AdapterNames.Contains(value),
            _ => true,
        };
        if (ok) return;
        throw new UsageException(name switch
        {
            "--claude-transport" => $"--claude-transport takes headless or tui, not '{value}'",
            "--claude-channels" => $"--claude-channels takes ask, accept or decline, not '{value}'",
            "--hosts" => $"--hosts takes on or off, not '{value}'",
            "--adapter" => $"--adapter takes {string.Join(", ", AdapterNames)}, not '{value}'",
            _ => $"{name} takes a number of minutes, 0 or more, not '{value}'",
        });
    }

    /// <summary>A number of minutes, 0 or more, that makes a time span; null for anything else.</summary>
    public static TimeSpan? MinutesOf(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m >= 0 && Minutes(m) ? TimeSpan.FromMinutes(m) : null;
}
