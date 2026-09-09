using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.AspNet.Common;

namespace NewHeap.Platform.AI.AspNet;

public sealed record NhAiAspNetScopeAuthorizationResource(
    string ScopeType,
    string ScopeId,
    string Purpose);

public static class NhAiAspNetServiceCollectionExtensions
{
    public static IServiceCollection AddNewHeapPlatformAIAspNet(
        this IServiceCollection services,
        Action<NhAiAspNetBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var state = services
            .Where(descriptor => descriptor.ServiceType == typeof(NhAiAspNetRegistrationState))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<NhAiAspNetRegistrationState>()
            .SingleOrDefault();
        if (state is null)
        {
            state = new NhAiAspNetRegistrationState();
            services.AddSingleton(state);
            services.AddHttpContextAccessor();
            services.AddNewHeapPlatformAI(ai =>
                ai.AddContextContributor<NhAiAspNetInvocationContextContributor>());
            services.TryAddScoped<INhAiToolInvocationGate, NhAiAspNetToolInvocationGate>();
            services.TryAddScoped<
                INhAiAuthenticatedInvocationContextResolver,
                NhAiAuthenticatedInvocationContextResolver>();
            services.TryAddScoped<INhAiBackgroundOperationRunAdapter, NhAiBackgroundOperationRunAdapter>();
            services.TryAddScoped<
                INhAiBackgroundOperationIngestionAdapter,
                NhAiBackgroundOperationIngestionAdapter>();
        }

        configure(new NhAiAspNetBuilder(services, state));
        return services;
    }
}

public sealed class NhAiAspNetBuilder
{
    private readonly IServiceCollection _services;
    private readonly NhAiAspNetRegistrationState _state;

    internal NhAiAspNetBuilder(
        IServiceCollection services,
        NhAiAspNetRegistrationState state)
    {
        _services = services;
        _state = state;
    }

    public NhAiAspNetBuilder AddActiveDivisionScope(
        string authorizationPolicy,
        string scopeKey = "division-id",
        string scopeType = "division")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        NhAiAspNetNames.ValidateSegment(scopeKey, nameof(scopeKey));
        NhAiAspNetNames.ValidateSegment(scopeType, nameof(scopeType));
        _state.SetActiveDivision(new NhAiAspNetActiveDivisionRegistration(
            authorizationPolicy,
            scopeKey,
            scopeType));
        return this;
    }

    public NhAiAspNetBuilder AddCapabilityGrant(
        string capability,
        string authorizationPolicy)
    {
        NhAiAspNetNames.ValidateSegment(capability, nameof(capability));
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        _state.AddCapability(capability, authorizationPolicy);
        return this;
    }

    public NhAiAspNetBuilder UseToolInvocationPurpose(string purpose)
    {
        NhAiAspNetNames.ValidateSegment(purpose, nameof(purpose));
        _state.SetToolInvocationPurpose(purpose);
        return this;
    }

    public NhAiAspNetBuilder UseAuthenticatedClaims(
        string expectedIssuer,
        string issuerClaimType = "iss",
        string subjectClaimType = "sub",
        string tenantClaimType = "tenant_id",
        string tenantScopeKey = "tenant-id",
        string tenantScopeType = "tenant")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIssuer);
        if (expectedIssuer.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedIssuer));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(issuerClaimType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectClaimType);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantClaimType);
        NhAiAspNetNames.ValidateSegment(tenantScopeKey, nameof(tenantScopeKey));
        NhAiAspNetNames.ValidateSegment(tenantScopeType, nameof(tenantScopeType));
        _state.SetIdentityProjection(new NhAiAspNetIdentityProjectionRegistration(
            expectedIssuer,
            issuerClaimType,
            subjectClaimType,
            tenantClaimType,
            tenantScopeKey,
            tenantScopeType));
        return this;
    }

    public NhAiAspNetBuilder AddClaimScope(
        string claimType,
        string scopeKey,
        bool required = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        NhAiAspNetNames.ValidateSegment(scopeKey, nameof(scopeKey));
        _state.AddClaimScope(new NhAiAspNetClaimScopeRegistration(
            claimType,
            scopeKey,
            required));
        return this;
    }

    public NhAiAspNetBuilder AddScopeCapability(
        string claimType,
        string claimValue,
        string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimValue);
        NhAiAspNetNames.ValidateSegment(capability, nameof(capability));
        _state.AddScopeCapability(new NhAiAspNetScopeCapabilityRegistration(
            claimType,
            claimValue,
            capability));
        return this;
    }

    public NhAiAspNetBuilder UseAuthenticatedInvocationContextResolver<TResolver>()
        where TResolver : class, INhAiAuthenticatedInvocationContextResolver
    {
        _services.Replace(ServiceDescriptor.Scoped<
            INhAiAuthenticatedInvocationContextResolver,
            TResolver>());
        return this;
    }
}

public interface INhAiAuthenticatedInvocationContextResolver
{
    ValueTask<TaskResult<NhAiInvocationContext>> ResolveAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiAspNetToolInvocationGate(
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorizationService,
    INhAiAuthenticatedInvocationContextResolver contextResolver,
    NhAiAspNetRegistrationState state) : INhAiToolInvocationGate
{
    public async ValueTask<TaskResult<NhAiInvocationContext>> AuthorizeAsync(
        NhAiToolDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return TaskResult<NhAiInvocationContext>.Failed(
                "ai-tool-authentication-required",
                "AI tool authentication is required.");
        }

        var resolved = await contextResolver.ResolveAsync(httpContext, cancellationToken);
        if (!resolved.Success)
        {
            return resolved;
        }

        object? resource = null;
        var context = resolved.Data;
        if (state.IdentityProjection is { } identityProjection
            && !string.IsNullOrWhiteSpace(context.TenantId))
        {
            resource = new NhAiAspNetScopeAuthorizationResource(
                identityProjection.TenantScopeType,
                context.TenantId,
                state.ToolInvocationPurpose);
        }
        else
        {
            var activeDivision = state.ActiveDivision;
            var divisionId = httpContext.GetActiveDivisionId();
            if (activeDivision is not null && divisionId is not null)
            {
                resource = new NhAiAspNetScopeAuthorizationResource(
                    activeDivision.ScopeType,
                    divisionId.Value.ToString(),
                    state.ToolInvocationPurpose);
            }
        }
        foreach (var policy in descriptor.AuthorizationPolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorization = await authorizationService.AuthorizeAsync(
                httpContext.User,
                resource,
                policy);
            if (!authorization.Succeeded)
            {
                return TaskResult<NhAiInvocationContext>.Failed(
                    "ai-tool-authorization-denied",
                    "AI tool authorization was denied.");
            }
        }

        return TaskResult<NhAiInvocationContext>.Succeeded(context);
    }
}

internal sealed class NhAiAuthenticatedInvocationContextResolver(
    INhAiInvocationContextFactory contextFactory,
    IAuthorizationService authorizationService,
    NhAiAspNetRegistrationState state) : INhAiAuthenticatedInvocationContextResolver
{
    public async ValueTask<TaskResult<NhAiInvocationContext>> ResolveAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            httpContext.RequestAborted);
        var token = requestCancellation.Token;
        token.ThrowIfCancellationRequested();
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Failed("ai-tool-authentication-required", "AI tool authentication is required.");
        }

        var projection = state.IdentityProjection;
        var issuer = projection is null
            ? TaskResult<string?>.Succeeded(null)
            : ResolveSingleClaim(httpContext.User, projection.IssuerClaimType, true);
        var subject = ResolveSingleClaim(
            httpContext.User,
            projection?.SubjectClaimType ?? ClaimTypes.NameIdentifier,
            true);
        var tenant = projection is null
            ? TaskResult<string?>.Succeeded(null)
            : ResolveSingleClaim(httpContext.User, projection.TenantClaimType, true);
        var identityFailure = FirstFailure(issuer, subject, tenant);
        if (identityFailure is not null)
        {
            return TaskResult<NhAiInvocationContext>.Failed(identityFailure);
        }
        if (projection is not null
            && !string.Equals(issuer.Data, projection.ExpectedIssuer, StringComparison.Ordinal))
        {
            return Failed(
                "ai-tool-issuer-mismatch",
                "The authenticated token issuer does not match the configured issuer.");
        }

        var scope = new Dictionary<string, string>(StringComparer.Ordinal);
        var tenantId = tenant.Data;
        if (projection is not null)
        {
            scope.Add(projection.TenantScopeKey, tenantId!);
        }
        foreach (var claimScope in state.ClaimScopes.OrderBy(item => item.ScopeKey, StringComparer.Ordinal))
        {
            var value = ResolveSingleClaim(httpContext.User, claimScope.ClaimType, claimScope.Required);
            if (!value.Success)
            {
                return TaskResult<NhAiInvocationContext>.Failed(value);
            }
            if (value.Data is not null)
            {
                scope.Add(claimScope.ScopeKey, value.Data);
            }
        }

        var actorId = CreateActorId(issuer.Data, subject.Data!);
        var context = await contextFactory.CreateAsync(
            new NhAiInvocationContextSeed(actorId, state.ToolInvocationPurpose, Scope: scope),
            token);
        var executionScopes = context.ExecutionScopes.ToList();
        if (projection is not null)
        {
            executionScopes.Add(new NhAiExecutionScopeEntry(
                projection.TenantScopeType,
                tenantId!));
        }
        var capabilities = context.CapabilityGrants.ToHashSet(StringComparer.Ordinal);
        foreach (var scopeCapability in state.ScopeCapabilities)
        {
            var values = httpContext.User.FindAll(scopeCapability.ClaimType).ToArray();
            if (values.Length > 1)
            {
                return Failed(
                    "ai-tool-claim-duplicate",
                    $"The authenticated claim '{scopeCapability.ClaimType}' must occur exactly once.");
            }
            if (values.Length == 1
                && values[0].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(scopeCapability.ClaimValue, StringComparer.Ordinal))
            {
                capabilities.Add(scopeCapability.Capability);
            }
        }

        object? resource = projection is null
            ? null
            : new NhAiAspNetScopeAuthorizationResource(
                projection.TenantScopeType,
                tenantId!,
                state.ToolInvocationPurpose);
        foreach (var capability in state.Capabilities.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var authorized = await authorizationService.AuthorizeAsync(
                httpContext.User,
                resource,
                capability.Value);
            if (authorized.Succeeded)
            {
                capabilities.Add(capability.Key);
            }
        }

        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(idempotencyKey) && !IsValidIdempotencyKey(idempotencyKey))
        {
            return Failed(
                "ai-tool-idempotency-key-invalid",
                "The AI tool idempotency key is invalid.");
        }

        return TaskResult<NhAiInvocationContext>.Succeeded(context with
        {
            Issuer = issuer.Data,
            Subject = subject.Data,
            TenantId = tenantId,
            CorrelationId = context.CorrelationId ?? httpContext.TraceIdentifier,
            ExecutionScopes = executionScopes,
            CapabilityGrants = capabilities,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey
        });
    }

    private static TaskResult<string?> ResolveSingleClaim(
        ClaimsPrincipal principal,
        string claimType,
        bool required)
    {
        var claims = principal.FindAll(claimType).ToArray();
        if (claims.Length > 1)
        {
            return TaskResult<string?>.Failed(
                "ai-tool-claim-duplicate",
                $"The authenticated claim '{claimType}' must occur exactly once.");
        }
        if (claims.Length == 0 || string.IsNullOrWhiteSpace(claims[0].Value))
        {
            return required
                ? TaskResult<string?>.Failed(
                    "ai-tool-claim-missing",
                    $"The authenticated claim '{claimType}' is required.")
                : TaskResult<string?>.Succeeded(null);
        }
        if (claims[0].Value.Length > 256)
        {
            return TaskResult<string?>.Failed(
                "ai-tool-claim-invalid",
                $"The authenticated claim '{claimType}' is invalid.");
        }
        return TaskResult<string?>.Succeeded(claims[0].Value);
    }

    private static TaskResult? FirstFailure(params TaskResult[] results)
    {
        return results.FirstOrDefault(result => !result.Success);
    }

    private static string CreateActorId(string? issuer, string subject)
    {
        if (issuer is null)
        {
            return subject;
        }
        var material = Encoding.UTF8.GetBytes(issuer + "\n" + subject);
        return "oidc:" + Convert.ToHexStringLower(SHA256.HashData(material));
    }

    private static bool IsValidIdempotencyKey(string value)
    {
        return value.Length <= 256
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':');
    }

    private static TaskResult<NhAiInvocationContext> Failed(string code, string message)
    {
        return TaskResult<NhAiInvocationContext>.Failed(code, message);
    }
}

internal sealed class NhAiAspNetInvocationContextContributor(
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorizationService,
    NhAiAspNetRegistrationState state) : INhAiInvocationContextContributor
{
    public int Order => 100;

    public async ValueTask ContributeAsync(
        NhAiInvocationContextBuilder builder,
        CancellationToken cancellationToken = default)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var actorId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.Equals(actorId, builder.ActorId, StringComparison.Ordinal))
        {
            return;
        }
        builder.CorrelationId ??= httpContext.TraceIdentifier;

        var activeDivision = state.ActiveDivision;
        var divisionId = httpContext.GetActiveDivisionId();
        if (activeDivision is null || divisionId is null)
        {
            return;
        }

        var resource = new NhAiAspNetScopeAuthorizationResource(
            activeDivision.ScopeType,
            divisionId.Value.ToString(),
            builder.Purpose);
        var scopeAuthorization = await authorizationService.AuthorizeAsync(
            httpContext.User,
            resource,
            activeDivision.AuthorizationPolicy);
        if (!scopeAuthorization.Succeeded)
        {
            return;
        }

        builder
            .SetScopeValue(activeDivision.ScopeKey, divisionId.Value.ToString())
            .AddExecutionScope(activeDivision.ScopeType, divisionId.Value.ToString());
        foreach (var capability in state.Capabilities.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capabilityAuthorization = await authorizationService.AuthorizeAsync(
                httpContext.User,
                resource,
                capability.Value);
            if (capabilityAuthorization.Succeeded)
            {
                builder.GrantCapability(capability.Key);
            }
        }
    }
}

internal sealed record NhAiAspNetActiveDivisionRegistration(
    string AuthorizationPolicy,
    string ScopeKey,
    string ScopeType);

internal sealed record NhAiAspNetIdentityProjectionRegistration(
    string ExpectedIssuer,
    string IssuerClaimType,
    string SubjectClaimType,
    string TenantClaimType,
    string TenantScopeKey,
    string TenantScopeType);

internal sealed record NhAiAspNetClaimScopeRegistration(
    string ClaimType,
    string ScopeKey,
    bool Required);

internal sealed record NhAiAspNetScopeCapabilityRegistration(
    string ClaimType,
    string ClaimValue,
    string Capability);

internal sealed class NhAiAspNetRegistrationState
{
    private readonly Dictionary<string, string> _capabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAiAspNetClaimScopeRegistration> _claimScopes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAiAspNetScopeCapabilityRegistration> _scopeCapabilities =
        new(StringComparer.Ordinal);

    public NhAiAspNetActiveDivisionRegistration? ActiveDivision { get; private set; }
    public NhAiAspNetIdentityProjectionRegistration? IdentityProjection { get; private set; }
    public IReadOnlyDictionary<string, string> Capabilities => _capabilities;
    public IReadOnlyCollection<NhAiAspNetClaimScopeRegistration> ClaimScopes => _claimScopes.Values;
    public IReadOnlyCollection<NhAiAspNetScopeCapabilityRegistration> ScopeCapabilities =>
        _scopeCapabilities.Values;
    public string ToolInvocationPurpose { get; private set; } = "tool-invocation";

    public void SetActiveDivision(NhAiAspNetActiveDivisionRegistration registration)
    {
        if (ActiveDivision is not null && ActiveDivision != registration)
        {
            throw new InvalidOperationException(
                "The ASP.NET AI active-division scope is already registered with a different contract.");
        }
        ActiveDivision = registration;
    }

    public void AddCapability(string capability, string authorizationPolicy)
    {
        if (_capabilities.TryGetValue(capability, out var existing)
            && !string.Equals(existing, authorizationPolicy, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AI capability '{capability}' is already registered with a different authorization policy.");
        }
        _capabilities[capability] = authorizationPolicy;
    }

    public void SetIdentityProjection(NhAiAspNetIdentityProjectionRegistration registration)
    {
        if (_claimScopes.ContainsKey(registration.TenantScopeKey))
        {
            throw new InvalidOperationException(
                $"AI tenant scope '{registration.TenantScopeKey}' conflicts with a projected claim scope.");
        }
        if (IdentityProjection is not null && IdentityProjection != registration)
        {
            throw new InvalidOperationException(
                "The ASP.NET AI authenticated claim projection is already registered with a different contract.");
        }
        IdentityProjection = registration;
    }

    public void AddClaimScope(NhAiAspNetClaimScopeRegistration registration)
    {
        if (string.Equals(
            IdentityProjection?.TenantScopeKey,
            registration.ScopeKey,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AI claim scope '{registration.ScopeKey}' conflicts with the tenant scope.");
        }
        if (_claimScopes.TryGetValue(registration.ScopeKey, out var existing)
            && existing != registration)
        {
            throw new InvalidOperationException(
                $"AI claim scope '{registration.ScopeKey}' is already registered with a different contract.");
        }
        _claimScopes[registration.ScopeKey] = registration;
    }

    public void AddScopeCapability(NhAiAspNetScopeCapabilityRegistration registration)
    {
        var key = registration.ClaimType + "\n" + registration.ClaimValue;
        if (_scopeCapabilities.TryGetValue(key, out var existing)
            && existing != registration)
        {
            throw new InvalidOperationException(
                $"AI scope value '{registration.ClaimValue}' is already registered with a different capability.");
        }
        _scopeCapabilities[key] = registration;
    }

    public void SetToolInvocationPurpose(string purpose)
    {
        if (!string.Equals(ToolInvocationPurpose, "tool-invocation", StringComparison.Ordinal)
            && !string.Equals(ToolInvocationPurpose, purpose, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The ASP.NET AI tool invocation purpose is already registered with a different value.");
        }
        ToolInvocationPurpose = purpose;
    }
}

internal static class NhAiAspNetNames
{
    public static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value[0] == '-'
            || value[^1] == '-'
            || value.Contains("--", StringComparison.Ordinal)
            || value.Any(character => character != '-'
                && (character < 'a' || character > 'z')
                && (character < '0' || character > '9')))
        {
            throw new ArgumentException(
                "AI identifiers must use lowercase dash-case.",
                parameterName);
        }
    }
}
