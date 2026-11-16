using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using Gorynych.Acp.Hosting;
using Gorynych.Outside;

// Gorynych.Outside [--pipe <name>] [--service <path>] [--log <dir>] [--mailbox-file <path>]
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

var mailboxPath = Arg("--mailbox-file");
var mailbox = mailboxPath is null ? null : MailboxFile(Path.GetFullPath(mailboxPath));
var server = new OutsideServer(pipe, service, ancestry.Inside, version, mailbox);
await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
OutsideLog.Info($"pid {Environment.ProcessId}: the host closed stdin");
return 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// A new token file is created with private permissions before writing, then atomically moved
// into place. Concurrent shims using the same path converge on the winner's token.
static string MailboxFile(string path)
{
    if (!File.Exists(path))
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = CreatePrivate(temp))
            {
                var bytes = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    if (OperatingSystem.IsWindows()) new FileInfo(path).SetAccessControl(PrivateSecurity());
    else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    var token = File.ReadAllText(path, Encoding.ASCII);
    return OutsideServer.ValidMailbox(token) ? token : throw new InvalidDataException("Invalid outside mailbox token file");
}

static FileStream CreatePrivate(string path) => OperatingSystem.IsWindows()
    ? new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, PrivateSecurity())
    : new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });

[SupportedOSPlatform("windows")]
static FileSecurity PrivateSecurity()
{
    using var identity = WindowsIdentity.GetCurrent();
    var security = new FileSecurity();
    security.SetAccessRuleProtection(true, false);
    security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
    return security;
}
