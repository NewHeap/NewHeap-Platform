using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-251: the identity provider's token service of the tests, which exchanges a person's NewHeap
/// token for a token of one MCP server (RFC 8693) when its client authenticates. The issued token
/// names a hash of the person's token, never the token itself; every exchange is recorded the same way.
/// </summary>
public sealed class SampleTokenExchangeService : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleTokenExchangeService(WebApplication app)
    {
        _app = app;
        TokenEndpoint = app.Urls.First() + "/token";
    }

    public string TokenEndpoint { get; }

    /// <summary>The exchanges: a hash of the person's token and the resource asked for.</summary>
    public ConcurrentQueue<(string SubjectHash, string Resource)> Exchanges { get; } = new();

    public static async Task<SampleTokenExchangeService> StartAsync(string clientId, string clientSecret)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        SampleTokenExchangeService? service = null;
        var client = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret));
        app.MapPost("/token", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            if (context.Request.Headers.Authorization.ToString() != client)
            {
                return Results.Json(new { error = "invalid_client" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            if (form["grant_type"] != "urn:ietf:params:oauth:grant-type:token-exchange"
                || string.IsNullOrEmpty(form["subject_token"]) || string.IsNullOrEmpty(form["resource"]))
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
            }
            var subjectHash = HashOf(form["subject_token"].ToString());
            service!.Exchanges.Enqueue((subjectHash, form["resource"].ToString()));
            return Results.Json(new
            {
                access_token = "exchanged:" + subjectHash,
                issued_token_type = "urn:ietf:params:oauth:token-type:access_token",
                token_type = "Bearer",
                expires_in = 300
            });
        });
        await app.StartAsync();
        service = new SampleTokenExchangeService(app);
        return service;
    }

    public static string HashOf(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
