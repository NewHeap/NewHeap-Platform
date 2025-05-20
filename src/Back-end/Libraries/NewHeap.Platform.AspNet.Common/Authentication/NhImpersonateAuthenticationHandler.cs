using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using static NewHeap.Platform.AspNet.Common.Constants;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Endpoint for refreshing the access token
/// </summary>
public class NhImpersonateAuthenticationHandler : BaseNhAuthenticationEndpoint
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
    public NhImpersonateAuthenticationHandler(
        IServiceProvider serviceProvider,
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor
        ) : base(httpContextAccessor, "authentication/impersonate", serviceProvider, configuration)
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
        
        Handler = Impersonate;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Impersonate user")]
    [Produces<Results<Ok<UserToken>, BadRequest>>]
    private async Task<IResult> Impersonate([FromBody] ImpersonateRequest? request)
    {
        var authenticationService = GetAuthService();
        
        if(_configuration.ImpersonateEnabled == false)
        {
            return TypedResults.NotFound();
        }
        
        if (!request?.UserId.HasValue == true)
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var currentUserId = HttpContext?.GetUserId();
        if (currentUserId == null)
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        if (HttpContext?.User.HasClaim(NhPlatformClaimTypes.Permission, NhPlatformPermissionValues.AuthImpersonateAllowed) != true)
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var result = await authenticationService.Impersonate(currentUserId.Value, request!);

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