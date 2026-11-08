using System.Reflection;
using Gorynych.Acp.Hosting;
using Gorynych.Outside;

// Gorynych.Outside [--pipe <name>] [--service <path>] [--log <dir>]
//   the gorynych-outside MCP server, for agents outside Gorynych: a stdio server that passes MCP
//   requests to the service as _gorynych/mcp_message (docs/service-protocol.md, "Agents outside Gorynych").
//   --service is the service exe to start when its pipe is missing (default: Gorynych.Service.exe in
//   the parent folder, as published); --log the log folder (default: %LOCALAPPDATA%\Gorynych\logs).
var pipe = Arg("--pipe") ?? GorynychProtocol.DefaultPipeName;
var service = Path.GetFullPath(Arg("--service") ?? Path.Combine(AppContext.BaseDirectory, "..", "Gorynych.Service.exe"));
OutsideLog.Init(Arg("--log") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gorynych", "logs"));

var version = typeof(OutsideServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
// Decided once: a process's ancestors are fixed when it starts.
var ancestry = Ancestry.OfCurrentProcess();
OutsideLog.Info($"pid {Environment.ProcessId}, pipe {pipe}, folder {Environment.CurrentDirectory}: " +
    (ancestry.Inside ? "inside Gorynych, no tools" : "outside Gorynych") + $" ({ancestry.Why}; ancestors: {string.Join(" < ", ancestry.Chain)})");

var server = new OutsideServer(pipe, service, ancestry.Inside, version);
await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
OutsideLog.Info($"pid {Environment.ProcessId}: the host closed stdin");
return 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
