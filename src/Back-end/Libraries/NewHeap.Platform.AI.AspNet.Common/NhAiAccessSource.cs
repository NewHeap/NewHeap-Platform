using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// The application's record of a person's rights for work outside the person's request: the claims
/// its sign-in and claims transformation gave the person, and when they were valid. A host whose rights
/// come from a claims transformation per request, such as an OIDC host that adds the application's own
/// permissions, implements it and registers it with
/// <see cref="NhAiAspNetBuilder.UsePrincipalsFromAccessSource{TSource}"/>, so an agent acts with a
/// person's rights in a background run only while the application's context of that person is fresh.
/// NewHeap stores nothing: the application decides whether and where it keeps contexts, for example
/// next to its sessions or in a distributed cache, and answers for technical accounts as well.
/// </summary>
public interface INhAiAccessSource
{
    /// <summary>
    /// Returns the claims of <paramref name="actorId"/> for work on <paramref name="subject"/> and the
    /// time they were valid, or <see langword="null"/> when the application holds no context of that
    /// person, for example because the person never signed in, signed out or was removed.
    /// </summary>
    /// <param name="actorId">
    /// The platform's actor id, the id the registered <see cref="INhAiAuthenticatedInvocationContextResolver"/>
    /// resolves in the person's own request (for an issuer projection an <c>oidc:</c> id, not the subject claim).
    /// </param>
    /// <param name="subject">
    /// The subject the rights are needed for. A source that keeps a context per tenant or division returns
    /// the one of the subject's tenant or division; others may ignore it.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    ValueTask<NhAiAccessContext?> GetAsync(
        string actorId,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The claims of a person as the application knew them at <see cref="ValidAt"/>, typically the claims of
/// the person's last request after the claims transformation. The principal built from it carries exactly
/// these claims: NewHeap adds none, so it never grants more than they say.
/// </summary>
public sealed class NhAiAccessContext
{
    /// <param name="claims">The person's claims; they are copied.</param>
    /// <param name="validAt">
    /// When the claims were valid, on the application's clock (the registered <see cref="TimeProvider"/>,
    /// otherwise the system clock), for example when the claims transformation last produced them.
    /// </param>
    public NhAiAccessContext(IEnumerable<Claim> claims, DateTimeOffset validAt)
    {
        ArgumentNullException.ThrowIfNull(claims);
        Claim[] copy = [.. claims];
        if (copy.Any(claim => claim is null))
        {
            throw new ArgumentException("An AI access context cannot contain null claims.", nameof(claims));
        }

        Claims = copy;
        ValidAt = validAt;
    }

    /// <summary>The person's claims.</summary>
    public IReadOnlyList<Claim> Claims { get; }

    /// <summary>When the claims were valid, on the application's clock.</summary>
    public DateTimeOffset ValidAt { get; }
}

/// <summary>
/// How <see cref="NhAiAspNetBuilder.UsePrincipalsFromAccessSource{TSource}"/> builds principals from the
/// access source.
/// </summary>
public sealed class NhAiAccessSourceOptions
{
    /// <summary>
    /// How long a context stays valid after its <see cref="NhAiAccessContext.ValidAt"/>, on the application's
    /// clock: an older context yields no principal, so the work it would allow is refused until the person
    /// is back and the application has a fresh context. Required and positive; it has no default, because
    /// only the application knows how soon a withdrawn right must stop acting in background work, and a
    /// missing (zero) or negative value fails at startup.
    /// </summary>
    public TimeSpan MaxAge { get; set; }

    /// <summary>
    /// The name claim type of the principal's identity, as the host's authentication scheme sets it;
    /// <see cref="ClaimTypes.Name"/> by default.
    /// </summary>
    public string NameClaimType { get; set; } = ClaimTypes.Name;

    /// <summary>
    /// The role claim type of the principal's identity, which role requirements read, as the host's
    /// authentication scheme sets it (for example <c>role</c> when inbound claim mapping is off);
    /// <see cref="ClaimTypes.Role"/> by default.
    /// </summary>
    public string RoleClaimType { get; set; } = ClaimTypes.Role;
}

/// <summary>
/// The registered access source and its validated options.
/// </summary>
internal sealed record NhAiAccessSourceRegistration(
    Type SourceType,
    TimeSpan MaxAge,
    string NameClaimType,
    string RoleClaimType)
{
    public static NhAiAccessSourceRegistration Create(Type sourceType, NhAiAccessSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"UsePrincipalsFromAccessSource<{sourceType.Name}> needs a positive MaxAge: how long a context of the "
                + "access source stays valid. It has no default; set it, for example o => o.MaxAge = TimeSpan.FromMinutes(30).");
        }
        if (string.IsNullOrWhiteSpace(options.NameClaimType) || string.IsNullOrWhiteSpace(options.RoleClaimType))
        {
            throw new InvalidOperationException(
                $"UsePrincipalsFromAccessSource<{sourceType.Name}> needs a name and a role claim type.");
        }

        return new NhAiAccessSourceRegistration(sourceType, options.MaxAge, options.NameClaimType, options.RoleClaimType);
    }

    /// <summary>
    /// Refuses a principal factory of the host's own next to the access source: only one of them can
    /// build the principals outside a request, and taking either silently would change whose rights an
    /// agent uses. The built-in factory is replaced.
    /// </summary>
    public static void RefuseOtherPrincipalFactories(IServiceCollection services, Type sourceType)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(INhAiPrincipalFactory) || descriptor.IsKeyedService)
            {
                continue;
            }
            if (descriptor.ImplementationType == typeof(NhAiUserManagerPrincipalFactory)
                || descriptor.ImplementationType == typeof(NhAiAccessSourcePrincipalFactory))
            {
                continue;
            }

            throw ConflictingFactory(sourceType, NameOf(descriptor));
        }
    }

    public static InvalidOperationException ConflictingFactory(Type sourceType, string factoryName)
    {
        return new InvalidOperationException(
            $"UsePrincipalsFromAccessSource<{sourceType.Name}> builds the principals outside a request, and the host "
            + $"also registers INhAiPrincipalFactory '{factoryName}'. Register only one of them: the access source, "
            + "or a principal factory of your own without UsePrincipalsFromAccessSource.");
    }

    private static string NameOf(ServiceDescriptor descriptor)
    {
        return descriptor.ImplementationType?.Name
            ?? descriptor.ImplementationInstance?.GetType().Name
            ?? "a factory delegate";
    }
}

/// <summary>
/// Builds a principal from the application's access source, for every decision outside a person's
/// request that asks <see cref="INhAiPrincipalFactory"/>. It returns none when the source holds no
/// context, when the context is older than <see cref="NhAiAccessSourceOptions.MaxAge"/> on the
/// application's clock (a context dated later than now counts as fresh only within the same bound, so a
/// wrong date never keeps it fresh), and when the AI ASP.NET integration takes tenants from a claim and
/// the context's tenant claim does not name exactly the subject's tenant. The principal carries exactly
/// the context's claims in one authenticated identity of the authentication type every principal NewHeap
/// rebuilds has (<c>nh-ai-delegation</c>); NewHeap adds no actor id, tenant or other claim.
/// </summary>
internal sealed partial class NhAiAccessSourcePrincipalFactory(
    INhAiAccessSource source,
    NhAiAspNetRegistrationState state,
    TimeProvider? timeProvider = null,
    ILogger<NhAiAccessSourcePrincipalFactory>? logger = null) : INhAiPrincipalFactory
{
    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async ValueTask<ClaimsPrincipal?> CreateAsync(
        string actorId,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(subject);
        var registration = state.AccessSource
            ?? throw new InvalidOperationException("The AI access source is not registered.");

        var context = await source.GetAsync(actorId, subject, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var age = _applicationClock.GetUtcNow() - context.ValidAt;
        if (age.Duration() > registration.MaxAge)
        {
            LogStale(_logger, registration.MaxAge);
            return null;
        }

        if (!NamesSubjectTenant(context, subject))
        {
            LogOtherTenant(_logger);
            return null;
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            context.Claims,
            NhAiUserManagerPrincipalFactory.AuthenticationType,
            registration.NameClaimType,
            registration.RoleClaimType));
    }

    /// <summary>
    /// Whether the context fits the subject's tenant. Only where the AI ASP.NET integration takes the
    /// tenant from a claim (<c>UseAuthenticatedClaims</c>) does a person's context carry a tenant, as in the
    /// person's request: then a subject of a tenant needs exactly one tenant claim with that tenant, so the
    /// rights of one tenant never act on another tenant's subject. A subject without a tenant, such as the
    /// whole application, and a host that projects no tenant or one fixed tenant
    /// (<c>UseAuthenticatedClaimsForSingleTenant</c>, the single-tenant mode) compare nothing.
    /// </summary>
    private bool NamesSubjectTenant(NhAiAccessContext context, NhAiSubjectInfo subject)
    {
        if (subject.TenantId is not { } tenantId)
        {
            return true;
        }

        var tenantClaimTypes = state.IdentityProjections
            .Select(projection => projection.TenantClaimType)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (tenantClaimTypes.Count == 0)
        {
            return true;
        }

        var tenants = context.Claims.Where(claim => tenantClaimTypes.Contains(claim.Type)).ToArray();
        return tenants.Length == 1 && string.Equals(tenants[0].Value, tenantId, StringComparison.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The AI access source returned a context older than its maximum age of {MaxAge}; no principal is built.")]
    private static partial void LogStale(ILogger logger, TimeSpan maxAge);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The AI access source returned a context whose tenant claim does not name the subject's tenant; no principal is built.")]
    private static partial void LogOtherTenant(ILogger logger);
}

/// <summary>
/// Fails the host's start when a principal factory of the host's own was registered after
/// <see cref="NhAiAspNetBuilder.UsePrincipalsFromAccessSource{TSource}"/>; one registered before it fails
/// the registration itself.
/// </summary>
internal sealed class NhAiAccessSourceStartupCheck(
    IServiceProvider hostServices,
    IServiceScopeFactory scopes,
    NhAiAspNetRegistrationState state) : NhAiStartupCheck(hostServices)
{
    protected override async Task ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state.AccessSource is not { } registration)
        {
            return;
        }

        // Factories may be scoped services, so they are resolved in a validation scope.
        await using var scope = scopes.CreateAsyncScope();
        foreach (var factory in scope.ServiceProvider.GetServices<INhAiPrincipalFactory>())
        {
            if (factory is not NhAiAccessSourcePrincipalFactory)
            {
                throw NhAiAccessSourceRegistration.ConflictingFactory(registration.SourceType, factory.GetType().Name);
            }
        }
    }
}
