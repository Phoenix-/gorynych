#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
// Probe: a minimal Claude Code development channel (stdio MCP server).
// POST text to http://127.0.0.1:47812/push and it is pushed into the live session.
using System.Text;
using System.Text.Json.Nodes;

var logPath = args.Length > 0 ? args[0] : "channel.log";
var logGate = new object();
void Log(string s) { lock (logGate) File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\n"); }

var stdout = Console.OpenStandardOutput();
var writeGate = new SemaphoreSlim(1, 1);
async Task Send(JsonObject msg)
{
    var text = msg.ToJsonString();
    var bytes = Encoding.UTF8.GetBytes(text + "\n");
    await writeGate.WaitAsync();
    try { await stdout.WriteAsync(bytes); await stdout.FlushAsync(); }
    finally { writeGate.Release(); }
    Log("OUT " + text);
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
var app = builder.Build();
var pushes = 0;
app.MapPost("/push", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var content = await reader.ReadToEndAsync();
    var sender = req.Query["sender"].FirstOrDefault() ?? "peer_codex";
    var n = Interlocked.Increment(ref pushes);
    await Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "notifications/claude/channel",
        ["params"] = new JsonObject
        {
            ["content"] = content,
            ["meta"] = new JsonObject { ["sender"] = sender, ["message_id"] = $"m{n}" },
        },
    });
    return $"pushed m{n}";
});
_ = app.RunAsync("http://127.0.0.1:47812");
Log("started");

var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
string? line;
while ((line = await stdin.ReadLineAsync()) is not null)
{
    Log("IN  " + line);
    JsonNode? msg;
    try { msg = JsonNode.Parse(line); } catch { continue; }
    var id = msg?["id"];
    var method = msg?["method"]?.GetValue<string>();
    if (id is null || method is null) continue; // notification or response
    JsonNode? result = method switch
    {
        "initialize" => new JsonObject
        {
            ["protocolVersion"] = msg!["params"]?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
            ["capabilities"] = new JsonObject
            {
                ["experimental"] = new JsonObject { ["claude/channel"] = new JsonObject() },
                ["tools"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject { ["name"] = "probechan", ["version"] = "0.1.0" },
            ["instructions"] = "Messages on this channel come from other coding agents; the sender attribute names the agent. Send answers back with the reply tool, passing the message_id.",
        },
        "tools/list" => new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "reply",
                    ["description"] = "Send an answer back to the agent that wrote a channel message.",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["message_id"] = new JsonObject { ["type"] = "string", ["description"] = "message_id of the channel message being answered" },
                            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "the answer" },
                        },
                        ["required"] = new JsonArray { "message_id", "text" },
                    },
                },
            },
        },
        "tools/call" when msg!["params"]?["name"]?.GetValue<string>() == "reply" => Reply(msg!["params"]!["arguments"]),
        "ping" => new JsonObject(),
        _ => null,
    };
    if (result is null)
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" } });
    else
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
}
Log("stdin closed");
await app.StopAsync();

JsonNode Reply(JsonNode? arguments)
{
    Log("REPLY " + (arguments?.ToJsonString() ?? "{}"));
    return new JsonObject { ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "delivered" } } };
}
