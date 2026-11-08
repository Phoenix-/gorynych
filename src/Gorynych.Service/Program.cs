using System.Text.Json;
using Gorynych.Acp.Hosting;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Kimi;
using Gorynych.Service;

// Gorynych.Service [--pipe <name>] [--data <dir>] [--default-agent claude|kimi] [--claude-model <m>] [--unload-after <minutes>]
//   [--claude-channels ask|accept|decline] [--no-channel] [--no-peers] [--console]
// Gorynych.Service peers [--pipe <name> --token <t>] [--channel-url <url>]
//   the gorynych MCP server agents start over stdio: peer tools, reply, and Claude's channel
if (args.Length > 0 && args[0] == "peers")
    return await PeersServer.RunAsync(Arg(args, "--pipe"), Arg(args, "--token"), Arg(args, "--channel-url"));
// Gorynych.Service acp --agent claude|kimi [--pipe <name>] [service options]
//   the stdio entry point for external ACP clients (Zed); starts the service when needed
if (args.Length > 0 && args[0] == "acp")
{
    var agent = Arg(args, "--agent") ?? throw new ArgumentException("--agent is required");
    var forwarded = new List<string>();
    foreach (var option in new[] { "--data", "--claude-model", "--default-agent", "--unload-after", "--claude-channels" })
        if (Arg(args, option) is { } value) forwarded.AddRange([option, value]);
    foreach (var flag in new[] { "--no-channel", "--no-peers" })
        if (args.Contains(flag)) forwarded.Add(flag);
    return await AcpBridge.RunAsync(agent, Arg(args, "--pipe") ?? GorynychProtocol.DefaultPipeName, forwarded);
}

var pipeName = Arg(args, "--pipe") ?? GorynychProtocol.DefaultPipeName;
var channels = Channels(Arg(args, "--claude-channels"));
var dataDirectory = Path.GetFullPath(Arg(args, "--data")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gorynych"));
Directory.CreateDirectory(dataDirectory);
ServiceLog.Init(Path.Combine(dataDirectory, "logs"), console: args.Contains("--console"));

// One service per user and endpoint, across logon sessions (RDP included).
using var instance = new Mutex(initiallyOwned: false, "Gorynych.Service." + pipeName,
    new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
bool owned;
try { owned = instance.WaitOne(0); }
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
        Model = Arg(args, "--claude-model"),
        ChannelServerCommand = args.Contains("--no-channel") ? null : [Environment.ProcessPath!, "peers"],
        Channels = channels,
        Log = ServiceLog.Info,
    }),
    new KimiAdapter(new KimiAdapterOptions { Log = ServiceLog.Info }),
};

await using var conductor = new Conductor(new ConductorOptions
{
    DataDirectory = dataDirectory,
    ServiceId = state.ServiceId,
    DefaultAgent = Arg(args, "--default-agent"),
    UnloadAfter = UnloadAfter(Arg(args, "--unload-after")),
    PeerServerCommand = args.Contains("--no-peers") ? null : [Environment.ProcessPath!, "peers", "--pipe", pipeName],
}, adapters);

// Drafts a client left behind in an earlier run (Zed quits without closing them).
_ = Task.Run(conductor.SweepDraftsAsync);

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

static string? Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
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

// --unload-after <minutes>: how long an idle session with no client stays loaded in its agent; 0 unloads at once.
static TimeSpan UnloadAfter(string? minutes)
{
    if (minutes is null) return ConductorOptions.DefaultUnloadAfter;
    if (double.TryParse(minutes, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 0)
        return TimeSpan.FromMinutes(m);
    throw new ArgumentException($"--unload-after takes a number of minutes, 0 or more; got '{minutes}'");
}
