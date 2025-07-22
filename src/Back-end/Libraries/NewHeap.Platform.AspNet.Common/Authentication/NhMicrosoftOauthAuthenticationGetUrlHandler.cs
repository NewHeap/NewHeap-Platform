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
    private readonly IServiceProvider _serviceProvider;

    public NhMicrosoftOauthAuthenticationGetUrlHandler(
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider serviceProvider,
        AuthenticationConfiguration configuration
    ) : base(httpContextAccessor, "authentication/oath/microsoft", serviceProvider, configuration)
    {
        _serviceProvider = serviceProvider;

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
        
        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<INhUserManager>();
        
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

        var microsoftAuthService = scope.ServiceProvider.GetRequiredService<MicrosoftAuthService>();
        
        var url = microsoftAuthService.GetLoginUrl(state);

        return Ok(url);
    }
}


public class NhMicrosoftOauthAuthenticationAuthorizeHandler<TUser> : BaseNhAuthenticationEndpoint
where TUser : IdentityUser<Guid>
{
    private readonly IServiceProvider _serviceProvider;

    public NhMicrosoftOauthAuthenticationAuthorizeHandler(
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider serviceProvider,
        AuthenticationConfiguration configuration
    ) : base(httpContextAccessor, "authentication/oath/microsoft/authorize", serviceProvider, configuration)
    {
        _serviceProvider = serviceProvider;
        Method = HttpMethod.Post;
        Handler = ExecuteAsync;
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Get Microsoft OAuth authorize")]
    [Produces<Results<Ok<UserToken>,BadRequest>>]
    private async Task<IResult> ExecuteAsync([FromBody] MicrosoftAuthUrlMutateModel? request)
    {
        var code = HttpContext!.Request.Query["code"];
        var state = HttpContext.Request.Query["state"];
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return BadRequest(TaskResult.Failed("Invalid request"));
        }
        
        
        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<INhUserManager<TUser>>();
        
        if (!userManager.IsOauthAccount(request.UserName!))
        {
            return TypedResults.Unauthorized();
        }
        var microsoftAuthService = scope.ServiceProvider.GetRequiredService<MicrosoftAuthService>();
        

        var token = await microsoftAuthService.GetToken(code!, state);
        if (token == null)
        {
            return TypedResults.Unauthorized();
        }
        var profile = await microsoftAuthService.GetProfile(token.AccessToken);
        var user = await userManager.FindByEmailAsync(profile!.Mail!);
        if (user == null)
        {
            return TypedResults.Unauthorized();
        }
        
        

        if (await userManager.IsBlockedAsync(user))
        {
            return TypedResults.Unauthorized();
        }

        var authService = GetAuthService();
        var tokenResult = await authService.LoginWithoutValidations(user.Id);
        if (!tokenResult.Success)
        {
            return BadRequest(tokenResult);
        }
        
        return Ok(tokenResult.Data!);
    }
}


public class MicrosoftAuthUrlMutateModel
{
    [Required]
    public string CallbackUrl { get; set; }
    [Required]
    public string UserName { get; set; }
}