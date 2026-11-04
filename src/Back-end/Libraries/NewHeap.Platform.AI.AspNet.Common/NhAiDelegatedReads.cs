using System.Collections.Concurrent;
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
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// Issues the short-lived bearer token with which the API bridge gateway reads for the accountable
/// person of a headless agent run. The token must be accepted by the host's own JWT bearer scheme
/// and carry every claim of <see cref="NhAiDelegatedReadRequest.Claims"/>. Without a custom issuer,
/// <c>AddNewHeapPlatformAIDelegatedReads</c> uses the NewHeap authentication service.
/// </summary>
/// <remarks>
/// The library asks the issuer only for a person who may lend rights outside a request: with
/// <see cref="NhAiAspNetBuilder.UsePrincipalsFromAccessSource{TSource}"/>, only while
/// <see cref="INhAiPrincipalFactory"/> builds the person's principal from a fresh context. An issuer
/// still refuses a person whom it does not know or who may no longer sign in. The token carries the
/// <see cref="NhAiDelegatedReadDefaults.ClaimType"/> marker, so a request it authenticates is never the
/// person's own: an access source does not record it (<see cref="NhAiAccessContext.IsPersonsOwnRequest"/>).
/// </remarks>
public interface INhAiDelegatedReadCredentialIssuer
{
    ValueTask<TaskResult<NhAiDelegatedReadCredential>> IssueAsync(
        NhAiDelegatedReadRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A request for a delegated read credential: the person whose rights the call uses, the subject's
/// division for hosts that carry the division in the token, the invocation the token is bound to,
/// its lifetime and the claims the token must carry exactly: the invocation marker and, for a call,
/// the gateway action it may reach. The library refuses a token without them or that lives longer.
/// </summary>
public sealed record NhAiDelegatedReadRequest(
    string PersonActorId,
    string? DivisionId,
    Guid InvocationId,
    TimeSpan Lifetime,
    IReadOnlyList<Claim> Claims)
{
    /// <summary>The subject's tenant, for hosts that bind tokens to a tenant.</summary>
    public string? TenantId { get; init; }
}

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

    /// <summary>
    /// The claim that binds a delegated read token to one gateway action; its value is the bridge
    /// tool id of the resource operation the call executes.
    /// </summary>
    public const string ActionClaimType = "nh-ai-delegated-read-action";

    /// <summary>The header that carries the invocation id of a bridge call.</summary>
    public const string InvocationHeaderName = "X-NewHeap-AI-Invocation";

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Why a gateway read of a headless agent run got no usable delegated read credential. The API bridge
/// gateway fails such a read with its cause as the code, so the run, the administration and the agent
/// builder can say what to fix, and each cause logs one content-free warning per host that names the
/// fix. A person who may not lend rights now, such as one without a fresh context under
/// <see cref="NhAiAspNetBuilder.UsePrincipalsFromAccessSource{TSource}"/> or a subject's responsible
/// person who changed after the run was queued, still fails with <c>api-bridge-unauthenticated</c>.
/// </summary>
public static class NhAiDelegatedReadFailureCodes
{
    /// <summary>
    /// The host authenticated the credential before it knew the request's target, so the restriction
    /// refused it: the host authenticates before routing. Route first (<c>UseRouting</c> before
    /// <c>UseAuthentication</c>), or, without endpoint routing, let the API bridge authenticate the
    /// scheme with the executing action as its target instead of middleware before MVC.
    /// </summary>
    public const string TargetUnknown = "ai-delegated-read-target-unknown";

    /// <summary>
    /// The agent allows gateway reads, but the host issues no delegated read credentials:
    /// <c>AddNewHeapPlatformAIDelegatedReads</c> is missing, or its built-in issuer runs on a host without
    /// the NewHeap authentication service and no issuer of its own (<see cref="NhAiDelegatedReadsBuilder.UseIssuer{TIssuer}"/>).
    /// </summary>
    public const string NoIssuer = "ai-delegated-read-no-issuer";

    /// <summary>
    /// The issuer refused the person the run reads for, failed, or issued a token without exactly its
    /// binding claims or that lives longer than the configured lifetime.
    /// </summary>
    public const string IssuerRefused = "ai-delegated-read-issuer-refused";

    /// <summary>
    /// The host's JWT bearer scheme rejected the issued credential, or the API answered the gateway
    /// read with <c>401</c> for another reason than an unknown target.
    /// </summary>
    public const string Rejected = "ai-delegated-read-rejected";

    /// <summary>Every cause, for an application that matches gateway read failures by code.</summary>
    public static IReadOnlyList<string> All { get; } = [TargetUnknown, NoIssuer, IssuerRefused, Rejected];
}

/// <summary>
/// The reads that a delegated read credential may reach, published by the API bridge gateway: its
/// read tools and the <c>GET</c> endpoints of its resources.
/// </summary>
public interface INhAiDelegatedReadSurface
{
    /// <summary>
    /// Whether the descriptor is a gateway read: one of the gateway's tools or the <c>GET</c> action
    /// of one of its resources.
    /// </summary>
    bool IsReadTool(NhAiToolDescriptor descriptor);

    /// <summary>
    /// Whether the endpoint is the <c>GET</c> action of the gateway resource operation whose bridge
    /// tool id is <paramref name="actionToolId"/>.
    /// </summary>
    bool IsReadEndpoint(Endpoint endpoint, string actionToolId);
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
}

public static class NhAiDelegatedReadServiceCollectionExtensions
{
    /// <summary>
    /// Lets the API bridge gateway read in headless agent runs with the rights of the person who
    /// started the run, or of its accountable person when nobody did. Every call gets a short-lived
    /// JWT of the host's own bearer scheme, bound to the invocation and the gateway action it calls.
    /// Every JWT bearer scheme of the host restricts such a token to that <c>GET</c> action with the
    /// matching <c>X-NewHeap-AI-Invocation</c> header; every other request with it fails
    /// authentication. Agents opt in with <c>AllowGatewayReads</c>. With
    /// <see cref="NhAiAspNetBuilder.UsePrincipalsFromAccessSource{TSource}"/> a credential is issued only
    /// while the access source holds a fresh context of the person the run reads for.
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
        services.TryAddSingleton<NhAiDelegatedReadDiagnostics>();
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, NhAiDelegatedReadRestriction>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAiDelegatedReadStartupValidator>());
        return services;
    }
}

/// <summary>
/// The built-in issuer for hosts on the NewHeap identity: the token carries the claims of the
/// person's own sign-in and their security stamp, and is only issued while the person may sign in,
/// so a removed, locked-out or blocked person gets none and a changed person's token is rejected.
/// </summary>
internal sealed class NhAiNewHeapDelegatedReadCredentialIssuer(IServiceProvider services) : INhAiDelegatedReadCredentialIssuer
{
    /// <summary>
    /// Whether the host has the NewHeap authentication service; without it this issuer issues nothing
    /// and the host needs an issuer of its own.
    /// </summary>
    public bool CanIssue => services.GetService<INhAuthenticationService>() is not null;

    public async ValueTask<TaskResult<NhAiDelegatedReadCredential>> IssueAsync(
        NhAiDelegatedReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authentication = services.GetService<INhAuthenticationService>();
        if (!Guid.TryParse(request.PersonActorId, out var userId) || authentication is null)
        {
            return NhAiDelegatedReadService.Unavailable();
        }

        var created = await authentication.CreateDelegatedTokenAsync(userId, request.Claims, request.Lifetime, cancellationToken);
        if (!created.Success || created.Data is null)
        {
            return NhAiDelegatedReadService.Unavailable();
        }
        return TaskResult<NhAiDelegatedReadCredential>.Succeeded(new NhAiDelegatedReadCredential(
            new JwtSecurityTokenHandler().WriteToken(created.Data),
            new DateTimeOffset(DateTime.SpecifyKind(created.Data.ValidTo, DateTimeKind.Utc))));
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
    NhAiDelegatedReadDiagnostics diagnostics,
    ILogger<NhAiDelegatedReadService> logger)
{
    /// <summary>
    /// The code of a credential that is not issued because the person may not lend rights now; the
    /// gateway reports it as <c>api-bridge-unauthenticated</c>. Other refusals carry a cause of
    /// <see cref="NhAiDelegatedReadFailureCodes"/>.
    /// </summary>
    public const string UnavailableCode = "ai-delegated-read-unavailable";

    private static readonly object InternalAuthentication = new();
    private static readonly object IdentifiedTarget = new();
    private static readonly TimeSpan ExpiryTolerance = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, NhAiDelegatedReadAuthentication> _principals = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the request is the library's own authentication of a credential. An external
    /// request cannot produce this mark.
    /// </summary>
    public static bool IsInternal(HttpContext httpContext)
    {
        return httpContext.Items.TryGetValue(InternalAuthentication, out var value)
            && ReferenceEquals(value, InternalAuthentication);
    }

    /// <summary>
    /// The target that the delegated read restriction checks: the target passed to
    /// <see cref="AuthenticateForTargetAsync"/> while that authentication runs, otherwise the routed
    /// endpoint, or <see langword="null"/> when neither is known yet.
    /// </summary>
    public static Endpoint? Target(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(IdentifiedTarget, out var value) && value is Endpoint identified)
        {
            return identified;
        }
        return httpContext.GetEndpoint();
    }

    /// <summary>
    /// Authenticates <paramref name="scheme"/> for a request whose target the host identified without
    /// endpoint routing, such as the action that MVC selected under <c>UseMvc</c>. The restriction sees
    /// the target only during this call; like every authentication, the result is cached for the
    /// request, so a scheme that was authenticated earlier keeps its earlier result.
    /// </summary>
    public static async Task<AuthenticateResult> AuthenticateForTargetAsync(
        HttpContext httpContext,
        Endpoint target,
        string scheme)
    {
        httpContext.Items[IdentifiedTarget] = target;
        try
        {
            return await httpContext.AuthenticateAsync(scheme);
        }
        finally
        {
            httpContext.Items.Remove(IdentifiedTarget);
        }
    }

    public static TaskResult<NhAiDelegatedReadCredential> Unavailable()
    {
        return TaskResult<NhAiDelegatedReadCredential>.Failed(
            UnavailableCode,
            "No delegated read credential could be issued.");
    }

    /// <summary>A refusal with one of the causes of <see cref="NhAiDelegatedReadFailureCodes"/>.</summary>
    private static TaskResult<NhAiDelegatedReadCredential> Refused(string cause)
    {
        return TaskResult<NhAiDelegatedReadCredential>.Failed(
            cause,
            "No delegated read credential could be issued.");
    }

    /// <summary>
    /// Issues a credential of the scope's reading person (<see cref="NhAiAgentInvocationScope.GatewayReadActorId"/>),
    /// bound to <paramref name="invocationId"/> and, for a call, to the gateway action
    /// <paramref name="actionToolId"/>. The token the issuer returns must carry exactly those claims
    /// and live no longer than the configured lifetime; otherwise it is never used. Under
    /// <see cref="NhAiAspNetBuilder.UsePrincipalsFromAccessSource{TSource}"/> the issuer is asked only
    /// while <see cref="INhAiPrincipalFactory"/> builds the person's principal. A refusal carries its
    /// cause (<see cref="NhAiDelegatedReadFailureCodes"/>), or <see cref="UnavailableCode"/> when the
    /// person may not lend rights now.
    /// </summary>
    public async ValueTask<TaskResult<NhAiDelegatedReadCredential>> IssueAsync(
        NhAiAgentInvocationScope scope,
        Guid invocationId,
        string? actionToolId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.GatewayReadActorId is not { } person || scope.Accountability == NhAiAgentAccountability.Agent)
        {
            return Unavailable();
        }
        // Without an issuer that can issue, no read of any person ever works: say so first.
        var issuer = services.GetService<INhAiDelegatedReadCredentialIssuer>();
        if (issuer is null or NhAiNewHeapDelegatedReadCredentialIssuer { CanIssue: false })
        {
            diagnostics.Report(NhAiDelegatedReadFailureCodes.NoIssuer, scope.AgentId);
            return Refused(NhAiDelegatedReadFailureCodes.NoIssuer);
        }
        // The run answers to the subject's responsible person; when that changed after the run was
        // queued, the person it was queued for no longer lends their rights.
        if (string.Equals(person, scope.AccountableOwnerId, StringComparison.Ordinal)
            && scope.Accountability == NhAiAgentAccountability.SubjectResponsible
            && !string.Equals(scope.Subject.ResponsibleActorId, person, StringComparison.Ordinal))
        {
            LogResponsibleChanged(logger, scope.AgentId);
            return Unavailable();
        }
        // Where the application's access source builds the principals outside a request, a person lends
        // rights only while their context is fresh; a read is no exception, whatever the issuer checks.
        if (!await MayLendRightsAsync(person, scope.Subject, cancellationToken))
        {
            LogNoFreshContext(logger, scope.AgentId);
            return Unavailable();
        }

        List<Claim> claims = [new Claim(NhAiDelegatedReadDefaults.ClaimType, invocationId.ToString("D"))];
        if (actionToolId is not null)
        {
            claims.Add(new Claim(NhAiDelegatedReadDefaults.ActionClaimType, actionToolId));
        }
        var request = new NhAiDelegatedReadRequest(
            person,
            scope.Subject.DivisionId?.ToString(),
            invocationId,
            options.Lifetime,
            claims)
        {
            TenantId = scope.Subject.TenantId
        };
        TaskResult<NhAiDelegatedReadCredential> issued;
        try
        {
            issued = await issuer.IssueAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Issuers read identity data; their messages may carry it. Log the type only.
            LogIssueFailed(logger, scope.AgentId, exception.GetType().Name);
            diagnostics.Report(NhAiDelegatedReadFailureCodes.IssuerRefused, scope.AgentId);
            return Refused(NhAiDelegatedReadFailureCodes.IssuerRefused);
        }

        if (!issued.Success || issued.Data is null || !IsBound(issued.Data.BearerToken, invocationId, actionToolId))
        {
            LogIssueRefused(logger, scope.AgentId);
            diagnostics.Report(NhAiDelegatedReadFailureCodes.IssuerRefused, scope.AgentId);
            return Refused(NhAiDelegatedReadFailureCodes.IssuerRefused);
        }
        return issued;
    }

    /// <summary>
    /// The principal of the scope's reading person as the host's scheme authenticates a credential,
    /// including claims transformation, or no principal with the code of the refusal when none can be
    /// issued or the scheme rejects it (<see cref="NhAiDelegatedReadFailureCodes.Rejected"/>). Cached
    /// for the run within this service scope.
    /// </summary>
    public async ValueTask<NhAiDelegatedReadAuthentication> AuthenticateAsync(
        NhAiAgentInvocationScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var key = scope.GatewayReadActorId + "|" + scope.RunId;
        if (_principals.TryGetValue(key, out var cached))
        {
            return cached;
        }

        NhAiDelegatedReadAuthentication authentication;
        // A credential bound to no action: the restriction accepts it only for this authentication.
        var credential = await IssueAsync(scope, Guid.NewGuid(), null, cancellationToken);
        if (!credential.Success)
        {
            authentication = new NhAiDelegatedReadAuthentication(null, FailureCodeOf(credential));
        }
        else
        {
            // Authentication handlers and their results are cached per service scope; a scope of its
            // own keeps every authentication apart from the run's and from each other.
            await using var authenticationScope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var httpContext = new DefaultHttpContext { RequestServices = authenticationScope.ServiceProvider };
            httpContext.Items[InternalAuthentication] = InternalAuthentication;
            httpContext.Request.Method = HttpMethods.Get;
            httpContext.Request.Headers.Authorization = "Bearer " + credential.Data!.BearerToken;
            var result = await httpContext.AuthenticateAsync(options.Scheme);
            if (result.Succeeded && result.Principal is { } principal)
            {
                authentication = new NhAiDelegatedReadAuthentication(principal, null);
            }
            else
            {
                LogAuthenticationRefused(logger, scope.AgentId);
                diagnostics.Report(NhAiDelegatedReadFailureCodes.Rejected, scope.AgentId);
                authentication = new NhAiDelegatedReadAuthentication(null, NhAiDelegatedReadFailureCodes.Rejected);
            }
        }

        _principals[key] = authentication;
        return authentication;
    }

    /// <summary>The code of a failed <see cref="IssueAsync"/>: its cause, or <see cref="UnavailableCode"/>.</summary>
    public static string FailureCodeOf(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => NhAiDelegatedReadFailureCodes.All.Contains(name, StringComparer.Ordinal))
            ?? UnavailableCode;
    }

    /// <summary>
    /// Whether <paramref name="person"/> may lend rights outside a request. Under
    /// <see cref="NhAiAspNetBuilder.UsePrincipalsFromAccessSource{TSource}"/> only while
    /// <see cref="INhAiPrincipalFactory"/> builds the person's principal from a fresh context; other hosts
    /// leave the decision to their issuer, as the built-in issuer does with the person's sign-in state.
    /// </summary>
    private async ValueTask<bool> MayLendRightsAsync(
        string person,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken)
    {
        if (services.GetService<NhAiAspNetRegistrationState>() is not { AccessSource: not null })
        {
            return true;
        }

        var principals = services.GetRequiredService<INhAiPrincipalFactory>();
        return await principals.CreateAsync(person, subject, cancellationToken) is not null;
    }

    /// <summary>
    /// Whether the token is a JWT that carries exactly the invocation marker and action of this
    /// call and expires within the configured lifetime.
    /// </summary>
    private bool IsBound(string bearerToken, Guid invocationId, string? actionToolId)
    {
        var handler = new JwtSecurityTokenHandler();
        if (string.IsNullOrWhiteSpace(bearerToken) || !handler.CanReadToken(bearerToken))
        {
            return false;
        }

        JwtSecurityToken token;
        try
        {
            token = handler.ReadJwtToken(bearerToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }

        var markers = token.Claims.Where(claim => claim.Type == NhAiDelegatedReadDefaults.ClaimType).Select(claim => claim.Value).ToArray();
        var actions = token.Claims.Where(claim => claim.Type == NhAiDelegatedReadDefaults.ActionClaimType).Select(claim => claim.Value).ToArray();
        return markers.Length == 1
            && string.Equals(markers[0], invocationId.ToString("D"), StringComparison.Ordinal)
            && (actionToolId is null
                ? actions.Length == 0
                : actions.Length == 1 && string.Equals(actions[0], actionToolId, StringComparison.Ordinal))
            && token.Payload.Expiration is not null
            && token.ValidTo <= DateTime.UtcNow + options.Lifetime + ExpiryTolerance;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "A delegated read credential for agent {AgentId} could not be issued: the issuer failed with {ExceptionType}.")]
    private static partial void LogIssueFailed(ILogger logger, string agentId, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The issuer refused a delegated read credential for agent {AgentId}, or issued one without its binding claims or that lives too long.")]
    private static partial void LogIssueRefused(ILogger logger, string agentId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The host's scheme rejected the delegated read credential for agent {AgentId}.")]
    private static partial void LogAuthenticationRefused(ILogger logger, string agentId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "The subject's responsible person changed since the run of agent {AgentId} was queued; no delegated read credential is issued.")]
    private static partial void LogResponsibleChanged(ILogger logger, string agentId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "The access source holds no fresh context of the person agent {AgentId} reads for; no delegated read credential is issued.")]
    private static partial void LogNoFreshContext(ILogger logger, string agentId);
}

/// <summary>
/// Restricts delegated read tokens, and delegated MCP tokens (<see cref="NhAiMcpDelegatedTokenDefaults"/>),
/// in every JWT bearer scheme of the host, so another scheme that trusts the same key cannot accept
/// them either. The scheme's events are wrapped in
/// <see cref="NhAiRestrictedJwtBearerEvents"/>, which applies the restriction before any handler of
/// the host, including an override of <see cref="JwtBearerEvents.TokenValidated"/> in an events
/// subclass.
/// </summary>
internal sealed class NhAiDelegatedReadRestriction : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions jwt)
    {
        if (jwt.Events is NhAiRestrictedJwtBearerEvents)
        {
            return;
        }
        jwt.Events = new NhAiRestrictedJwtBearerEvents(jwt.Events ?? new JwtBearerEvents());
    }

    /// <summary>
    /// Whether the validated token may authenticate the request. A token with the
    /// <see cref="NhAiDelegatedReadDefaults.ClaimType"/> claim authenticates only a <c>GET</c> request
    /// to the gateway action named by its <see cref="NhAiDelegatedReadDefaults.ActionClaimType"/>
    /// claim with the matching invocation header, or the library's own authentication of it. Every
    /// other request with such a token, including one whose target is not yet known
    /// (<see cref="NhAiDelegatedReadService.Target"/>), fails. When a bridge call with its own
    /// invocation header fails for want of a target, and the request ends with <c>401</c>, the
    /// diagnostics name the cause (<see cref="NhAiDelegatedReadFailureCodes.TargetUnknown"/>). Tokens
    /// without the claim are not affected.
    /// </summary>
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

        var header = httpContext.Request.Headers[NhAiDelegatedReadDefaults.InvocationHeaderName];
        if (!HttpMethods.IsGet(httpContext.Request.Method)
            || header.Count != 1
            || !Guid.TryParse(header[0], out var headerInvocationId)
            || headerInvocationId != invocationId)
        {
            return false;
        }

        if (NhAiDelegatedReadService.Target(httpContext) is not { } target)
        {
            // The bridge's own call, authenticated before the host knew where it goes.
            NhAiDelegatedReadDiagnostics.WatchUnknownTarget(httpContext, invocationId);
            return false;
        }
        return Reaches(context.Principal!, target, httpContext.RequestServices);
    }

    /// <summary>
    /// Whether the principal may reach <paramref name="target"/>: every identity with the
    /// <see cref="NhAiDelegatedReadDefaults.ClaimType"/> claim needs a single action claim that names
    /// the target as its gateway read. Identities without the claim are not affected, so a principal
    /// that authorization merged from several schemes is checked per delegated read token.
    /// </summary>
    internal static bool Reaches(ClaimsPrincipal principal, Endpoint target, IServiceProvider services)
    {
        foreach (var identity in principal.Identities)
        {
            if (!identity.HasClaim(claim => claim.Type == NhAiDelegatedReadDefaults.ClaimType))
            {
                continue;
            }

            var actions = identity.FindAll(NhAiDelegatedReadDefaults.ActionClaimType).ToArray();
            if (actions.Length != 1
                || !services.GetServices<INhAiDelegatedReadSurface>().Any(surface => surface.IsReadEndpoint(target, actions[0].Value)))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// The events of a JWT bearer scheme with the delegated read restriction applied first. The host's
/// own events keep working: their methods, also overridden ones, are the wrapper's delegates, and a
/// handler composed onto those delegates later still runs after the restriction.
/// </summary>
internal sealed class NhAiRestrictedJwtBearerEvents : JwtBearerEvents
{
    public NhAiRestrictedJwtBearerEvents(JwtBearerEvents inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
        OnAuthenticationFailed = inner.AuthenticationFailed;
        OnChallenge = inner.Challenge;
        OnForbidden = inner.Forbidden;
        OnMessageReceived = inner.MessageReceived;
        OnTokenValidated = inner.TokenValidated;
    }

    public JwtBearerEvents Inner { get; }

    public override Task TokenValidated(TokenValidatedContext context)
    {
        if (!NhAiDelegatedReadRestriction.Allows(context))
        {
            context.Fail("The delegated read credential is not valid for this request.");
            return Task.CompletedTask;
        }
        if (!NhAiMcpDelegatedTokenRestriction.Allows(context))
        {
            context.Fail("The delegated MCP token is not valid for this request.");
            return Task.CompletedTask;
        }
        return base.TokenValidated(context);
    }
}

/// <summary>
/// Fails startup when the delegated read scheme is not a JWT bearer scheme, or when any JWT bearer
/// scheme resolves its events from a type or replaced the restricted events after they were
/// composed: such a scheme could accept a delegated read token without the restriction.
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

        foreach (var jwtScheme in (await schemes.GetAllSchemesAsync())
            .Where(candidate => typeof(JwtBearerHandler).IsAssignableFrom(candidate.HandlerType)))
        {
            var jwt = jwtOptions.Get(jwtScheme.Name);
            if (jwt.EventsType is not null)
            {
                throw new InvalidOperationException(
                    $"The JWT bearer scheme '{jwtScheme.Name}' resolves its events from a type, so the delegated read restriction cannot be applied to it; set Events instead of EventsType.");
            }
            if (jwt.Events is not NhAiRestrictedJwtBearerEvents)
            {
                throw new InvalidOperationException(
                    $"The events of the JWT bearer scheme '{jwtScheme.Name}' were replaced after AddNewHeapPlatformAIDelegatedReads; configure them in the scheme's own configuration instead.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// How the library's own authentication of a delegated read credential ended: the reading person's
/// principal, or none with the code of the refusal (a cause of <see cref="NhAiDelegatedReadFailureCodes"/>
/// or <see cref="NhAiDelegatedReadService.UnavailableCode"/>).
/// </summary>
internal readonly record struct NhAiDelegatedReadAuthentication(ClaimsPrincipal? Principal, string? FailureCode);

/// <summary>
/// Names why gateway reads fail, without content: one warning per cause and host that names the fix,
/// and the cause of a bridge call that the host refused for want of a target, which the call's
/// <c>401</c> cannot say. Warnings use the category of <see cref="NhAiDelegatedReadService"/>, next to
/// the warnings of each occurrence.
/// </summary>
internal sealed partial class NhAiDelegatedReadDiagnostics(ILogger<NhAiDelegatedReadService> logger)
{
    private const int MaxRefusals = 256;

    private static readonly object Watched = new();
    private static readonly long RefusalLifetime = (long)(NhAiDelegatedReadDefaults.MaxLifetime + TimeSpan.FromMinutes(1)).TotalMilliseconds;

    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, (string Cause, long RecordedAt)> _refusals = new();

    /// <summary>
    /// Logs the warning of <paramref name="cause"/> the first time this host meets it; later
    /// occurrences log nothing here. <paramref name="agentId"/> is the agent that met it first.
    /// </summary>
    public void Report(string cause, string? agentId)
    {
        if (!_reported.TryAdd(cause, 0))
        {
            return;
        }

        var agent = agentId ?? "(unknown)";
        switch (cause)
        {
            case NhAiDelegatedReadFailureCodes.TargetUnknown:
                LogTargetUnknown(logger);
                break;
            case NhAiDelegatedReadFailureCodes.NoIssuer:
                LogNoIssuer(logger, agent);
                break;
            case NhAiDelegatedReadFailureCodes.IssuerRefused:
                LogIssuerRefused(logger, agent);
                break;
            case NhAiDelegatedReadFailureCodes.Rejected:
                LogRejected(logger, agent);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, "Not a cause of NhAiDelegatedReadFailureCodes.");
        }
    }

    /// <summary>
    /// The cause the host recorded for the bridge call <paramref name="invocationId"/> when it refused
    /// its credential, once; <see langword="null"/> when this host recorded none, for example because
    /// another instance answered the call.
    /// </summary>
    public string? TakeRefusal(Guid invocationId)
    {
        return _refusals.TryRemove(invocationId, out var refusal) ? refusal.Cause : null;
    }

    /// <summary>
    /// Watches a bridge call whose credential the restriction refused because its target was not known
    /// yet: when the request ends with <c>401</c>, the host records the cause for the call and warns
    /// once. A request that another scheme still authorizes warns about nothing.
    /// </summary>
    public static void WatchUnknownTarget(HttpContext httpContext, Guid invocationId)
    {
        if (httpContext.Response.HasStarted
            || httpContext.Items.ContainsKey(Watched)
            || httpContext.RequestServices?.GetService<NhAiDelegatedReadDiagnostics>() is not { } diagnostics)
        {
            return;
        }

        httpContext.Items[Watched] = Watched;
        httpContext.Response.OnStarting(() =>
        {
            if (httpContext.Response.StatusCode == StatusCodes.Status401Unauthorized)
            {
                diagnostics.Record(invocationId, NhAiDelegatedReadFailureCodes.TargetUnknown);
                diagnostics.Report(NhAiDelegatedReadFailureCodes.TargetUnknown, null);
            }
            return Task.CompletedTask;
        });
    }

    private void Record(Guid invocationId, string cause)
    {
        var now = Environment.TickCount64;
        if (_refusals.Count >= MaxRefusals)
        {
            // A call answered by this host is taken at once; what stays is another instance's call.
            foreach (var stale in _refusals.Where(entry => now - entry.Value.RecordedAt > RefusalLifetime).ToArray())
            {
                _refusals.TryRemove(stale.Key, out _);
            }
            if (_refusals.Count >= MaxRefusals)
            {
                _refusals.Clear();
            }
        }
        _refusals[invocationId] = (cause, now);
    }

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "A delegated read credential reached a request whose target was not known yet, so it was refused (ai-delegated-read-target-unknown): the host authenticates before routing. Call UseRouting before UseAuthentication; with UseNewHeapPlatformAspNetCommon, route controllers with UseEndpointRoutingOnly(). A host without endpoint routing lets the API bridge's MVC filter authenticate the delegated read scheme with the executing action as its target (AuthenticateForTargetAsync) and must not authenticate that scheme in middleware first, for example as the default authentication scheme.")]
    private static partial void LogTargetUnknown(ILogger logger);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Agent {AgentId} allows gateway reads, but the host issues no delegated read credentials (ai-delegated-read-no-issuer). Call AddNewHeapPlatformAIDelegatedReads; a host without the NewHeap authentication service (INhAuthenticationService) registers an issuer of its own with UseIssuer<T>().")]
    private static partial void LogNoIssuer(ILogger logger, string agentId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "The delegated read issuer gave agent {AgentId} no usable credential (ai-delegated-read-issuer-refused): it refused the person the run reads for, failed, or issued a token without exactly the claims of NhAiDelegatedReadRequest.Claims or that lives longer than NhAiDelegatedReadRequest.Lifetime. Check that the person may still sign in and that the issuer binds its tokens; events 1 and 2 name each occurrence.")]
    private static partial void LogIssuerRefused(ILogger logger, string agentId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "The host rejected the delegated read credential of agent {AgentId} (ai-delegated-read-rejected): its JWT bearer scheme did not accept the token, or the API answered a gateway read with 401. Make the delegated read scheme (UseScheme, Bearer by default) validate the issuer's tokens, with their issuer, audience and signing key, and let the self base URL reach this API with the X-NewHeap-AI-Invocation header unchanged. Where the API authenticated before routing, it logs ai-delegated-read-target-unknown.")]
    private static partial void LogRejected(ILogger logger, string agentId);
}
