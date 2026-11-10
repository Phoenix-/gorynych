using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Gorynych.Acp.Hosting;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The service's pipe on Windows (docs/service-protocol.md, Endpoint). Its owner and only allowed
/// principal is the token's default owner: BUILTIN\Administrators when elevated (GitHub's Windows
/// runners are), else the user. Without elevation the two are the same SID, so the elevated cases
/// tell the difference only on an elevated run.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows, IgnoreMessage = "pipe ACLs are Windows only; elsewhere the pipe is a Unix socket")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PipeTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    static string Name() => "gorynych-test-" + Guid.NewGuid().ToString("N");

    [TestMethod]
    public async Task ThePipe_IsOwnedByTheTokensDefaultOwner_AndOpenToThatOwnerOnly_WithNoDenyForTheNetworkGroup()
    {
        var name = Name();
        // What .NET's own CurrentUserOnly server makes its owner.
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
        CollectionAssert.AreEquivalent(new[] { ((IdentityReference)expected, AccessControlType.Allow, PipeAccessRights.FullControl) }, rules,
            "the owner only; no deny for NETWORK, which every ssh login has");

        var accepting = server.WaitForConnectionAsync();
        await using var client = await ServicePipe.ConnectAsync(name, Wait);
        Assert.IsNotNull(client);
        await accepting.WaitAsync(Wait);
        Assert.IsTrue(client.IsConnected);
    }

    /// <summary>
    /// A client on this machine that opens the pipe through the SMB redirector
    /// (<c>\\localhost\pipe\…</c>) is a remote client as the server sees it, as one on another
    /// machine is. The same pipe without the flag shows that the loopback works here at all.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ThePipe_RefusesAClientThatComesOverTheNetwork()
    {
        var open = Name();
        await using (var reference = PipeListener.Create(open, rejectRemoteClients: false))
        {
            var accepting = reference.WaitForConnectionAsync();
            await using var overSmb = new NamedPipeClientStream("localhost", open, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await overSmb.ConnectAsync(10_000); }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                Assert.Inconclusive($"no loopback SMB to the pipe here ({e.GetType().Name}: {e.Message})");
            }
            await accepting.WaitAsync(Wait);
        }

        var name = Name();
        await using var server = PipeListener.Create(name);
        var waiting = server.WaitForConnectionAsync();
        await using var remote = new NamedPipeClientStream("localhost", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var refused = await Assert.ThrowsAsync<Exception>(() => remote.ConnectAsync(10_000));
        Assert.IsFalse(waiting.IsCompleted, $"the server took no connection ({refused.GetType().Name}: {refused.Message})");

        // A local client still gets in.
        await using var local = await ServicePipe.ConnectAsync(name, Wait);
        await waiting.WaitAsync(Wait);
    }

    /// <summary>
    /// A plain service's pipe is owned by the user. An elevated client of the same user, as every
    /// ssh login of an administrator is, opens it; .NET's own check, which takes only the token's
    /// default owner (BUILTIN\Administrators when elevated), would refuse it.
    /// </summary>
    [TestMethod]
    public async Task APipeOwnedByTheUser_OpensForTheUsersClients_ElevatedOrNot()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var name = Name();
        await using var server = PipeListener.Create(name, PipeListener.Security(identity.User!), rejectRemoteClients: true);
        var accepting = server.WaitForConnectionAsync();
        await using var client = await ServicePipe.ConnectAsync(name, Wait);
        Assert.IsNotNull(client);
        await accepting.WaitAsync(Wait);

        if (identity.Owner == identity.User) return; // not elevated: .NET's check takes this pipe too
        var other = Name();
        await using var second = PipeListener.Create(other, PipeListener.Security(identity.User!), rejectRemoteClients: true);
        await using var dotnet = new NamedPipeClientStream(".", other, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => dotnet.ConnectAsync(5000), "why the clients check the owner themselves");
    }

    /// <summary>
    /// A pipe with the service's name that someone else owns: here a pipe of the test's own, seen
    /// by a client that takes itself for another user.
    /// </summary>
    [TestMethod]
    public async Task APipeOwnedBySomeoneElse_IsRefused_BeforeTheClientSendsAnything()
    {
        var name = Name();
        await using var server = PipeListener.Create(name);
        var accepting = server.WaitForConnectionAsync();
        var stranger = new ServicePipe.Identity(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), new SecurityIdentifier("S-1-5-21-1-2-3-1001"));
        var refused = await Assert.ThrowsExactlyAsync<ServicePipe.ForeignPipeException>(() => ServicePipe.ConnectAsync(name, Wait, stranger, CancellationToken.None));
        Assert.IsInstanceOfType<UnauthorizedAccessException>(refused, "callers that catch an access error still catch it");
        StringAssert.Contains(refused.Message, "is not this user's service");
        await accepting.WaitAsync(Wait);
        var buffer = new byte[1];
        Assert.AreEqual(0, await server.ReadAsync(buffer).AsTask().WaitAsync(Wait), "the client closed the pipe without a byte");
    }

    [TestMethod]
    public void TheOwnerCheck_TakesTheUserOrTheClientsDefaultOwner_AndNobodyElse()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var stranger = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        Assert.IsTrue(ServicePipe.TrustsOwner(user, user, user), "a plain client, a plain service");
        Assert.IsTrue(ServicePipe.TrustsOwner(user, user, administrators), "an elevated client (ssh), a plain service");
        Assert.IsTrue(ServicePipe.TrustsOwner(administrators, user, administrators), "an elevated client, an elevated service");
        Assert.IsFalse(ServicePipe.TrustsOwner(administrators, user, user), "a plain client never trusts an elevated service");
        Assert.IsFalse(ServicePipe.TrustsOwner(stranger, user, user), "another user's pipe");
        Assert.IsFalse(ServicePipe.TrustsOwner(stranger, user, administrators), "another user's pipe, elevated client");
        Assert.IsFalse(ServicePipe.TrustsOwner(system, user, administrators), "a service's pipe");
    }
}
