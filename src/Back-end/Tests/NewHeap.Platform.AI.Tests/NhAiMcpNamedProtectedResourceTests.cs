using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// Named protected resources (FB57): next to the default resource at <c>/mcp</c>, the resource
/// <c>partners</c> at <c>/partners/mcp</c> has a scheme, policy, audience, authorization servers, scopes,
/// host mapping and metadata of its own. Both trust the same authorization server, so only the audience
/// keeps them apart: a token for one resource never signs in at the other, the root metadata address stays
/// the default resource's, and the tokens of both are never passed on.
/// </summary>
public sealed class NhAiMcpNamedProtectedResourceTests(NhAiMcpNamedProtectedResourceTests.Host host)
    : IClassFixture<NhAiMcpNamedProtectedResourceTests.Host>
{
    private const string Partners = "partners";
    private const string DefaultResource = "https://planning.example.org/mcp";
    private const string PartnersResource = "https://planning.example.org/partners/mcp";
    private const string DefaultMetadata = "https://planning.example.org/.well-known/oauth-protected-resource/mcp";
    private const string PartnersMetadata = "https://planning.example.org/.well-known/oauth-protected-resource/partners/mcp";

    [Fact]
    public async Task Each_resource_serves_its_own_metadata_and_the_root_address_stays_the_default_resources()
    {
        using var defaultMetadata = await host.Client.GetAsync("/.well-known/oauth-protected-resource/mcp");
        using var partnersMetadata = await host.Client.GetAsync("/.well-known/oauth-protected-resource/partners/mcp");
        using var root = await host.Client.GetAsync("/.well-known/oauth-protected-resource");

        Assert.Equal(HttpStatusCode.OK, defaultMetadata.StatusCode);
        Assert.Equal(HttpStatusCode.OK, partnersMetadata.StatusCode);
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        using var defaultDocument = JsonDocument.Parse(await defaultMetadata.Content.ReadAsStringAsync());
        using var partnersDocument = JsonDocument.Parse(await partnersMetadata.Content.ReadAsStringAsync());
        using var rootDocument = JsonDocument.Parse(await root.Content.ReadAsStringAsync());
        Assert.Equal(DefaultResource, defaultDocument.RootElement.GetProperty("resource").GetString());
        Assert.Equal(["planning.read"], Strings(defaultDocument.RootElement, "scopes_supported"));
        Assert.Equal([host.Authorization.Issuer], Strings(defaultDocument.RootElement, "authorization_servers"));
        Assert.Equal(PartnersResource, partnersDocument.RootElement.GetProperty("resource").GetString());
        Assert.Equal(["partners.read"], Strings(partnersDocument.RootElement, "scopes_supported"));
        Assert.Equal(
            [host.Authorization.Issuer, host.PartnerAuthorization.Issuer],
            Strings(partnersDocument.RootElement, "authorization_servers"));
        Assert.Equal("Partners", partnersDocument.RootElement.GetProperty("resource_name").GetString());
        Assert.Equal(DefaultResource, rootDocument.RootElement.GetProperty("resource").GetString());
    }

    [Fact]
    public async Task Each_endpoint_challenges_with_its_own_metadata_address_and_scopes()
    {
        using var defaultChallenge = await host.Client.SendAsync(Mcp("/mcp", token: null));
        using var partnersChallenge = await host.Client.SendAsync(Mcp("/partners/mcp", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, defaultChallenge.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{DefaultMetadata}\", scope=\"planning.read\"",
            Assert.Single(defaultChallenge.Headers.WwwAuthenticate).ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, partnersChallenge.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{PartnersMetadata}\", scope=\"partners.read\"",
            Assert.Single(partnersChallenge.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task A_token_for_the_default_resource_signs_in_there_and_never_at_the_named_resource()
    {
        // The same authorization server issues tokens for both resources; only the audience differs.
        var token = host.Authorization.IssueAccessToken("planner", DefaultResource, "planning.read");

        using var atDefault = await host.Client.SendAsync(Mcp("/mcp", token));
        using var atPartners = await host.Client.SendAsync(Mcp("/partners/mcp", token));

        Assert.Equal(HttpStatusCode.OK, atDefault.StatusCode);
        var signedIn = await SignedInAsync(atDefault);
        Assert.Equal("planner", signedIn.Subject);
        Assert.Null(signedIn.Resource);
        Assert.False(signedIn.CallerToken);
        Assert.Equal(HttpStatusCode.Unauthorized, atPartners.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{PartnersMetadata}\", scope=\"partners.read\", error=\"invalid_token\"",
            Assert.Single(atPartners.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task A_token_for_the_named_resource_signs_in_there_with_its_own_mapping_and_never_at_the_default_resource()
    {
        foreach (var authorization in new[] { host.Authorization, host.PartnerAuthorization })
        {
            var token = authorization.IssueAccessToken("partner-app", PartnersResource, "partners.read");

            using var atPartners = await host.Client.SendAsync(Mcp("/partners/mcp", token));
            using var atDefault = await host.Client.SendAsync(Mcp("/mcp", token));

            Assert.Equal(HttpStatusCode.OK, atPartners.StatusCode);
            var signedIn = await SignedInAsync(atPartners);
            Assert.Equal("partner-app", signedIn.Subject);
            // Only the named resource's own OnTokenValidated ran.
            Assert.Equal(Partners, signedIn.Resource);
            Assert.False(signedIn.CallerToken);
            Assert.Equal(HttpStatusCode.Unauthorized, atDefault.StatusCode);
            Assert.Equal(
                $"Bearer resource_metadata=\"{DefaultMetadata}\", scope=\"planning.read\", error=\"invalid_token\"",
                Assert.Single(atDefault.Headers.WwwAuthenticate).ToString());
        }
    }

    [Fact]
    public async Task A_token_for_either_resource_signs_in_nowhere_else_on_the_host()
    {
        var forDefault = host.Authorization.IssueAccessToken("planner", DefaultResource, "planning.read");
        var forPartners = host.Authorization.IssueAccessToken("partner-app", PartnersResource, "partners.read");

        foreach (var token in new[] { forDefault, forPartners })
        {
            using var elsewhere = await host.Client.SendAsync(Get("/api/partners-scheme", token));

            Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.StatusCode);
        }
    }

    [Fact]
    public void The_names_schemes_and_policies_of_named_resources_are_checked()
    {
        Assert.Equal("NewHeap.AI.McpProtectedResource.partners", NhAiMcpProtectedResourceDefaults.AuthenticationSchemeFor(Partners));
        Assert.Equal("NewHeap.AI.McpProtectedResource.partners", NhAiMcpProtectedResourceDefaults.AuthorizationPolicyFor(Partners));
        foreach (var name in new[] { "", " ", "Partners", "1partners", "partners app", "partners.app", "partners_app", new string('a', 65) })
        {
            Assert.ThrowsAny<ArgumentException>(() => NhAiMcpProtectedResourceDefaults.AuthenticationSchemeFor(name));
            Assert.ThrowsAny<ArgumentException>(() => Register(new ServiceCollection(), exposure => exposure.UseProtectedResource(name, Options(PartnersResource))));
        }

        NhAiMcpProtectedResourceDefaults.AuthorizationPolicyFor(new string('a', NhAiMcpProtectedResourceDefaults.MaxNameLength));
    }

    [Fact]
    public void A_second_resource_of_the_same_name_url_or_metadata_address_is_refused()
    {
        var services = new ServiceCollection();
        Register(services, exposure => exposure
            .UseProtectedResource(Options(DefaultResource))
            .UseProtectedResource(Partners, Options(PartnersResource)));

        var sameName = Assert.Throws<InvalidOperationException>(() => Register(services, exposure => exposure
            .UseProtectedResource(Partners, Options("https://planning.example.org/other/mcp"))));
        var secondDefault = Assert.Throws<InvalidOperationException>(() => Register(services, exposure => exposure
            .UseProtectedResource(Options("https://planning.example.org/other/mcp"))));
        var sameResource = Assert.Throws<InvalidOperationException>(() => Register(services, exposure => exposure
            .UseProtectedResource("suppliers", Options("https://PLANNING.example.org:443/partners/mcp/"))));
        // Another origin with the same path would share the metadata address on this host.
        var sameMetadata = Assert.Throws<InvalidOperationException>(() => Register(services, exposure => exposure
            .UseProtectedResource("suppliers", Options("https://suppliers.example.org/partners/mcp"))));
        // A resource at the root of an origin would serve the root address, which is the default resource's.
        var rootMetadata = Assert.Throws<InvalidOperationException>(() => Register(services, exposure => exposure
            .UseProtectedResource("suppliers", Options("https://suppliers.example.org"))));
        // The scheme of a protected resource is never a host scheme of another one.
        var resourceScheme = Assert.Throws<InvalidOperationException>(() => Register(new ServiceCollection(), exposure => exposure
            .UseProtectedResource(Partners, options =>
            {
                Options(PartnersResource)(options);
                options.AcceptHostSchemes.Add(NhAiMcpProtectedResourceDefaults.AuthenticationScheme);
            })));
        var namedScheme = Assert.Throws<InvalidOperationException>(() => Register(new ServiceCollection(), exposure => exposure
            .UseProtectedResource(options =>
            {
                Options(DefaultResource)(options);
                options.AcceptHostSchemes.Add(NhAiMcpProtectedResourceDefaults.AuthenticationSchemeFor(Partners));
            })));

        Assert.Contains($"named '{Partners}' already", sameName.Message, StringComparison.Ordinal);
        Assert.Contains("once per host", secondDefault.Message, StringComparison.Ordinal);
        Assert.Contains("is the resource of the protected resource 'partners' already", sameResource.Message, StringComparison.Ordinal);
        Assert.Contains("/.well-known/oauth-protected-resource/partners/mcp", sameMetadata.Message, StringComparison.Ordinal);
        Assert.Contains("the default protected resource serves its metadata already", rootMetadata.Message, StringComparison.Ordinal);
        Assert.Contains("AcceptHostSchemes", resourceScheme.Message, StringComparison.Ordinal);
        Assert.Contains("AcceptHostSchemes", namedScheme.Message, StringComparison.Ordinal);

        // A named resource may come first, and a host may have named resources only.
        Register(new ServiceCollection(), exposure => exposure
            .UseProtectedResource(Partners, Options(PartnersResource))
            .UseProtectedResource(Options(DefaultResource)));
        Register(new ServiceCollection(), exposure => exposure
            .UseProtectedResource(Partners, Options(PartnersResource))
            .UseProtectedResource("suppliers", Options("https://planning.example.org/suppliers/mcp")));
    }

    [Fact]
    public async Task The_host_does_not_start_when_a_named_resource_names_a_host_scheme_that_does_not_exist()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = await StartAsync(exposure => exposure
                .UseProtectedResource(Options(DefaultResource))
                .UseProtectedResource(Partners, options =>
                {
                    Options(PartnersResource)(options);
                    options.AcceptHostSchemes.Add("missing");
                }));
        });

        Assert.Contains("the protected resource 'partners'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'missing'", exception.Message, StringComparison.Ordinal);
    }

    private static Action<NhAiMcpProtectedResourceOptions> Options(string resource)
    {
        return options =>
        {
            options.Resource = resource;
            options.AuthorizationServers.Add("https://login.example.org");
        };
    }

    private static void Register(IServiceCollection services, Action<NhAiMcpExposureBuilder> configure)
    {
        services.AddMcpServer().WithNewHeapPlatformAITools(configure);
    }

    private static HttpRequestMessage Mcp(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<SignedIn> SignedInAsync(HttpResponseMessage response)
    {
        return JsonSerializer.Deserialize<SignedIn>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static string[] Strings(JsonElement element, string name)
    {
        return element.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    /// <summary>
    /// A loopback host whose MCP exposure is configured by <paramref name="configure"/>, with a
    /// <c>POST /mcp</c> behind the default resource's policy and a <c>POST /partners/mcp</c> behind the
    /// named resource's policy, both standing in for <c>MapMcp</c> and reporting who signed in.
    /// </summary>
    private static async Task<WebApplication> StartAsync(Action<NhAiMcpExposureBuilder> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(options => options.AddPolicy(
            "partners-scheme",
            policy => policy.AddAuthenticationSchemes(NhAiMcpProtectedResourceDefaults.AuthenticationSchemeFor(Partners)).RequireAuthenticatedUser()));
        builder.Services.AddNewHeapPlatformAIAspNet(ai => ai.UseToolInvocationPurpose("tool-invocation"));
        builder.Services.AddMcpServer().WithNewHeapPlatformAITools(configure);

        var app = builder.Build();
        app.MapPost("/mcp", ReportAsync).RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicy);
        app.MapPost("/partners/mcp", ReportAsync).RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicyFor(Partners));
        app.MapGet("/api/partners-scheme", () => "signed in").RequireAuthorization("partners-scheme");
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return app;

        static async Task<IResult> ReportAsync(HttpContext context, INhAiCallerCredentialAccessor credentials)
        {
            return Results.Json(new SignedIn(
                context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
                context.User.FindFirst("resource")?.Value,
                await credentials.GetBearerTokenAsync(context.RequestAborted) is not null));
        }
    }

    private sealed record SignedIn(string? Subject, string? Resource, bool CallerToken);

    /// <summary>
    /// The host of the class: the default resource of one authorization server and the named resource
    /// <c>partners</c> of that server and a second one, whose mapping adds the claim <c>resource</c>.
    /// </summary>
    public sealed class Host : IAsyncLifetime
    {
        private NhAiTestAuthorizationServer? _authorization;
        private NhAiTestAuthorizationServer? _partnerAuthorization;
        private WebApplication? _app;
        private HttpClient? _client;

        public NhAiTestAuthorizationServer Authorization => _authorization ?? throw new InvalidOperationException("Not started.");

        public NhAiTestAuthorizationServer PartnerAuthorization => _partnerAuthorization ?? throw new InvalidOperationException("Not started.");

        public HttpClient Client => _client ?? throw new InvalidOperationException("Not started.");

        public async Task InitializeAsync()
        {
            _authorization = await NhAiTestAuthorizationServer.StartAsync();
            _partnerAuthorization = await NhAiTestAuthorizationServer.StartAsync();
            var issuer = _authorization.Issuer;
            var partnerIssuer = _partnerAuthorization.Issuer;
            _app = await StartAsync(exposure => exposure
                .UseProtectedResource(resource =>
                {
                    resource.Resource = DefaultResource;
                    resource.AuthorizationServers.Add(issuer);
                    resource.ScopesSupported.Add("planning.read");
                })
                .UseProtectedResource(Partners, resource =>
                {
                    resource.Resource = PartnersResource;
                    resource.AuthorizationServers.Add(issuer);
                    resource.AuthorizationServers.Add(partnerIssuer);
                    resource.ScopesSupported.Add("partners.read");
                    resource.ResourceName = "Partners";
                    resource.OnTokenValidated = context =>
                    {
                        var identity = new ClaimsIdentity(context.Principal.Claims, "partner-application");
                        identity.AddClaim(new Claim("resource", Partners));
                        context.Principal = new ClaimsPrincipal(identity);
                        return Task.CompletedTask;
                    };
                }));
            _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        }

        public async Task DisposeAsync()
        {
            _client?.Dispose();
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            if (_partnerAuthorization is not null)
            {
                await _partnerAuthorization.DisposeAsync();
            }
            if (_authorization is not null)
            {
                await _authorization.DisposeAsync();
            }
        }
    }
}
