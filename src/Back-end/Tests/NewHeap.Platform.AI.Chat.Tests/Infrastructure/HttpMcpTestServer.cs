using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// An official MCP SDK server over real HTTP on loopback, for the assistant's own HTTP client:
/// Streamable HTTP at <c>/planning</c>, or with <c>legacySse</c> also the deprecated HTTP+SSE
/// transport at <c>/planning/sse</c> and <c>/planning/message</c>. It records every request's path and
/// <c>Authorization</c> header.
/// </summary>
internal sealed class HttpMcpTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private HttpMcpTestServer(WebApplication app, string origin, ConcurrentQueue<HttpMcpTestRequest> requests)
    {
        _app = app;
        Origin = origin;
        Requests = requests;
    }

    /// <summary>The origin with the host name <c>localhost</c>, for example <c>http://localhost:5123</c>.</summary>
    public string Origin { get; }

    public string StreamableUrl => Origin + "/planning";

    public string LegacySseUrl => Origin + "/planning/sse";

    public ConcurrentQueue<HttpMcpTestRequest> Requests { get; }

    public static async Task<HttpMcpTestServer> StartAsync(
        Func<IEnumerable<McpServerTool>> tools,
        bool legacySse = false,
        Func<HttpContext, Func<Task>, Task>? guard = null)
    {
        var requests = new ConcurrentQueue<HttpMcpTestRequest>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
                if (legacySse)
                {
                    // The HTTP+SSE transport keeps a session per client.
                    options.Stateless = false;
                    options.EnableLegacySse = true;
                }
            })
            .WithTools(tools());
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            requests.Enqueue(new HttpMcpTestRequest(
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                context.Request.Headers.Authorization.ToString()));
            await next(context);
        });
        if (guard is not null)
        {
            // For example an NhAiTestProtectedResource that requires OAuth tokens.
            app.Use((context, next) => guard(context, () => next(context)));
        }
        app.MapMcp("/planning");
        await app.StartAsync();
        var origin = app.Urls.First().Replace("127.0.0.1", "localhost", StringComparison.Ordinal).TrimEnd('/');
        return new HttpMcpTestServer(app, origin, requests);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed record HttpMcpTestRequest(string Method, string Path, string Authorization);
