using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using static NewHeap.Platform.AspNet.Common.Constants;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Endpoint for refreshing the access token
/// </summary>
public class NhRevertImpersonateAuthenticationHandler : BaseNhAuthenticationEndpoint
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AuthenticationConfiguration _configuration;
    internal string? TokenCookieName { get; set; } = "nh_access_token";
    internal string? RefreshTokenCookieName { get; set; } = "nh_access_token";

    protected readonly INhUserManager _userManager;


    /// <summary>
    /// 
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="configuration"></param>
    /// <param name="httpContextAccessor"></param>
    public NhRevertImpersonateAuthenticationHandler(
        IServiceProvider serviceProvider,
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        INhUserManager userManager
        ) : base(httpContextAccessor, "authentication/ImpersonateRevert", serviceProvider, configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _userManager = userManager;

        if (!string.IsNullOrWhiteSpace(configuration.RefreshTokenEndpoint))
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
        
        Handler = ImpersonateRevert;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Impersonate user revert")]
    [Produces<Results<Ok<UserToken>, BadRequest>>]
    private async Task<IResult> ImpersonateRevert([FromBody] ImpersonateRevertRequest? request)
    {
        var authenticationService = GetAuthService();
        
        if(_configuration.ImpersonateEnabled == false)
        {
            return TypedResults.NotFound();
        }

        var impersonatedUserId = HttpContext?.GetUserId();
        if (impersonatedUserId == null)
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var originalUserIdString = HttpContext?.User.Claims.FirstOrDefault(x => x.Type == NhPlatformClaimTypes.ImpersontateOriginUserId)?.Value;

        if(string.IsNullOrEmpty(originalUserIdString))
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var originalUserId = Guid.Parse(originalUserIdString);
        var originalUserClaims = await _userManager.GetValidClaimsByUserIdAsync(originalUserId);

        if (!originalUserClaims.Any(x => x.Type == NhPlatformClaimTypes.Permission && x.Value == NhPlatformPermissionValues.AuthImpersonateAllowed))
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var result = await authenticationService.ImpersonateRevert(impersonatedUserId.Value, originalUserId);

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