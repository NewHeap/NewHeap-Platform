using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Endpoint for refreshing the access token
/// </summary>
public class NhRefreshTokenAuthenticationHandler : BaseNhAuthenticationEndpoint
{
    private readonly AuthenticationConfiguration _configuration;
    internal string? TokenCookieName { get; set; } = "nh_access_token";
    internal string? RefreshTokenCookieName { get; set; } = "nh_access_token";


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="httpContextAccessor"></param>
    public NhRefreshTokenAuthenticationHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor
        ) : base(httpContextAccessor, "authentication/refresh", configuration)
    {
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
    private async Task<IResult> Authenticate([FromBody] RefreshTokenRequest? request)
    {
        var authenticationService = GetAuthService();
        
        if(_configuration.RefreshTokenEnabled == false)
        {
            return TypedResults.NotFound();
        }
        
        if (string.IsNullOrEmpty(request?.UserName) || string.IsNullOrEmpty(request.RefreshToken))
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }

        var result = await authenticationService.AuthenticateRefreshTokenAsync(request);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        var token = result.Data;
        var domain = new Uri(token.Issuer).Host;

        if (token != null)
        { 
            WriteTokenToCookie(token);
        }

        return TypedResults.Ok(token);
    }
}