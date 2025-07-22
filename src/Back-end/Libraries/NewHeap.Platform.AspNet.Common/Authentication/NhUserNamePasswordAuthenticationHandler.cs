using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Endpoint for username and password authentication.
/// </summary>
public class NhUserNamePasswordAuthenticationHandler : BaseNhAuthenticationEndpoint
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AuthenticationConfiguration _configuration;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="configuration"></param>
    /// <param name="httpContextAccessor"></param>
    public NhUserNamePasswordAuthenticationHandler(
        IServiceProvider serviceProvider,
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
    :base(httpContextAccessor, "authentication/login", serviceProvider, configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        Handler = Authenticate;
        
        if(!string.IsNullOrWhiteSpace(configuration.AuthenticationEndpoint))
        {
            Pattern = configuration.AuthenticationEndpoint;
        }
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Login")]
    [Produces<Results<Ok<UserToken>,BadRequest>>]
    private async Task<IResult> Authenticate([FromBody] AuthenticateRequest? request)
    {
        var authenticationService = GetAuthService();
        
        var modelValid = ValidateModel(request);
        if (!modelValid.Success)
        {
            return BadRequest(modelValid);
        }

        var result = await authenticationService.Authenticate(request!, _configuration.AuthenticateRequiredClaims);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        var token = result.Data!;
        
        WriteTokenToCookie(token);
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