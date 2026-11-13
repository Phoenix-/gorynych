using System.Text;
using Gorynych.Service.Ahp;

namespace Gorynych.Tests;

/// <summary>The face's <c>file:</c> URIs (docs/ahp-face.md, "Identifiers"), read as VS Code writes them.</summary>
[TestClass]
public sealed class AhpNamesTests
{
    /// <summary>
    /// A local path as VS Code writes its <c>file:</c> URI (<c>URI.file(path).toString()</c>): slashes, the drive letter
    /// lower-cased, and every byte of the UTF-8 path but an unreserved character or '/' percent-encoded, ':' included.
    /// </summary>
    internal static string VsCodeUri(string path)
    {
        var p = path.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length >= 3 && p[2] == ':' && char.IsAsciiLetterUpper(p[1])) p = $"/{char.ToLowerInvariant(p[1])}{p[2..]}";
        var uri = new StringBuilder("file://");
        foreach (var b in Encoding.UTF8.GetBytes(p))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '/')) uri.Append(c);
            else uri.Append('%').Append(b.ToString("X2"));
        }
        return uri.ToString();
    }

    [TestMethod]
    public void VsCodesForm_IsTheOneTheWindowSends()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("a drive letter is Windows'");
        Assert.AreEqual("file:///c%3A/Users/x/My%20Project%20%232%20%D0%94", VsCodeUri(@"C:\Users\x\My Project #2 Д"));
    }

    [TestMethod]
    public void AFileUri_NamesItsLocalPath_InVsCodesFormAndInOthers()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var (uri, path) in new[]
            {
                ("file:///c%3A/Users/x", @"C:\Users\x"),
                ("file:///C:/Users/x", @"C:\Users\x"),
                ("file:///c:/Users/x", @"C:\Users\x"),
                ("file:///c%3a/Users/x", @"C:\Users\x"),
                ("file:///c%3A", @"C:\"),
                ("file:///c%3A/", @"C:\"),
                ("file:///c%3A/Users/x/", @"C:\Users\x"),
                ("FILE://localhost/c%3A/x", @"C:\x"),
                ("file:/c%3A/x", @"C:\x"),
                ("file:///c%3A/a%20b/%D0%94%D0%B8%D0%BC%D0%B0/x%23y%25z", @"C:\a b\Дима\x#y%z"),
                ("file:///c%3A/a/../b", @"C:\b"),
            })
                Assert.AreEqual(path, AhpNames.FilePath(uri), uri);
            foreach (var uri in new[] { "file://server/share/x", "file:///Users/x", "file:///cc/x", "file:///c%3Ax", "file:c:/x", "file:///c%3A/a%00b" })
                Assert.IsNull(AhpNames.FilePath(uri), uri);
        }
        else
        {
            foreach (var (uri, path) in new[]
            {
                ("file:///home/x%20y/%D0%94%23", "/home/x y/Д#"),
                ("file://localhost/home/x/", "/home/x"),
                ("file:/home/x", "/home/x"),
                ("file:///c%3A/x", "/c:/x"),
                ("file:///", "/"),
            })
                Assert.AreEqual(path, AhpNames.FilePath(uri), uri);
            foreach (var uri in new[] { "file://server/share/x", "file:x", "file:///a%00b" })
                Assert.IsNull(AhpNames.FilePath(uri), uri);
        }
        foreach (var uri in new[] { "file:///x?y", "file:///x#y", "https://example.org/x", "ahp-session:/x", "", null })
            Assert.IsNull(AhpNames.FilePath(uri), uri);
    }

    [TestMethod]
    public void TheFacesUri_EscapesWhatAPathHas_APercentSignAmongTheRest()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("file:///C:/x/100%2541%20a%23b", AhpNames.FileUri(@"C:\x\100%41 a#b"));
            Assert.AreEqual("file:///C:/", AhpNames.FileUri(@"C:\"));
            Assert.AreEqual("file://server/share/x", AhpNames.FileUri(@"\\server\share\x"));
            foreach (var odd in new[] { "C:x", "C:", @"\\?\C:\x", @"\\.\pipe\x", @"\\" })
                Assert.IsNull(AhpNames.FileUri(odd), odd); // relative to a drive's current folder, or a device's
        }
        else
        {
            Assert.AreEqual("file:///x/100%2541%20a%23b", AhpNames.FileUri("/x/100%41 a#b"));
            Assert.AreEqual("file:///x/a%5Cb", AhpNames.FileUri(@"/x/a\b"), "a backslash is a name's");
            Assert.AreEqual("file:///", AhpNames.FileUri("/"));
        }
        Assert.IsNull(AhpNames.FileUri("relative"));
    }

    [TestMethod]
    public void APath_ComesBackFromVsCodesUriAndFromTheFacesOwn()
    {
        foreach (var name in new[] { "plain", "a b", "#1 100% Дима", "100%41", "x;y[z]&(w)+=@'!$,~" })
        {
            var path = Path.Combine(Path.GetTempPath(), "gorynych-uri", name);
            Assert.AreEqual(path, AhpNames.FilePath(VsCodeUri(path)), VsCodeUri(path));
            Assert.AreEqual(path, AhpNames.FilePath(AhpNames.FileUri(path)), AhpNames.FileUri(path));
        }
    }
}
