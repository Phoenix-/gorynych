using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;

namespace Gorynych.Tests;

[TestClass]
public sealed class AcpTypesTests
{
    [TestMethod]
    public void SessionUpdate_RoundTripsKnownVariant_WithDiscriminatorFirst()
    {
        var json = """{"sessionId":"s1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hi"},"messageId":"m1"}}""";
        var n = JsonSerializer.Deserialize(json, AcpJsonContext.Default.SessionNotification)!;
        var chunk = (SessionUpdateAgentMessageChunk)n.Update;
        Assert.AreEqual("agent_message_chunk", chunk.Tag);
        Assert.AreEqual("hi", ((TextContent)chunk.Content).Text);
        Assert.AreEqual("m1", chunk.MessageId);
        var back = JsonSerializer.Serialize(n, AcpJsonContext.Default.SessionNotification);
        Assert.AreEqual(json, back);
    }

    [TestMethod]
    public void SessionUpdate_KeepsUnknownVariantRaw()
    {
        var json = """{"sessionId":"s1","update":{"sessionUpdate":"session_status","status":"working","extra":[1,2]}}""";
        var n = JsonSerializer.Deserialize(json, AcpJsonContext.Default.SessionNotification)!;
        var unknown = (SessionUpdate.Unknown)n.Update;
        Assert.AreEqual("session_status", unknown.Tag);
        Assert.AreEqual(json, JsonSerializer.Serialize(n, AcpJsonContext.Default.SessionNotification));
    }

    [TestMethod]
    public void OpenEnum_ToleratesUnknownValues()
    {
        var json = """{"stopReason":"some_future_reason"}""";
        var r = JsonSerializer.Deserialize(json, AcpJsonContext.Default.PromptResponse)!;
        Assert.AreEqual("some_future_reason", r.StopReason.Value);
        Assert.AreEqual(StopReason.EndTurn, StopReason.Create("end_turn"));
        Assert.AreEqual(json, JsonSerializer.Serialize(r, AcpJsonContext.Default.PromptResponse));
    }

    [TestMethod]
    public void McpServer_StdioIsTheVariantWithoutType()
    {
        var json = """{"cwd":"C:\\w","mcpServers":[{"name":"a","command":"x.exe","args":[],"env":[]},{"type":"http","name":"b","url":"http://h","headers":[]}]}""";
        var r = JsonSerializer.Deserialize(json, AcpJsonContext.Default.NewSessionRequest)!;
        Assert.IsInstanceOfType<McpServerStdio>(r.McpServers[0]);
        Assert.IsInstanceOfType<McpServerHttp>(r.McpServers[1]);
        Assert.AreEqual(json, JsonSerializer.Serialize(r, AcpJsonContext.Default.NewSessionRequest));
    }

    [TestMethod]
    public void PermissionOutcome_Variants()
    {
        var selected = JsonSerializer.Deserialize("""{"outcome":{"outcome":"selected","optionId":"allow"}}""", AcpJsonContext.Default.RequestPermissionResponse)!;
        Assert.AreEqual("allow", ((SelectedPermissionOutcome)selected.Outcome).OptionId);
        var cancelled = JsonSerializer.Deserialize("""{"outcome":{"outcome":"cancelled"}}""", AcpJsonContext.Default.RequestPermissionResponse)!;
        Assert.IsInstanceOfType<RequestPermissionOutcomeCancelled>(cancelled.Outcome);
        Assert.AreEqual("""{"outcome":{"outcome":"cancelled"}}""", JsonSerializer.Serialize(cancelled, AcpJsonContext.Default.RequestPermissionResponse));
    }

    [TestMethod]
    public void ToolCall_SerializesAsSessionUpdate()
    {
        SessionUpdate update = new ToolCall
        {
            ToolCallId = "t1",
            Title = "Write hello.txt",
            Kind = ToolKind.Edit,
            Status = ToolCallStatus.Pending,
            RawInput = new JsonObject { ["file_path"] = "hello.txt" },
        };
        var node = AcpJson.ToNode(update)!.AsObject();
        Assert.AreEqual("tool_call", (string?)node["sessionUpdate"]);
        Assert.AreEqual("edit", (string?)node["kind"]);
        Assert.AreEqual("hello.txt", (string?)node["rawInput"]!["file_path"]);
        Assert.IsInstanceOfType<ToolCall>(AcpJson.FromNode<SessionUpdate>(node));
    }

    [TestMethod]
    public void Initialize_ParsesDefaults()
    {
        var r = JsonSerializer.Deserialize("""{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"sessionCapabilities":{"list":{},"resume":{}}}}""", AcpJsonContext.Default.InitializeResponse)!;
        Assert.AreEqual(1, r.ProtocolVersion);
        Assert.IsTrue(r.AgentCapabilities!.LoadSession);
        Assert.IsNotNull(r.AgentCapabilities.SessionCapabilities!.List);
        Assert.IsNull(r.AgentCapabilities.SessionCapabilities.Close);
    }
}
