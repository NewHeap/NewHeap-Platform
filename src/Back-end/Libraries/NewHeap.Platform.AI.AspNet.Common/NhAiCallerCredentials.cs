using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// Supplies the calling user's credential for same-user delegated calls, such as an API
/// bridge that executes a tool through the application's own HTTP pipeline.
/// </summary>
public interface INhAiCallerCredentialAccessor
{
    /// <summary>
    /// Returns the caller's bearer token for same-user delegated calls, or null.
    /// Never stored in <see cref="NhAiInvocationContext"/>, never logged. A request whose token is marked
    /// as never passed on (<see cref="NhAiCallerCredentials.IsNotForwardable"/>) has none: a request signed
    /// in with an access token for the MCP exposure (<see cref="NhAiMcpExposureBuilder.UseProtectedResource(Action{NhAiMcpProtectedResourceOptions})"/>
    /// and its named resources), and a request whose token the application marked with
    /// <see cref="NhAiCallerCredentials.MarkNotForwardable"/>. An accessor of the application's own
    /// returns none for such a request as well.
    /// </summary>
    ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The mark that keeps a request's token from being passed on. A token that was issued for one resource,
/// such as an MCP endpoint, is a credential for that resource only: handing it to the application's own
/// API through the API bridge, or to an MCP server with <c>forward-user-token</c>, would make it a
/// credential for something it was not issued for.
/// </summary>
public static class NhAiCallerCredentials
{
    /// <summary>
    /// Marks the token of the current request as never passed on: <see cref="INhAiCallerCredentialAccessor"/>
    /// then returns none for the request, so an API bridge call fails with
    /// <c>api-bridge-unauthenticated</c> and a <c>forward-user-token</c> MCP server gets no token. Call it
    /// from the <c>OnTokenValidated</c> event of an authentication scheme of the application's own that
    /// validates tokens issued for another resource, for example an MCP entry the application validates
    /// itself. A token of a protected resource of the MCP exposure is marked without this call. The mark
    /// lasts for the request and cannot be removed.
    /// </summary>
    /// <param name="httpContext">The request whose token was validated.</param>
    /// <param name="issuer">The issuer of the token, exactly as it names itself.</param>
    public static void MarkNotForwardable(HttpContext httpContext, string issuer)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        if (httpContext.Features.Get<NhAiNotForwardableToken>() is not null)
        {
            // The first mark stays: a later one never replaces it, so nothing can undo it.
            return;
        }

        httpContext.Features.Set(new NhAiNotForwardableToken(issuer));
    }

    /// <summary>
    /// Whether the token of the request is marked as never passed on, by a protected resource of the MCP
    /// exposure or by <see cref="MarkNotForwardable"/>.
    /// </summary>
    public static bool IsNotForwardable(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.Features.Get<NhAiNotForwardableToken>() is not null;
    }
}

/// <summary>
/// The mark of a request whose token is for one resource only, so nothing passes it on.
/// </summary>
internal sealed class NhAiNotForwardableToken(string issuer)
{
    /// <summary>The issuer of the token, for diagnostics.</summary>
    public string Issuer { get; } = issuer;
}

/// <summary>
/// Default accessor: the saved <c>access_token</c> of the current authentication result,
/// falling back to the <c>Authorization: Bearer</c> header of the current request; none for a
/// request whose token is marked as never passed on.
/// </summary>
internal sealed class NhAiHttpContextCallerCredentialAccessor(
    IHttpContextAccessor httpContextAccessor) : INhAiCallerCredentialAccessor
{
    private const string BearerPrefix = "Bearer ";

    public async ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return null;
        }
        if (NhAiCallerCredentials.IsNotForwardable(httpContext))
        {
            // The token's audience is another resource, such as the MCP resource (RFC 8707): passing it
            // on to the application's own API or another server would make it a credential for something
            // it was not issued for.
            return null;
        }

        var schemes = httpContext.RequestServices?.GetService<IAuthenticationSchemeProvider>();
        if (httpContext.RequestServices?.GetService<IAuthenticationService>() is not null
            && schemes is not null
            && await schemes.GetDefaultAuthenticateSchemeAsync() is not null)
        {
            var savedToken = await httpContext.GetTokenAsync("access_token");
            if (!string.IsNullOrWhiteSpace(savedToken))
            {
                return savedToken;
            }
        }

        var authorization = httpContext.Request.Headers[HeaderNames.Authorization].ToString();
        if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var headerToken = authorization[BearerPrefix.Length..].Trim();
            if (headerToken.Length > 0)
            {
                return headerToken;
            }
        }

        return null;
    }
}
