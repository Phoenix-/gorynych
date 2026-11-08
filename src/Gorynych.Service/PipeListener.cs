using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Gorynych.Service;

/// <summary>Accepts clients on the service's named pipe and hands each to the conductor.</summary>
public static class PipeListener
{
    public static async Task RunAsync(Conductor conductor, string pipeName, CancellationToken ct)
    {
        var count = 0;
        while (!ct.IsCancellationRequested)
        {
            var server = Create(pipeName);
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

    /// <summary>
    /// Only the current user may open the pipe, at the service's own elevation; on Windows remote
    /// (network) clients are refused too.
    /// </summary>
    internal static NamedPipeServerStream Create(string name)
    {
        const PipeOptions options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
    }

    /// <summary>
    /// What <see cref="PipeOptions.CurrentUserOnly"/> gives a server, plus a deny for network
    /// clients (the two cannot be combined). Its owner and only allowed principal is the default
    /// owner of the service's token, the SID that the clients' <see cref="PipeOptions.CurrentUserOnly"/>
    /// check compares with their own: the user, or BUILTIN\Administrators when elevated. So an
    /// elevated service serves only elevated clients, and a plain one only plain ones: a plain
    /// process can never drive an elevated service, which runs agents. (The user's SID as owner
    /// broke elevated clients, dotnet/runtime#123903.)
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static PipeSecurity Security()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.Owner ?? identity.User!;
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }
}
