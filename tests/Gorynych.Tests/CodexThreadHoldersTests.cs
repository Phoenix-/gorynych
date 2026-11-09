using System.Diagnostics;
using Gorynych.Adapters.Codex;

namespace Gorynych.Tests;

/// <summary>
/// Who else holds a Codex thread open (docs/adapters.md, "One writer"): the processes that have its
/// writer-lock file open, asked of the file system without taking the lock, classified by image and
/// parent, and nothing known on any failure.
/// </summary>
[TestClass]
public sealed class CodexThreadHoldersTests
{
    const string Held = "019a0000-0000-7000-8000-000000000001";
    const string Free = "019a0000-0000-7000-8000-000000000002";
    const string NoFile = "019a0000-0000-7000-8000-000000000003";

    /// <summary>A child process that opens the file as Codex does (read and write, sharing all) and holds it until its input closes.</summary>
    static async Task<Process> HoldAsync(string path)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"$f = [System.IO.File]::Open('{path}', 'Open', 'ReadWrite', 'ReadWrite, Delete'); 'open'; [void][Console]::In.ReadLine(); $f.Dispose()" })
            psi.ArgumentList.Add(a);
        var holder = Process.Start(psi)!;
        var line = await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual("open", line, "the holder opened the file");
        return holder;
    }

    static async Task ReleaseAsync(Process holder)
    {
        holder.StandardInput.Close();
        await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        holder.Dispose();
    }

    static string LocksDir()
    {
        var dir = Path.Combine(TestDirs.New(), "thread-writer-locks");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AThreadAnotherProcessHolds_IsFound_WithThatProcess_AndNothingIsLockedOrCreated()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("the holders are known on Windows only");
        var dir = LocksDir();
        File.WriteAllBytes(Path.Combine(dir, Held + ".lock"), []);
        File.WriteAllBytes(Path.Combine(dir, Free + ".lock"), []);
        var holder = await HoldAsync(Path.Combine(dir, Held + ".lock"));
        try
        {
            var holders = new CodexThreadHolders(dir, () => []);

            var found = holders.Find([Held, Free, NoFile]);

            CollectionAssert.AreEqual(new[] { Held }, found.Keys.ToArray(), "only the held one");
            Assert.AreEqual($"a terminal codex (pid {holder.Id})", found[Held], "powershell is neither Codex Desktop nor VS Code");
            Assert.AreEqual(found[Held], holders.Find(Held), "one thread alone looks the same");
            Assert.IsNull(holders.Find(Free));
            CollectionAssert.AreEquivalent(new[] { Held + ".lock", Free + ".lock" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray(), "nothing created");
            // Taking no lock: the holder still writes and the file can still be opened as Codex opens it.
            using (var again = new FileStream(Path.Combine(dir, Held + ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                again.Lock(0, 1);

            Assert.AreEqual(0, new CodexThreadHolders(dir, () => [holder.Id]).Find([Held]).Count, "the adapter's own app-server does not count");
            var inner = Directory.CreateDirectory(Path.Combine(dir, "inner")).FullName;
            Assert.AreEqual(0, new CodexThreadHolders(inner, () => []).Find([@"..\" + Held, "../" + Held]).Count, "an ID never reaches outside the folder");
        }
        finally
        {
            await ReleaseAsync(holder);
        }
        Assert.AreEqual(0, new CodexThreadHolders(dir, () => []).Find([Held]).Count, "free once the holder let go");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APageOfAList_ReadsTheFolderOnce_AndFindsTheHeldAmongMany()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("the holders are known on Windows only");
        var dir = LocksDir();
        var ids = Enumerable.Range(10, 8).Select(i => $"019a0000-0000-7000-8000-0000000000{i}").ToArray();
        foreach (var id in ids) File.WriteAllBytes(Path.Combine(dir, id + ".lock"), []);
        var holder = await HoldAsync(Path.Combine(dir, ids[5] + ".lock"));
        try
        {
            var found = new CodexThreadHolders(dir, () => []).Find(ids.Append(NoFile));

            CollectionAssert.AreEqual(new[] { ids[5] }, found.Keys.ToArray());
        }
        finally
        {
            await ReleaseAsync(holder);
        }
    }

    [TestMethod]
    public void AnythingThatCannotBeTold_MarksNothing()
    {
        var dir = LocksDir();
        Assert.AreEqual(0, new CodexThreadHolders(Path.Combine(dir, "missing"), () => []).Find([Held]).Count, "no folder");
        Assert.AreEqual(0, new CodexThreadHolders(null, () => []).Find([Held]).Count, "no folder known");
        Directory.CreateDirectory(Path.Combine(dir, Held + ".lock")); // a folder where a file should be: cannot be opened as one
        Assert.AreEqual(0, new CodexThreadHolders(dir, () => []).Find([Held]).Count);
        File.WriteAllBytes(Path.Combine(dir, "x.lock"), []);
        Assert.AreEqual(0, new CodexThreadHolders(dir, () => []).Find(["..\\x", "a/b", ""]).Count, "only IDs that name a file in the folder");
        Assert.AreEqual(0, new CodexThreadHolders(dir, () => throw new InvalidOperationException("broken")).Find([Held]).Count, "a failure is not known");
    }

    [TestMethod]
    [DataRow(@"C:\Users\u\AppData\Local\OpenAI\Codex\bin\26.1029\codex.exe", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1029.0_x64\app\ChatGPT.exe", "Codex Desktop")]
    [DataRow(@"C:\Users\u\AppData\Local\OpenAI\Codex\bin\codex.exe", null, "Codex Desktop")]
    [DataRow(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1029.0_x64\app\resources\codex.exe", null, "Codex Desktop")]
    [DataRow(null, @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1029.0_x64\app\ChatGPT.exe", "Codex Desktop")]
    [DataRow(@"C:\Users\u\.vscode\extensions\openai.chatgpt-0.4.1-win32-x64\bin\windows-x86_64\codex.exe", null, "VS Code")]
    [DataRow(@"C:\Users\u\AppData\Roaming\npm\node_modules\@openai\codex\vendor\codex.exe", @"C:\Users\u\AppData\Local\Programs\Microsoft VS Code\Code.exe", "VS Code")]
    [DataRow(@"C:\Users\u\AppData\Roaming\npm\node_modules\@openai\codex\vendor\codex.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe", "a terminal codex (pid 4242)")]
    [DataRow(null, null, "a terminal codex (pid 4242)")]
    public void AHolder_IsNamedByItsImageOrItsParent(string? image, string? parent, string expected)
    {
        Assert.AreEqual(expected, CodexThreadHolders.Classify(4242, image, parent, @"C:\Users\u\AppData\Local"));
    }
}
