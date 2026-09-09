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

    [Theory]
    [InlineData("iss")]
    [InlineData("sub")]
    [InlineData("tenant_id")]
    [InlineData("scope")]
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
            item => item.Name == "ai-tool-issuer-mismatch");

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

    private sealed class TestAuthorizationService(
        IEnumerable<string> allowedPolicies) : IAuthorizationService
    {
        private readonly HashSet<string> _allowed =
            allowedPolicies.ToHashSet(StringComparer.Ordinal);

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
