using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AgentHostProtocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gorynych.Service.Ahp;

/// <summary>
/// The AHP face's endpoint (docs/ahp-face.md, "Endpoint"): a WebSocket at <c>ws://127.0.0.1:&lt;port&gt;/ahp</c> on Kestrel,
/// loopback only, one JSON-RPC message per text frame. It takes the upgrade only with the token (<c>?tkn=</c> or
/// <c>Authorization: Bearer</c>, compared in constant time; 401 otherwise), and never from a web page (an
/// <c>Origin</c> of <c>http:</c> or <c>https:</c>; 403), since a browser lets any page open a WebSocket to localhost.
/// </summary>
internal sealed class AhpEndpoint : IAsyncDisposable
{
    /// <summary>The port <c>--ahp on</c> means.</summary>
    public const int DefaultPort = 8766;

    /// <summary>The token file in the data folder.</summary>
    public const string TokenFile = "ahp.token";

    readonly WebApplication app;

    AhpEndpoint(WebApplication app, int port)
    {
        this.app = app;
        Port = port;
    }

    public int Port { get; }

    /// <summary>The face's token, made at its first start and kept in the data folder, the user's alone.</summary>
    public static string EnsureToken(string dataDirectory) => Hosts.HostNames.EnsureTokenFile(Path.Combine(dataDirectory, TokenFile));

    /// <summary>Listens on <paramref name="port"/> (0: any free one). Throws when the port is taken.</summary>
    public static async Task<AhpEndpoint> StartAsync(AhpFace face, int port, string token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async ctx =>
        {
            if (ctx.Request.Path != "/ahp")
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var given = ctx.Request.Query["tkn"].ToString();
            var authorization = ctx.Request.Headers.Authorization.ToString();
            if (given.Length == 0 && authorization.StartsWith("Bearer ", StringComparison.Ordinal)) given = authorization["Bearer ".Length..].Trim();
            if (!Hosts.HostNames.SameToken(given, token))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await face.ServeAsync(new WebSocketServerTransport(socket), ctx.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new AhpEndpoint(app, new Uri(address).Port);
    }

    public async ValueTask DisposeAsync()
    {
        try { await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await app.DisposeAsync();
    }
}

/// <summary>AHP's <see cref="ITransport"/> over an accepted WebSocket: a message is one frame, text as AHP sends it.</summary>
internal sealed class WebSocketServerTransport(WebSocket socket) : ITransport
{
    const int MaxMessage = 32 * 1024 * 1024;

    public async ValueTask SendAsync(TransportMessage message, CancellationToken cancellationToken)
    {
        var text = message.Frame == TransportFrame.Text;
        var bytes = text ? Encoding.UTF8.GetBytes(message.Text ?? "") : message.Binary.ToArray();
        await socket.SendAsync(bytes, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }

    public async ValueTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.GetMemory(16 * 1024), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) throw new TransportClosedException("the client closed the connection");
            buffer.Advance(result.Count);
            if (buffer.WrittenCount > MaxMessage) throw new TransportClosedException("a message larger than this host takes");
            if (!result.EndOfMessage) continue;
            return result.MessageType == WebSocketMessageType.Text
                ? TransportMessage.FromText(Encoding.UTF8.GetString(buffer.WrittenSpan))
                : TransportMessage.FromBinary(buffer.WrittenMemory.ToArray());
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken); }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>The socket is Kestrel's, disposed when its request ends.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
