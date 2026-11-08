using System.ComponentModel;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

/// <summary>
/// The stdio entry point for external ACP clients (Zed and the like), which spawn an agent and
/// speak ACP over its stdin and stdout: <c>Gorynych.Service acp --agent claude|kimi [--pipe P]</c>.
/// It starts the service if needed, connects to its pipe and passes messages through. The one
/// change it makes is to add <c>_meta["gorynych/agent"]</c> to the client's <c>initialize</c>,
/// which scopes the connection to that agent, so the client itself stays plain ACP.
/// </summary>
public static class AcpBridge
{
    static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="serviceArgs">Arguments for the service if the bridge has to start it.</param>
    public static async Task<int> RunAsync(string agent, string pipe, IReadOnlyList<string> serviceArgs)
    {
        NamedPipeClientStream service;
        try
        {
            service = await ConnectAsync(pipe, 300) ?? await StartAndConnectAsync(pipe, serviceArgs);
        }
        catch (Exception e) when (e is TimeoutException or IOException or Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"gorynych acp: cannot reach the service on pipe {pipe}: {e.Message}");
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

    static async Task<NamedPipeClientStream?> ConnectAsync(string pipe, int timeoutMs)
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

    static async Task<NamedPipeClientStream> StartAndConnectAsync(string pipe, IReadOnlyList<string> serviceArgs)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        List<string> args = ["--pipe", pipe, .. serviceArgs];
        Console.Error.WriteLine($"gorynych acp: starting the service ({exe} {string.Join(' ', args)})");
        if (!Outside.DetachedProcess.Start(exe, args))
            Console.Error.WriteLine("gorynych acp: the client's job object forbids breaking away, so the service stays in it; " +
                "closing the client may stop the service and every session");
        return await ConnectAsync(pipe, 20000) ?? throw new TimeoutException("the service did not open its pipe");
    }

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
