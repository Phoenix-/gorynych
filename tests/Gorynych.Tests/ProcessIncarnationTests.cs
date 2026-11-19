using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ProcessIncarnationTests
{
    [TestMethod]
    public void LinuxKernelIdentityIsExactAndMalformedIsUnknown()
    {
        const string boot = "abcdefab-cdef-abcd-efab-cdefabcdefab";
        var fields = Enumerable.Range(0, 19).Select(_ => "0").Append("7654321").Append("0");
        var stat = "123 (name ) with spaces) " + string.Join(" ", fields);
        var actual = ProcessIncarnation.ParseLinux(boot, stat);
        Assert.AreEqual("linux:abcdefabcdefabcdefabcdefabcdefab:7654321", actual);
        Assert.IsTrue(ProcessIncarnation.ValidLinux(actual!));
        Assert.IsNull(ProcessIncarnation.ParseLinux(boot, "123 (broken) S 0"));
        Assert.IsNull(ProcessIncarnation.ParseLinux("broken", stat));
        Assert.IsFalse(ProcessIncarnation.ValidLinux(actual! + "x"));
        Assert.IsFalse(ProcessIncarnation.ValidLinux("linux:ABCDEFABCDEFABCDEFABCDEFABCDEFAB:1"));
        Assert.IsFalse(ProcessIncarnation.ProvenDifferent(actual!, null, linux: true));
        Assert.IsFalse(ProcessIncarnation.ProvenDifferent(actual!, actual, linux: true));
        Assert.IsFalse(ProcessIncarnation.ProvenDifferent("2026-01-01T00:00:00.0000000Z", actual, linux: true),
            "legacy Linux wall clock is not proof of a new incarnation");
        Assert.IsTrue(ProcessIncarnation.ProvenDifferent(actual!, "linux:abcdefabcdefabcdefabcdefabcdefab:7654322", linux: true));
        Assert.IsTrue(ProcessIncarnation.ProvenDifferent(actual!, "linux:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:7654321", linux: true));
    }

    [TestMethod]
    public void CurrentProcessHasStableRepeatedIdentity()
    {
        var id = ProcessIncarnation.Read(Environment.ProcessId);
        Assert.IsNotNull(id);
        for (var i = 0; i < 100; i++) Assert.AreEqual(id, ProcessIncarnation.Read(Environment.ProcessId));
    }
}
