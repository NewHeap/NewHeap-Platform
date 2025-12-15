using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.ComponentModel.DataAnnotations;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication;

public class NhMicrosoftOauthAuthenticationGetUrlHandler : BaseNhAuthenticationEndpoint
{
    public NhMicrosoftOauthAuthenticationGetUrlHandler(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationConfiguration configuration
    ) : base(httpContextAccessor, "authentication/oath/microsoft", configuration)
    {
        Method = HttpMethod.Post;
        Handler = ExecuteAsync;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Get Microsoft OAuth url")]
    [Produces<Results<Ok<string>,BadRequest>>]
    private async Task<IResult> ExecuteAsync([FromBody] MicrosoftAuthUrlMutateModel? request)
    {
        var result = new TaskResult<string>();
        if (string.IsNullOrWhiteSpace(request?.CallbackUrl))
        {
            result.AddError(nameof(request.CallbackUrl),"Field is required");
        }

        if (string.IsNullOrWhiteSpace(request?.UserName))
        {
            result.AddError(nameof(request.UserName),"Field is required");
        }
        
        var userManager = HttpContext!.RequestServices.GetRequiredService<INhUserManager>();
        
        if (!userManager.IsOauthAccount(request!.UserName!))
        {
            return TypedResults.Unauthorized();
        }

        if (!result.Success)
        {
            return BadRequest(result);
        }
        var stateId = Guid.NewGuid();
        var state = request!.CallbackUrl + ";" + stateId;

        var microsoftAuthService = HttpContext!.RequestServices.GetRequiredService<MicrosoftAuthService>();
        
        var url = microsoftAuthService.GetLoginUrl(state);

        return Ok(url);
    }
}


public class NhMicrosoftOauthAuthenticationAuthorizeHandler<TUser> : BaseNhAuthenticationEndpoint
where TUser : IdentityUser<Guid>
{
    public NhMicrosoftOauthAuthenticationAuthorizeHandler(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationConfiguration configuration
    ) : base(httpContextAccessor, "authentication/oath/microsoft/authorize", configuration)
    {
        Method = HttpMethod.Get;
        Handler = ExecuteAsync;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Get Microsoft OAuth authorize")]
    [Produces<Results<Ok<UserToken>,BadRequest>>]
    private async Task<IResult> ExecuteAsync([FromQuery] string code, [FromQuery] string state)
    {
        var request = new MicrosoftAuthorizationRequest { Code = code, State = state };
        
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.State))
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }
        
        var userManager = HttpContext!.RequestServices.GetRequiredService<INhUserManager<TUser>>();
        var microsoftAuthService = HttpContext!.RequestServices.GetRequiredService<MicrosoftAuthService>();
        
        var token = await microsoftAuthService.GetToken(request.Code!, request.State);
        if (token == null)
        {
            return BadRequest(TaskResult.Failed("Failed to validate token"));
        }
        
        var profile = await microsoftAuthService.GetProfile(token.AccessToken);
        var user = await userManager.FindByNameAsync(profile!.Mail!);
        if (user == null)
        {
            return BadRequest(TaskResult.Failed("Unknown user"));
        }
        
        if (!userManager.IsOauthAccount(user.UserName!))
        {
            return BadRequest(TaskResult.Failed("Invalid login method"));
        }

        if (await userManager.IsBlockedAsync(user))
        {
            return TypedResults.Unauthorized();
        }

        var authService = GetAuthService();
        var tokenResult = await authService.LoginWithoutValidations(user.Id, true);
        if (!tokenResult.Success)
        {
            return BadRequest(tokenResult);
        }
        
        WriteTokenToCookie(tokenResult.Data!);

        var redirect = state.Split(';')[0];

        return TypedResults.Redirect(redirect, preserveMethod:true);
    }
}

public class MicrosoftAuthorizationRequest
{
    public string Code { get; set; } = null!;
    public string State { get; set; } = null!;
}

public class MicrosoftAuthUrlMutateModel
{
    [Required]
    public string CallbackUrl { get; set; } = null!;
    [Required]
    public string UserName { get; set; } = null!;
}