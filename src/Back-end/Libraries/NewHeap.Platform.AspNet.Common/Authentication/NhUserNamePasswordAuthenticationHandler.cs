using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Endpoint for username and password authentication.
/// </summary>
public class NhUserNamePasswordAuthenticationHandler : BaseNhAuthenticationEndpoint
{
    private readonly AuthenticationConfiguration _configuration;

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
    /// <param name="configuration"></param>
    /// <param name="httpContextAccessor"></param>
    public NhUserNamePasswordAuthenticationHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
    :base(httpContextAccessor, "authentication/login")
    {
        _configuration = configuration;
        Handler = Authenticate;
        
        if(!string.IsNullOrWhiteSpace(configuration.AuthenticationEndpoint))
        {
            Pattern = configuration.AuthenticationEndpoint;
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
    [EndpointName("Login")]
    [Produces<Results<Ok<UserToken>,BadRequest>>]
    private async Task<IResult> Authenticate([FromBody] AuthenticateRequest? request,[FromServices] INhAuthenticationService authenticationService)
    {
        var modelValid = ValidateModel(request);
        if (!modelValid.Success)
        {
            return BadRequest(modelValid);
        }

        var result = await authenticationService.Authenticate(request!);
        if (!result.Success)
        {
            return BadRequest(result);
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

    private TaskResult ValidateModel(AuthenticateRequest? request)
    {
        var result = new TaskResult();
        if (string.IsNullOrEmpty(request?.UserName))
        {
            result.AddError(nameof(request.UserName),"Field is required");
        }
        
        if (string.IsNullOrEmpty(request?.Password))
        {
            result.AddError(nameof(request.Password),"Field is required");
        }
        return result;
    }
}