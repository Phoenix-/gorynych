using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Gorynych.Service;

/// <summary>Accepts clients on the service's named pipe and hands each to the conductor.</summary>
public static partial class PipeListener
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
    /// Only the current user may open the pipe, and on Windows only from this machine:
    /// <c>PIPE_REJECT_REMOTE_CLIENTS</c> refuses clients over the network (SMB), while a local
    /// process of the user that an ssh server started gets in (docs/service-protocol.md, Endpoint).
    /// </summary>
    /// <param name="rejectRemoteClients">False only for tests, to compare with a pipe that lets remote clients in.</param>
    internal static NamedPipeServerStream Create(string name, bool rejectRemoteClients = true)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        return Create(name, Security(), rejectRemoteClients);
    }

    /// <summary>
    /// What <see cref="PipeOptions.CurrentUserOnly"/> gives a server: its owner and only allowed
    /// principal is the default owner of the service's token, the user, or BUILTIN\Administrators
    /// when elevated. So a plain process can never open an elevated service's pipe, and an elevated
    /// service runs agents. The other way round, an elevated client of the same user opens a plain
    /// service's pipe: its token has the user's SID, and its own owner check takes the user as
    /// owner (<c>ServicePipe</c>). (The user's SID as owner of an elevated
    /// service's pipe broke elevated clients of .NET's own check, dotnet/runtime#123903.)
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static PipeSecurity Security()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return Security(identity.Owner ?? identity.User!);
    }

    [SupportedOSPlatform("windows")]
    internal static PipeSecurity Security(SecurityIdentifier owner)
    {
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// <c>NamedPipeServerStreamAcl.Create</c> with <c>PIPE_REJECT_REMOTE_CLIENTS</c>, which .NET 10
    /// never sets: it passes only the
    /// transmission mode as the pipe mode, and the flag is a pipe mode, out of reach of
    /// <see cref="PipeOptions"/> (which go to the open mode).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe NamedPipeServerStream Create(string name, PipeSecurity security, bool rejectRemoteClients)
    {
        const uint accessDuplex = 0x3, overlapped = 0x40000000, rejectRemote = 0x8, unlimitedInstances = 255;
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        nint handle;
        fixed (byte* sd = descriptor)
        {
            var attributes = new SecurityAttributes { Length = sizeof(SecurityAttributes), SecurityDescriptor = sd };
            handle = CreateNamedPipe(@"\\.\pipe\" + name, accessDuplex | overlapped, rejectRemoteClients ? rejectRemote : 0,
                unlimitedInstances, 0, 0, 0, &attributes);
        }
        if (handle == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            var message = $"cannot create the pipe {name}: {new Win32Exception(error).Message}";
            throw error == 5 ? new UnauthorizedAccessException(message) : new IOException(message, Marshal.GetHRForLastWin32Error());
        }
        return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, new SafePipeHandle(handle, ownsHandle: true));
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct SecurityAttributes
    {
        public int Length;
        public byte* SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, SecurityAttributes* attributes);
}
