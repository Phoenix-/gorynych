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

    /// <summary>Only the current user may open the pipe; on Windows remote (network) clients are refused too.</summary>
    static NamedPipeServerStream Create(string name)
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
}
