using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

// A console ACP client for the Gorynych service, for manual and scripted checks.
//
//   gorynych-test [--pipe <name>] [--service <exe>] [--approve ask|allow|deny] <command>
//     status | list | shutdown
//     new --agent <claude|kimi> --cwd <dir> [--wait-title <s>] <prompt…>
//     prompt <sessionId> <prompt…>
//     load <sessionId> [--follow]          replay the history, then optionally keep watching
//     opinion --to <sessionId> [--sender <name>] <question…>
Console.OutputEncoding = System.Text.Encoding.UTF8;
var options = Options.Parse(args);
if (options.Command is null)
{
    Console.Error.WriteLine("usage: gorynych-test [--pipe name] [--approve ask|allow|deny] status|list|new|prompt|load|delete|opinion|shutdown …");
    return 2;
}

await using var client = await Client.ConnectAsync(options);
var printer = new FeedPrinter();
client.Rpc.NotificationHandler = (method, p) =>
{
    if (method == AcpMethods.Client.SessionUpdate) printer.Print(p);
    return Task.CompletedTask;
};
client.Rpc.RequestHandler = (req, ct) => req.Method switch
{
    AcpMethods.Client.SessionRequestPermission => Approvals.AnswerAsync(req, options.Approve, printer),
    AcpMethods.Client.ElicitationCreate => Approvals.FillAsync(req, options.Approve, printer),
    _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, $"test client does not handle {req.Method}"),
};
client.Rpc.Start();

var init = await client.Rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
{
    ProtocolVersion = AcpMethods.ProtocolVersion,
    ClientCapabilities = new ClientCapabilities
    {
        Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
        Terminal = false,
        Elicitation = new ElicitationCapabilities { Form = new ElicitationFormCapabilities() },
    },
    ClientInfo = new Implementation { Name = "gorynych-test", Version = "0.1.0" },
});
Info($"connected to {init.AgentInfo?.Name} {init.AgentInfo?.Version}, service {(string?)init.Meta?[GorynychProtocol.MetaServiceId]}");

try
{
    return await RunAsync(client, options, printer);
}
catch (JsonRpcException e)
{
    printer.EndLine();
    Console.Error.WriteLine($"· error {e.Code}: {e.Message}");
    return 1;
}

static async Task<int> RunAsync(Client client, Options options, FeedPrinter printer)
{
switch (options.Command)
{
    case "status":
        Console.WriteLine((await client.Rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject()))?.ToJsonString(new() { WriteIndented = true }));
        return 0;
    case "shutdown":
        await client.Rpc.SendRequestAsync(GorynychProtocol.MethodShutdown, new JsonObject());
        Info("service asked to exit");
        return 0;
    case "list":
    {
        var list = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest { Cwd = options.Cwd });
        foreach (var s in list.Sessions)
            Console.WriteLine($"{s.UpdatedAt,-34} {(string?)s.Meta?[GorynychProtocol.MetaAgent],-7} {s.SessionId}  {s.Title}  [{s.Cwd}]");
        if (list.NextCursor is not null) Console.WriteLine("(more…)");
        return 0;
    }
    case "new":
    {
        var cwd = Path.GetFullPath(options.Cwd ?? Environment.CurrentDirectory);
        var created = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = [],
            Meta = options.Agent is null ? null : new JsonObject { [GorynychProtocol.MetaAgent] = options.Agent },
        });
        Info($"session {created.SessionId} ({(string?)created.Meta?[GorynychProtocol.MetaAgent]}) in {cwd}");
        var stop = await PromptAsync(client, created.SessionId, options.Text);
        await WaitForTitleAsync(printer, options.WaitTitleSeconds);
        Info($"session {created.SessionId}: {stop}; title: {printer.Title ?? "(none yet)"}");
        return 0;
    }
    case "prompt":
    {
        var stop = await PromptAsync(client, options.Positional(0), options.TextAfter(1));
        await WaitForTitleAsync(printer, options.WaitTitleSeconds);
        Info($"stop: {stop}; title: {printer.Title ?? "(unchanged)"}");
        return 0;
    }
    case "load":
    {
        var id = options.Positional(0);
        await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
            AcpJson.ToNode(new LoadSessionRequest { SessionId = id, Cwd = options.Cwd ?? Environment.CurrentDirectory, McpServers = [] }));
        printer.EndLine();
        Info($"history replayed: {printer.Count} updates; title: {printer.Title ?? "(none)"}");
        if (options.Follow)
        {
            Info("following; Ctrl+C to stop");
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        }
        return 0;
    }
    case "delete":
    {
        var id = options.Positional(0);
        await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });
        Info($"session {id} deleted");
        return 0;
    }
    case "opinion":
    {
        var result = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = options.To ?? throw new ArgumentException("--to is required"),
            ["sender"] = options.Sender ?? "peer_kimi",
            ["text"] = options.Text,
        });
        Info($"reply: {(string?)result?["text"]}");
        return 0;
    }
    default:
        Console.Error.WriteLine($"unknown command {options.Command}");
        return 2;
}
}

static async Task<string> PromptAsync(Client client, string sessionId, string text)
{
    if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("a prompt is required");
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        _ = client.Rpc.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = sessionId });
    };
    var response = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
        new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });
    return response.StopReason.Value;
}

static async Task WaitForTitleAsync(FeedPrinter printer, int seconds)
{
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    var start = printer.TitleUpdates;
    while (DateTime.UtcNow < deadline && printer.TitleUpdates == start) await Task.Delay(250);
    printer.EndLine();
}

static void Info(string s) => Console.Error.WriteLine("· " + s);

sealed class Options
{
    readonly List<string> positional = [];
    public string? Command { get; private set; }
    public string? Pipe { get; private set; }
    public string? Service { get; private set; }
    public string Approve { get; private set; } = "ask";
    public string? Agent { get; private set; }
    public string? Cwd { get; private set; }
    public string? To { get; private set; }
    public string? Sender { get; private set; }
    public bool Follow { get; private set; }
    public int WaitTitleSeconds { get; private set; }

    public string Text => string.Join(' ', positional);
    public string TextAfter(int index) => string.Join(' ', positional.Skip(index));
    public string Positional(int index) => index < positional.Count ? positional[index] : throw new ArgumentException("missing argument");

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");
            switch (args[i])
            {
                case "--pipe": o.Pipe = Next(); break;
                case "--service": o.Service = Next(); break;
                case "--approve": o.Approve = Next(); break;
                case "--agent": o.Agent = Next(); break;
                case "--cwd": o.Cwd = Next(); break;
                case "--to": o.To = Next(); break;
                case "--sender": o.Sender = Next(); break;
                case "--wait-title": o.WaitTitleSeconds = int.Parse(Next()); break;
                case "--follow": o.Follow = true; break;
                default:
                    if (o.Command is null) o.Command = args[i];
                    else o.positional.Add(args[i]);
                    break;
            }
        }
        return o;
    }
}

sealed class Client : IAsyncDisposable
{
    Client(JsonRpcConnection rpc) => Rpc = rpc;
    public JsonRpcConnection Rpc { get; }

    /// <summary>Connects to the service, starting it in the background if it is not running.</summary>
    public static async Task<Client> ConnectAsync(Options options)
    {
        var pipe = options.Pipe ?? GorynychProtocol.DefaultPipeName;
        var stream = await TryConnectAsync(pipe, 300);
        if (stream is null)
        {
            var exe = options.Service ?? Environment.GetEnvironmentVariable("GORYNYCH_SERVICE") ?? FindService()
                ?? throw new InvalidOperationException("the service is not running and Gorynych.Service.exe was not found; pass --service");
            Console.Error.WriteLine($"· starting {exe}");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            if (options.Pipe is not null) { psi.ArgumentList.Add("--pipe"); psi.ArgumentList.Add(options.Pipe); }
            if (Environment.GetEnvironmentVariable("GORYNYCH_SERVICE_ARGS") is { Length: > 0 } extra)
                foreach (var a in extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
            Process.Start(psi);
            stream = await TryConnectAsync(pipe, 15000) ?? throw new TimeoutException("the service did not open its pipe");
        }
        return new Client(new JsonRpcConnection(stream, stream, "service"));
    }

    static async Task<NamedPipeClientStream?> TryConnectAsync(string pipe, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await stream.ConnectAsync(200);
                return stream;
            }
            catch (TimeoutException)
            {
                await stream.DisposeAsync();
                if (Environment.TickCount64 > deadline) return null;
            }
        }
    }

    static string? FindService()
    {
        var name = OperatingSystem.IsWindows() ? "Gorynych.Service.exe" : "Gorynych.Service";
        var sibling = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(sibling)) return sibling;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "src", "Gorynych.Service", "bin");
            if (!Directory.Exists(bin)) continue;
            return Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }

    public ValueTask DisposeAsync() => Rpc.DisposeAsync();
}

/// <summary>Prints the feed: streamed text inline, everything else on its own line.</summary>
sealed class FeedPrinter
{
    readonly Lock gate = new();
    bool midLine;
    string? stream;
    public int Count { get; private set; }
    public int TitleUpdates { get; private set; }
    public string? Title { get; private set; }

    public void Print(JsonNode? p)
    {
        if (p?["update"] is not JsonObject u) return;
        lock (gate)
        {
            Count++;
            var kind = (string?)u["sessionUpdate"];
            switch (kind)
            {
                case "agent_message_chunk":
                    Stream("message", "", (string?)u["content"]?["text"] ?? "");
                    break;
                case "agent_thought_chunk":
                    Stream("thought", "  (thinking) ", (string?)u["content"]?["text"] ?? "");
                    break;
                case "user_message_chunk":
                    var origin = (string?)u["_meta"]?[GorynychProtocol.MetaOrigin];
                    Line($"> {(origin is null ? "" : $"[{origin}] ")}{(string?)u["content"]?["text"]}");
                    break;
                case "tool_call":
                    Line($"  [tool] {(string?)u["title"]} ({(string?)u["kind"]}, {(string?)u["status"]}) #{Short((string?)u["toolCallId"])}");
                    break;
                case "tool_call_update":
                    var text = (string?)u["content"]?[0]?["content"]?["text"];
                    Line($"  [tool] #{Short((string?)u["toolCallId"])} {(string?)u["status"]}{(text is null ? "" : ": " + OneLine(text))}");
                    break;
                case "session_info_update":
                    if ((string?)u["title"] is { } title) { Title = title; TitleUpdates++; Line($"  [title] {title}"); }
                    break;
                case "plan":
                    Line($"  [plan] {u["entries"]?.AsArray().Count} entries");
                    break;
                case "usage_update":
                case "available_commands_update":
                    break;
                default:
                    Line($"  [{kind}]");
                    break;
            }
        }
    }

    public void Prompt(string text)
    {
        lock (gate) Line(text);
    }

    public void EndLine()
    {
        lock (gate)
        {
            if (midLine) { Console.WriteLine(); midLine = false; }
            stream = null;
        }
    }

    /// <summary>Streams text chunks inline; a new kind of stream starts on its own line.</summary>
    void Stream(string kind, string prefix, string s)
    {
        if (stream != kind)
        {
            if (midLine) Console.WriteLine();
            Console.Write(prefix);
            midLine = prefix.Length > 0;
            stream = kind;
        }
        Console.Write(s);
        if (s.Length > 0) midLine = !s.EndsWith('\n');
    }

    void Line(string s)
    {
        stream = null;
        if (midLine) { Console.WriteLine(); midLine = false; }
        Console.WriteLine(s);
    }

    static string Short(string? id) => id is null ? "?" : id.Length <= 8 ? id : id[^8..];
    static string OneLine(string s) { var l = s.ReplaceLineEndings(" "); return l.Length > 100 ? l[..99] + "…" : l; }
}

static class Approvals
{
    static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    public static async Task<JsonNode?> AnswerAsync(JsonRpcRequest request, string mode, FeedPrinter printer)
    {
        var r = request.ParamsAs<RequestPermissionRequest>();
        await ConsoleGate.WaitAsync();
        try
        {
            printer.Prompt($"  [approval] {r.ToolCall.Title}");
            for (var i = 0; i < r.Options.Count; i++)
                printer.Prompt($"     {i + 1}. {r.Options[i].Name} ({r.Options[i].Kind.Value})");
            PermissionOption? chosen = mode switch
            {
                "allow" => r.Options.FirstOrDefault(o => o.Kind.Value.StartsWith("allow", StringComparison.Ordinal)),
                "deny" => r.Options.FirstOrDefault(o => o.Kind.Value.StartsWith("reject", StringComparison.Ordinal)),
                _ => Ask(r.Options),
            };
            printer.Prompt($"     -> {chosen?.Name ?? "cancelled"}");
            RequestPermissionOutcome outcome = chosen is null
                ? new RequestPermissionOutcomeCancelled()
                : new SelectedPermissionOutcome { OptionId = chosen.OptionId };
            return AcpJson.ToNode(new RequestPermissionResponse { Outcome = outcome });
        }
        finally
        {
            ConsoleGate.Release();
        }
    }

    /// <summary>Fills an <c>elicitation/create</c> form: choices by number, text as typed.</summary>
    public static async Task<JsonNode?> FillAsync(JsonRpcRequest request, string mode, FeedPrinter printer)
    {
        var form = request.ParamsAs<CreateElicitationRequest>() as ElicitationFormMode
            ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "only form elicitation is supported");
        await ConsoleGate.WaitAsync();
        try
        {
            printer.Prompt($"  [question] {form.Message}");
            if (mode == "deny")
            {
                printer.Prompt("     -> declined");
                return AcpJson.ToNode<CreateElicitationResponse>(new CreateElicitationResponseDecline());
            }
            var content = new Dictionary<string, JsonNode?>();
            foreach (var (key, schema) in form.RequestedSchema.Properties ?? [])
            {
                var (title, choices, many) = schema switch
                {
                    StringPropertySchema s => (s.Title ?? key, s.OneOf?.Select(o => (o.Const, o.Title, o.Description)).ToList(), false),
                    MultiSelectPropertySchema m => (m.Title ?? key, (m.Items as TitledMultiSelectItems)?.AnyOf.Select(o => (o.Const, o.Title, o.Description)).ToList(), true),
                    _ => (key, null, false),
                };
                printer.Prompt($"     {title}{(many ? " (several)" : "")}");
                if (choices is null)
                {
                    var text = mode == "allow" || Console.IsInputRedirected ? "" : Read("     text: ");
                    content[key] = text;
                    continue;
                }
                for (var i = 0; i < choices.Count; i++)
                    printer.Prompt($"       {i + 1}. {choices[i].Title}{(choices[i].Description is { } d ? " — " + d : "")}");
                var picked = mode == "allow" || Console.IsInputRedirected
                    ? [0]
                    : Read("     choose: ").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => int.TryParse(t, out var n) ? n - 1 : -1).Where(n => n >= 0 && n < choices.Count).ToArray();
                if (picked.Length == 0)
                {
                    printer.Prompt("     -> cancelled");
                    return AcpJson.ToNode<CreateElicitationResponse>(new CreateElicitationResponseCancel());
                }
                content[key] = many
                    ? new JsonArray(picked.Select(n => (JsonNode)choices[n].Const).ToArray())
                    : choices[picked[0]].Const;
                printer.Prompt($"     -> {string.Join(", ", picked.Select(n => choices[n].Title))}");
            }
            return AcpJson.ToNode<CreateElicitationResponse>(new ElicitationAcceptAction { Content = content });
        }
        finally
        {
            ConsoleGate.Release();
        }
    }

    static string Read(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine() ?? "";
    }

    static PermissionOption? Ask(List<PermissionOption> options)
    {
        if (Console.IsInputRedirected) return null;
        Console.Write("     choose 1-" + options.Count + ": ");
        var line = Console.ReadLine();
        return int.TryParse(line, out var n) && n >= 1 && n <= options.Count ? options[n - 1] : null;
    }
}
