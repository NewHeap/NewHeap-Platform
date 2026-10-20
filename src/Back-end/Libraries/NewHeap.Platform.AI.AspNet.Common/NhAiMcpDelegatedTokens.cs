using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// Claims and limits of a delegated NewHeap token that an assistant sends to a first-party MCP
/// server for a person in a headless run. Such a token is signed with the host's own key, so only
/// hosts that share that key can validate it; it is meant for hosts in the same trust domain.
/// </summary>
public static class NhAiMcpDelegatedTokenDefaults
{
    /// <summary>
    /// The claim that marks a delegated MCP token; its value names the invocation it is for, or the
    /// connection when no tool call runs.
    /// </summary>
    public const string MarkerClaimType = "nh-ai-mcp-delegated";

    /// <summary>
    /// The claim that binds the token to one server: the server's origin (<see cref="AudienceOf"/>).
    /// NewHeap tokens set <c>aud</c> to their issuer, so a second <c>aud</c> value would still let the
    /// issuing host accept the token; the binding is this dedicated claim instead.
    /// </summary>
    public const string AudienceClaimType = "nh-ai-mcp-audience";

    /// <summary>
    /// The stable code when no token can be issued, for example for a person who may not sign in.
    /// </summary>
    public const string UnavailableCode = "ai-mcp-delegated-token-unavailable";

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The audience of a server URL: its lowercase scheme and host, with the port only when it is not
    /// the scheme's default, for example <c>https://planning.example.org</c>.
    /// </summary>
    public static string AudienceOf(Uri serverUrl)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        if (!serverUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The server URL must be absolute.", nameof(serverUrl));
        }
        var origin = serverUrl.Scheme.ToLowerInvariant() + "://" + serverUrl.IdnHost.TrimEnd('.').ToLowerInvariant();
        return serverUrl.IsDefaultPort ? origin : origin + ":" + serverUrl.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A request for a delegated MCP token for the person a headless run acts for.
/// </summary>
/// <param name="PersonActorId">The person the token authenticates.</param>
/// <param name="Audience">The receiving server's origin (<see cref="NhAiMcpDelegatedTokenDefaults.AudienceOf"/>).</param>
/// <param name="Marker">The value of the marker claim: the invocation id, or a connection id.</param>
/// <param name="Lifetime">At most <see cref="NhAiMcpDelegatedTokenDefaults.MaxLifetime"/>.</param>
public sealed record NhAiMcpDelegatedTokenRequest(
    string PersonActorId,
    string Audience,
    string Marker,
    TimeSpan Lifetime)
{
    /// <summary>The run's tenant, for hosts that bind tokens to a tenant.</summary>
    public string? TenantId { get; init; }
}

/// <summary>
/// A delegated MCP token. It is held for one request only and never stored, logged or audited.
/// </summary>
public sealed record NhAiMcpDelegatedToken(string BearerToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues delegated MCP tokens. The token must be a JWT that carries exactly one claim of
/// <see cref="NhAiMcpDelegatedTokenDefaults.MarkerClaimType"/> and of
/// <see cref="NhAiMcpDelegatedTokenDefaults.AudienceClaimType"/> with the requested values and lives
/// no longer than requested; the assistant never sends a token without them. Without a custom issuer
/// the NewHeap authentication service issues it, and only while the person may sign in.
/// </summary>
public interface INhAiMcpDelegatedTokenIssuer
{
    ValueTask<TaskResult<NhAiMcpDelegatedToken>> IssueAsync(
        NhAiMcpDelegatedTokenRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Configures delegated MCP tokens on a host: which of them it accepts and who issues them.
/// </summary>
public sealed class NhAiMcpDelegatedTokensBuilder
{
    private readonly NhAiMcpDelegatedTokenOptions _options;

    internal NhAiMcpDelegatedTokensBuilder(NhAiMcpDelegatedTokenOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Accepts delegated tokens whose audience is <paramref name="audience"/>, this host's own origin
    /// as the issuing assistant names it, for requests below <paramref name="path"/> only, typically
    /// the MCP endpoint. Every other request with such a token fails authentication.
    /// </summary>
    public NhAiMcpDelegatedTokensBuilder Accept(string audience, string path = "/mcp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Uri.TryCreate(audience, UriKind.Absolute, out var uri) || uri.PathAndQuery is not ("/" or ""))
        {
            throw new ArgumentException("The audience is an origin such as https://planning.example.org.", nameof(audience));
        }
        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("The path starts with '/'.", nameof(path));
        }
        _options.Accepted.Add(new NhAiMcpDelegatedTokenAcceptance(NhAiMcpDelegatedTokenDefaults.AudienceOf(uri), new PathString(path)));
        return this;
    }

    /// <summary>
    /// Issues tokens with <typeparamref name="TIssuer"/> instead of the NewHeap authentication service.
    /// </summary>
    public NhAiMcpDelegatedTokensBuilder UseIssuer<TIssuer>()
        where TIssuer : class, INhAiMcpDelegatedTokenIssuer
    {
        _options.IssuerType = typeof(TIssuer);
        return this;
    }
}

internal sealed record NhAiMcpDelegatedTokenAcceptance(string Audience, PathString Path);

internal sealed class NhAiMcpDelegatedTokenOptions
{
    public List<NhAiMcpDelegatedTokenAcceptance> Accepted { get; } = [];

    public Type IssuerType { get; set; } = typeof(NhAiNewHeapMcpDelegatedTokenIssuer);

    public bool Accepts(string audience, PathString path)
    {
        return Accepted.Any(acceptance =>
            string.Equals(acceptance.Audience, audience, StringComparison.Ordinal)
            && path.StartsWithSegments(acceptance.Path, StringComparison.OrdinalIgnoreCase));
    }
}

public static class NhAiMcpDelegatedTokenServiceCollectionExtensions
{
    /// <summary>
    /// Restricts delegated MCP tokens in every JWT bearer scheme of the host: a token with the
    /// <see cref="NhAiMcpDelegatedTokenDefaults.MarkerClaimType"/> claim authenticates only a request
    /// below a path that <see cref="NhAiMcpDelegatedTokensBuilder.Accept"/> named for its audience.
    /// Without <c>Accept</c> the host accepts no such token, which is what an issuing assistant needs.
    /// May be called more than once; later calls add accepted audiences.
    /// </summary>
    public static IServiceCollection AddNewHeapPlatformAIMcpDelegatedTokens(
        this IServiceCollection services,
        Action<NhAiMcpDelegatedTokensBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services
            .Where(descriptor => descriptor.ServiceType == typeof(NhAiMcpDelegatedTokenOptions))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<NhAiMcpDelegatedTokenOptions>()
            .FirstOrDefault();
        if (options is null)
        {
            options = new NhAiMcpDelegatedTokenOptions();
            services.AddSingleton(options);
        }
        configure?.Invoke(new NhAiMcpDelegatedTokensBuilder(options));

        services.AddHttpContextAccessor();
        // The issuer type can change with a later call, so it is read when the issuer is resolved.
        services.TryAddScoped<INhAiMcpDelegatedTokenIssuer>(provider =>
            (INhAiMcpDelegatedTokenIssuer)ActivatorUtilities.CreateInstance(
                provider,
                provider.GetRequiredService<NhAiMcpDelegatedTokenOptions>().IssuerType));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<JwtBearerOptions>, NhAiDelegatedReadRestriction>());
        return services;
    }

    /// <summary>
    /// Whether every JWT bearer scheme of the host applies the restriction. An issuing assistant
    /// mints no delegated token otherwise, because the host itself could then accept it as the person
    /// on any endpoint.
    /// </summary>
    public static async ValueTask<bool> IsDelegatedTokenRestrictionEnforcedAsync(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var schemes = services.GetService<IAuthenticationSchemeProvider>();
        if (schemes is null)
        {
            return true;
        }
        var jwtOptions = services.GetService<IOptionsMonitor<JwtBearerOptions>>();
        foreach (var scheme in (await schemes.GetAllSchemesAsync())
            .Where(candidate => typeof(JwtBearerHandler).IsAssignableFrom(candidate.HandlerType)))
        {
            var jwt = jwtOptions?.Get(scheme.Name);
            if (jwt is null || jwt.EventsType is not null || jwt.Events is not NhAiRestrictedJwtBearerEvents)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// The delegated MCP token rule applied in every JWT bearer scheme, next to the delegated read rule.
/// </summary>
internal static class NhAiMcpDelegatedTokenRestriction
{
    /// <summary>
    /// Whether the validated token may authenticate the request. Tokens without the marker claim are
    /// not affected; a token with it needs exactly one marker and one audience, an accepted audience
    /// and a request below that audience's path.
    /// </summary>
    public static bool Allows(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var markers = principal?.FindAll(NhAiMcpDelegatedTokenDefaults.MarkerClaimType).ToArray() ?? [];
        if (markers.Length == 0)
        {
            return true;
        }
        var audiences = principal!.FindAll(NhAiMcpDelegatedTokenDefaults.AudienceClaimType).ToArray();
        if (markers.Length != 1 || audiences.Length != 1 || string.IsNullOrWhiteSpace(markers[0].Value))
        {
            return false;
        }
        var options = context.HttpContext.RequestServices.GetService<NhAiMcpDelegatedTokenOptions>();
        return options is not null && options.Accepts(audiences[0].Value, context.HttpContext.Request.Path);
    }
}

/// <summary>
/// The built-in issuer for hosts on the NewHeap identity: the token carries the person's own claims
/// and security stamp, and is issued only while the person may sign in. A person who is no NewHeap
/// user or may not sign in gets no token (<see cref="NhAiMcpDelegatedTokenDefaults.UnavailableCode"/>,
/// followed by the authentication service's own reason); a host without the NewHeap authentication
/// service, or a lifetime outside (0, <see cref="NhAiMcpDelegatedTokenDefaults.MaxLifetime"/>], is a
/// configuration or programming error and throws.
/// </summary>
internal sealed class NhAiNewHeapMcpDelegatedTokenIssuer(IServiceProvider services) : INhAiMcpDelegatedTokenIssuer
{
    public async ValueTask<TaskResult<NhAiMcpDelegatedToken>> IssueAsync(
        NhAiMcpDelegatedTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lifetime <= TimeSpan.Zero || request.Lifetime > NhAiMcpDelegatedTokenDefaults.MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Lifetime,
                "A delegated MCP token lives longer than zero and at most five minutes.");
        }
        var authentication = services.GetService<INhAuthenticationService>()
            ?? throw new InvalidOperationException(
                "The built-in delegated MCP token issuer needs the NewHeap authentication service (INhAuthenticationService); register it or a custom issuer with UseIssuer.");
        if (!Guid.TryParse(request.PersonActorId, out var userId))
        {
            // Only a NewHeap user has a token of their own.
            return Unavailable();
        }

        Claim[] claims =
        [
            new(NhAiMcpDelegatedTokenDefaults.MarkerClaimType, request.Marker),
            new(NhAiMcpDelegatedTokenDefaults.AudienceClaimType, request.Audience)
        ];
        var created = await authentication.CreateDelegatedTokenAsync(userId, claims, request.Lifetime, cancellationToken);
        if (!created.Success)
        {
            // The person may not sign in: the authentication service's reason follows the code.
            var refused = Unavailable();
            refused.ApplyModelStateErrors(created.GetResultItems()
                .Where(item => !string.IsNullOrEmpty(item.Name))
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.SelectMany(item => item.ErrorMessages).ToArray(), StringComparer.Ordinal));
            return refused;
        }
        if (created.Data is null)
        {
            throw new InvalidOperationException("The authentication service reported a delegated token without returning it.");
        }
        return TaskResult<NhAiMcpDelegatedToken>.Succeeded(new NhAiMcpDelegatedToken(
            new JwtSecurityTokenHandler().WriteToken(created.Data),
            new DateTimeOffset(DateTime.SpecifyKind(created.Data.ValidTo, DateTimeKind.Utc))));
    }

    private static TaskResult<NhAiMcpDelegatedToken> Unavailable()
    {
        return TaskResult<NhAiMcpDelegatedToken>.Failed(
            NhAiMcpDelegatedTokenDefaults.UnavailableCode,
            "No delegated MCP token can be issued for this person.");
    }
}
