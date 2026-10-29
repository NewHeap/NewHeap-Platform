using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// One loopback-name rule (FB56): <c>localhost</c>, a <c>.localhost</c> name (RFC 6761) and a loopback IP
/// literal are loopback names for the inbound protected resource, the assistant's OAuth options and the
/// assistant's outbound guard alike, and no other name is. Plain http to such a name stays behind
/// <see cref="NhAssistantMcpOptions.AllowLoopback"/> and connects only when every address the name
/// resolves to is a loopback address.
/// </summary>
public sealed class AssistantMcpLoopbackNameTests
{
    private const string Testing = "Testing";
    private const string PublicAddress = "203.0.113.7";

    [Theory]
    [InlineData("http://localhost:5281", true)]
    [InlineData("http://LocalHost:5281", true)]
    [InlineData("http://auth.dev.localhost:5281", true)]
    [InlineData("http://Planning.Dev.LocalHost:5281", true)]
    [InlineData("http://127.0.0.1:5281", true)]
    [InlineData("http://127.10.20.30:5281", true)]
    [InlineData("http://[::1]:5281", true)]
    [InlineData("http://auth.dev.localhost.:5281", true)]
    [InlineData("http://mylocalhost:5281", false)]
    [InlineData("http://localhost.example.org:5281", false)]
    [InlineData("http://auth.localhost.example.org:5281", false)]
    [InlineData("http://10.0.0.1:5281", false)]
    [InlineData("http://planning.example.org", false)]
    public void The_inbound_resource_the_oauth_options_and_the_outbound_guard_share_one_loopback_name_rule(string origin, bool loopback)
    {
        var guard = Guard(Testing, allowLoopback: true).HostGuard;

        var inbound = Record.Exception(() => new ServiceCollection()
            .AddMcpServer()
            .WithNewHeapPlatformAITools(exposure => exposure.UseProtectedResource(resource =>
            {
                resource.Resource = origin + "/mcp";
                resource.AuthorizationServers.Add(origin);
            })));
        var options = Record.Exception(() => new NhAssistantMcpOAuthOptions
        {
            RedirectUri = origin + "/api/assistant/mcp/oauth/callback",
            ReturnOrigins = [origin]
        }.Validate(allowLoopbackHttp: true));
        var server = guard.CheckUri(new Uri(origin + "/mcp"));
        var oauth = guard.CheckOAuthUri(new Uri(origin + "/token"));

        Assert.Equal(loopback, inbound is null);
        Assert.Equal(loopback, options is null);
        Assert.Equal(loopback, server is null);
        Assert.Equal(loopback, oauth is null);
        if (!loopback)
        {
            Assert.IsType<InvalidOperationException>(inbound);
            Assert.IsType<InvalidOperationException>(options);
            Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, server);
            Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, oauth);
        }
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData(Testing, false)]
    public void Where_loopback_is_not_allowed_a_localhost_name_is_blocked_over_http_and_https(string environmentName, bool allowLoopback)
    {
        var guard = Guard(environmentName, allowLoopback).HostGuard;

        foreach (var url in new[] { "http://auth.dev.localhost:5281/token", "https://auth.dev.localhost/token", "https://localhost/token" })
        {
            Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, guard.CheckUri(new Uri(url)));
            Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, guard.CheckOAuthUri(new Uri(url)));
        }
    }

    [Theory]
    [InlineData("http", "auth.dev.localhost", "127.0.0.1", true)]
    [InlineData("http", "auth.dev.localhost", "127.0.0.1,::1", true)]
    [InlineData("http", "auth.dev.localhost", "::ffff:127.0.0.1", true)]
    [InlineData("http", "auth.dev.localhost", PublicAddress, false)]
    [InlineData("http", "auth.dev.localhost", "127.0.0.1," + PublicAddress, false)]
    [InlineData("http", "localhost", PublicAddress, false)]
    [InlineData("https", "auth.dev.localhost", PublicAddress, true)]
    [InlineData("http", "planning.example.org", PublicAddress, true)]
    public async Task Plain_http_to_a_loopback_name_connects_only_when_every_address_it_resolves_to_is_loopback(
        string scheme,
        string host,
        string resolvesTo,
        bool connects)
    {
        var guard = Guard(Testing, allowLoopback: true, resolvesTo.Split(',')).HostGuard;
        var cancellationToken = CancellationToken.None;

        var server = await guard.ResolveAllowedAsync(host, scheme, cancellationToken);
        var oauth = await guard.ResolveAllowedForOAuthAsync(host, scheme, cancellationToken);

        Assert.Equal(connects, server.Count > 0);
        Assert.Equal(connects, oauth.Count > 0);
    }

    [Fact]
    public async Task An_authorization_server_and_an_mcp_endpoint_on_a_localhost_name_are_reached_over_http_only_on_loopback()
    {
        var cancellationToken = CancellationToken.None;
        var requests = 0;
        await using var server = await LocalhostTestServer.StartPlainAsync(app =>
        {
            app.MapGet("/.well-known/oauth-authorization-server", () =>
            {
                Interlocked.Increment(ref requests);
                return Results.Json(new { issuer = "http://auth.dev.localhost" });
            });
            app.MapPost("/mcp", (HttpContext context) =>
            {
                Interlocked.Increment(ref requests);
                context.Response.Headers.WWWAuthenticate = "Bearer resource_metadata=\"http://planning.dev.localhost/.well-known/oauth-protected-resource/mcp\"";
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            });
        });
        var port = new Uri(LocalhostTestServer.Origin(server)).Port;
        var metadata = new Uri($"http://auth.dev.localhost:{port}/.well-known/oauth-authorization-server");
        var endpoint = new Uri($"http://planning.dev.localhost:{port}/mcp");

        var onLoopback = Guard(Testing, allowLoopback: true, "127.0.0.1");
        using (var http = new NhAssistantMcpOAuthHttp(onLoopback.HostGuard, onLoopback.Options, NullLogger<NhAssistantMcpOAuthHttp>.Instance))
        {
            var read = await http.GetAsync(metadata, cancellationToken);
            var probed = await http.ProbeAsync(endpoint, cancellationToken);

            Assert.True(read.Success, string.Join(", ", read.AllErrorMessages));
            Assert.Equal(HttpStatusCode.OK, read.Data!.Status);
            Assert.Equal("http://auth.dev.localhost", read.Data.String("issuer"));
            Assert.True(probed.Success, string.Join(", ", probed.AllErrorMessages));
            Assert.Equal(HttpStatusCode.Unauthorized, probed.Data!.Status);
        }
        Assert.Equal(2, requests);

        // The same names, sent elsewhere by DNS, are never contacted over http.
        var elsewhere = Guard(Testing, allowLoopback: true, PublicAddress);
        using (var http = new NhAssistantMcpOAuthHttp(elsewhere.HostGuard, elsewhere.Options, NullLogger<NhAssistantMcpOAuthHttp>.Instance))
        {
            var read = await http.GetAsync(metadata, cancellationToken);
            var probed = await http.ProbeAsync(endpoint, cancellationToken);

            Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, Code(read));
            Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, Code(probed));
        }
        Assert.Equal(2, requests);
    }

    /// <summary>
    /// The guard of a host of <paramref name="environmentName"/> with <c>RequireHttps</c> on, whose host
    /// names resolve to <paramref name="resolvesTo"/>.
    /// </summary>
    private static (NhAssistantMcpHostGuard HostGuard, IOptionsMonitor<NhAssistantOptions> Options) Guard(
        string environmentName,
        bool allowLoopback,
        params string[] resolvesTo)
    {
        var services = new ServiceCollection();
        services.AddOptions<NhAssistantOptions>().Configure(options =>
        {
            options.Mcp.RequireHttps = true;
            options.Mcp.AllowLoopback = allowLoopback;
        });
        var monitor = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<NhAssistantOptions>>();
        var addresses = resolvesTo.Select(IPAddress.Parse).ToArray();
        var guard = new NhAssistantMcpHostGuard(
            monitor,
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environmentName })
        {
            Resolve = (_, _) => Task.FromResult(addresses)
        };
        return (guard, monitor);
    }

    private static string? Code(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }
}
