using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// The MCP exposure as a protected resource (<c>UseProtectedResource</c>) on a loopback host whose
/// endpoint stands in for <c>MapMcp</c>: the metadata at both well-known addresses and its contents, the
/// challenge without a token and with a token it does not accept, the audience check of the resource
/// indicator, a reverse proxy with a path prefix, the host's own sign-in next to it, a token that signs
/// in nowhere else and is never passed on, the host's mapping of the person, and no metadata or challenge
/// without the option. The tests with the MCP transport and with clients that follow the MCP authorization
/// flow are in NewHeap.Platform.AI.Chat.Tests.
/// </summary>
public sealed class NhAiMcpProtectedResourceTests(NhAiMcpProtectedResourceTests.Host host)
    : IClassFixture<NhAiMcpProtectedResourceTests.Host>
{
    private const string Resource = "https://planning.example.org/mcp";
    private const string MetadataAddress = "https://planning.example.org/.well-known/oauth-protected-resource/mcp";
    private const string Challenge = $"Bearer resource_metadata=\"{MetadataAddress}\", scope=\"planning.read planning.write\"";
    private const string InvalidTokenChallenge = Challenge + ", error=\"invalid_token\"";

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/OAuth-Protected-Resource/MCP/")]
    public async Task The_metadata_names_the_resource_its_authorization_servers_scopes_and_bearer_method_at_both_addresses(string path)
    {
        using var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var metadata = document.RootElement;
        Assert.Equal(
            ["authorization_servers", "bearer_methods_supported", "resource", "resource_name", "scopes_supported"],
            metadata.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        // The configured resource was https://Planning.Example.org:443/mcp/: published in canonical form.
        Assert.Equal(Resource, metadata.GetProperty("resource").GetString());
        Assert.Equal([host.Authorization.Issuer], Strings(metadata, "authorization_servers"));
        Assert.Equal(["planning.read", "planning.write"], Strings(metadata, "scopes_supported"));
        Assert.Equal(["header"], Strings(metadata, "bearer_methods_supported"));
        Assert.Equal("Planning", metadata.GetProperty("resource_name").GetString());
    }

    [Fact]
    public async Task The_metadata_answers_a_head_request_and_no_other_method_or_path()
    {
        using var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/.well-known/oauth-protected-resource/mcp"));
        using var post = await host.Client.PostAsync("/.well-known/oauth-protected-resource/mcp", new StringContent("{}"));
        using var other = await host.Client.GetAsync("/.well-known/oauth-protected-resource/other");

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task A_request_without_a_token_is_challenged_with_the_metadata_address_and_the_scopes()
    {
        using var response = await host.Client.SendAsync(Mcp(token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Challenge, Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task A_token_that_is_not_valid_expired_or_for_another_audience_is_challenged_with_invalid_token()
    {
        string[] tokens =
        [
            "not-a-token",
            host.Authorization.IssueAccessToken("planner", Resource, "planning.read", DateTimeOffset.UtcNow.AddMinutes(-10), TimeSpan.FromMinutes(5)),
            host.Authorization.IssueAccessToken("planner", "https://planning.example.org/other", "planning.read"),
            host.Authorization.IssueAccessToken("planner", "https://planning.example.org", "planning.read"),
            host.Authorization.IssueAccessToken("planner", "https://planning.example.net/mcp", "planning.read"),
            // An authorization server the resource does not name, with the right audience.
            host.OtherAuthorization.IssueAccessToken("planner", Resource, "planning.read")
        ];

        foreach (var token in tokens)
        {
            using var response = await host.Client.SendAsync(Mcp(token));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(InvalidTokenChallenge, Assert.Single(response.Headers.WwwAuthenticate).ToString());
        }
    }

    [Theory]
    [InlineData(Resource)]
    [InlineData("https://PLANNING.example.org:443/mcp/")]
    public async Task A_token_whose_audience_is_the_resource_signs_in_and_is_never_passed_on(string audience)
    {
        using var response = await host.Client.SendAsync(Mcp(host.Authorization.IssueAccessToken("planner", audience, "planning.read")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signedIn = await SignedInAsync(response);
        Assert.Equal("planner", signedIn.Subject);
        Assert.Equal(host.Authorization.Issuer, signedIn.Issuer);
        Assert.False(signedIn.CallerToken);
    }

    [Fact]
    public async Task A_token_of_an_authorization_server_of_the_resource_is_never_handed_to_a_host_scheme()
    {
        // The lenient host scheme accepts every token of that server, whatever its audience.
        var token = host.Authorization.IssueAccessToken("planner", "https://planning.example.org/other", "planning.read");
        using var elsewhere = await host.Client.SendAsync(Get("/api/lenient", token));

        using var mcp = await host.Client.SendAsync(Mcp(token));

        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        Assert.Equal(InvalidTokenChallenge, Assert.Single(mcp.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task The_host_sign_in_keeps_working_at_the_mcp_endpoint_and_keeps_its_token()
    {
        using var response = await host.Client.SendAsync(Mcp(Host.CreateHostToken("planner")));
        using var expired = await host.Client.SendAsync(Mcp(Host.CreateHostToken("planner", expired: true)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signedIn = await SignedInAsync(response);
        Assert.Equal("planner", signedIn.Subject);
        Assert.Equal(Host.HostIssuer, signedIn.Issuer);
        Assert.True(signedIn.CallerToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(InvalidTokenChallenge, Assert.Single(expired.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task A_token_that_the_applications_own_scheme_marks_is_never_passed_on_and_an_unmarked_one_is()
    {
        // An MCP entry that the application validates itself with a scheme of its own (FB58), next to the
        // host scheme that keeps its token.
        const string PartnerScheme = "partner-tokens";
        await using var partners = await NhAiTestAuthorizationServer.StartAsync();
        await using var app = await StartAsync(
            exposure => exposure.UseProtectedResource(resource =>
            {
                resource.Resource = Resource;
                resource.AuthorizationServers.Add(host.Authorization.Issuer);
                resource.AcceptHostSchemes.Add(PartnerScheme);
                resource.AcceptHostSchemes.Add(Host.HostScheme);
            }),
            services: services => services.AddAuthentication().AddJwtBearer(PartnerScheme, options =>
            {
                options.MapInboundClaims = false;
                options.Authority = partners.Issuer;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters.ValidAudience = "https://planning.example.org/partners";
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        NhAiCallerCredentials.MarkNotForwardable(context.HttpContext, context.SecurityToken.Issuer);
                        return Task.CompletedTask;
                    }
                };
            }));
        using var client = Client(app);

        using var marked = await client.SendAsync(Mcp(partners.IssueAccessToken("partner-app", "https://planning.example.org/partners", "partners.read")));
        using var unmarked = await client.SendAsync(Mcp(Host.CreateHostToken("planner")));

        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        var partner = await SignedInAsync(marked);
        Assert.Equal("partner-app", partner.Subject);
        Assert.False(partner.CallerToken);
        Assert.Equal(HttpStatusCode.OK, unmarked.StatusCode);
        Assert.True((await SignedInAsync(unmarked)).CallerToken);
    }

    [Fact]
    public async Task A_token_for_the_resource_signs_in_nowhere_else_on_the_host()
    {
        var token = host.Authorization.IssueAccessToken("planner", Resource, "planning.read");

        using var hostEndpoint = await host.Client.SendAsync(Get("/api/me", token));
        using var resourceScheme = await host.Client.SendAsync(Get("/api/resource-scheme", token));

        Assert.Equal(HttpStatusCode.Unauthorized, hostEndpoint.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, resourceScheme.StatusCode);
    }

    [Fact]
    public async Task The_host_maps_the_tokens_person_or_refuses_it()
    {
        using var mapped = await host.Client.SendAsync(Mcp(host.Authorization.IssueAccessToken("linked-person", Resource, "planning.read")));
        using var refused = await host.Client.SendAsync(Mcp(host.Authorization.IssueAccessToken("stranger", Resource, "planning.read")));
        using var anonymous = await host.Client.SendAsync(Mcp(host.Authorization.IssueAccessToken("anonymous", Resource, "planning.read")));

        Assert.Equal(HttpStatusCode.OK, mapped.StatusCode);
        Assert.Equal("account-7", (await SignedInAsync(mapped)).NameIdentifier);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(InvalidTokenChallenge, Assert.Single(refused.Headers.WwwAuthenticate).ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Behind_a_proxy_the_metadata_follows_the_public_path_and_the_challenge_names_the_public_address()
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        await using var app = await StartAsync(
            exposure => exposure.UseProtectedResource(resource =>
            {
                // The proxy publishes the app's /mcp as /tenant-a/planning/mcp.
                resource.Resource = "https://gateway.example.org/tenant-a/planning/mcp";
                resource.AuthorizationServers.Add(authorization.Issuer);
            }));
        using var client = Client(app);

        using var publicPath = await client.GetAsync("/.well-known/oauth-protected-resource/tenant-a/planning/mcp");
        using var appPath = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");
        using var root = await client.GetAsync("/.well-known/oauth-protected-resource");
        var challengeRequest = Mcp(token: null);
        challengeRequest.Headers.Add("X-Forwarded-Host", "attacker.example");
        using var challenge = await client.SendAsync(challengeRequest);

        Assert.Equal(HttpStatusCode.OK, publicPath.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, appPath.StatusCode);
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        using var metadata = JsonDocument.Parse(await publicPath.Content.ReadAsStringAsync());
        Assert.Equal("https://gateway.example.org/tenant-a/planning/mcp", metadata.RootElement.GetProperty("resource").GetString());
        Assert.False(metadata.RootElement.TryGetProperty("scopes_supported", out var scopes) && scopes.GetArrayLength() > 0);
        Assert.Equal(
            "Bearer resource_metadata=\"https://gateway.example.org/.well-known/oauth-protected-resource/tenant-a/planning/mcp\"",
            Assert.Single(challenge.Headers.WwwAuthenticate).ToString());
        Assert.Equal(
            "https://gateway.example.org/.well-known/oauth-protected-resource/tenant-a/planning/mcp",
            NhAiMcpProtectedResourceDefaults.MetadataAddressOf("https://Gateway.example.org/tenant-a/planning/mcp/"));
    }

    [Fact]
    public async Task Without_the_option_the_exposure_serves_no_metadata_and_keeps_the_hosts_challenge()
    {
        await using var app = await StartAsync(configure: null, policy: null);
        using var client = Client(app);

        using var metadata = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");
        using var root = await client.GetAsync("/.well-known/oauth-protected-resource");
        using var challenge = await client.SendAsync(Mcp(token: null));

        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Equal("Bearer", Assert.Single(challenge.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task The_host_does_not_start_with_a_host_scheme_that_does_not_exist()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = await StartAsync(exposure => exposure.UseProtectedResource(resource =>
            {
                resource.Resource = Resource;
                resource.AuthorizationServers.Add("https://login.example.org");
                resource.AcceptHostSchemes.Add("missing");
            }));
        });

        Assert.Contains("'missing'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "https://login.example.org", "planning.read", "Resource is required")]
    [InlineData("http://planning.example.org/mcp", "https://login.example.org", "planning.read", "plain http")]
    [InlineData("https://planning.example.org/mcp?tenant=a", "https://login.example.org", "planning.read", "no query or fragment")]
    [InlineData("https://planning.example.org/mcp#tools", "https://login.example.org", "planning.read", "no query or fragment")]
    [InlineData("https://operator@planning.example.org/mcp", "https://login.example.org", "planning.read", "without credentials")]
    [InlineData("planning/mcp", "https://login.example.org", "planning.read", "absolute https URL")]
    [InlineData("https://planning.example.org/mcp", null, "planning.read", "at least one issuer")]
    [InlineData("https://planning.example.org/mcp", "http://login.example.org", "planning.read", "plain http")]
    [InlineData("https://planning.example.org/mcp", "https://login.example.org?tenant=a", "planning.read", "no query or fragment")]
    [InlineData("https://planning.example.org/mcp", "https://login.example.org", "planning read", "scope tokens")]
    [InlineData("https://planning.example.org/mcp", "https://login.example.org", "planning\"read", "scope tokens")]
    public void Invalid_options_stop_the_registration(string? resource, string? issuer, string scope, string expected)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddMcpServer()
            .WithNewHeapPlatformAITools(exposure => exposure.UseProtectedResource(options =>
            {
                options.Resource = resource;
                if (issuer is not null)
                {
                    options.AuthorizationServers.Add(issuer);
                }
                options.ScopesSupported.Add(scope);
            })));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_clock_skew_the_host_schemes_and_a_second_registration_are_checked()
    {
        void Register(IServiceCollection services, Action<NhAiMcpProtectedResourceOptions> configure)
        {
            services.AddMcpServer().WithNewHeapPlatformAITools(exposure => exposure.UseProtectedResource(options =>
            {
                options.Resource = Resource;
                options.AuthorizationServers.Add("https://login.example.org");
                configure(options);
            }));
        }

        Assert.Throws<InvalidOperationException>(() => Register(new ServiceCollection(), options => options.ClockSkew = TimeSpan.FromMinutes(6)));
        Assert.Throws<InvalidOperationException>(() => Register(new ServiceCollection(), options => options.ClockSkew = TimeSpan.FromSeconds(-1)));
        Assert.Throws<InvalidOperationException>(() => Register(new ServiceCollection(), options =>
            options.AcceptHostSchemes.Add(NhAiMcpProtectedResourceDefaults.AuthenticationScheme)));
        var services = new ServiceCollection();
        Register(services, _ => { });
        Assert.Contains("once per host", Assert.Throws<InvalidOperationException>(() => Register(services, _ => { })).Message, StringComparison.Ordinal);
        // A loopback host may use plain http, as a development authorization server does.
        Register(new ServiceCollection(), options =>
        {
            options.Resource = "http://planning.dev.localhost:5281/mcp";
            options.AuthorizationServers.Clear();
            options.AuthorizationServers.Add("http://127.0.0.1:5123");
        });
    }

    private static HttpRequestMessage Mcp(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
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

    private static HttpClient Client(WebApplication app)
    {
        return new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    /// <summary>
    /// A loopback host with the host's own bearer scheme as its default, an MCP exposure configured by
    /// <paramref name="configure"/> and a <c>POST /mcp</c> endpoint with <paramref name="policy"/> that stands
    /// in for <c>MapMcp</c> and reports who signed in.
    /// </summary>
    private static async Task<WebApplication> StartAsync(
        Action<NhAiMcpExposureBuilder>? configure,
        string? policy = NhAiMcpProtectedResourceDefaults.AuthorizationPolicy,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(Host.HostScheme)
            .AddJwtBearer(Host.HostScheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Host.HostIssuer,
                    ValidAudience = Host.HostIssuer,
                    IssuerSigningKey = Host.HostKey,
                    ClockSkew = TimeSpan.Zero
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddNewHeapPlatformAIAspNet(ai => ai.UseToolInvocationPurpose("tool-invocation"));
        services?.Invoke(builder.Services);
        var mcp = builder.Services.AddMcpServer();
        if (configure is null)
        {
            mcp.WithNewHeapPlatformAITools();
        }
        else
        {
            mcp.WithNewHeapPlatformAITools(configure);
        }

        var app = builder.Build();
        var endpoint = app.MapPost("/mcp", async (HttpContext context, INhAiCallerCredentialAccessor credentials) => Results.Json(new SignedIn(
            context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
            context.User.FindFirst(JwtRegisteredClaimNames.Iss)?.Value,
            context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            await credentials.GetBearerTokenAsync(context.RequestAborted) is not null)));
        if (policy is null)
        {
            endpoint.RequireAuthorization();
        }
        else
        {
            endpoint.RequireAuthorization(policy);
        }
        map?.Invoke(app);
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
    }

    private sealed record SignedIn(string? Subject, string? Issuer, string? NameIdentifier, bool CallerToken);

    /// <summary>
    /// The host of the class: a protected resource of one authorization server, the host's own bearer
    /// scheme and a lenient scheme that accepts every token of that server, with a mapping of the person.
    /// </summary>
    public sealed class Host : IAsyncLifetime
    {
        public const string HostScheme = "host";
        public const string LenientScheme = "lenient";
        public const string HostIssuer = "https://host.example.test";

        private const string HostSigningCredential = "test-host-signing-key-of-the-protected-resource-host";

        private NhAiTestAuthorizationServer? _authorization;
        private NhAiTestAuthorizationServer? _otherAuthorization;
        private WebApplication? _app;
        private HttpClient? _client;

        public static SymmetricSecurityKey HostKey => new(Encoding.UTF8.GetBytes(HostSigningCredential));

        public NhAiTestAuthorizationServer Authorization => _authorization ?? throw new InvalidOperationException("Not started.");

        public NhAiTestAuthorizationServer OtherAuthorization => _otherAuthorization ?? throw new InvalidOperationException("Not started.");

        public HttpClient Client => _client ?? throw new InvalidOperationException("Not started.");

        public static string CreateHostToken(string subject, bool expired = false)
        {
            var issuedAt = expired ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                HostIssuer,
                HostIssuer,
                [new Claim(JwtRegisteredClaimNames.Sub, subject)],
                issuedAt,
                issuedAt.AddMinutes(5),
                new SigningCredentials(HostKey, SecurityAlgorithms.HmacSha256)));
        }

        public async Task InitializeAsync()
        {
            _authorization = await NhAiTestAuthorizationServer.StartAsync();
            _otherAuthorization = await NhAiTestAuthorizationServer.StartAsync();
            var issuer = _authorization.Issuer;
            _app = await StartAsync(
                exposure => exposure.UseProtectedResource(resource =>
                {
                    resource.Resource = "https://Planning.Example.org:443/mcp/";
                    resource.AuthorizationServers.Add(issuer);
                    resource.ScopesSupported.Add("planning.read");
                    resource.ScopesSupported.Add("planning.write");
                    resource.ResourceName = "Planning";
                    resource.AcceptHostSchemes.Add(HostScheme);
                    resource.AcceptHostSchemes.Add(LenientScheme);
                    resource.OnTokenValidated = MapPersonAsync;
                }),
                services: services =>
                {
                    services.AddAuthentication().AddJwtBearer(LenientScheme, options =>
                    {
                        options.MapInboundClaims = false;
                        options.Authority = issuer;
                        options.RequireHttpsMetadata = false;
                        options.TokenValidationParameters.ValidateAudience = false;
                    });
                    services.AddAuthorization(options => options.AddPolicy(
                        "resource-scheme",
                        policy => policy.AddAuthenticationSchemes(NhAiMcpProtectedResourceDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
                    services.AddAuthorization(options => options.AddPolicy(
                        "lenient",
                        policy => policy.AddAuthenticationSchemes(LenientScheme).RequireAuthenticatedUser()));
                },
                map: app =>
                {
                    app.MapGet("/api/me", (HttpContext context) => context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value).RequireAuthorization();
                    app.MapGet("/api/resource-scheme", () => "signed in").RequireAuthorization("resource-scheme");
                    app.MapGet("/api/lenient", () => "signed in").RequireAuthorization("lenient");
                });
            _client = Client(_app);
        }

        public async Task DisposeAsync()
        {
            _client?.Dispose();
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            if (_otherAuthorization is not null)
            {
                await _otherAuthorization.DisposeAsync();
            }
            if (_authorization is not null)
            {
                await _authorization.DisposeAsync();
            }
        }

        /// <summary>
        /// The host's mapping: <c>linked-person</c> is account 7, <c>stranger</c> is linked to nobody and
        /// refused, <c>anonymous</c> becomes an unauthenticated principal, everyone else keeps the token's claims.
        /// </summary>
        private static Task MapPersonAsync(NhAiMcpProtectedResourceTokenValidatedContext context)
        {
            switch (context.Subject)
            {
                case "linked-person":
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "account-7"), new Claim(JwtRegisteredClaimNames.Iss, context.Issuer)],
                        "linked-account"));
                    break;
                case "stranger":
                    context.Fail("no linked account");
                    break;
                case "anonymous":
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity());
                    break;
            }

            return Task.CompletedTask;
        }
    }
}
