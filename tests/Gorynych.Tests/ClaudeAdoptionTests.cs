using System.Diagnostics;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Adapters.Claude;
using Gorynych.AdoptClaude;

namespace Gorynych.Tests;

[TestClass]
public sealed class ClaudeAdoptionTests
{
    internal static string Transcript(string projects, string cwd, string id)
    {
        var path = TranscriptTitles.ExpectedPath(cwd, id, projects);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path,
        [
            new JsonObject
            {
                ["type"] = "user", ["uuid"] = "outside-u", ["parentUuid"] = null,
                ["sessionId"] = id, ["cwd"] = cwd, ["timestamp"] = "2026-11-01T12:00:00Z",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "From the terminal" },
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "assistant", ["uuid"] = "outside-a", ["parentUuid"] = "outside-u",
                ["sessionId"] = id, ["cwd"] = cwd, ["timestamp"] = "2026-11-01T12:01:00Z",
                ["message"] = new JsonObject
                {
                    ["id"] = "outside-msg", ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Done there" }),
                },
            }.ToJsonString(),
            "{\"type\":\"ai-title\",\"aiTitle\":\"Generated\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"Chosen title\"}",
        ]);
        return path;
    }

    [TestMethod]
    public void RegistersWithoutChangingNativeFiles_AndIsIdempotent()
    {
        var dir = TestDirs.New();
        var projects = Path.Combine(dir, "projects");
        var id = Guid.NewGuid().ToString();
        var transcript = Transcript(projects, dir, id);
        var native = File.ReadAllBytes(transcript);
        var path = Path.Combine(dir, "claude", "sessions.json");
        var live = new ClaudeStateStore(path);
        live.Update("already-running", dir, r => { r.Title = "Keep"; r.Model = "opus"; });

        Assert.IsTrue(ClaudeAdoption.Register(dir, id, dir, projects));
        var record = live.Get(id)!; // an existing instance sees the external addition
        Assert.AreEqual("Chosen title", record.Title);
        Assert.AreEqual(DateTimeOffset.Parse("2026-11-01T12:00:00Z"), record.CreatedAt);
        Assert.AreEqual(DateTimeOffset.Parse("2026-11-01T12:01:00Z"), record.UpdatedAt);
        live.Update(id, dir, r => { r.Model = "sonnet"; r.Mode = "plan"; r.Effort = "high"; });
        var catalog = File.ReadAllBytes(path);
        Assert.IsFalse(ClaudeAdoption.Register(dir, id, dir, projects));
        CollectionAssert.AreEqual(catalog, File.ReadAllBytes(path), "no rewriting an existing registration");
        CollectionAssert.AreEqual(native, File.ReadAllBytes(transcript));
        Assert.AreEqual("opus", live.Get("already-running")!.Model);
        Assert.IsFalse(File.Exists(Path.Combine(dir, "sessions.json")), "the service overlay is not touched");
    }

    [TestMethod]
    public void InvalidInputsAndCorruptCatalog_AreLeftUnchanged()
    {
        var dir = TestDirs.New();
        var projects = Path.Combine(dir, "projects");
        var id = Guid.NewGuid().ToString();
        var transcript = Transcript(projects, dir, id);
        var path = Path.Combine(dir, "claude", "sessions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var invalid in new[] { "not json", "null", "{}", "[null]", "[{}]" })
        {
            File.WriteAllText(path, invalid);
            Assert.Throws<Exception>(() => ClaudeAdoption.Register(dir, id, dir, projects));
            Assert.AreEqual(invalid, File.ReadAllText(path));
        }
        File.WriteAllText(path, "[]");
        Assert.Throws<ArgumentException>(() => ClaudeAdoption.Register(dir, "../outside", dir, projects));
        Assert.Throws<FileNotFoundException>(() => ClaudeAdoption.Register(dir, Guid.NewGuid().ToString(), dir, projects));
        File.WriteAllText(transcript, "{\"type\":\"user\",\"sessionId\":\"wrong\"}\n");
        Assert.Throws<IOException>(() => ClaudeAdoption.Register(dir, id, dir, projects));
        File.WriteAllText(transcript, "{\"type\":\"user\",\"isSidechain\":true}\n");
        Assert.Throws<IOException>(() => ClaudeAdoption.Register(dir, id, dir, projects));
        Assert.AreEqual("[]", File.ReadAllText(path));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SeparateCliProcesses_AndRunningStore_KeepAllConcurrentChanges()
    {
        var dir = TestDirs.New();
        var config = Path.Combine(dir, "native");
        var projects = Path.Combine(config, "projects");
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid().ToString()).ToArray();
        foreach (var id in ids) Transcript(projects, dir, id);
        var path = Path.Combine(dir, "claude", "sessions.json");
        var live = new ClaudeStateStore(path);
        live.Update("running", dir, r => r.Title = "before");
        var write = Task.Run(() =>
        {
            for (var i = 0; i < 40; i++) live.Update("running", dir, r => r.Title = $"change {i}");
        });
        await Task.WhenAll(ids.Select(async id =>
        {
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(typeof(ClaudeAdoption).Assembly.Location);
            foreach (var arg in new[] { id, "--cwd", dir, "--data", dir }) info.ArgumentList.Add(arg);
            info.Environment["CLAUDE_CONFIG_DIR"] = config;
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, await error + await output);
        }));
        await write;
        CollectionAssert.AreEquivalent(ids.Append("running").ToArray(), live.All().Select(r => r.SessionId).ToArray());
        Assert.AreEqual("change 39", live.Get("running")!.Title);
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public void LockedCatalog_IsAnError_AndDoesNotReportAnUnsavedRegistration()
    {
        var dir = TestDirs.New();
        var projects = Path.Combine(dir, "projects");
        var id = Guid.NewGuid().ToString();
        Transcript(projects, dir, id);
        var path = Path.Combine(dir, "claude", "sessions.json");
        using var held = SharedFileLock.Acquire(path);
        File.WriteAllText(path, "[]");
        Assert.Throws<IOException>(() => ClaudeAdoption.Register(dir, id, dir, projects));
        Assert.AreEqual("[]", File.ReadAllText(path));
    }

    [TestMethod]
    public void Registration_PreservesFieldsItDoesNotOwn()
    {
        var dir = TestDirs.New();
        var projects = Path.Combine(dir, "projects");
        var id = Guid.NewGuid().ToString();
        Transcript(projects, dir, id);
        var path = Path.Combine(dir, "claude", "sessions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string existing = "{\"sessionId\":\"running\",\"cwd\":\"/w\",\"futureField\":{\"keep\":[1,2]}}";
        File.WriteAllText(path, "[" + existing + "]");
        Assert.IsTrue(ClaudeAdoption.Register(dir, id, dir, projects));
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(existing), JsonNode.Parse(File.ReadAllText(path))![0]));
    }

    [TestMethod]
    public void CommandLine_ReportsUsageAndReadFailures()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.AreEqual(0, AdoptionCommand.Run(["--help"], output, error));
        Assert.AreEqual(2, AdoptionCommand.Run([], output, error));
        Assert.AreEqual(2, AdoptionCommand.Run(["id", "--cwd"], output, error));
        Assert.AreEqual(2, AdoptionCommand.Run(["id", "--cwd", "x", "--cwd", "y"], output, error));
        Assert.AreEqual(1, AdoptionCommand.Run(["bad-id", "--cwd", TestDirs.New()], output, error));
    }
}
