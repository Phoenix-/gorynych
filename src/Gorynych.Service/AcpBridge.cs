using System.ComponentModel;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Outside;

namespace Gorynych.Service;

/// <summary>
/// The stdio entry point for external ACP clients (Zed and the like), which spawn an agent and
/// speak ACP over its stdin and stdout: <c>Gorynych.Service acp --agent claude|kimi|codex|dsh [--pipe P]</c>.
/// It starts the service if needed, connects to its pipe and passes messages through. The one
/// change it makes is to add <c>_meta["gorynych/agent"]</c> to the client's <c>initialize</c>,
/// which scopes the connection to that agent, so the client itself stays plain ACP.
/// </summary>
public static class AcpBridge
{
    static readonly JsonSerializerOptions Wire = Acp.AcpJson.TextOptions;

    /// <param name="serviceArgs">Arguments for the service if the bridge has to start it.</param>
    public static async Task<int> RunAsync(string agent, string pipe, IReadOnlyList<string> serviceArgs)
    {
        NamedPipeClientStream service;
        try
        {
            service = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromMilliseconds(300)) ?? await StartAndConnectAsync(pipe, serviceArgs);
        }
        catch (NotStartedException e)
        {
            Console.Error.WriteLine($"gorynych acp: {e.Message}");
            return 1;
        }
        catch (Exception e) when (e is TimeoutException or IOException or Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"gorynych acp: cannot reach the service on pipe {pipe}: {e.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine($"gorynych acp: cannot reach the service on pipe {pipe}: {e.Message}{Hint(e)}");
            return 1;
        }
        await using (service)
        {
            var up = Task.Run(() => ClientToServiceAsync(Console.OpenStandardInput(), service, agent));
            var down = Task.Run(() => service.CopyToAsync(new FlushingStream(Console.OpenStandardOutput())));
            await Task.WhenAny(up, down);
        }
        return 0;
    }

    /// <summary>What a refused connection to the pipe usually means (docs/service-protocol.md, Endpoint).</summary>
    /// <summary>The elevation hint for a pipe whose ACL refused this process; none for a pipe someone else owns.</summary>
    internal static string Hint(Exception e) => e is UnauthorizedAccessException and not ServicePipe.ForeignPipeException ? " " + ElevationHint : "";

    internal const string ElevationHint =
        "A service started elevated (from an administrator terminal) serves only elevated clients; a plain service " +
        "serves the user's plain and elevated clients alike.";

    /// <summary>
    /// <c>Gorynych.Service shutdown [--pipe P]</c>: asks the running service to exit, as a client's
    /// <c>_gorynych/shutdown</c> does, and waits up to <paramref name="waitSeconds"/> for its process
    /// to end. 0 when it stopped, 1 when no service runs on the pipe or it could not be asked, 2
    /// when it was asked but still runs.
    /// </summary>
    public static async Task<int> ShutdownAsync(string pipe, int waitSeconds = 30)
    {
        NamedPipeClientStream? stream;
        try
        {
            stream = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"gorynych shutdown: cannot reach the service on pipe {pipe}: {e.Message}{Hint(e)}");
            return 1;
        }
        if (stream is null)
        {
            Console.Error.WriteLine($"gorynych shutdown: no service is running on pipe {pipe}");
            return 1;
        }
        int processId;
        await using (var rpc = new JsonRpcConnection(stream, stream, "shutdown"))
        {
            rpc.Start();
            try
            {
                await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
                {
                    ProtocolVersion = AcpMethods.ProtocolVersion,
                    ClientInfo = new Implementation { Name = "gorynych-shutdown", Version = AgentHost.Version },
                });
                processId = (int?)(await rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject()))?["processId"] ?? 0;
                await rpc.SendRequestAsync(GorynychProtocol.MethodShutdown, new JsonObject());
            }
            catch (Exception e) when (e is IOException or JsonRpcException)
            {
                Console.Error.WriteLine($"gorynych shutdown: the service on pipe {pipe} did not take the request: {e.Message}");
                return 1;
            }
        }
        Console.Error.WriteLine($"gorynych shutdown: asked the service (process {processId}) to exit; turns still running are cancelled");
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(waitSeconds));
        }
        catch (ArgumentException) { } // already gone
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"gorynych shutdown: the service (process {processId}) still runs after {waitSeconds} s");
            return 2;
        }
        Console.Error.WriteLine("gorynych shutdown: the service has stopped");
        return 0;
    }

    /// <summary>Passes the client's lines to the service, scoping its <c>initialize</c> to <paramref name="agent"/>.</summary>
    static async Task ClientToServiceAsync(Stream input, Stream service, string agent)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.Length == 0) continue;
            var forwarded = line.Contains("\"initialize\"", StringComparison.Ordinal) ? ScopeInitialize(line, agent) : line;
            await service.WriteAsync(Encoding.UTF8.GetBytes(forwarded + "\n"));
            await service.FlushAsync();
        }
    }

    /// <summary>Adds <c>_meta["gorynych/agent"]</c> to an <c>initialize</c> request unless the client set one.</summary>
    public static string ScopeInitialize(string line, string agent)
    {
        JsonObject? message;
        try { message = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return line; }
        if (message is null || (string?)message["method"] != "initialize" || message["id"] is null) return line;
        var @params = message["params"] as JsonObject ?? [];
        var meta = @params["_meta"] as JsonObject ?? [];
        if (meta[GorynychProtocol.MetaAgent] is null) meta[GorynychProtocol.MetaAgent] = agent;
        @params["_meta"] = meta;
        message["params"] = @params;
        return message.ToJsonString(Wire);
    }

    /// <summary>
    /// Starts the service, as <see cref="ServiceLaunch.Decide"/> says, and waits for its pipe: with
    /// the logon task when there is one, else as a detached process, but never from an ssh login.
    /// </summary>
    static async Task<NamedPipeClientStream> StartAndConnectAsync(string pipe, IReadOnlyList<string> serviceArgs)
    {
        const int wait = 20;
        switch (ServiceLaunch.Decide(LogonTask.Exists(pipe), ServiceLaunch.IsRemoteLogon()))
        {
            case ServiceLaunch.Way.Refuse:
                throw new NotStartedException(ServiceLaunch.Refusal(pipe, pipe == GorynychProtocol.DefaultPipeName));
            case ServiceLaunch.Way.RunTask:
                Console.Error.WriteLine($"gorynych acp: starting the service with the logon task {LogonTask.Name(pipe)}" +
                    (serviceArgs.Count > 0 ? "; the task's options apply, not these" : ""));
                if (LogonTask.Run(pipe) is { } failure)
                    throw new InvalidOperationException($"the logon task {LogonTask.Name(pipe)} did not start: {failure}");
                return await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(wait)) ?? throw new TimeoutException(ServiceLaunch.TaskTimeout(pipe, wait));
            default:
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
                List<string> args = ["--pipe", pipe, .. serviceArgs];
                Console.Error.WriteLine($"gorynych acp: starting the service ({exe} {string.Join(' ', args)})");
                if (!DetachedProcess.Start(exe, args))
                    Console.Error.WriteLine("gorynych acp: the client's job object forbids breaking away, so the service stays in it; " +
                        "closing the client may stop the service and every session");
                return await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(wait)) ?? throw new TimeoutException("the service did not open its pipe");
        }
    }

    /// <summary>The bridge started no service, on purpose; the message says why.</summary>
    sealed class NotStartedException(string message) : Exception(message);

    /// <summary>Writes through to stdout and flushes every write, so each message leaves at once.</summary>
    sealed class FlushingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); inner.Flush(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            await inner.FlushAsync(ct);
        }
    }
}
