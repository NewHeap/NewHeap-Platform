using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class NhRefreshTokenAuthenticationHandler : IAuthenticationEndpoint
{
    private readonly INhAuthenticationService _authenticationService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    public string Pattern { get; set; } = "authentication/refresh";

    internal string? TokenCookieName { get; set; } = "nh_access_token";
    internal string? RefreshTokenCookieName { get; set; } = "nh_access_token";

    public HttpMethod Method => HttpMethod.Post;
    public Delegate Handler => Authenticate;
    public HttpContext? HttpContext => _httpContextAccessor.HttpContext;

    
    public NhRefreshTokenAuthenticationHandler(INhAuthenticationService authenticationService,
        IHttpContextAccessor httpContextAccessor)
    {
        _authenticationService = authenticationService;
        _httpContextAccessor = httpContextAccessor;
    }

    private async Task<IResult> Authenticate([FromBody] RefreshTokenRequest? request)
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

        HttpContext!.Response.Cookies.Append(TokenCookieName!, token.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = token.ValidTo,
            Domain = domain,
            IsEssential = true,
        });
        
        HttpContext.Response.Cookies.Append(RefreshTokenCookieName!, token.RefreshToken!, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.Now.AddDays(2),
            Domain = domain,
            IsEssential = true,
        });

        return TypedResults.Ok(token);
    }
}