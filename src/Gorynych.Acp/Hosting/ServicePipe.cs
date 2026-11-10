using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Gorynych.Acp.Hosting;

/// <summary>
/// The client end of the service's pipe, for every process that opens it: the stdio bridge,
/// <c>shutdown</c>, the peers server, the outside server and the test client
/// (docs/service-protocol.md, Endpoint).
/// </summary>
public static class ServicePipe
{
    /// <summary>
    /// Connects to the service's pipe, trying again until <paramref name="timeout"/> has passed.
    /// </summary>
    /// <returns>The connected pipe, or null when no service opened it in time.</returns>
    /// <exception cref="UnauthorizedAccessException">The pipe is there but not this user's service:
    /// its ACL refuses this process (a plain process and an elevated service), or its owner is
    /// someone else (<see cref="TrustsOwner"/>).</exception>
    public static Task<NamedPipeClientStream?> ConnectAsync(string pipe, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return ConnectAsync(pipe, timeout, null, ct);
        using var identity = WindowsIdentity.GetCurrent();
        return ConnectAsync(pipe, timeout, new Identity(identity.User!, identity.Owner), ct);
    }

    /// <summary>Who the client is, for the owner check: its user and its token's default owner.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed record Identity(SecurityIdentifier User, SecurityIdentifier? DefaultOwner);

    internal static async Task<NamedPipeClientStream?> ConnectAsync(string pipe, TimeSpan timeout, Identity? identity, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            var stream = Open(pipe);
            try
            {
                await stream.ConnectAsync(200, ct).ConfigureAwait(false);
                if (OperatingSystem.IsWindows()) CheckOwner(stream, pipe, identity!);
                return stream;
            }
            catch (TimeoutException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                if (Environment.TickCount64 > deadline) return null;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// On Windows without <see cref="PipeOptions.CurrentUserOnly"/>, whose check takes only the
    /// token's default owner as the pipe's owner: an elevated client, whose default owner is
    /// BUILTIN\Administrators, could then never open a plain service, and an ssh login of an
    /// administrator is always elevated. The owner is checked here instead, before anything is
    /// sent. The server may only identify the client, never act as it. Elsewhere the pipe is a
    /// Unix socket and <see cref="PipeOptions.CurrentUserOnly"/> checks its peer's user.
    /// </summary>
    static NamedPipeClientStream Open(string pipe) => OperatingSystem.IsWindows()
        ? new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification)
        : new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    [SupportedOSPlatform("windows")]
    static void CheckOwner(NamedPipeClientStream stream, string pipe, Identity identity)
    {
        var owner = stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is not null && TrustsOwner(owner, identity.User, identity.DefaultOwner)) return;
        throw new UnauthorizedAccessException(
            $"the pipe {pipe} is owned by {owner?.Value ?? "nobody"}, so it is not this user's service");
    }

    /// <summary>
    /// Whether a pipe with this owner is the user's service: owned by the user, as a plain
    /// service's pipe is, which an elevated client of the same user may open too; or by the
    /// client's own default owner, as an elevated service's pipe is for an elevated client
    /// (BUILTIN\Administrators). A plain client so never trusts an elevated service, whose ACL
    /// refuses it anyway, and nobody trusts a pipe of another user's with the same name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool TrustsOwner(SecurityIdentifier owner, SecurityIdentifier user, SecurityIdentifier? defaultOwner) =>
        owner == user || owner == defaultOwner;
}
