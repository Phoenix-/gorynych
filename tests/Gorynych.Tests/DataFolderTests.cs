using System.Security.AccessControl;
using System.Security.Principal;
using Gorynych.Acp.Hosting;
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
            Assert.AreEqual(DataFolder.Wanted(user), folder.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
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

    /// <summary>
    /// Windows passes over an item it may not change without a word: the folder counts as closed only
    /// once a start closed every item inside, and each start goes over it again until then.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "DACLs are Windows only; on Unix the folder's own mode keeps others out of all inside")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AnItemThatCouldNotBeClosed_KeepsTheFolderOpen_AndEachStartTriesAgain_UntilItIsClosed()
    {
        var data = OpenToEveryone();
        File.WriteAllText(Path.Combine(data, "a.json"), "a");
        Directory.CreateDirectory(Path.Combine(data, "journal"));
        File.WriteAllText(Path.Combine(data, "journal", "s1.jsonl"), "x");
        // An item this user may not change the DACL of, as Windows' own pass meets one: it denies them WRITE_DAC.
        var stuck = Path.Combine(data, "journal", "stuck.jsonl");
        File.WriteAllText(stuck, "s");
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSecurity();
        deny.SetSecurityDescriptorSddlForm($"D:(D;;WD;;;OW)(D;;WD;;;{user.Value})", AccessControlSections.Access);
        new FileInfo(stuck).SetAccessControl(deny);

        var first = DataFolder.Protect(data);

        Assert.IsNotNull(first);
        Assert.IsTrue(first.Value.Failed, first.Value.Said);
        StringAssert.Contains(first.Value.Said, Path.Combine("journal", "stuck.jsonl"));
        StringAssert.Contains(first.Value.Said, "the next start tries again");
        Assert.IsTrue(LetsIn(new FileInfo(stuck).GetAccessControl(), Everyone), "it is still open");
        Assert.IsFalse(LetsIn(new FileInfo(Path.Combine(data, "journal", "s1.jsonl")).GetAccessControl(), Everyone), "the rest is closed");
        Assert.IsFalse(File.Exists(Path.Combine(data, DataFolder.ClosedFile)), "not noted as closed");

        var second = DataFolder.Protect(data);
        Assert.IsNotNull(second, "the folder's own DACL is right, yet it is gone over again");
        Assert.IsTrue(second.Value.Failed, second.Value.Said);

        File.Delete(stuck); // what held it is gone
        var third = DataFolder.Protect(data);
        Assert.IsNotNull(third);
        Assert.IsFalse(third.Value.Failed, third.Value.Said);
        Assert.AreEqual(DataFolder.Wanted(user), File.ReadAllText(Path.Combine(data, DataFolder.ClosedFile)), "noted, with the DACL given");
        Assert.IsNull(DataFolder.Protect(data), "closed: left alone");
    }

    /// <summary>An item left open under a folder that is closed (by an earlier build, or by hand) is closed at the next start.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "DACLs are Windows only")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AnItemLeftOpen_UnderAClosedFolder_IsClosed_ByTheNextStart()
    {
        var data = TestDirs.New();
        var user = WindowsIdentity.GetCurrent().User!;
        var closed = new DirectorySecurity();
        closed.SetSecurityDescriptorSddlForm(DataFolder.Dacl(user.Value), AccessControlSections.Access);
        new DirectoryInfo(data).SetAccessControl(closed);
        Directory.CreateDirectory(Path.Combine(data, "claude"));
        var open = Path.Combine(data, "claude", "settings.json");
        File.WriteAllText(open, "{}");
        var everyone = new FileSecurity();
        everyone.SetSecurityDescriptorSddlForm("D:(A;;FR;;;WD)", AccessControlSections.Access);
        new FileInfo(open).SetAccessControl(everyone);
        Assert.IsTrue(LetsIn(new FileInfo(open).GetAccessControl(), Everyone));

        var said = DataFolder.Protect(data);

        Assert.IsNotNull(said);
        Assert.IsFalse(said.Value.Failed, said.Value.Said);
        Assert.IsFalse(LetsIn(new FileInfo(open).GetAccessControl(), Everyone), new FileInfo(open).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.IsTrue(LetsIn(new FileInfo(open).GetAccessControl(), user), "the user keeps it");
        Assert.AreEqual("{}", File.ReadAllText(open));
        Assert.IsNull(DataFolder.Protect(data));
    }

    /// <summary>
    /// SDDL names the built-in Administrator (RID 500) <c>LA</c>: a folder closed for that user reads
    /// back so, and must count as closed, or every start of a service run as that user (a CI runner)
    /// goes over the folder again. Whoever runs the test, it writes that DACL and reads it back.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "SDDL is Windows only")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ADaclForTheBuiltInAdministrator_ReadBackAsLA_IsTheOneWanted()
    {
        var aliased = new RawSecurityDescriptor("D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;LA)");
        var administrator = ((CommonAce)aliased.DiscretionaryAcl![1]).SecurityIdentifier;
        StringAssert.EndsWith(administrator.Value, "-500", "this machine's built-in Administrator");
        Assert.AreNotEqual(DataFolder.Dacl(administrator.Value), aliased.GetSddlForm(AccessControlSections.Access), "the text differs: why it is compared in Windows' form");

        // The test's user owns the folder, so it can still read its DACL and set another.
        var folder = new DirectoryInfo(TestDirs.New());
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(DataFolder.Dacl(administrator.Value), AccessControlSections.Access);
        folder.SetAccessControl(security);
        string read;
        try
        {
            read = folder.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        }
        finally
        {
            var inherit = new DirectorySecurity();
            inherit.SetSecurityDescriptorSddlForm("D:AI", AccessControlSections.Access);
            folder.SetAccessControl(inherit);
            folder.Delete();
        }

        StringAssert.Contains(read, ";;;LA)", "written with the full SID, read back by its alias");
        Assert.AreEqual(read, DataFolder.Wanted(administrator));
        Assert.AreEqual(DataFolder.Wanted(administrator), DataFolder.Canonical(DataFolder.Wanted(administrator)), "and written back as it is");
    }

    /// <summary>
    /// A folder closed for the built-in Administrator, and noted so, is left alone by the next start of a
    /// service run as that user: before, its DACL read back as <c>LA</c> never matched, and every start
    /// went over the folder again. Whoever runs the test, the service's side is that user's.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "DACLs are Windows only")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AFolderClosedForTheBuiltInAdministrator_IsLeftAlone_ByItsNextStart()
    {
        var administrator = ((CommonAce)new RawSecurityDescriptor("D:(A;;FA;;;LA)").DiscretionaryAcl![0]).SecurityIdentifier;
        var user = WindowsIdentity.GetCurrent().User!;
        var data = new DirectoryInfo(TestDirs.New());
        // The note as that service wrote it, which this test's user may read as that service would.
        var noted = Path.Combine(data.FullName, DataFolder.ClosedFile);
        File.WriteAllText(noted, DataFolder.Wanted(administrator));
        var readable = new FileSecurity();
        readable.SetSecurityDescriptorSddlForm($"D:P(A;;FA;;;{user.Value})(A;;FA;;;{administrator.Value})", AccessControlSections.Access);
        new FileInfo(noted).SetAccessControl(readable);
        var closed = new DirectorySecurity();
        closed.SetSecurityDescriptorSddlForm(DataFolder.Dacl(administrator.Value), AccessControlSections.Access);
        data.SetAccessControl(closed);
        try
        {
            Assert.IsNull(DataFolder.ProtectWindows(data.FullName, administrator), "closed and noted: nothing to do");
        }
        finally
        {
            // Its owner may still set another DACL, and then remove it.
            var inherit = new DirectorySecurity();
            inherit.SetSecurityDescriptorSddlForm("D:AI", AccessControlSections.Access);
            data.SetAccessControl(inherit);
            data.Delete(recursive: true);
        }
    }

    /// <summary>A junction inside leads out of the data folder: what it leads to keeps its DACL.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "junctions and DACLs are Windows only; chmod of the folder follows nothing inside")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AJunctionInside_IsNotFollowed_AndWhatItLeadsToKeepsItsDacl()
    {
        var data = OpenToEveryone();
        var outside = OpenToEveryone();
        Directory.CreateDirectory(Path.Combine(outside, "deeper"));
        File.WriteAllText(Path.Combine(outside, "deeper", "theirs.txt"), "t");
        var link = Path.Combine(data, "elsewhere");
        Junction(link, outside);
        try
        {
            string[] Dacls() =>
            [
                new DirectoryInfo(outside).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                new DirectoryInfo(Path.Combine(outside, "deeper")).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                new FileInfo(Path.Combine(outside, "deeper", "theirs.txt")).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access),
            ];
            var before = Dacls();
            Assert.IsTrue(LetsIn(new FileInfo(Path.Combine(outside, "deeper", "theirs.txt")).GetAccessControl(), Everyone), "open, which the pass would close if it went there");

            var said = DataFolder.Protect(data);

            Assert.IsNotNull(said);
            Assert.IsFalse(said.Value.Failed, said.Value.Said);
            StringAssert.Contains(said.Value.Said, "not followed");
            CollectionAssert.AreEqual(before, Dacls(), "what the junction leads to is as it was");
        }
        finally
        {
            Directory.Delete(link); // the junction only
        }
    }

    /// <summary>The service closes only a folder that is its own: never a drive, the profile, the system's folders, or someone else's files.</summary>
    [TestMethod]
    public void AFolderThatIsNotGorynychs_IsRefused_WithWhyAndWhatToDo()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> refused = [Path.GetPathRoot(home)!, home, Path.GetDirectoryName(home)!];
        if (OperatingSystem.IsWindows())
            refused.AddRange([
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Gorynych"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gorynych"),
            ]);
        else refused.Add(Path.Combine(home, ".local", "share"));
        var others = TestDirs.New();
        File.WriteAllText(Path.Combine(others, "notes.txt"), "mine");
        refused.Add(others);
        foreach (var path in refused)
        {
            var why = DataFolder.Refusal(path);
            Assert.IsNotNull(why, path);
            StringAssert.Contains(why, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), "names the folder");
            StringAssert.Contains(why, "give --data a new or empty folder", "says what to do");
        }
        StringAssert.Contains(DataFolder.Refusal(others), "notes.txt");
        StringAssert.Contains(DataFolder.Refusal(Path.GetPathRoot(home)!), "the root of a drive");

        var empty = TestDirs.New();
        var ours = TestDirs.New();
        File.WriteAllText(Path.Combine(ours, "notes.txt"), "a log");
        DataFolder.Claim(ours);
        var earlier = TestDirs.New();
        File.WriteAllText(Path.Combine(earlier, "service.json"), "{}");
        File.WriteAllText(Path.Combine(earlier, "sessions.json"), "{}");
        foreach (var path in new[] { empty, Path.Combine(empty, "new", "deeper"), ours, earlier, DataFolder.DefaultPath() })
            Assert.IsNull(DataFolder.Refusal(path), path);
    }

    /// <summary>A folder that leads to one the service may not close is refused too.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "a junction needs no privilege on Windows only")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AJunctionToTheProfile_IsRefused()
    {
        var link = Path.Combine(TestDirs.New(), "data");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Junction(link, home);
        try
        {
            var why = DataFolder.Refusal(link);
            Assert.IsNotNull(why);
            StringAssert.Contains(why, "it leads to");
        }
        finally
        {
            Directory.Delete(link); // the junction only
        }
    }

    /// <summary>The service itself refuses such a folder, before it writes anything, and so does the bridge that would start it.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheServiceAndTheBridge_RefuseSomeoneElsesFolder_AndWriteNothingThere()
    {
        var others = TestDirs.New();
        File.WriteAllText(Path.Combine(others, "notes.txt"), "mine");
        var pipe = "gorynych-test-" + Guid.NewGuid().ToString("N")[..8];
        List<List<string>> runs = [["--pipe", pipe, "--data", others]];
        // From an ssh login or session 0 (a CI runner) the bridge starts no service at all, and says that instead.
        if (!Gorynych.Outside.ServiceLaunch.IsRemoteLogon()) runs.Add(["acp", "--agent", "kimi", "--pipe", pipe, "--data", others]);
        foreach (var args in runs)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BridgeTests.ServiceExe(), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            })!;
            try
            {
                // Bounded: a service that took the folder would run on and never close its stderr.
                var error = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreNotEqual(0, process.ExitCode, string.Join(' ', args));
                StringAssert.Contains(error, $"will not take {others} for its data folder", string.Join(' ', args));
                StringAssert.Contains(error, "notes.txt");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        CollectionAssert.AreEqual(new[] { "notes.txt" }, Directory.EnumerateFileSystemEntries(others).Select(Path.GetFileName).ToArray(), "nothing written there");
        Assert.IsNull(await ServicePipe.ConnectAsync(pipe, TimeSpan.FromMilliseconds(200)), "no service started");
    }

    /// <summary>A protection that throws, of whatever kind, is said, and the service runs on.</summary>
    [TestMethod]
    public void AFolderThatCannotBeClosed_IsSaid_NotThrown()
    {
        var missing = Path.Combine(TestDirs.New(), "gone");

        var said = DataFolder.Protect(missing);

        Assert.IsNotNull(said);
        Assert.IsTrue(said.Value.Failed);
        StringAssert.Contains(said.Value.Said, "is not closed to other accounts");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    /// <summary>A new folder that lets everyone in, inherited by all inside, as the user profile lets Codex's sandbox in.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string OpenToEveryone()
    {
        var dir = TestDirs.New();
        var folder = new DirectoryInfo(dir);
        var security = folder.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(security);
        return dir;
    }

    static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.AreEqual(0, mklink.ExitCode, mklink.StandardError.ReadToEnd());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool LetsIn(FileSystemSecurity security, IdentityReference who) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(who));
}
