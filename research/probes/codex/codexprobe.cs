// Probe: drive `codex app-server` over stdio: start a thread, run one turn, name it, list it, archive it.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

var exe = args[0];
var cwd = args[1];
var logPath = args[2];
var logGate = new object();
string Trunc(string s) => s.Length > 400 ? s[..400] + "..." : s;
void Log(string s) { lock (logGate) File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\n"); }
void Say(string s) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + s); Log("### " + s); }

var psi = new ProcessStartInfo(exe)
{
    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
    WorkingDirectory = cwd,
};
psi.ArgumentList.Add("app-server");
var proc = Process.Start(psi)!;
var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var pending = new ConcurrentDictionary<long, TaskCompletionSource<JsonNode>>();
var notes = new BlockingCollection<JsonNode>();

_ = Task.Run(async () =>
{
    string? line;
    while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
    {
        Log("IN  " + Trunc(line));
        JsonNode? m;
        try { m = JsonNode.Parse(line); } catch { continue; }
        if (m is null) continue;
        var hasMethod = m["method"] is not null;
        if (!hasMethod && m["id"] is JsonNode id && pending.TryRemove(id.GetValue<long>(), out var tcs)) tcs.SetResult(m);
        else if (hasMethod && m["id"] is not null)
        {
            // A server request (for example an approval): decline it, this probe runs no tools.
            Say("server request " + m["method"]);
            var reply = new JsonObject { ["id"] = m["id"]!.DeepClone(), ["result"] = new JsonObject { ["decision"] = "decline" } };
            await stdin.WriteLineAsync(reply.ToJsonString());
        }
        else if (hasMethod) notes.Add(m);
    }
});
_ = Task.Run(async () => { string? l; while ((l = await proc.StandardError.ReadLineAsync()) is not null) Log("ERR " + Trunc(l)); });

long next = 0;
async Task<JsonNode> Call(string method, JsonObject prms)
{
    var id = Interlocked.Increment(ref next);
    var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
    pending[id] = tcs;
    var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = prms };
    Log("OUT " + msg.ToJsonString());
    await stdin.WriteLineAsync(msg.ToJsonString());
    var r = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(180));
    if (r["error"] is not null) Say($"{method} ERROR {r["error"]!.ToJsonString()}");
    return r;
}
JsonNode? WaitNote(string method, int seconds)
{
    var deadline = DateTime.Now.AddSeconds(seconds);
    while (DateTime.Now < deadline)
        if (notes.TryTake(out var n, 500) && n["method"]!.GetValue<string>() == method) return n;
    Say("timeout waiting for " + method);
    return null;
}
JsonNode? Find(JsonNode list, string threadId) =>
    list["result"]?["data"]?.AsArray().FirstOrDefault(t => t?["id"]?.GetValue<string>() == threadId);

await Call("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "zerus_probe", ["title"] = "Zerus probe", ["version"] = "0.1.0" } });
await stdin.WriteLineAsync(new JsonObject { ["method"] = "initialized" }.ToJsonString());

var started = await Call("thread/start", new JsonObject { ["cwd"] = cwd, ["approvalPolicy"] = "never", ["sandbox"] = "read-only" });
var threadId = started["result"]!["thread"]!["id"]!.GetValue<string>();
Say("thread started " + threadId);

await Call("turn/start", new JsonObject
{
    ["threadId"] = threadId,
    ["input"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Reply with the single word READY." } },
    ["effort"] = "low",
});
var done = WaitNote("turn/completed", 150);
Say("turn completed: " + (done is not null));

var before = await Call("thread/list", new JsonObject { ["limit"] = 10 });
var mine = Find(before, threadId);
Say("listed before naming: name=" + mine?["name"] + " preview=" + Trunc(mine?["preview"]?.ToString() ?? "null"));

await Call("thread/name/set", new JsonObject { ["threadId"] = threadId, ["name"] = "zerus probe" });
var named = WaitNote("thread/name/updated", 20);
Say("thread/name/updated: " + named?["params"]?.ToJsonString());

var after = await Call("thread/list", new JsonObject { ["limit"] = 10 });
Say("listed after naming: name=" + Find(after, threadId)?["name"]);

await Call("thread/archive", new JsonObject { ["threadId"] = threadId });
var archived = WaitNote("thread/archived", 20);
Say("thread/archived: " + archived?["params"]?.ToJsonString());

var active = await Call("thread/list", new JsonObject { ["limit"] = 10 });
var arch = await Call("thread/list", new JsonObject { ["limit"] = 10, ["archived"] = true });
Say("in active list: " + (Find(active, threadId) is not null) + ", in archived list: " + (Find(arch, threadId) is not null));
Console.WriteLine("THREAD " + threadId);

stdin.Close();
if (!proc.WaitForExit(5000)) proc.Kill(true);
