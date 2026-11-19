using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class CompletionGroupIdentityTests
{
    [TestMethod]
    public void GroupInstanceSurvivesRenameAndRestartButNotDeletionAndReuse()
    {
        var path = Path.Combine(TestDirs.New(), "metadata.json");
        var store = new UiMetadataStore(path);
        Assert.IsNull(store.EnsureGroupInstance("project", "group"));
        store.Set("project", "group", "First");
        var original = store.EnsureGroupInstance("project", "group");
        Assert.IsNotNull(original);
        store.Set("project", "group", "Renamed");
        Assert.AreEqual(original, new UiMetadataStore(path).EnsureGroupInstance("project", "group"));
        store.Delete("project", "group", out var deleted);
        Assert.IsTrue(deleted);
        store.Set("project", "group", "Recreated");
        var replacement = new UiMetadataStore(path).EnsureGroupInstance("project", "group");
        Assert.IsNotNull(replacement);
        Assert.AreNotEqual(original, replacement);
    }

    [TestMethod]
    public void FailedInstanceOrDeleteCommitDoesNotChangeBinding()
    {
        var path = Path.Combine(TestDirs.New(), "metadata.json");
        var store = new UiMetadataStore(path);
        store.Set("project", "group", "First");
        var write = store.WriteFile;
        store.WriteFile = (_, _) => throw new IOException("injected");
        Assert.ThrowsExactly<IOException>(() => store.EnsureGroupInstance("project", "group"));
        store.WriteFile = write;
        var original = store.EnsureGroupInstance("project", "group");
        store.WriteFile = (_, _) => throw new IOException("injected");
        Assert.ThrowsExactly<IOException>(() => store.Delete("project", "group", out _));
        Assert.AreEqual(original, store.EnsureGroupInstance("project", "group"));
        Assert.AreEqual(original, new UiMetadataStore(path).EnsureGroupInstance("project", "group"));
    }
}
