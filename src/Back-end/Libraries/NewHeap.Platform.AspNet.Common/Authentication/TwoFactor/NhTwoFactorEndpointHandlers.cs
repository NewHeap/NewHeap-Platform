using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using System.Security.Claims;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Completes a second-factor challenge and issues the session.
/// </summary>
public class NhTwoFactorVerifyAuthenticationHandler : BaseNhAuthenticationEndpoint, IConfigurableAuthenticationEndpoint
{
    public NhTwoFactorVerifyAuthenticationHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/verify", configuration)
    {
        Handler = VerifyAsync;
    }

    public virtual void Configure(RouteHandlerBuilder builder)
    {
        builder
            .AllowAnonymous()
            .WithSummary("Complete a two-factor challenge")
            .WithDescription(
                "Verifies an authenticator code or recovery code for the challenge returned by the login endpoint " +
                "and issues the access and refresh tokens. Wrong codes count towards the account lockout.")
            .Produces<NhLoginResponse>(StatusCodes.Status200OK)
            .Produces<Dictionary<string, string[]>>(StatusCodes.Status400BadRequest);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Verify two-factor challenge")]
    private async Task<IResult> VerifyAsync([FromBody] NhTwoFactorVerifyRequest? request)
    {
        var validation = ValidateRequest(request);
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        if (GetAuthService() is not INhMultiFactorAuthenticationService { IsTwoFactorAvailable: true } multiFactorService)
        {
            return BadRequest(NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ConfigurationInvalid));
        }

        var result = await multiFactorService.VerifyTwoFactorAsync(request!, Configuration.AuthenticateRequiredClaims);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        var session = result.Data!.Session!;
        WriteTokenToCookie(session);

        return TypedResults.Ok(NhLoginResponse.FromSession(session));
    }

    private static TaskResult ValidateRequest(NhTwoFactorVerifyRequest? request)
    {
        var result = new TaskResult();
        if (string.IsNullOrWhiteSpace(request?.ChallengeToken))
        {
            result.AddError(nameof(NhTwoFactorVerifyRequest.ChallengeToken), "Field is required");
        }

        if (string.IsNullOrWhiteSpace(request?.Method))
        {
            result.AddError(nameof(NhTwoFactorVerifyRequest.Method), "Field is required");
        }

        if (string.IsNullOrWhiteSpace(request?.Code))
        {
            result.AddError(nameof(NhTwoFactorVerifyRequest.Code), "Field is required");
        }

        return result;
    }
}

/// <summary>
/// Base class for the signed-in user's two-factor endpoints.
/// </summary>
public abstract class NhTwoFactorAccountEndpointHandler<TUser> : BaseNhAuthenticationEndpoint, IConfigurableAuthenticationEndpoint
    where TUser : IdentityUser<Guid>
{
    protected NhTwoFactorAccountEndpointHandler(
        IHttpContextAccessor httpContextAccessor,
        string pattern,
        HttpMethod method,
        AuthenticationConfiguration configuration)
        : base(httpContextAccessor, pattern, configuration)
    {
        Method = method;
    }

    public virtual void Configure(RouteHandlerBuilder builder)
    {
        builder
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme })
            .Produces<Dictionary<string, string[]>>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);
    }

    protected INhTwoFactorService<TUser> TwoFactorService =>
        HttpContext!.RequestServices.GetRequiredService<INhTwoFactorService<TUser>>();

    protected Guid? CurrentUserId
    {
        get
        {
            var value = HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(value, out var userId))
            {
                return userId;
            }

            return null;
        }
    }

    /// <summary>
    /// Loads the signed-in user. Changes are refused while an administrator impersonates the
    /// user, because the administrator must not control the user's second factor.
    /// </summary>
    protected async Task<CurrentUserResult> GetCurrentUserAsync(bool forChange)
    {
        if (forChange && HttpContext!.User.HasClaim(x => x.Type == NhPlatformClaimTypes.ImpersonateOriginUserId))
        {
            return new CurrentUserResult(
                null,
                BadRequest(NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.NotAllowedWhileImpersonating)));
        }

        var userId = CurrentUserId;
        if (!userId.HasValue)
        {
            return new CurrentUserResult(null, TypedResults.Unauthorized());
        }

        var userManager = HttpContext!.RequestServices.GetRequiredService<UserManager<TUser>>();
        var user = await userManager.FindByIdAsync(userId.Value.ToString());
        if (user == null)
        {
            return new CurrentUserResult(null, TypedResults.Unauthorized());
        }

        return new CurrentUserResult(user, null);
    }

    /// <summary>
    /// Issues a new session for this device after a change that ended every session.
    /// </summary>
    protected async Task<UserToken?> RenewSessionAsync(NhTwoFactorChangeResult change)
    {
        if (change.RenewalProof == null)
        {
            return null;
        }

        if (GetAuthService() is not INhMultiFactorAuthenticationService multiFactorService)
        {
            return null;
        }

        var session = await multiFactorService.RenewSessionAsync(change.RenewalProof);
        if (!session.Success)
        {
            return null;
        }

        WriteTokenToCookie(session.Data!);
        return session.Data;
    }

    protected async Task<IResult> ChangeResponseAsync(TaskResult<NhTwoFactorChangeResult> result)
    {
        if (!result.Success)
        {
            return BadRequest(result);
        }

        var change = result.Data!;
        var session = await RenewSessionAsync(change);

        return TypedResults.Ok(new NhTwoFactorChangeResponse
        {
            RecoveryCodes = change.RecoveryCodes,
            Session = session,
        });
    }

    protected sealed record CurrentUserResult(TUser? User, IResult? Error);
}

/// <summary>
/// Returns the signed-in user's two-factor status.
/// </summary>
public class NhTwoFactorStatusEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorStatusEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/two-factor", HttpMethod.Get, configuration)
    {
        Handler = GetStatusAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Get two-factor status")
            .WithDescription("Returns the enrolled second-factor methods, whether the policy requires them and how many recovery codes are left.")
            .Produces<NhTwoFactorStatusViewModel>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Get two-factor status")]
    private async Task<IResult> GetStatusAsync()
    {
        var current = await GetCurrentUserAsync(forChange: false);
        if (current.Error != null)
        {
            return current.Error;
        }

        var status = await TwoFactorService.GetStatusAsync(current.User!, HttpContext!.RequestAborted);
        return TypedResults.Ok(status);
    }
}

/// <summary>
/// Starts authenticator-app enrollment.
/// </summary>
public class NhTwoFactorAuthenticatorSetupEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorAuthenticatorSetupEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/two-factor/authenticator", HttpMethod.Post, configuration)
    {
        Handler = BeginSetupAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Start authenticator enrollment")
            .WithDescription(
                "Creates a pending authenticator key and returns it with an otpauth URI and QR code. The key becomes " +
                "active after confirmation. Restarting enrollment while two-factor authentication is enabled requires reauthentication.")
            .Produces<NhAuthenticatorSetupViewModel>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Start authenticator enrollment")]
    private async Task<IResult> BeginSetupAsync([FromBody] NhTwoFactorReauthenticationRequest? request)
    {
        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.BeginAuthenticatorSetupAsync(
            current.User!,
            request?.Reauthentication,
            HttpContext!.RequestAborted);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.Ok(result.Data!);
    }
}

/// <summary>
/// Confirms authenticator enrollment and enables two-factor authentication.
/// </summary>
public class NhTwoFactorAuthenticatorConfirmEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorAuthenticatorConfirmEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/two-factor/authenticator/confirm", HttpMethod.Post, configuration)
    {
        Handler = ConfirmAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Confirm authenticator enrollment")
            .WithDescription(
                "Verifies a code from the authenticator app, enables two-factor authentication and returns the recovery " +
                "codes once. Every other session ends; the response contains a new session for this device.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Confirm authenticator enrollment")]
    private async Task<IResult> ConfirmAsync([FromBody] NhAuthenticatorConfirmRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.Code))
        {
            return BadRequest(TaskResult.Failed(nameof(NhAuthenticatorConfirmRequest.Code), "Field is required"));
        }

        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.ConfirmAuthenticatorAsync(
            current.User!,
            request.Code,
            current.User!.Id,
            HttpContext!.RequestAborted);

        return await ChangeResponseAsync(result);
    }
}

/// <summary>
/// Replaces the signed-in user's recovery codes.
/// </summary>
public class NhTwoFactorRecoveryCodesEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorRecoveryCodesEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/two-factor/recovery-codes", HttpMethod.Post, configuration)
    {
        Handler = RegenerateAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Regenerate recovery codes")
            .WithDescription("Requires reauthentication. Replaces every recovery code and returns the new codes once.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Regenerate recovery codes")]
    private async Task<IResult> RegenerateAsync([FromBody] NhTwoFactorReauthenticationRequest? request)
    {
        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.RegenerateRecoveryCodesAsync(
            current.User!,
            request?.Reauthentication,
            current.User!.Id,
            HttpContext!.RequestAborted);

        return await ChangeResponseAsync(result);
    }
}

/// <summary>
/// Disables two-factor authentication for the signed-in user.
/// </summary>
public class NhTwoFactorDisableEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorDisableEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/two-factor/disable", HttpMethod.Post, configuration)
    {
        Handler = DisableAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Disable two-factor authentication")
            .WithDescription(
                "Requires reauthentication and is refused when the two-factor policy requires a second factor. " +
                "Every other session ends; the response contains a new session for this device.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Disable two-factor authentication")]
    private async Task<IResult> DisableAsync([FromBody] NhTwoFactorReauthenticationRequest? request)
    {
        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.DisableAsync(
            current.User!,
            request?.Reauthentication,
            current.User!.Id,
            HttpContext!.RequestAborted);

        return await ChangeResponseAsync(result);
    }
}
