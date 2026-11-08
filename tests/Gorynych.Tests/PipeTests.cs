using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The service's pipe on Windows. Its owner must be the SID that clients' PipeOptions.CurrentUserOnly
/// check compares with: the token's default owner, which is BUILTIN\Administrators when elevated
/// (GitHub's Windows runners are). Without elevation that is the user, so these tests tell the
/// difference only on an elevated run.
/// </summary>
[TestClass]
public sealed class PipeTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "pipe ACLs are Windows only; elsewhere the pipe is a Unix socket")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ThePipe_IsOwnedAsCurrentUserOnlyClientsExpect_AndOpenToThatOwnerOnly()
    {
        var name = "gorynych-test-" + Guid.NewGuid().ToString("N");
        // What .NET's own CurrentUserOnly server makes its owner: what its clients check for.
        SecurityIdentifier expected;
        await using (var reference = new NamedPipeServerStream(name + "-reference", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            expected = (SecurityIdentifier)reference.GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
        using (var identity = WindowsIdentity.GetCurrent())
            Assert.AreEqual(expected, identity.Owner, "the token's default owner");

        await using var server = PipeListener.Create(name);
        var security = server.GetAccessControl();
        Assert.AreEqual(expected, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>().Select(r => (r.IdentityReference, r.AccessControlType, r.PipeAccessRights)).ToArray();
        CollectionAssert.AreEquivalent(new[]
        {
            ((IdentityReference)expected, AccessControlType.Allow, PipeAccessRights.FullControl),
            (new SecurityIdentifier(WellKnownSidType.NetworkSid, null), AccessControlType.Deny, PipeAccessRights.FullControl),
        }, rules, "the owner only, and never over the network");

        // A client at the same elevation passes its CurrentUserOnly check.
        var accepting = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        await accepting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(client.IsConnected);
    }
}
