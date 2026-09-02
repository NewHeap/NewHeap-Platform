using System.Security.Claims;
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
            services.TryAddScoped<INhAiBackgroundOperationRunAdapter, NhAiBackgroundOperationRunAdapter>();
            services.TryAddScoped<
                INhAiBackgroundOperationIngestionAdapter,
                NhAiBackgroundOperationIngestionAdapter>();
        }

        configure(new NhAiAspNetBuilder(state));
        return services;
    }
}

public sealed class NhAiAspNetBuilder
{
    private readonly NhAiAspNetRegistrationState _state;

    internal NhAiAspNetBuilder(NhAiAspNetRegistrationState state)
    {
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
}

internal sealed class NhAiAspNetToolInvocationGate(
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorizationService,
    INhAiInvocationContextFactory contextFactory,
    NhAiAspNetRegistrationState state) : INhAiToolInvocationGate
{
    public async ValueTask<TaskResult<NhAiInvocationContext>> AuthorizeAsync(
        NhAiToolDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.User.Identity?.IsAuthenticated != true)
        {
            return TaskResult<NhAiInvocationContext>.Failed(
                "ai-tool-authentication-required",
                "AI tool authentication is required.");
        }

        var actorId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return TaskResult<NhAiInvocationContext>.Failed(
                "ai-tool-actor-missing",
                "The authenticated AI tool actor could not be resolved.");
        }

        object? resource = null;
        var activeDivision = state.ActiveDivision;
        var divisionId = httpContext.GetActiveDivisionId();
        if (activeDivision is not null && divisionId is not null)
        {
            resource = new NhAiAspNetScopeAuthorizationResource(
                activeDivision.ScopeType,
                divisionId.Value.ToString(),
                state.ToolInvocationPurpose);
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

        var context = await contextFactory.CreateAsync(
            new NhAiInvocationContextSeed(
                actorId,
                state.ToolInvocationPurpose),
            cancellationToken);
        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            if (idempotencyKey.Length > 256
                || idempotencyKey.Any(character =>
                    !char.IsAsciiLetterOrDigit(character)
                    && character is not '-' and not '_' and not '.' and not ':'))
            {
                return TaskResult<NhAiInvocationContext>.Failed(
                    "ai-tool-idempotency-key-invalid",
                    "The AI tool idempotency key is invalid.");
            }
            context = context with { IdempotencyKey = idempotencyKey };
        }
        return TaskResult<NhAiInvocationContext>.Succeeded(context);
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

internal sealed class NhAiAspNetRegistrationState
{
    private readonly Dictionary<string, string> _capabilities = new(StringComparer.Ordinal);

    public NhAiAspNetActiveDivisionRegistration? ActiveDivision { get; private set; }
    public IReadOnlyDictionary<string, string> Capabilities => _capabilities;
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
