using Gorynych.Service;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using System.Text.Json.Nodes;

namespace Gorynych.Tests;

[TestClass]
public sealed class CompletionGroupIdentityTests
{
    [TestMethod]
    public void GroupInstanceSurvivesRenameAndRestartButNotDeletionAndReuse()
    {
        var path = Path.Combine(TestDirs.New(), "metadata.json"); var store = new UiMetadataStore(path);
        Assert.IsNull(store.EnsureGroupInstance("project", "group"));
        store.Set("project", "group", "First");
        var original = store.EnsureGroupInstance("project", "group"); Assert.IsNotNull(original);
        Assert.AreEqual(original, (string?)store.List("project")[0]!["instanceId"]);
        store.Set("project", "group", "Renamed");
        Assert.AreEqual(original, new UiMetadataStore(path).EnsureGroupInstance("project", "group"));
        store.Delete("project", "group", out var deleted); Assert.IsTrue(deleted);
        store.Set("project", "group", "Recreated");
        var replacement = new UiMetadataStore(path).EnsureGroupInstance("project", "group");
        Assert.IsNotNull(replacement); Assert.AreNotEqual(original, replacement);
    }

    [TestMethod]
    public void FailedInstanceOrDeleteCommitDoesNotChangeBinding()
    {
        var path = Path.Combine(TestDirs.New(), "metadata.json"); var store = new UiMetadataStore(path);
        store.Set("project", "group", "First"); var original = store.EnsureGroupInstance("project", "group");
        store.WriteFile = (_, _) => throw new IOException("injected");
        Assert.ThrowsExactly<IOException>(() => store.Delete("project", "group", out _));
        Assert.AreEqual(original, store.EnsureGroupInstance("project", "group"));
        Assert.AreEqual(original, new UiMetadataStore(path).EnsureGroupInstance("project", "group"));
    }

    [TestMethod]
    public void LaunchScopePersistsOnlyExplicitOptions()
    {
        var id = Guid.NewGuid().ToString("N");
        var line = CommandLine.Parse(["serve", "--completion-project", "p", "--completion-group", "g", "--completion-instance", id]);
        CollectionAssert.AreEqual(new[] { "--completion-project", "p", "--completion-group", "g", "--completion-instance", id }, line.PersistentServiceArguments());
        var dir = TestDirs.New(); ServiceOptions.Write(dir, line.PersistentServiceArguments());
        CollectionAssert.AreEqual(line.PersistentServiceArguments(), ServiceOptions.Read(dir).ToList());
        Assert.AreEqual(0, CommandLine.Parse(["serve"]).PersistentServiceArguments().Count);
        Assert.IsNotNull(CommandLine.Parse(["serve", "--completion-project", "p"]));
    }

    [TestMethod]
    public async Task ConductorScopeFollowsExactInstanceAcrossRestartAndDisable()
    {
        var dir = TestDirs.New(); var seed = new UiMetadataStore(Path.Combine(dir, "ui-metadata.json"));
        seed.Set("p", "g", "group"); var id = seed.EnsureGroupInstance("p", "g")!;
        ConductorOptions Selected(string instance) => new() { DataDirectory = dir, ServiceId = "scope", CompletionConfiguration = ("p", "g", instance) };
        await using (var first = new Conductor(Selected(id), [new LifecycleAgent("kimi", true).Adapter])) Assert.IsTrue(first.CompletionScopeIsCurrent);
        await using (var reopened = new Conductor(Selected(id), [new LifecycleAgent("kimi", true).Adapter])) Assert.IsTrue(reopened.CompletionScopeIsCurrent);
        await using (var off = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "scope" }, [new LifecycleAgent("kimi", true).Adapter])) Assert.IsFalse(off.CompletionScopeIsCurrent);
        await using (var invalid = new Conductor(Selected("invalid"), [new LifecycleAgent("kimi", true).Adapter])) Assert.IsFalse(invalid.CompletionScopeIsCurrent);
        seed = new UiMetadataStore(Path.Combine(dir, "ui-metadata.json")); seed.Delete("p", "g", out _); seed.Set("p", "g", "new");
        await using (var stale = new Conductor(Selected(id), [new LifecycleAgent("kimi", true).Adapter])) Assert.IsFalse(stale.CompletionScopeIsCurrent);
        var next = new UiMetadataStore(Path.Combine(dir, "ui-metadata.json")).EnsureGroupInstance("p", "g")!;
        await using (var fresh = new Conductor(Selected(next), [new LifecycleAgent("kimi", true).Adapter])) Assert.IsTrue(fresh.CompletionScopeIsCurrent);
    }
    [TestMethod]
    public async Task InvalidOptionalScopeDoesNotStopService()
    {
        var dir = TestDirs.New(); var path = Path.Combine(dir, "ui-metadata.json"); var seed = new UiMetadataStore(path);
        seed.Set("p", "g", "group");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; root["groupInstances"] = "invalid";
        File.WriteAllText(path, root.ToJsonString());
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "scope",
            CompletionConfiguration = ("p", "g", Guid.NewGuid().ToString("N")) }, [new LifecycleAgent("kimi", true).Adapter]);
        Assert.IsFalse(service.CompletionScopeIsCurrent);
        Assert.AreEqual(1, service.UiMetadata.List("p").Count);
    }
    [TestMethod]
    public async Task NoLaunchScopeDefersAuthenticatedOwnerWithoutClaim()
    {
        var dir = TestDirs.New(); var agent = new LifecycleAgent("kimi", true);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = dir, ServiceId = "scope-off",
            PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(service);
        async Task<string> New() => (string)(await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
            new JsonObject { ["cwd"] = dir, ["mcpServers"] = new JsonArray() }))!["sessionId"]!;
        var parent = await New(); var child = await New();
        service.Store.Update(parent, "kimi", r => r.Draft = false, durable: true);
        service.Store.Update(child, "kimi", r => r.Draft = false, durable: true);
        service.ReplyMailbox.Accept("off-task", parent, child, "work", delegation: true);
        service.ReplyMailbox.MarkStarted("off-task", child); service.ReplyMailbox.MarkDelivered("off-task", child);
        var generation = (string)service.ReplyMailbox.FindAssignment("off-task", parent)!["generation"]!;
        var response = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodRequestCompletionReminder,
            new JsonObject { ["assignmentId"] = "off-task", ["generation"] = generation,
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = service.Store.Get(parent)!.PeerToken } });
        Assert.AreEqual("disabled", (string?)response?["reason"]);
        Assert.IsNull(service.ReplyMailbox.ReadCompletionReminder("off-task", parent, child, generation));
    }

}
