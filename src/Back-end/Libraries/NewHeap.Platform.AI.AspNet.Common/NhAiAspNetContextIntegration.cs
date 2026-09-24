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

/// <summary>
/// Maps the authority claims of one accepted issuer into a NewHeap AI invocation context.
/// </summary>
public sealed record NhAiAspNetIssuerClaimMapping(
    string ExpectedIssuer,
    string IssuerClaimType = "iss",
    string SubjectClaimType = "sub",
    string TenantClaimType = "tenant_id",
    string TenantScopeKey = "tenant-id",
    string TenantScopeType = "tenant");

/// <summary>
/// Stable failure codes returned while resolving an authenticated ASP.NET AI context.
/// </summary>
public static class NhAiAspNetFailureCodes
{
    public const string AuthenticationRequired = "ai-tool-authentication-required";
    public const string ClaimDuplicate = "ai-tool-claim-duplicate";
    public const string ClaimMissing = "ai-tool-claim-missing";
    public const string ClaimInvalid = "ai-tool-claim-invalid";

    /// <summary>
    /// The principal's issuer is not in the configured issuer set. The existing wire value is
    /// retained so single-issuer consumers can keep their current failure mapping.
    /// </summary>
    public const string IssuerNotAccepted = "ai-tool-issuer-mismatch";
}

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
            services.TryAddScoped<INhAiCallerCredentialAccessor, NhAiHttpContextCallerCredentialAccessor>();
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
        _state.AddIdentityProjections([new NhAiAspNetIdentityProjectionRegistration(
            expectedIssuer,
            issuerClaimType,
            subjectClaimType,
            tenantClaimType,
            null,
            tenantScopeKey,
            tenantScopeType)]);
        return this;
    }

    /// <summary>
    /// Accepts one or more exact issuers, each with its own authority claim mapping.
    /// </summary>
    public NhAiAspNetBuilder UseAuthenticatedClaims(
        IEnumerable<NhAiAspNetIssuerClaimMapping> issuers)
    {
        ArgumentNullException.ThrowIfNull(issuers);
        var mappings = issuers.ToArray();
        if (mappings.Length == 0)
        {
            throw new ArgumentException(
                "At least one ASP.NET AI issuer claim mapping is required.",
                nameof(issuers));
        }

        var registrations = new NhAiAspNetIdentityProjectionRegistration[mappings.Length];
        for (var index = 0; index < mappings.Length; index++)
        {
            var mapping = mappings[index] ?? throw new ArgumentException(
                "ASP.NET AI issuer claim mappings cannot contain null entries.",
                nameof(issuers));
            ValidateIdentityProjection(
                mapping.ExpectedIssuer,
                mapping.IssuerClaimType,
                mapping.SubjectClaimType);
            ArgumentException.ThrowIfNullOrWhiteSpace(mapping.TenantClaimType);
            NhAiAspNetNames.ValidateSegment(mapping.TenantScopeKey, nameof(mapping.TenantScopeKey));
            NhAiAspNetNames.ValidateSegment(mapping.TenantScopeType, nameof(mapping.TenantScopeType));
            registrations[index] = new NhAiAspNetIdentityProjectionRegistration(
                mapping.ExpectedIssuer,
                mapping.IssuerClaimType,
                mapping.SubjectClaimType,
                mapping.TenantClaimType,
                null,
                mapping.TenantScopeKey,
                mapping.TenantScopeType);
        }

        _state.AddIdentityProjections(registrations);
        return this;
    }

    /// <summary>
    /// Projects an exact issuer and subject without requiring a tenant claim. Use this for an
    /// explicitly tenantless application, not as a fallback when a tenant claim is missing.
    /// </summary>
    public NhAiAspNetBuilder UseAuthenticatedClaimsWithoutTenant(
        string expectedIssuer,
        string issuerClaimType = "iss",
        string subjectClaimType = "sub")
    {
        ValidateIdentityProjection(expectedIssuer, issuerClaimType, subjectClaimType);
        _state.AddIdentityProjections([new NhAiAspNetIdentityProjectionRegistration(
            expectedIssuer,
            issuerClaimType,
            subjectClaimType,
            null,
            null,
            "tenant-id",
            "tenant")]);
        return this;
    }

    /// <summary>
    /// Projects every authenticated subject into one explicit tenant without trusting a tenant
    /// claim supplied by the caller.
    /// </summary>
    public NhAiAspNetBuilder UseAuthenticatedClaimsForSingleTenant(
        string expectedIssuer,
        string tenantId,
        string issuerClaimType = "iss",
        string subjectClaimType = "sub",
        string tenantScopeKey = "tenant-id",
        string tenantScopeType = "tenant")
    {
        ValidateIdentityProjection(expectedIssuer, issuerClaimType, subjectClaimType);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (tenantId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId));
        }
        NhAiAspNetNames.ValidateSegment(tenantScopeKey, nameof(tenantScopeKey));
        NhAiAspNetNames.ValidateSegment(tenantScopeType, nameof(tenantScopeType));
        _state.AddIdentityProjections([new NhAiAspNetIdentityProjectionRegistration(
            expectedIssuer,
            issuerClaimType,
            subjectClaimType,
            null,
            tenantId,
            tenantScopeKey,
            tenantScopeType)]);
        return this;
    }

    private static void ValidateIdentityProjection(
        string expectedIssuer,
        string issuerClaimType,
        string subjectClaimType)
    {
        ValidateExpectedIssuer(expectedIssuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuerClaimType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectClaimType);
    }

    private static void ValidateExpectedIssuer(string expectedIssuer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIssuer);
        if (expectedIssuer.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedIssuer));
        }
    }

    public NhAiAspNetBuilder AddClaimScope(
        string claimType,
        string scopeKey,
        bool required = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        NhAiAspNetNames.ValidateSegment(scopeKey, nameof(scopeKey));
        _state.AddClaimScope(new NhAiAspNetClaimScopeRegistration(
            null,
            claimType,
            scopeKey,
            required));
        return this;
    }

    /// <summary>
    /// Projects a scalar claim only when the resolved principal belongs to the specified issuer.
    /// </summary>
    public NhAiAspNetBuilder AddClaimScope(
        string expectedIssuer,
        string claimType,
        string scopeKey,
        bool required = false)
    {
        ValidateExpectedIssuer(expectedIssuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        NhAiAspNetNames.ValidateSegment(scopeKey, nameof(scopeKey));
        _state.AddClaimScope(new NhAiAspNetClaimScopeRegistration(
            expectedIssuer,
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
        if (state.TryGetIdentityProjection(context.Issuer, out var identityProjection)
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
            return Failed(
                NhAiAspNetFailureCodes.AuthenticationRequired,
                "AI tool authentication is required.");
        }

        NhAiAspNetIdentityProjectionRegistration? projection = null;
        var issuer = TaskResult<string?>.Succeeded(null);
        if (state.IdentityProjections.Count > 0)
        {
            var resolvedProjection = ResolveIdentityProjection(
                httpContext.User,
                state.IdentityProjections);
            if (!resolvedProjection.Success)
            {
                return TaskResult<NhAiInvocationContext>.Failed(resolvedProjection);
            }

            projection = resolvedProjection.Data;
            issuer = TaskResult<string?>.Succeeded(projection.ExpectedIssuer);
        }
        var subject = ResolveSingleClaim(
            httpContext.User,
            projection?.SubjectClaimType ?? ClaimTypes.NameIdentifier,
            true);
        var tenant = projection?.FixedTenantId is not null
            ? TaskResult<string?>.Succeeded(projection.FixedTenantId)
            : projection?.TenantClaimType is not null
                ? ResolveSingleClaim(httpContext.User, projection.TenantClaimType, true)
                : projection is null
            ? TaskResult<string?>.Succeeded(null)
            : TaskResult<string?>.Succeeded(null);
        var identityFailure = FirstFailure(issuer, subject, tenant);
        if (identityFailure is not null)
        {
            return TaskResult<NhAiInvocationContext>.Failed(identityFailure);
        }

        var scope = new Dictionary<string, string>(StringComparer.Ordinal);
        var tenantId = tenant.Data;
        if (projection is not null && tenantId is not null)
        {
            scope.Add(projection.TenantScopeKey, tenantId!);
        }
        foreach (var claimScope in state.ClaimScopes
            .Where(item => item.AppliesTo(projection?.ExpectedIssuer))
            .OrderBy(item => item.ScopeKey, StringComparer.Ordinal))
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
        if (projection is not null && tenantId is not null)
        {
            executionScopes.Add(new NhAiExecutionScopeEntry(
                projection.TenantScopeType,
                tenantId!));
        }
        var capabilities = context.CapabilityGrants.ToHashSet(StringComparer.Ordinal);
        foreach (var scopeCapability in state.ScopeCapabilities)
        {
            var values = httpContext.User.FindAll(scopeCapability.ClaimType).ToArray();
            if (values.Any(value => value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(scopeCapability.ClaimValue, StringComparer.Ordinal)))
            {
                capabilities.Add(scopeCapability.Capability);
            }
        }

        object? resource = projection is null || tenantId is null
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

    private static TaskResult<NhAiAspNetIdentityProjectionRegistration> ResolveIdentityProjection(
        ClaimsPrincipal principal,
        IReadOnlyCollection<NhAiAspNetIdentityProjectionRegistration> projections)
    {
        var issuerClaims = new List<(string ClaimType, string Value)>();
        foreach (var claimType in projections
            .Select(item => item.IssuerClaimType)
            .Distinct(StringComparer.Ordinal))
        {
            var issuer = ResolveSingleClaim(principal, claimType, false);
            if (!issuer.Success)
            {
                return TaskResult<NhAiAspNetIdentityProjectionRegistration>.Failed(issuer);
            }
            if (issuer.Data is not null)
            {
                issuerClaims.Add((claimType, issuer.Data));
            }
        }

        if (issuerClaims.Count == 0)
        {
            return TaskResult<NhAiAspNetIdentityProjectionRegistration>.Failed(
                NhAiAspNetFailureCodes.ClaimMissing,
                "One configured authenticated issuer claim is required.");
        }
        if (issuerClaims.Count > 1)
        {
            return MultipleAuthorityClaimsFailure();
        }

        var issuerClaim = issuerClaims[0];
        var projection = projections.SingleOrDefault(item =>
            string.Equals(item.IssuerClaimType, issuerClaim.ClaimType, StringComparison.Ordinal)
            && string.Equals(item.ExpectedIssuer, issuerClaim.Value, StringComparison.Ordinal));
        if (projection is null)
        {
            return TaskResult<NhAiAspNetIdentityProjectionRegistration>.Failed(
                NhAiAspNetFailureCodes.IssuerNotAccepted,
                "The authenticated token issuer is not accepted.");
        }
        if (HasAuthorityClaimsFromAnotherIssuer(principal, projection, projections))
        {
            return MultipleAuthorityClaimsFailure();
        }

        return TaskResult<NhAiAspNetIdentityProjectionRegistration>.Succeeded(projection);
    }

    private static bool HasAuthorityClaimsFromAnotherIssuer(
        ClaimsPrincipal principal,
        NhAiAspNetIdentityProjectionRegistration selected,
        IReadOnlyCollection<NhAiAspNetIdentityProjectionRegistration> projections)
    {
        var selectedClaimTypes = AuthorityClaimTypes(selected).ToHashSet(StringComparer.Ordinal);
        return projections
            .Where(item => !string.Equals(
                item.ExpectedIssuer,
                selected.ExpectedIssuer,
                StringComparison.Ordinal))
            .SelectMany(AuthorityClaimTypes)
            .Distinct(StringComparer.Ordinal)
            .Where(claimType => !selectedClaimTypes.Contains(claimType))
            .Any(claimType => principal.FindAll(claimType).Any());
    }

    private static IEnumerable<string> AuthorityClaimTypes(
        NhAiAspNetIdentityProjectionRegistration projection)
    {
        yield return projection.IssuerClaimType;
        yield return projection.SubjectClaimType;
        if (projection.TenantClaimType is not null)
        {
            yield return projection.TenantClaimType;
        }
    }

    private static TaskResult<NhAiAspNetIdentityProjectionRegistration>
        MultipleAuthorityClaimsFailure()
    {
        return TaskResult<NhAiAspNetIdentityProjectionRegistration>.Failed(
            NhAiAspNetFailureCodes.ClaimDuplicate,
            "The authenticated principal must carry authority claims for exactly one issuer.");
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
                NhAiAspNetFailureCodes.ClaimDuplicate,
                $"The authenticated claim '{claimType}' must occur exactly once.");
        }
        if (claims.Length == 0 || string.IsNullOrWhiteSpace(claims[0].Value))
        {
            return required
                ? TaskResult<string?>.Failed(
                    NhAiAspNetFailureCodes.ClaimMissing,
                    $"The authenticated claim '{claimType}' is required.")
                : TaskResult<string?>.Succeeded(null);
        }
        if (claims[0].Value.Length > 256)
        {
            return TaskResult<string?>.Failed(
                NhAiAspNetFailureCodes.ClaimInvalid,
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
    string? TenantClaimType,
    string? FixedTenantId,
    string TenantScopeKey,
    string TenantScopeType)
{
    public bool ProjectsTenant => TenantClaimType is not null || FixedTenantId is not null;
}

internal sealed record NhAiAspNetClaimScopeRegistration(
    string? ExpectedIssuer,
    string ClaimType,
    string ScopeKey,
    bool Required)
{
    public bool AppliesTo(string? issuer)
    {
        return ExpectedIssuer is null
            || string.Equals(ExpectedIssuer, issuer, StringComparison.Ordinal);
    }
}

internal sealed record NhAiAspNetScopeCapabilityRegistration(
    string ClaimType,
    string ClaimValue,
    string Capability);

internal sealed class NhAiAspNetRegistrationState
{
    private readonly Dictionary<string, string> _capabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAiAspNetIdentityProjectionRegistration> _identityProjections =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string? ExpectedIssuer, string ScopeKey), NhAiAspNetClaimScopeRegistration>
        _claimScopes = new();
    private readonly Dictionary<string, NhAiAspNetScopeCapabilityRegistration> _scopeCapabilities =
        new(StringComparer.Ordinal);

    public NhAiAspNetActiveDivisionRegistration? ActiveDivision { get; private set; }
    public IReadOnlyCollection<NhAiAspNetIdentityProjectionRegistration> IdentityProjections =>
        _identityProjections.Values;
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

    public void AddIdentityProjections(
        IEnumerable<NhAiAspNetIdentityProjectionRegistration> registrations)
    {
        var additions = registrations.ToArray();
        var combined = new Dictionary<string, NhAiAspNetIdentityProjectionRegistration>(
            _identityProjections,
            StringComparer.Ordinal);
        foreach (var registration in additions)
        {
            if (registration.ProjectsTenant && _claimScopes.Values.Any(claimScope =>
                claimScope.AppliesTo(registration.ExpectedIssuer)
                && string.Equals(
                    claimScope.ScopeKey,
                    registration.TenantScopeKey,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"AI tenant scope '{registration.TenantScopeKey}' conflicts with a projected claim scope.");
            }
            if (combined.TryGetValue(registration.ExpectedIssuer, out var existing)
                && existing != registration)
            {
                throw new InvalidOperationException(
                    $"The ASP.NET AI issuer '{registration.ExpectedIssuer}' is already registered with a different claim mapping.");
            }
            combined[registration.ExpectedIssuer] = registration;
        }

        foreach (var registration in additions)
        {
            _identityProjections[registration.ExpectedIssuer] = registration;
        }
    }

    public bool TryGetIdentityProjection(
        string? issuer,
        out NhAiAspNetIdentityProjectionRegistration registration)
    {
        if (issuer is not null && _identityProjections.TryGetValue(issuer, out var found))
        {
            registration = found;
            return true;
        }

        registration = null!;
        return false;
    }

    public void AddClaimScope(NhAiAspNetClaimScopeRegistration registration)
    {
        if (registration.ExpectedIssuer is not null
            && !_identityProjections.ContainsKey(registration.ExpectedIssuer))
        {
            throw new InvalidOperationException(
                $"ASP.NET AI issuer '{registration.ExpectedIssuer}' must be registered before adding an issuer-specific claim scope.");
        }
        if (_identityProjections.Values.Any(identityProjection =>
            registration.AppliesTo(identityProjection.ExpectedIssuer)
            && identityProjection.ProjectsTenant
            && string.Equals(
                identityProjection.TenantScopeKey,
                registration.ScopeKey,
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"AI claim scope '{registration.ScopeKey}' conflicts with the tenant scope.");
        }
        var existing = _claimScopes.Values.FirstOrDefault(item =>
            string.Equals(item.ScopeKey, registration.ScopeKey, StringComparison.Ordinal)
            && (item.ExpectedIssuer is null
                || registration.ExpectedIssuer is null
                || string.Equals(
                    item.ExpectedIssuer,
                    registration.ExpectedIssuer,
                    StringComparison.Ordinal)));
        if (existing is not null && existing != registration)
        {
            throw new InvalidOperationException(
                $"AI claim scope '{registration.ScopeKey}' is already registered with a different contract.");
        }
        _claimScopes[(registration.ExpectedIssuer, registration.ScopeKey)] = registration;
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
