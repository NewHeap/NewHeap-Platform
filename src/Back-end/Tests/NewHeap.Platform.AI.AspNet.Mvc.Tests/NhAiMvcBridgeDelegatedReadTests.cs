using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AI.AspNet.Mvc.Tests.TestApi;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.AspNet.Mvc.Tests;

/// <summary>
/// Gateway reads in headless agent runs: a short-lived delegated read token of the host's own JWT
/// bearer scheme, restricted to <c>GET</c> calls of gateway resources with the matching invocation
/// header, and gateway tools that read and discover with the accountable person's rights.
/// </summary>
public sealed class NhAiMvcBridgeDelegatedReadTests
{
    private const string Search = "test-gateway_search-resources_v1";
    private const string Describe = "test-gateway_describe-resource_v1";
    private const string Query = "test-gateway_query_v1";
    private const string Get = "test-gateway_get_v1";

    [Fact]
    public async Task A_delegated_read_token_authenticates_only_a_gateway_GET_with_its_invocation_header()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var invocation = Guid.NewGuid();
        var token = TestReadTokens.Create("reader", [TestPolicies.OrderView], invocation, TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/orders", token, invocation));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/orders/7", token, invocation));
        // Without the header, with another invocation, as a write and outside the gateway it fails.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, "/orders", token, null));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, "/orders", token, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Post, "/orders", token, invocation));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, "/probe/echo", token, invocation));
        // An expired token fails; a token without the marker is not restricted.
        var expired = TestReadTokens.Create("reader", [TestPolicies.OrderView], invocation, TimeSpan.FromSeconds(-30));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Get, "/orders", expired, invocation));
        var plain = TestReadTokens.Create("reader", [TestPolicies.OrderView], null, TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/probe/echo", plain, null));
    }

    [Fact]
    public async Task Another_JWT_bearer_scheme_with_the_same_key_restricts_the_token_as_well()
    {
        await using var factory = CreateFactory(services => services.AddAuthentication()
            .AddJwtBearer("Partner", options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TestReadTokens.Issuer,
                    ValidateAudience = false,
                    IssuerSigningKey = TestReadTokens.Key,
                    ClockSkew = TimeSpan.Zero
                };
            }));
        // Authentication handlers are cached per service scope, so each request gets its own.
        await using var scope = factory.Services.CreateAsyncScope();
        await using var plainScope = factory.Services.CreateAsyncScope();
        var invocation = Guid.NewGuid();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers.Authorization = "Bearer " + TestReadTokens.Create("reader", [TestPolicies.OrderView], invocation, TimeSpan.FromMinutes(1));
        var plainContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = plainScope.ServiceProvider };
        plainContext.Request.Method = "POST";
        plainContext.Request.Headers.Authorization = "Bearer " + TestReadTokens.Create("reader", [TestPolicies.OrderView], null, TimeSpan.FromMinutes(1));

        var restricted = await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.AuthenticateAsync(httpContext, "Partner");
        var plain = await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.AuthenticateAsync(plainContext, "Partner");

        Assert.False(restricted.Succeeded);
        Assert.True(plain.Succeeded);
    }

    [Fact]
    public async Task A_headless_run_reads_through_the_gateway_with_the_accountable_persons_rights()
    {
        await using var factory = CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var issued = factory.Services.GetRequiredService<TestReadIssuer.Recorder>();
        var audit = factory.Services.GetRequiredService<NhAiCapturedAuditSink>();

        using (NhAiAgentInvocationScopes.Begin(AgentScope("reader")))
        {
            // Only the four gateway tools are offered, never the resource actions themselves.
            var offered = await DiscoverAsync(services);
            Assert.Equal([Describe, Get, Query, Search], offered.Where(name => name.StartsWith("test-gateway_", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(offered, name => name.StartsWith("test-api_", StringComparison.Ordinal));

            // The search shows what the person may read; the query reads it through the controller.
            Assert.Equal(["order"], (await InvokeAsync(services, Search, new { query = "" })).Data.EnumerateArray().Select(item => item.GetProperty("resource").GetString()));
            var query = await InvokeAsync(services, Query, new { resource = "order", itemsPerPage = 5 });
            Assert.True(query.Success, query.Errors);
            var get = await InvokeAsync(services, Get, new { resource = "order", id = 7 });
            Assert.True(get.Success, get.Errors);
        }

        Assert.Contains(factory.Requests, request => request.StartsWith("GET /orders?", StringComparison.Ordinal));
        Assert.Contains("GET /orders/7", factory.Requests);
        // One credential to authenticate the person for discovery, one per call, each for the person.
        Assert.All(issued.Requests, request => Assert.Equal("reader", request.PersonActorId));
        Assert.All(issued.Requests, request => Assert.Equal(NhAiDelegatedReadDefaults.DefaultLifetime, request.Lifetime));
        Assert.Contains(audit.Records, record => record.ToolId == "test-api.order.get" && record.GrantSource == NhAiGrantSource.DelegatedRead);
        Assert.Contains(audit.Records, record => record.ToolId == "test-api.order.get-by-id" && record.GrantSource == NhAiGrantSource.DelegatedRead);
        var calls = audit.Records.Where(record => record.ToolId.StartsWith("test-api.order.", StringComparison.Ordinal)).Select(record => record.InvocationId).ToArray();
        Assert.All(calls, invocationId => Assert.Contains(issued.Requests, request => request.InvocationId == invocationId));
    }

    [Fact]
    public async Task Without_the_scope_key_a_person_without_rights_or_a_credential_the_gateway_reads_nothing()
    {
        await using var factory = CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        using (NhAiAgentInvocationScopes.Begin(AgentScope("reader") with { AllowGatewayReads = false }))
        {
            Assert.DoesNotContain(await DiscoverAsync(services), name => name.StartsWith("test-gateway_", StringComparison.Ordinal));
        }
        using (NhAiAgentInvocationScopes.Begin(AgentScope("outsider")))
        {
            var query = await InvokeAsync(services, Query, new { resource = "order" });
            Assert.Equal(NhAiBridgeFailureCodes.ResourceNotFound, query.Property("code").GetString());
        }
        using (NhAiAgentInvocationScopes.Begin(AgentScope("removed")))
        {
            var query = await InvokeAsync(services, Query, new { resource = "order" });
            var search = await InvokeAsync(services, Search, new { query = "" });
            Assert.Equal(NhAiBridgeFailureCodes.Unauthenticated, query.Property("code").GetString());
            Assert.Equal(NhAiBridgeFailureCodes.Unauthenticated, search.Property("code").GetString());
        }
        Assert.DoesNotContain(factory.Requests, request => request.StartsWith("GET /orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delegated_read_result_needs_the_policies_of_the_resource_it_read()
    {
        await using var factory = CreateFactory();
        var surface = factory.Services.GetServices<INhAiDelegatedReadSurface>().Single();

        Assert.Equal([TestPolicies.OrderView], surface.ResultPolicies("test-gateway.query", """{"input":{"resource":"order"}}"""));
        Assert.Equal([TestPolicies.OrderManage], surface.ResultPolicies("test-gateway.get", """{"resource":"order-audit","id":1}"""));
        Assert.Equal([TestPolicies.OrderManage, TestPolicies.OrderView], surface.ResultPolicies("test-gateway.search-resources", "{}"));
        Assert.Null(surface.ResultPolicies("test-gateway.query", """{"input":{"resource":"unknown"}}"""));
        Assert.Null(surface.ResultPolicies("test-gateway.query", "not json"));
        Assert.Null(surface.ResultPolicies("test-api.order.post", null));
    }

    [Fact]
    public async Task Startup_fails_without_a_JWT_bearer_scheme()
    {
        await using var factory = new BridgeApiFactory(ConfigureGateway, services =>
            services.AddNewHeapPlatformAIDelegatedReads(reads => reads.UseIssuer<TestReadIssuer>()));

        var failure = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("JWT bearer scheme 'Bearer'", failure.Message);
    }

    private static NhAiAgentInvocationScope AgentScope(string person)
    {
        return new NhAiAgentInvocationScope(
            "order-planner",
            "agent:order-planner",
            1,
            new NhAiSubjectInfo(new NhAiSubjectRef("order", "7"), "order:7"),
            person,
            Guid.NewGuid().ToString(),
            "assistant")
        {
            Accountability = NhAiAgentAccountability.SubjectResponsible,
            AllowGatewayReads = true
        };
    }

    private static async Task<string[]> DiscoverAsync(IServiceProvider services)
    {
        var descriptors = await services.GetRequiredService<INhAiToolDiscoveryService>().DiscoverAsync(
            new NhAiToolDiscoveryRequest(
                new NhAiInvocationContext("agent:order-planner", "assistant", new Dictionary<string, string>()),
                NhAiToolExposure.Agent));
        return descriptors.Select(descriptor => descriptor.ExportName).ToArray();
    }

    private static async Task<BridgeToolResult> InvokeAsync(IServiceProvider services, string exportName, object input)
    {
        var catalog = services.GetRequiredService<NhAiMvcBridgeToolCatalog>();
        var function = catalog.CreateFunctions(services).Single(item => item.Name == exportName);
        var output = await function.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = JsonSerializer.SerializeToElement(input)
        });
        return new BridgeToolResult(output is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(output, function.JsonSerializerOptions));
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string path, string token, Guid? invocation)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (invocation is { } id)
        {
            request.Headers.Add(NhAiDelegatedReadDefaults.InvocationHeaderName, id.ToString("D"));
        }
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static BridgeApiFactory CreateFactory(Action<IServiceCollection>? configure = null)
    {
        return new BridgeApiFactory(ConfigureGateway, services =>
        {
            configure?.Invoke(services);
            services.AddSingleton<TestReadIssuer.Recorder>();
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = TestReadTokens.Issuer,
                        ValidateAudience = false,
                        IssuerSigningKey = TestReadTokens.Key,
                        ClockSkew = TimeSpan.Zero
                    };
                });
            services.AddNewHeapPlatformAIDelegatedReads(reads => reads.UseIssuer<TestReadIssuer>());
        });
    }

    private static void ConfigureGateway(NhAiMvcBridgeBuilder bridge)
    {
        bridge
            .UseToolSetId("test-api")
            .UseSelfBaseUrl("http://localhost/")
            .IncludeControllers("Order", "OrderAudit")
            .UseConventions<NhAiMvcBridgeGatewayTests.GatewayTestConventions>()
            .EnableGateway(gateway => gateway
                .UseGatewayToolSetId("test-gateway")
                .IncludeReadOnlyOnly());
    }
}

/// <summary>Creates the test API's JWTs with a fixed symmetric key.</summary>
internal static class TestReadTokens
{
    public const string Issuer = "delegated-read-tests";

    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("delegated-read-tests-signing-key-0123456789"));

    public static string Create(string person, IReadOnlyList<string> permissions, Guid? invocation, TimeSpan lifetime, IEnumerable<Claim>? extra = null)
    {
        var claims = new List<Claim> { new("sub", person), new(ClaimTypes.NameIdentifier, person) };
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));
        if (invocation is { } id)
        {
            claims.Add(new Claim(NhAiDelegatedReadDefaults.ClaimType, id.ToString("D")));
        }
        claims.AddRange(extra ?? []);
        var now = DateTime.UtcNow;
        var notBefore = lifetime < TimeSpan.Zero ? now.Add(lifetime).AddMinutes(-1) : now;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer,
            null,
            claims,
            notBefore,
            now.Add(lifetime),
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
    }
}

/// <summary>
/// Issues tokens for a reader, a person without rights, and refuses a removed person.
/// </summary>
internal sealed class TestReadIssuer(TestReadIssuer.Recorder recorder) : INhAiDelegatedReadCredentialIssuer
{
    public ValueTask<TaskResult<NhAiDelegatedReadCredential>> IssueAsync(
        NhAiDelegatedReadRequest request,
        CancellationToken cancellationToken)
    {
        recorder.Requests.Enqueue(request);
        if (request.PersonActorId == "removed")
        {
            return ValueTask.FromResult(TaskResult<NhAiDelegatedReadCredential>.Failed("person-removed", "The person was removed."));
        }

        string[] permissions = request.PersonActorId == "reader" ? [TestPolicies.OrderView] : [];
        var token = TestReadTokens.Create(request.PersonActorId, permissions, null, request.Lifetime, request.Claims);
        return ValueTask.FromResult(TaskResult<NhAiDelegatedReadCredential>.Succeeded(
            new NhAiDelegatedReadCredential(token, DateTimeOffset.UtcNow + request.Lifetime)));
    }

    public sealed class Recorder
    {
        public ConcurrentQueue<NhAiDelegatedReadRequest> Requests { get; } = new();
    }
}
