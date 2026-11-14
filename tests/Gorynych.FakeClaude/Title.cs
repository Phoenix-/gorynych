// The isolated title face of the fake: `claude -p --output-format json --json-schema ...` with no
// tools and no session, as Claude Code 2.1.302 is launched for a generated title. The prompt decides
// what happens: "hold-title" waits for a "release-title" file in the working folder, "timeout-title"
// never ends, "fail-title" reports an error, and anything else returns a structured title.
using System.Text;
using System.Text.Json.Nodes;

static class FakeTitle
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var id = Arg("--session-id") ?? "title";
        var root = Path.Combine(Path.GetTempPath(), "gorynych-fake-claude");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "title-" + id + ".args.json"), new JsonArray(args.Select(a => (JsonNode)a).ToArray()).ToJsonString());
        var prompt = args.LastOrDefault() ?? "";
        if (prompt.Contains("hold-title", StringComparison.OrdinalIgnoreCase))
        {
            var current = Environment.CurrentDirectory;
            var parent = Directory.GetParent(current)?.FullName ?? current;
            while (!File.Exists(Path.Combine(current, "release-title")) && !File.Exists(Path.Combine(parent, "release-title")))
                await Task.Delay(20);
        }
        if (prompt.Contains("timeout-title", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        Console.OutputEncoding = new UTF8Encoding(false);
        if (prompt.Contains("fail-title", StringComparison.OrdinalIgnoreCase))
        {
            Console.Out.WriteLine(new JsonObject
            {
                ["type"] = "result", ["subtype"] = "error_during_execution", ["is_error"] = true, ["result"] = "no title",
            }.ToJsonString());
            return 1;
        }
        var title = prompt.Contains("plain-title", StringComparison.OrdinalIgnoreCase) ? "plain generated title" : "Fake generated title";
        Console.Out.WriteLine(new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["result"] = new JsonObject { ["title"] = title }.ToJsonString(),
            ["structured_output"] = new JsonObject { ["title"] = title },
        }.ToJsonString());
        return 0;
    }
}
