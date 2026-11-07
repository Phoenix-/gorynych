#:sdk Microsoft.NET.Sdk.Web
// Probe: receives Claude Code http hooks, logs every event, answers PermissionRequest.
using System.Text.Json.Nodes;

var log = args.Length > 0 ? args[0] : "events.jsonl";
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();
var gate = new object();

app.MapGet("/health", () => "ok");
app.MapPost("/shutdown", (IHostApplicationLifetime life) => { life.StopApplication(); return "bye"; });
app.MapPost("/hook", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    JsonNode? evt = null;
    try { evt = JsonNode.Parse(body); } catch { }
    var name = evt?["hook_event_name"]?.GetValue<string>() ?? "?";
    var reply = new JsonObject();
    if (name == "PermissionRequest" && evt is not null)
    {
        var decision = new JsonObject { ["behavior"] = "allow" };
        if (evt["tool_name"]?.GetValue<string>() == "AskUserQuestion")
        {
            // Answer every question with its LAST option, so the choice is visibly ours.
            var input = evt["tool_input"]!.DeepClone().AsObject();
            var answers = new JsonObject();
            foreach (var q in input["questions"]!.AsArray())
            {
                var options = q!["options"]!.AsArray();
                answers[q["question"]!.GetValue<string>()] = options[options.Count - 1]!["label"]!.GetValue<string>();
            }
            input["answers"] = answers;
            decision["updatedInput"] = input;
        }
        reply["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision };
    }
    var line = new JsonObject
    {
        ["at"] = DateTime.Now.ToString("HH:mm:ss.fff"),
        ["event"] = name,
        ["in"] = evt?.DeepClone() ?? JsonValue.Create(body),
        ["out"] = reply.DeepClone(),
    };
    lock (gate) File.AppendAllText(log, line.ToJsonString() + "\n");
    return Results.Text(reply.ToJsonString(), "application/json");
});

app.Run("http://127.0.0.1:47811");
