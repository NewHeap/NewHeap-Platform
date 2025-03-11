using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.Services;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class NhLogoutAuthenticationHandler : IAuthenticationEndpoint
{
    public string Pattern { get; internal set; } = "authentication/logout";
    public HttpMethod Method { get; } = HttpMethod.Post;
    public Delegate Handler => Logout;
    
    internal string? TokenCookieName { get; set; } = "nh_access_token";
    internal string? RefreshTokenCookieName { get; set; } = "nh_access_token";

    private async Task<IResult> Logout([FromServices] IHttpContextAccessor httpContextAccessor, [FromServices] INhAuthenticationService authenticationService)
    {
        var domain = new Uri(authenticationService.GetIssuer()).Host;
        var httpContext = httpContextAccessor.HttpContext!;
        if (!string.IsNullOrWhiteSpace(TokenCookieName))
        {
            httpContext!.Response.Cookies.Append(TokenCookieName!, "", new CookieOptions
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
            httpContext!.Response.Cookies.Append(RefreshTokenCookieName!, "", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.Now.AddDays(-1),
                Domain = domain,
                IsEssential = true,
            });
        }
        return TypedResults.NoContent();
    }
}