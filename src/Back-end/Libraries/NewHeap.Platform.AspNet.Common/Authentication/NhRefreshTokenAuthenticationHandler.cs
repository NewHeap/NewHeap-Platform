using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class NhRefreshTokenAuthenticationHandler : BaseNhAuthenticationEndpoint<RefreshTokenRequest>
{
    private readonly INhAuthenticationService _authenticationService;
    internal string? TokenCookieName { get; set; } = "nh_access_token";
    internal string? RefreshTokenCookieName { get; set; } = "nh_access_token";


    public NhRefreshTokenAuthenticationHandler(
        INhAuthenticationService authenticationService,
        IHttpContextAccessor httpContextAccessor
        ) : base(httpContextAccessor, "authentication/refresh")
    {
        _authenticationService = authenticationService;
    }

    protected override async Task<IResult> Authenticate([FromBody] RefreshTokenRequest? request)
    {
        if (string.IsNullOrEmpty(request?.UserName) || string.IsNullOrEmpty(request.RefreshToken))
        {
            return TypedResults.BadRequest();
        }

        var result = await _authenticationService.RefreshToken(request);

        if (!result.Success)
        {
            return TypedResults.BadRequest(result);
        }

        var token = result.Data;
        var domain = new Uri(token.Issuer).Host;

        if (!string.IsNullOrWhiteSpace(TokenCookieName))
        {
            HttpContext!.Response.Cookies.Append(TokenCookieName!, token.Token, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = token.ValidTo,
                Domain = domain,
                IsEssential = true,
            });
        }

        if (!string.IsNullOrWhiteSpace(RefreshTokenCookieName))
        {
            HttpContext!.Response.Cookies.Append(RefreshTokenCookieName!, token.RefreshToken!, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.Now.AddDays(2),
                Domain = domain,
                IsEssential = true,
            });
        }

        return TypedResults.Ok(token);
    }
}