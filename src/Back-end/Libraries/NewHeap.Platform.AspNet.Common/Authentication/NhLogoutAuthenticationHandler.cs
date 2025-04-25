using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.Services;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class NhLogoutAuthenticationHandler : IAuthenticationEndpoint
{
    private readonly AuthenticationConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    public string Pattern { get; internal set; } = "authentication/logout";
    public HttpMethod Method { get; } = HttpMethod.Post;
    public Delegate Handler => Logout;
    
    internal string? TokenCookieName { get; set; } = "nh_access_token";
    internal string? RefreshTokenCookieName { get; set; } = "nh_access_token";

    public NhLogoutAuthenticationHandler(AuthenticationConfiguration configuration, IServiceProvider serviceProvider)
    {
        _configuration = configuration;
        _serviceProvider = serviceProvider;
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
    }
    
    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Logout")]
    [Produces<NoContentResult>]
    private async Task<IResult> Logout([FromServices] IHttpContextAccessor httpContextAccessor, [FromServices] INhAuthenticationService authenticationService)
    {
        if (!string.IsNullOrEmpty(_configuration.AuthenticationServiceKey))
        {
            authenticationService = _serviceProvider.GetRequiredKeyedService<INhAuthenticationService>(_configuration.AuthenticationServiceKey);
        }
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