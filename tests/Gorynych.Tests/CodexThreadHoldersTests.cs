using System.Diagnostics;
using Gorynych.Adapters.Codex;

namespace Gorynych.Tests;

/// <summary>
/// Who else holds a Codex thread open (docs/adapters.md, "One writer"): the processes that have its
/// writer-lock file open, asked of the file system without taking the lock, classified by image and
/// parent; only a Codex process that can be read counts, the adapter's own app-server and its
/// descendants never, and nothing is known on any failure. The holders are real processes: Windows
/// PowerShell, and a copy of it named codex.exe.
/// </summary>
[TestClass]
public sealed class CodexThreadHoldersTests
{
    const string Held = "019a0000-0000-7000-8000-000000000001";
    const string Free = "019a0000-0000-7000-8000-000000000002";
    const string NoFile = "019a0000-0000-7000-8000-000000000003";

    static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Windows PowerShell under the name codex.exe, in a folder of its own: a process Codex's name.</summary>
    static string CodexNamed()
    {
        var exe = Path.Combine(TestDirs.New(), "codex.exe");
        File.Copy(PowerShell, exe);
        return exe;
    }

    /// <summary>A child process that opens the file as Codex does (read and write, sharing all) and holds it until its input closes.</summary>
    static async Task<Process> HoldAsync(string path, string? exe = null)
    {
        var psi = new ProcessStartInfo(exe ?? CodexNamed())
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
            Assert.AreEqual($"a terminal codex (pid {holder.Id})", found[Held], "a codex.exe that is neither Codex Desktop's nor VS Code's");
            Assert.AreEqual(found[Held], holders.Find(Held), "one thread alone looks the same");
            Assert.IsNull(holders.Find(Free));
            CollectionAssert.AreEquivalent(new[] { Held + ".lock", Free + ".lock" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray(), "nothing created");
            // Taking no lock: the holder still writes and the file can still be opened as Codex opens it.
            using (var again = new FileStream(Path.Combine(dir, Held + ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                again.Lock(0, 1);

            Assert.AreEqual(0, new CodexThreadHolders(dir, () => [holder.Id]).Find([Held]).Count, "the adapter's own app-server does not count");
            Assert.AreEqual(0, new CodexThreadHolders(dir, () => [Environment.ProcessId]).Find([Held]).Count,
                "nor a process it started: this test process started the holder, as the app-server's wrapper would");
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
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AProcessThatIsNotCodex_IsNoHolder()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("the holders are known on Windows only");
        var dir = LocksDir();
        File.WriteAllBytes(Path.Combine(dir, Held + ".lock"), []);
        // As an antivirus, an indexer, a backup or a preview may have the file open.
        var other = await HoldAsync(Path.Combine(dir, Held + ".lock"), PowerShell);
        try
        {
            Assert.AreEqual(0, new CodexThreadHolders(dir, () => []).Find([Held]).Count, "app-server decides");
        }
        finally
        {
            await ReleaseAsync(other);
        }
    }

    [TestMethod]
    public void WithNoFolder_NoLockFile_OrAnIdThatNamesNone_NothingIsMarked()
    {
        var dir = LocksDir();
        Assert.AreEqual(0, new CodexThreadHolders(Path.Combine(dir, "missing"), () => []).Find([Held]).Count, "no folder");
        Assert.AreEqual(0, new CodexThreadHolders(null, () => []).Find([Held]).Count, "no folder known");
        Directory.CreateDirectory(Path.Combine(dir, Held + ".lock")); // a folder named like a lock file is no lock file
        Assert.AreEqual(0, new CodexThreadHolders(dir, () => []).Find([Held]).Count);
        File.WriteAllBytes(Path.Combine(dir, "x.lock"), []);
        Assert.AreEqual(0, new CodexThreadHolders(dir, () => []).Find(["..\\x", "a/b", ""]).Count, "only IDs that name a file in the folder");
        Assert.AreEqual(0, new CodexThreadHolders(dir, () => throw new InvalidOperationException("broken")).Find([Held]).Count, "a failure is not known");
    }

    [TestMethod]
    [DataRow(@"C:\Users\u\AppData\Local\OpenAI\Codex\bin\26.1029\codex.exe", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1029.0_x64\app\ChatGPT.exe", "Codex Desktop")]
    [DataRow(@"C:\Users\u\AppData\Local\OpenAI\Codex\bin\codex.exe", null, "Codex Desktop")]
    [DataRow(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1029.0_x64\app\resources\codex.exe", null, "Codex Desktop")]
    [DataRow(null, @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1029.0_x64\app\ChatGPT.exe", null, DisplayName = "not read: not taken for anything")]
    [DataRow(@"C:\Users\u\.vscode\extensions\openai.chatgpt-0.4.1-win32-x64\bin\windows-x86_64\codex.exe", null, "VS Code")]
    [DataRow(@"C:\Users\u\AppData\Roaming\npm\node_modules\@openai\codex\vendor\codex.exe", @"C:\Users\u\AppData\Local\Programs\Microsoft VS Code\Code.exe", "VS Code")]
    [DataRow(@"C:\Users\u\AppData\Roaming\npm\node_modules\@openai\codex\vendor\codex.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe", "a terminal codex (pid 4242)")]
    [DataRow(null, null, null, DisplayName = "not read, gone or reused: not taken for a terminal codex")]
    [DataRow(@"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18\MsMpEng.exe", @"C:\Windows\System32\services.exe", null, DisplayName = "an antivirus")]
    [DataRow(@"C:\Windows\System32\SearchProtocolHost.exe", @"C:\Windows\System32\SearchIndexer.exe", null, DisplayName = "the indexer")]
    [DataRow(@"C:\Windows\explorer.exe", null, null, DisplayName = "Explorer's preview")]
    [DataRow(@"C:\Users\u\AppData\Local\Programs\Microsoft VS Code\Code.exe", @"C:\Users\u\AppData\Local\Programs\Microsoft VS Code\Code.exe", null, DisplayName = "VS Code itself")]
    [DataRow(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1029.0_x64\app\helper.exe", null, "Codex Desktop", DisplayName = "anything of Codex Desktop's own")]
    [DataRow(@"C:\Users\u\AppData\Local\Temp\notcodex.exe", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1029.0_x64\app\ChatGPT.exe", null, DisplayName = "another child of ChatGPT.exe")]
    [DataRow(@"c:/users/U/appdata/local/openai/codex/bin/CODEX.EXE", null, "Codex Desktop", DisplayName = "forward slashes, any case")]
    [DataRow(@"C:/Users/u/.vscode/extensions/openai.chatgpt-0.4.1/bin/codex.exe", null, "VS Code", DisplayName = "an extension's, forward slashes")]
    [DataRow(@"D:\tools\codex-x86_64-pc-windows-msvc.exe", @"C:\Windows\System32\cmd.exe", "a terminal codex (pid 4242)", DisplayName = "a renamed release binary")]
    public void AHolder_IsNamedByItsImageOrItsParent(string? image, string? parent, string? expected)
    {
        Assert.AreEqual(expected, CodexThreadHolders.Classify(4242, image, parent, @"C:\Users\u\AppData\Local"));
    }
}
