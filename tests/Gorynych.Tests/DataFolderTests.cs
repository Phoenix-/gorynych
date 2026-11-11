using System.Security.AccessControl;
using System.Security.Principal;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>The data folder is its user's alone, also when it was made before the service closed it.</summary>
[TestClass]
public sealed class DataFolderTests
{
    [TestMethod]
    public void AFolderThatLetOthersIn_IsClosed_WithAllInside_AndLeftAloneOnceItIs()
    {
        var data = TestDirs.New();
        Directory.CreateDirectory(Path.Combine(data, "journal"));
        File.WriteAllText(Path.Combine(data, "sessions.json"), "{}");
        File.WriteAllText(Path.Combine(data, "journal", "s1.jsonl"), "x");
        if (OperatingSystem.IsWindows())
        {
            // As the user profile lets Codex's sandbox in: an ACE every file inside inherits.
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var folder = new DirectoryInfo(data);
            var security = folder.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(security);
            Assert.IsTrue(LetsIn(new FileInfo(Path.Combine(data, "journal", "s1.jsonl")).GetAccessControl(), everyone), "inherited, before");

            var said = DataFolder.Protect(data);

            Assert.IsNotNull(said);
            Assert.IsFalse(said.Value.Failed, said.Value.Said);
            var user = WindowsIdentity.GetCurrent().User!;
            Assert.AreEqual(DataFolder.Dacl(user.Value), folder.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            foreach (var inside in new FileSystemSecurity[]
                     {
                         new FileInfo(Path.Combine(data, "sessions.json")).GetAccessControl(),
                         new DirectoryInfo(Path.Combine(data, "journal")).GetAccessControl(),
                         new FileInfo(Path.Combine(data, "journal", "s1.jsonl")).GetAccessControl(),
                     })
            {
                Assert.IsFalse(LetsIn(inside, everyone), "nothing inside lets others in: " + inside.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
                Assert.IsTrue(LetsIn(inside, user), "the user keeps it");
            }
            File.WriteAllText(Path.Combine(data, "journal", "s2.jsonl"), "y");
            Assert.IsFalse(LetsIn(new FileInfo(Path.Combine(data, "journal", "s2.jsonl")).GetAccessControl(), everyone), "nor what is written later");
        }
        else
        {
            File.SetUnixFileMode(data, (UnixFileMode)Convert.ToInt32("755", 8));

            var said = DataFolder.Protect(data);

            Assert.IsNotNull(said);
            Assert.IsFalse(said.Value.Failed, said.Value.Said);
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(data));
        }
        Assert.IsNull(DataFolder.Protect(data), "closed already: left alone");
        Assert.AreEqual("{}", File.ReadAllText(Path.Combine(data, "sessions.json")), "the user reads it as before");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool LetsIn(FileSystemSecurity security, IdentityReference who) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(who));
}
