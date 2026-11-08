// A stand-in for the interactive `claude` binary, for adapter tests without a live agent.
// It takes the same per-launch flags, posts hook payloads shaped like Claude Code 2.1.302's
// to the URL in --settings, turns on bracketed paste, and treats a pasted line followed by
// Enter as the next user turn. A prompt containing "write" asks for a Write approval; one
// containing "on your own" is followed by a turn of Claude's own, for a channel message.
// Launched with development channels, it first shows Claude's notice and waits for Enter.
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var settingsPath = Arg("--settings") ?? throw new ArgumentException("--settings is required");
var sessionId = Arg("--session-id") ?? Arg("--resume") ?? throw new ArgumentException("--session-id or --resume is required");
var dash = Array.IndexOf(args, "--");
var firstPrompt = dash >= 0 && dash + 1 < args.Length ? args[dash + 1] : null;
var url = (string)JsonNode.Parse(File.ReadAllText(settingsPath))!["hooks"]!["Stop"]![0]!["hooks"]![0]!["url"]!;
var cwd = Environment.CurrentDirectory;
var transcript = Path.Combine(Path.GetTempPath(), "gorynych-fake-claude", sessionId + ".jsonl");
Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
// Leave the launch arguments next to the transcript, so tests can check the per-launch flags.
File.WriteAllText(Path.ChangeExtension(transcript, ".args.json"), new JsonArray(args.Select(a => (JsonNode)a).ToArray()).ToJsonString());
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var turns = 0;
var tools = 0;

Console.OutputEncoding = Encoding.UTF8;
RawInput();
var stdin = Console.OpenStandardInput();
if (args.Contains("--dangerously-load-development-channels"))
{
    // Real Claude shows a full-screen notice at every launch with development channels and goes
    // on only after Enter; nothing else may confirm it for the person.
    Console.Out.Write("WARNING: Loading development channels\r\n--dangerously-load-development-channels is for local channel development only.\r\nEnter to confirm · Esc to cancel\r\n");
    Console.Out.Flush();
    var key = new byte[1];
    while (true)
    {
        if (stdin.Read(key, 0, 1) <= 0) return 1;
        if (key[0] == '\r') break;
    }
    File.AppendAllText(Path.ChangeExtension(transcript, ".notice.txt"), "confirmed\n");
}
Console.Out.Write("\x1b[?2004h fake claude ready\r\n");
Console.Out.Flush();

// "hang" stands for a launch stuck on a dialog nobody answers: no hooks ever arrive.
if (firstPrompt?.Contains("hang", StringComparison.OrdinalIgnoreCase) == true)
{
    Console.Out.Write("Do you want to continue? (a dialog the adapter does not know)\r\n");
    await Task.Delay(Timeout.Infinite);
}
if (firstPrompt is not null) await TurnAsync(firstPrompt);

var buffer = new byte[4096];
var pending = new StringBuilder();
var decoder = Encoding.UTF8.GetDecoder();
while (true)
{
    var n = stdin.Read(buffer, 0, buffer.Length);
    if (n <= 0) break;
    var chars = new char[decoder.GetCharCount(buffer, 0, n)];
    decoder.GetChars(buffer, 0, n, chars, 0);
    pending.Append(chars);
    var text = pending.ToString();
    var enter = text.LastIndexOf('\r');
    if (enter < 0 || text.LastIndexOf("\x1b[201~", StringComparison.Ordinal) > enter) continue;
    var line = text[..enter].Replace("\x1b[200~", "").Replace("\x1b[201~", "").Trim('\x1b', '\r', '\n', ' ');
    pending.Clear();
    if (line.Length > 0) await TurnAsync(line);
}
return 0;

async Task TurnAsync(string prompt)
{
    turns++;
    var promptId = Guid.NewGuid().ToString();
    await PostAsync(Hook("UserPromptSubmit", promptId, new JsonObject { ["prompt"] = prompt }));
    string answer;
    if (prompt.Contains("write", StringComparison.OrdinalIgnoreCase))
    {
        var toolUseId = $"toolu_fake{++tools}";
        var input = new JsonObject { ["file_path"] = Path.Combine(cwd, "fake.txt"), ["content"] = "hi" };
        await PostAsync(Hook("PreToolUse", promptId, new JsonObject { ["tool_name"] = "Write", ["tool_input"] = input.DeepClone(), ["tool_use_id"] = toolUseId }));
        var reply = await PostAsync(Hook("PermissionRequest", promptId, new JsonObject { ["tool_name"] = "Write", ["tool_input"] = input.DeepClone() }));
        var allowed = (string?)reply?["hookSpecificOutput"]?["decision"]?["behavior"] == "allow";
        if (allowed)
            await PostAsync(Hook("PostToolUse", promptId, new JsonObject
            {
                ["tool_name"] = "Write", ["tool_input"] = input.DeepClone(), ["tool_use_id"] = toolUseId,
                ["tool_response"] = new JsonObject { ["type"] = "create", ["filePath"] = (string)input["file_path"]! },
            }));
        answer = allowed ? "wrote it" : "not allowed";
    }
    else if (prompt.Contains("peers", StringComparison.OrdinalIgnoreCase))
    {
        // Calls the two gorynych peer tools the way Claude would: each asks PermissionRequest first.
        var verdicts = new List<string>();
        foreach (var (tool, input) in new[]
        {
            ("mcp__gorynych__list_agents", new JsonObject()),
            ("mcp__gorynych__ask_agent", new JsonObject { ["session_id"] = "other", ["question"] = "tabs?" }),
        })
        {
            var reply = await PostAsync(Hook("PermissionRequest", promptId, new JsonObject { ["tool_name"] = tool, ["tool_input"] = input }));
            verdicts.Add($"{tool.Split("__")[2]}:{(string?)reply?["hookSpecificOutput"]?["decision"]?["behavior"]}");
        }
        answer = string.Join(" ", verdicts);
    }
    else if (prompt.Contains("question", StringComparison.OrdinalIgnoreCase))
    {
        var toolUseId = $"toolu_fake{++tools}";
        var input = JsonNode.Parse("""
            {"questions":[{"question":"Which color?","header":"Color","multiSelect":false,
              "options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cold"}]}]}
            """)!.AsObject();
        await PostAsync(Hook("PreToolUse", promptId, new JsonObject { ["tool_name"] = "AskUserQuestion", ["tool_input"] = input.DeepClone(), ["tool_use_id"] = toolUseId }));
        var reply = await PostAsync(Hook("PermissionRequest", promptId, new JsonObject { ["tool_name"] = "AskUserQuestion", ["tool_input"] = input.DeepClone() }));
        var decision = reply?["hookSpecificOutput"]?["decision"];
        var chosen = (string?)decision?["updatedInput"]?["answers"]?["Which color?"];
        answer = (string?)decision?["behavior"] == "allow" && chosen is not null ? "you chose " + chosen : "no answer";
    }
    else answer = "echo: " + prompt;
    await PostAsync(Hook("MessageDisplay", promptId, new JsonObject
    {
        ["turn_id"] = Guid.NewGuid().ToString(), ["message_id"] = Guid.NewGuid().ToString(), ["index"] = 0, ["final"] = true, ["delta"] = answer,
    }));
    if (turns == 1) File.AppendAllText(transcript, $$"""{"type":"ai-title","aiTitle":"Fake title","sessionId":"{{sessionId}}"}""" + "\n");
    await PostAsync(Hook("Stop", promptId, new JsonObject { ["stop_hook_active"] = false, ["last_assistant_message"] = answer }));
    if (prompt.Contains("on your own", StringComparison.OrdinalIgnoreCase))
        await TurnAsync("<channel source=\"gorynych\" kind=\"report\">\n[Report · from session_id k9 (kimi) · your child session · its turn ended]\ndone\n</channel>");
}

JsonObject Hook(string name, string promptId, JsonObject fields)
{
    var e = new JsonObject
    {
        ["session_id"] = sessionId, ["transcript_path"] = transcript, ["cwd"] = cwd, ["prompt_id"] = promptId,
        ["permission_mode"] = "default", ["hook_event_name"] = name,
    };
    foreach (var (k, v) in fields.ToList()) { fields.Remove(k); e[k] = v; }
    return e;
}

async Task<JsonObject?> PostAsync(JsonObject e)
{
    using var response = await http.PostAsync(url, new StringContent(e.ToJsonString(), Encoding.UTF8, "application/json"));
    var body = await response.Content.ReadAsStringAsync();
    return body.Length > 0 ? JsonNode.Parse(body) as JsonObject : null;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Raw console input with VT sequences passed through, as a TUI sets it up.
static void RawInput()
{
    var handle = Native.GetStdHandle(-10);
    if (!Native.GetConsoleMode(handle, out var mode)) return;
    mode &= ~(0x0001u | 0x0002u | 0x0004u); // processed, line and echo input off
    mode |= 0x0200u;                         // ENABLE_VIRTUAL_TERMINAL_INPUT
    Native.SetConsoleMode(handle, mode);
}

static partial class Native
{
    [LibraryImport("kernel32.dll")] internal static partial nint GetStdHandle(int which);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetConsoleMode(nint handle, out uint mode);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetConsoleMode(nint handle, uint mode);
}
