using System.Diagnostics;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

[TestClass]
public sealed class HostGenerationTests
{
    [TestMethod]
    [DataRow("Gorynych.Service.exe", "Gorynych.Service.dll")]
    [DataRow("Gorynych.Service", "Gorynych.Service.dll")]
    public void OnDisk_UnversionedApphost_ReadsTheMatchingAssembly(string entry, string managed)
    {
        var dir = TestDirs.New();
        var exe = Path.Combine(dir, entry);
        File.WriteAllText(exe, "an apphost without a version resource");
        var assembly = typeof(HostGeneration).Assembly.Location;
        File.Copy(assembly, Path.Combine(dir, managed));

        var disk = HostGeneration.OnDisk(exe);
        var version = FileVersionInfo.GetVersionInfo(assembly).ProductVersion;
        Assert.IsFalse(string.IsNullOrWhiteSpace(version));
        Assert.AreEqual(version, disk.Version);
        Assert.IsNotNull(disk.Build);
        StringAssert.StartsWith(disk.Build, version.Split('+')[0] + "+");
        Assert.IsNull(disk.Unread);
    }

    [TestMethod]
    public void OnDisk_UnversionedApphost_DoesNotBorrowAnUnrelatedAssemblyVersion()
    {
        var dir = TestDirs.New();
        var exe = Path.Combine(dir, TestDirs.Exe("Gorynych.Other"));
        File.WriteAllText(exe, "an apphost without a version resource");
        File.Copy(typeof(HostGeneration).Assembly.Location, Path.Combine(dir, "Gorynych.Service.dll"));

        var disk = HostGeneration.OnDisk(exe);
        Assert.IsNull(disk.Version);
        Assert.IsNotNull(disk.Build);
        StringAssert.StartsWith(disk.Build, "unknown+");
        Assert.IsNull(disk.Unread);
    }
}
