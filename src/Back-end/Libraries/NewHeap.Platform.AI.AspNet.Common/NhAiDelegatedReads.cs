using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// Issues the short-lived bearer token with which the API bridge gateway reads for the accountable
/// person of a headless agent run. The token must be accepted by the host's own JWT bearer scheme
/// and carry every claim of <see cref="NhAiDelegatedReadRequest.Claims"/>. Without a custom issuer,
/// <c>AddNewHeapPlatformAIDelegatedReads</c> uses the NewHeap authentication service.
/// </summary>
public interface INhAiDelegatedReadCredentialIssuer
{
    ValueTask<TaskResult<NhAiDelegatedReadCredential>> IssueAsync(
        NhAiDelegatedReadRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A request for a delegated read credential: the person whose rights the call uses, the subject's
/// division for hosts that carry the division in the token, the invocation the token is bound to,
/// its lifetime and the marker claims the token must carry.
/// </summary>
public sealed record NhAiDelegatedReadRequest(
    string PersonActorId,
    string? DivisionId,
    Guid InvocationId,
    TimeSpan Lifetime,
    IReadOnlyList<Claim> Claims);

/// <summary>
/// A delegated read credential. It is held for one call only and never stored, logged or placed in
/// an invocation context or audit record.
/// </summary>
public sealed record NhAiDelegatedReadCredential(string BearerToken, DateTimeOffset ExpiresAt);

public static class NhAiDelegatedReadDefaults
{
    /// <summary>
    /// The claim that marks a delegated read token; its value is the invocation id the token is bound to.
    /// </summary>
    public const string ClaimType = "nh-ai-delegated-read";

    /// <summary>The header that carries the invocation id of a bridge call.</summary>
    public const string InvocationHeaderName = "X-NewHeap-AI-Invocation";

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The reads that a delegated read credential may reach, published by the API bridge gateway: its
/// read tools, the <c>GET</c> endpoints its resources expose, and the policies a reader of a thread
/// must satisfy to see what such a read returned.
/// </summary>
public interface INhAiDelegatedReadSurface
{
    /// <summary>
    /// Whether the descriptor is a gateway read: one of the gateway's tools or the <c>GET</c> action
    /// of one of its resources.
    /// </summary>
    bool IsReadTool(NhAiToolDescriptor descriptor);

    /// <summary>Whether the endpoint is the <c>GET</c> action of a gateway resource.</summary>
    bool IsReadEndpoint(Endpoint endpoint);

    /// <summary>
    /// The policies a reader must satisfy to see the result of a delegated read of
    /// <paramref name="toolId"/> with <paramref name="argumentsJson"/>, or <see langword="null"/> when
    /// the tool is not a gateway read or its arguments name no known resource.
    /// </summary>
    IReadOnlyList<string>? ResultPolicies(string toolId, string? argumentsJson);
}

/// <summary>
/// Configures delegated reads: the credential issuer, its lifetime and the JWT bearer scheme that
/// validates and restricts the credential.
/// </summary>
public sealed class NhAiDelegatedReadsBuilder
{
    private readonly NhAiDelegatedReadOptions _options;

    internal NhAiDelegatedReadsBuilder(NhAiDelegatedReadOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Issues credentials with <typeparamref name="TIssuer"/> instead of the NewHeap authentication
    /// service, for hosts with another identity provider.
    /// </summary>
    public NhAiDelegatedReadsBuilder UseIssuer<TIssuer>()
        where TIssuer : class, INhAiDelegatedReadCredentialIssuer
    {
        _options.IssuerType = typeof(TIssuer);
        return this;
    }

    /// <summary>The lifetime of one credential: 60 seconds by default, at most 5 minutes.</summary>
    public NhAiDelegatedReadsBuilder Lifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > NhAiDelegatedReadDefaults.MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                "A delegated read credential lives longer than zero and at most 5 minutes.");
        }
        _options.Lifetime = lifetime;
        return this;
    }

    /// <summary>
    /// The JWT bearer scheme that validates the credential, <c>Bearer</c> by default.
    /// </summary>
    public NhAiDelegatedReadsBuilder UseScheme(string scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        _options.Scheme = scheme;
        return this;
    }
}

internal sealed class NhAiDelegatedReadOptions
{
    public Type IssuerType { get; set; } = typeof(NhAiNewHeapDelegatedReadCredentialIssuer);

    public TimeSpan Lifetime { get; set; } = NhAiDelegatedReadDefaults.DefaultLifetime;

    public string Scheme { get; set; } = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>The restriction the JWT bearer scheme ended up with, checked at startup.</summary>
    public Func<TokenValidatedContext, Task>? AppliedRestriction { get; set; }
}

public static class NhAiDelegatedReadServiceCollectionExtensions
{
    /// <summary>
    /// Lets the API bridge gateway read in headless agent runs with the rights of the run's
    /// accountable person. Every call gets a short-lived JWT of the host's own bearer scheme,
    /// marked with the invocation it belongs to. The library restricts such a token to <c>GET</c>
    /// calls of gateway resources with the matching <c>X-NewHeap-AI-Invocation</c> header; every
    /// other request with it fails authentication. Agents opt in with <c>AllowGatewayReads</c>.
    /// </summary>
    public static IServiceCollection AddNewHeapPlatformAIDelegatedReads(
        this IServiceCollection services,
        Action<NhAiDelegatedReadsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(NhAiDelegatedReadOptions)))
        {
            throw new InvalidOperationException(
                "AddNewHeapPlatformAIDelegatedReads can only be called once per application.");
        }

        var options = new NhAiDelegatedReadOptions();
        configure?.Invoke(new NhAiDelegatedReadsBuilder(options));
        services.AddSingleton(options);
        services.AddHttpContextAccessor();
        services.TryAdd(ServiceDescriptor.Scoped(typeof(INhAiDelegatedReadCredentialIssuer), options.IssuerType));
        services.TryAddScoped<NhAiDelegatedReadService>();
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, NhAiDelegatedReadRestriction>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAiDelegatedReadStartupValidator>());
        return services;
    }
}

/// <summary>
/// The built-in issuer for hosts on the NewHeap identity: the token carries the person's own
/// claims and security stamp, so the host's session validation rejects a removed or changed person.
/// </summary>
internal sealed class NhAiNewHeapDelegatedReadCredentialIssuer(IServiceProvider services) : INhAiDelegatedReadCredentialIssuer
{
    public async ValueTask<TaskResult<NhAiDelegatedReadCredential>> IssueAsync(
        NhAiDelegatedReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userManager = services.GetService<INhUserManager>();
        var authentication = services.GetService<INhAuthenticationService>();
        if (!Guid.TryParse(request.PersonActorId, out var userId) || userManager is null || authentication is null)
        {
            return NhAiDelegatedReadService.Unavailable();
        }

        var claims = await userManager.GetValidClaimsByUserIdAsync(userId, true, cancellationToken);
        var canSignIn = claims.Any(claim =>
            claim.Type == NhPlatformClaimTypes.Permission
            && claim.Value == Platform.Common.Constants.PermissionClaimValues.AuthenticatedAccess);
        if (!canSignIn)
        {
            return NhAiDelegatedReadService.Unavailable();
        }

        var markers = request.Claims.Select(claim => claim.Type).ToHashSet(StringComparer.Ordinal);
        var token = await authentication.CreateToken(
            userId,
            claims.Where(claim => !markers.Contains(claim.Type)).Concat(request.Claims),
            request.Lifetime);
        return TaskResult<NhAiDelegatedReadCredential>.Succeeded(new NhAiDelegatedReadCredential(
            new JwtSecurityTokenHandler().WriteToken(token),
            new DateTimeOffset(DateTime.SpecifyKind(token.ValidTo, DateTimeKind.Utc))));
    }
}

/// <summary>
/// Issues delegated read credentials for the current agent scope and authenticates them through the
/// host's own scheme, so bridge discovery and the gateway see the same principal as the call.
/// Credentials and principals never leave this service except into the one call they are for.
/// </summary>
internal sealed partial class NhAiDelegatedReadService(
    IServiceProvider services,
    NhAiDelegatedReadOptions options,
    ILogger<NhAiDelegatedReadService> logger)
{
    public const string UnavailableCode = "ai-delegated-read-unavailable";

    private static readonly object InternalAuthentication = new();
    private static readonly TimeSpan ExpiryTolerance = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, ClaimsPrincipal?> _principals = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the request is the library's own authentication of a credential. An external
    /// request cannot produce this mark.
    /// </summary>
    public static bool IsInternal(HttpContext httpContext)
    {
        return httpContext.Items.TryGetValue(InternalAuthentication, out var value)
            && ReferenceEquals(value, InternalAuthentication);
    }

    public static TaskResult<NhAiDelegatedReadCredential> Unavailable()
    {
        return TaskResult<NhAiDelegatedReadCredential>.Failed(
            UnavailableCode,
            "No delegated read credential could be issued.");
    }

    /// <summary>
    /// Issues a credential of the scope's accountable person, bound to <paramref name="invocationId"/>.
    /// </summary>
    public async ValueTask<TaskResult<NhAiDelegatedReadCredential>> IssueAsync(
        NhAiAgentInvocationScope scope,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.AllowGatewayReads
            || scope.Accountability == NhAiAgentAccountability.Agent
            || string.IsNullOrWhiteSpace(scope.AccountableOwnerId))
        {
            return Unavailable();
        }

        var issuer = services.GetService<INhAiDelegatedReadCredentialIssuer>();
        if (issuer is null)
        {
            return Unavailable();
        }

        var request = new NhAiDelegatedReadRequest(
            scope.AccountableOwnerId,
            scope.Subject.DivisionId?.ToString(),
            invocationId,
            options.Lifetime,
            [new Claim(NhAiDelegatedReadDefaults.ClaimType, invocationId.ToString("D"))]);
        TaskResult<NhAiDelegatedReadCredential> issued;
        try
        {
            issued = await issuer.IssueAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Issuers read identity data; their messages may carry it. Log the type only.
            LogIssueFailed(logger, scope.AgentId, exception.GetType().Name);
            return Unavailable();
        }

        if (!issued.Success
            || issued.Data is null
            || string.IsNullOrWhiteSpace(issued.Data.BearerToken)
            || issued.Data.ExpiresAt > DateTimeOffset.UtcNow + options.Lifetime + ExpiryTolerance)
        {
            LogIssueRefused(logger, scope.AgentId);
            return Unavailable();
        }
        return issued;
    }

    /// <summary>
    /// The principal of the scope's accountable person as the host's scheme authenticates a
    /// credential, including claims transformation, or <see langword="null"/> when none can be
    /// issued or the scheme rejects it. Cached for the run within this service scope.
    /// </summary>
    public async ValueTask<ClaimsPrincipal?> AuthenticateAsync(
        NhAiAgentInvocationScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var key = scope.AccountableOwnerId + "|" + scope.RunId;
        if (_principals.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ClaimsPrincipal? principal = null;
        var credential = await IssueAsync(scope, Guid.NewGuid(), cancellationToken);
        if (credential.Success)
        {
            // Authentication handlers and their results are cached per service scope; a scope of its
            // own keeps every authentication apart from the run's and from each other.
            await using var authenticationScope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var httpContext = new DefaultHttpContext { RequestServices = authenticationScope.ServiceProvider };
            httpContext.Items[InternalAuthentication] = InternalAuthentication;
            httpContext.Request.Method = HttpMethods.Get;
            httpContext.Request.Headers.Authorization = "Bearer " + credential.Data!.BearerToken;
            var result = await httpContext.AuthenticateAsync(options.Scheme);
            principal = result.Succeeded ? result.Principal : null;
            if (principal is null)
            {
                LogAuthenticationRefused(logger, scope.AgentId);
            }
        }

        _principals[key] = principal;
        return principal;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "A delegated read credential for agent {AgentId} could not be issued: the issuer failed with {ExceptionType}.")]
    private static partial void LogIssueFailed(ILogger logger, string agentId, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The issuer refused a delegated read credential for agent {AgentId} or issued one that lives too long.")]
    private static partial void LogIssueRefused(ILogger logger, string agentId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The host's scheme rejected the delegated read credential for agent {AgentId}.")]
    private static partial void LogAuthenticationRefused(ILogger logger, string agentId);
}

/// <summary>
/// Restricts delegated read tokens in every JWT bearer scheme of the host, so another scheme that
/// trusts the same key cannot accept them either. A token with the
/// <see cref="NhAiDelegatedReadDefaults.ClaimType"/> claim authenticates only a <c>GET</c> request
/// to an endpoint of a gateway resource that carries the matching invocation header, or the
/// library's own authentication of it. Every other request with such a token, including one whose
/// endpoint is not yet known, fails authentication. Tokens without the claim are not affected.
/// </summary>
internal sealed class NhAiDelegatedReadRestriction(NhAiDelegatedReadOptions options) : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions jwt)
    {
        var events = jwt.Events ?? new JwtBearerEvents();
        var inner = events.OnTokenValidated;
        Func<TokenValidatedContext, Task> restriction = async context =>
        {
            if (!Allows(context))
            {
                context.Fail("The delegated read credential is not valid for this request.");
                return;
            }
            if (inner is not null)
            {
                await inner(context);
            }
        };
        events.OnTokenValidated = restriction;
        jwt.Events = events;
        if (string.Equals(name, options.Scheme, StringComparison.Ordinal))
        {
            options.AppliedRestriction = restriction;
        }
    }

    internal static bool Allows(TokenValidatedContext context)
    {
        var markers = context.Principal?.FindAll(NhAiDelegatedReadDefaults.ClaimType).ToArray() ?? [];
        if (markers.Length == 0)
        {
            return true;
        }
        if (markers.Length != 1 || !Guid.TryParse(markers[0].Value, out var invocationId))
        {
            return false;
        }

        var httpContext = context.HttpContext;
        if (NhAiDelegatedReadService.IsInternal(httpContext))
        {
            return true;
        }
        if (!HttpMethods.IsGet(httpContext.Request.Method)
            || httpContext.GetEndpoint() is not { } endpoint
            || !httpContext.RequestServices.GetServices<INhAiDelegatedReadSurface>().Any(surface => surface.IsReadEndpoint(endpoint)))
        {
            return false;
        }

        var header = httpContext.Request.Headers[NhAiDelegatedReadDefaults.InvocationHeaderName];
        return header.Count == 1
            && Guid.TryParse(header[0], out var headerInvocationId)
            && headerInvocationId == invocationId;
    }
}

/// <summary>
/// Fails startup when the delegated read scheme is not a JWT bearer scheme, resolves its events
/// from a type, or replaced the restriction after it was composed.
/// </summary>
internal sealed class NhAiDelegatedReadStartupValidator(
    NhAiDelegatedReadOptions options,
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitor<JwtBearerOptions> jwtOptions) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scheme = await schemes.GetSchemeAsync(options.Scheme);
        if (scheme is null || !typeof(JwtBearerHandler).IsAssignableFrom(scheme.HandlerType))
        {
            throw new InvalidOperationException(
                $"Delegated reads need the JWT bearer scheme '{options.Scheme}'; register it or choose another with UseScheme.");
        }

        var jwt = jwtOptions.Get(options.Scheme);
        if (jwt.EventsType is not null)
        {
            throw new InvalidOperationException(
                $"The JWT bearer scheme '{options.Scheme}' resolves its events from a type, so the delegated read restriction cannot be composed; set Events instead of EventsType.");
        }
        if (options.AppliedRestriction is null || !ReferenceEquals(jwt.Events?.OnTokenValidated, options.AppliedRestriction))
        {
            throw new InvalidOperationException(
                $"The OnTokenValidated event of the JWT bearer scheme '{options.Scheme}' was replaced after AddNewHeapPlatformAIDelegatedReads; compose your handler in the scheme's own configuration instead.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
