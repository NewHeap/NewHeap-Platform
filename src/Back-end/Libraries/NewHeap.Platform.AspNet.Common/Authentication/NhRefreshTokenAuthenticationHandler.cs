using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Endpoint for refreshing the access token
/// </summary>
public class NhRefreshTokenAuthenticationHandler : BaseNhAuthenticationEndpoint
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AuthenticationConfiguration _configuration;
    internal string? TokenCookieName { get; set; } = "nh_access_token";
    internal string? RefreshTokenCookieName { get; set; } = "nh_access_token";


    /// <summary>
    /// 
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="configuration"></param>
    /// <param name="httpContextAccessor"></param>
    public NhRefreshTokenAuthenticationHandler(
        IServiceProvider serviceProvider,
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor
        ) : base(httpContextAccessor, "authentication/refresh")
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        
        if(!string.IsNullOrWhiteSpace(configuration.RefreshTokenEndpoint))
        {
            Pattern = configuration.RefreshTokenEndpoint;
        }
        
        if(!string.IsNullOrWhiteSpace(configuration.RefreshCookieName))
        {
            TokenCookieName = configuration.CookieName;
        }
        if(!string.IsNullOrWhiteSpace(configuration.RefreshCookieName))
        {
            RefreshTokenCookieName = configuration.RefreshCookieName;
        }
        
        Handler = Authenticate;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Refresh token")]
    [Produces<Results<Ok<UserToken>,BadRequest>>]
    private async Task<IResult> Authenticate([FromBody] RefreshTokenRequest? request, [FromServices] INhAuthenticationService authenticationService)
    {
        if (!string.IsNullOrEmpty(_configuration.AuthenticationServiceKey))
        {
            authenticationService = _serviceProvider.GetRequiredKeyedService<INhAuthenticationService>(_configuration.AuthenticationServiceKey);
        }
        
        if(_configuration.RefreshTokenEnabled == false)
        {
            return TypedResults.NotFound();
        }
        
        if (string.IsNullOrEmpty(request?.UserName) || string.IsNullOrEmpty(request.RefreshToken))
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var result = await authenticationService.RefreshToken(request);

        if (!result.Success)
        {
            return BadRequest(result);
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