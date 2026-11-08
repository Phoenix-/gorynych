using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// The loopback HTTP endpoint that Claude's per-launch http hooks post to, and that the
/// channel server polls. Every URL carries the session ID and its secret token.
/// </summary>
internal sealed class HookServer : IAsyncDisposable
{
    readonly WebApplication app;

    HookServer(WebApplication app, string baseUrl)
    {
        this.app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>
    /// <paramref name="handler"/> gets (kind, session, token, body) where kind is
    /// <c>hook</c>, <c>channel-next</c> or <c>channel-reply</c>, and returns the JSON reply,
    /// or null for 404.
    /// </summary>
    public static async Task<HookServer> StartAsync(Func<string, string, string, JsonObject?, CancellationToken, Task<JsonNode?>> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        RequestDelegate Route(string kind) => async ctx =>
        {
            var sid = ctx.Request.RouteValues["sid"] as string ?? "";
            var token = ctx.Request.RouteValues["token"] as string ?? "";
            JsonObject? body = null;
            if (HttpMethods.IsPost(ctx.Request.Method))
                try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; }
                catch (JsonException) { ctx.Response.StatusCode = 400; return; }
            JsonNode? reply;
            try { reply = await handler(kind, sid, token, body, ctx.RequestAborted); }
            catch (OperationCanceledException) { return; }
            if (reply is null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(reply.ToJsonString(Gorynych.Acp.AcpJson.TextOptions), ctx.RequestAborted);
        };

        app.MapPost("/hook/{sid}/{token}", Route("hook"));
        app.MapGet("/channel/{sid}/{token}/next", Route("channel-next"));
        app.MapPost("/channel/{sid}/{token}/reply", Route("channel-reply"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HookServer(app, address.TrimEnd('/'));
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
