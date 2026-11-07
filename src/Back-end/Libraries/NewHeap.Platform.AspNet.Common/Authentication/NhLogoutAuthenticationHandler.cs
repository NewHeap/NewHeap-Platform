using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class NhLogoutAuthenticationHandler : BaseNhAuthenticationEndpoint
{
    /// <summary>
    /// Response header of a logout with <see cref="LogoutRequest.KeepAuthenticationCookies"/>: its value
    /// <see cref="AuthenticationCookiesKept"/> confirms that the browser's cookies were left alone. An
    /// older server, which does not know the option, expires them and sends no such header.
    /// </summary>
    public const string AuthenticationCookiesHeaderName = "X-NH-Authentication-Cookies";

    /// <summary>The value of <see cref="AuthenticationCookiesHeaderName"/> when the cookies were kept.</summary>
    public const string AuthenticationCookiesKept = "kept";

    internal string? TokenCookieName { get; set; } = "nh_access_token";
    internal string? RefreshTokenCookieName { get; set; } = "nh_refresh_token";

    public NhLogoutAuthenticationHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor
    ) : base(httpContextAccessor, "authentication/logout", configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.LogoutEndpoint))
        {
            Pattern = configuration.LogoutEndpoint;
        }
        if(!string.IsNullOrWhiteSpace(configuration.CookieName))
        {
            TokenCookieName = configuration.CookieName;
        }
        if(!string.IsNullOrWhiteSpace(configuration.RefreshCookieName))
        {
            RefreshTokenCookieName = configuration.RefreshCookieName;
        }
        
        Method = HttpMethod.Post;
        Handler = Logout;
    }
    
    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Logout")]
    [EndpointSummary("Log out the current session")]
    [EndpointDescription("Revokes the presented refresh token and expires the authentication cookies. With keepAuthenticationCookies it revokes only the refresh token in the body and leaves the cookies of the browser's active account alone, which the X-NH-Authentication-Cookies response header confirms.")]
    [AllowAnonymous]
    [Produces<NoContentResult>]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    private async Task<IResult> Logout(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] LogoutRequest? request,
        [FromServices] IHttpContextAccessor httpContextAccessor,
        [FromServices] IRepository<NhUserAuthRefreshToken> refreshTokenRepository,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var authenticationService = GetAuthService();
        var domain = new Uri(authenticationService.GetIssuer()).Host;
        var httpContext = httpContextAccessor.HttpContext!;

        if (request?.KeepAuthenticationCookies == true)
        {
            // Signs out one account of several in a browser: only the presented token, never the
            // cookie's, and the cookies stay with the account that is still active.
            if (Configuration.RefreshTokenEnabled && !string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                await NhRefreshTokenOperations.RevokeAsync(
                    refreshTokenRepository.GetAll(),
                    NhRefreshTokenKey.Digest(configuration, request.RefreshToken),
                    cancellationToken);
            }

            httpContext.Response.Headers[AuthenticationCookiesHeaderName] = AuthenticationCookiesKept;
            return TypedResults.NoContent();
        }

        try
        {
            var refreshToken = request?.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken)
                && !string.IsNullOrWhiteSpace(RefreshTokenCookieName))
            {
                httpContext.Request.Cookies.TryGetValue(RefreshTokenCookieName, out refreshToken);
            }

            // Without refresh tokens no client holds one to present.
            if (Configuration.RefreshTokenEnabled && !string.IsNullOrWhiteSpace(refreshToken))
            {
                await NhRefreshTokenOperations.RevokeAsync(
                    refreshTokenRepository.GetAll(),
                    NhRefreshTokenKey.Digest(configuration, refreshToken),
                    cancellationToken);
            }

            return TypedResults.NoContent();
        }
        finally
        {
            ExpireAuthenticationCookies(httpContext, domain);
        }
    }

    private void ExpireAuthenticationCookies(HttpContext httpContext, string domain)
    {
        if (!string.IsNullOrWhiteSpace(TokenCookieName))
        {
            httpContext.Response.Cookies.Append(TokenCookieName, "", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.Now.AddDays(-1),
                Domain = domain,
                IsEssential = true,
            });
        }

        if (!string.IsNullOrWhiteSpace(RefreshTokenCookieName))
        {
            httpContext.Response.Cookies.Append(RefreshTokenCookieName, "", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.Now.AddDays(-1),
                Domain = domain,
                IsEssential = true,
            });
        }
    }
}
