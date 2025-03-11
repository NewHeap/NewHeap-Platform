using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Endpoint for username and password authentication.
/// </summary>
public class NhUserNamePasswordAuthenticationHandler : BaseNhAuthenticationEndpoint<AuthenticateRequest>
{
    /// <summary>
    /// Name of the cookie that contains the access token
    /// When empty the cookie is not set
    /// </summary>
    internal string? TokenCookieName { get; set; } = "nh_access_token";
    
    /// <summary>
    /// Name of the cookie that contains the refresh token
    /// When empty the cookie is not set
    /// </summary>
    internal string? RefreshTokenCookieName { get; set; } = "nh_access_token";
    
    /// <summary>
    /// Enables the refresh token cookie
    /// </summary>
    public bool EnableRefreshToken { get; set; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="httpContextAccessor"></param>
    public NhUserNamePasswordAuthenticationHandler(
        IHttpContextAccessor httpContextAccessor)
    :base(httpContextAccessor, "authentication/login")
    {
        Handler = Authenticate;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [EndpointName("Login")]
    [Produces<Results<Ok<UserToken>,BadRequest>>]
    private async Task<IResult> Authenticate([FromBody] AuthenticateRequest? request,[FromServices] INhAuthenticationService authenticationService)
    {
        if (string.IsNullOrEmpty(request?.UserName) || string.IsNullOrEmpty(request?.Password))
        {
            return TypedResults.BadRequest("Username or password is missing");
        }

        var result = await authenticationService.Authenticate(request);
        if (!result.Success)
        {
            return TypedResults.BadRequest(result);
        }

        var token = result.Data;
        var domain = new Uri(token.Issuer).Host;

        HttpContext!.Response.Cookies.Append(TokenCookieName!, token.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = token.ValidTo,
            Domain = domain,
            IsEssential = true,
        });

        if (EnableRefreshToken)
        {
            HttpContext.Response.Cookies.Append(RefreshTokenCookieName!, token.RefreshToken!, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.Now.AddDays(2),
                Domain = domain,
                IsEssential = true,
            });
        }
        else
        {
            token.RefreshToken = null;
        }

        return TypedResults.Ok(token);
    }
}