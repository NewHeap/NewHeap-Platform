using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// Names of the protected resource that <see cref="NhAiMcpExposureBuilder.UseProtectedResource"/> makes
/// of the MCP exposure.
/// </summary>
public static class NhAiMcpProtectedResourceDefaults
{
    /// <summary>
    /// The authentication scheme that validates access tokens for the MCP exposure, answers its challenge
    /// and serves its protected resource metadata.
    /// </summary>
    public const string AuthenticationScheme = "NewHeap.AI.McpProtectedResource";

    /// <summary>
    /// The authorization policy of the MCP endpoint: a person signed in with an access token for the
    /// resource, or with one of <see cref="NhAiMcpProtectedResourceOptions.AcceptHostSchemes"/>. Map the
    /// endpoint with <c>MapMcp(...).RequireAuthorization(AuthorizationPolicy)</c>.
    /// </summary>
    public const string AuthorizationPolicy = "NewHeap.AI.McpProtectedResource";

    /// <summary>
    /// The named <see cref="HttpClient"/> that reads the authorization servers' metadata and keys.
    /// </summary>
    public const string HttpClientName = "newheap-ai-mcp-protected-resource";

    /// <summary>The well-known path of protected resource metadata (RFC 9728).</summary>
    public const string MetadataPath = "/.well-known/oauth-protected-resource";

    /// <summary>The only way the resource accepts a token: the <c>Authorization: Bearer</c> header.</summary>
    public const string BearerMethod = "header";

    /// <summary>The challenge's error when the request sent a token that the resource does not accept.</summary>
    public const string InvalidTokenError = "invalid_token";

    public static readonly TimeSpan DefaultClockSkew = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The address of a resource's metadata (RFC 9728, section 3.1): <see cref="MetadataPath"/> between
    /// the origin and the path of the resource, for example
    /// <c>https://gateway.example.org/.well-known/oauth-protected-resource/planning/mcp</c> for
    /// <c>https://gateway.example.org/planning/mcp</c>.
    /// </summary>
    public static string MetadataAddressOf(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        var canonical = NhAiMcpProtectedResource.Canonical(resource, nameof(resource));
        var uri = new Uri(canonical, UriKind.Absolute);
        return uri.GetLeftPart(UriPartial.Authority) + MetadataPath + uri.AbsolutePath.TrimEnd('/');
    }
}

/// <summary>
/// The MCP exposure as an OAuth protected resource (RFC 9728) of the host's authorization servers, so
/// that an MCP client that follows the MCP authorization flow finds the authorization server from the
/// endpoint, obtains a token for this resource there and signs in. The authorization server, the
/// registration of clients and the consent stay the host's; NewHeap only serves the metadata, answers
/// the challenge and accepts tokens whose audience is <see cref="Resource"/> (RFC 8707).
/// </summary>
public sealed class NhAiMcpProtectedResourceOptions
{
    /// <summary>
    /// The public URL of the MCP endpoint as clients reach it, for example
    /// <c>https://planning.example.org/mcp</c>: the resource indicator that tokens name as their audience
    /// and that the metadata names as <c>resource</c>. It may differ from the application's own route
    /// behind a reverse proxy; https is required except for a loopback host, and it has no query or
    /// fragment. A trailing slash and the case of the scheme and host do not matter.
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>
    /// The issuer identifiers of the authorization servers whose access tokens the resource accepts,
    /// exactly as each server's metadata names its <c>issuer</c>. At least one; https except for a
    /// loopback host. The resource reads each server's metadata (RFC 8414, otherwise OpenID Connect
    /// discovery) and validates tokens with the keys of its <c>jwks_uri</c>.
    /// </summary>
    public IList<string> AuthorizationServers { get; set; } = [];

    /// <summary>
    /// The scopes a client requests for the resource: published as <c>scopes_supported</c> and named in
    /// the challenge. The resource does not require them of a token; map a scope to a capability with
    /// <c>AddScopeCapability("scope", value, capability)</c> when a tool needs it.
    /// </summary>
    public IList<string> ScopesSupported { get; set; } = [];

    /// <summary>An optional human-readable name of the resource (<c>resource_name</c>).</summary>
    public string? ResourceName { get; set; }

    /// <summary>
    /// Authentication schemes of the host that also sign a request in at the MCP endpoint, for example the
    /// application's own bearer scheme for its own clients. Only a request without a token of one of
    /// <see cref="AuthorizationServers"/> reaches them; a token of those servers is accepted for this
    /// resource only and never handed to another scheme.
    /// </summary>
    public IList<string> AcceptHostSchemes { get; set; } = [];

    /// <summary>The tolerance for the token's lifetime; zero up to five minutes.</summary>
    public TimeSpan ClockSkew { get; set; } = NhAiMcpProtectedResourceDefaults.DefaultClockSkew;

    /// <summary>
    /// Called after a token for the resource was validated, before the request is signed in: the host
    /// turns the token's principal into the application's person, for example the account linked to the
    /// issuer and subject, or refuses it with
    /// <see cref="NhAiMcpProtectedResourceTokenValidatedContext.Fail"/>. Without it the request is signed in
    /// with the token's claims (<c>iss</c>, <c>sub</c>, <c>scope</c>, <c>client_id</c>, ...), for example
    /// for <c>UseAuthenticatedClaimsWithoutTenant(issuer)</c>.
    /// </summary>
    public Func<NhAiMcpProtectedResourceTokenValidatedContext, Task>? OnTokenValidated { get; set; }
}

/// <summary>
/// A validated access token for the MCP exposure, before the request is signed in with it. The token
/// itself is not part of the context: it serves this resource only.
/// </summary>
public sealed class NhAiMcpProtectedResourceTokenValidatedContext
{
    internal NhAiMcpProtectedResourceTokenValidatedContext(HttpContext httpContext, string issuer, ClaimsPrincipal principal)
    {
        HttpContext = httpContext;
        Issuer = issuer;
        Subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        Principal = principal;
    }

    public HttpContext HttpContext { get; }

    /// <summary>The authorization server that issued the token, one of the configured servers.</summary>
    public string Issuer { get; }

    /// <summary>The token's subject (<c>sub</c>), or null.</summary>
    public string? Subject { get; }

    /// <summary>
    /// The principal the request is signed in with; replace it with the application's person. It must
    /// stay authenticated, otherwise the request is refused.
    /// </summary>
    public ClaimsPrincipal Principal { get; set; }

    /// <summary>Why the request is refused, after <see cref="Fail"/>.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    /// Refuses the token: the request is answered with the challenge and <c>invalid_token</c>, for example
    /// for a subject that is linked to no account. The reason is logged, never returned.
    /// </summary>
    public void Fail(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        FailureReason = reason;
    }
}

/// <summary>
/// Configures the MCP exposure of <c>WithNewHeapPlatformAITools</c>.
/// </summary>
public sealed class NhAiMcpExposureBuilder
{
    private readonly IServiceCollection _services;

    internal NhAiMcpExposureBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Makes the MCP exposure a protected resource of the host's authorization servers, so that any MCP
    /// client that follows the MCP authorization flow signs in:
    /// <list type="bullet">
    /// <item>the protected resource metadata (RFC 9728) is served at the well-known address of
    /// <see cref="NhAiMcpProtectedResourceOptions.Resource"/> and at the root well-known address, with
    /// <c>resource</c>, <c>authorization_servers</c>, <c>scopes_supported</c> and
    /// <c>bearer_methods_supported</c>;</item>
    /// <item>an MCP request without a valid token is answered with <c>401</c> and
    /// <c>WWW-Authenticate: Bearer resource_metadata="...", scope="..."</c>, with
    /// <c>error="invalid_token"</c> when it sent a token;</item>
    /// <item>only access tokens whose audience is the resource are accepted (RFC 8707), and such a token is
    /// never passed on: <see cref="INhAiCallerCredentialAccessor"/> returns none for the request.</item>
    /// </list>
    /// Map the MCP endpoint with
    /// <c>RequireAuthorization(NhAiMcpProtectedResourceDefaults.AuthorizationPolicy)</c>. Once per host.
    /// </summary>
    /// <exception cref="InvalidOperationException">An option is invalid, or the exposure is a protected
    /// resource already.</exception>
    public NhAiMcpExposureBuilder UseProtectedResource(Action<NhAiMcpProtectedResourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_services.Any(descriptor => descriptor.ServiceType == typeof(NhAiMcpProtectedResource)))
        {
            throw new InvalidOperationException(
                "The MCP exposure is a protected resource already; call UseProtectedResource once per host.");
        }

        var options = new NhAiMcpProtectedResourceOptions();
        configure(options);
        var resource = NhAiMcpProtectedResource.Create(options);

        _services.AddSingleton(resource);
        _services.AddHttpClient(
            NhAiMcpProtectedResourceDefaults.HttpClientName,
            client => client.Timeout = NhAiMcpProtectedResource.MetadataTimeout);
        _services.TryAddSingleton<NhAiMcpAccessTokenValidator>();
        _services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, NhAiMcpProtectedResourceHandler>(
                NhAiMcpProtectedResourceDefaults.AuthenticationScheme,
                displayName: null,
                configureOptions: null);
        _services.AddAuthorization(authorization => authorization.AddPolicy(
            NhAiMcpProtectedResourceDefaults.AuthorizationPolicy,
            policy => policy
                .AddAuthenticationSchemes(NhAiMcpProtectedResourceDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()));
        _services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAiMcpProtectedResourceStartupCheck>());
        return this;
    }
}

/// <summary>
/// The validated settings of the protected resource and the documents it serves.
/// </summary>
internal sealed class NhAiMcpProtectedResource
{
    public static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);

    private const int MaxScopeLength = 256;
    private const int MaxResourceNameLength = 200;

    private NhAiMcpProtectedResource(
        string resource,
        IReadOnlyList<string> authorizationServers,
        IReadOnlyList<string> scopesSupported,
        string? resourceName,
        IReadOnlyList<string> acceptHostSchemes,
        TimeSpan clockSkew,
        Func<NhAiMcpProtectedResourceTokenValidatedContext, Task>? onTokenValidated)
    {
        Resource = resource;
        AuthorizationServers = authorizationServers;
        ScopesSupported = scopesSupported;
        ResourceName = resourceName;
        AcceptHostSchemes = acceptHostSchemes;
        ClockSkew = clockSkew;
        OnTokenValidated = onTokenValidated;

        var uri = new Uri(resource, UriKind.Absolute);
        var resourcePath = uri.AbsolutePath.TrimEnd('/');
        PathMetadata = NhAiMcpProtectedResourceDefaults.MetadataPath + resourcePath;
        MetadataAddress = uri.GetLeftPart(UriPartial.Authority) + PathMetadata;
        MetadataDocument = JsonSerializer.SerializeToUtf8Bytes(
            new ProtectedResourceMetadata
            {
                Resource = resource,
                AuthorizationServers = [.. authorizationServers],
                ScopesSupported = [.. scopesSupported],
                BearerMethodsSupported = [NhAiMcpProtectedResourceDefaults.BearerMethod],
                ResourceName = resourceName
            },
            McpJsonUtilities.DefaultOptions);
    }

    /// <summary>The canonical resource: lowercase scheme and host, no default port, no trailing slash.</summary>
    public string Resource { get; }

    public IReadOnlyList<string> AuthorizationServers { get; }

    public IReadOnlyList<string> ScopesSupported { get; }

    public string? ResourceName { get; }

    public IReadOnlyList<string> AcceptHostSchemes { get; }

    public TimeSpan ClockSkew { get; }

    public Func<NhAiMcpProtectedResourceTokenValidatedContext, Task>? OnTokenValidated { get; }

    /// <summary>The path of the resource's own metadata address on the public origin.</summary>
    public string PathMetadata { get; }

    /// <summary>The public address of the resource's metadata, named by the challenge.</summary>
    public string MetadataAddress { get; }

    public byte[] MetadataDocument { get; }

    public static NhAiMcpProtectedResource Create(NhAiMcpProtectedResourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Resource))
        {
            throw Invalid("Resource is required: the public URL of the MCP endpoint, such as https://planning.example.org/mcp.");
        }

        var resource = Canonical(options.Resource, nameof(options.Resource));
        var authorizationServers = AuthorizationServersOf(options.AuthorizationServers);
        var scopes = ScopesOf(options.ScopesSupported);
        var resourceName = string.IsNullOrWhiteSpace(options.ResourceName) ? null : options.ResourceName.Trim();
        if (resourceName is { Length: > MaxResourceNameLength })
        {
            throw Invalid($"ResourceName has at most {MaxResourceNameLength} characters.");
        }

        var hostSchemes = HostSchemesOf(options.AcceptHostSchemes);
        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew > NhAiMcpProtectedResourceDefaults.MaxClockSkew)
        {
            throw Invalid("ClockSkew is zero up to five minutes.");
        }

        return new NhAiMcpProtectedResource(
            resource,
            authorizationServers,
            scopes,
            resourceName,
            hostSchemes,
            options.ClockSkew,
            options.OnTokenValidated);
    }

    /// <summary>
    /// The canonical form of a resource URL (RFC 8707): lowercase scheme and host, the port only when it
    /// is not the scheme's default, the path without a trailing slash, without query and fragment.
    /// </summary>
    public static string Canonical(string url, string name)
    {
        var uri = AbsoluteUrl(url, name);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw Invalid($"{name} has no query or fragment.");
        }

        var authority = uri.Scheme.ToLowerInvariant() + "://" + uri.IdnHost.TrimEnd('.').ToLowerInvariant()
            + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        return authority + uri.AbsolutePath.TrimEnd('/');
    }

    /// <summary>Whether a request path is one of the addresses of the metadata.</summary>
    public bool IsMetadataPath(PathString path)
    {
        var value = (path.Value ?? string.Empty).TrimEnd('/');
        return string.Equals(value, PathMetadata, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, NhAiMcpProtectedResourceDefaults.MetadataPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether an audience names this resource.</summary>
    public bool IsResource(string? audience)
    {
        if (string.IsNullOrWhiteSpace(audience) || !Uri.TryCreate(audience, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            return false;
        }

        var authority = uri.Scheme.ToLowerInvariant() + "://" + uri.IdnHost.TrimEnd('.').ToLowerInvariant()
            + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        return string.Equals(authority + uri.AbsolutePath.TrimEnd('/'), Resource, StringComparison.Ordinal);
    }

    /// <summary>Whether the resource accepts tokens of <paramref name="issuer"/>.</summary>
    public bool Names(string? issuer)
    {
        return issuer is not null && AuthorizationServers.Contains(issuer, StringComparer.Ordinal);
    }

    /// <summary>
    /// The <c>WWW-Authenticate</c> challenge: the metadata address, the scopes, and <c>invalid_token</c>
    /// when the request sent a token.
    /// </summary>
    public string Challenge(bool tokenSent)
    {
        var challenge = new StringBuilder("Bearer resource_metadata=\"")
            .Append(MetadataAddress)
            .Append('"');
        if (ScopesSupported.Count > 0)
        {
            challenge.Append(", scope=\"").Append(string.Join(' ', ScopesSupported)).Append('"');
        }
        if (tokenSent)
        {
            challenge.Append(", error=\"").Append(NhAiMcpProtectedResourceDefaults.InvalidTokenError).Append('"');
        }

        return challenge.ToString();
    }

    private static IReadOnlyList<string> AuthorizationServersOf(IList<string>? servers)
    {
        var result = new List<string>();
        foreach (var server in servers ?? [])
        {
            if (string.IsNullOrWhiteSpace(server))
            {
                throw Invalid("AuthorizationServers has no empty issuer.");
            }

            var issuer = server.Trim();
            var uri = AbsoluteUrl(issuer, nameof(NhAiMcpProtectedResourceOptions.AuthorizationServers));
            if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw Invalid("An issuer of AuthorizationServers has no query or fragment.");
            }
            if (!result.Contains(issuer, StringComparer.Ordinal))
            {
                result.Add(issuer);
            }
        }

        if (result.Count == 0)
        {
            throw Invalid("AuthorizationServers names at least one issuer, exactly as its metadata names it.");
        }

        return result;
    }

    private static IReadOnlyList<string> ScopesOf(IList<string>? scopes)
    {
        var result = new List<string>();
        foreach (var scope in scopes ?? [])
        {
            // RFC 6749: a scope token is printable ASCII without space, quote and backslash.
            if (string.IsNullOrEmpty(scope)
                || scope.Length > MaxScopeLength
                || !scope.All(character => character >= 0x21 && character <= 0x7E && character != '"' && character != '\\'))
            {
                throw Invalid("ScopesSupported holds scope tokens: printable ASCII without space, quote or backslash.");
            }
            if (!result.Contains(scope, StringComparer.Ordinal))
            {
                result.Add(scope);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> HostSchemesOf(IList<string>? schemes)
    {
        var result = new List<string>();
        foreach (var scheme in schemes ?? [])
        {
            if (string.IsNullOrWhiteSpace(scheme))
            {
                throw Invalid("AcceptHostSchemes has no empty scheme name.");
            }
            if (string.Equals(scheme, NhAiMcpProtectedResourceDefaults.AuthenticationScheme, StringComparison.Ordinal))
            {
                throw Invalid("AcceptHostSchemes names the host's own schemes, not the protected resource's.");
            }
            if (!result.Contains(scheme, StringComparer.Ordinal))
            {
                result.Add(scheme);
            }
        }

        return result;
    }

    private static Uri AbsoluteUrl(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0)
        {
            throw Invalid($"{name} is an absolute https URL without credentials.");
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
        {
            throw Invalid($"{name} uses https; plain http is only accepted for a loopback host.");
        }

        return uri;
    }

    private static bool IsLoopback(Uri uri)
    {
        return uri.IsLoopback
            || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException Invalid(string message)
    {
        return new InvalidOperationException("NhAiMcpProtectedResourceOptions." + message);
    }
}

/// <summary>
/// Marks a request that signed in with an access token for the MCP exposure. That token is for the
/// resource only, so nothing passes it on.
/// </summary>
internal sealed class NhAiMcpProtectedResourceSignIn(string issuer)
{
    public string Issuer { get; } = issuer;

    public static bool Applies(HttpContext httpContext)
    {
        return httpContext.Features.Get<NhAiMcpProtectedResourceSignIn>() is not null;
    }
}

/// <summary>
/// Serves the metadata, validates tokens for the resource and answers the challenge. It authenticates
/// only on endpoints that require <see cref="NhAiMcpProtectedResourceDefaults.AuthorizationPolicy"/>, so a
/// token for the MCP exposure never signs in a request elsewhere on the host.
/// </summary>
internal sealed partial class NhAiMcpProtectedResourceHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    NhAiMcpProtectedResource resource,
    NhAiMcpAccessTokenValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder), IAuthenticationRequestHandler
{
    private const string BearerPrefix = "Bearer ";

    public async Task<bool> HandleRequestAsync()
    {
        if (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method))
        {
            return false;
        }
        if (!resource.IsMetadataPath(Request.PathBase.Add(Request.Path)))
        {
            return false;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/json";
        Response.ContentLength = resource.MetadataDocument.Length;
        // The document is public and carries no credentials, so a browser-based client may read it.
        if (!Response.Headers.ContainsKey(HeaderNames.AccessControlAllowOrigin))
        {
            Response.Headers.AccessControlAllowOrigin = "*";
        }
        if (HttpMethods.IsGet(Request.Method))
        {
            await Response.Body.WriteAsync(resource.MetadataDocument, Context.RequestAborted);
        }

        return true;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!RequiresResource(Context.GetEndpoint()))
        {
            return AuthenticateResult.NoResult();
        }

        var token = BearerToken(Request);
        if (token is not null && IssuerOf(token) is { } issuer && resource.Names(issuer))
        {
            return await AuthenticateResourceTokenAsync(token, issuer);
        }

        return await AuthenticateWithHostSchemesAsync(token is not null);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, resource.Challenge(BearerToken(Request) is not null));
        return Task.CompletedTask;
    }

    /// <summary>Whether the endpoint requires the policy of the protected resource.</summary>
    internal static bool RequiresResource(Endpoint? endpoint)
    {
        return endpoint?.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(data => string.Equals(data.Policy, NhAiMcpProtectedResourceDefaults.AuthorizationPolicy, StringComparison.Ordinal))
            == true;
    }

    private async Task<AuthenticateResult> AuthenticateResourceTokenAsync(string token, string issuer)
    {
        var validated = await validator.ValidateAsync(token, issuer, Context.RequestAborted);
        if (!validated.IsValid)
        {
            LogTokenRefused(Logger, issuer, validated.Exception?.GetType().Name ?? "invalid");
            return AuthenticateResult.Fail("The access token is not valid for this MCP resource.");
        }

        var principal = new ClaimsPrincipal(validated.ClaimsIdentity);
        if (resource.OnTokenValidated is { } onTokenValidated)
        {
            var context = new NhAiMcpProtectedResourceTokenValidatedContext(Context, issuer, principal);
            await onTokenValidated(context);
            if (context.FailureReason is { } reason)
            {
                LogTokenRefusedByHost(Logger, issuer, reason);
                return AuthenticateResult.Fail("The host refused the access token.");
            }
            if (context.Principal.Identity?.IsAuthenticated != true)
            {
                LogTokenRefusedByHost(Logger, issuer, "unauthenticated-principal");
                return AuthenticateResult.Fail("The host refused the access token.");
            }

            principal = context.Principal;
        }

        Context.Features.Set(new NhAiMcpProtectedResourceSignIn(issuer));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private async Task<AuthenticateResult> AuthenticateWithHostSchemesAsync(bool tokenSent)
    {
        Exception? failure = null;
        foreach (var scheme in resource.AcceptHostSchemes)
        {
            var result = await Context.AuthenticateAsync(scheme);
            if (result.Succeeded)
            {
                return AuthenticateResult.Success(new AuthenticationTicket(result.Principal, result.Properties, Scheme.Name));
            }

            failure ??= result.Failure;
        }

        if (failure is not null)
        {
            return AuthenticateResult.Fail(failure);
        }

        return tokenSent
            ? AuthenticateResult.Fail("The access token was not issued by an authorization server of this MCP resource.")
            : AuthenticateResult.NoResult();
    }

    private static string? BearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[BearerPrefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>The unvalidated issuer of a JWT, only to choose who validates it.</summary>
    private static string? IssuerOf(string token)
    {
        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(token))
        {
            return null;
        }

        try
        {
            return handler.ReadJsonWebToken(token).Issuer;
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "An access token of {Issuer} for the MCP resource was refused: {Reason}.")]
    private static partial void LogTokenRefused(ILogger logger, string issuer, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The host refused an access token of {Issuer} for the MCP resource: {Reason}.")]
    private static partial void LogTokenRefusedByHost(ILogger logger, string issuer, string reason);
}

/// <summary>
/// Validates access tokens for the resource with the keys of their authorization server: signature,
/// issuer, audience (the resource) and lifetime. The metadata and keys of each server are read as
/// JWT bearer authentication reads them and refreshed once when a token names an unknown key.
/// </summary>
internal sealed partial class NhAiMcpAccessTokenValidator
{
    private static readonly string[] AsymmetricAlgorithms =
    [
        SecurityAlgorithms.RsaSha256,
        SecurityAlgorithms.RsaSha384,
        SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256,
        SecurityAlgorithms.RsaSsaPssSha384,
        SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256,
        SecurityAlgorithms.EcdsaSha384,
        SecurityAlgorithms.EcdsaSha512
    ];

    private readonly NhAiMcpProtectedResource _resource;
    private readonly ILogger<NhAiMcpAccessTokenValidator> _logger;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _servers;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public NhAiMcpAccessTokenValidator(
        NhAiMcpProtectedResource resource,
        IHttpClientFactory httpClientFactory,
        ILogger<NhAiMcpAccessTokenValidator> logger)
    {
        _resource = resource;
        _logger = logger;
        _servers = new ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>(StringComparer.Ordinal);
        foreach (var issuer in resource.AuthorizationServers)
        {
            var documents = new HttpDocumentRetriever(httpClientFactory.CreateClient(NhAiMcpProtectedResourceDefaults.HttpClientName))
            {
                // An issuer on a loopback host may use http; the keys of an https issuer are read over https.
                RequireHttps = new Uri(issuer, UriKind.Absolute).Scheme == Uri.UriSchemeHttps
            };
            _servers[issuer] = new ConfigurationManager<OpenIdConnectConfiguration>(
                issuer,
                new NhAiAuthorizationServerMetadataRetriever(),
                documents);
        }
    }

    public async Task<TokenValidationResult> ValidateAsync(string token, string issuer, CancellationToken cancellationToken)
    {
        var server = _servers[issuer];
        var result = await ValidateWithAsync(server, token, issuer, cancellationToken);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // The server may have rotated its keys since they were read.
            server.RequestRefresh();
            result = await ValidateWithAsync(server, token, issuer, cancellationToken);
        }

        return result;
    }

    private async Task<TokenValidationResult> ValidateWithAsync(
        ConfigurationManager<OpenIdConnectConfiguration> server,
        string token,
        string issuer,
        CancellationToken cancellationToken)
    {
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await server.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException)
        {
            // As JWT bearer authentication does: the request is not signed in, and the operator sees why.
            LogMetadataUnavailable(_logger, issuer, exception);
            return new TokenValidationResult { IsValid = false, Exception = exception };
        }

        return await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidateIssuer = true,
            ValidateAudience = true,
            AudienceValidator = (audiences, _, _) => audiences.Any(_resource.IsResource),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = _resource.ClockSkew,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidAlgorithms = AsymmetricAlgorithms,
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role",
            AuthenticationType = NhAiMcpProtectedResourceDefaults.AuthenticationScheme
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The metadata or keys of the authorization server {Issuer} could not be read.")]
    private static partial void LogMetadataUnavailable(ILogger logger, string issuer, Exception exception);
}

/// <summary>
/// Reads an authorization server's metadata from the well-known addresses in the order the MCP
/// authorization specification gives (RFC 8414, then OpenID Connect discovery), checks that it names the
/// issuer exactly and reads the signing keys of its <c>jwks_uri</c>.
/// </summary>
internal sealed class NhAiAuthorizationServerMetadataRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address,
        IDocumentRetriever retriever,
        CancellationToken cancel)
    {
        Exception? failure = null;
        foreach (var candidate in Candidates(address))
        {
            string document;
            try
            {
                document = await retriever.GetDocumentAsync(candidate, cancel);
            }
            catch (IOException exception)
            {
                failure ??= exception;
                continue;
            }

            var configuration = OpenIdConnectConfiguration.Create(document);
            if (!string.Equals(configuration.Issuer, address, StringComparison.Ordinal))
            {
                // Mix-up protection: metadata of another issuer is never used.
                throw new InvalidOperationException($"The metadata at {candidate} does not name the issuer {address}.");
            }
            if (string.IsNullOrWhiteSpace(configuration.JwksUri))
            {
                throw new InvalidOperationException($"The metadata of {address} names no jwks_uri.");
            }

            configuration.JsonWebKeySet = new JsonWebKeySet(await retriever.GetDocumentAsync(configuration.JwksUri, cancel));
            foreach (var key in configuration.JsonWebKeySet.GetSigningKeys())
            {
                configuration.SigningKeys.Add(key);
            }

            return configuration;
        }

        throw new InvalidOperationException($"No metadata of the authorization server {address} could be read.", failure);
    }

    private static IEnumerable<string> Candidates(string issuer)
    {
        var uri = new Uri(issuer, UriKind.Absolute);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            yield return origin + "/.well-known/oauth-authorization-server";
            yield return origin + "/.well-known/openid-configuration";
            yield break;
        }

        yield return origin + "/.well-known/oauth-authorization-server" + path;
        yield return origin + "/.well-known/openid-configuration" + path;
        yield return origin + path + "/.well-known/openid-configuration";
    }
}

/// <summary>
/// Checks at startup that every scheme of <see cref="NhAiMcpProtectedResourceOptions.AcceptHostSchemes"/>
/// is an authentication scheme of the host.
/// </summary>
internal sealed class NhAiMcpProtectedResourceStartupCheck(
    IServiceProvider hostServices,
    IAuthenticationSchemeProvider schemes,
    NhAiMcpProtectedResource resource) : NhAiStartupCheck(hostServices)
{
    protected override async Task ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var name in resource.AcceptHostSchemes)
        {
            if (await schemes.GetSchemeAsync(name) is null)
            {
                throw new InvalidOperationException(
                    $"NhAiMcpProtectedResourceOptions.AcceptHostSchemes names '{name}', which is no authentication scheme of the host.");
            }
        }
    }
}
