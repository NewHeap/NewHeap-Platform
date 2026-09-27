using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class NhLogoutAuthenticationHandler : BaseNhAuthenticationEndpoint
{
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
    [EndpointDescription("Revokes the presented refresh token and expires the authentication cookies.")]
    [AllowAnonymous]
    [Produces<NoContentResult>]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    private async Task<IResult> Logout(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] LogoutRequest? request,
        [FromServices] IHttpContextAccessor httpContextAccessor,
        [FromServices] IRepository<NhUserAuthRefreshToken> refreshTokenRepository,
        CancellationToken cancellationToken)
    {
        var authenticationService = GetAuthService();
        var domain = new Uri(authenticationService.GetIssuer()).Host;
        var httpContext = httpContextAccessor.HttpContext!;

        try
        {
            var refreshToken = request?.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken)
                && !string.IsNullOrWhiteSpace(RefreshTokenCookieName))
            {
                httpContext.Request.Cookies.TryGetValue(RefreshTokenCookieName, out refreshToken);
            }

            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                await NhRefreshTokenOperations.RevokeAsync(
                    refreshTokenRepository.GetAll(),
                    refreshToken,
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
