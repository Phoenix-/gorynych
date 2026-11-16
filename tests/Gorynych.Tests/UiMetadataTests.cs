using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class UiMetadataTests
{
    [TestMethod]
    public void Store_MergesDeletesAndPersistsAtomically()
    {
        var path = Path.Combine(TestDirs.New(), "ui.json");
        var s = new UiMetadataStore(path);
        s.Set("a", "same", "One");
        s.Set("b", "same", "Two");
        s.SetInitial("draft", JsonNode.Parse("""{"gorynych/group":{"projectKey":"a","groupId":"same"},"nested":{"a":1,"b":2}}""")!.AsObject());
        s.Patch("draft", JsonNode.Parse("""{"nested":{"a":null,"c":[1,2]},"other":true}""")!.AsObject());
        s.SetInitial("archived", JsonNode.Parse("""{"gorynych/group":{"projectKey":"b","groupId":"same"}}""")!.AsObject());
        var broken = new UiMetadataStore(path) { WriteFile = (_, _) => throw new IOException("failed") };
        Assert.ThrowsExactly<IOException>(() => broken.Delete("a", "same", out _));
        Assert.IsNotNull(broken.Get("draft")["gorynych/group"]);
        Assert.AreEqual(1, broken.List("a").Count);
        var affected = s.Delete("a", "same", out var changed);
        Assert.IsTrue(changed);
        CollectionAssert.AreEqual(new[] { "draft" }, affected);
        var reopened = new UiMetadataStore(path);
        Assert.AreEqual(0, reopened.List("a").Count);
        Assert.AreEqual(1, reopened.List("b").Count);
        Assert.IsNull(reopened.Get("draft")["gorynych/group"]);
        Assert.IsNotNull(reopened.Get("archived")["gorynych/group"]);
        Assert.IsNull(reopened.Get("draft")["nested"]!["a"]);
        Assert.AreEqual(2, (int)reopened.Get("draft")["nested"]!["b"]!);
        Assert.AreEqual(2, reopened.Get("draft")["nested"]!["c"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task Protocol_InitialPatchAndGroupDeletion()
    {
        var agent = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "t", PeerServerCommand = ["svc", "peers"] }, [agent.Adapter]);
        await using var client = await TestClient.ConnectAsync(conductor, agent: "kimi");
        Assert.AreEqual(true, (bool?)client.Initialized!.AgentCapabilities!.Meta!["gorynych/groups"]);
        await client.Rpc.SendRequestAsync("_gorynych/groups/set", new JsonObject { ["projectKey"] = "p", ["groupId"] = "g", ["name"] = "Group" });
        var created = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath(), McpServers = [], Meta = new JsonObject { ["gorynych/attributes"] = JsonNode.Parse("""{"gorynych/group":{"projectKey":"p","groupId":"g"},"x":1}""") } });
        var id = created.SessionId;
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "hello" }] });
        var before = conductor.Store.Get(id)!.UpdatedAt;
        var patch = await client.Rpc.SendRequestAsync("_gorynych/session_attributes/patch", new JsonObject { ["sessionId"] = id, ["patch"] = new JsonObject { ["x"] = 2 } });
        Assert.AreEqual(2, (int)patch!["attributes"]!["x"]!);
        Assert.AreEqual(before, conductor.Store.Get(id)!.UpdatedAt);
        await client.Rpc.SendRequestAsync("_gorynych/groups/delete", new JsonObject { ["projectKey"] = "p", ["groupId"] = "g" });
        var result = await client.Rpc.SendRequestAsync("_gorynych/session_attributes/get", new JsonObject { ["sessionId"] = id });
        Assert.IsNull(result!["attributes"]!["gorynych/group"]);
        Assert.AreEqual(2, (int)result["attributes"]!["x"]!);
        var cleared = await client.Rpc.SendRequestAsync("_gorynych/session_attributes/patch", new JsonObject { ["sessionId"] = id, ["patch"] = new JsonObject { ["x"] = null } });
        Assert.AreEqual(0, cleared!["attributes"]!.AsObject().Count);
        for (int i = 0; i < 100 && !client.Snapshot().Any(u => u["_meta"]?["gorynych/attributes"] is JsonObject attrs && attrs.Count == 0); i++) await Task.Delay(20);
        Assert.IsTrue(client.Snapshot().Any(u => u["_meta"]?["gorynych/attributes"] is JsonObject attrs && attrs.Count == 0));
        var list = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
        Assert.AreEqual(0, list.Sessions.Single(s => s.SessionId == id).Meta![GorynychProtocol.MetaAttributes]!.AsObject().Count);
    }

    [TestMethod]
    public async Task Protocol_SharedCatalogFailureAndNoNativeLoad()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var claude = new LifecycleAgent("claude", canDelete: false);
        await using var conductor = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "t", PeerServerCommand = ["svc", "peers"] }, [kimi.Adapter, claude.Adapter]);
        await using var a = await TestClient.ConnectAsync(conductor, agent: "kimi");
        await using var b = await TestClient.ConnectAsync(conductor, agent: "claude");
        await a.Rpc.SendRequestAsync("_gorynych/groups/set", new JsonObject { ["projectKey"] = "p", ["groupId"] = "g", ["name"] = "Group" });
        var shared = await b.Rpc.SendRequestAsync("_gorynych/groups/list", new JsonObject { ["projectKey"] = "p" });
        Assert.AreEqual(1, shared!["groups"]!.AsArray().Count);
        for (int i = 0; i < 100 && (!a.Notices.Contains("_gorynych/groups_changed") || !b.Notices.Contains("_gorynych/groups_changed")); i++) await Task.Delay(20);
        CollectionAssert.Contains(a.Notices, "_gorynych/groups_changed");
        CollectionAssert.Contains(b.Notices, "_gorynych/groups_changed");
        var created = await a.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath(), McpServers = [] });
        var id = created.SessionId;
        var calls = kimi.CallsFor(id);
        await a.Rpc.SendRequestAsync("_gorynych/session_attributes/patch", new JsonObject { ["sessionId"] = id, ["patch"] = new JsonObject { ["x"] = 1 } });
        CollectionAssert.AreEqual(calls, kimi.CallsFor(id));
        for (int i = 0; i < 100 && !a.Snapshot().Any(u => (int?)u["_meta"]?[GorynychProtocol.MetaAttributes]?["x"] == 1); i++) await Task.Delay(20);
        var updatesBefore = a.Snapshot().Length;
        var changesBefore = a.ChangeLog().Length;
        int noticesBefore; lock (a.Notices) noticesBefore = a.Notices.Count;
        conductor.UiMetadata.WriteFile = (_, _) => throw new IOException("injected");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => a.Rpc.SendRequestAsync("_gorynych/session_attributes/patch", new JsonObject { ["sessionId"] = id, ["patch"] = new JsonObject { ["x"] = 2 } }));
        var result = await a.Rpc.SendRequestAsync("_gorynych/session_attributes/get", new JsonObject { ["sessionId"] = id });
        Assert.AreEqual(1, (int)result!["attributes"]!["x"]!);
        Assert.AreEqual(updatesBefore, a.Snapshot().Length);
        Assert.AreEqual(changesBefore, a.ChangeLog().Length);
        lock (a.Notices) Assert.AreEqual(noticesBefore, a.Notices.Count);
        conductor.UiMetadata.WriteFile = (_, _) => throw new UnauthorizedAccessException("injected");
        await a.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });
        Assert.IsNull(conductor.Store.Get(id));
    }

    [TestMethod]
    public async Task Protocol_RestartRetainsArchivedAttributes()
    {
        var directory = TestDirs.New();
        var agent = new LifecycleAgent("kimi", canDelete: true);
        Conductor Create() => new(new ConductorOptions { DataDirectory = directory, ServiceId = "t", PeerServerCommand = ["svc", "peers"] }, [agent.Adapter]);
        string id;
        await using (var first = Create())
        await using (var client = await TestClient.ConnectAsync(first, agent: "kimi"))
        {
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodGroupsSet, new JsonObject { ["projectKey"] = "p", ["groupId"] = "empty", ["name"] = "Empty" });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodGroupsSet, new JsonObject { ["projectKey"] = "p", ["groupId"] = "occupied", ["name"] = "Occupied" });
            id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath(), McpServers = [] })).SessionId;
            await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = "hello" }] });
            await client.Rpc.SendRequestAsync("_gorynych/session_attributes/patch", new JsonObject { ["sessionId"] = id, ["patch"] = new JsonObject { ["label"] = "kept", [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "p", ["groupId"] = "occupied" } } });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = id, ["archived"] = true });
        }
        await using (var second = Create())
        await using (var client = await TestClient.ConnectAsync(second, agent: "kimi"))
        {
            Assert.IsTrue(second.Store.Get(id)!.Archived);
            var result = await client.Rpc.SendRequestAsync("_gorynych/session_attributes/get", new JsonObject { ["sessionId"] = id });
            Assert.AreEqual("kept", (string?)result!["attributes"]!["label"]);
            var groups = await client.Rpc.SendRequestAsync("_gorynych/groups/list", new JsonObject { ["projectKey"] = "p" });
            Assert.AreEqual(2, groups!["groups"]!.AsArray().Count);
            var list = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest { Meta = new JsonObject { [GorynychProtocol.MetaIncludeArchived] = true } });
            Assert.AreEqual("occupied", (string?)list.Sessions.Single(s => s.SessionId == id).Meta![GorynychProtocol.MetaAttributes]![GorynychProtocol.MetaGroup]!["groupId"]);
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = id, ["archived"] = false });
            result = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesGet, new JsonObject { ["sessionId"] = id });
            Assert.AreEqual("occupied", (string?)result!["attributes"]![GorynychProtocol.MetaGroup]!["groupId"]);
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = id, ["archived"] = true });
            var draft = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest { Cwd = Path.GetTempPath(), McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAttributes] = new JsonObject { [GorynychProtocol.MetaGroup] = new JsonObject { ["projectKey"] = "p", ["groupId"] = "occupied" }, ["draft"] = true } } });
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodGroupsDelete, new JsonObject { ["projectKey"] = "p", ["groupId"] = "occupied" });
            result = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesGet, new JsonObject { ["sessionId"] = id });
            Assert.IsNull(result!["attributes"]![GorynychProtocol.MetaGroup]);
            Assert.AreEqual("kept", (string?)result["attributes"]!["label"]);
            var draftAttributes = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesGet, new JsonObject { ["sessionId"] = draft.SessionId });
            Assert.IsNull(draftAttributes!["attributes"]![GorynychProtocol.MetaGroup]);
            Assert.AreEqual(true, (bool?)draftAttributes["attributes"]!["draft"]);
            await client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = id, ["archived"] = false });
            result = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodAttributesGet, new JsonObject { ["sessionId"] = id });
            Assert.IsNull(result!["attributes"]![GorynychProtocol.MetaGroup]);
            Assert.AreEqual("kept", (string?)result["attributes"]!["label"]);
        }
    }
}
