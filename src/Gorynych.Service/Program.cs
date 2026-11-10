using System.Text.Json;
using Gorynych.Acp.Hosting;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Codex;
using Gorynych.Adapters.Dsh;
using Gorynych.Adapters.Kimi;
using Gorynych.Service;

// The commands and their options: CommandLine.Usage. A wrong command line, or --help, prints the
// usage and exits with 2; it never runs the service.
CommandLine line;
try
{
    line = CommandLine.Parse(args);
}
catch (UsageException e)
{
    if (e.Problem is { } problem) Console.Error.WriteLine($"Gorynych.Service: {problem}");
    Console.Error.Write(CommandLine.Usage);
    return CommandLine.UsageExitCode;
}
switch (line.Command)
{
    // The gorynych MCP server agents start over stdio: peer tools, reply, and Claude's channel (TUI) or reply route (headless).
    case "peers":
        return await PeersServer.RunAsync(line.Value("--pipe"), line.Value("--token"), line.Value("--channel-url"), line.Value("--reply-url"));
    // Asks the running service to exit and waits for it; exit code 1 when none runs on the pipe.
    case "shutdown":
        return await AcpBridge.ShutdownAsync(line.Value("--pipe") ?? GorynychProtocol.DefaultPipeName);
    // Asks it to exit for an update without breaking its clients' work; its bridges start the new one.
    case "restart":
        return await AcpBridge.RestartAsync(line.Value("--pipe") ?? GorynychProtocol.DefaultPipeName, !line.Flag("--no-wait"),
            CommandLine.MinutesOf(line.Value("--timeout")) ?? Conductor.DefaultRestartTimeout, line.Flag("--force"));
    // The stdio entry point for external ACP clients (Zed); starts the service when needed.
    case "acp":
        return await AcpBridge.RunAsync(line.Value("--agent")!, line.Value("--pipe") ?? GorynychProtocol.DefaultPipeName, line.ServiceArguments("--pipe"));
    // The Windows logon task that starts the service in this user's session (docs/external-clients.md).
    case "task":
        return TaskCommand(line);
}

var pipeName = line.Value("--pipe") ?? GorynychProtocol.DefaultPipeName;
var channels = Channels(line.Value("--claude-channels"));
var claudeTransport = Transport(line.Value("--claude-transport"));
var dataDirectory = Path.GetFullPath(line.Value("--data")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gorynych"));
Directory.CreateDirectory(dataDirectory);
ServiceLog.Init(Path.Combine(dataDirectory, "logs"), console: line.Flag("--console"));
// Started by the logon task, it has a console window of its own: let it go, so that closing it cannot stop the service.
if (!line.Flag("--console") && Gorynych.Outside.DetachedProcess.ReleaseOwnConsole())
    ServiceLog.Info("service: let go of its own console window");

// One service per user and endpoint, across logon sessions (RDP included).
using var instance = new Mutex(initiallyOwned: false, "Gorynych.Service." + pipeName,
    new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
bool owned;
try
{
    owned = instance.WaitOne(0);
    if (!owned)
    {
        // A bridge starts a service when the pipe is missing: the one that holds the mutex may be
        // on its way out (a restart), so this one waits a while for it rather than leave nobody.
        ServiceLog.Info($"another service holds {pipeName}; waiting up to 15 s for it to exit");
        owned = instance.WaitOne(TimeSpan.FromSeconds(15));
    }
}
catch (AbandonedMutexException) { owned = true; }
if (!owned)
{
    ServiceLog.Info($"another service already serves {pipeName}; exiting");
    return 3;
}

var state = LoadState(Path.Combine(dataDirectory, "service.json"));
ServiceLog.Info($"service {state.ServiceId} starting, pid {Environment.ProcessId}, pipe {pipeName}, data {dataDirectory}");
// Started without breaking away from a host's job (the stdio bridge, the outside server): say so.
if (Gorynych.Outside.DetachedProcess.KillOnCloseJob() is { } job) ServiceLog.Warn($"service: {job}");

var adapters = new List<IAgentAdapter>
{
    new ClaudeAdapter(new ClaudeAdapterOptions
    {
        DataDirectory = Path.Combine(dataDirectory, "claude"),
        Transport = claudeTransport,
        Model = line.Value("--claude-model"),
        ChannelServerCommand = line.Flag("--no-channel") ? null : [Environment.ProcessPath!, "peers"],
        Channels = channels,
        Log = ServiceLog.Info,
    }),
    new KimiAdapter(new KimiAdapterOptions { Log = ServiceLog.Info }),
    new CodexAdapter(new CodexAdapterOptions { Log = ServiceLog.Info, ClientVersion = AgentHost.Version }),
    new DshAdapter(new DshAdapterOptions
    {
        DataDirectory = Path.Combine(dataDirectory, "dsh"),
        Instructions = line.Flag("--no-peers") ? null : PeersServer.SystemPrompt,
        Log = ServiceLog.Info,
    }),
};

await using var conductor = new Conductor(new ConductorOptions
{
    DataDirectory = dataDirectory,
    ServiceId = state.ServiceId,
    DefaultAgent = line.Value("--default-agent"),
    UnloadAfter = UnloadAfter(line.Value("--unload-after")),
    PeerServerCommand = line.Flag("--no-peers") ? null : [Environment.ProcessPath!, "peers", "--pipe", pipeName],
}, adapters);

// Drafts a client left behind in an earlier run (Zed quits without closing them), once the bridges
// that come back after a restart had the time to resume theirs.
_ = Task.Run(() => conductor.SweepDraftsAsync(ConductorOptions.DefaultComeBackGrace));

using var stop = CancellationTokenSource.CreateLinkedTokenSource(conductor.ShutdownRequested);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(stop); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop(stop);

await PipeListener.RunAsync(conductor, pipeName, stop.Token);
ServiceLog.Info("service stopping");
return 0;

static ServiceState LoadState(string path)
{
    if (File.Exists(path))
        try
        {
            if (JsonSerializer.Deserialize(File.ReadAllText(path), ServiceJsonContext.Default.ServiceState) is { ServiceId.Length: > 0 } existing)
                return existing;
        }
        catch (JsonException) { }
    var created = new ServiceState { ServiceId = Guid.NewGuid().ToString(), CreatedAt = DateTimeOffset.UtcNow };
    File.WriteAllText(path, JsonSerializer.Serialize(created, ServiceJsonContext.Default.ServiceState));
    return created;
}

static void Stop(CancellationTokenSource cts)
{
    try { cts.Cancel(); } catch (ObjectDisposedException) { }
}

// task install|uninstall [--pipe <name>] [service options]: registers or removes the logon task that starts
// this executable with these options in the user's session, with a limited token; it works from an elevated
// ssh login too, and the bridge and the outside server run it when the pipe is missing.
static int TaskCommand(CommandLine line)
{
    var pipe = line.Value("--pipe") ?? GorynychProtocol.DefaultPipeName;
    var name = Gorynych.Outside.LogonTask.Name(pipe);
    if (line.Action == "uninstall")
    {
        var (removed, said) = Gorynych.Outside.LogonTask.Uninstall(pipe);
        if (removed != 0)
        {
            Console.Error.WriteLine($"gorynych task: cannot remove the logon task {name}: {said}");
            return removed;
        }
        Console.WriteLine($"gorynych task: removed the logon task {name}. A running service keeps running; " +
            $"Gorynych.Service shutdown{(line.Value("--pipe") is null ? "" : $" --pipe {pipe}")} stops it.");
        return 0;
    }
    var exe = Environment.ProcessPath!;
    var serviceArgs = line.ServiceArguments();
    // The task starts in the executable's folder, so a relative data folder is made absolute here.
    var data = serviceArgs.IndexOf("--data");
    if (data >= 0) serviceArgs[data + 1] = Path.GetFullPath(serviceArgs[data + 1]);
    var (code, output) = Gorynych.Outside.LogonTask.Install(pipe, exe, serviceArgs);
    if (code != 0)
    {
        Console.Error.WriteLine($"gorynych task: cannot register the logon task {name}: {output}");
        return code;
    }
    Console.WriteLine($"gorynych task: registered the logon task {name}. At this user's logon it runs " +
        $"{string.Join(' ', new[] { exe }.Concat(serviceArgs).Select(Gorynych.Outside.DetachedProcess.Quote))} " +
        "in their session with a limited token. The bridge and the outside server run it whenever the service's pipe is " +
        $"missing; to start it now: schtasks /Run /TN {name}");
    return 0;
}

// --claude-channels ask|accept|decline: whether Claude may be started with development channels, a Claude Code
// research preview whose notice the adapter then confirms on each launch. ask (the default) asks the person once
// and keeps the answer in <data>\claude\channels.json; accept and decline answer up front, for headless use.
static ChannelConsent Channels(string? value) => value switch
{
    null or "ask" => ChannelConsent.Ask,
    "accept" => ChannelConsent.Accept,
    "decline" => ChannelConsent.Decline,
    _ => throw new ArgumentException($"--claude-channels takes ask, accept or decline; got '{value}'"),
};

// --claude-transport headless|tui: how the Claude adapter drives Claude Code. headless (the default) runs
// `claude -p` with stream-json; tui runs the interactive TUI in a pseudo console, the fallback (Windows only).
static ClaudeTransport Transport(string? value) => value switch
{
    null or "headless" => ClaudeTransport.Headless,
    "tui" => ClaudeTransport.Tui,
    _ => throw new ArgumentException($"--claude-transport takes headless or tui; got '{value}'"),
};

// --unload-after <minutes>: how long an idle session with no client stays loaded in its agent; 0 unloads at once.
static TimeSpan UnloadAfter(string? minutes)
{
    if (minutes is null) return ConductorOptions.DefaultUnloadAfter;
    if (double.TryParse(minutes, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 0)
        return TimeSpan.FromMinutes(m);
    throw new ArgumentException($"--unload-after takes a number of minutes, 0 or more; got '{minutes}'");
}
