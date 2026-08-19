using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly AuthenticationConfiguration _configuration;
    internal string? TokenCookieName { get; set; } = "nh_access_token";
    internal string? RefreshTokenCookieName { get; set; } = "nh_access_token";


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="httpContextAccessor"></param>
    public NhRevertImpersonateAuthenticationHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor
        ) : base(httpContextAccessor, "authentication/ImpersonateRevert", configuration)
    {
        _configuration = configuration;

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
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
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

        var originalUserIdString = HttpContext?.User.Claims.FirstOrDefault(x => x.Type == NhPlatformClaimTypes.ImpersonateOriginUserId)?.Value;

        if(string.IsNullOrEmpty(originalUserIdString))
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var originalUserId = Guid.Parse(originalUserIdString);
        var userManager = HttpContext!.RequestServices.GetRequiredService<INhUserManager>();

        var originalUserClaims = await userManager.GetValidClaimsByUserIdAsync(originalUserId);

        if (!originalUserClaims.Any(x => x.Type == NhPlatformClaimTypes.Permission && x.Value == Platform.Common.Constants.PermissionClaimValues.AuthImpersonateAllowed))
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var result = await authenticationService.ImpersonateRevert(impersonatedUserId.Value, originalUserId);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        var token = result.Data;
        WriteTokenToCookie(token);

        return TypedResults.Ok(token);
    }
}