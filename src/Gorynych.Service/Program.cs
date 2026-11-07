using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Gorynych.Acp.Hosting;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Kimi;
using Gorynych.Service;

// Gorynych.Service [--pipe <name>] [--data <dir>] [--default-agent claude|kimi] [--claude-model <m>] [--no-channel] [--console]
// Gorynych.Service channel --url <url>      (the Claude channel server; Claude starts it over stdio)
if (args.Length > 0 && args[0] == "channel")
    return await ChannelServer.RunAsync(Arg(args, "--url") ?? throw new ArgumentException("--url is required"));

var pipeName = Arg(args, "--pipe") ?? GorynychProtocol.DefaultPipeName;
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

var adapters = new List<IAgentAdapter>
{
    new ClaudeAdapter(new ClaudeAdapterOptions
    {
        DataDirectory = Path.Combine(dataDirectory, "claude"),
        Model = Arg(args, "--claude-model"),
        ChannelServerCommand = args.Contains("--no-channel") ? null : [Environment.ProcessPath!, "channel"],
        Log = ServiceLog.Info,
    }),
    new KimiAdapter(new KimiAdapterOptions { Log = ServiceLog.Info }),
};

await using var conductor = new Conductor(new ConductorOptions
{
    DataDirectory = dataDirectory,
    ServiceId = state.ServiceId,
    DefaultAgent = Arg(args, "--default-agent"),
}, adapters);

using var stop = CancellationTokenSource.CreateLinkedTokenSource(conductor.ShutdownRequested);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(stop); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop(stop);

await Listen(conductor, pipeName, stop.Token);
ServiceLog.Info("service stopping");
return 0;

static async Task Listen(Conductor conductor, string pipeName, CancellationToken ct)
{
    var count = 0;
    while (!ct.IsCancellationRequested)
    {
        var server = CreatePipe(pipeName);
        try
        {
            await server.WaitForConnectionAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await server.DisposeAsync();
            break;
        }
        var name = $"client{++count}";
        _ = Task.Run(() => conductor.ServeClientAsync(server, server, name), CancellationToken.None);
    }
}

// Only the current user may open the pipe; on Windows remote (network) clients are refused too.
static NamedPipeServerStream CreatePipe(string name)
{
    const PipeOptions options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    if (!OperatingSystem.IsWindows())
        return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
    var security = new PipeSecurity();
    using var identity = WindowsIdentity.GetCurrent();
    security.SetOwner(identity.User!);
    security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
    return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
}

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
