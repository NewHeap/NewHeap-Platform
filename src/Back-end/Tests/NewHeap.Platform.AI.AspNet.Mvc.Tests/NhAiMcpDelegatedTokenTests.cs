using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// Delegated MCP tokens: every JWT bearer scheme accepts a marked token only for an accepted
/// audience below its path, an issuing host accepts none, and the built-in issuer binds the token to
/// the requested marker, audience and lifetime through the NewHeap authentication service.
/// </summary>
public sealed class NhAiMcpDelegatedTokenTests
{
    private const string Issuer = "mcp-delegated-tests";
    private const string Audience = "https://planning.example.org";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("mcp-delegated-tests-signing-key-0123456789"));

    [Fact]
    public async Task A_marked_token_authenticates_only_below_the_accepted_path_of_its_audience()
    {
        await using var app = await StartAsync(tokens => tokens.Accept(Audience, "/mcp"));
        using var client = app.GetTestClient();
        var marked = Token(("nh-ai-mcp-delegated", "invocation-1"), ("nh-ai-mcp-audience", Audience));

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, "/mcp", marked));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, "/mcp/tools", marked));
        // Every other endpoint of the host refuses it, also one whose path only starts alike.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, "/api/orders", marked));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, "/mcp-admin", marked));
        // Another audience, a missing audience, two markers or an empty marker are refused.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, "/mcp", Token(("nh-ai-mcp-delegated", "invocation-1"), ("nh-ai-mcp-audience", "https://other.example.org"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, "/mcp", Token(("nh-ai-mcp-delegated", "invocation-1"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, "/mcp", Token(("nh-ai-mcp-delegated", "a"), ("nh-ai-mcp-delegated", "b"), ("nh-ai-mcp-audience", Audience))));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, "/mcp", Token(("nh-ai-mcp-delegated", " "), ("nh-ai-mcp-audience", Audience))));
        // A token without the marker is not affected.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, "/api/orders", Token()));
    }

    [Fact]
    public async Task An_issuing_host_accepts_no_marked_token_and_reports_the_restriction_as_enforced()
    {
        await using var app = await StartAsync(configure: null);
        using var client = app.GetTestClient();
        var marked = Token(("nh-ai-mcp-delegated", "invocation-1"), ("nh-ai-mcp-audience", "http://localhost"));

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, "/mcp", marked));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, "/api/orders", marked));
        Assert.True(await app.Services.IsDelegatedTokenRestrictionEnforcedAsync());
    }

    [Fact]
    public async Task A_scheme_with_events_from_a_type_leaves_the_restriction_unenforced()
    {
        await using var app = await StartAsync(configure: null, services =>
        {
            services.AddSingleton<JwtBearerEvents>();
            services.AddAuthentication().AddJwtBearer("Typed", options =>
            {
                options.EventsType = typeof(JwtBearerEvents);
                options.TokenValidationParameters = new TokenValidationParameters { IssuerSigningKey = Key };
            });
        });

        Assert.False(await app.Services.IsDelegatedTokenRestrictionEnforcedAsync());
    }

    [Fact]
    public async Task The_built_in_issuer_binds_the_token_and_issues_none_it_may_not()
    {
        var person = Guid.NewGuid();
        var authentication = Substitute.For<INhAuthenticationService>();
        // The issuer asks for a token for the server's origin, which a host that signs asymmetrically
        // sets as aud.
        authentication
            .CreateDelegatedTokenAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Claim>>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) == person
                // Only a person who may sign in gets a token.
                ? TaskResult<JwtSecurityToken>.Succeeded(new JwtSecurityToken(
                    Issuer,
                    call.ArgAt<string>(3),
                    call.ArgAt<IEnumerable<Claim>>(1),
                    DateTime.UtcNow,
                    DateTime.UtcNow.Add(call.ArgAt<TimeSpan>(2)),
                    new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)))
                : TaskResult<JwtSecurityToken>.Failed("user-locked-out", "The user may not sign in."));
        var services = new ServiceCollection();
        services.AddSingleton(authentication);
        services.AddNewHeapPlatformAIMcpDelegatedTokens();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var issuer = scope.ServiceProvider.GetRequiredService<INhAiMcpDelegatedTokenIssuer>();

        var issued = await issuer.IssueAsync(
            new NhAiMcpDelegatedTokenRequest(person.ToString(), Audience, "invocation-1", TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        // A lifetime outside (0, 5 minutes] is a programming error of the caller, not a refusal.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await issuer.IssueAsync(
            new NhAiMcpDelegatedTokenRequest(person.ToString(), Audience, "invocation-1", TimeSpan.FromMinutes(6)),
            CancellationToken.None));
        var notAPerson = await issuer.IssueAsync(
            new NhAiMcpDelegatedTokenRequest("technical-account", Audience, "invocation-1", TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        var refused = await issuer.IssueAsync(
            new NhAiMcpDelegatedTokenRequest(Guid.NewGuid().ToString(), Audience, "invocation-1", TimeSpan.FromMinutes(2)),
            CancellationToken.None);

        Assert.True(issued.Success);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(issued.Data!.BearerToken);
        Assert.Equal([Audience], token.Audiences);
        Assert.Equal("invocation-1", Assert.Single(token.Claims, claim => claim.Type == NhAiMcpDelegatedTokenDefaults.MarkerClaimType).Value);
        Assert.Equal(Audience, Assert.Single(token.Claims, claim => claim.Type == NhAiMcpDelegatedTokenDefaults.AudienceClaimType).Value);
        foreach (var failed in new[] { notAPerson, refused })
        {
            Assert.False(failed.Success);
            Assert.Equal(NhAiMcpDelegatedTokenDefaults.UnavailableCode, failed.GetResultItems()[0].Name);
        }
        // A refusal keeps the authentication service's reason after the code.
        Assert.Contains(refused.GetResultItems(), item => item.Name == "user-locked-out");
    }

    [Fact]
    public async Task The_built_in_issuer_needs_the_newheap_authentication_service()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAIMcpDelegatedTokens();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var issuer = scope.ServiceProvider.GetRequiredService<INhAiMcpDelegatedTokenIssuer>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await issuer.IssueAsync(
            new NhAiMcpDelegatedTokenRequest(Guid.NewGuid().ToString(), Audience, "invocation-1", TimeSpan.FromMinutes(2)),
            CancellationToken.None));

        Assert.Contains("INhAuthenticationService", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_first_party_host_validates_an_asymmetric_delegated_token_with_the_published_key_only()
    {
        // The issuing application signs with ES256 and publishes its public key; the receiving host
        // holds no key that could mint tokens and accepts the token for its own origin as aud.
        using var signingKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var settings = new Dictionary<string, string?>
        {
            [NhJwtSigningKeys.AlgorithmKey] = "ES256",
            [NhJwtSigningKeys.KeyIdKey] = "2026-10",
            [NhJwtSigningKeys.PrivateKeyKey] = signingKey.ExportPkcs8PrivateKeyPem()
        };
        var issuerBuilder = WebApplication.CreateBuilder();
        issuerBuilder.WebHost.UseTestServer();
        issuerBuilder.Logging.ClearProviders();
        issuerBuilder.Configuration.AddInMemoryCollection(settings);
        issuerBuilder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                // The issuing application validates its own tokens: its issuer as audience.
                options.TokenValidationParameters = new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = Issuer, ClockSkew = TimeSpan.Zero };
                NhJwtSigningKeys.Read(issuerBuilder.Configuration).ApplyTo(options.TokenValidationParameters);
            });
        issuerBuilder.Services.AddAuthorization();
        issuerBuilder.Services.AddNewHeapPlatformAIMcpDelegatedTokens();
        await using var issuer = issuerBuilder.Build();
        issuer.UseAuthentication();
        issuer.UseAuthorization();
        issuer.MapNewHeapJwks();
        issuer.MapGet("/mcp", () => Results.Ok()).RequireAuthorization();
        await issuer.StartAsync();

        var receiverBuilder = WebApplication.CreateBuilder();
        receiverBuilder.WebHost.UseTestServer();
        receiverBuilder.Logging.ClearProviders();
        receiverBuilder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.BackchannelHttpHandler = issuer.GetTestServer().CreateHandler();
                options.RequireHttpsMetadata = false;
                options.UseNewHeapJwks(new Uri("http://localhost" + NhJwtSigningKeys.DefaultJwksPath), Issuer, Issuer, Audience);
            });
        receiverBuilder.Services.AddAuthorization();
        receiverBuilder.Services.AddNewHeapPlatformAIMcpDelegatedTokens(tokens => tokens.Accept(Audience, "/mcp"));
        await using var receiver = receiverBuilder.Build();
        receiver.UseAuthentication();
        receiver.UseAuthorization();
        receiver.MapGet("/mcp/{**rest}", () => Results.Ok()).RequireAuthorization();
        receiver.MapGet("/api/orders", () => Results.Ok()).RequireAuthorization();
        await receiver.StartAsync();

        var credentials = NhJwtSigningKeys.Read(issuer.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()).SigningCredentials;
        var delegated = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer,
            Audience,
            [new Claim("sub", "person-1"), new Claim(NhAiMcpDelegatedTokenDefaults.MarkerClaimType, "invocation-1"), new Claim(NhAiMcpDelegatedTokenDefaults.AudienceClaimType, Audience)],
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(2),
            credentials));
        using var receiverClient = receiver.GetTestClient();
        using var issuerClient = issuer.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await SendAsync(receiverClient, "/mcp", delegated));
        // Below another path the restriction still refuses it, and the issuing application refuses
        // it everywhere: its aud is the server, not the issuer.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(receiverClient, "/api/orders", delegated));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(issuerClient, "/mcp", delegated));
        // A token signed with a key the application never published is refused.
        using var otherKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer,
            Audience,
            [new Claim("sub", "person-1"), new Claim(NhAiMcpDelegatedTokenDefaults.MarkerClaimType, "invocation-1"), new Claim(NhAiMcpDelegatedTokenDefaults.AudienceClaimType, Audience)],
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(2),
            new SigningCredentials(new ECDsaSecurityKey(otherKey) { KeyId = "2026-10" }, SecurityAlgorithms.EcdsaSha256)));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(receiverClient, "/mcp", forged));
    }

    [Theory]
    [InlineData("https://Planning.Example.org/mcp", "https://planning.example.org")]
    [InlineData("https://planning.example.org:443/mcp?x=1", "https://planning.example.org")]
    [InlineData("https://planning.example.org:8443/mcp", "https://planning.example.org:8443")]
    [InlineData("http://localhost:5123/planning", "http://localhost:5123")]
    public void The_audience_is_the_lowercase_origin_of_the_server(string url, string audience)
    {
        Assert.Equal(audience, NhAiMcpDelegatedTokenDefaults.AudienceOf(new Uri(url)));
    }

    [Fact]
    public void An_accepted_audience_is_an_origin_and_its_path_is_absolute()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddNewHeapPlatformAIMcpDelegatedTokens(tokens => tokens.Accept(Audience + "/mcp")));
        Assert.Throws<ArgumentException>(() => services.AddNewHeapPlatformAIMcpDelegatedTokens(tokens => tokens.Accept("planning.example.org")));
        Assert.Throws<ArgumentException>(() => services.AddNewHeapPlatformAIMcpDelegatedTokens(tokens => tokens.Accept(Audience, "mcp")));
    }

    private static async Task<WebApplication> StartAsync(
        Action<NhAiMcpDelegatedTokensBuilder>? configure,
        Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidateAudience = false,
                    IssuerSigningKey = Key,
                    ClockSkew = TimeSpan.Zero
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddNewHeapPlatformAIMcpDelegatedTokens(configure);
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/mcp/{**rest}", () => Results.Ok()).RequireAuthorization();
        app.MapGet("/mcp-admin", () => Results.Ok()).RequireAuthorization();
        app.MapGet("/api/orders", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private static string Token(params (string Type, string Value)[] claims)
    {
        var all = new List<Claim> { new("sub", "person-1") };
        all.AddRange(claims.Select(claim => new Claim(claim.Type, claim.Value)));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer,
            null,
            all,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(2),
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
