using System.Diagnostics;
using System.Text.Json.Nodes;
using Gorynych.AdoptClaude;

namespace Gorynych.Tests;

[TestClass]
public sealed class NativeClaudeSessionsTests
{
    [TestMethod]
    public void NewestFirst_WithNamesIdsFolders_FilterAndLimit()
    {
        var dir = TestDirs.New();
        var projects = Path.Combine(dir, "projects");
        var other = Path.Combine(dir, "other");
        var older = Guid.NewGuid().ToString();
        var newer = Guid.NewGuid().ToString();
        var oldFile = ClaudeAdoptionTests.Transcript(projects, dir, older);
        var newFile = ClaudeAdoptionTests.Transcript(projects, other, newer);
        File.AppendAllText(newFile, "{\"type\":\"custom-title\",\"customTitle\":\"Named session\"}\n");
        var oldTime = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var newTime = oldTime.AddDays(1);
        File.SetLastWriteTimeUtc(oldFile, oldTime);
        File.SetLastWriteTimeUtc(newFile, newTime);
        var before = File.ReadAllBytes(newFile);

        var sessions = NativeClaudeSessions.List(projectsDirectory: projects);
        CollectionAssert.AreEqual(new[] { newer, older }, sessions.Select(s => s.SessionId).ToArray());
        Assert.AreEqual("Named session", sessions[0].Title);
        Assert.AreEqual(other, sessions[0].Cwd);
        Assert.AreEqual(new DateTimeOffset(newTime), sessions[0].ModifiedAt);
        Assert.AreEqual(newer, NativeClaudeSessions.List(limit: 1, projectsDirectory: projects).Single().SessionId);
        Assert.AreEqual(older, NativeClaudeSessions.List(cwd: dir + Path.DirectorySeparatorChar, projectsDirectory: projects).Single().SessionId);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(newFile));
        Assert.AreEqual(newTime, File.GetLastWriteTimeUtc(newFile));
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "claude")), "listing never creates the registration catalog");
    }

    [TestMethod]
    public void SkipsSubagentsAndMetadataOnlyFiles_AndFallsBackToFirstPrompt()
    {
        var dir = TestDirs.New();
        var projects = Path.Combine(dir, "projects");
        var id = Guid.NewGuid().ToString();
        var file = ClaudeAdoptionTests.Transcript(projects, dir, id);
        var project = Path.GetDirectoryName(file)!;
        File.WriteAllText(file, new JsonObject
        {
            ["type"] = "user", ["sessionId"] = id, ["cwd"] = dir,
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "First\nrequest" }) },
        }.ToJsonString() + "\n");
        File.WriteAllText(Path.Combine(project, Guid.NewGuid() + ".jsonl"), "{\"type\":\"user\",\"isSidechain\":true}\n");
        File.WriteAllText(Path.Combine(project, Guid.NewGuid() + ".jsonl"), "{\"type\":\"file-history-snapshot\"}\n");
        File.WriteAllText(Path.Combine(project, "agent-helper.jsonl"), File.ReadAllText(file));
        var nested = Path.Combine(project, id, "subagents");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, Guid.NewGuid() + ".jsonl"), File.ReadAllText(file));
        var session = NativeClaudeSessions.List(projectsDirectory: projects).Single();
        Assert.AreEqual(id, session.SessionId);
        Assert.AreEqual("First request", session.Title);
    }

    [TestMethod]
    public void LargeTranscript_ReadsLatestTitleFromTail_WhileWriterHasItOpen()
    {
        var dir = TestDirs.New();
        var projects = Path.Combine(dir, "projects");
        var id = Guid.NewGuid().ToString();
        var file = ClaudeAdoptionTests.Transcript(projects, dir, id);
        // The middle crosses both bounded chunks. The end can be in the middle of a live write.
        File.AppendAllText(file, "{\"type\":\"padding\",\"data\":\"" + new string('x', 1024 * 1024) + "\"}\n"
            + "{\"type\":\"custom-title\",\"customTitle\":\"Latest title\"}\n{\"unfinished\":");
        using var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var session = NativeClaudeSessions.List(projectsDirectory: projects).Single();
        Assert.AreEqual("Latest title", session.Title);
        Assert.AreEqual(dir, session.Cwd);
    }

    [TestMethod]
    public void MissingDirectory_IsEmpty_AndLimitIsValidated()
    {
        Assert.AreEqual(0, NativeClaudeSessions.List(projectsDirectory: Path.Combine(TestDirs.New(), "absent")).Count);
        Assert.Throws<ArgumentException>(() => NativeClaudeSessions.List(limit: 0));
        Assert.Throws<ArgumentException>(() => NativeClaudeSessions.List(limit: 1001));
        using var output = new StringWriter();
        using var error = new StringWriter();
        foreach (var args in new[]
        {
            new[] { "--list", "id" }, new[] { "--list", "--data", "x" }, new[] { "--limit", "2" },
            new[] { "--list", "--limit", "bad" }, new[] { "--list", "--list" }, new[] { "--list", "--limit", "0" },
        }) Assert.AreEqual(2, AdoptionCommand.Run(args, output, error));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CliList_UsesNativeConfig_PrintsUnicodeAndDoesNotRegister()
    {
        var dir = TestDirs.New();
        var config = Path.Combine(dir, "native");
        var id = Guid.NewGuid().ToString();
        var file = ClaudeAdoptionTests.Transcript(Path.Combine(config, "projects"), dir, id);
        File.AppendAllText(file, "{\"type\":\"custom-title\",\"customTitle\":\"Имя сессии\"}\n");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(ClaudeAdoption).Assembly.Location, "--list", "--cwd", dir, "--limit", "1" }) info.ArgumentList.Add(arg);
        info.Environment["CLAUDE_CONFIG_DIR"] = config;
        using var process = Process.Start(info)!;
        var readOutput = process.StandardOutput.ReadToEndAsync();
        var readError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await readOutput;
        Assert.AreEqual(0, process.ExitCode, await readError);
        StringAssert.Contains(output, id);
        StringAssert.Contains(output, "Имя сессии");
        StringAssert.Contains(output, dir);
        StringAssert.Contains(output, "Shown: 1");
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "claude")));
    }
}
