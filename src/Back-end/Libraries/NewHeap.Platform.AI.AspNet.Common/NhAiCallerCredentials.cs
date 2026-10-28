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
    /// Never stored in <see cref="NhAiInvocationContext"/>, never logged. A request signed in with an
    /// access token for the MCP exposure (<see cref="NhAiMcpExposureBuilder.UseProtectedResource"/>) has
    /// none: that token is for the MCP resource only and is never passed on.
    /// </summary>
    ValueTask<string?> GetBearerTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default accessor: the saved <c>access_token</c> of the current authentication result,
/// falling back to the <c>Authorization: Bearer</c> header of the current request; none for a
/// request signed in with an access token for the MCP exposure.
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
        if (NhAiMcpProtectedResourceSignIn.Applies(httpContext))
        {
            // The token's audience is the MCP resource (RFC 8707): passing it on to the application's own
            // API or another server would make it a credential for something it was not issued for.
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
