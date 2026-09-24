using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiAspNetContextTests
{
    [Fact]
    public async Task Authorized_active_division_and_capability_are_contributed_server_side()
    {
        var divisionId = Guid.NewGuid();
        var httpContext = CreateHttpContext("actor-1", divisionId);
        var services = CreateServices(
            httpContext,
            "division-access",
            "project-read");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var context = await scope.ServiceProvider
            .GetRequiredService<INhAiInvocationContextFactory>()
            .CreateAsync(new NhAiInvocationContextSeed(
                "actor-1",
                "project-assistance"));

        Assert.True(context.TryGetScopeValue("division-id", out var scopeId));
        Assert.Equal(divisionId.ToString(), scopeId);
        Assert.Equal(divisionId.ToString(), Assert.Single(context.ExecutionScopes).Id);
        Assert.Contains("projects-read", context.CapabilityGrants);
        Assert.Equal(httpContext.TraceIdentifier, context.CorrelationId);
    }

    [Fact]
    public async Task Browser_division_header_is_not_scope_when_authorization_fails()
    {
        var httpContext = CreateHttpContext("actor-1", Guid.NewGuid());
        var services = CreateServices(httpContext);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var context = await scope.ServiceProvider
            .GetRequiredService<INhAiInvocationContextFactory>()
            .CreateAsync(new NhAiInvocationContextSeed(
                "actor-1",
                "project-assistance"));

        Assert.False(context.TryGetScopeValue("division-id", out _));
        Assert.Empty(context.ExecutionScopes);
        Assert.Empty(context.CapabilityGrants);
    }

    [Fact]
    public async Task Tool_gate_reauthorizes_policies_and_builds_server_owned_context()
    {
        var divisionId = Guid.NewGuid();
        var httpContext = CreateHttpContext("actor-1", divisionId);
        httpContext.Request.Headers["Idempotency-Key"] = "proposal-123.retry_1";
        var services = CreateServices(
            httpContext,
            "division-access",
            "project-read");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiToolInvocationGate>()
            .AuthorizeAsync(ToolDescriptor);

        Assert.True(result.Success);
        Assert.Equal("actor-1", result.Data.ActorId);
        Assert.Equal("tool-invocation", result.Data.Purpose);
        Assert.Equal("proposal-123.retry_1", result.Data.IdempotencyKey);
        Assert.True(result.Data.TryGetScopeValue("division-id", out var scopeId));
        Assert.Equal(divisionId.ToString(), scopeId);
    }

    [Fact]
    public async Task Tool_gate_denies_failed_policy_and_invalid_idempotency_key()
    {
        var deniedServices = CreateServices(
            CreateHttpContext("actor-1", Guid.NewGuid()),
            "division-access");
        using var deniedProvider = deniedServices.BuildServiceProvider();
        using var deniedScope = deniedProvider.CreateScope();

        var denied = await deniedScope.ServiceProvider
            .GetRequiredService<INhAiToolInvocationGate>()
            .AuthorizeAsync(ToolDescriptor);

        Assert.False(denied.Success);

        var invalidContext = CreateHttpContext("actor-1", Guid.NewGuid());
        invalidContext.Request.Headers["Idempotency-Key"] = new string('x', 257);
        var invalidServices = CreateServices(
            invalidContext,
            "division-access",
            "project-read");
        using var invalidProvider = invalidServices.BuildServiceProvider();
        using var invalidScope = invalidProvider.CreateScope();

        var invalid = await invalidScope.ServiceProvider
            .GetRequiredService<INhAiToolInvocationGate>()
            .AuthorizeAsync(ToolDescriptor);

        Assert.False(invalid.Success);
    }

    [Fact]
    public async Task Authenticated_resolver_projects_exact_issuer_subject_tenant_claims_and_scopes()
    {
        var httpContext = CreateOidcHttpContext(
            "https://identity.example",
            "subject-a",
            "tenant-a",
            "orders.read");
        var services = CreateOidcServices(httpContext);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext);

        Assert.True(result.Success);
        Assert.Equal("https://identity.example", result.Data.Issuer);
        Assert.Equal("subject-a", result.Data.Subject);
        Assert.Equal("tenant-a", result.Data.TenantId);
        Assert.NotEqual("subject-a", result.Data.ActorId);
        Assert.True(result.Data.TryGetScopeValue("tenant-id", out var tenant));
        Assert.Equal("tenant-a", tenant);
        Assert.Contains("orders-read", result.Data.CapabilityGrants);
    }

    [Fact]
    public async Task Authenticated_resolver_accepts_multiple_issuers_with_their_own_claim_mappings_and_scopes()
    {
        var issuerA = CreateMultiIssuerHttpContext(
            "iss",
            "https://identity-a.example",
            "sub",
            "shared-subject",
            "tenant_id",
            "tenant-a",
            "department_a",
            "operations");
        var issuerB = CreateMultiIssuerHttpContext(
            "issuer_b",
            "https://identity-b.example",
            "subject_b",
            "shared-subject",
            "organization_b",
            "tenant-b",
            "department_b",
            "engineering");
        var services = CreateMultiIssuerServices(issuerA);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolver = scope.ServiceProvider
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>();

        var first = await resolver.ResolveAsync(issuerA);
        var second = await resolver.ResolveAsync(issuerB);

        Assert.True(first.Success);
        Assert.Equal("https://identity-a.example", first.Data.Issuer);
        Assert.Equal("shared-subject", first.Data.Subject);
        Assert.Equal("tenant-a", first.Data.TenantId);
        Assert.True(first.Data.TryGetScopeValue("tenant-id", out var firstTenant));
        Assert.Equal("tenant-a", firstTenant);
        Assert.True(first.Data.TryGetScopeValue("department", out var firstDepartment));
        Assert.Equal("operations", firstDepartment);
        Assert.True(first.Data.TryGetScopeValue("region", out var firstRegion));
        Assert.Equal("eu", firstRegion);

        Assert.True(second.Success);
        Assert.Equal("https://identity-b.example", second.Data.Issuer);
        Assert.Equal("shared-subject", second.Data.Subject);
        Assert.Equal("tenant-b", second.Data.TenantId);
        Assert.True(second.Data.TryGetScopeValue("organization-id", out var secondTenant));
        Assert.Equal("tenant-b", secondTenant);
        Assert.True(second.Data.TryGetScopeValue("department", out var secondDepartment));
        Assert.Equal("engineering", secondDepartment);
        Assert.True(second.Data.TryGetScopeValue("region", out var secondRegion));
        Assert.Equal("eu", secondRegion);
        Assert.NotEqual(first.Data.ActorId, second.Data.ActorId);
    }

    [Fact]
    public async Task Authenticated_resolver_rejects_authority_claims_from_multiple_issuers()
    {
        var httpContext = CreateMultiIssuerHttpContext(
            "iss",
            "https://identity-a.example",
            "sub",
            "subject-a",
            "tenant_id",
            "tenant-a",
            "department_a",
            "operations");
        httpContext.User.Identities.Single().AddClaims(
        [
            new Claim("issuer_b", "https://identity-b.example"),
            new Claim("subject_b", "subject-b"),
            new Claim("organization_b", "tenant-b")
        ]);
        var services = CreateMultiIssuerServices(httpContext);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext);

        Assert.False(result.Success);
        Assert.Contains(
            result.GetResultItems(),
            item => item.Name == NhAiAspNetFailureCodes.ClaimDuplicate);
    }

    [Fact]
    public async Task Authenticated_resolver_returns_a_typed_failure_for_an_unaccepted_issuer()
    {
        var httpContext = CreateMultiIssuerHttpContext(
            "issuer_b",
            "https://unaccepted.example",
            "subject_b",
            "subject-b",
            "organization_b",
            "tenant-b",
            "department_b",
            "engineering");
        var services = CreateMultiIssuerServices(httpContext);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext);

        Assert.False(result.Success);
        Assert.Contains(
            result.GetResultItems(),
            item => item.Name == NhAiAspNetFailureCodes.IssuerNotAccepted);
    }

    [Fact]
    public async Task Tool_gate_uses_the_resolved_issuers_tenant_scope_contract()
    {
        var httpContext = CreateMultiIssuerHttpContext(
            "issuer_b",
            "https://identity-b.example",
            "subject_b",
            "subject-b",
            "organization_b",
            "tenant-b",
            "department_b",
            "engineering");
        var authorization = new TestAuthorizationService(["project-read"]);
        var services = CreateMultiIssuerServices(httpContext);
        services.AddSingleton<IAuthorizationService>(authorization);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiToolInvocationGate>()
            .AuthorizeAsync(ToolDescriptor);

        Assert.True(result.Success);
        var resource = Assert.IsType<NhAiAspNetScopeAuthorizationResource>(
            authorization.LastResource);
        Assert.Equal("organization", resource.ScopeType);
        Assert.Equal("tenant-b", resource.ScopeId);
    }

    [Theory]
    [InlineData("iss")]
    [InlineData("sub")]
    [InlineData("tenant_id")]
    public async Task Authenticated_resolver_rejects_duplicate_authority_claims(string duplicateClaimType)
    {
        var httpContext = CreateOidcHttpContext(
            "https://identity.example",
            "subject-a",
            "tenant-a",
            "orders.read");
        httpContext.User.Identities.Single().AddClaim(new Claim(
            duplicateClaimType,
            httpContext.User.FindFirstValue(duplicateClaimType)!));
        var services = CreateOidcServices(httpContext);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext);

        Assert.False(result.Success);
        Assert.Contains(
            result.GetResultItems(),
            item => item.Name == "ai-tool-claim-duplicate");
    }

    [Fact]
    public async Task Authenticated_resolver_combines_repeated_capability_claims()
    {
        var httpContext = CreateOidcHttpContext(
            "https://identity.example",
            "subject-a",
            "tenant-a",
            "profile.read");
        httpContext.User.Identities.Single().AddClaim(new Claim("scope", "orders.read"));
        var services = CreateOidcServices(httpContext);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext);

        Assert.True(result.Success);
        Assert.Contains("orders-read", result.Data.CapabilityGrants);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "sample-tenant")]
    public async Task Authenticated_resolver_supports_explicit_tenantless_and_single_tenant_modes(
        bool singleTenant,
        string? expectedTenant)
    {
        var httpContext = CreateOidcHttpContext(
            "https://identity.example",
            "subject-a",
            "ignored",
            "orders.read");
        httpContext.User.Identities.Single().RemoveClaim(httpContext.User.FindFirst("tenant_id")!);
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton<IAuthorizationService>(new TestAuthorizationService([]));
        services.AddNewHeapPlatformAIAspNet(ai =>
        {
            if (singleTenant)
            {
                ai.UseAuthenticatedClaimsForSingleTenant("https://identity.example", "sample-tenant");
            }
            else
            {
                ai.UseAuthenticatedClaimsWithoutTenant("https://identity.example");
            }
        });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext);

        Assert.True(result.Success);
        Assert.Equal(expectedTenant, result.Data.TenantId);
        Assert.Equal(singleTenant, result.Data.TryGetScopeValue("tenant-id", out _));
    }

    [Fact]
    public async Task Authenticated_resolver_rejects_an_unexpected_issuer_and_request_cancellation()
    {
        var unexpected = CreateOidcHttpContext(
            "https://attacker.example",
            "subject-a",
            "tenant-a",
            "orders.read");
        var services = CreateOidcServices(unexpected);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolver = scope.ServiceProvider
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>();

        var mismatch = await resolver.ResolveAsync(unexpected);
        Assert.False(mismatch.Success);
        Assert.Contains(
            mismatch.GetResultItems(),
            item => item.Name == NhAiAspNetFailureCodes.IssuerNotAccepted);

        var cancelled = CreateOidcHttpContext(
            "https://identity.example",
            "subject-a",
            "tenant-a",
            "orders.read");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        cancelled.RequestAborted = cancellation.Token;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync(cancelled));
    }

    [Fact]
    public void Durable_run_binding_uses_operation_identity_attempt_idempotency_and_fencing()
    {
        var services = CreateServices(CreateHttpContext("actor-1", Guid.NewGuid()));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var adapter = scope.ServiceProvider
            .GetRequiredService<INhAiBackgroundOperationRunAdapter>();
        var operation = new TestBackgroundOperationContext();

        var bound = adapter.BindInvocation(
            new NhAiInvocationContext(
                "project-agent",
                "portfolio-report",
                new Dictionary<string, string>())
            {
                ActorKind = NhAiActorKind.Agent,
                AccountableOwnerId = Guid.NewGuid().ToString()
            },
            operation,
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Equal(operation.OperationId.ToString("N"), bound.RunId);
        Assert.Equal(operation.AttemptNumber, bound.RunAttemptNumber);
        Assert.Equal(operation.IdempotencyKey, bound.IdempotencyKey);
        Assert.Equal(operation.FencingToken.ToString(), bound.FencingToken);
        Assert.NotNull(bound.Deadline);
    }

    private static ServiceCollection CreateServices(
        HttpContext httpContext,
        params string[] allowedPolicies)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton<IAuthorizationService>(
            new TestAuthorizationService(allowedPolicies));
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .AddActiveDivisionScope("division-access")
            .AddCapabilityGrant("projects-read", "project-read"));
        return services;
    }

    private static readonly NhAiToolDescriptor ToolDescriptor = new(
        "projects.search",
        1,
        "Search authorized projects.",
        typeof(string),
        typeof(string),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local,
        true,
        ["project-read"]);

    private static DefaultHttpContext CreateHttpContext(
        string actorId,
        Guid divisionId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId)],
                "test"))
        };
        context.Request.Headers[Constants.HttpHeaderKeys.ActiveDivisionId] =
            divisionId.ToString();
        return context;
    }

    private static ServiceCollection CreateOidcServices(HttpContext httpContext)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton<IAuthorizationService>(
            new TestAuthorizationService([]));
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .UseAuthenticatedClaims("https://identity.example")
            .AddScopeCapability("scope", "orders.read", "orders-read"));
        return services;
    }

    private static ServiceCollection CreateMultiIssuerServices(HttpContext httpContext)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton<IAuthorizationService>(
            new TestAuthorizationService([]));
        services.AddNewHeapPlatformAIAspNet(ai => ai
            .UseAuthenticatedClaims(
            [
                new NhAiAspNetIssuerClaimMapping("https://identity-a.example"),
                new NhAiAspNetIssuerClaimMapping(
                    "https://identity-b.example",
                    "issuer_b",
                    "subject_b",
                    "organization_b",
                    "organization-id",
                    "organization")
            ])
            .AddClaimScope("region", "region", required: true)
            .AddClaimScope(
                "https://identity-a.example",
                "department_a",
                "department",
                required: true)
            .AddClaimScope(
                "https://identity-b.example",
                "department_b",
                "department",
                required: true));
        return services;
    }

    private static DefaultHttpContext CreateOidcHttpContext(
        string issuer,
        string subject,
        string tenant,
        string scope)
    {
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("iss", issuer),
                    new Claim("sub", subject),
                    new Claim("tenant_id", tenant),
                    new Claim("scope", scope)
                ],
                "test"))
        };
    }

    private static DefaultHttpContext CreateMultiIssuerHttpContext(
        string issuerClaimType,
        string issuer,
        string subjectClaimType,
        string subject,
        string tenantClaimType,
        string tenant,
        string departmentClaimType,
        string department)
    {
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(issuerClaimType, issuer),
                    new Claim(subjectClaimType, subject),
                    new Claim(tenantClaimType, tenant),
                    new Claim(departmentClaimType, department),
                    new Claim("region", "eu")
                ],
                "test"))
        };
    }

    private sealed class TestAuthorizationService(
        IEnumerable<string> allowedPolicies) : IAuthorizationService
    {
        private readonly HashSet<string> _allowed =
            allowedPolicies.ToHashSet(StringComparer.Ordinal);

        public object? LastResource { get; private set; }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
        {
            return Task.FromResult(AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            LastResource = resource;
            return Task.FromResult(_allowed.Contains(policyName)
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed());
        }
    }

    private sealed class TestBackgroundOperationContext : INhBackgroundOperationContext
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public Guid AttemptId { get; } = Guid.NewGuid();
        public int AttemptNumber => 3;
        public long FencingToken => 17;
        public string IdempotencyKey => $"nh-operation-{OperationId:N}";
        public INhBackgroundOperationProgressContext Progress => null!;
        public INhBackgroundOperationMessageSink Messages => null!;
        public INhBackgroundOperationCheckpointStore Checkpoints => null!;
        public INhBackgroundOperationLeaseManager Leases => null!;
        public INhBackgroundOperationIdempotencyManager Idempotency => null!;
        public INhBackgroundOperationFanOutContext FanOut => null!;
        public INhBackgroundOperationSuspensionContext Suspension => null!;

        public Task ThrowIfCancellationRequestedAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetResultAsync(
            NhBackgroundOperationResultReference result,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
