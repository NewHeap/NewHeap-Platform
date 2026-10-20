using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AspNet.Common.Extensions;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-251: a first-party MCP server. The assistant's host signs its tokens with ES256, and this
/// server validates them with the public keys the host publishes (<c>UseNewHeapJwks</c>), so it holds
/// no key that could mint a NewHeap token. It accepts the host's own tokens and tokens whose
/// <c>aud</c> is this server's origin. A delegated MCP token, which an assistant sends for the person a
/// background run acts for, is accepted for this server's own origin below <c>/mcp</c> only
/// (<c>AddNewHeapPlatformAIMcpDelegatedTokens</c>). Every request records the person, the delegated
/// token's marker and audience, the token's <c>aud</c> and key id, never the token.
/// </summary>
public sealed class SampleFirstPartyMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleFirstPartyMcpServer(WebApplication app, string origin)
    {
        _app = app;
        Origin = origin;
    }

    /// <summary>The server's origin, for example <c>http://127.0.0.1:5123</c>: the delegated token's audience.</summary>
    public string Origin { get; }

    public string Url => Origin + "/mcp";

    public ConcurrentQueue<SampleFirstPartyMcpRequest> Requests { get; } = new();

    public ConcurrentQueue<string> Calls { get; } = new();

    public static async Task<SampleFirstPartyMcpServer> StartAsync(string issuer, Uri jwksUri)
    {
        // The audience names the origin, so the port is chosen before the server starts.
        var origin = "http://127.0.0.1:" + FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(origin);
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                // The test host listens on loopback over http; a real issuer publishes over https.
                options.RequireHttpsMetadata = false;
                // The host's own tokens name its issuer as audience, delegated tokens this server.
                options.UseNewHeapJwks(jwksUri, issuer, issuer, origin);
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            });
        builder.Services.AddAuthorization();
        builder.Services.AddNewHeapPlatformAIMcpDelegatedTokens(tokens => tokens.Accept(origin, "/mcp"));
        SampleFirstPartyMcpServer? server = null;
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(
            [
                McpServerTool.Create(
                    (string project) =>
                    {
                        server!.Calls.Enqueue(project);
                        return $"The team calendar has the next milestone of {project} on Friday.";
                    },
                    new McpServerToolCreateOptions
                    {
                        Name = "next_milestone",
                        Description = "Returns the next milestone of a project from the team calendar.",
                        ReadOnly = true
                    })
            ]);
        var app = builder.Build();
        server = new SampleFirstPartyMcpServer(app, origin);
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            var token = context.Request.Headers.Authorization.ToString() is { Length: > 7 } header
                && header.StartsWith("Bearer ", StringComparison.Ordinal)
                && context.User.Identity?.IsAuthenticated == true
                    ? new JwtSecurityTokenHandler().ReadJwtToken(header["Bearer ".Length..])
                    : null;
            server.Requests.Enqueue(new SampleFirstPartyMcpRequest(
                context.Request.Path.Value ?? string.Empty,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                context.User.FindFirstValue(NhAiMcpDelegatedTokenDefaults.MarkerClaimType),
                context.User.FindFirstValue(NhAiMcpDelegatedTokenDefaults.AudienceClaimType))
            {
                TokenAudience = token?.Audiences.SingleOrDefault(),
                KeyId = token?.Header.Kid
            });
            await next(context);
        });
        app.UseAuthorization();
        app.MapMcp("/mcp").RequireAuthorization();
        await app.StartAsync();
        return server;
    }

    /// <summary>
    /// A NewHeap access token of a signed-in person, as the host signs it for a browser that then
    /// calls the assistant.
    /// </summary>
    public static string CreateAccessToken(string issuer, SigningCredentials credentials, string personId)
    {
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer,
            issuer,
            [new Claim(ClaimTypes.NameIdentifier, personId)],
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(10),
            credentials));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// One request to <see cref="SampleFirstPartyMcpServer"/>: the person it authenticated, and for a
/// delegated token its marker and audience; null when the request was not authenticated.
/// </summary>
public sealed record SampleFirstPartyMcpRequest(string Path, string? PersonId, string? Marker, string? Audience)
{
    /// <summary>The validated token's <c>aud</c>.</summary>
    public string? TokenAudience { get; init; }

    /// <summary>The key id (<c>kid</c>) the token was signed with.</summary>
    public string? KeyId { get; init; }
}
